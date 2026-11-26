using System.Net;
using System.Security.Cryptography;
using Octopus.Remote.Protocol;
using Octopus.Remote.Security;
using Octopus.Remote.Transport;

namespace Octopus.Remote.Host;

/// <summary>A device that asks to be paired and waits for the person at this computer.</summary>
public sealed class PairingRequest
{
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<PairingRequest, bool> _answered;

    internal PairingRequest(PeerOffer offer, Action<PairingRequest, bool> answered)
    {
        Offer = offer;
        _answered = answered;
    }

    internal PeerOffer Offer { get; }
    internal Task<bool> Answer => _answer.Task;
    public string DeviceId => Offer.Key.DeviceId;
    public DeviceDescription Device => Offer.Description;
    /// <summary>The six digits the device shows too; the person compares them before allowing.</summary>
    public string Sas => Offer.Sas;

    public void Allow() => Finish(true);
    public void Refuse() => Finish(false);

    private void Finish(bool allowed)
    {
        if (_answer.TrySetResult(allowed)) _answered(this, allowed);
    }
}

/// <summary>The few minutes during which this host lets new devices ask to be paired.</summary>
public sealed class PairingWindow
{
    /// <summary>How many devices may ask in one window: each try of somebody in the middle has one chance in a million.</summary>
    public const int MaxRequests = 3;

    internal PairingWindow(DateTimeOffset expiresAt, byte[] token) => (ExpiresAt, Token) = (expiresAt, token);

    public DateTimeOffset ExpiresAt { get; }
    /// <summary>The one-time token of the pairing link; null once it was used.</summary>
    public byte[]? Token { get; internal set; }
    internal int Attempts;
    internal List<PairingRequest> Pending { get; } = [];
    public bool IsOpen => DateTimeOffset.UtcNow < ExpiresAt;
}

/// <summary>
/// How this host takes devices that come with its access password (design/16-账号设备与访问密码.md): the key made
/// from the password, and the count of wrong ones, by device and in all.
/// </summary>
internal sealed class PasswordDoor
{
    /// <summary>Wrong passwords from one device before it has to wait.</summary>
    public const int MaxFailuresPerDevice = 5;
    public static readonly TimeSpan DeviceLock = TimeSpan.FromMinutes(15);
    /// <summary>Wrong passwords from all devices within <see cref="Window"/> before password access is suspended.</summary>
    public const int MaxFailuresInWindow = 10;
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public string? Password;
    public byte[]? Key;
    public bool Suspended;
    public readonly Dictionary<string, (int Failures, DateTimeOffset LockedUntil)> Devices = new(StringComparer.Ordinal);
    public readonly Queue<DateTimeOffset> Recent = new();
}

/// <summary>A device that is connected right now.</summary>
public sealed record ConnectedDevice(string DeviceId, DeviceDescription Device, DateTimeOffset ConnectedAt, IPEndPoint? Remote, int AttachedTerminals);

/// <summary>
/// Everything the host side does: listens on the local network, decides who may come in (paired devices, and new
/// ones while a pairing window is open), and serves each connected device from the workspace.
/// </summary>
public sealed class RemoteHostService : IAsyncDisposable
{
    /// <summary>How long a device has to finish the handshake when nobody has to be asked.</summary>
    public static readonly TimeSpan HandshakeLimit = TimeSpan.FromSeconds(15);

    private readonly DeviceIdentity _identity;
    private readonly Func<DeviceDescription> _description;
    private readonly PairedPeerStore _devices;
    private readonly IHostWorkspace _workspace;
    private readonly HelloMessage _hello;
    private readonly object _gate = new();
    private readonly List<(HostConnection Connection, IPEndPoint? Remote)> _connections = [];
    private WebSocketListener? _listener;
    private CancellationTokenSource? _listening;
    private QuicTransportListener? _quicListener;
    private CancellationTokenSource? _quicListening;
    private PairingWindow? _pairing;
    private readonly PasswordDoor _password = new();

