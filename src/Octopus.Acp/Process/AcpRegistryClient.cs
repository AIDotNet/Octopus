using System.Runtime.InteropServices;
using System.Text.Json;
using Octopus.Acp.Protocol;
using Octopus.Platform;

namespace Octopus.Acp.Process;

/// <summary>
/// Fetches and caches the official ACP Registry (<c>https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json</c>)
/// with hourly refresh, matching Orca's <c>acp-registry-catalog.ts</c>.
/// </summary>
public sealed class AcpRegistryClient(IAppPaths paths, HttpClient? http = null)
{
    public const string RegistryUrl = "https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _fetchedAt;
    private Dictionary<string, AcpRegistryAgent> _agents = new(StringComparer.OrdinalIgnoreCase);

    public async Task<AcpRegistryAgent?> FindAsync(string registryId, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _agents.TryGetValue(registryId, out var agent) ? agent : null;
    }

    public async Task<IReadOnlyList<AcpRegistryAgent>> ListAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _agents.Values.ToList();
    }

    /// <summary>Returns the official registry target key for this host (e.g. <c>darwin-aarch64</c>, <c>windows-x86_64</c>).</summary>
    public static string? CurrentTargetKey()
    {
        var os = OperatingSystem.IsMacOS() ? "darwin"
            : OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : null;
        var cpu = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "aarch64",
            Architecture.X64 => "x86_64",
            _ => null,
        };
        return os is not null && cpu is not null ? $"{os}-{cpu}" : null;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_agents.Count > 0 && DateTimeOffset.UtcNow - _fetchedAt < RefreshInterval)
        {
            return;
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_agents.Count > 0 && DateTimeOffset.UtcNow - _fetchedAt < RefreshInterval) return;

            var cachePath = Path.Combine(paths.CacheDirectory, "acp-registry.json");
            try
            {
                var json = await _http.GetStringAsync(RegistryUrl, cancellationToken).ConfigureAwait(false);
                var doc = JsonSerializer.Deserialize(json, AcpJsonContext.Default.AcpRegistryDocument);
                if (doc?.Agents is { Count: > 0 })
                {
                    _agents = doc.Agents
                        .Where(a => !string.IsNullOrWhiteSpace(a.Id))
                        .ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
                    _fetchedAt = DateTimeOffset.UtcNow;
                    try
                    {
                        Directory.CreateDirectory(paths.CacheDirectory);
                        await File.WriteAllTextAsync(cachePath, json, cancellationToken).ConfigureAwait(false);
                    }
                    catch { /* Cache write is best-effort. */ }
                    return;
                }
            }
            catch
            {
                // Fall back to disk cache when offline.
            }

            if (_agents.Count == 0 && File.Exists(cachePath))
            {
                try
                {
                    var cached = await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
                    var doc = JsonSerializer.Deserialize(cached, AcpJsonContext.Default.AcpRegistryDocument);
                    if (doc?.Agents is { Count: > 0 })
                    {
                        _agents = doc.Agents
                            .Where(a => !string.IsNullOrWhiteSpace(a.Id))
                            .ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
                        _fetchedAt = DateTimeOffset.UtcNow;
                    }
                }
                catch { /* Ignore corrupt cache. */ }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
