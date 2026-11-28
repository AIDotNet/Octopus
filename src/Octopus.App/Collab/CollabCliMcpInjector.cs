using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Platform;

namespace Octopus.App.Collab;

/// <summary>
/// Prepares per-session MCP configuration arguments and environment variables so native terminal CLI tabs
/// (Claude Code, Codex CLI) can invoke Octopus's local collaboration MCP gateway without exposing bearer tokens in
/// process launch arguments or plaintext config files. Agents lacking a supported private CLI mechanism fail closed.
/// </summary>
public sealed class CollabCliMcpInjector(CollabMcpServer server, IAppPaths paths) : IDisposable
{
    private const string FolderName = "collab-mcp";
    internal const string DefaultCliLeaseId = "cli";

    private sealed class LeaseHandle(CollabCliMcpInjector injector, string itemId, string leaseId) : IDisposable
    {
        private CollabCliMcpInjector? _injector = injector;

        public void Dispose()
        {
            Interlocked.Exchange(ref _injector, null)?.ReleaseLease(itemId, leaseId);
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, string?>> _trackedLeases = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// Computes extra CLI arguments and environment variables that wire <c>octopus-collab</c> into a terminal agent.
    /// Does not modify any of the user's own config files; per-session JSON files live under a private directory inside
    /// the Octopus cache directory and reference runtime environment variables rather than literal bearer tokens.
    /// Returns null (fail-closed, without issuing a token) when the server is stopped, shutting down, the forwarder is
    /// absent, or the agent lacks a supported private attachment mechanism.
    /// </summary>
    public CollabCliAttachment? Plan(AgentDefinition definition, ProjectItem item, string? forwarderExecutable) =>
        PlanCore(definition, item, forwarderExecutable, DefaultCliLeaseId, perLeaseFilename: false);

    /// <summary>
    /// Plans a CLI collaboration attachment bound to a dedicated per-process credential lease and per-lease config file.
    /// Disposing <see cref="CollabCliAttachment.Lease"/> revokes only that lease and removes only its config file.
    /// </summary>
    internal CollabCliAttachment? PlanLease(
        AgentDefinition definition,
        ProjectItem item,
        string? forwarderExecutable,
        string? leaseId = null) =>
        PlanCore(
            definition,
            item,
            forwarderExecutable,
            string.IsNullOrWhiteSpace(leaseId) ? CollabMcpServer.NewLeaseId() : leaseId,
            perLeaseFilename: true);

    private CollabCliAttachment? PlanCore(
        AgentDefinition definition,
        ProjectItem item,
        string? forwarderExecutable,
        string leaseId,
        bool perLeaseFilename)
    {
        lock (_gate)
        {
            if (_disposed) return null;
        }

        if (!server.IsRunning || string.IsNullOrWhiteSpace(server.EndpointUrl))
        {
            return null;
        }

        var hasForwarder = !string.IsNullOrWhiteSpace(forwarderExecutable) && File.Exists(forwarderExecutable);
        if (!hasForwarder)
        {
            return null;
        }

        var args = new List<string>();
        var id = definition.Id.Trim().ToLowerInvariant();
        string? configPath = null;

        if (id == "claude")
        {
            configPath = WriteClaudeMcpConfig(item.Id, perLeaseFilename ? leaseId : null, forwarderExecutable!);
            if (configPath is null)
            {
                return null;
            }
            args.Add("--mcp-config");
            args.Add(configPath);
        }
        else if (id == "codex")
        {
            // Codex CLI forwards listed environment variables from the child process environment via env_vars,
            // keeping the collaboration bearer token out of argv.
            var escapedCmd = EscapeTomlString(forwarderExecutable!);
            args.Add("-c");
            args.Add($"mcp_servers.octopus_collab.command=\"{escapedCmd}\"");
            args.Add("-c");
            args.Add("mcp_servers.octopus_collab.args=[\"--mcp\"]");
            args.Add("-c");
            args.Add($"mcp_servers.octopus_collab.env_vars=[\"{CollabMcpServer.UrlEnvVariable}\",\"{CollabMcpServer.TokenEnvVariable}\",\"{CollabMcpServer.ItemIdEnvVariable}\"]");
        }
        else
        {
            // Fail closed: agents without a supported private CLI MCP mechanism do not receive a live token.
            return null;
        }

        string token;
        string endpointUrl;
        string? replacedConfigPath = null;
        lock (_gate)
        {
            if (_disposed || !server.TryIssueLease(item.Id, leaseId, out token, out endpointUrl))
            {
                if (configPath is not null) DeleteConfigPath(configPath);
                return null;
            }
            if (!_trackedLeases.TryGetValue(item.Id, out var map))
            {
                map = new Dictionary<string, string?>(StringComparer.Ordinal);
                _trackedLeases[item.Id] = map;
            }
            if (map.TryGetValue(leaseId, out var previousPath)
                && !string.IsNullOrWhiteSpace(previousPath)
                && !string.Equals(previousPath, configPath, StringComparison.Ordinal))
            {
                replacedConfigPath = previousPath;
            }
            map[leaseId] = configPath;
        }

        if (replacedConfigPath is not null)
        {
            DeleteConfigPath(replacedConfigPath);
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CollabMcpServer.UrlEnvVariable] = endpointUrl,
            [CollabMcpServer.TokenEnvVariable] = token,
            [CollabMcpServer.ItemIdEnvVariable] = item.Id,
        };

        return new CollabCliAttachment(args, env)
        {
            Lease = new LeaseHandle(this, item.Id, leaseId),
        };
    }