    public RemoteHostService(DeviceIdentity identity, Func<DeviceDescription> description, PairedPeerStore devices, IHostWorkspace workspace, HelloMessage hello)
    {
        _identity = identity;
        _description = description;
        _devices = devices;
        _workspace = workspace;
        _hello = hello;
    }

    /// <summary>Optional STUN server used when gathering server-reflexive candidates for WebRTC P2P upgrades.</summary>
    public IPEndPoint? StunServer { get; set; }

    /// <summary>Connections, pairing requests or the listening state changed. Raised on any thread.</summary>
    public event Action? Changed;

    public DeviceIdentity Identity => _identity;
    public PairedPeerStore Devices => _devices;

    /// <summary>The port this host listens on; null while it does not listen.</summary>
    public int? LanPort
    {
        get { lock (_gate) return _listener?.Port; }
    }

    /// <summary>The UDP QUIC port this host listens on; null while QUIC listening is not active.</summary>
    public int? QuicPort
    {
        get { lock (_gate) return _quicListener?.Port; }
    }

    public IReadOnlyList<ConnectedDevice> Connections
    {
        get
        {
            lock (_gate)
            {
                return [.. _connections.Select(c => new ConnectedDevice(c.Connection.Peer.DeviceId, c.Connection.PeerDescription, c.Connection.ConnectedAt, c.Remote, c.Connection.AttachedCount))];
            }
        }
    }

    /// <summary>The open pairing window, or null.</summary>
    public PairingWindow? Pairing
    {
        get { lock (_gate) return _pairing is { IsOpen: true } ? _pairing : null; }
    }

    /// <summary>The devices waiting for the person to compare numbers.</summary>
    public IReadOnlyList<PairingRequest> PairingRequests
    {
        get { lock (_gate) return _pairing is { IsOpen: true } window ? [.. window.Pending] : []; }
    }

    /// <summary>Starts listening for devices of the local network. Throws when the port cannot be opened.</summary>
    /// <param name="port">0 lets the system pick one.</param>
    public void StartLan(int port, IPAddress? address = null)
    {
        lock (_gate)
        {
            if (_listener is not null) return;
            var listener = new WebSocketListener(address ?? IPAddress.Any, port);
            listener.Start();
            _listener = listener;
            _listening = new CancellationTokenSource();
            _ = AcceptLoopAsync(listener, _listening.Token);
        }
        Changed?.Invoke();
    }

