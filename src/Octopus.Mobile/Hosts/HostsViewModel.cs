using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Account;
using Octopus.Mobile.Common;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Hosts;

/// <summary>
/// Scheme A root dashboard: three bottom tabs (Activity inbox, Computers, Me / Account center).
/// Aggregates cross-host waiting/working sessions into the Activity tab while keeping full host grouping and account management.
/// </summary>
public sealed partial class HostsViewModel : ObservableObject, IDisposable
{
    public const int TabActivity = 0;
    public const int TabHosts = 1;
    public const int TabAccount = 2;

    private readonly IRemoteWorkspace _workspace;
    private readonly IAccountSession _account;
    private readonly INavigator _navigator;
    private readonly Action<Uri> _openBrowser;
    private readonly List<RemoteHost> _watchedHosts = [];
    private readonly List<RemoteProject> _watchedProjects = [];
    private readonly List<RemoteSession> _watchedSessions = [];

    public HostsViewModel(IRemoteWorkspace workspace, IAccountSession account, INavigator navigator, Action<Uri>? openBrowser = null)
    {
        _workspace = workspace;
        _account = account;
        _navigator = navigator;
        _openBrowser = openBrowser ?? (_ => { });
        IsSignedIn = account.IsSignedIn;
        workspace.Hosts.CollectionChanged += OnStructureChanged;
        RebuildAll();
    }

    /// <summary>Active bottom tab: 0 = Activity, 1 = Computers, 2 = Me.</summary>
    [ObservableProperty] public partial int SelectedTab { get; set; } = TabActivity;

    public bool IsActivityTab => SelectedTab == TabActivity;
    public bool IsHostsTab => SelectedTab == TabHosts;
    public bool IsAccountTab => SelectedTab == TabAccount;

    public string CurrentTabTitle => SelectedTab switch
    {
        TabHosts => MobileLangResources.Get(MobileLangResourceKind.TabHosts),
        TabAccount => MobileLangResources.Get(MobileLangResourceKind.TabMe),
        _ => MobileLangResources.Get(MobileLangResourceKind.TabActivity),
    };

    public string ActivityTabLabel => AttentionSessions.Count > 0
        ? MobileLangResources.Format(MobileLangResourceKind.TabActivityBadge, AttentionSessions.Count)
        : MobileLangResources.Get(MobileLangResourceKind.TabActivity);

    public ObservableCollection<ActivitySessionRowViewModel> AttentionSessions { get; } = [];
    public ObservableCollection<ActivitySessionRowViewModel> WorkingSessions { get; } = [];
    public ObservableCollection<HostRowViewModel> Online { get; } = [];
    public ObservableCollection<HostRowViewModel> Offline { get; } = [];
    public ObservableCollection<HostRowViewModel> Unpaired { get; } = [];

    [ObservableProperty] public partial bool HasAttention { get; private set; }
    [ObservableProperty] public partial bool HasWorking { get; private set; }
    [ObservableProperty] public partial bool HasOnline { get; private set; }
    [ObservableProperty] public partial bool HasOffline { get; private set; }
    [ObservableProperty] public partial bool HasUnpaired { get; private set; }
    [ObservableProperty] public partial bool IsEmpty { get; private set; }
    /// <summary>The account sheet (its name and "Sign out") is showing.</summary>
    [ObservableProperty] public partial bool IsAccountOpen { get; set; }
    /// <summary>The sheet of a phone used without an account ("Sign in", "Leave local network mode") is showing.</summary>
    [ObservableProperty] public partial bool IsLocalOpen { get; set; }

    public bool IsActivityAllClear => HasHosts && !HasAttention && !HasWorking;
    public bool HasHosts => !IsEmpty;
    public int AttentionCount => AttentionSessions.Count;

    /// <summary>Fixed for the page's life: signing in or out replaces the page.</summary>
    public bool IsSignedIn { get; }
    public bool IsSignedOut => !IsSignedIn;
    public string AccountName => IsSignedIn
        ? _account.Name ?? MobileLangResources.Get(MobileLangResourceKind.Account)
        : MobileLangResources.Get(MobileLangResourceKind.NotSignedIn);
    public string AccountInitial
    {
        get
        {
            var name = _account.Name?.Trim();
            return string.IsNullOrEmpty(name) ? "O" : char.ToUpperInvariant(name[0]).ToString();
        }
    }

