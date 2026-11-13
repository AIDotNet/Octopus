using System.Text.Json;
using System.Text.RegularExpressions;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Agents;

/// <summary>
/// Finds installed agents by running every candidate's version command. The candidates come from the login shell's
/// PATH, then from the directories agent installers usually write to (<see cref="AgentSearchPaths"/>) and from the
/// definition's own <c>detect.extraDirs</c>. See design/05 section 5.
/// </summary>
public sealed partial class AgentScanner : IAgentScanner
{
    private const string CacheFileName = "agent-scan.json";
    private const int MaxConcurrency = 8;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly IAgentCatalog _catalog;
    private readonly IExecutableLocator _locator;
    private readonly IProcessRunner _runner;
    private readonly ILoginEnvironment _loginEnvironment;
    private readonly IAppPaths _paths;
    private readonly Func<IReadOnlyList<string>> _searchDirectories;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _gate = new();
    private AgentScanResult _current = new();
    private int _scanning;

    public AgentScanner(
        IAgentCatalog catalog,
        IExecutableLocator locator,
        IProcessRunner runner,
        ILoginEnvironment loginEnvironment,
        IAppPaths paths,
        Func<IReadOnlyList<string>>? searchDirectories = null)
    {
        _searchDirectories = searchDirectories ?? AgentSearchPaths.ForThisMachine;
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
            // An agent that was uninstalled since the last run must not show as installed until the scan finishes.
            result.Installations = result.Installations.Where(i => File.Exists(i.Path)).ToList();
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
                var path = WithSearchDirectories(GetPath(env), _searchDirectories());
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

    /// <summary>PATH followed by the directories that are not on it yet.</summary>
    private static string? WithSearchDirectories(string? path, IReadOnlyList<string> directories)
    {
        if (directories.Count == 0)
        {
            return path;
        }
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var known = new HashSet<string>(
            (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => d.TrimEnd('/', '\\')),
            comparer);
        var extra = directories.Where(d => known.Add(d.TrimEnd('/', '\\'))).ToList();
        return extra.Count == 0 ? path : string.Join(Path.PathSeparator, new[] { path ?? "" }.Concat(extra).Where(p => p.Length > 0));
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

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var ownDirectories = def.Detect.ExtraDirs
            .Select(d => AgentSearchPaths.Expand(d, home, Environment.GetEnvironmentVariable))
            .Where(d => d.Length > 0 && Directory.Exists(d))
            .ToList();
        var searchPath = WithSearchDirectories(path, ownDirectories);
        var tried = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var name in def.Detect.Executables)
        {
            IReadOnlyList<string> candidates;
            try
            {
                candidates = _locator.FindAll(name, searchPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                // Two names of one program (qoder and qodercli, kilo and kilocode) are started once.
                if (!tried.Add(RealPath(candidate)))
                {
                    continue;
                }
                ProcessResult result;
                try
                {
                    result = await _runner.RunAsync(candidate, def.Detect.VersionArgs, WithRuntimeNextToIt(env, candidate, searchPath), null, ProbeTimeout, ct)
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
                var output = StripAnsi(result.StandardOutput + "\n" + result.StandardError);
                if (!MatchesFingerprint(def.Detect.Fingerprint, output))
                {
                    continue;
                }
                return new AgentInstallation { AgentId = def.Id, Path = candidate, Version = ExtractVersion(def, output) };
            }
        }
        return null;
    }

    /// <summary>
    /// The environment for one probe, with the full search path. A script that starts with <c>env node</c> finds the
    /// Node that sits in its own directory (nvm, fnm, volta), which the login shell's PATH may not list.
    /// </summary>
    private static Dictionary<string, string> WithRuntimeNextToIt(Dictionary<string, string> env, string candidate, string? searchPath)
    {
        var copy = new Dictionary<string, string>(env, StringComparer.Ordinal);
        var directory = Path.GetDirectoryName(candidate);
        var pathKey = copy.Keys.FirstOrDefault(k => string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(searchPath)) parts.Add(searchPath);
        if (!string.IsNullOrEmpty(directory)
            && !(searchPath ?? "").Split(Path.PathSeparator).Contains(directory, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            parts.Add(directory);
        }
        if (parts.Count > 0)
        {
            copy[pathKey] = string.Join(Path.PathSeparator, parts);
        }
        return copy;
    }

    /// <summary>The file a link leads to, so that a name and its alias count once.</summary>
    private static string RealPath(string candidate)
    {
        try
        {
            return File.ResolveLinkTarget(candidate, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return candidate;
        }
    }

    /// <summary>True without a fingerprint; otherwise when the output contains any <c>|</c> separated alternative.</summary>
    internal static bool MatchesFingerprint(string? fingerprint, string output)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return true;
        }
        return fingerprint
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(f => output.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    internal static string StripAnsi(string text) => text.Contains('\u001b') ? AnsiRegex().Replace(text, "") : text;

    [GeneratedRegex(@"\u001b\[[0-9;?]*[ -/]*[@-~]|\u001b\][^\u0007\u001b]*(?:\u0007|\u001b\\)")]
    private static partial Regex AnsiRegex();

    /// <summary>The first version-looking number, for a CLI whose output the definition's regex does not fit.</summary>
    [GeneratedRegex(@"\bv?(\d+\.\d+\.\d+(?:[-+][0-9A-Za-z][0-9A-Za-z.\-]*)?)")]
    private static partial Regex AnyVersionRegex();

    internal static string? ExtractVersion(AgentDefinition def, string output)
    {
        if (!string.IsNullOrEmpty(def.Detect.VersionRegex))
        {
            try
            {
                var match = Regex.Match(output, def.Detect.VersionRegex, RegexOptions.Multiline, TimeSpan.FromSeconds(1));
                if (match.Success)
                {
                    return match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                // A broken pattern in a user's definition falls through to the generic one.
            }
        }
        // A new release that words its output differently still shows a version.
        var any = AnyVersionRegex().Match(output);
        return any.Success ? any.Groups[1].Value : null;
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
