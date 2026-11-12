using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Platform;

namespace Octopus.Agents.Signals;

/// <summary>Finds every account of an agent on this machine, not only the ones that already sent a signal.</summary>
public interface IAgentAccountDiscovery
{
    /// <summary>
    /// The accounts of the agent: its default directory, the one the login environment names, the directories the
    /// hub or an earlier run has seen and, for Claude, the configuration folders found on disk. Each is registered
    /// on the hub, so probes and signals can name it. The scan is remembered for a few minutes.
    /// </summary>
    Task<IReadOnlyList<AgentAccount>> DiscoverAsync(string agentId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The accounts a user has, found without waiting for them to report (design/plugins/12 §3). Directories are kept in
/// <c>agent-accounts.json</c> so an account seen once (a session Octopus started with its own <c>CODEX_HOME</c>) is
/// still queried after a restart; a directory that no longer exists is dropped. Only Claude's folders are searched
/// for on disk: <c>.claude.json</c> marks one, while <c>auth.json</c> and <c>config.toml</c> are shared by Codex and
/// Grok, so a stray folder could be mistaken for the wrong agent.
/// </summary>
public sealed class AgentAccountDiscovery : IAgentAccountDiscovery
{
    /// <summary>A scan walks the home directory's hidden folders: not worth repeating on every query.</summary>
    public static readonly TimeSpan ScanEvery = TimeSpan.FromMinutes(5);

    private const string FileName = "agent-accounts.json";

    private readonly ISignalHub _hub;
    private readonly IAgentCatalog _catalog;
    private readonly ILoginEnvironment _login;
    private readonly string _file;
    private readonly string _home;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _scanned = new(StringComparer.Ordinal);
    private Dictionary<string, HashSet<string>>? _remembered;

    public AgentAccountDiscovery(ISignalHub hub, IAgentCatalog catalog, ILoginEnvironment login, IAppPaths paths, TimeProvider? time = null, string? home = null)
    {
        _hub = hub;
        _catalog = catalog;
        _login = login;
        _file = Path.Combine(paths.DataDirectory, FileName);
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<AgentAccount>> DiscoverAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var definition = _catalog.Find(agentId);
        if (definition?.Signals?.Account is not { Env.Length: > 0 }) return _hub.Accounts(agentId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (_scanned.TryGetValue(definition.Id, out var last) && now - last < ScanEvery) return _hub.Accounts(definition.Id);

            var environment = await _login.GetAsync(cancellationToken).ConfigureAwait(false);
            var remembered = Remembered();
            var candidates = new List<string>();
            void Add(string? directory)
            {
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) candidates.Add(directory);
            }

            Add(SignalAccounts.ResolveDirectory(definition, null, _home));
            Add(SignalAccounts.ResolveDirectory(definition, environment.GetValueOrDefault(definition.Signals.Account.Env), _home));
            foreach (var directory in remembered.GetValueOrDefault(definition.Id) ?? []) Add(directory);
            foreach (var account in _hub.Accounts(definition.Id)) Add(account.Directory);
            if (definition.Id == "claude")
            {
                var variable = environment.GetValueOrDefault(definition.Signals.Account.Env);
                var found = await Task.Run(() => ClaudeConfigDirectories.Find(_home, variable), cancellationToken).ConfigureAwait(false);
                foreach (var directory in found) Add(directory);
            }

            foreach (var directory in candidates) _hub.RegisterAccount(definition.Id, directory);
            _scanned[definition.Id] = now;

            var accounts = _hub.Accounts(definition.Id);
            Remember(definition.Id, accounts.Select(a => a.Directory));
            return accounts;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<string, HashSet<string>> Remembered()
    {
        if (_remembered is not null) return _remembered;
        _remembered = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_file) && JsonNode.Parse(File.ReadAllText(_file)) is JsonObject root)
            {
                foreach (var (agent, list) in root)
                {
                    if (list is not JsonArray array) continue;
                    _remembered[agent] = [.. array.Select(n => n?.GetValue<string>()).OfType<string>()];
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // An unreadable file is the same as none: the next scan writes it again.
        }
        return _remembered;
    }

    private void Remember(string agentId, IEnumerable<string> directories)
    {
        var known = Remembered();
        var set = known.TryGetValue(agentId, out var existing) ? existing : (known[agentId] = []);
        var grew = false;
        foreach (var directory in directories) grew |= set.Add(directory);
        if (!grew) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var root = new JsonObject();
            foreach (var (agent, list) in known) root[agent] = new JsonArray([.. list.Order(StringComparer.Ordinal).Select(d => (JsonNode)JsonValue.Create(d)!)]);
            var temp = $"{_file}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, root.ToJsonString());
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Remembering is a convenience: the directories are found again by the next scan.
        }
    }
}
