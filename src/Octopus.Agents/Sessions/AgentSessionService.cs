using System.Text.Json;
using Octopus.Agents.Signals;
using Octopus.Core.Agents;

namespace Octopus.Agents.Sessions;

/// <summary>One account of an agent and its sessions in a project.</summary>
/// <param name="Directory">The account's folder (what the agent's account variable names).</param>
/// <param name="Label">How the account is shown: the signed-in identity when the folder records one, else the folder's last segments.</param>
/// <param name="IsDefault">The folder the agent uses when its variable is unset.</param>
public sealed record AgentSessionAccount(string Directory, string Label, bool IsDefault, IReadOnlyList<AgentSessionInfo> Sessions);

/// <summary>An agent's sessions in a project, grouped by account.</summary>
/// <param name="IsSupported">False when Octopus cannot read this agent's sessions yet: the list then says so instead of showing none.</param>
public sealed record AgentSessionGroup(AgentDefinition Agent, bool IsSupported, IReadOnlyList<AgentSessionAccount> Accounts)
{
    public int Count => Accounts.Sum(a => a.Sessions.Count);

    public DateTimeOffset? LatestAt => Accounts.SelectMany(a => a.Sessions).Select(s => (DateTimeOffset?)s.UpdatedAt).Max();
}

/// <summary>What the session list and the migration dialog need: sessions per agent and account, and the ways to move one.</summary>
public interface IAgentSessionService
{
    /// <summary>Whether Octopus can read the agent's sessions.</summary>
    bool CanRead(string agentId);

    /// <summary>The accounts the agent has on this machine (with no sessions listed), the default first.</summary>
    Task<IReadOnlyList<AgentSessionAccount>> AccountsAsync(AgentDefinition agent, CancellationToken cancellationToken = default);

    /// <summary>The sessions of one agent in a project, every account it has on this machine.</summary>
    Task<AgentSessionGroup> ListAsync(AgentDefinition agent, string projectPath, CancellationToken cancellationToken = default);

    Task<SessionTranscript> ReadAsync(AgentSessionInfo session, CancellationToken cancellationToken = default);

    /// <summary>The ways a session of <paramref name="sourceAgentId"/> can reach <paramref name="target"/>, native first.</summary>
    IReadOnlyList<SessionMigrationKind> MigrationKinds(string sourceAgentId, AgentDefinition target);

    Task<SessionMigrationResult> MigrateAsync(AgentSessionInfo session, SessionMigrationKind kind, SessionMigrationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The variables a tab needs to run in the session's account: the agent's account variable set to its folder, or
    /// null when the folder is the agent's default and nothing else would pick another one.
    /// </summary>
    /// <param name="inherited">The variables the agent would otherwise get (login environment and profile).</param>
    Dictionary<string, string>? AccountEnvironment(AgentDefinition agent, string accountDirectory, IReadOnlyDictionary<string, string>? inherited);
}

public sealed class AgentSessionService : IAgentSessionService
{
    private readonly IAgentAccountDiscovery _accounts;
    private readonly Dictionary<string, ISessionSource> _sources;
    private readonly IReadOnlyList<ISessionConverter> _converters;
    private readonly string _home;

    public AgentSessionService(IAgentAccountDiscovery accounts, IEnumerable<ISessionSource> sources, IEnumerable<ISessionConverter> converters, string? home = null)
    {
        _accounts = accounts;
        _sources = sources.ToDictionary(s => s.AgentId, StringComparer.Ordinal);
        // Native first: it keeps the whole history, the handoff is what is left when nothing can.
        _converters = [.. converters.OrderBy(c => c.Kind == SessionMigrationKind.Native ? 0 : 1)];
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public bool CanRead(string agentId) => _sources.ContainsKey(agentId);

    public async Task<IReadOnlyList<AgentSessionAccount>> AccountsAsync(AgentDefinition agent, CancellationToken cancellationToken = default)
    {
        var defaultDirectory = SignalAccounts.ResolveDirectory(agent, null, _home);
        var directories = await DirectoriesAsync(agent, defaultDirectory, cancellationToken).ConfigureAwait(false);
        return [.. directories
            .Select(d => new AgentSessionAccount(d, Label(agent, d, defaultDirectory), PathKey.Comparer.Equals(d, defaultDirectory), []))
            .OrderBy(a => a.IsDefault ? 0 : 1)
            .ThenBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase)];
    }

    private async Task<List<string>> DirectoriesAsync(AgentDefinition agent, string defaultDirectory, CancellationToken cancellationToken)
    {
        if (agent.Signals?.Account is not { Env.Length: > 0 }) return [];
        return (await _accounts.DiscoverAsync(agent.Id, cancellationToken).ConfigureAwait(false))
            .Select(a => a.Directory)
            .Prepend(defaultDirectory)
            .Where(d => d.Length > 0 && Directory.Exists(d))
            .Distinct(PathKey.Comparer)
            .ToList();
    }

