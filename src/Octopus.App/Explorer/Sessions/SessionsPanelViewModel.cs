using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents;
using Octopus.Agents.Sessions;
using Octopus.Agents.Signals;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Storage;

namespace Octopus.App.Explorer.Sessions;

/// <summary>
/// The sessions tab of the right panel (design/ui/会话管理方案.html, layout A): first the installed agents with how many
/// sessions each saved for this folder, then one agent's sessions grouped by account. A session is continued in a new
/// tab (in its own account) or moved to another agent.
/// </summary>
public sealed partial class SessionsPanelViewModel : ObservableObject
{
    /// <summary>Signal channels whose payload names the session a tab runs.</summary>
    private static readonly string[] SessionChannels = ["statusline", "session-start", "prompt-submit", "stop"];

    /// <summary>The handoff's "only the last turns" option.</summary>
    public const int RecentTurns = 20;

    private readonly IAgentSessionService _sessions;
    private readonly IAgentCatalog _catalog;
    private readonly IAgentScanner _scanner;
    private readonly ISettingsStore _settings;
    private readonly IProjectService _projects;
    private readonly IItemOpener _opener;
    private readonly ISessionMigrationDialog _migration;
    private readonly IDialogService _dialogs;
    private readonly IAppPaths _paths;
    private readonly ISignalHub? _hub;
    private readonly ILoginEnvironment? _login;
    private readonly TimeProvider _time;
    private string? _projectPath;
    private int _version;
    /// <summary>The project the list was read for; a tab shown again for the same one keeps what it has.</summary>
    private string? _loadedFor;
    private bool _isActive;
    private string? _openAgentId;

