using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Octopus.Remote.Transport;

/// <summary>A WebSocket as a transport: one binary WebSocket message per message.</summary>
public sealed class WebSocketTransport(WebSocket socket, IDisposable? owner = null) : IMessageTransport
{
    /// <summary>The path a host serves and a device asks for.</summary>
    public const string Path = "/octopus/v1";
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    /// <summary>Connects to a host at <c>ws://host:port</c> (or <c>wss://</c> for the relay).</summary>
    public static async Task<WebSocketTransport> ConnectAsync(Uri address, CancellationToken cancellationToken = default)
    {
        var client = new ClientWebSocket();
        client.Options.KeepAliveInterval = KeepAlive;
        try
        {
            await client.ConnectAsync(new UriBuilder(address) { Path = Path }.Uri, cancellationToken).ConfigureAwait(false);
            return new WebSocketTransport(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        try
        {
            await socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            throw new IOException("The connection is closed.", e);
        }
    }

    public async ValueTask<byte[]?> ReceiveAsync(int maxLength, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[Math.Min(maxLength, 16 * 1024)];
        var length = 0;
        while (true)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                return null; // the other side went away without a closing handshake
            }
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Binary)
            {
                socket.Abort();
                throw new IOException("Only binary messages are expected.");
            }
            length += result.Count;
            if (result.EndOfMessage) return buffer.AsSpan(0, length).ToArray();
            if (length == buffer.Length)
            {
                if (buffer.Length >= maxLength)
                {
                    socket.Abort();
                    throw new IOException($"A message exceeds the limit of {maxLength} bytes.");
                }
                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, maxLength));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, limit.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException or InvalidOperationException) { }
        socket.Dispose();
        owner?.Dispose();
    }
}

/// <summary>A device that connected to a <see cref="WebSocketListener"/>.</summary>
public sealed record AcceptedTransport(IMessageTransport Transport, IPEndPoint? Remote);

/// <summary>
/// The host's listening side: plain TCP with the WebSocket upgrade done by hand, so the desktop app carries no web
/// server. Whoever connects has proven nothing yet: the request is read with a size limit and a deadline, and anything
/// that is not a proper upgrade for <see cref="WebSocketTransport.Path"/> is answered with an error and dropped.
/// </summary>
public sealed class WebSocketListener : IAsyncDisposable
{
    private const int MaxRequestBytes = 8 * 1024;
    private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private static readonly TimeSpan UpgradeDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    private readonly TcpListener _listener;
    private readonly Channel<AcceptedTransport> _ready = Channel.CreateBounded<AcceptedTransport>(16);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <param name="port">0 lets the system pick one (see <see cref="Port"/>).</param>
    public WebSocketListener(IPAddress address, int port) => _listener = new TcpListener(address, port);

    /// <summary>Which addresses may connect; the default lets in this computer and private networks only (<see cref="LocalNetwork.IsLocal"/>).</summary>
    public Func<IPAddress, bool> AllowRemote { get; init; } = LocalNetwork.IsLocal;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The next device whose upgrade succeeded.</summary>
    public async Task<AcceptedTransport> AcceptAsync(CancellationToken cancellationToken = default) =>
        await _ready.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        _ready.Writer.TryComplete();
        while (_ready.Reader.TryRead(out var pending)) await pending.Transport.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            // Each upgrade on its own: a client that sends its request slowly holds up nobody else.
            _ = Task.Run(() => UpgradeAsync(client));
        }
    }

    private async Task UpgradeAsync(TcpClient client)
    {
        try
        {
            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            if (remote is null || !AllowRemote(remote.Address))
            {
                client.Dispose();
                return;
            }
            client.NoDelay = true;
            var stream = client.GetStream();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            deadline.CancelAfter(UpgradeDeadline);
            var key = await ReadUpgradeRequestAsync(stream, deadline.Token).ConfigureAwait(false);
            if (key is null)
            {
                await stream.WriteAsync("HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), deadline.Token).ConfigureAwait(false);
                client.Dispose();
                return;
            }
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + AcceptGuid)));
            var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), deadline.Token).ConfigureAwait(false);
            var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = KeepAlive });
            var accepted = new AcceptedTransport(new WebSocketTransport(socket, client), remote);
            if (!_ready.Writer.TryWrite(accepted)) await accepted.Transport.DisposeAsync().ConfigureAwait(false); // nobody is accepting fast enough
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            client.Dispose();
        }
    }

    /// <summary>The request's Sec-WebSocket-Key when it is a WebSocket upgrade for our path; null for anything else.</summary>
    private static async Task<string?> ReadUpgradeRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        // Byte by byte up to the blank line: nothing after the request may be taken from the stream.
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            length++;
            if (length >= 4 && buffer.AsSpan(length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
        }
        if (length == buffer.Length) return null;
        var lines = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var request = lines[0].Split(' ');
        if (request.Length != 3 || request[0] != "GET" || request[1] != WebSocketTransport.Path || request[2] != "HTTP/1.1") return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        if (!headers.TryGetValue("Upgrade", out var upgrade) || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)) return null;
        if (!headers.TryGetValue("Connection", out var connection) || !connection.Contains("upgrade", StringComparison.OrdinalIgnoreCase)) return null;
        if (!headers.TryGetValue("Sec-WebSocket-Version", out var version) || version != "13") return null;
        return headers.TryGetValue("Sec-WebSocket-Key", out var key) && key.Length is > 0 and <= 64 ? key : null;
    }
}

/// <summary>Which addresses count as "this computer or its own network" for a host that is open to the local network only.</summary>
public static class LocalNetwork
{
    /// <summary>Loopback, the private IPv4 ranges, link-local addresses and IPv6 unique local addresses.</summary>
    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            address.TryWriteBytes(b, out _);
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Span<byte> b = stackalloc byte[16];
            address.TryWriteBytes(b, out _);
            return address.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
        }
        return false;
    }
}
