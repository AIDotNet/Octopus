using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Agents;

/// <summary>Runs an agent's <c>model.discover</c> command and parses its output. Results are cached in memory.</summary>
public sealed partial class ModelDiscovery : IModelDiscovery
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly IProcessRunner _runner;
    private readonly ILoginEnvironment _loginEnvironment;
    private readonly ISystemProxy? _systemProxy;
    private readonly ConcurrentDictionary<string, IReadOnlyList<AgentOption>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<AgentOption>>> _inflight = new(StringComparer.OrdinalIgnoreCase);

    public ModelDiscovery(IProcessRunner runner, ILoginEnvironment loginEnvironment, ISystemProxy? systemProxy = null)
    {
        _runner = runner;
        _loginEnvironment = loginEnvironment;
        _systemProxy = systemProxy;
    }

    public async Task<IReadOnlyList<AgentOption>> DiscoverAsync(
        AgentDefinition definition,
        string executable,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var discover = definition.Capabilities.Model?.Discover;
        if (discover is null || discover.Args.Count == 0)
        {
            return [];
        }
        if (!refresh)
        {
            if (_cache.TryGetValue(definition.Id, out var cached)) return cached;
            var key = $"{definition.Id}\0{executable}";
            var shared = _inflight.GetOrAdd(key, _ => RunDiscoverAsync(definition, executable, discover, CancellationToken.None));
            try
            {
                return await shared.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (shared.IsCompleted) _inflight.TryRemove(KeyValuePair.Create(key, shared));
            }
        }
        return await RunDiscoverAsync(definition, executable, discover, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<AgentOption>> RunDiscoverAsync(
        AgentDefinition definition,
        string executable,
        ModelDiscoverySpec discover,
        CancellationToken cancellationToken)
    {
        try
        {
            var env = await _loginEnvironment.GetAsync(cancellationToken).ConfigureAwait(false);
            var merged = new Dictionary<string, string>(env, StringComparer.Ordinal);
            foreach (var (k, v) in definition.Detect.ProbeEnv)
            {
                merged[k] = v;
            }
            if (_systemProxy?.Resolve() is { } proxy)
            {
                foreach (var (k, v) in AgentProxyEnvironment.Compute(proxy, merged))
                {
                    merged[k] = v;
                }
            }
            var result = await _runner.RunAsync(executable, discover.Args, merged, null, Timeout, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return [];
            }
            var options = Parse(result.StandardOutput, discover.Parser);
            if (options.Count > 0)
            {
                _cache[definition.Id] = options;
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
