using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Localization;
using Octopus.App.Remote;
using Octopus.App.Services;
using Octopus.Remote.Client;
using Octopus.Remote.Security;

namespace Octopus.App.Settings.Remote;

/// <summary>A paired host in the list.</summary>
public sealed record RemoteHostRow(string Id, string Name, string Address, string StateText, bool IsConnected, bool IsConnecting, string? Error, string ProjectsText)
{
    /// <summary>The connection can be asked for: the host is neither connected nor on its way.</summary>
    public bool CanConnect => !IsConnected && !IsConnecting;

    /// <summary>The button while there is a connection to end (or one on its way to stop).</summary>
    public string StopText => IsConnecting
        ? AppLangResources.Get(AppLangResourceKind.SettingsRemoteHostsCancel)
        : AppLangResources.Get(AppLangResourceKind.SettingsRemoteHostsDisconnect);
}

/// <summary>
/// The "Remote hosts" page (design/12-远程操作.md §7.2): the computers this one is paired with, and pairing with
/// another one by its address (comparing six digits) or its pairing link.
/// </summary>
public sealed partial class RemoteHostsSettingsViewModel : ObservableObject, IDisposable
{
    private readonly RemoteClientRuntime _runtime;
    private readonly IProjectService _projects;
    private CancellationTokenSource? _pairing;

    public RemoteHostsSettingsViewModel(RemoteClientRuntime runtime, IProjectService projects)
    {
        _runtime = runtime;
        _projects = projects;
        runtime.StateChanged += OnChanged;
        runtime.Hosts.Changed += OnHostsChanged;
        projects.Changed += OnProjectsChanged;
        Refresh();
    }

    public ObservableCollection<RemoteHostRow> Hosts { get; } = [];
    [ObservableProperty] public partial bool HasHosts { get; private set; }

    /// <summary>What the person typed: an address, or a pairing link.</summary>
    [ObservableProperty] public partial string Target { get; set; } = "";
    [ObservableProperty] public partial bool IsPairing { get; private set; }
    /// <summary>The six digits to compare with the host's, while the host waits for its person; null otherwise.</summary>
    [ObservableProperty] public partial string? Sas { get; private set; }
    [ObservableProperty] public partial string? ErrorText { get; private set; }

    public bool HasSas => !string.IsNullOrEmpty(Sas);
    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public bool IsConnecting => IsPairing && !HasSas;

    /// <summary>The pairing in progress, for tests to wait on.</summary>
    internal Task Pairing { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private Task PairAsync() => Pairing = PairCoreAsync();

    [RelayCommand]
    private void CancelPairing() => _pairing?.Cancel();

    /// <summary>Connects a paired host on the person's word; a failure is shown on its row and retried in the background.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ConnectAsync(RemoteHostRow? row)
    {
        if (row is null) return;
        try { await _runtime.ConnectAsync(row.Id); }
        catch (Exception e) when (e is IOException or HandshakeException or Octopus.Remote.Protocol.RemoteException
                                      or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException
                                      or ObjectDisposedException)
        {
            // The row says why, and the retry loop keeps trying.
        }
        Refresh();
    }

    /// <summary>Ends a connection (or stops one being made); something on the host connects it again when needed.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DisconnectAsync(RemoteHostRow? row)
    {
        if (row is null) return;
        await _runtime.DisconnectAsync(row.Id);
        Refresh();
    }

    [RelayCommand]
    private async Task RemoveAsync(RemoteHostRow? row)
    {
        if (row is null) return;
        await _runtime.RemoveHostAsync(row.Id);
        Refresh();
    }

    public void Dispose()
    {
        _runtime.StateChanged -= OnChanged;
        _runtime.Hosts.Changed -= OnHostsChanged;
        _projects.Changed -= OnProjectsChanged;
        _pairing?.Cancel();
    }

    private async Task PairCoreAsync()
    {
        if (IsPairing) return;
        ErrorText = null;
        Sas = null;
        IsPairing = true;
        _pairing = new CancellationTokenSource();
        Notify();
        try
        {
            await _runtime.PairAsync(Target, digits => Dispatcher.UIThread.Post(() =>
            {
                Sas = digits.Length == 6 ? digits[..3] + " " + digits[3..] : digits;
                Notify();
            }), _pairing.Token);
            Target = "";
        }
        catch (FormatException)
        {
            ErrorText = AppLangResources.Get(AppLangResourceKind.SettingsRemoteHostsBadAddress);
        }
        catch (HandshakeException e) when (e.Status == HandshakeStatus.PairingClosed)
        {
            ErrorText = AppLangResources.Get(AppLangResourceKind.SettingsRemoteHostsNotPairing);
        }
        catch (HandshakeException e) when (e.Failure == HandshakeFailure.Rejected)
        {
            ErrorText = AppLangResources.Get(AppLangResourceKind.SettingsRemoteHostsRefused);
        }
        catch (OperationCanceledException)
        {
            // The person cancelled: nothing to say.
        }
        catch (Exception e) when (e is IOException or HandshakeException or Octopus.Remote.Protocol.RemoteException
                                      or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException)
        {
            ErrorText = string.Format(AppLangResources.Get(AppLangResourceKind.SettingsRemoteHostsUnreachable), e.Message);
        }
        finally
        {
            _pairing?.Dispose();
            _pairing = null;
            IsPairing = false;
            Sas = null;
            Refresh();
        }
    }

    private void OnHostsChanged() => OnChanged("");

    // Each host's row says how many of its projects are here.
    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (e.Kind == ProjectChangeKind.Projects) OnChanged("");
    }

    // The runtime reports from network threads.
    private void OnChanged(string hostId)
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        var rows = _runtime.Hosts.Peers.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(peer =>
        {
            var state = _runtime.StateOf(peer.Id);
            var count = _projects.Projects.Count(p => p.RemoteHost == peer.Id);
            return new RemoteHostRow(
                peer.Id,
                peer.Name,
                _runtime.IsThroughRelay(peer.Id) ? AppLangResources.Get(AppLangResourceKind.SettingsRemoteHostsViaRelay) : peer.Addresses.FirstOrDefault() ?? "",
                AppLangResources.Get(state switch
                {
                    RemoteHostState.Connected => AppLangResourceKind.SettingsRemoteHostsStateConnected,
                    RemoteHostState.Connecting => AppLangResourceKind.SettingsRemoteHostsStateConnecting,
                    _ => AppLangResourceKind.SettingsRemoteHostsStateOffline,
                }),
                state == RemoteHostState.Connected,
                state == RemoteHostState.Connecting,
                state == RemoteHostState.Connected ? null : _runtime.ErrorOf(peer.Id),
                count == 0 ? "" : string.Format(AppLangResources.Get(AppLangResourceKind.SettingsRemoteHostsProjects), count));
        }).ToList();
        if (!rows.SequenceEqual(Hosts))
        {
            Hosts.Clear();
            foreach (var row in rows) Hosts.Add(row);
        }
        HasHosts = Hosts.Count > 0;
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(HasSas));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsConnecting));
    }
}