    /// <summary>Stops listening. Devices that are connected stay connected.</summary>
    public async Task StopLanAsync()
    {
        WebSocketListener? listener;
        lock (_gate)
        {
            listener = _listener;
            _listener = null;
            _listening?.Cancel();
            _listening = null;
        }
        if (listener is not null) await listener.DisposeAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>Starts listening for QUIC connections (<c>quic://host:port</c>) on the local network when supported.</summary>
    public async Task StartQuicAsync(int port, IPAddress? address = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_quicListener is not null) return;
        }
        var listener = new QuicTransportListener(address ?? IPAddress.Any, port);
        try
        {
            await listener.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        CancellationToken token;
        lock (_gate)
        {
            if (_quicListener is not null)
            {
                _ = listener.DisposeAsync().AsTask();
                return;
            }
            _quicListener = listener;
            _quicListening = new CancellationTokenSource();
            token = _quicListening.Token;
        }
        _ = AcceptQuicLoopAsync(listener, token);
        Changed?.Invoke();
    }

    /// <summary>Stops listening for QUIC connections.</summary>
    public async Task StopQuicAsync()
    {
        QuicTransportListener? listener;
        lock (_gate)
        {
            listener = _quicListener;
            _quicListener = null;
            _quicListening?.Cancel();
            _quicListening = null;
        }
        if (listener is not null) await listener.DisposeAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>Lets new devices ask to be paired for <paramref name="duration"/>; an open window is replaced.</summary>
    public PairingWindow OpenPairing(TimeSpan duration)
    {
        PairingWindow window;
        lock (_gate)
        {
            RefusePending(_pairing);
            window = _pairing = new PairingWindow(DateTimeOffset.UtcNow + duration, RandomNumberGenerator.GetBytes(16));
        }
        Changed?.Invoke();
        return window;
    }

    /// <summary>Ends the pairing window; devices that were waiting are refused.</summary>
    public void ClosePairing()
    {
        lock (_gate)
        {
            RefusePending(_pairing);
            _pairing = null;
        }
        Changed?.Invoke();
    }

    /// <summary>The link a device follows to pair without comparing numbers; null while no window is open or its token was used.</summary>
    public PairingLink? CreatePairingLink(IEnumerable<string> addresses)
    {
        lock (_gate)
        {
            return _pairing is { IsOpen: true, Token: { } token }
                ? new PairingLink(_identity.Id, _identity.PublicKey.Fingerprint(), token, _description().Name, [.. addresses])
                : null;
        }
    }

    /// <summary>
    /// The access password devices of the same account may come in with; null: none may (password access is off). A
    /// normalized password (<see cref="AccessPassword.Normalize"/>). Setting it lifts a suspension and every device's wait.
    /// </summary>
    public string? AccessPassword
    {
        get { lock (_gate) return _password.Password; }
    }

    /// <summary>True while password access is suspended after too many wrong passwords; <see cref="SetAccessPassword"/> or <see cref="ResumePasswordAccess"/> ends it.</summary>
    public bool PasswordAccessSuspended
    {
        get { lock (_gate) return _password.Suspended; }
    }

    /// <summary>Password access is on, and not suspended: devices with the right password come in.</summary>
    public bool AcceptsPassword
    {
        get { lock (_gate) return _password.Password is not null && !_password.Suspended; }
    }

    /// <summary>Too many wrong passwords: password access was suspended. Raised on any thread.</summary>
    public event Action? PasswordAccessSuspendedChanged;

    /// <summary>Sets the access password (null turns password access off). Throws when it is not eight letters and digits.</summary>
    public void SetAccessPassword(string? password)
    {
        var normalized = password is null ? null : Octopus.Remote.Security.AccessPassword.Normalize(password) ?? throw new ArgumentException("An access password is eight letters and digits.", nameof(password));
        bool wasSuspended;
        lock (_gate)
        {
            if (_password.Password != normalized) _password.Key = null; // made again on first use: it takes a moment
            _password.Password = normalized;
            wasSuspended = _password.Suspended;
            _password.Suspended = false;
            _password.Devices.Clear();
            _password.Recent.Clear();
        }
        if (wasSuspended) PasswordAccessSuspendedChanged?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Takes passwords again after a suspension, with the same password.</summary>
    public void ResumePasswordAccess() => SetAccessPassword(AccessPassword);

    /// <summary>Forgets every device that came in with the access password and ends their connections (when the password changed hands).</summary>
    public async Task RevokePasswordDevicesAsync()
    {
        var removed = _devices.RemoveAll(PairedVia.Password);
        List<HostConnection> ended;
        lock (_gate) ended = [.. _connections.Where(c => removed.Contains(c.Connection.Peer.DeviceId)).Select(c => c.Connection)];
        foreach (var connection in ended) await connection.DisposeAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>Forgets a paired device and ends its connections at once.</summary>
    public async Task RevokeAsync(string deviceId)
    {
        _devices.Remove(deviceId);
        List<HostConnection> ended;
        lock (_gate) ended = [.. _connections.Where(c => c.Connection.Peer.DeviceId == deviceId).Select(c => c.Connection)];
        foreach (var connection in ended) await connection.DisposeAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>
    /// Serves one device over a transport that just connected: the handshake, then its requests until it leaves. Returns
    /// when the connection has ended; a device that may not come in ends it during the handshake.
    /// </summary>
    public async Task ServeAsync(IMessageTransport transport, IPEndPoint? remote = null, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(HandshakeLimit);
        SecureChannel channel;
        try
        {
            channel = await SecureChannel.AcceptAsync(transport, _identity, _description(), new Door(this, deadline), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HandshakeException or OperationCanceledException or IOException)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            return;
        }
        _devices.Touch(channel.Peer.DeviceId, channel.PeerDescription.Name);
        var helloWithInfo = _hello.HostInfo is not null ? _hello : new HelloMessage
        {
            Protocol = _hello.Protocol,
            MinProtocol = _hello.MinProtocol,
            MaxProtocol = _hello.MaxProtocol,
            AppVersion = _hello.AppVersion,
            Capabilities = _hello.Capabilities,
            TerminalBuild = _hello.TerminalBuild,
            Transport = _hello.Transport,
            HostInfo = _workspace.Info,
        };
        var connection = new HostConnection(new RemoteConnection(channel), _workspace, helloWithInfo, HandleP2pHandshakeAsync);
        lock (_gate) _connections.Add((connection, remote));
        connection.Start();
        Changed?.Invoke();
        try
        {
            await connection.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_gate) _connections.RemoveAll(c => ReferenceEquals(c.Connection, connection));
            await connection.DisposeAsync().ConfigureAwait(false);
            Changed?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopLanAsync().ConfigureAwait(false);
        await StopQuicAsync().ConfigureAwait(false);
        ClosePairing();
        List<HostConnection> connections;
        lock (_gate) connections = [.. _connections.Select(c => c.Connection)];
        foreach (var connection in connections) await connection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(WebSocketListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AcceptedTransport accepted;
            try { accepted = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or System.Threading.Channels.ChannelClosedException) { return; }
            // Not tied to the listener: turning "local network access" off does not cut off who is already in.
            _ = ServeAsync(accepted.Transport, accepted.Remote);
        }
    }

    private async Task AcceptQuicLoopAsync(QuicTransportListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AcceptedTransport accepted;
            try { accepted = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or System.Threading.Channels.ChannelClosedException) { return; }
            _ = ServeAsync(accepted.Transport, accepted.Remote);
        }
    }

    private async Task<P2pHandshakeMessage> HandleP2pHandshakeAsync(HostConnection connection, P2pHandshakeMessage remoteHandshake, CancellationToken cancellationToken)
    {
        var bindAddress = remoteHandshake.Candidates.Any(c => IPAddress.TryParse(c.Address, out var ip) && IPAddress.IsLoopback(ip))
            && remoteHandshake.Candidates.All(c => IPAddress.TryParse(c.Address, out var ip) && IPAddress.IsLoopback(ip))
            ? IPAddress.Loopback
            : IPAddress.Any;
        var endpoint = new IceP2pEndpoint(bindAddress, 0);
        try
        {
            var ownHandshake = await endpoint.GatherAsync(StunServer, includeLoopback: true, cancellationToken).ConfigureAwait(false);
            _ = Task.Run(async () =>
            {
                using (endpoint)
                {
                    try
                    {
                        var p2pTransport = await endpoint.PunchAsync(remoteHandshake, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                        using var deadline = new CancellationTokenSource(HandshakeLimit);
                        var policy = new ExpectedPeerDoor(connection.Peer);
                        var newChannel = await SecureChannel.AcceptAsync(p2pTransport, _identity, _description(), policy, deadline.Token).ConfigureAwait(false);
                        await connection.Connection.MigrateTransportAsync(newChannel, deadline.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // P2P upgrade failed or timed out; the existing connection stays untouched.
                    }
                }
            });
            return ownHandshake;
        }
        catch
        {
            endpoint.Dispose();
            throw;
        }
    }

    private sealed class ExpectedPeerDoor(DevicePublicKey expected) : IAcceptPolicy
    {
        public AcceptDecision Decide(PeerOffer offer) =>
            offer.Key.Equals(expected) ? AcceptDecision.Accept : AcceptDecision.Reject(HandshakeStatus.UnknownDevice);
    }

    private static void RefusePending(PairingWindow? window)
    {
        if (window is null) return;
        foreach (var request in window.Pending.ToList()) request.Refuse();
        window.Pending.Clear();
    }

    /// <summary>Who may come in (design/remote/安全通道.md §3.1, the responder's third step).</summary>
    private sealed class Door(RemoteHostService host, CancellationTokenSource deadline) : IAcceptPolicy
    {
        public AcceptDecision Decide(PeerOffer offer)
        {
            // A device with a password is checked even when it is known: it takes only a host that proves the password back.
            if (offer.RequestsPasswordAccess) return host.DecideByPassword(offer);
            if (host._devices.Find(offer.Key) is not null) return AcceptDecision.Accept;
            if (!offer.RequestsPairing) return AcceptDecision.Reject(HandshakeStatus.UnknownDevice);
            PairingRequest request;
            lock (host._gate)
            {
                if (host._pairing is not { IsOpen: true } window) return AcceptDecision.Reject(HandshakeStatus.PairingClosed);
                if (window.Token is { } token && offer.Token.Length > 0 && CryptographicOperations.FixedTimeEquals(token, offer.Token.Span))
                {
                    window.Token = null; // once
                    host._devices.Add(offer.Key, offer.Description);
                    return AcceptDecision.Accept;
                }
                if (window.Attempts >= PairingWindow.MaxRequests) return AcceptDecision.Reject(HandshakeStatus.PairingClosed);
                window.Attempts++;
                request = new PairingRequest(offer, host.OnAnswered);
                window.Pending.Add(request);
                // The person may take until the window closes; the handshake waits that long instead of the usual limit.
                deadline.CancelAfter(window.ExpiresAt - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5));
            }
            host.Changed?.Invoke();
            return AcceptDecision.AskPerson(request.Answer);
        }
    }

    // design/16-账号设备与访问密码.md §4: a device waits after five wrong passwords; ten from anyone within an hour
    // suspend password access until the person at this computer takes it up again.
    private AcceptDecision DecideByPassword(PeerOffer offer)
    {
        var id = offer.Key.DeviceId;
        var now = DateTimeOffset.UtcNow;
        string password;
        lock (_gate)
        {
            if (_password.Password is not { } current || _password.Suspended) return AcceptDecision.Reject(HandshakeStatus.PairingClosed);
            if (_password.Devices.TryGetValue(id, out var record) && record.LockedUntil > now) return AcceptDecision.Reject(HandshakeStatus.PasswordLocked);
            if (_password.Key is { } ready) return Check(ready);
            password = current;
        }
        // Slow on purpose; outside the lock. Two first devices at once both make it, which is harmless.
        var key = Octopus.Remote.Security.AccessPassword.DeriveKey(password, _identity.PublicKey);
        lock (_gate)
        {
            if (_password.Password != password) return AcceptDecision.Reject(HandshakeStatus.WrongPassword); // changed meanwhile
            _password.Key = key;
            return Check(key);
        }

        AcceptDecision Check(byte[] key)
        {
            if (offer.ProvesPassword(key))
            {
                _password.Devices.Remove(id);
                _devices.Add(offer.Key, offer.Description, via: PairedVia.Password);
                return AcceptDecision.AcceptWithPassword(key);
            }
            var failures = (_password.Devices.TryGetValue(id, out var before) ? before.Failures : 0) + 1;
            _password.Devices[id] = failures >= PasswordDoor.MaxFailuresPerDevice ? (0, now + PasswordDoor.DeviceLock) : (failures, default);
            _password.Recent.Enqueue(now);
            while (_password.Recent.TryPeek(out var oldest) && now - oldest > PasswordDoor.Window) _password.Recent.Dequeue();
            if (_password.Recent.Count >= PasswordDoor.MaxFailuresInWindow)
            {
                _password.Suspended = true;
                _ = Task.Run(() =>
                {
                    PasswordAccessSuspendedChanged?.Invoke();
                    Changed?.Invoke();
                });
            }
            return AcceptDecision.Reject(failures >= PasswordDoor.MaxFailuresPerDevice ? HandshakeStatus.PasswordLocked : HandshakeStatus.WrongPassword);
        }
    }

    private void OnAnswered(PairingRequest request, bool allowed)
    {
        lock (_gate)
        {
            _pairing?.Pending.Remove(request);
            if (allowed) _devices.Add(request.Offer.Key, request.Offer.Description);
        }
        Changed?.Invoke();
    }
}
