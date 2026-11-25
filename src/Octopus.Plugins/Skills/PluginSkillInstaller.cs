using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Octopus.Plugins.Skills;

public enum SkillInstallScope
{
    UserGlobal,
    ProjectLocal,
}

/// <summary>One Agent client skill target directory supported by <see cref="PluginSkillInstaller"/>.</summary>
public sealed record SkillTargetSpec(
    string Id,
    string DisplayName,
    string DisplayNameZh,
    string ClientsNote,
    string ClientsNoteZh,
    string GlobalRelativePath,
    string ProjectRelativePath,
    bool DefaultSelected);

/// <summary>Inspection of a target directory on this machine before installing.</summary>
public sealed record SkillTargetStatus(
    SkillTargetSpec Spec,
    string ResolvedPath,
    bool AgentRootDetected,
    string? InstalledVersion);

/// <summary>Outcome of overwriting one target directory.</summary>
public sealed record SkillTargetWriteResult(
    string TargetId,
    string TargetPath,
    bool Succeeded,
    string? PreviousVersion,
    string InstalledVersion,
    bool UsedSymlink,
    string? Error = null);

/// <summary>Result of installing/overwriting the <c>octopus-plugin</c> Skill across one or more Agent targets.</summary>
public sealed record SkillInstallBatchResult(
    string SkillVersion,
    string Source,
    IReadOnlyList<SkillTargetWriteResult> Results)
{
    public int SucceededCount => Results.Count(r => r.Succeeded);
    public int FailedCount => Results.Count(r => !r.Succeeded);
}

internal sealed record SkillMetaFile(string? Name, string? Version, int? PluginApiVersion);

internal sealed record RemoteSkillManifest(
    string Name,
    string Version,
    int PluginApiVersion,
    string Sha256,
    long BundleSize,
    string BundleUrl);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SkillMetaFile))]
[JsonSerializable(typeof(RemoteSkillManifest))]
internal sealed partial class SkillInstallerJson : JsonSerializerContext;

/// <summary>
/// Installs the official <c>octopus-plugin</c> authoring Skill into one or more AI coding agent skill directories.
/// Every invocation performs a strict atomic overwrite (staging → SHA-256 verification → deleting the previous
/// <c>octopus-plugin</c> folder → writing the fresh copy) so stale API docs or templates never linger.
/// Default targets: <c>agents</c> (<c>.agents/skills</c>), <c>claude</c>, <c>cursor</c>, <c>codex</c>.
/// </summary>
public sealed class PluginSkillInstaller
{
    public const string SkillName = "octopus-plugin";

    public static readonly IReadOnlyList<SkillTargetSpec> BuiltInTargets =
    [
        new(
            "agents",
            "Standard .agents (Recommended)",
            "通用 .agents 标准（默认首选）",
            "Antigravity (agy), OpenCode, Amp, Cline, Kilo, Goose, Qwen, Kimi, Droid, etc.",
            "Antigravity (agy)、OpenCode、Amp、Cline、Kilo、Goose、Qwen、Kimi 等 30+ 智能体",
            Path.Combine(".agents", "skills", SkillName),
            Path.Combine(".agents", "skills", SkillName),
            DefaultSelected: true),
        new(
            "claude",
            "Claude Code",
            "Claude Code",
            "Anthropic Claude Code CLI / ACP",
            "Anthropic Claude Code CLI / ACP",
            Path.Combine(".claude", "skills", SkillName),
            Path.Combine(".claude", "skills", SkillName),
            DefaultSelected: true),
        new(
            "cursor",
            "Cursor",
            "Cursor",
            "Cursor Agent / IDE",
            "Cursor Agent / IDE",
            Path.Combine(".cursor", "skills", SkillName),
            Path.Combine(".cursor", "skills", SkillName),
            DefaultSelected: true),
        new(
            "codex",
            "Codex CLI",
            "Codex CLI",
            "OpenAI Codex CLI",
            "OpenAI Codex CLI",
            Path.Combine(".codex", "skills", SkillName),
            Path.Combine(".codex", "skills", SkillName),
            DefaultSelected: true),
        new(
            "copilot",
            "GitHub Copilot",
            "GitHub Copilot",
            "GitHub Copilot CLI / VS Code",
            "GitHub Copilot CLI / VS Code",
            Path.Combine(".copilot", "skills", SkillName),
            Path.Combine(".github", "skills", SkillName),
            DefaultSelected: false),
        new(
            "gemini",
            "Gemini CLI",
            "Gemini CLI",
            "Google Gemini CLI",
            "Google Gemini CLI",
            Path.Combine(".gemini", "skills", SkillName),
            Path.Combine(".gemini", "skills", SkillName),
            DefaultSelected: false),
        new(
            "windsurf",
            "Windsurf",
            "Windsurf",
            "Codeium Windsurf",
            "Codeium Windsurf",
            Path.Combine(".codeium", "windsurf", "skills", SkillName),
            Path.Combine(".windsurf", "skills", SkillName),
            DefaultSelected: false),
        new(
            "kiro",
            "Kiro",
            "Kiro",
            "AWS Kiro",
            "AWS Kiro",
            Path.Combine(".kiro", "skills", SkillName),
            Path.Combine(".kiro", "skills", SkillName),
            DefaultSelected: false),
    ];

