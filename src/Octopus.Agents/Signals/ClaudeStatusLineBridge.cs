using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Platform;

namespace Octopus.Agents.Signals;

/// <summary>One Claude Code configuration directory and whether the bridge is installed in it.</summary>
/// <param name="Account">The account's display name and e-mail from <c>.claude.json</c> (<c>oauthAccount</c>), for the settings page only.</param>
/// <param name="UpdatedAt">When the status line last reported through the bridge; null when it has not yet.</param>
public sealed record ClaudeConfigDirectory(string Directory, string? Account, bool IsInstalled, DateTimeOffset? UpdatedAt);

/// <summary>
/// Claude Code's documented status line (https://code.claude.com/docs/en/statusline) receives the plan's usage limits
/// (<c>rate_limits.five_hour</c> / <c>seven_day</c>) on stdin. Installed with the user's consent, a small bridge script
/// becomes the status line of a configuration directory: it saves that JSON for Octopus and then runs the status line
/// the user had, so their status line looks the same. Unlike the per-session attach (<see cref="SignalAttachPlanner"/>),
/// every Claude Code session reports, also the ones started outside Octopus, and the last report is kept on disk.
/// No token is read and no undocumented endpoint is called. The approach follows O_O's ClaudeStatusLineBridge.
/// <para>
/// Installing edits <c>&lt;config dir&gt;/settings.json</c>: the previous <c>statusLine</c> is kept in a backup and put back
/// exactly by <see cref="Restore"/>; a settings file that cannot be parsed is never touched.
/// </para>
/// </summary>
public sealed class ClaudeStatusLineBridge(IAppPaths paths)
{
    /// <summary>Appears in the installed command, so an installation is recognized (and never stacked twice).</summary>
    public const string Marker = "octopus-statusline";

    public string Directory => Path.Combine(paths.DataDirectory, "claude-statusline");

    public string ScriptPath => Path.Combine(Directory, OperatingSystem.IsWindows() ? $"{Marker}.ps1" : $"{Marker}.sh");

