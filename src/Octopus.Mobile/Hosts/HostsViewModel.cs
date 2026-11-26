using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Account;
using Octopus.Mobile.Bots;
using Octopus.Mobile.Common;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Updates;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Hosts;

/// <summary>
/// Scheme A root dashboard: four bottom tabs (Activity inbox, Bots, Computers, Me / Account center).
/// Aggregates cross-host waiting/working sessions into Activity and cross-host Bots with multi-chat session management into Bots.
/// </summary>
public sealed partial class HostsViewModel : ObservableObject, IDisposable
{
    public const int TabActivity = 0;
    public const int TabBots = 1;
    public const int TabHosts = 2;
    public const int TabAccount = 3;

    private readonly IRemoteWorkspace _workspace;
    private readonly IAccountSession _account;
    private readonly INavigator _navigator;
    private readonly Action<Uri> _openBrowser;
    private readonly List<RemoteHost> _watchedHosts = [];
    private readonly List<RemoteProject> _watchedProjects = [];
    private readonly List<RemoteSession> _watchedSessions = [];

    public HostsViewModel(IRemoteWorkspace workspace, IAccountSession account, INavigator navigator, Action<Uri>? openBrowser = null, MobileUpdater? updater = null)
    {
        Updater = updater ?? MobileUpdater.Unsupported;
        Updater.PropertyChanged += OnUpdaterChanged;
        _workspace = workspace;
        _account = account;
        _navigator = navigator;
        _openBrowser = openBrowser ?? (_ => { });
        IsSignedIn = account.IsSignedIn;
        Bots = new BotsTabViewModel(workspace, navigator);
        Bots.SummaryChanged += OnBotsSummaryChanged;
        MobileHost.Voice.Changed += OnVoiceChanged;
        workspace.Hosts.CollectionChanged += OnHostsChanged;
        workspace.OtherDevices.CollectionChanged += OnOtherDevicesChanged;
        RebuildAll();
        RebuildOtherDevices();
    }

    /// <summary>The app's updater: the "Me" tab's update row binds to it.</summary>
    public MobileUpdater Updater { get; }

    /// <summary>Cross-host Bots tab state (accordion session cards, 1-tap approvals, New Bot &amp; Link Import sheets).</summary>
    public BotsTabViewModel Bots { get; }

    /// <summary>Active bottom tab: 0 = Activity, 1 = Bots, 2 = Computers, 3 = Me.</summary>
    [ObservableProperty] public partial int SelectedTab { get; set; } = TabActivity;

    public bool IsActivityTab => SelectedTab == TabActivity;
    public bool IsBotsTab => SelectedTab == TabBots;
    public bool IsHostsTab => SelectedTab == TabHosts;
    public bool IsAccountTab => SelectedTab == TabAccount;

    /// <summary>Shows the dismissible update reminder banner on Activity, Bots &amp; Computers tabs; Me tab has its own full section.</summary>
    public bool ShowUpdateBanner => Updater.IsBannerVisible && !IsAccountTab;

    public string CurrentTabTitle => SelectedTab switch
    {
        TabBots => MobileLangResources.Get(MobileLangResourceKind.TabBots),
        TabHosts => MobileLangResources.Get(MobileLangResourceKind.TabHosts),
        TabAccount => MobileLangResources.Get(MobileLangResourceKind.TabMe),
        _ => MobileLangResources.Get(MobileLangResourceKind.TabActivity),
    };

    /// <summary>"Bots", with a pending count while any Bot work item or chat needs attention.</summary>
    public string BotsTabLabel => Bots.TotalNeedsCount > 0
        ? MobileLangResources.Format(MobileLangResourceKind.TabBotsBadge, Bots.TotalNeedsCount)
        : MobileLangResources.Get(MobileLangResourceKind.TabBots);

    /// <summary>"Me", with a dot while a newer version waits in the update row.</summary>
    public string MeTabLabel => Updater.HasNews
        ? MobileLangResources.Get(MobileLangResourceKind.TabMeUpdate)
        : MobileLangResources.Get(MobileLangResourceKind.TabMe);

    public string ActivityTabLabel => AttentionSessions.Count > 0
        ? MobileLangResources.Format(MobileLangResourceKind.TabActivityBadge, AttentionSessions.Count)
        : MobileLangResources.Get(MobileLangResourceKind.TabActivity);

    public ObservableCollection<ActivitySessionRowViewModel> AttentionSessions { get; } = [];
    public ObservableCollection<ActivitySessionRowViewModel> WorkingSessions { get; } = [];
    public ObservableCollection<HostRowViewModel> Online { get; } = [];
    public ObservableCollection<HostRowViewModel> Offline { get; } = [];
    public ObservableCollection<HostRowViewModel> Unpaired { get; } = [];
    /// <summary>The account's phones, this one too: named under "Other devices", never opened.</summary>
    public ObservableCollection<AccountDeviceRowViewModel> OtherDevices { get; } = [];

