using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Octopus.Acp.Transport;

/// <summary>
/// Full-duplex JSON-RPC 2.0 ACP transport over WebSocket (<c>ws://</c> / <c>wss://</c>),
/// following the ACP Remote WebSocket transport profile.
/// </summary>
public sealed class AcpWebSocketTransport : IAcpTransport
{
    private readonly Uri _endpoint;
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private volatile bool _connected;
    private int _disposed;

    public AcpWebSocketTransport(
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        int maxLineBytes = NdjsonLineFramer.DefaultMaxLineBytes,
        ClientWebSocket? socket = null)
    {
        if (endpoint.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException($"ACP WebSocket endpoint must use ws:// or wss:// scheme (got '{endpoint.Scheme}').", nameof(endpoint));
        }
        _endpoint = endpoint;
        MaxLineBytes = maxLineBytes;
        _socket = socket ?? new ClientWebSocket();
        if (socket is null && headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                if (!string.IsNullOrWhiteSpace(k))
                {
                    _socket.Options.SetRequestHeader(k, v);
                }
            }
        }
    }

    public int MaxLineBytes { get; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connected || _socket.State == WebSocketState.Open)
        {
            _connected = true;
            return;
        }
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connected || _socket.State == WebSocketState.Open)
            {
                _connected = true;
                return;
            }
            await _socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false);
            _connected = true;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task SendAsync(string jsonLine, CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(jsonLine);
        if (bytes.Length > MaxLineBytes)
        {
            throw new AcpException($"Outgoing ACP WebSocket frame exceeds max size ({bytes.Length} bytes).");
        }
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunReceiveLoopAsync(
        Action<JsonDocument> onMessage,
        Action<string> onOtherLine,
        Action<OversizedAcpLine> onOversized,
        CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken).ConfigureAwait(false);

        var chunk = new byte[16 * 1024];
        using var messageBuffer = new MemoryStream(4096);
        var prefix = new List<byte>(4096);

        while (!cancellationToken.IsCancellationRequested && _socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            messageBuffer.SetLength(0);
            prefix.Clear();
            var oversized = false;
            long totalBytes = 0;

            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                if (result.Count == 0) continue;

                totalBytes += result.Count;
                if (!oversized && messageBuffer.Length + result.Count > MaxLineBytes)
                {
                    oversized = true;
                    if (messageBuffer.TryGetBuffer(out var existing))
                    {
                        CapturePrefix(prefix, existing.AsSpan());
                    }
                    CapturePrefix(prefix, chunk.AsSpan(0, result.Count));
                    messageBuffer.SetLength(0);
                }
                else if (oversized)
                {
                    CapturePrefix(prefix, chunk.AsSpan(0, result.Count));
                }
                else
                {
                    messageBuffer.Write(chunk, 0, result.Count);
                }
            }
            while (!result.EndOfMessage);

            if (oversized)
            {
                var pfx = Encoding.UTF8.GetString([.. prefix]);
                onOversized(new OversizedAcpLine(pfx, totalBytes, NdjsonLineFramer.TryExtractId(pfx)));
                continue;
            }

            if (messageBuffer.Length == 0) continue;

            DispatchFrame(messageBuffer, onMessage, onOtherLine);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", cts.Token).ConfigureAwait(false);
            }
        }
        catch { /* Best-effort WebSocket close. */ }
        _socket.Dispose();
        _connectGate.Dispose();
    }

    private static void DispatchFrame(MemoryStream buffer, Action<JsonDocument> onMessage, Action<string> onOtherLine)
    {
        var raw = buffer.TryGetBuffer(out var seg)
            ? new ReadOnlyMemory<byte>(seg.Array!, seg.Offset, (int)buffer.Length)
            : buffer.ToArray();
        try
        {
            var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                onMessage(doc);
                return;
            }
            doc.Dispose();
        }
        catch (JsonException) { }

        var text = Encoding.UTF8.GetString(raw.Span).Trim();
        if (text.Length > 0)
        {
            onOtherLine(text);
        }
    }

    private static void CapturePrefix(List<byte> target, ReadOnlySpan<byte> bytes)
    {
        var remaining = 4096 - target.Count;
        if (remaining <= 0) return;
        var take = Math.Min(remaining, bytes.Length);
        for (var i = 0; i < take; i++) target.Add(bytes[i]);
    }
}