    public SessionsPanelViewModel(
        IAgentSessionService sessions,
        IAgentCatalog catalog,
        IAgentScanner scanner,
        ISettingsStore settings,
        IProjectService projects,
        IItemOpener opener,
        ISessionMigrationDialog migration,
        IDialogService dialogs,
        IAppPaths paths,
        ISignalHub? hub = null,
        ILoginEnvironment? login = null,
        TimeProvider? time = null)
    {
        _sessions = sessions;
        _catalog = catalog;
        _scanner = scanner;
        _settings = settings;
        _projects = projects;
        _opener = opener;
        _migration = migration;
        _dialogs = dialogs;
        _paths = paths;
        _hub = hub;
        _login = login;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Agents with sessions in this folder, the most recently used first.</summary>
    public ObservableCollection<SessionAgentRowViewModel> Agents { get; } = [];

    /// <summary>Installed agents with no session here, or whose sessions Octopus cannot read.</summary>
    public ObservableCollection<SessionAgentRowViewModel> QuietAgents { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotInstalled))]
    public partial int NotInstalledCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledCountText))]
    public partial int InstalledCount { get; private set; }

    public string InstalledCountText => AppLangResources.Format(AppLangResourceKind.SessionsInstalledCount, InstalledCount);
    public bool HasQuietAgents => QuietAgents.Count > 0;
    public bool HasNotInstalled => NotInstalledCount > 0;
    public bool HasNoAgents => !IsLoading && Agents.Count == 0;

    [ObservableProperty] public partial bool IsQuietExpanded { get; set; }
    [ObservableProperty] public partial bool IsNotInstalledExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoAgents), nameof(IsListEmpty))]
    public partial bool IsLoading { get; private set; }

    /// <summary>The agent whose sessions show; null on the first page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAgentsPage), nameof(IsSessionsPage), nameof(HeaderSummary))]
    public partial SessionAgentRowViewModel? Current { get; private set; }

    public bool IsAgentsPage => Current is null;
    public bool IsSessionsPage => Current is not null;

    public string HeaderSummary => Current is null
        ? ""
        : AppLangResources.Format(AppLangResourceKind.SessionsHeaderSummary, Current.Group.Count, Current.Group.Accounts.Count);

    /// <summary>The account groups of <see cref="Current"/>, filtered by <see cref="Filter"/>.</summary>
    public ObservableCollection<SessionAccountGroupViewModel> Groups { get; } = [];

    public bool IsListEmpty => IsSessionsPage && !IsLoading && Groups.Count == 0;

    [ObservableProperty] public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value) => BuildGroups();

    /// <summary>Whether the tab shows: a hidden tab reads nothing.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            if (value && _loadedFor != _projectPath) _ = LoadAsync();
        }
    }

    /// <summary>The project folder (null: none, or one on an SSH host, whose sessions are not on this computer).</summary>
    public void SetProject(string? path)
    {
        if (_projectPath == path) return;
        _projectPath = path;
        Current = null;
        _openAgentId = null;
        Filter = "";
        if (_isActive) _ = LoadAsync();
        else Clear();
    }

    [RelayCommand]
    public Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private void Back()
    {
        Current = null;
        _openAgentId = null;
        Filter = "";
        Groups.Clear();
        OnPropertyChanged(nameof(IsListEmpty));
    }

    [RelayCommand]
    private void ToggleQuiet() => IsQuietExpanded = !IsQuietExpanded;

    [RelayCommand]
    private void ToggleNotInstalled() => IsNotInstalledExpanded = !IsNotInstalledExpanded;

    private void Open(SessionAgentRowViewModel row)
    {
        if (!row.HasSessions) return;
        _openAgentId = row.AgentId;
        Filter = "";
        Current = row;
        BuildGroups();
    }

    private void Clear()
    {
        _loadedFor = null;
        Agents.Clear();
        QuietAgents.Clear();
        Groups.Clear();
        NotifyLists();
    }

    /// <summary>Reads every installed agent's sessions for the folder; a newer read makes an older one's result void.</summary>
    public async Task LoadAsync()
    {
        var version = ++_version;
        var path = _projectPath;
        if (path is null)
        {
            Clear();
            return;
        }
        IsLoading = true;
        try
        {
            var installed = Installed();
            var groups = await Task.WhenAll(installed.Select(async agent =>
            {
                try
                {
                    return await _sessions.ListAsync(agent, path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return new AgentSessionGroup(agent, _sessions.CanRead(agent.Id), []);
                }
            }));
            if (version != _version) return;
            var now = _time.GetUtcNow();
            Agents.Clear();
            QuietAgents.Clear();
            foreach (var group in groups.Where(g => g.Count > 0).OrderByDescending(g => g.LatestAt))
            {
                Agents.Add(new SessionAgentRowViewModel(group, now, Open));
            }
            foreach (var group in groups.Where(g => g.Count == 0).OrderBy(g => g.IsSupported ? 0 : 1).ThenBy(g => g.Agent.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                QuietAgents.Add(new SessionAgentRowViewModel(group, now, Open));
            }
            InstalledCount = installed.Count;
            NotInstalledCount = Math.Max(0, _catalog.Definitions.Count - installed.Count);
            _loadedFor = path;
            // The open agent stays open across a refresh; it falls back to the first page when it has no sessions left.
            Current = _openAgentId is null ? null : Agents.FirstOrDefault(a => a.AgentId == _openAgentId);
            if (Current is null) _openAgentId = null;
            BuildGroups();
        }
        finally
        {
            if (version == _version) IsLoading = false;
            NotifyLists();
        }
    }

    private void NotifyLists()
    {
        OnPropertyChanged(nameof(HasQuietAgents));
        OnPropertyChanged(nameof(HasNoAgents));
        OnPropertyChanged(nameof(IsListEmpty));
    }

    /// <summary>The installed, enabled agents that run on this computer, in the catalog's order.</summary>
    private List<AgentDefinition> Installed()
    {
        var agents = _settings.Current.Agents;
        return [.. _scanner.Current.Installations
            .Select(i => _catalog.Find(i.AgentId))
            .OfType<AgentDefinition>()
            .Where(d => agents.ProfileOf(d.Id).Enabled)
            .DistinctBy(d => d.Id)];
    }

    private void BuildGroups()
    {
        Groups.Clear();
        if (Current is { } agent)
        {
            var now = _time.GetUtcNow();
            var live = LiveSessions();
            var filter = Filter.Trim();
            foreach (var account in agent.Group.Accounts)
            {
                var rows = account.Sessions
                    .Where(s => filter.Length == 0 || s.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase) || s.SessionId.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                    .Select(s => new SessionRowViewModel(s, now, live.GetValueOrDefault(s.SessionId), ContinueAsync, MigrateAsync))
                    .ToList();
                if (rows.Count > 0) Groups.Add(new SessionAccountGroupViewModel(account, rows));
            }
        }
        OnPropertyChanged(nameof(IsListEmpty));
    }

    /// <summary>
    /// Session id to tab: what the project's agent tabs were opened with, and what their signals last said they run
    /// (a tab started fresh only knows its session once the agent reports it).
    /// </summary>
    private Dictionary<string, string> LiveSessions()
    {
        var live = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_projects.Current is not { } project) return live;
        foreach (var item in project.Items.Where(i => i.Kind == ProjectItemKind.Agent && i.AgentId is not null))
        {
            if (_hub is not null)
            {
                var latest = SessionChannels
                    .Select(c => _hub.LastSignal(item.AgentId!, c, item.Id))
                    .OfType<AgentSignal>()
                    .OrderByDescending(s => s.ReceivedAt)
                    .Select(s => SessionIdOf(s.Payload))
                    .FirstOrDefault(id => id is not null);
                if (latest is not null)
                {
                    live[latest] = item.Id;
                    continue;
                }
            }
            if (item.Launch?.SessionId is { Length: > 0 } chosen) live.TryAdd(chosen, item.Id);
        }
        return live;
    }

    internal static string? SessionIdOf(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.TrimStart()[0] != '{') return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            foreach (var key in (string[])["session_id", "sessionId", "thread_id"])
            {
                if (document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } id) return id;
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task ContinueAsync(SessionRowViewModel row)
    {
        if (row.LiveItemId is { } itemId && _projects.FindItem(itemId) is not null)
        {
            _projects.ActivateItem(itemId);
            return;
        }
        if (_catalog.Find(row.Info.AgentId) is not { } agent) return;
        var env = _sessions.AccountEnvironment(agent, row.Info.AccountDirectory, await InheritedAsync(agent));
        var item = _opener.OpenAgent(agent.Id, new AgentLaunchOptions { SessionId = row.Info.SessionId, Env = env });
        if (item is not null) row.LiveItemId = item.Id;
    }

    private async Task MigrateAsync(SessionRowViewModel row)
    {
        if (_projectPath is not { } project || _catalog.Find(row.Info.AgentId) is not { } source) return;
        var targets = new List<SessionMigrationTarget>();
        foreach (var agent in Installed())
        {
            var accounts = await _sessions.AccountsAsync(agent);
            var same = agent.Id == source.Id;
            if (same)
            {
                // To the same agent only makes sense into another of its accounts.
                accounts = [.. accounts.Where(a => !SamePath(a.Directory, row.Info.AccountDirectory))];
                if (accounts.Count == 0) continue;
            }
            var kinds = _sessions.MigrationKinds(source.Id, agent);
            if (same) kinds = [.. kinds.Where(k => k == SessionMigrationKind.Native)];
            targets.Add(new SessionMigrationTarget(agent, kinds, accounts));
        }
        var sourceAccount = Current?.Group.Accounts.FirstOrDefault(a => SamePath(a.Directory, row.Info.AccountDirectory))?.Label ?? row.Info.AccountDirectory;
        var form = new SessionMigrationFormViewModel(row.Info, source.Name, sourceAccount, targets);
        if (await _migration.AskAsync(form) is not { } choice) return;

        try
        {
            var result = await _sessions.MigrateAsync(row.Info, choice.Kind, new SessionMigrationRequest(
                choice.Target.Id,
                choice.AccountDirectory,
                project,
                choice.IncludeToolOutput,
                choice.RecentOnly ? RecentTurns : null,
                Path.Combine(_paths.DataDirectory, "handoffs"),
                _settings.Current.Appearance.Language is { Length: > 0 } language ? language : "zh-CN"));
            var env = choice.AccountDirectory is { } directory ? _sessions.AccountEnvironment(choice.Target, directory, await InheritedAsync(choice.Target)) : null;
            var launch = result.Kind == SessionMigrationKind.Native
                ? new AgentLaunchOptions { SessionId = result.ResumeSessionId, Env = env }
                : new AgentLaunchOptions { Prompt = result.Prompt, Env = env };
            _opener.OpenAgent(choice.Target.Id, launch);
            // A copy into another account adds a session there.
            if (result.Kind == SessionMigrationKind.Native) _ = LoadAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            await _dialogs.ConfirmAsync(
                AppLangResources.Get(AppLangResourceKind.MigrateFailed),
                e.Message,
                AppLangResources.Get(AppLangResourceKind.MigrateFailedClose));
        }
    }

    /// <summary>The variables the agent would get without a chosen account: the login environment, then its profile.</summary>
    private async Task<IReadOnlyDictionary<string, string>> InheritedAsync(AgentDefinition agent)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_login is not null)
        {
            try
            {
                foreach (var (key, value) in await _login.GetAsync()) environment[key] = value;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException)
            {
                // Without the login environment the process's own stands in.
            }
        }
        if (environment.Count == 0)
        {
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string key && entry.Value is string value) environment[key] = value;
            }
        }
        foreach (var (key, value) in _settings.Current.Agents.ProfileOf(agent.Id).Env) environment[key] = value;
        return environment;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}
