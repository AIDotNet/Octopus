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
    private PairingWindow? _pairing;

    public RemoteHostService(DeviceIdentity identity, Func<DeviceDescription> description, PairedPeerStore devices, IHostWorkspace workspace, HelloMessage hello)
    {
        _identity = identity;
        _description = description;
        _devices = devices;
        _workspace = workspace;
        _hello = hello;
    }

    /// <summary>Connections, pairing requests or the listening state changed. Raised on any thread.</summary>
    public event Action? Changed;

    public DeviceIdentity Identity => _identity;
    public PairedPeerStore Devices => _devices;

    /// <summary>The port this host listens on; null while it does not listen.</summary>
    public int? LanPort
    {
        get { lock (_gate) return _listener?.Port; }
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
        var connection = new HostConnection(new RemoteConnection(channel), _workspace, _hello);
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
