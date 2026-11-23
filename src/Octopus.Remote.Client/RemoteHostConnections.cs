using Octopus.Remote.Protocol;
using Octopus.Remote.Relay;
using Octopus.Remote.Security;
using Octopus.Remote.Transport;

namespace Octopus.Remote.Client;

public enum RemoteHostState
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>
/// The device side of remote operation: the hosts this device is paired with, and one connection to each host that is
/// in use. A connection that drops is made again by itself, with growing pauses, for as long as the host is wanted
/// (design/12-远程操作.md §6.4). The desktop app and the phone app both use this.
/// </summary>
public class RemoteHostConnections : IDisposable
{
    private static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(10);
    /// <summary>The scheme of "this host, through the relay": <c>octopus-relay://&lt;device id&gt;</c>.</summary>
    public const string RelayScheme = "octopus-relay";
    /// <summary>How long each further way to a host waits for the ones before it: the direct ones get a head start over the relay.</summary>
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan[] RetryPauses = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly Func<Task<DeviceIdentity>> _identity;
    private readonly Func<DeviceDescription> _description;
    private readonly HelloMessage _hello;
    private readonly IRelayGate? _relay;
    private readonly object _gate = new();
    private readonly Dictionary<string, Link> _links = new(StringComparer.Ordinal);
    private readonly Lazy<PairedPeerStore> _hosts;
    private bool _disposed;

    private sealed class Link
    {
        public RemoteHostClient? Client;
        public Task<RemoteHostClient>? Connecting;
        public string? Error;
        public IReadOnlyList<AgentMessage> Agents = [];
        /// <summary>Something shows this host: when the connection drops it is made again.</summary>
        public bool Wanted;
        public bool Retrying;
        public bool ThroughRelay;
        /// <summary>The person disconnected: a connection still being made is closed when it arrives rather than kept.</summary>
        public bool Closed;
    }

    /// <param name="identity">This device's key (read from its keystore on first use).</param>
    /// <param name="hosts">Where the paired hosts are kept; made on first use.</param>
    /// <param name="description">What this device calls itself to a host.</param>
    /// <param name="hello">What this app says about itself to a host.</param>
    /// <param name="relay">This device's sign-in at a relay, when it may use one.</param>
    public RemoteHostConnections(Func<Task<DeviceIdentity>> identity, Func<PairedPeerStore> hosts, Func<DeviceDescription> description, HelloMessage hello, IRelayGate? relay = null)
    {
        (_identity, _description, _hello, _relay) = (identity, description, hello, relay);
        Dial = DialAsync;
        _hosts = new Lazy<PairedPeerStore>(hosts);
    }

    /// <summary>How a transport to an address is opened; replaced in tests.</summary>
    public Func<Uri, CancellationToken, Task<IMessageTransport>> Dial { get; set; }