    public string? ActiveServerUrl => !string.IsNullOrWhiteSpace(_account.Server) ? _account.Server : _account.OfficialServer;
    public string ServerHostDisplay => WelcomeViewModel.FormatHost(ActiveServerUrl) ?? "octoterm.si";
    public string AccountHostSummary => MobileLangResources.Format(MobileLangResourceKind.MeHostSummary, ServerHostDisplay, Online.Count);
    public string AccountStatusBadge => MobileLangResources.Get(IsSignedIn ? MobileLangResourceKind.MeStatusOnline : MobileLangResourceKind.MeStatusLocal);
    public string UpgradeBannerHint => WelcomeViewModel.FormatHost(_account.OfficialServer) is { Length: > 0 } host
        ? MobileLangResources.Format(MobileLangResourceKind.UpgradeBannerHint, host)
        : MobileLangResources.Get(MobileLangResourceKind.UpgradeBannerHintGeneric);
    public string SignInButtonText => WelcomeViewModel.FormatHost(_account.OfficialServer) is { Length: > 0 } host
        ? MobileLangResources.Format(MobileLangResourceKind.SignInWithOfficial, host)
        : MobileLangResources.Get(MobileLangResourceKind.SignInStart);

    public Uri? AccountWebUri
    {
        get
        {
            if (!IsSignedIn || string.IsNullOrWhiteSpace(ActiveServerUrl)) return null;
            var raw = ActiveServerUrl.Trim();
            if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
            return Uri.TryCreate(raw.TrimEnd('/') + "/account", UriKind.Absolute, out var uri) ? uri : null;
        }
    }

    public bool HasAccountWeb => AccountWebUri is not null;

    /// <summary>What an empty list says: nothing of the account has remote access on, or nothing was added yet.</summary>
    public string EmptyText => MobileLangResources.Get(IsSignedIn ? MobileLangResourceKind.HostsEmpty : MobileLangResourceKind.HostsEmptyLocal);

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsActivityTab));
        OnPropertyChanged(nameof(IsHostsTab));
        OnPropertyChanged(nameof(IsAccountTab));
        OnPropertyChanged(nameof(CurrentTabTitle));
    }

    partial void OnIsEmptyChanged(bool value)
    {
        OnPropertyChanged(nameof(HasHosts));
        OnPropertyChanged(nameof(IsActivityAllClear));
    }

    partial void OnHasAttentionChanged(bool value) => OnPropertyChanged(nameof(IsActivityAllClear));
    partial void OnHasWorkingChanged(bool value) => OnPropertyChanged(nameof(IsActivityAllClear));

    [RelayCommand]
    private void SelectTab(int tab) => SelectedTab = tab;

    [RelayCommand]
    private void SelectAccountTab() => SelectedTab = TabAccount;

    [RelayCommand]
    private void OpenAccount()
    {
        if (_account.IsSignedIn) IsAccountOpen = true;
        else IsLocalOpen = true;
    }

    /// <summary>A computer by its address or pairing link: also one the account does not know.</summary>
    [RelayCommand]
    private void AddHost() => _navigator.ShowAddHost();

    [RelayCommand]
    private void SignIn()
    {
        IsLocalOpen = false;
        _navigator.ShowSignIn();
    }

    [RelayCommand]
    private void OpenCustomServer() => _navigator.ShowCustomServer();

    [RelayCommand]
    private void OpenAccountWeb()
    {
        if (AccountWebUri is { } uri) _openBrowser(uri);
    }

    /// <summary>Back to the welcome page. The computers stay paired.</summary>
    [RelayCommand]
    private void LeaveLocal()
    {
        IsLocalOpen = false;
        _account.LeaveLocalOnly();
    }

    /// <summary>Reads the account's computers again; the page asks while it is showing.</summary>
    public Task RefreshAsync() => _workspace.RefreshAsync();

    [RelayCommand]
    private void SignOut()
    {
        IsAccountOpen = false;
        _account.SignOut();
    }

    public void Dispose()
    {
        _workspace.Hosts.CollectionChanged -= OnStructureChanged;
        UnwatchAll();
        ClearRows();
    }

    private void OnStructureChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildAll();

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemoteHost.Link) or nameof(RemoteHost.IsPaired)) RebuildAll();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteSession.Activity)) RebuildActivity();
    }

    private void RebuildAll()
    {
        UnwatchAll();
        WatchAll();
        RebuildHosts();
        RebuildActivity();
    }

    private void WatchAll()
    {
        foreach (var host in _workspace.Hosts)
        {
            host.PropertyChanged += OnHostChanged;
            host.Projects.CollectionChanged += OnStructureChanged;
            _watchedHosts.Add(host);
            foreach (var project in host.Projects)
            {
                project.Sessions.CollectionChanged += OnStructureChanged;
                _watchedProjects.Add(project);
                foreach (var session in project.Sessions)
                {
                    session.PropertyChanged += OnSessionChanged;
                    _watchedSessions.Add(session);
                }
            }
        }
    }

    private void UnwatchAll()
    {
        foreach (var host in _watchedHosts)
        {
            host.PropertyChanged -= OnHostChanged;
            host.Projects.CollectionChanged -= OnStructureChanged;
        }
        foreach (var project in _watchedProjects) project.Sessions.CollectionChanged -= OnStructureChanged;
        foreach (var session in _watchedSessions) session.PropertyChanged -= OnSessionChanged;
        _watchedHosts.Clear();
        _watchedProjects.Clear();
        _watchedSessions.Clear();
    }

    private void RebuildHosts()
    {
        foreach (var row in Online.Concat(Offline).Concat(Unpaired)) row.Dispose();
        Online.Clear();
        Offline.Clear();
        Unpaired.Clear();
        foreach (var host in _workspace.Hosts)
        {
            if (!host.IsPaired) Unpaired.Add(new HostRowViewModel(host, _navigator.ShowPairing));
            else if (host.IsOnline) Online.Add(new HostRowViewModel(host, _navigator.ShowHost));
            else Offline.Add(new HostRowViewModel(host, _ => { }));
        }
        HasOnline = Online.Count > 0;
        HasOffline = Offline.Count > 0;
        HasUnpaired = Unpaired.Count > 0;
        IsEmpty = _workspace.Hosts.Count == 0;
        OnPropertyChanged(nameof(AccountHostSummary));
    }

    private void RebuildActivity()
    {
        foreach (var row in AttentionSessions.Concat(WorkingSessions)) row.Dispose();
        AttentionSessions.Clear();
        WorkingSessions.Clear();
        foreach (var host in _workspace.Hosts)
        {
            if (!host.IsPaired || !host.IsOnline) continue;
            foreach (var project in host.Projects)
            {
                foreach (var session in project.Sessions)
                {
                    if (session.Activity is SessionActivity.Waiting or SessionActivity.Error)
                    {
                        AttentionSessions.Add(new ActivitySessionRowViewModel(session, _navigator.ShowSession));
                    }
                    else if (session.Activity == SessionActivity.Working)
                    {
                        WorkingSessions.Add(new ActivitySessionRowViewModel(session, _navigator.ShowSession));
                    }
                }
            }
        }
        HasAttention = AttentionSessions.Count > 0;
        HasWorking = WorkingSessions.Count > 0;
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(ActivityTabLabel));
        OnPropertyChanged(nameof(IsActivityAllClear));
    }

    private void ClearRows()
    {
        foreach (var row in Online.Concat(Offline).Concat(Unpaired)) row.Dispose();
        foreach (var row in AttentionSessions.Concat(WorkingSessions)) row.Dispose();
        Online.Clear();
        Offline.Clear();
        Unpaired.Clear();
        AttentionSessions.Clear();
        WorkingSessions.Clear();
    }
}

