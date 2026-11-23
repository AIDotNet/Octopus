using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Acp.Process;

/// <summary>
/// Atomically installs and caches companion ACP adapters under <c>&lt;CacheDirectory&gt;/acp-adapters/&lt;agentId&gt;/</c>.
/// Prevents half-installed global <c>~/.npm/_npx</c> corruption when interrupted, skips redundant bundled CLIs
/// when the native CLI is already installed, and makes subsequent launches instantaneous (&lt;1s).
/// </summary>
public sealed class AcpAdapterInstaller(IAppPaths paths, AcpRegistryClient registry, HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public string AdapterRoot(string agentId) =>
        Path.Combine(paths.CacheDirectory, "acp-adapters", SanitizeSegment(agentId));

    /// <summary>Returns an already-installed managed adapter executable if present and intact; null otherwise.</summary>
    public string? FindCachedExecutable(string agentId, AgentAcpAdapterSpec adapter)
    {
        var root = AdapterRoot(agentId);

        // 1. Standalone registry binary cache
        var binDir = Path.Combine(root, "bin");
        if (Directory.Exists(binDir))
        {
            foreach (var name in adapter.Executables)
            {
                var found = FindBinaryInDir(binDir, name);
                if (found is not null) return found;
            }
        }

        // 2. Managed npm prefix cache
        var npmBinDir = Path.Combine(root, "npm", "node_modules", ".bin");
        if (Directory.Exists(npmBinDir))
        {
            foreach (var name in adapter.Executables)
            {
                var candidate = Path.Combine(npmBinDir, OperatingSystem.IsWindows() ? $"{name}.cmd" : name);
                if (File.Exists(candidate)) return candidate;
                var bare = Path.Combine(npmBinDir, name);
                if (File.Exists(bare)) return bare;
            }
        }

        return null;
    }

    /// <summary>Atomically installs an npm ACP adapter into Octopus's cache directory and returns its executable path.</summary>
    public async Task<string> EnsureNpmAdapterAsync(
        string agentId,
        AgentAcpAdapterSpec adapter,
        string npmExecutable,
        IReadOnlyDictionary<string, string> environment,
        bool omitOptional,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(adapter.NpxPackage))
        {
            throw new InvalidOperationException($"Adapter for '{agentId}' does not specify an npm package.");
        }

        var gate = _locks.GetOrAdd(agentId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (FindCachedExecutable(agentId, adapter) is { } ready)
            {
                return ready;
            }

            onProgress?.Invoke($"Installing {adapter.NpxPackage}…");

            var root = AdapterRoot(agentId);
            Directory.CreateDirectory(root);
            var finalDir = Path.Combine(root, "npm");
            var tempDir = Path.Combine(root, $"npm.tmp.{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = npmExecutable,
                    WorkingDirectory = tempDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("install");
                if (omitOptional)
                {
                    psi.ArgumentList.Add("--omit=optional");
                }
                psi.ArgumentList.Add("--no-audit");
                psi.ArgumentList.Add("--no-fund");
                psi.ArgumentList.Add("--loglevel=error");
                psi.ArgumentList.Add("--prefix");
                psi.ArgumentList.Add(tempDir);
                psi.ArgumentList.Add(adapter.NpxPackage);

                psi.Environment.Clear();
                foreach (var (k, v) in environment)
                {
                    psi.Environment[k] = v;
                }

                using var proc = new System.Diagnostics.Process { StartInfo = psi };
                if (!proc.Start())
                {
                    throw new InvalidOperationException($"Failed to start '{npmExecutable}' to install '{adapter.NpxPackage}'.");
                }

                var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);

                using var installTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                installTimeout.CancelAfter(TimeSpan.FromMinutes(4));
                try
                {
                    await proc.WaitForExitAsync(installTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    if (cancellationToken.IsCancellationRequested) throw;
                    throw new TimeoutException($"Installing ACP adapter '{adapter.NpxPackage}' via npm timed out.");
                }

                var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
                _ = await stdoutTask.ConfigureAwait(false);

                if (proc.ExitCode != 0)
                {
                    var detail = string.IsNullOrWhiteSpace(stderr) ? $"exit code {proc.ExitCode}" : TailLines(stderr, 8);
                    throw new InvalidOperationException($"Failed to install ACP adapter '{adapter.NpxPackage}': {detail}");
                }

                var binDir = Path.Combine(tempDir, "node_modules", ".bin");
                string? installedBinName = null;
                foreach (var name in adapter.Executables)
                {
                    var expected = OperatingSystem.IsWindows() ? $"{name}.cmd" : name;
                    if (File.Exists(Path.Combine(binDir, expected)))
                    {
                        installedBinName = expected;
                        break;
                    }
                    if (File.Exists(Path.Combine(binDir, name)))
                    {
                        installedBinName = name;
                        break;
                    }
                }
                if (installedBinName is null && Directory.Exists(binDir))
                {
                    var first = Directory.EnumerateFiles(binDir).FirstOrDefault();
                    if (first is not null) installedBinName = Path.GetFileName(first);
                }
                if (installedBinName is null)
                {
                    throw new InvalidOperationException($"Installed '{adapter.NpxPackage}', but no executable was found in node_modules/.bin.");
                }

                if (Directory.Exists(finalDir))
                {
                    try { Directory.Delete(finalDir, recursive: true); } catch { }
                }
                Directory.Move(tempDir, finalDir);
                return Path.Combine(finalDir, "node_modules", ".bin", installedBinName);
            }
            catch
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Downloads and verifies a standalone binary from the official ACP Registry when available for this host.</summary>
    public async Task<string?> TryEnsureRegistryBinaryAsync(
        string agentId,
        AgentAcpAdapterSpec adapter,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(adapter.RegistryId) || AcpRegistryClient.CurrentTargetKey() is not { } targetKey)
        {
            return null;
        }

        var gate = _locks.GetOrAdd(agentId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (FindCachedExecutable(agentId, adapter) is { } ready)
            {
                return ready;
            }

            var entry = await registry.FindAsync(adapter.RegistryId, cancellationToken).ConfigureAwait(false);
            if (entry?.Distribution?.Binary is null || !entry.Distribution.Binary.TryGetValue(targetKey, out var dist)
                || string.IsNullOrWhiteSpace(dist.Archive))
            {
                return null;
            }

            onProgress?.Invoke($"Downloading {entry.Name} ({entry.Version})…");

            var root = AdapterRoot(agentId);
            Directory.CreateDirectory(root);
            var finalDir = Path.Combine(root, "bin");
            var tempDir = Path.Combine(root, $"bin.tmp.{Guid.NewGuid():N}");
            var archiveFile = Path.Combine(root, $"archive.{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                using (var resp = await _http.GetAsync(dist.Archive, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = File.Create(archiveFile);
                    await resp.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                }

                if (!string.IsNullOrWhiteSpace(dist.Sha256))
                {
                    await using var fs = File.OpenRead(archiveFile);
                    var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, cancellationToken).ConfigureAwait(false));
                    if (!string.Equals(hash, dist.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"SHA-256 mismatch for ACP adapter '{entry.Id}'.");
                    }
                }

                if (dist.Archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ZipFile.ExtractToDirectory(archiveFile, tempDir, overwriteFiles: true);
                }
                else
                {
                    await using var fs = File.OpenRead(archiveFile);
                    await using var gz = new GZipStream(fs, CompressionMode.Decompress);
                    await TarFile.ExtractToDirectoryAsync(gz, tempDir, overwriteFiles: true, cancellationToken).ConfigureAwait(false);
                }

                var cmdRel = dist.Cmd.TrimStart('.', '/', '\\');
                var extractedCmd = Path.Combine(tempDir, cmdRel);
                if (!File.Exists(extractedCmd))
                {
                    extractedCmd = adapter.Executables.Select(n => FindBinaryInDir(tempDir, n)).FirstOrDefault(p => p is not null) ?? "";
                }
                if (string.IsNullOrEmpty(extractedCmd) || !File.Exists(extractedCmd))
                {
                    return null;
                }

                if (!OperatingSystem.IsWindows())
                {
                    try
                    {
                        File.SetUnixFileMode(extractedCmd,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }
                    catch { }
                }

                var relativeCmd = Path.GetRelativePath(tempDir, extractedCmd);
                if (Directory.Exists(finalDir))
                {
                    try { Directory.Delete(finalDir, recursive: true); } catch { }
                }
                Directory.Move(tempDir, finalDir);
                return Path.Combine(finalDir, relativeCmd);
            }
            catch
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
                throw;
            }
            finally
            {
                try { if (File.Exists(archiveFile)) File.Delete(archiveFile); } catch { }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static string? FindBinaryInDir(string dir, string name)
    {
        var candidates = OperatingSystem.IsWindows() ? new[] { $"{name}.exe", $"{name}.cmd", name } : new[] { name };
        foreach (var c in candidates)
        {
            var direct = Path.Combine(dir, c);
            if (File.Exists(direct)) return direct;
        }
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(file);
                if (candidates.Contains(fileName, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
                {
                    return file;
                }
            }
        }
        catch { }
        return null;
    }

    private static string SanitizeSegment(string id) =>
        string.Concat(id.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));

    private static string TailLines(string text, int maxLines)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n", lines.TakeLast(maxLines));
    }
}
