using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Activity;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Remote;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.Core.Projects;
using Octopus.Remote.Client;
using Octopus.Ssh;
using Octopus.Storage;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.Localization;

namespace Octopus.App.Sidebar;

/// <summary>
/// Manages the Option B sidebar device switcher: the circular device badge at the top of the project rail,
/// the side flyout listing local / paired remote Octopus hosts / SSH hosts, cross-device activity aggregation,
/// and per-device custom icon/color/picture editing.
/// </summary>
public sealed partial class DeviceSwitcherViewModel : ObservableObject
{
    private readonly IProjectService _projects;
    private readonly IDialogService _dialogs;
    private readonly ISettingsNavigator _navigator;
    private readonly IProjectIconStore? _icons;
    private readonly IItemActivityService? _activity;
    private readonly RemoteClientRuntime? _remoteRuntime;
    private readonly RemoteProjects? _remoteProjects;
    private readonly ISshHostCatalog? _sshCatalog;
    private readonly ISettingsStore? _settings;

    public DeviceSwitcherViewModel(
        IProjectService projects,
        IDialogService dialogs,
        ISettingsNavigator navigator,
        IProjectIconStore? icons = null,
        IItemActivityService? activity = null,
        RemoteClientRuntime? remoteRuntime = null,
        RemoteProjects? remoteProjects = null,
        ISshHostCatalog? sshCatalog = null,
        ISettingsStore? settings = null)
    {
        _projects = projects;
        _dialogs = dialogs;
        _navigator = navigator;
        _icons = icons;
        _activity = activity;
        _remoteRuntime = remoteRuntime;
        _remoteProjects = remoteProjects;
        _sshCatalog = sshCatalog;
        _settings = settings;

        _projects.Changed += OnProjectsChanged;
        _activity?.Changed += (_, _) => UiContext.Run(RefreshActivity);
        if (_remoteRuntime is not null)
        {
            _remoteRuntime.StateChanged += _ => UiContext.Run(Rebuild);
            _remoteRuntime.Hosts.Changed += () => UiContext.Run(Rebuild);
        }
        _sshCatalog?.Changed += (_, _) => UiContext.Run(Rebuild);
        _settings?.Changed += (_, _) => UiContext.Run(Rebuild);
        ArcLanguage.Changed += (_, _) => UiContext.Run(Rebuild);

        Rebuild();
    }