    public static string Hash(string configDir) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(configDir)))[..8];

    public string OutputPath(string configDir) => Path.Combine(Directory, $"{Hash(configDir)}.json");

    private string OriginalCommandPath(string configDir) => Path.Combine(Directory, $"{Hash(configDir)}.original");

    private string BackupPath(string configDir) => Path.Combine(Directory, $"{Hash(configDir)}.statusline-backup.json");

    /// <summary>Which configuration directory each output file belongs to (the hash is one-way).</summary>
    private string IndexPath => Path.Combine(Directory, "directories.json");

    private static string SettingsPath(string configDir) => Path.Combine(configDir, "settings.json");

    public bool IsInstalled(string configDir) =>
        ReadSettings(configDir) is { } settings &&
        settings["statusLine"]?["command"] is JsonValue command &&
        command.TryGetValue<string>(out var text) &&
        text.Contains(Marker, StringComparison.Ordinal);

    /// <summary>The configuration directories the bridge was installed in (and not restored since).</summary>
    public IReadOnlyList<string> InstalledDirectories()
    {
        try
        {
            return File.Exists(IndexPath) && JsonNode.Parse(File.ReadAllText(IndexPath)) is JsonArray list
                ? [.. list.Select(n => n?.GetValue<string>()).OfType<string>().Where(IsInstalled)]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Sets the bridge as the status line of <paramref name="configDir"/>, keeping what was there.</summary>
    /// <exception cref="InvalidOperationException">settings.json exists but cannot be parsed; nothing was changed.</exception>
    public void Install(string configDir)
    {
        var path = SettingsPath(configDir);
        var settings = File.Exists(path)
            ? ReadSettings(configDir) ?? throw new InvalidOperationException($"{path} cannot be parsed; to avoid damaging it nothing was changed.")
            : new JsonObject();
        if (IsInstalled(configDir))
        {
            Remember(configDir, add: true);
            return;
        }
        System.IO.Directory.CreateDirectory(Directory);
        WriteScript();

        var previous = settings["statusLine"];
        File.WriteAllText(BackupPath(configDir), previous?.ToJsonString() ?? "null");
        if (File.Exists(path))
        {
            File.Copy(path, Path.Combine(Directory, $"{Hash(configDir)}.settings.json.bak"), overwrite: true);
        }
        var originalCommand = previous?["type"]?.GetValue<string>() == "command" ? previous["command"]?.GetValue<string>() : null;
        if (!string.IsNullOrWhiteSpace(originalCommand))
        {
            File.WriteAllText(OriginalCommandPath(configDir), originalCommand);
        }
        else
        {
            File.Delete(OriginalCommandPath(configDir));
        }

        // Keep the previous status line's other options (padding and so on); only the command changes.
        var statusLine = previous is JsonObject old ? (JsonObject)old.DeepClone() : new JsonObject();
        statusLine["type"] = "command";
        statusLine["command"] = Command(configDir);
        settings["statusLine"] = statusLine;
        WriteSettings(configDir, settings);
        Remember(configDir, add: true);
    }

    /// <summary>Puts back the status line that was configured before <see cref="Install"/>.</summary>
    public void Restore(string configDir)
    {
        Remember(configDir, add: false);
        if (ReadSettings(configDir) is not { } settings || !IsInstalled(configDir))
        {
            return;
        }
        var backup = File.Exists(BackupPath(configDir)) ? JsonNode.Parse(File.ReadAllText(BackupPath(configDir))) : null;
        if (backup is null)
        {
            settings.Remove("statusLine");
        }
        else
        {
            settings["statusLine"] = backup;
        }
        WriteSettings(configDir, settings);
        foreach (var file in new[] { BackupPath(configDir), OriginalCommandPath(configDir), OutputPath(configDir) })
        {
            File.Delete(file);
        }
    }

    /// <summary>The JSON the status line last passed through the bridge, with the time it was written; null before the first report.</summary>
    public (string Json, DateTimeOffset At)? Read(string configDir)
    {
        var path = OutputPath(configDir);
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            using var _ = JsonDocument.Parse(json); // a half-written file (older bridge) waits for the next refresh
            return (json, File.GetLastWriteTimeUtc(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The configuration directory an output file belongs to, if the bridge was installed there.</summary>
    public string? DirectoryOfOutput(string outputPath)
    {
        var hash = Path.GetFileNameWithoutExtension(outputPath);
        return InstalledDirectories().FirstOrDefault(d => Hash(d) == hash);
    }

    /// <summary>The status line command for <paramref name="configDir"/>; paths are quoted for the shell.</summary>
    public string Command(string configDir)
    {
        if (OperatingSystem.IsWindows())
        {
            return $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{ScriptPath}\" \"{OutputPath(configDir)}\" \"{OriginalCommandPath(configDir)}\"";
        }
        static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
        return $"sh {Quote(ScriptPath)} {Quote(OutputPath(configDir))} {Quote(OriginalCommandPath(configDir))}";
    }

    private void Remember(string configDir, bool add)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var list = new List<string>();
        try
        {
            if (File.Exists(IndexPath) && JsonNode.Parse(File.ReadAllText(IndexPath)) is JsonArray stored)
            {
                list.AddRange(stored.Select(n => n?.GetValue<string>()).OfType<string>());
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
        }
        list.RemoveAll(d => d == configDir);
        if (add) list.Add(configDir);
        var array = new JsonArray();
        foreach (var dir in list) array.Add((JsonNode)JsonValue.Create(dir));
        File.WriteAllText(IndexPath, array.ToJsonString());
    }

    private void WriteScript()
    {
        var script = OperatingSystem.IsWindows()
            ? """
              # Octopus bridge for Claude Code's status line (installed by Octopus; restore it in Octopus's settings).
              # Saves the JSON Claude Code passes on stdin for Octopus, then runs the status line configured before.
              param([string]$Out, [string]$Orig)
              $in = [Console]::In.ReadToEnd()
              [IO.File]::WriteAllText("$Out.tmp", $in)
              Move-Item -Force "$Out.tmp" $Out
              if ($Orig -and (Test-Path $Orig)) { $in | & cmd /c (Get-Content -Raw $Orig) }
              """
            : """
              #!/bin/sh
              # Octopus bridge for Claude Code's status line (installed by Octopus; restore it in Octopus's settings).
              # Saves the JSON Claude Code passes on stdin for Octopus, then runs the status line configured before.
              out="$1"
              orig="$2"
              input=$(cat)
              printf '%s' "$input" > "$out.tmp" && mv -f "$out.tmp" "$out"
              if [ -s "$orig" ]; then
                printf '%s' "$input" | sh -c "$(cat "$orig")"
              fi
              """;
        File.WriteAllText(ScriptPath, script.Replace("\r\n", "\n"));
    }

    private static JsonObject? ReadSettings(string configDir)
    {
        var path = SettingsPath(configDir);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Atomic: a temporary file next to it, then a rename, so Claude Code never reads half a file.</summary>
    private static void WriteSettings(string configDir, JsonObject settings)
    {
        System.IO.Directory.CreateDirectory(configDir);
        var path = SettingsPath(configDir);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Finds Claude Code configuration directories: <c>~/.claude</c>, <c>CLAUDE_CONFIG_DIR</c>, and folders holding a
/// <c>.claude.json</c> under the hidden folders of the home directory (account managers such as <c>rec</c> give every
/// account its own, e.g. <c>~/.rec/claude/accounts/&lt;id&gt;/config</c>). Only folder names are read while scanning.
/// </summary>
public static class ClaudeConfigDirectories
{
    private const int ScanDepth = 5;
    private const int ScanMaxEntries = 20_000;
    private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(2);
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".Trash", "Library", ".cache", ".npm", ".nuget", ".cargo", ".rustup", ".gradle", ".m2",
        ".vscode", ".cursor", ".docker", ".local", "projects", "sessions", "shell-snapshots", "todos", "file-history",
    };

    public static IReadOnlyList<string> Find(string home, string? configDirVariable)
    {
        var found = new List<string>();
        void Add(string dir)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            if (!found.Contains(full, StringComparer.Ordinal)) found.Add(full);
        }
        var defaultDir = Path.Combine(home, ".claude");
        if (System.IO.Directory.Exists(defaultDir)) Add(defaultDir);
        if (!string.IsNullOrWhiteSpace(configDirVariable) && System.IO.Directory.Exists(configDirVariable)) Add(configDirVariable);

        var clock = Stopwatch.StartNew();
        var queue = new Queue<(string Dir, int Depth)>();
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var dir in System.IO.Directory.EnumerateDirectories(home, ".*", options))
            {
                if (!Skip.Contains(Path.GetFileName(dir))) queue.Enqueue((dir, 1));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        var entries = 0;
        while (queue.Count > 0 && entries < ScanMaxEntries && clock.Elapsed < ScanBudget)
        {
            var (dir, depth) = queue.Dequeue();
            entries++;
            if (File.Exists(Path.Combine(dir, ".claude.json")))
            {
                Add(dir);
                continue; // a configuration directory does not hold others worth finding
            }
            if (depth >= ScanDepth) continue;
            try
            {
                foreach (var child in System.IO.Directory.EnumerateDirectories(dir, "*", options))
                {
                    if (!Skip.Contains(Path.GetFileName(child))) queue.Enqueue((child, depth + 1));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return found;
    }

    /// <summary>"Name (e-mail)" from the directory's <c>.claude.json</c> (<c>~/.claude.json</c> for the default directory); never credentials.</summary>
    public static string? AccountOf(string configDir, string home)
    {
        var file = Path.Combine(configDir, ".claude.json");
        if (!File.Exists(file) && Path.GetFullPath(configDir) == Path.GetFullPath(Path.Combine(home, ".claude"))) file = Path.Combine(home, ".claude.json");
        try
        {
            if (!File.Exists(file)) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions { AllowTrailingCommas = true });
            if (!document.RootElement.TryGetProperty("oauthAccount", out var account) || account.ValueKind != JsonValueKind.Object) return null;
            var email = account.TryGetProperty("emailAddress", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            var name = account.TryGetProperty("displayName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            return name is { Length: > 0 } && email is not null ? $"{name} ({email})" : email ?? name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Turns what the bridge writes into <c>claude/statusline</c> signals on the hub, so plugins receive them exactly as
/// they receive status lines of sessions Octopus started: each installed directory is registered as an account, its
/// last report is published at start, and every new report as it is written.
/// </summary>
public sealed class ClaudeStatusLineWatcher(ClaudeStatusLineBridge bridge, ISignalHub hub) : IDisposable
{
    private FileSystemWatcher? _watcher;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _published = new(StringComparer.Ordinal);

    public void Start()
    {
        lock (_gate)
        {
            if (_watcher is not null) return;
            System.IO.Directory.CreateDirectory(bridge.Directory);
            _watcher = new FileSystemWatcher(bridge.Directory, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            _watcher.Created += (_, e) => PublishFile(e.FullPath);
            _watcher.Changed += (_, e) => PublishFile(e.FullPath);
            _watcher.Renamed += (_, e) => PublishFile(e.FullPath);
            _watcher.EnableRaisingEvents = true;
        }
        foreach (var dir in bridge.InstalledDirectories()) Publish(dir);
    }

    /// <summary>Publishes a directory's last report now (after installing, so the plugin knows the account at once).</summary>
    public void Publish(string configDir)
    {
        var key = hub.RegisterAccount("claude", configDir);
        if (bridge.Read(configDir) is not { } report) return;
        lock (_gate)
        {
            var stamp = report.At.ToUnixTimeMilliseconds();
            if (_published.TryGetValue(configDir, out var last) && last == stamp) return;
            _published[configDir] = stamp;
        }
        hub.Publish(new AgentSignal("claude", "statusline", "", key, report.At.ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), report.Json));
    }

    private void PublishFile(string path)
    {
        if (Path.GetFileName(path) == "directories.json") return;
        if (bridge.DirectoryOfOutput(path) is { } dir) Publish(dir);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }
}