    internal void ReleaseLease(string itemId, string? leaseId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(leaseId)) return;
        string? configPath = null;
        lock (_gate)
        {
            if (_trackedLeases.TryGetValue(itemId, out var map))
            {
                map.Remove(leaseId, out configPath);
                if (map.Count == 0)
                {
                    _trackedLeases.Remove(itemId);
                }
            }
        }
        server.RevokeLease(itemId, leaseId);
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            DeleteConfigPath(configPath);
        }
    }

    internal void ReleaseDefaultLease(string itemId) => ReleaseLease(itemId, DefaultCliLeaseId);

    public void CleanupItem(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return;
        List<string> pathsToDelete = [];
        lock (_gate)
        {
            if (_trackedLeases.Remove(itemId, out var map))
            {
                foreach (var path in map.Values)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        pathsToDelete.Add(path);
                    }
                }
            }
        }
        server.RevokeToken(itemId);
        foreach (var path in pathsToDelete)
        {
            DeleteConfigPath(path);
        }
        DeleteConfigFile(itemId);
    }

    public void Dispose()
    {
        List<(string ItemId, string LeaseId, string? ConfigPath)> snapshot = [];
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var (itemId, map) in _trackedLeases)
            {
                foreach (var (leaseId, path) in map)
                {
                    snapshot.Add((itemId, leaseId, path));
                }
            }
            _trackedLeases.Clear();
        }
        foreach (var (itemId, leaseId, path) in snapshot)
        {
            server.RevokeLease(itemId, leaseId);
            if (!string.IsNullOrWhiteSpace(path))
            {
                DeleteConfigPath(path);
            }
        }
    }

    private string? WriteClaudeMcpConfig(string itemId, string? leaseId, string forwarderPath)
    {
        string? tempPath = null;
        try
        {
            var dir = EnsurePrivateDirectory();
            if (dir is null)
            {
                return null;
            }

            var fileStem = string.IsNullOrWhiteSpace(leaseId)
                ? Sanitize(itemId)
                : $"{Sanitize(itemId)}-{Sanitize(leaseId)}";
            var path = Path.Combine(dir, $"{fileStem}.json");
            if (IsSymlinkOrReparsePoint(path))
            {
                return null;
            }

            // Claude Code expands ${VAR} references in --mcp-config env entries from the process environment,
            // so the secret bearer token is never written to disk.
            var doc = new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    [CollabMcpServer.ServerName] = new JsonObject
                    {
                        ["command"] = forwarderPath,
                        ["args"] = new JsonArray("--mcp"),
                        ["env"] = new JsonObject
                        {
                            [CollabMcpServer.UrlEnvVariable] = $"${{{CollabMcpServer.UrlEnvVariable}}}",
                            [CollabMcpServer.TokenEnvVariable] = $"${{{CollabMcpServer.TokenEnvVariable}}}",
                            [CollabMcpServer.ItemIdEnvVariable] = $"${{{CollabMcpServer.ItemIdEnvVariable}}}",
                        },
                    },
                },
            };

            var content = Encoding.UTF8.GetBytes(doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            tempPath = Path.Combine(dir, $".{fileStem}.{Guid.NewGuid():N}.tmp");
            WritePrivateAtomic(tempPath, path, content);
            tempPath = null;
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); } catch { }
            }
            return null;
        }
    }

    private string? EnsurePrivateDirectory()
    {
        var dir = Path.Combine(paths.CacheDirectory, FolderName);
        if (IsSymlinkOrReparsePoint(dir))
        {
            return null;
        }
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
            return IsSymlinkOrReparsePoint(dir) ? null : dir;
        }

        const UnixFileMode privateDirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(dir, privateDirMode);
        if (IsSymlinkOrReparsePoint(dir))
        {
            return null;
        }
        File.SetUnixFileMode(dir, privateDirMode);
        return dir;
    }

    private static void WritePrivateAtomic(string tempPath, string destinationPath, byte[] content)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var fs = new FileStream(tempPath, options))
        {
            fs.Write(content);
            fs.Flush();
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(tempPath, destinationPath, overwrite: true);
    }

    private void DeleteConfigFile(string itemId)
    {
        var dir = Path.Combine(paths.CacheDirectory, FolderName);
        if (IsSymlinkOrReparsePoint(dir)) return;
        DeleteConfigPath(Path.Combine(dir, $"{Sanitize(itemId)}.json"));
    }

    private void DeleteConfigPath(string file)
    {
        try
        {
            var dir = Path.Combine(paths.CacheDirectory, FolderName);
            if (IsSymlinkOrReparsePoint(dir)) return;
            if (File.Exists(file) && !IsSymlinkOrReparsePoint(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsSymlinkOrReparsePoint(string path)
    {
        if (!Path.Exists(path)) return false;
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
            var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string EscapeTomlString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Sanitize(string itemId)
    {
        var sb = new StringBuilder(itemId.Length);
        foreach (var ch in itemId)
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }
        return sb.Length > 0 ? sb.ToString() : "session";
    }
}

public sealed record CollabCliAttachment(
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>Per-process credential/config lease handle disposed when the process exits, fails, restarts, or closes.</summary>
    internal IDisposable? Lease { get; init; }
}