    [ObservableProperty] public partial bool HasAttention { get; private set; }
    [ObservableProperty] public partial bool HasWorking { get; private set; }
    [ObservableProperty] public partial bool HasOnline { get; private set; }
    [ObservableProperty] public partial bool HasOffline { get; private set; }
    [ObservableProperty] public partial bool HasUnpaired { get; private set; }
    [ObservableProperty] public partial bool IsEmpty { get; private set; }
    [ObservableProperty] public partial bool HasOtherDevices { get; private set; }
    /// <summary>"Other devices" is folded open; it starts folded.</summary>
    [ObservableProperty] public partial bool IsOtherDevicesOpen { get; set; }
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
        OnPropertyChanged(nameof(IsBotsTab));
        OnPropertyChanged(nameof(IsHostsTab));
        OnPropertyChanged(nameof(IsAccountTab));
        OnPropertyChanged(nameof(CurrentTabTitle));
        OnPropertyChanged(nameof(ShowUpdateBanner));
    }

    partial void OnIsEmptyChanged(bool value)
    {
        OnPropertyChanged(nameof(HasHosts));
        OnPropertyChanged(nameof(IsActivityAllClear));
    }

    /// <summary>The header row of the folded section: its name and how many devices are in it.</summary>
    public string OtherDevicesTitle => MobileLangResources.Format(MobileLangResourceKind.OtherDevicesTitle, OtherDevices.Count);

    partial void OnHasAttentionChanged(bool value) => OnPropertyChanged(nameof(IsActivityAllClear));
    partial void OnHasWorkingChanged(bool value) => OnPropertyChanged(nameof(IsActivityAllClear));

    [RelayCommand]
    private void SelectTab(int tab) => SelectedTab = tab;

    [RelayCommand]
    private void SelectAccountTab() => SelectedTab = TabAccount;

    [RelayCommand]
    private void ToggleOtherDevices() => IsOtherDevicesOpen = !IsOtherDevicesOpen;

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

    /// <summary>Some voice engine could work on this phone: the "Voice input" row shows.</summary>
    public bool IsVoiceSupported => MobileHost.Voice.IsSupported;

    /// <summary>The engine voice input uses now, under the row.</summary>
    public string VoiceEngineName => Voice.VoiceService.NameOf(MobileHost.Voice.Settings.Engine);

    [RelayCommand]
    private void OpenVoiceSettings() => _navigator.ShowVoiceSettings();

    private void OnVoiceChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(VoiceEngineName)));

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
        _workspace.Hosts.CollectionChanged -= OnHostsChanged;
        _workspace.OtherDevices.CollectionChanged -= OnOtherDevicesChanged;
        Updater.PropertyChanged -= OnUpdaterChanged;
        Bots.SummaryChanged -= OnBotsSummaryChanged;
        MobileHost.Voice.Changed -= OnVoiceChanged;
        Bots.Dispose();
        UnwatchAll();
        ClearRows();
    }

    private void OnBotsSummaryChanged() => OnPropertyChanged(nameof(BotsTabLabel));

    private void OnHostsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildAll();

    private void OnOtherDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildOtherDevices();

    private void RebuildOtherDevices()
    {
        OtherDevices.Clear();
        foreach (var device in _workspace.OtherDevices) OtherDevices.Add(new AccountDeviceRowViewModel(device));
        HasOtherDevices = OtherDevices.Count > 0;
        OnPropertyChanged(nameof(OtherDevicesTitle));
    }

    private void OnUpdaterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MobileUpdater.HasNews) or nameof(MobileUpdater.State)) OnPropertyChanged(nameof(MeTabLabel));
        if (e.PropertyName is nameof(MobileUpdater.IsBannerVisible) or nameof(MobileUpdater.State)) OnPropertyChanged(nameof(ShowUpdateBanner));
    }

    private void OnActivityStructureChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UnwatchAll();
        WatchAll();
        RebuildActivity();
    }

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemoteHost.Link) or nameof(RemoteHost.IsPaired) or nameof(RemoteHost.AcceptsPassword)) RebuildAll();
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
            host.Projects.CollectionChanged += OnActivityStructureChanged;
            _watchedHosts.Add(host);
            foreach (var project in host.Projects)
            {
                project.Sessions.CollectionChanged += OnActivityStructureChanged;
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
            host.Projects.CollectionChanged -= OnActivityStructureChanged;
        }
        foreach (var project in _watchedProjects) project.Sessions.CollectionChanged -= OnActivityStructureChanged;
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
            // Offline, paired or not, is only named: nothing is tried until the account says it is back.
            if (!host.IsOnline) Offline.Add(new HostRowViewModel(host, _ => { }));
            else if (host.IsPaired) Online.Add(new HostRowViewModel(host, _navigator.ShowHost));
            else Unpaired.Add(new HostRowViewModel(host, GetIn));
        }
        HasOnline = Online.Count > 0;
        HasOffline = Offline.Count > 0;
        HasUnpaired = Unpaired.Count > 0;
        IsEmpty = _workspace.Hosts.Count == 0;
        OnPropertyChanged(nameof(AccountHostSummary));
    }

    // A computer of the account that is not paired: its access password when it takes one, else the six digits.
    private void GetIn(RemoteHost host)
    {
        if (host.AcceptsPassword) _navigator.ShowAccessPassword(host);
        else _navigator.ShowPairing(host);
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
        ? $"{Session.Project.Name} ({branch}) · {Session.Project.Host.Name}"
        : $"{Session.Project.Name} · {Session.Project.Host.Name}";
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
    /// <summary>Offline: drawn faded, and nothing happens on a tap.</summary>
    public bool IsDimmed => !Host.IsOnline;
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
                return Host.LastSeen is { } seen ? LastSeenText(seen, DateTimeOffset.Now) : MobileLangResources.Get(MobileLangResourceKind.LinkOffline);
            }
            var link = LinkLabel;
            return Host.IsPaired ? link + " · " + string.Format(MobileLangResources.Get(MobileLangResourceKind.ProjectCount), Host.Projects.Count)
                : Host.AcceptsPassword ? link + " · " + MobileLangResources.Get(MobileLangResourceKind.HostAccessByPassword)
                : link;
        }
    }

    private int Count(SessionActivity activity) => _sessions.Count(session => session.Activity == activity);

    public bool IsWaiting => Host.IsPaired && Count(SessionActivity.Waiting) > 0;
    public bool IsError => Host.IsPaired && !IsWaiting && Count(SessionActivity.Error) > 0;
    public bool IsWorking => Host.IsPaired && !IsWaiting && !IsError && Count(SessionActivity.Working) > 0;
    /// <summary>The pill says "Pair" for a host that is not paired (a lock and "Password" when it takes its access password), otherwise the most urgent count.</summary>
    public bool IsPairAction => !Host.IsPaired && Host.IsOnline;
    /// <summary>Not paired, online, and it takes its access password: the pill has a lock.</summary>
    public bool IsPasswordAction => IsPairAction && Host.AcceptsPassword;
    public bool HasPill => IsWaiting || IsError || IsWorking || IsPairAction;
    public bool IsIdle => Host.IsOnline && Host.IsPaired && !HasPill;

    public string PillText =>
        IsPasswordAction ? MobileLangResources.Get(MobileLangResourceKind.AccessPasswordAction)
        : IsPairAction ? MobileLangResources.Get(MobileLangResourceKind.Pair)
        : IsWaiting ? string.Format(MobileLangResources.Get(MobileLangResourceKind.WaitingCount), Count(SessionActivity.Waiting))
        : IsError ? string.Format(MobileLangResources.Get(MobileLangResourceKind.ErrorCount), Count(SessionActivity.Error))
        : IsWorking ? string.Format(MobileLangResources.Get(MobileLangResourceKind.WorkingCount), Count(SessionActivity.Working))
        : "";

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open() => _open(Host);

    /// <summary>"Online just now", "Online 5 minutes ago", "Online 3 days ago"; the date when it is longer ago than a month.</summary>
    public static string LastSeenText(DateTimeOffset seen, DateTimeOffset now)
    {
        var ago = now - seen;
        if (ago < TimeSpan.FromMinutes(1)) return MobileLangResources.Get(MobileLangResourceKind.LastSeenJustNow);
        if (ago < TimeSpan.FromHours(1)) return MobileLangResources.Format(MobileLangResourceKind.LastSeenMinutes, (int)ago.TotalMinutes);
        if (ago < TimeSpan.FromDays(1)) return MobileLangResources.Format(MobileLangResourceKind.LastSeenHours, (int)ago.TotalHours);
        if (ago < TimeSpan.FromDays(31)) return MobileLangResources.Format(MobileLangResourceKind.LastSeenDays, (int)ago.TotalDays);
        return MobileLangResources.Format(MobileLangResourceKind.LastSeen, seen.LocalDateTime.ToString("yyyy-MM-dd"));
    }

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

/// <summary>A phone of the account under "Other devices": its name, "This phone" for itself, and when it was online. Not opened.</summary>
public sealed class AccountDeviceRowViewModel(AccountDevice device)
{
    public AccountDevice Device { get; } = device;
    public string Name => Device.Name;
    public bool IsCurrent => Device.IsCurrent;
    public string Summary
    {
        get
        {
            var state = Device.IsCurrent ? MobileLangResources.Get(MobileLangResourceKind.ThisPhoneSummary)
                : Device.IsOnline ? MobileLangResources.Get(MobileLangResourceKind.HostsOnline)
                : Device.LastSeen is { } seen ? HostRowViewModel.LastSeenText(seen, DateTimeOffset.Now)
                : MobileLangResources.Get(MobileLangResourceKind.LinkOffline);
            return Device.Platform is { Length: > 0 } platform ? platform + " · " + state : state;
        }
    }
}
