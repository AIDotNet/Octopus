using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Agents;

/// <summary>Runs an agent's <c>model.discover</c> strategy and parses its output. Results are cached in memory.</summary>
public sealed partial class ModelDiscovery : IModelDiscovery
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DynamicCacheTtl = TimeSpan.FromSeconds(30);

    private static readonly string[] FingerprintEnvVars =
    [
        "CODEX_HOME", "OPENAI_BASE_URL", "OPENAI_API_KEY", "CODEX_API_KEY",
        "CLAUDE_CONFIG_DIR", "ANTHROPIC_BASE_URL", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_MODEL",
    ];

    private sealed record CacheEntry(IReadOnlyList<AgentOption> Options, DateTimeOffset CachedAt);

    private readonly IProcessRunner _runner;
    private readonly ILoginEnvironment _loginEnvironment;
    private readonly ISystemProxy? _systemProxy;
    private readonly HttpClient? _httpClient;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<AgentOption>>> _inflight = new(StringComparer.OrdinalIgnoreCase);

    public ModelDiscovery(
        IProcessRunner runner,
        ILoginEnvironment loginEnvironment,
        ISystemProxy? systemProxy = null,
        HttpClient? httpClient = null)
    {
        _runner = runner;
        _loginEnvironment = loginEnvironment;
        _systemProxy = systemProxy;
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<AgentOption>> DiscoverAsync(
        AgentDefinition definition,
        string executable,
        bool refresh = false,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        var discover = definition.Capabilities.Model?.Discover;
        if (discover is null)
        {
            return [];
        }
        var kind = string.IsNullOrWhiteSpace(discover.Kind) ? "command" : discover.Kind;
        var isDynamic = !string.Equals(kind, "command", StringComparison.OrdinalIgnoreCase);
        if (!isDynamic && discover.Args.Count == 0)
        {
            return [];
        }

        var cacheKey = BuildCacheKey(definition.Id, executable, environmentOverrides);
        if (!refresh)
        {
            if (_cache.TryGetValue(cacheKey, out var cached)
                && (!isDynamic || DateTimeOffset.UtcNow - cached.CachedAt < DynamicCacheTtl))
            {
                return cached.Options;
            }
            var shared = _inflight.GetOrAdd(
                cacheKey,
                _ => RunDiscoverAsync(definition, executable, discover, kind, cacheKey, environmentOverrides, CancellationToken.None));
            try
            {
                return await shared.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (shared.IsCompleted) _inflight.TryRemove(KeyValuePair.Create(cacheKey, shared));
            }
        }
        return await RunDiscoverAsync(definition, executable, discover, kind, cacheKey, environmentOverrides, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<AgentOption>> RunDiscoverAsync(
        AgentDefinition definition,
        string executable,
        ModelDiscoverySpec discover,
        string kind,
        string cacheKey,
        IReadOnlyDictionary<string, string>? environmentOverrides,
        CancellationToken cancellationToken)
    {
        try
        {
            var env = await _loginEnvironment.GetAsync(cancellationToken).ConfigureAwait(false);
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var merged = new Dictionary<string, string>(env, comparer);
            foreach (var (k, v) in definition.Detect.ProbeEnv)
            {
                merged[k] = v;
            }
            if (environmentOverrides is not null)
            {
                foreach (var (k, v) in environmentOverrides)
                {
                    merged[k] = v;
                }
            }
            if (_systemProxy?.Resolve() is { } proxy)
            {
                foreach (var (k, v) in AgentProxyEnvironment.Compute(proxy, merged))
                {
                    merged[k] = v;
                }
            }

            IReadOnlyList<AgentOption> options;
            if (string.Equals(kind, "codex", StringComparison.OrdinalIgnoreCase))
            {
                options = await ThirdPartyModelDiscovery.DiscoverCodexAsync(
                    executable, discover.Args, merged, _httpClient, cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(kind, "anthropic", StringComparison.OrdinalIgnoreCase))
            {
                options = await ThirdPartyModelDiscovery.DiscoverAnthropicAsync(
                    merged, _httpClient, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(executable))
                {
                    return [];
                }
                var result = await _runner.RunAsync(executable, discover.Args, merged, null, Timeout, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    return [];
                }
                options = Parse(result.StandardOutput, discover.Parser);
            }

            if (options.Count > 0)
            {
                _cache[cacheKey] = new CacheEntry(options, DateTimeOffset.UtcNow);
            }
            return options;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string BuildCacheKey(string agentId, string executable, IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return $"{agentId}\0{executable}";
        }
        var sb = new StringBuilder(agentId).Append('\0').Append(executable);
        foreach (var key in FingerprintEnvVars)
        {
            foreach (var (k, v) in overrides)
            {
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v))
                {
                    sb.Append('\0').Append(key).Append('=').Append(v);
                    break;
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>Parses discovery output; exposed for tests.</summary>
    public static IReadOnlyList<AgentOption> Parse(string output, string parser)
    {
        var options = new List<AgentOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var separator = string.Equals(parser, "idDashLabel", StringComparison.OrdinalIgnoreCase) ? " - "
            : string.Equals(parser, "idTabLabel", StringComparison.OrdinalIgnoreCase) ? "\t"
            : null;
        foreach (var rawLine in AnsiRegex().Replace(output, "").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            string id;
            string? label = null;
            if (separator is not null)
            {
                var split = line.IndexOf(separator, StringComparison.Ordinal);
                if (split <= 0)
                {
                    continue;
                }
                id = line[..split].Trim();
                label = line[(split + separator.Length)..].Trim();
            }
            else
            {
                id = line;
            }
            if (id.Length == 0 || id.Any(char.IsWhiteSpace) || !seen.Add(id))
            {
                continue;
            }
            options.Add(new AgentOption { Id = id, Label = string.IsNullOrEmpty(label) ? null : label });
        }
        return options;
    }

    [GeneratedRegex("\u001b\\[[0-9;?]*[ -/]*[@-~]|\u001b\\][^\u0007\u001b]*(\u0007|\u001b\\\\)")]
    private static partial Regex AnsiRegex();
}
