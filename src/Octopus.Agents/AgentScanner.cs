using System.Text.Json;
using System.Text.RegularExpressions;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Agents;

/// <summary>Finds installed agents by running every PATH candidate's version command. See design/05 section 5.</summary>
public sealed class AgentScanner : IAgentScanner
{
    private const string CacheFileName = "agent-scan.json";
    private const int MaxConcurrency = 6;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly IAgentCatalog _catalog;
    private readonly IExecutableLocator _locator;
    private readonly IProcessRunner _runner;
    private readonly ILoginEnvironment _loginEnvironment;
    private readonly IAppPaths _paths;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _gate = new();
    private AgentScanResult _current = new();
    private int _scanning;

    public AgentScanner(
        IAgentCatalog catalog,
        IExecutableLocator locator,
        IProcessRunner runner,
        ILoginEnvironment loginEnvironment,
        IAppPaths paths)
    {
        _catalog = catalog;
        _locator = locator;
        _runner = runner;
        _loginEnvironment = loginEnvironment;
        _paths = paths;
    }

    public AgentScanResult Current
    {
        get { lock (_gate) { return _current; } }
    }

    public bool IsScanning => Volatile.Read(ref _scanning) > 0;

    public event EventHandler? Changed;

    public AgentInstallation? Find(string agentId) =>
        Current.Installations.FirstOrDefault(i => string.Equals(i.AgentId, agentId, StringComparison.OrdinalIgnoreCase));

    public async Task LoadCacheAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var path = Path.Combine(_paths.CacheDirectory, CacheFileName);
            if (!File.Exists(path))
            {
                return;
            }
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize(text, AgentJsonContext.Default.AgentScanResult);
            if (result is null)
            {
                return;
            }
            lock (_gate)
            {
                // A scan that finished meanwhile is newer than the cache.
                if (_current.ScannedAt != default)
                {
                    return;
                }
                _current = result;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken cache is simply ignored.
        }
    }

    public async Task<AgentScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref _scanning);
            Changed?.Invoke(this, EventArgs.Empty);
            try
            {
                var env = await _loginEnvironment.GetAsync(cancellationToken).ConfigureAwait(false);
                var path = GetPath(env);
                var definitions = _catalog.Definitions.ToList();
                var found = new AgentInstallation?[definitions.Count];

                await Parallel.ForEachAsync(
                    Enumerable.Range(0, definitions.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = cancellationToken },
                    async (i, ct) => found[i] = await ProbeAsync(definitions[i], env, path, ct).ConfigureAwait(false))
                    .ConfigureAwait(false);

                var result = new AgentScanResult
                {
                    ScannedAt = DateTimeOffset.UtcNow,
                    Installations = found.Where(f => f is not null).Select(f => f!).ToList()
                };
                lock (_gate)
                {
                    _current = result;
                }
                await WriteCacheAsync(result).ConfigureAwait(false);
                return result;
            }
            finally
            {
                Interlocked.Decrement(ref _scanning);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private static string? GetPath(IReadOnlyDictionary<string, string> env)
    {
        foreach (var (k, v) in env)
        {
            if (string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }
        return null;
    }

    private async Task<AgentInstallation?> ProbeAsync(
        AgentDefinition def,
        IReadOnlyDictionary<string, string> loginEnv,
        string? path,
        CancellationToken ct)
    {
        var env = new Dictionary<string, string>(loginEnv, StringComparer.Ordinal);
        foreach (var (k, v) in def.Detect.ProbeEnv)
        {
            env[k] = v;
        }

        foreach (var name in def.Detect.Executables)
        {
            IReadOnlyList<string> candidates;
            try
            {
                candidates = _locator.FindAll(name, path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                ProcessResult result;
                try
                {
                    result = await _runner.RunAsync(candidate, def.Detect.VersionArgs, env, null, ProbeTimeout, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    continue;
                }
                if (!result.Succeeded)
                {
                    continue;
                }
                var output = result.StandardOutput + "\n" + result.StandardError;
                if (!string.IsNullOrEmpty(def.Detect.Fingerprint)
                    && !output.Contains(def.Detect.Fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return new AgentInstallation { AgentId = def.Id, Path = candidate, Version = ExtractVersion(def, output) };
            }
        }
        return null;
    }

    private static string? ExtractVersion(AgentDefinition def, string output)
    {
        if (string.IsNullOrEmpty(def.Detect.VersionRegex))
        {
            return null;
        }
        try
        {
            var match = Regex.Match(output, def.Detect.VersionRegex, RegexOptions.Multiline, TimeSpan.FromSeconds(1));
            if (!match.Success)
            {
                return null;
            }
            return match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value;
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private async Task WriteCacheAsync(AgentScanResult result)
    {
        try
        {
            Directory.CreateDirectory(_paths.CacheDirectory);
            var path = Path.Combine(_paths.CacheDirectory, CacheFileName);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(result, AgentJsonContext.Default.AgentScanResult))
                .ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation only.
        }
    }
}