    private async Task<IMessageTransport> DialAsync(Uri address, CancellationToken cancellationToken)
    {
        if (address.Scheme != RelayScheme) return await WebSocketTransport.ConnectAsync(address, cancellationToken).ConfigureAwait(false);
        if (_relay is null) throw new IOException("No relay is set up on this computer.");
        var relay = await _relay.GetAsync(false, cancellationToken).ConfigureAwait(false) ?? throw new IOException("This computer is not signed in to a relay.");
        try
        {
            return await RelayDialer.ConnectAsync(relay, address.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (RelayException e) when (e.Failure == RelayFailure.Unauthorized)
        {
            // The token may only have run out a moment ago: once more with one made anew. Refused again, this computer is signed out.
            relay = await _relay.GetAsync(true, cancellationToken).ConfigureAwait(false) ?? throw new IOException("This computer was signed out of the relay. Sign in again in the settings.", e);
            return await RelayDialer.ConnectAsync(relay, address.Host, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The address of a host through the relay this computer uses.</summary>
    public static Uri RelayAddress(string hostId) => new($"{RelayScheme}://{hostId}");

    /// <summary>True while the live connection to the host goes through the relay.</summary>
    public bool IsThroughRelay(string hostId)
    {
        lock (_gate) return _links.GetValueOrDefault(hostId) is { Client: not null, ThroughRelay: true };
    }

    /// <summary>The hosts this computer is paired with.</summary>
    public PairedPeerStore Hosts => _hosts.Value;

    /// <summary>A host connected, disconnected or failed to connect; the argument is its id. Raised on any thread.</summary>
    public event Action<string>? StateChanged;
    /// <summary>A connected host says its projects or items changed.</summary>
    public event Action<string>? WorkspaceChanged;
    /// <summary>A connected host says one item's title or activity changed.</summary>
    public event Action<string, ItemMessage>? ItemChanged;

    public RemoteHostState StateOf(string hostId)
    {
        lock (_gate)
        {
            return !_links.TryGetValue(hostId, out var link) ? RemoteHostState.Disconnected
                : link.Client is not null ? RemoteHostState.Connected
                : link.Connecting is not null || link.Retrying ? RemoteHostState.Connecting
                : RemoteHostState.Disconnected;
        }
    }

    /// <summary>Why the last attempt to reach the host failed; null when it worked or none was made.</summary>
    public string? ErrorOf(string hostId)
    {
        lock (_gate) return _links.GetValueOrDefault(hostId)?.Error;
    }

    /// <summary>The live connection to a host, or null.</summary>
    public RemoteHostClient? ClientOf(string hostId)
    {
        lock (_gate) return _links.GetValueOrDefault(hostId)?.Client;
    }

    /// <summary>The agents the host offers, as of the last connection.</summary>
    public IReadOnlyList<AgentMessage> AgentsOf(string hostId)
    {
        lock (_gate) return _links.GetValueOrDefault(hostId)?.Agents ?? [];
    }

    public string HostName(string hostId) => Hosts.Find(hostId)?.Name ?? hostId;

    /// <summary>What this device calls itself to a host.</summary>
    public DeviceDescription Description => _description();

    /// <summary>
    /// The connection to a paired host, made when there is none. From now on the host is wanted: a connection that
    /// drops is made again. Throws when the host cannot be reached now (it is still retried in the background).
    /// </summary>
    public Task<RemoteHostClient> ConnectAsync(string hostId)
    {
        Task<RemoteHostClient> connecting;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_links.TryGetValue(hostId, out var link)) _links[hostId] = link = new Link();
            link.Wanted = true;
            if (link.Client is { } client) return Task.FromResult(client);
            if (link.Connecting is { } pending) return pending;
            connecting = link.Connecting = ConnectCoreAsync(hostId, link);
        }
        StateChanged?.Invoke(hostId);
        return connecting;
    }

    /// <summary>
    /// Pairs with a host: <paramref name="target"/> is where it listens (<c>192.168.1.5:47600</c>, <c>ws://…</c>), then
    /// <paramref name="onPending"/> gets the six digits to compare with the ones on the host; or a pairing link, which
    /// needs no comparing. Returns the host, now remembered. Throws <see cref="HandshakeException"/> when the host
    /// refuses, <see cref="FormatException"/> when <paramref name="target"/> is neither.
    /// </summary>
    public async Task<PairedPeer> PairAsync(string target, Action<string>? onPending, CancellationToken cancellationToken = default)
    {
        var link = PairingLink.TryParse(target);
        var addresses = link is not null
            ? link.Addresses.Select(ParseAddress).OfType<Uri>().ToList()
            : ParseAddress(target) is { } typed ? [typed] : [];
        if (addresses.Count == 0) throw new FormatException("Not an address or a pairing link.");
        var identity = await _identity().ConfigureAwait(false);
        Exception? last = null;
        foreach (var address in addresses)
        {
            IMessageTransport transport;
            try { transport = await Dial(address, cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { last = e; continue; }
            var expected = link is null ? PeerExpectation.AnyForPairing : PeerExpectation.WithFingerprint(link.Fingerprint);
            var options = new ConnectOptions { RequestPairing = true, Token = link?.Token ?? default, OnPending = onPending };
            var client = await RemoteHostClient.ConnectAsync(transport, identity, Description, expected, _hello, options, cancellationToken).ConfigureAwait(false);
            // The way through the relay is not an address of the host's: it is tried for every host while a relay is set up.
            var peer = Hosts.Add(client.Host, client.HostDescription, address.Scheme == RelayScheme ? [] : [address.ToString()]);
            Link hostLink;
            lock (_gate)
            {
                if (!_links.TryGetValue(peer.Id, out var existing)) _links[peer.Id] = hostLink = new Link();
                else hostLink = existing;
            }
            Adopt(peer.Id, hostLink, client, address.Scheme == RelayScheme);
            return peer;
        }
        throw new IOException(last?.Message ?? "The host cannot be reached.", last);
    }

    /// <summary>Ends every connection and stops asking for the hosts; who is paired is kept. Connecting again starts over.</summary>
    public async Task DisconnectAllAsync()
    {
        List<(string Id, RemoteHostClient? Client)> links;
        lock (_gate)
        {
            links = [.. _links.Select(link => (link.Key, link.Value.Client))];
            _links.Clear();
        }
        foreach (var (id, client) in links)
        {
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
            StateChanged?.Invoke(id);
        }
    }

    /// <summary>
    /// Ends the connection to one host and stops asking for it; who is paired is kept. Something that shows the host
    /// (a project, a terminal) connects it again when it is next needed.
    /// </summary>
    public async Task DisconnectAsync(string hostId)
    {
        Task<RemoteHostClient>? connecting;
        RemoteHostClient? client;
        lock (_gate)
        {
            if (!_links.Remove(hostId, out var link)) return;
            link.Wanted = false;
            link.Retrying = false;
            link.Closed = true;
            client = link.Client;
            link.Client = null;
            connecting = link.Connecting;
        }
        StateChanged?.Invoke(hostId);
        // A connection being made is taken when it arrives, so that it can be closed too.
        if (client is null && connecting is not null)
        {
            try { client = await connecting.ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or HandshakeException or RemoteException
                                          or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException
                                          or OperationCanceledException or ObjectDisposedException) { }
        }
        if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Forgets a host and ends the connection to it.</summary>
    public async Task RemoveHostAsync(string hostId)
    {
        await DisconnectAsync(hostId).ConfigureAwait(false);
        Hosts.Remove(hostId);
        StateChanged?.Invoke(hostId);
    }

    /// <summary>
    /// An address as people type it: <c>host:port</c>, <c>ws://host:port</c>, or a host alone (then the default port);
    /// or a host through the relay (<c>octopus-relay://id</c>, as a pairing link carries it). Null when it is none of these.
    /// </summary>
    public static Uri? ParseAddress(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Contains(' ')) return null;
        if (trimmed.StartsWith(RelayScheme + "://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var relay) && relay.Host.Length == 26 && relay.Host.All(char.IsAsciiLetterOrDigit) ? relay : null;
        }
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "ws://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss") || uri.Host.Length == 0) return null;
        return uri.IsDefaultPort && uri.Scheme == "ws"
            ? new UriBuilder(uri) { Port = Octopus.Core.Settings.RemoteSettings.DefaultLanPort }.Uri
            : uri;
    }

    public void Dispose()
    {
        List<RemoteHostClient> clients;
        lock (_gate)
        {
            _disposed = true;
            clients = [.. _links.Values.Select(link => link.Client).OfType<RemoteHostClient>()];
            _links.Clear();
        }
        foreach (var client in clients) _ = client.DisposeAsync().AsTask();
    }

    private async Task<RemoteHostClient> ConnectCoreAsync(string hostId, Link link)
    {
        // Off the caller's thread: the first await may be the keystore.
        await Task.Yield();
        try
        {
            var host = Hosts.Find(hostId) ?? throw new IOException("This computer is not paired with that host.");
            var key = host.Key() ?? throw new IOException("The host's stored key is unreadable; pair with it again.");
            var identity = await _identity().ConfigureAwait(false);
            // Every way to the host: the addresses it was reached at before, then the relay when this computer uses one.
            // They are tried side by side, each a little after the one before it, and the first to answer is taken: a
            // laptop away from home does not sit out a dead local address before the relay gets its turn.
            var ways = host.Addresses.ToList().Select(text => (Text: (string?)text, Address: ParseAddress(text))).Where(way => way.Address is { Scheme: not RelayScheme }).ToList();
            if (_relay is not null && await _relay.IsUsableAsync().ConfigureAwait(false)) ways.Add((null, RelayAddress(hostId)));
            using var all = new CancellationTokenSource();
            var attempts = ways.Select((way, index) => AttemptAsync(way.Address!, way.Text, index)).ToList();
            Exception? last = null;
            while (attempts.Count > 0)
            {
                var done = await Task.WhenAny(attempts).ConfigureAwait(false);
                attempts.Remove(done);
                if (!done.IsCompletedSuccessfully)
                {
                    last = done.IsCanceled ? new OperationCanceledException() : done.Exception!.InnerException;
                    continue;
                }
                all.Cancel();
                // A slower way that still got through is not needed.
                foreach (var other in attempts)
                {
                    _ = other.ContinueWith(static late =>
                    {
                        if (late.IsCompletedSuccessfully) _ = late.Result.Client.DisposeAsync().AsTask();
                        else _ = late.Exception;
                    }, TaskScheduler.Default);
                }
                var (client, text, address) = done.Result;
                Hosts.Touch(hostId, client.HostDescription.Name, text);
                Adopt(hostId, link, client, address.Scheme == RelayScheme);
                return client;
            }
            throw new IOException(last switch
            {
                OperationCanceledException => "The host did not answer in time.",
                null => "No address is known for this host.",
                _ => last.Message,
            }, last);

            async Task<(RemoteHostClient Client, string? Text, Uri Address)> AttemptAsync(Uri address, string? text, int index)
            {
                try
                {
                    if (index > 0) await Task.Delay(Stagger * index, all.Token).ConfigureAwait(false);
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(all.Token);
                    limit.CancelAfter(ConnectLimit);
                    var transport = await Dial(address, limit.Token).ConfigureAwait(false);
                    var client = await RemoteHostClient.ConnectAsync(transport, identity, Description, PeerExpectation.Paired(key), _hello, null, limit.Token).ConfigureAwait(false);
                    return (client, text, address);
                }
                catch (Exception e) when (e is HandshakeException or RemoteException or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException)
                {
                    // Told as one kind of failure to the loop above (which the callers of ConnectAsync expect).
                    throw new IOException(e.Message, e);
                }
            }
        }
        catch (Exception e)
        {
            bool retry;
            lock (_gate)
            {
                link.Connecting = null;
                link.Error = e.Message;
                retry = link.Wanted && !link.Retrying && !_disposed && _links.ContainsKey(hostId);
                if (retry) link.Retrying = true;
            }
            if (retry) _ = RetryAsync(hostId, link);
            StateChanged?.Invoke(hostId);
            throw;
        }
    }

    // A live client for a host: remembered, listened to, and replaced when it ends.
    private void Adopt(string hostId, Link link, RemoteHostClient client, bool throughRelay = false)
    {
        lock (_gate)
        {
            // The person disconnected while it was being made: it is closed rather than kept.
            if (link.Closed || !_links.TryGetValue(hostId, out var current) || !ReferenceEquals(current, link))
            {
                _ = client.DisposeAsync().AsTask();
                return;
            }
            link.Client = client;
            link.ThroughRelay = throughRelay;
            link.Connecting = null;
            link.Error = null;
            link.Retrying = false;
        }
        client.WorkspaceChanged += () => WorkspaceChanged?.Invoke(hostId);
        client.ItemChanged += item => ItemChanged?.Invoke(hostId, item);
        _ = client.Completion.ContinueWith(_ => OnLost(hostId, client), TaskScheduler.Default);
        _ = LoadAgentsAsync(hostId, client);
        StateChanged?.Invoke(hostId);
    }

    private async Task LoadAgentsAsync(string hostId, RemoteHostClient client)
    {
        try
        {
            var agents = await client.ListAgentsAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (_links.TryGetValue(hostId, out var link) && ReferenceEquals(link.Client, client)) link.Agents = agents;
            }
            StateChanged?.Invoke(hostId);
        }
        catch (Exception e) when (e is IOException or RemoteException) { }
    }

    private void OnLost(string hostId, RemoteHostClient client)
    {
        bool retry;
        Link? link;
        lock (_gate)
        {
            if (!_links.TryGetValue(hostId, out link) || !ReferenceEquals(link.Client, client)) return;
            link.Client = null;
            link.Error = "The connection to the host was lost.";
            retry = link.Wanted && !link.Retrying && !_disposed;
            if (retry) link.Retrying = true;
        }
        StateChanged?.Invoke(hostId);
        if (retry) _ = RetryAsync(hostId, link);
    }

    // Again and again, with longer pauses, until it works or the host is no longer wanted: never gives up by itself.
    private async Task RetryAsync(string hostId, Link link)
    {
        for (var attempt = 0; ; attempt++)
        {
            await Task.Delay(RetryPauses[Math.Min(attempt, RetryPauses.Length - 1)]).ConfigureAwait(false);
            Task<RemoteHostClient> connecting;
            lock (_gate)
            {
                if (_disposed || !_links.TryGetValue(hostId, out var current) || !ReferenceEquals(current, link) || link.Client is not null)
                {
                    link.Retrying = false;
                    return;
                }
                connecting = link.Connecting ??= ConnectCoreAsync(hostId, link);
            }
            try
            {
                await connecting.ConfigureAwait(false);
                return; // Adopt cleared Retrying
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }
}