/// <summary>One cross-host session row in the Scheme A Activity inbox: names the host, project/branch, preview and status.</summary>
public sealed partial class ActivitySessionRowViewModel : ObservableObject, IDisposable
{
    private readonly Action<RemoteSession> _open;

    public ActivitySessionRowViewModel(RemoteSession session, Action<RemoteSession> open)
    {
        Session = session;
        _open = open;
        var agent = session.AgentId is null ? null : session.Project.Host.Agents.FirstOrDefault(a => a.Id == session.AgentId);
        IsAgent = session.Kind == SessionKind.Agent;
        Initial = agent?.Initial ?? (IsAgent ? "A" : "");
        Tint = agent is not null && Color.TryParse(agent.Color, out var color) ? new SolidColorBrush(color) : null;
        session.PropertyChanged += OnSessionChanged;
    }

    public RemoteSession Session { get; }
    public bool IsAgent { get; }
    public bool IsTerminal => !IsAgent;
    public string Initial { get; }
    public IBrush? Tint { get; }
    public string Title => Session.Title;
    public string ContextLine => Session.WorkspaceBranch is { Length: > 0 } branch
        ? $"{Session.Project.Host.Name} · {Session.Project.Name} ({branch})"
        : $"{Session.Project.Host.Name} · {Session.Project.Name}";
    public string? Preview => Session.Preview;
    public bool HasPreview => !string.IsNullOrEmpty(Session.Preview);
    public string? WorkspaceTaskKey => Session.WorkspaceTaskKey;
    public bool HasWorkspaceTask => !string.IsNullOrEmpty(Session.WorkspaceTaskKey);
    public bool IsWaiting => Session.Activity == SessionActivity.Waiting;
    public bool IsWorking => Session.Activity == SessionActivity.Working;
    public bool IsError => Session.Activity == SessionActivity.Error;
    public bool HasStatus => Session.Activity != SessionActivity.Idle;
    public string StatusText => SessionRowViewModel.StatusOf(Session.Activity);