    public ObservableCollection<DeviceEntryViewModel> LocalDevices { get; } = [];
    public ObservableCollection<DeviceEntryViewModel> RemoteDevices { get; } = [];
    public ObservableCollection<DeviceEntryViewModel> SshDevices { get; } = [];
    public ObservableCollection<DeviceEntryViewModel> AllDevices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentBadgeTip), nameof(ShowDeviceHeader), nameof(ShowOfflineBanner), nameof(OfflineBannerTitle))]
    public partial DeviceEntryViewModel CurrentDevice { get; private set; } = null!;

    [ObservableProperty] public partial bool HasRemoteDevices { get; private set; }
    [ObservableProperty] public partial bool HasSshDevices { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDeviceHeader))]
    public partial bool HasMultipleDevices { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherWaiting), nameof(OtherWaitingBadgeText), nameof(ShowOtherWorking), nameof(CurrentBadgeTip))]
    public partial int OtherWaitingCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOtherWorking))]
    public partial bool HasOtherWorking { get; private set; }

    public bool HasOtherWaiting => OtherWaitingCount > 0;
    public string OtherWaitingBadgeText => OtherWaitingCount > 9 ? "9+" : OtherWaitingCount.ToString();
    public bool ShowOtherWorking => !HasOtherWaiting && HasOtherWorking;

    /// <summary>True when the panel should display the compact device echo chip at the top.</summary>
    public bool ShowDeviceHeader => HasMultipleDevices || (CurrentDevice is not null && !CurrentDevice.IsLocal);

    public bool ShowOfflineBanner => CurrentDevice is { IsRemote: true } d && (d.IsOffline || d.IsConnecting);

    public string OfflineBannerTitle => CurrentDevice is { IsConnecting: true }
        ? AppLangResources.Get(AppLangResourceKind.DeviceOfflineConnecting)
        : AppLangResources.Get(AppLangResourceKind.DeviceOfflineTitle);

    public string CurrentBadgeTip
    {
        get
        {
            if (CurrentDevice is null) return "";
            var tip = AppLangResources.Format(
                AppLangResourceKind.DeviceSwitchTip,
                CurrentDevice.Name,
                CurrentDevice.StatusText,
                CurrentDevice.ProjectCountText);
            if (HasOtherWaiting)
            {
                tip += "\n" + AppLangResources.Format(AppLangResourceKind.DeviceOtherWaitingTip, OtherWaitingCount);
            }
            return tip;
        }
    }

    /// <summary>Raised when the switcher popover should close (after picking a device, editing an icon, or navigating to settings).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised whenever the device list or active device state is rebuilt, so dependents (e.g. panel header) can refresh.</summary>
    public event EventHandler? Changed;

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (e.Kind is ProjectChangeKind.Projects or ProjectChangeKind.Device or ProjectChangeKind.CurrentProject or ProjectChangeKind.Items)
        {
            Rebuild();
        }
    }

    public void Rebuild()
    {
        var activeKey = _projects.ActiveDeviceKey;
        LocalDevices.Clear();
        RemoteDevices.Clear();
        SshDevices.Clear();
        AllDevices.Clear();

        // 1. This computer (Local)
        var localEntry = BuildLocalDevice(activeKey);
        LocalDevices.Add(localEntry);
        AllDevices.Add(localEntry);

        var (activeKind, activeTarget) = DeviceKeys.Parse(activeKey);

        // 2. Paired remote Octopus hosts + any remote host referenced by existing projects (or currently selected)
        var remoteIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_remoteRuntime is not null)
        {
            foreach (var peer in _remoteRuntime.Hosts.Peers.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                remoteIds[peer.Id] = peer.Name;
            }
        }
        foreach (var project in _projects.Projects)
        {
            if (project.RemoteHost is { Length: > 0 } hostId && !remoteIds.ContainsKey(hostId))
            {
                remoteIds[hostId] = _remoteRuntime?.HostName(hostId) ?? hostId;
            }
        }
        if (activeKind == ProjectLocationKind.Remote && activeTarget is { Length: > 0 } activeRemote && !remoteIds.ContainsKey(activeRemote))
        {
            remoteIds[activeRemote] = _remoteRuntime?.HostName(activeRemote) ?? activeRemote;
        }
        foreach (var (hostId, name) in remoteIds.OrderBy(kv => kv.Value, StringComparer.CurrentCultureIgnoreCase))
        {
            var entry = BuildRemoteDevice(hostId, name, activeKey);
            RemoteDevices.Add(entry);
            AllDevices.Add(entry);
        }

        // 3. SSH hosts (configured in ~/.ssh/config, referenced by existing projects, or currently selected)
        var sshHosts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_sshCatalog is not null)
        {
            foreach (var host in _sshCatalog.Current.Hosts)
            {
                var addr = FormatSshSubtitle(host);
                sshHosts[host.Alias] = addr;
            }
        }
        foreach (var project in _projects.Projects)
        {
            if (project.SshHost is { Length: > 0 } alias && !sshHosts.ContainsKey(alias))
            {
                sshHosts[alias] = alias;
            }
        }
        if (activeKind == ProjectLocationKind.Ssh && activeTarget is { Length: > 0 } activeSsh && !sshHosts.ContainsKey(activeSsh))
        {
            sshHosts[activeSsh] = activeSsh;
        }
        foreach (var (alias, subtitle) in sshHosts.OrderBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var entry = BuildSshDevice(alias, subtitle, activeKey);
            SshDevices.Add(entry);
            AllDevices.Add(entry);
        }

        HasRemoteDevices = RemoteDevices.Count > 0;
        HasSshDevices = SshDevices.Count > 0;
        HasMultipleDevices = AllDevices.Count > 1;
        CurrentDevice = AllDevices.FirstOrDefault(d => string.Equals(d.Key, activeKey, StringComparison.Ordinal)) ?? localEntry;
        UpdateCrossDeviceActivity();
        OnPropertyChanged(nameof(CurrentBadgeTip));
        OnPropertyChanged(nameof(ShowDeviceHeader));
        OnPropertyChanged(nameof(ShowOfflineBanner));
        OnPropertyChanged(nameof(OfflineBannerTitle));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshActivity()
    {
        foreach (var device in AllDevices)
        {
            var (waiting, working, done) = ActivityOf(device.Key);
            device.WaitingCount = waiting;
            device.IsWorking = working;
            device.IsDone = done;
        }
        UpdateCrossDeviceActivity();
    }

    private void UpdateCrossDeviceActivity()
    {
        var activeKey = _projects.ActiveDeviceKey;
        var otherWaiting = 0;
        var otherWorking = false;
        foreach (var device in AllDevices)
        {
            if (string.Equals(device.Key, activeKey, StringComparison.Ordinal)) continue;
            otherWaiting += device.WaitingCount;
            if (device.IsWorking) otherWorking = true;
        }
        OtherWaitingCount = otherWaiting;
        HasOtherWorking = otherWorking;
    }

    private DeviceEntryViewModel BuildLocalDevice(string activeKey)
    {
        var name = string.IsNullOrWhiteSpace(_settings?.Current.Remote.DeviceName)
            ? Environment.MachineName
            : _settings!.Current.Remote.DeviceName!.Trim();
        var key = DeviceKeys.Local;
        var custom = _projects.GetDeviceAppearance(key);
        var (waiting, working, done) = ActivityOf(key);
        var count = _projects.Projects.Count(p => DeviceKeys.Matches(p, key));
        return new DeviceEntryViewModel(
            key: key,
            kind: ProjectLocationKind.Local,
            targetId: null,
            name: name,
            subtitle: AppLangResources.Get(AppLangResourceKind.DeviceSectionLocal),
            statusText: AppLangResources.Get(AppLangResourceKind.DeviceStatusLocal),
            status: DeviceConnectionStatus.Local,
            isCurrent: string.Equals(activeKey, key, StringComparison.Ordinal),
            customAppearance: custom,
            defaultColor: ProjectService.Palette[0],
            defaultIcon: LucideIconKind.Laptop,
            image: _icons?.Load(custom?.Image),
            projectCount: count,
            waitingCount: waiting,
            isWorking: working,
            isDone: done,
            select: SelectDevice,
            changeIcon: ChangeDeviceIconAsync);
    }

    private DeviceEntryViewModel BuildRemoteDevice(string hostId, string name, string activeKey)
    {
        var key = DeviceKeys.Remote(hostId);
        var state = _remoteRuntime?.StateOf(hostId) ?? RemoteHostState.Disconnected;
        var throughRelay = _remoteRuntime?.IsThroughRelay(hostId) ?? false;
        var peer = _remoteRuntime?.Hosts.Peers.FirstOrDefault(p => p.Id == hostId);
        var address = throughRelay
            ? AppLangResources.Get(AppLangResourceKind.SettingsRemoteHostsViaRelay)
            : peer?.Addresses.FirstOrDefault() ?? hostId;

        var (status, statusText) = state switch
        {
            RemoteHostState.Connected => (
                DeviceConnectionStatus.Online,
                AppLangResources.Get(throughRelay ? AppLangResourceKind.DeviceStatusRelay : AppLangResourceKind.DeviceStatusLan)),
            RemoteHostState.Connecting => (
                DeviceConnectionStatus.Connecting,
                AppLangResources.Get(AppLangResourceKind.DeviceStatusConnecting)),
            _ => (
                DeviceConnectionStatus.Offline,
                AppLangResources.Get(AppLangResourceKind.DeviceStatusOffline)),
        };

        var custom = _projects.GetDeviceAppearance(key);
        var (waiting, working, done) = ActivityOf(key);
        var count = _projects.Projects.Count(p => DeviceKeys.Matches(p, key));
        var colorIndex = Math.Abs(StringComparer.Ordinal.GetHashCode(hostId)) % (ProjectService.Palette.Length - 1) + 1;
        return new DeviceEntryViewModel(
            key: key,
            kind: ProjectLocationKind.Remote,
            targetId: hostId,
            name: name,
            subtitle: address,
            statusText: statusText,
            status: status,
            isCurrent: string.Equals(activeKey, key, StringComparison.Ordinal),
            customAppearance: custom,
            defaultColor: ProjectService.Palette[colorIndex],
            defaultIcon: LucideIconKind.Monitor,
            image: _icons?.Load(custom?.Image),
            projectCount: count,
            waitingCount: waiting,
            isWorking: working,
            isDone: done,
            select: SelectDevice,
            changeIcon: ChangeDeviceIconAsync,
            reconnect: ReconnectDeviceAsync);
    }

    private DeviceEntryViewModel BuildSshDevice(string alias, string subtitle, string activeKey)
    {
        var key = DeviceKeys.Ssh(alias);
        var custom = _projects.GetDeviceAppearance(key);
        var (waiting, working, done) = ActivityOf(key);
        var count = _projects.Projects.Count(p => DeviceKeys.Matches(p, key));
        return new DeviceEntryViewModel(
            key: key,
            kind: ProjectLocationKind.Ssh,
            targetId: alias,
            name: alias,
            subtitle: subtitle,
            statusText: AppLangResources.Get(AppLangResourceKind.DeviceStatusSsh),
            status: DeviceConnectionStatus.Ssh,
            isCurrent: string.Equals(activeKey, key, StringComparison.Ordinal),
            customAppearance: custom,
            defaultColor: ProjectService.Palette[7],
            defaultIcon: LucideIconKind.Terminal,
            image: _icons?.Load(custom?.Image),
            projectCount: count,
            waitingCount: waiting,
            isWorking: working,
            isDone: done,
            select: SelectDevice,
            changeIcon: ChangeDeviceIconAsync);
    }

    private (int Waiting, bool Working, bool Done) ActivityOf(string deviceKey)
    {
        if (_activity is null) return (0, false, false);
        var waiting = 0;
        var working = false;
        var done = false;
        foreach (var project in _projects.Projects)
        {
            if (!DeviceKeys.Matches(project, deviceKey)) continue;
            foreach (var item in project.Items)
            {
                switch (_activity.Of(item.Id))
                {
                    case ItemActivity.Waiting: waiting++; break;
                    case ItemActivity.Working: working = true; break;
                    case ItemActivity.Done: done = true; break;
                }
            }
        }
        return (waiting, working, done);
    }

    private static string FormatSshSubtitle(SshHost host)
    {
        var user = host.User is null ? "" : host.User + "@";
        var port = host.Port is { } p ? ":" + p : "";
        var address = user + host.HostName + port;
        return string.IsNullOrWhiteSpace(address) || address == host.Alias ? "SSH" : address;
    }

    public void SelectDevice(DeviceEntryViewModel device)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _projects.SetActiveDevice(device.Key);
        if (device is { IsRemote: true, IsOffline: true, TargetId: { } hostId } && _remoteProjects is not null)
        {
            _ = _remoteProjects.ConnectQuietlyAsync(hostId);
        }
    }

    public async Task ChangeDeviceIconAsync(DeviceEntryViewModel device)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        var current = _projects.GetDeviceAppearance(device.Key);
        var choice = await _dialogs.AskDeviceAppearanceAsync(
            device.Key,
            device.Name,
            device.Subtitle,
            current,
            device.DefaultColor,
            device.DefaultIcon.ToString());
        if (choice is null)
        {
            return;
        }
        var previousImage = current?.Image;
        _projects.SetDeviceAppearance(device.Key, choice.Color, choice.Icon, choice.Image);
        if (previousImage is not null && previousImage != choice.Image)
        {
            _icons?.Delete(previousImage);
        }
    }

    public Task ReconnectDeviceAsync(DeviceEntryViewModel device)
    {
        if (device is not { IsRemote: true, TargetId: { } hostId }) return Task.CompletedTask;
        if (_remoteProjects is not null)
        {
            return _remoteProjects.ConnectQuietlyAsync(hostId);
        }
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task ChangeCurrentIconAsync() => CurrentDevice is { } current ? ChangeDeviceIconAsync(current) : Task.CompletedTask;

    [RelayCommand]
    private Task ReconnectCurrentAsync() => CurrentDevice is { } current ? ReconnectDeviceAsync(current) : Task.CompletedTask;

    [RelayCommand]
    private void OpenRemoteHostsSettings()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _navigator.OpenSettings("remote-hosts");
    }

    [RelayCommand]
    private void OpenSshSettings()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _navigator.OpenSettings("ssh");
    }
}