    private readonly string? _builtinSkillDirectory;
    private readonly string _userHomeDirectory;
    private readonly HttpClient _http;

    public PluginSkillInstaller(string? builtinSkillDirectory, HttpClient? httpClient = null, string? userHomeDirectory = null)
    {
        _builtinSkillDirectory = builtinSkillDirectory;
        _userHomeDirectory = userHomeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>Inspects all built-in targets on this machine and reads any currently installed Skill version.</summary>
    public IReadOnlyList<SkillTargetStatus> InspectTargets(SkillInstallScope scope, string? projectRoot = null)
    {
        var baseDir = ResolveBaseDir(scope, projectRoot);
        var list = new List<SkillTargetStatus>(BuiltInTargets.Count);
        foreach (var spec in BuiltInTargets)
        {
            var rel = scope == SkillInstallScope.ProjectLocal ? spec.ProjectRelativePath : spec.GlobalRelativePath;
            var resolved = Path.Combine(baseDir, rel);
            var rootFolder = Path.Combine(baseDir, FirstSegment(rel));
            var detected = Directory.Exists(rootFolder) || Directory.Exists(resolved);
            var installed = ReadInstalledVersion(resolved);
            list.Add(new SkillTargetStatus(spec, resolved, detected, installed));
        }

        return list;
    }

    public string ResolveTargetPath(string targetIdOrPath, SkillInstallScope scope, string? projectRoot = null)
    {
        var baseDir = ResolveBaseDir(scope, projectRoot);
        var spec = BuiltInTargets.FirstOrDefault(t => t.Id.Equals(targetIdOrPath, StringComparison.OrdinalIgnoreCase));
        if (spec is not null)
        {
            var rel = scope == SkillInstallScope.ProjectLocal ? spec.ProjectRelativePath : spec.GlobalRelativePath;
            return Path.GetFullPath(Path.Combine(baseDir, rel));
        }

        var expanded = ExpandTilde(targetIdOrPath.Trim());
        var full = Path.GetFullPath(expanded);
        return string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(full)), SkillName, StringComparison.OrdinalIgnoreCase)
            ? full
            : Path.Combine(full, SkillName);
    }

    /// <summary>
    /// Installs and strictly overwrites the <c>octopus-plugin</c> Skill into every requested target.
    /// Attempts to fetch the latest signed/hashed bundle from <paramref name="serverBaseUrl"/> first,
    /// falling back cleanly to the embedded <c>BuiltinSkills/octopus-plugin</c> directory when offline.
    /// </summary>
    public async Task<SkillInstallBatchResult> InstallOverwriteAsync(
        IReadOnlyList<string> targetIdsOrPaths,
        SkillInstallScope scope,
        string? projectRoot = null,
        string? serverBaseUrl = null,
        bool preferSymlinkToAgents = false,
        CancellationToken cancellationToken = default)
    {
        if (targetIdsOrPaths.Count == 0)
        {
            throw new ArgumentException("At least one target must be selected.", nameof(targetIdsOrPaths));
        }

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"octopus-skill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        try
        {
            var (sourceDir, version, sourceKind) = await PrepareSourceAsync(stagingRoot, serverBaseUrl, cancellationToken).ConfigureAwait(false);
            var results = new List<SkillTargetWriteResult>(targetIdsOrPaths.Count);
            string? canonicalAgentsPath = null;

            // Ensure "agents" is written first when symlink mode is enabled.
            var ordered = targetIdsOrPaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t.Equals("agents", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();

            foreach (var target in ordered)
            {
                var dest = ResolveTargetPath(target, scope, projectRoot);
                var prevVer = ReadInstalledVersion(dest);
                try
                {
                    var parent = Path.GetDirectoryName(dest)!;
                    Directory.CreateDirectory(parent);

                    // Strict force-overwrite: remove any existing folder/symlink first so stale files cannot survive.
                    ForceDeletePath(dest);

                    var usedSymlink = false;
                    if (preferSymlinkToAgents
                        && !target.Equals("agents", StringComparison.OrdinalIgnoreCase)
                        && canonicalAgentsPath is not null
                        && !OperatingSystem.IsWindows())
                    {
                        Directory.CreateSymbolicLink(dest, canonicalAgentsPath);
                        usedSymlink = true;
                    }
                    else
                    {
                        var tempDest = dest + $".tmp-{Guid.NewGuid():N}";
                        try
                        {
                            CopyDirectory(sourceDir, tempDest);
                            Directory.Move(tempDest, dest);
                        }
                        catch
                        {
                            ForceDeletePath(tempDest);
                            throw;
                        }
                    }

                    if (target.Equals("agents", StringComparison.OrdinalIgnoreCase))
                    {
                        canonicalAgentsPath = dest;
                    }

                    results.Add(new SkillTargetWriteResult(target, dest, true, prevVer, version, usedSymlink));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    results.Add(new SkillTargetWriteResult(target, dest, false, prevVer, version, false, ex.Message));
                }
            }

            return new SkillInstallBatchResult(version, sourceKind, results);
        }
        finally
        {
            ForceDeletePath(stagingRoot);
        }
    }

    public static string? ReadInstalledVersion(string skillDirectory)
    {
        try
        {
            var metaPath = Path.Combine(skillDirectory, ".skill-meta.json");
            if (File.Exists(metaPath))
            {
                var meta = JsonSerializer.Deserialize(File.ReadAllText(metaPath), SkillInstallerJson.Default.SkillMetaFile);
                if (!string.IsNullOrWhiteSpace(meta?.Version)) return meta.Version;
            }

            return File.Exists(Path.Combine(skillDirectory, "SKILL.md")) ? "unknown" : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<(string SourceDir, string Version, string SourceKind)> PrepareSourceAsync(
        string stagingRoot,
        string? serverBaseUrl,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(serverBaseUrl))
        {
            try
            {
                var baseUri = serverBaseUrl.Trim().TrimEnd('/');
                using var manifestResp = await _http.GetAsync($"{baseUri}/api/skills/{SkillName}/manifest", ct).ConfigureAwait(false);
                if (manifestResp.IsSuccessStatusCode)
                {
                    var manifestJson = await manifestResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var manifest = JsonSerializer.Deserialize(manifestJson, SkillInstallerJson.Default.RemoteSkillManifest);
                    if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.Sha256))
                    {
                        var bundleUrl = string.IsNullOrWhiteSpace(manifest.BundleUrl)
                            ? $"{baseUri}/api/skills/{SkillName}/bundle.zip"
                            : manifest.BundleUrl;
                        var zipBytes = await _http.GetByteArrayAsync(bundleUrl, ct).ConfigureAwait(false);
                        var actualSha = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
                        if (string.Equals(actualSha, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            var extracted = ExtractSafeZip(zipBytes, Path.Combine(stagingRoot, "remote"));
                            return (extracted, manifest.Version, "server");
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or JsonException)
            {
                // Fall back to the embedded offline Skill bundle when the server is unreachable.
            }
        }

        if (!string.IsNullOrWhiteSpace(_builtinSkillDirectory) && Directory.Exists(_builtinSkillDirectory))
        {
            var ver = ReadInstalledVersion(_builtinSkillDirectory) ?? "1.0.0";
            return (_builtinSkillDirectory, ver, "builtin");
        }

        throw new InvalidOperationException("No Skill bundle is available online or in BuiltinSkills.");
    }

    private static string ExtractSafeZip(byte[] zipBytes, string destRoot)
    {
        Directory.CreateDirectory(destRoot);
        var fullRoot = Path.GetFullPath(destRoot);
        using var zip = new ZipArchive(new MemoryStream(zipBytes, writable: false), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrEmpty(name) || name.StartsWith('/') || name.Contains("..", StringComparison.Ordinal) || name.Contains(':'))
            {
                throw new InvalidDataException($"Unsafe entry in skill bundle: {name}");
            }

            var target = Path.GetFullPath(Path.Combine(fullRoot, name));
            if (!target.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Skill bundle entry escaped root: {name}");
            }

            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }

        var nested = Path.Combine(fullRoot, SkillName);
        return File.Exists(Path.Combine(nested, "SKILL.md")) ? nested : fullRoot;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private static void ForceDeletePath(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is not null)
            {
                info.Delete();
                return;
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string ResolveBaseDir(SkillInstallScope scope, string? projectRoot) =>
        scope == SkillInstallScope.ProjectLocal && !string.IsNullOrWhiteSpace(projectRoot)
            ? Path.GetFullPath(projectRoot)
            : _userHomeDirectory;

    private string ExpandTilde(string path)
    {
        if (path == "~") return _userHomeDirectory;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(_userHomeDirectory, path[2..]);
        }

        return path;
    }

    private static string FirstSegment(string relativePath)
    {
        var idx = relativePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return idx > 0 ? relativePath[..idx] : relativePath;
    }
}