    [RelayCommand]
    private void Open() => _open(Session);

    public void Dispose() => Session.PropertyChanged -= OnSessionChanged;

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(string.Empty);
}

/// <summary>One computer in the list: how it is reached, and how many of its sessions wait, failed or work.</summary>
public sealed partial class HostRowViewModel : ObservableObject, IDisposable
{
    private readonly Action<RemoteHost> _open;
    private readonly List<RemoteProject> _projects = [];
    private readonly List<RemoteSession> _sessions = [];

    public HostRowViewModel(RemoteHost host, Action<RemoteHost> open)
    {
        Host = host;
        _open = open;
        host.PropertyChanged += OnChanged;
        host.Projects.CollectionChanged += OnStructureChanged;
        Watch();
    }

    public RemoteHost Host { get; }
    public string Name => Host.Name;
    public bool IsOnline => Host.IsOnline;
    public bool IsPaired => Host.IsPaired;
    public bool IsLan => Host.Link == HostLink.Lan;
    public string LinkLabel => MobileLangResources.Get(Host.Link == HostLink.Lan ? MobileLangResourceKind.LinkLan : MobileLangResourceKind.LinkRelay);
    /// <summary>A paired, online host opens; an unpaired one starts pairing; an offline one does nothing.</summary>
    public bool CanOpen => Host.IsOnline;
    public bool ShowChevron => Host.IsOnline && Host.IsPaired;

    public string Summary
    {
        get
        {
            if (!Host.IsOnline)
            {
                return Host.LastSeen is { } seen
                    ? string.Format(MobileLangResources.Get(MobileLangResourceKind.LastSeen), seen.LocalDateTime.ToString("MM-dd HH:mm"))
                    : MobileLangResources.Get(MobileLangResourceKind.LinkOffline);
            }
            var link = LinkLabel;
            return Host.IsPaired
                ? link + " · " + string.Format(MobileLangResources.Get(MobileLangResourceKind.ProjectCount), Host.Projects.Count)
                : link;
        }
    }

    private int Count(SessionActivity activity) => _sessions.Count(session => session.Activity == activity);

    public bool IsWaiting => Host.IsPaired && Count(SessionActivity.Waiting) > 0;
    public bool IsError => Host.IsPaired && !IsWaiting && Count(SessionActivity.Error) > 0;
    public bool IsWorking => Host.IsPaired && !IsWaiting && !IsError && Count(SessionActivity.Working) > 0;
    /// <summary>The pill says "Pair" for a host that is not paired, otherwise the most urgent count.</summary>
    public bool IsPairAction => !Host.IsPaired && Host.IsOnline;
    public bool HasPill => IsWaiting || IsError || IsWorking || IsPairAction;
    public bool IsIdle => Host.IsOnline && Host.IsPaired && !HasPill;

    public string PillText =>
        IsPairAction ? MobileLangResources.Get(MobileLangResourceKind.Pair)
        : IsWaiting ? string.Format(MobileLangResources.Get(MobileLangResourceKind.WaitingCount), Count(SessionActivity.Waiting))
        : IsError ? string.Format(MobileLangResources.Get(MobileLangResourceKind.ErrorCount), Count(SessionActivity.Error))
        : IsWorking ? string.Format(MobileLangResources.Get(MobileLangResourceKind.WorkingCount), Count(SessionActivity.Working))
        : "";

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open() => _open(Host);

    public void Dispose()
    {
        Host.PropertyChanged -= OnChanged;
        Host.Projects.CollectionChanged -= OnStructureChanged;
        Unwatch();
    }

    private void Watch()
    {
        foreach (var project in Host.Projects)
        {
            project.Sessions.CollectionChanged += OnStructureChanged;
            _projects.Add(project);
            foreach (var session in project.Sessions)
            {
                session.PropertyChanged += OnChanged;
                _sessions.Add(session);
            }
        }
    }

    private void Unwatch()
    {
        foreach (var project in _projects) project.Sessions.CollectionChanged -= OnStructureChanged;
        foreach (var session in _sessions) session.PropertyChanged -= OnChanged;
        _projects.Clear();
        _sessions.Clear();
    }

    private void OnStructureChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Unwatch();
        Watch();
        OnPropertyChanged(string.Empty);
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(string.Empty);
        OpenCommand.NotifyCanExecuteChanged();
    }
}
