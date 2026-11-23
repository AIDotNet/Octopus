using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Hosts;

/// <summary>
/// The first page: the account's computers and the ones this phone paired with itself (only those, without an account),
/// online ones first, each with what needs the person there.
/// </summary>
public sealed partial class HostsViewModel : ObservableObject
{
    private readonly IRemoteWorkspace _workspace;
    private readonly IAccountSession _account;
    private readonly INavigator _navigator;

    public HostsViewModel(IRemoteWorkspace workspace, IAccountSession account, INavigator navigator)
    {
        _workspace = workspace;
        _account = account;
        _navigator = navigator;
        workspace.Hosts.CollectionChanged += OnHostsChanged;
        foreach (var host in workspace.Hosts) host.PropertyChanged += OnHostChanged;
        Rebuild();
    }

    public ObservableCollection<HostRowViewModel> Online { get; } = [];
    public ObservableCollection<HostRowViewModel> Offline { get; } = [];
    public ObservableCollection<HostRowViewModel> Unpaired { get; } = [];

    [ObservableProperty] public partial bool HasOnline { get; private set; }
    [ObservableProperty] public partial bool HasOffline { get; private set; }
    [ObservableProperty] public partial bool HasUnpaired { get; private set; }
    [ObservableProperty] public partial bool IsEmpty { get; private set; }
    /// <summary>The account sheet (its name and "Sign out") is showing.</summary>
    [ObservableProperty] public partial bool IsAccountOpen { get; set; }
    /// <summary>The sheet of a phone used without an account ("Sign in", "Leave local network mode") is showing.</summary>
    [ObservableProperty] public partial bool IsLocalOpen { get; set; }

    /// <summary>Fixed for the page's life: signing in or out replaces the page.</summary>
    public bool IsSignedIn => _account.IsSignedIn;
    public bool IsSignedOut => !_account.IsSignedIn;
    public string AccountName => _account.IsSignedIn
        ? _account.Name ?? MobileLangResources.Get(MobileLangResourceKind.Account)
        : MobileLangResources.Get(MobileLangResourceKind.NotSignedIn);
    /// <summary>What an empty list says: nothing of the account has remote access on, or nothing was added yet.</summary>
    public string EmptyText => MobileLangResources.Get(_account.IsSignedIn ? MobileLangResourceKind.HostsEmpty : MobileLangResourceKind.HostsEmptyLocal);
    public bool HasHosts => !IsEmpty;

    partial void OnIsEmptyChanged(bool value) => OnPropertyChanged(nameof(HasHosts));

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

    private void OnHostsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (RemoteHost host in e.OldItems) host.PropertyChanged -= OnHostChanged;
        if (e.NewItems is not null) foreach (RemoteHost host in e.NewItems) host.PropertyChanged += OnHostChanged;
        Rebuild();
    }

    // Going online, offline or paired moves the host to another group.
    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemoteHost.Link) or nameof(RemoteHost.IsPaired)) Rebuild();
    }

    private void Rebuild()
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
    }
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
            var link = MobileLangResources.Get(Host.Link == HostLink.Lan ? MobileLangResourceKind.LinkLan : MobileLangResourceKind.LinkRelay);
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
