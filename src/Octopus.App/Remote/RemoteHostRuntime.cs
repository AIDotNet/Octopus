using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using Avalonia;
using Avalonia.Styling;
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
    private readonly SemaphoreSlim _passwordGate = new(1, 1);
    /// <summary>The keystore entry of the access password (design/16).</summary>
    internal const string AccessPasswordName = "access-password";
    private string? _accessPassword;
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
        relay?.Changed += OnRelayAccessChanged;
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
        Protocol = RemoteProtocol.Version,
        MinProtocol = RemoteProtocol.Version,
        MaxProtocol = RemoteProtocol.LatestVersion,
        AppVersion = HostWorkspace.AppVersion,
        Capabilities =
        [
            RemoteMethods.CapabilityWorkspace, RemoteMethods.CapabilityNativeSnapshot, RemoteMethods.CapabilityFiles,
            RemoteMethods.CapabilityGit, RemoteMethods.CapabilitySizeRelease, RemoteMethods.CapabilityBrowse,
            RemoteMethods.CapabilityWorkspaces, RemoteMethods.CapabilityAcpGui, RemoteMethods.CapabilityAcpMentions,
            RemoteMethods.CapabilityAcpCollabCancel, RemoteMethods.CapabilityUpload,
            RemoteMethods.CapabilitySystemMetrics, RemoteMethods.CapabilitySystemSignal, RemoteMethods.CapabilityProcessRun,
            RemoteMethods.CapabilityProtocolV2, RemoteMethods.CapabilityCompression, RemoteMethods.CapabilityFileStream,
            RemoteMethods.CapabilitySessionResume, RemoteMethods.CapabilityTransportQuic, RemoteMethods.CapabilityTransportP2p,
            RemoteMethods.CapabilityTransportMigrate, RemoteMethods.CapabilityProjectIcons, RemoteMethods.CapabilityProjectGroups,
            RemoteMethods.CapabilityBots, RemoteMethods.CapabilityBotGroups, RemoteMethods.CapabilityLlmTunnel,
        ],
        TerminalBuild = typeof(TKCode.Terminal.Core.TerminalCore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        Transport = TransportNegotiation.CreateDefault(),
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
            var service = new RemoteHostService(identity, () => new DeviceDescription(DeviceName, DeviceKind.Desktop), devices, _workspace, Hello);
            service.PasswordAccessSuspendedChanged += OnPasswordAccessChanged;
            var password = await LoadAccessPasswordAsync().ConfigureAwait(false);
            if (_settings.Current.Remote.PasswordAccessEnabled) service.SetAccessPassword(password);
            return _service = service;
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
            await ApplyPasswordAsync().ConfigureAwait(false);
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
        var link = new RelayHostLink((fresh, cancellationToken) => _relay!.GetAsync(fresh, cancellationToken), service.Identity, () => DeviceName, transport => service.ServeAsync(transport))
        {
            Features = RelayFeatures,
        };
        link.Changed += OnRelayChanged;
        (_relayLink, _relayLinkFor) = (link, wanted);
        link.Start();
    }

    private void OnRelayChanged()
    {
        if (_service is { StunServer: null } service && _relayLink?.ServerInfo is { StunServers.Count: > 0 } info)
        {
            _ = ApplyRelayStunAsync(service, info);
        }
        Changed?.Invoke();
    }

    private static async Task ApplyRelayStunAsync(RemoteHostService service, RelayServerInfo info)
    {
        try
        {
            if (await info.ResolveStunEndPointAsync().ConfigureAwait(false) is { } stun)
            {
                service.StunServer ??= stun;
            }
        }
        catch (Exception)
        {
        }
    }

    // ---- the access password (design/16-账号设备与访问密码.md) ----

    /// <summary>This computer's access password, normalized (<see cref="AccessPassword.Normalize"/>); made and kept in the keystore the first time.</summary>
    public async Task<string> GetAccessPasswordAsync()
    {
        await GetAsync().ConfigureAwait(false);
        return await LoadAccessPasswordAsync().ConfigureAwait(false);
    }

    /// <summary>True while the service takes passwords: the setting is on and it was not suspended after too many wrong ones.</summary>
    public bool AcceptsPassword => _service?.AcceptsPassword == true;

    /// <summary>Password access was suspended after too many wrong passwords.</summary>
    public bool PasswordAccessSuspended => _service?.PasswordAccessSuspended == true;

    /// <summary>
    /// Replaces the access password with one the person chose (eight letters and digits; throws
    /// <see cref="ArgumentException"/> otherwise) or, with null, with a new random one. With <paramref name="revokeOld"/>,
    /// the devices that came in with a password are forgotten and cut off. Returns the new password.
    /// </summary>
    public async Task<string> ChangeAccessPasswordAsync(string? typed, bool revokeOld)
    {
        var password = typed is null ? AccessPassword.Generate() : AccessPassword.Normalize(typed) ?? throw new ArgumentException("An access password is eight letters and digits.", nameof(typed));
        var service = await GetAsync().ConfigureAwait(false);
        await _passwordGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _identity.SaveSecretAsync(AccessPasswordName, password).ConfigureAwait(false);
            _accessPassword = password;
        }
        finally
        {
            _passwordGate.Release();
        }
        if (_settings.Current.Remote.PasswordAccessEnabled) service.SetAccessPassword(password);
        if (revokeOld) await service.RevokePasswordDevicesAsync().ConfigureAwait(false);
        OnPasswordAccessChanged();
        return password;
    }

    /// <summary>Takes passwords again after a suspension.</summary>
    public void ResumePasswordAccess()
    {
        _service?.ResumePasswordAccess();
        OnPasswordAccessChanged();
    }

    /// <summary>What this host tells the relay it offers, for the account's device list.</summary>
    private IReadOnlyList<string> RelayFeatures()
    {
        var features = new List<string>(4) { RelayHostFeatures.ProtocolV2, RelayHostFeatures.P2p };
        if (QuicMessageTransport.IsSupported) features.Add(RelayHostFeatures.Quic);
        if (AcceptsPassword) features.Add(RelayHostFeatures.PasswordAccess);
        return features;
    }

    private void OnPasswordAccessChanged()
    {
        if (_relayLink is { } link) _ = link.AnnounceFeaturesAsync();
        Changed?.Invoke();
    }

    // The service follows the setting: off takes the password away from it, on gives it back.
    private async Task ApplyPasswordAsync()
    {
        if (_service is not { } service) return;
        var wanted = _settings.Current.Remote.PasswordAccessEnabled ? await LoadAccessPasswordAsync().ConfigureAwait(false) : null;
        if (service.AccessPassword == wanted) return;
        service.SetAccessPassword(wanted);
        OnPasswordAccessChanged();
    }

    private async Task<string> LoadAccessPasswordAsync()
    {
        if (_accessPassword is { } known) return known;
        await _passwordGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_accessPassword is { } loaded) return loaded;
            var stored = AccessPassword.Normalize(await _identity.LoadSecretAsync(AccessPasswordName).ConfigureAwait(false));
            if (stored is null)
            {
                stored = AccessPassword.Generate();
                await _identity.SaveSecretAsync(AccessPasswordName, stored).ConfigureAwait(false);
            }
            return _accessPassword = stored;
        }
        finally
        {
            _passwordGate.Release();
        }
    }

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

    /// <summary>Where a device of the local network reaches this host: prioritized private IPv4 addresses of this computer; empty while not listening.</summary>
    public IReadOnlyList<string> LanAddresses()
    {
        if (_service?.LanPort is not { } port) return [];
        var candidates = new List<LanCandidate>();
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var properties = network.GetIPProperties();
                var hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address));
                foreach (var address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork && LocalNetwork.IsLocal(address.Address))
                    {
                        candidates.Add(new LanCandidate(address.Address, network.Name, network.Description, network.NetworkInterfaceType, hasGateway));
                    }
                }
            }
        }
        catch (NetworkInformationException) { }
        var selected = SelectLanAddresses(candidates, port);
        return selected.Count > 0 ? selected : [$"ws://{IPAddress.Loopback}:{port}"];
    }

    internal readonly record struct LanCandidate(IPAddress Address, string Name, string Description, NetworkInterfaceType InterfaceType, bool HasGateway);

    /// <summary>
    /// Ranks reachable physical LAN interfaces ahead of virtual bridges/tunnels (Docker, VMware, WSL, VPN) and drops
    /// link-local or virtual-only addresses when real LAN interfaces are available, keeping pairing QR codes compact
    /// and avoiding dead-NIC timeouts.
    /// </summary>
    internal static IReadOnlyList<string> SelectLanAddresses(IEnumerable<LanCandidate> candidates, int port, int maxAddresses = 4)
    {
        var list = candidates
            .Where(c => c.Address.AddressFamily == AddressFamily.InterNetwork && LocalNetwork.IsLocal(c.Address) && !IPAddress.IsLoopback(c.Address))
            .DistinctBy(c => c.Address)
            .ToList();
        if (list.Count == 0) return [];

        static bool IsLinkLocal(IPAddress ip)
        {
            Span<byte> b = stackalloc byte[4];
            return ip.TryWriteBytes(b, out _) && b[0] == 169 && b[1] == 254;
        }

        static bool IsVirtual(LanCandidate c)
        {
            if (c.InterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Loopback) return true;
            var name = c.Name;
            var desc = c.Description;
            return ContainsAny(name, "docker", "br-", "veth", "virbr", "vmnet", "vbox", "utun", "awdl", "llw", "bridge", "tailscale", "wg", "tun", "tap", "wsl", "vethernet")
                || ContainsAny(desc, "virtual", "vmware", "virtualbox", "hyper-v", "wsl", "docker", "loopback", "tunnel", "vpn", "wireguard", "tailscale", "zerotier");
        }

        static bool ContainsAny(string text, params ReadOnlySpan<string> needles)
        {
            foreach (var needle in needles)
            {
                if (text.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static int Rank(LanCandidate c)
        {
            var score = 0;
            if (!IsVirtual(c)) score += 100;
            if (c.HasGateway) score += 50;
            if (!IsLinkLocal(c.Address)) score += 25;
            if (c.InterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet) score += 10;
            return score;
        }

        var physical = list.Where(c => !IsVirtual(c) && !IsLinkLocal(c.Address)).ToList();
        var pool = physical.Count > 0 ? physical : list;
        return pool
            .OrderByDescending(Rank)
            .ThenBy(c => c.Address.ToString(), StringComparer.Ordinal)
            .Take(maxAddresses)
            .Select(c => $"ws://{c.Address}:{port}")
            .ToList();
    }

    public void Dispose()
    {
        // At exit: devices are cut off and the port is given back; nothing waits for them.
        _relay?.Changed -= OnRelayAccessChanged;
        if (_service is { } running) running.PasswordAccessSuspendedChanged -= OnPasswordAccessChanged;
        if (_relayLink is { } link) _ = link.DisposeAsync().AsTask();
        if (_service is { } service) _ = service.DisposeAsync().AsTask();
        _workspace.Dispose();
    }
}
