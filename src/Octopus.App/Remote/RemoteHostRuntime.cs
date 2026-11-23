using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using Avalonia;
using Avalonia.Styling;
using Octopus.Platform;
using Octopus.Remote.Host;
using Octopus.Remote.Protocol;
using Octopus.Remote.Relay;
using Octopus.Remote.Security;
using Octopus.Remote.Transport;
using Octopus.Storage;

namespace Octopus.App.Remote;

/// <summary>
/// The host side of remote operation inside the app: this computer's device key, its list of paired devices, and the
/// service that lets them in, kept in line with the settings. Nothing is made until it is first needed (a computer
/// that never turns remote access on gets no key and no file).
/// </summary>
public sealed class RemoteHostRuntime : IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly RemoteIdentityProvider _identity;
    private readonly HostWorkspace _workspace;
    private readonly SemaphoreSlim _starting = new(1, 1);
    private readonly SemaphoreSlim _applying = new(1, 1);
    private readonly RelayAccess? _relay;
    private RemoteHostService? _service;
    private RelayHostLink? _relayLink;
    // The relay and the account the link was made for: another address or another account means another link.
    private (Uri Url, string AccountId)? _relayLinkFor;

    public RemoteHostRuntime(ISettingsStore settings, RemoteIdentityProvider identity, HostWorkspace workspace, RelayAccess? relay = null)
    {
        _relay = relay;
        _settings = settings;
        _identity = identity;
        _workspace = workspace;
        // Signed in, signed out, or signed out by the relay: the standing connection follows, whether or not the
        // settings page is open.
        if (relay is not null) relay.Changed += OnRelayAccessChanged;
        workspace.IsDark = static () => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
    }

    /// <summary>The service, once something needed it.</summary>
    public RemoteHostService? Service => _service;

    /// <summary>Why listening did not start, as the system said it; null when it did or was not asked to.</summary>
    public string? ListenError { get; private set; }

    /// <summary>How the connection to the relay is doing; null while this computer is not to be reached through one.</summary>
    public RelayLinkState? RelayState => _relayLink?.State;

    /// <summary>Why this computer is not reachable through the relay, in the relay client's words; null when it is, or is not meant to be.</summary>
    public string? RelayError => _relayLink is { State: RelayLinkState.Failed } link ? link.Error : null;

    /// <summary>What a device is given to reach this computer through the relay; null while it is not registered there.</summary>
    public string? RelayAddress => _relayLink is { State: RelayLinkState.Online } && _service is { } service ? RemoteClientRuntime.RelayAddress(service.Identity.Id).ToString() : null;

    /// <summary>The listening state, the relay's state or an error changed. Raised on any thread.</summary>
    public event Action? Changed;

    /// <summary>The name other devices see: the one from the settings, or the computer's.</summary>
    public string DeviceName => string.IsNullOrWhiteSpace(_settings.Current.Remote.DeviceName) ? Environment.MachineName : _settings.Current.Remote.DeviceName!.Trim();

    /// <summary>What this app says about itself to a device: its version, what it can do, and its terminal library's build.</summary>
    public static HelloMessage Hello { get; } = new()
    {
        AppVersion = HostWorkspace.AppVersion,
        Capabilities = [RemoteMethods.CapabilityWorkspace, RemoteMethods.CapabilityNativeSnapshot, RemoteMethods.CapabilityFiles, RemoteMethods.CapabilityGit, RemoteMethods.CapabilitySizeRelease, RemoteMethods.CapabilityBrowse, RemoteMethods.CapabilityWorkspaces],
        TerminalBuild = typeof(TKCode.Terminal.Core.TerminalCore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    };

    /// <summary>The service, made on first use: the device key is read from the keystore (or made), the paired devices from their file.</summary>
    public async Task<RemoteHostService> GetAsync()
    {
        if (_service is { } ready) return ready;
        await _starting.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_service is { } made) return made;
            var identity = await _identity.GetAsync().ConfigureAwait(false);
            var devices = new PairedPeerStore(Path.Combine(_identity.Folder, "devices.json"));
            return _service = new RemoteHostService(identity, () => new DeviceDescription(DeviceName, DeviceKind.Desktop), devices, _workspace, Hello);
        }
        finally
        {
            _starting.Release();
        }
    }

    /// <summary>
    /// Makes the state what the settings say: starts or stops listening on the local network, and connects to the relay
    /// or leaves it.
    /// </summary>
    public async Task ApplyAsync()
    {
        await _applying.WaitAsync().ConfigureAwait(false);
        try
        {
            await ApplyLanAsync().ConfigureAwait(false);
            await ApplyRelayAsync().ConfigureAwait(false);
        }
        finally
        {
            _applying.Release();
        }
        Changed?.Invoke();
    }

    private async Task ApplyLanAsync()
    {
        var remote = _settings.Current.Remote;
        if (!remote.LanEnabled)
        {
            ListenError = null;
            if (_service is { } idle) await idle.StopLanAsync().ConfigureAwait(false);
            return;
        }
        var service = await GetAsync().ConfigureAwait(false);
        try
        {
            service.StartLan(remote.LanPort);
            ListenError = null;
        }
        catch (SocketException e)
        {
            ListenError = e.Message;
        }
    }

    // The standing connection to the relay: there while the switch is on, a relay is set up and this computer is
    // signed in to it, and made anew when the address or the account changed. Who comes through it is served like any
    // device that connects.
    private async Task ApplyRelayAsync()
    {
        (Uri Url, string AccountId)? wanted = null;
        if (_relay is { } relay && _settings.Current.Remote.RelayHostEnabled)
        {
            await relay.LoadAsync().ConfigureAwait(false);
            if (Octopus.Remote.Relay.RelayAccountClient.ParseUrl(relay.Url) is { } url && relay.Account is { } account) wanted = (url, account.Id);
        }
        if (_relayLink is { } current && wanted != _relayLinkFor)
        {
            (_relayLink, _relayLinkFor) = (null, null);
            current.Changed -= OnRelayChanged;
            await current.DisposeAsync().ConfigureAwait(false);
        }
        if (wanted is null || _relayLink is not null) return;
        var service = await GetAsync().ConfigureAwait(false);
        // The link asks for a token each time it connects: they live for minutes, the link for as long as the app.
        var link = new RelayHostLink((fresh, cancellationToken) => _relay!.GetAsync(fresh, cancellationToken), service.Identity, () => DeviceName, transport => service.ServeAsync(transport));
        link.Changed += OnRelayChanged;
        (_relayLink, _relayLinkFor) = (link, wanted);
        link.Start();
    }

    private void OnRelayChanged() => Changed?.Invoke();

    private void OnRelayAccessChanged() => _ = FollowRelayAsync();

    private async Task FollowRelayAsync()
    {
        try
        {
            await ApplyAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
        Changed?.Invoke();
    }

    /// <summary>Where a device of the local network reaches this host: one address per private IPv4 address of this computer; empty while not listening.</summary>
    public IReadOnlyList<string> LanAddresses()
    {
        if (_service?.LanPort is not { } port) return [];
        var addresses = new List<string>();
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var address in network.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork && LocalNetwork.IsLocal(address.Address))
                    {
                        addresses.Add($"ws://{address.Address}:{port}");
                    }
                }
            }
        }
        catch (NetworkInformationException) { }
        return addresses.Count > 0 ? addresses : [$"ws://{IPAddress.Loopback}:{port}"];
    }

    public void Dispose()
    {
        // At exit: devices are cut off and the port is given back; nothing waits for them.
        if (_relay is not null) _relay.Changed -= OnRelayAccessChanged;
        if (_relayLink is { } link) _ = link.DisposeAsync().AsTask();
        if (_service is { } service) _ = service.DisposeAsync().AsTask();
        _workspace.Dispose();
    }
}