    public async Task<AgentSessionGroup> ListAsync(AgentDefinition agent, string projectPath, CancellationToken cancellationToken = default)
    {
        if (!_sources.TryGetValue(agent.Id, out var source)) return new AgentSessionGroup(agent, false, []);
        var defaultDirectory = SignalAccounts.ResolveDirectory(agent, null, _home);
        var directories = await DirectoriesAsync(agent, defaultDirectory, cancellationToken).ConfigureAwait(false);
        var lists = await Task.WhenAll(directories.Select(async directory =>
        {
            try
            {
                return await source.ListAsync(directory, projectPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return (IReadOnlyList<AgentSessionInfo>)[];
            }
        })).ConfigureAwait(false);
        var accounts = directories
            .Select((directory, i) => new AgentSessionAccount(directory, Label(agent, directory, defaultDirectory), PathKey.Comparer.Equals(directory, defaultDirectory), lists[i]))
            .Where(a => a.Sessions.Count > 0)
            .OrderByDescending(a => a.Sessions.Max(s => s.UpdatedAt))
            .ToList();
        return new AgentSessionGroup(agent, true, accounts);
    }

    public Task<SessionTranscript> ReadAsync(AgentSessionInfo session, CancellationToken cancellationToken = default) =>
        _sources.TryGetValue(session.AgentId, out var source)
            ? source.ReadAsync(session, cancellationToken)
            : throw new InvalidOperationException($"No session reader for '{session.AgentId}'.");

    public IReadOnlyList<SessionMigrationKind> MigrationKinds(string sourceAgentId, AgentDefinition target)
    {
        var kinds = new List<SessionMigrationKind>();
        foreach (var converter in _converters)
        {
            if (!converter.CanConvert(sourceAgentId, target.Id) || kinds.Contains(converter.Kind)) continue;
            // A native session is useless to an agent that cannot be told which one to resume; a handoff needs a prompt.
            if (converter.Kind == SessionMigrationKind.Native && target.Capabilities.Resume?.ById is not { Count: > 0 }) continue;
            if (converter.Kind == SessionMigrationKind.Handoff && target.Capabilities.Prompt is null) continue;
            kinds.Add(converter.Kind);
        }
        return kinds;
    }

    public async Task<SessionMigrationResult> MigrateAsync(AgentSessionInfo session, SessionMigrationKind kind, SessionMigrationRequest request, CancellationToken cancellationToken = default)
    {
        var converter = _converters.FirstOrDefault(c => c.Kind == kind && c.CanConvert(session.AgentId, request.TargetAgentId))
            ?? throw new InvalidOperationException($"No {kind} converter from '{session.AgentId}' to '{request.TargetAgentId}'.");
        var transcript = await ReadAsync(session, cancellationToken).ConfigureAwait(false);
        return await converter.ConvertAsync(transcript, request, cancellationToken).ConfigureAwait(false);
    }

    public Dictionary<string, string>? AccountEnvironment(AgentDefinition agent, string accountDirectory, IReadOnlyDictionary<string, string>? inherited)
    {
        if (agent.Signals?.Account is not { Env.Length: > 0 } spec || accountDirectory.Length == 0) return null;
        var current = SignalAccounts.ResolveDirectory(agent, inherited?.GetValueOrDefault(spec.Env), _home);
        // Claude reads ~/.claude.json only while CLAUDE_CONFIG_DIR is unset: the variable is set only when the tab would
        // otherwise run in another account.
        if (PathKey.Comparer.Equals(current, accountDirectory)) return null;
        return new Dictionary<string, string>(StringComparer.Ordinal) { [spec.Env] = accountDirectory };
    }

    private string Label(AgentDefinition agent, string directory, string defaultDirectory)
    {
        if (agent.Id == "claude" && ClaudeIdentity(directory, PathKey.Comparer.Equals(directory, defaultDirectory)) is { } identity) return identity;
        // "accounts/<n>/config" says more as "accounts/<n>".
        var trimmed = Path.GetFileName(directory.TrimEnd('/', '\\')) is "config" ? Path.GetDirectoryName(directory.TrimEnd('/', '\\')) ?? directory : directory;
        return SignalAccounts.DefaultLabel(trimmed);
    }

    /// <summary>The signed-in email (or organisation) a Claude configuration folder records; null when it has none.</summary>
    private string? ClaudeIdentity(string directory, bool isDefault)
    {
        var file = isDefault ? Path.Combine(_home, ".claude.json") : Path.Combine(directory, ".claude.json");
        try
        {
            if (!File.Exists(file)) return null;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("oauthAccount", out var account) || account.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in (string[])["emailAddress", "organizationName", "displayName"])
            {
                if (account.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text) return text;
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>Folder paths compared as the file system does: ignoring a trailing separator, and case on macOS and Windows.</summary>
internal static class PathKey
{
    public static readonly IEqualityComparer<string> Comparer = new FolderComparer();

    private sealed class FolderComparer : IEqualityComparer<string>
    {
        private static readonly StringComparison Mode = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        private static string Trim(string path) => path.Length > 1 ? path.TrimEnd('/', '\\') : path;

        public bool Equals(string? x, string? y) => x is null || y is null ? x == y : string.Equals(Trim(x), Trim(y), Mode);

        public int GetHashCode(string obj) => string.GetHashCode(Trim(obj), Mode);
    }
}
