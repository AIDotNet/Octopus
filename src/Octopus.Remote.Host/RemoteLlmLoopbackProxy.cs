using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Octopus.Remote.Protocol;

namespace Octopus.Remote.Host;

/// <summary>
/// Loopback-only HTTP/1.1 reverse proxy running on the remote host (<c>127.0.0.1</c> ephemeral port).
/// Local agent CLIs (Claude Code, Codex, OpenCode, Gemini, etc.) send requests to <c>http://127.0.0.1:&lt;port&gt;/t/&lt;grantToken&gt;/...</c>.
/// This proxy strips placeholder auth headers and forwards every request over the authenticated Octopus <see cref="RemoteConnection"/>
/// back to the controller, streaming SSE/JSON responses chunk-by-chunk with zero buffering (design/20 §6).
/// No real API keys or upstream URLs ever exist on the remote host.
/// </summary>
public sealed class RemoteLlmLoopbackProxy : IAsyncDisposable
{
    /// <summary>High bit reserving host-initiated LLM tunnel stream channels so they never collide with client channels.</summary>
    public const uint HostChannelBit = 0x8000_0000u;

    /// <summary>Request bodies up to 32 KiB are inlined in <see cref="LlmProxyOpenMessage.InlineBody"/> to save a stream round-trip.</summary>
    public const int InlineBodyLimitBytes = 32 * 1024;

    /// <summary>Upper bound on a single tunneled HTTP request body (64 MiB).</summary>
    public const int MaxRequestBodyBytes = 64 * 1024 * 1024;

    private const int MaxHeaderBytes = 64 * 1024;

    private static readonly HashSet<string> StrippedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host",
        "connection",
        "keep-alive",
        "transfer-encoding",
        "te",
        "trailer",
        "upgrade",
        "proxy-authorization",
        "proxy-connection",
        "content-length",
        "expect",
        "accept-encoding",
        "authorization",
        "x-api-key",
        "x-goog-api-key",
    };

    private static readonly HashSet<string> StrippedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection",
        "keep-alive",
        "transfer-encoding",
        "te",
        "trailer",
        "upgrade",
        "content-length",
        "content-encoding",
    };

    private readonly object _startGate = new();
    private readonly ConcurrentDictionary<string, GrantEntry> _grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<uint, ActiveCall> _activeCalls = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _port;
    private int _nextChannel;
    private bool _disposed;

    /// <summary>How long an incoming CLI request waits for the controller to reconnect before returning HTTP 503.</summary>
    public TimeSpan ReconnectWaitTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a grant remains rebindable after its controller disconnects.</summary>
    public TimeSpan DisconnectGracePeriod { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The loopback TCP port once started, or 0 before the first grant is registered.</summary>
    public int Port => Volatile.Read(ref _port);

    /// <summary>
    /// Ensures the <c>127.0.0.1</c> listener is running, registers <paramref name="grantToken"/> bound to
    /// <paramref name="deviceId"/> and <paramref name="connection"/>, and returns the loopback base URL for CLI materialization.
    /// </summary>
    public string RegisterGrant(string grantToken, string deviceId, RemoteConnection connection)
    {
        if (string.IsNullOrWhiteSpace(grantToken)) throw new ArgumentException("Grant token is required.", nameof(grantToken));
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("Device id is required.", nameof(deviceId));
        ArgumentNullException.ThrowIfNull(connection);

        EnsureStarted();
        PruneExpiredGrants();

        var entry = new GrantEntry(grantToken, deviceId, connection);
        _grants[grantToken] = entry;
        return $"http://127.0.0.1:{Port}/t/{grantToken}";
    }

    /// <summary>Rebinds existing grants owned by <paramref name="deviceId"/> to a freshly reconnected <see cref="RemoteConnection"/>.</summary>
    public int RebindGrants(string deviceId, RemoteConnection newConnection, IEnumerable<string> grantTokens)
    {
        PruneExpiredGrants();
        var count = 0;
        foreach (var token in grantTokens)
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            if (_grants.TryGetValue(token, out var entry) && string.Equals(entry.DeviceId, deviceId, StringComparison.Ordinal))
            {
                entry.Rebind(newConnection);
                count++;
            }
        }
        return count;
    }

    /// <summary>Immediately revokes <paramref name="grantToken"/> if owned by <paramref name="deviceId"/>.</summary>
    public bool RevokeGrant(string deviceId, string grantToken)
    {
        if (_grants.TryGetValue(grantToken, out var entry) && string.Equals(entry.DeviceId, deviceId, StringComparison.Ordinal))
        {
            if (_grants.TryRemove(grantToken, out _))
            {
                entry.Revoke();
                return true;
            }
        }
        return false;
    }

    /// <summary>Marks every grant bound to <paramref name="connection"/> as temporarily offline (entering reconnect grace).</summary>
    public void OnConnectionClosed(RemoteConnection connection)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _grants.Values)
        {
            entry.DetachIf(connection, now);
        }
        foreach (var call in _activeCalls.Values)
        {
            if (ReferenceEquals(call.Connection, connection))
            {
                call.Fail(new IOException("The controller connection closed while streaming the response."));
            }
        }
    }

    /// <summary>Routes incoming high-bit binary stream frames (<see cref="FrameType.StreamData"/>/<see cref="FrameType.StreamEnd"/>) from the controller.</summary>
    public void OnFrame(FrameHeader header, ReadOnlyMemory<byte> payload)
    {
        if ((header.Channel & HostChannelBit) == 0) return;
        if (!_activeCalls.TryGetValue(header.Channel, out var call)) return;

        switch (header.Type)
        {
            case FrameType.StreamData:
                call.PushResponseChunk(payload.ToArray());
                break;
            case FrameType.StreamEnd:
                var status = payload.Length > 0 ? payload.Span[0] : (byte)0;
                call.CompleteResponse(status == 0 ? null : new IOException("Upstream response stream ended with an error."));
                break;
        }
    }

    private void EnsureStarted()
    {
        lock (_startGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null) return;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _listener = listener;
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        }
    }

    private void PruneExpiredGrants()
    {
        var cutoff = DateTimeOffset.UtcNow - DisconnectGracePeriod;
        foreach (var (token, entry) in _grants)
        {
            if (entry.IsExpired(cutoff) && _grants.TryRemove(token, out _))
            {
                entry.Revoke();
            }
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => HandleClientAsync(client, cancellationToken));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken shutdownToken)
    {
        using (client)
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();
            try
            {
                var parsed = await ReadHttpRequestAsync(stream, shutdownToken).ConfigureAwait(false);
                if (parsed is null) return;

                if (!TryExtractGrantAndPath(parsed.Target, out var grantToken, out var relativePath))
                {
                    await WriteJsonErrorAsync(stream, 404, "Not Found", "invalid_tunnel_path", "Unknown tunnel endpoint.", shutdownToken).ConfigureAwait(false);
                    return;
                }

                PruneExpiredGrants();
                if (!_grants.TryGetValue(grantToken, out var grant))
                {
                    await WriteJsonErrorAsync(stream, 401, "Unauthorized", "invalid_grant", "Tunnel grant is missing, revoked, or expired.", shutdownToken).ConfigureAwait(false);
                    return;
                }

                var connection = await grant.GetOrWaitConnectionAsync(ReconnectWaitTimeout, shutdownToken).ConfigureAwait(false);
                if (connection is null)
                {
                    await WriteJsonErrorAsync(
                        stream,
                        503,
                        "Service Unavailable",
                        "controller_offline",
                        "Octopus controller is temporarily disconnected; retry once the remote session reconnects.",
                        shutdownToken).ConfigureAwait(false);
                    return;
                }

                await ForwardThroughTunnelAsync(stream, connection, grantToken, parsed.Method, relativePath, parsed.Headers, parsed.Body, shutdownToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Local CLI disconnected or proxy shutting down.
            }
        }
    }

    private async Task ForwardThroughTunnelAsync(
        NetworkStream clientStream,
        RemoteConnection connection,
        string grantToken,
        string method,
        string relativePath,
        Dictionary<string, string> headers,
        byte[] body,
        CancellationToken shutdownToken)
    {
        var channel = AllocateChannel();
        var call = new ActiveCall(connection);
        _activeCalls[channel] = call;

        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        var streamBody = body.Length > InlineBodyLimitBytes;
        var openMsg = new LlmProxyOpenMessage
        {
            GrantToken = grantToken,
            Channel = channel,
            Method = method,
            RelativePath = relativePath,
            Headers = headers,
            InlineBody = !streamBody && body.Length > 0 ? Convert.ToBase64String(body) : null,
            HasBodyStream = streamBody,
        };

        try
        {
            var openTask = connection.RequestAsync(
                RemoteMethods.LlmProxyOpen,
                openMsg,
                RemoteJsonContext.Default.LlmProxyOpenMessage,
                RemoteJsonContext.Default.LlmProxyOpenResultMessage,
                callCts.Token);

            if (streamBody)
            {
                await SendBodyStreamAsync(connection, channel, body, callCts.Token).ConfigureAwait(false);
            }

            LlmProxyOpenResultMessage head;
            try
            {
                head = await openTask.ConfigureAwait(false);
            }
            catch (RemoteException ex)
            {
                var status = ex.Code == RemoteMethods.NotAllowed ? 403 : ex.Code == RemoteMethods.NotFound ? 404 : 502;
                await WriteJsonErrorAsync(clientStream, status, ReasonPhrase(status), "tunnel_rejected", ex.Message, shutdownToken).ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                await WriteJsonErrorAsync(clientStream, 503, "Service Unavailable", "controller_disconnected", "Lost connection to Octopus controller.", shutdownToken).ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrEmpty(head.ErrorMessage) && head.StatusCode >= 400)
            {
                await WriteJsonErrorAsync(clientStream, head.StatusCode, ReasonPhrase(head.StatusCode), "tunnel_error", head.ErrorMessage!, shutdownToken).ConfigureAwait(false);
                return;
            }

            // Write HTTP/1.1 response head with Transfer-Encoding: chunked for zero-buffer SSE/streaming.
            var headerBuilder = new StringBuilder(256);
            headerBuilder.Append("HTTP/1.1 ").Append(head.StatusCode).Append(' ').Append(ReasonPhrase(head.StatusCode)).Append("\r\n");
            foreach (var (key, value) in head.Headers)
            {
                if (StrippedResponseHeaders.Contains(key)) continue;
                if (key.Contains('\r') || key.Contains('\n') || value.Contains('\r') || value.Contains('\n')) continue;
                headerBuilder.Append(key).Append(": ").Append(value).Append("\r\n");
            }
            headerBuilder.Append("Transfer-Encoding: chunked\r\n");
            headerBuilder.Append("Connection: close\r\n\r\n");
            var headBytes = Encoding.ASCII.GetBytes(headerBuilder.ToString());
            await clientStream.WriteAsync(headBytes, callCts.Token).ConfigureAwait(false);
            await clientStream.FlushAsync(callCts.Token).ConfigureAwait(false);

            // Pump streamed response chunks directly to the local CLI with immediate flush per chunk.
            try
            {
                await foreach (var chunk in call.ResponseChunks.ReadAllAsync(callCts.Token).ConfigureAwait(false))
                {
                    if (chunk.Length == 0) continue;
                    var prefix = Encoding.ASCII.GetBytes($"{chunk.Length:x}\r\n");
                    await clientStream.WriteAsync(prefix, callCts.Token).ConfigureAwait(false);
                    await clientStream.WriteAsync(chunk, callCts.Token).ConfigureAwait(false);
                    await clientStream.WriteAsync("\r\n"u8.ToArray(), callCts.Token).ConfigureAwait(false);
                    await clientStream.FlushAsync(callCts.Token).ConfigureAwait(false);
                }
                await clientStream.WriteAsync("0\r\n\r\n"u8.ToArray(), callCts.Token).ConfigureAwait(false);
                await clientStream.FlushAsync(callCts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
            {
                // Local CLI aborted or disconnected mid-stream: cancel upstream on controller!
                _ = NotifyCancelAsync(connection, channel);
            }
        }
        finally
        {
            _activeCalls.TryRemove(channel, out _);
        }
    }

    private static async Task SendBodyStreamAsync(RemoteConnection connection, uint channel, byte[] body, CancellationToken cancellationToken)
    {
        const int chunkSize = 32 * 1024;
        var offset = 0;
        while (offset < body.Length)
        {
            var count = Math.Min(chunkSize, body.Length - offset);
            await connection.SendDataAsync(FrameType.StreamData, channel, body.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            offset += count;
        }
        await connection.SendDataAsync(FrameType.StreamEnd, channel, new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task NotifyCancelAsync(RemoteConnection connection, uint channel)
    {
        try
        {
            await connection.NotifyAsync(RemoteMethods.LlmProxyCancel, new LlmProxyCancelMessage { Channel = channel }, RemoteJsonContext.Default.LlmProxyCancelMessage).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private uint AllocateChannel()
    {
        var seq = (uint)Interlocked.Increment(ref _nextChannel) & 0x7FFF_FFFFu;
        if (seq == 0) seq = (uint)Interlocked.Increment(ref _nextChannel) & 0x7FFF_FFFFu;
        return HostChannelBit | seq;
    }

    private static bool TryExtractGrantAndPath(string target, out string grantToken, out string relativePath)
    {
        grantToken = "";
        relativePath = "/";
        if (string.IsNullOrEmpty(target) || !target.StartsWith("/t/", StringComparison.Ordinal))
        {
            return false;
        }
        var rest = target[3..];
        var sep = rest.IndexOfAny(['/', '?']);
        if (sep < 0)
        {
            grantToken = rest;
            relativePath = "/";
        }
        else
        {
            grantToken = rest[..sep];
            relativePath = rest[sep] == '?' ? "/" + rest[sep..] : rest[sep..];
        }
        return grantToken.Length >= 8 && grantToken.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }

    private static async Task<ParsedHttpRequest?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var headerBuffer = new MemoryStream(1024);
        var readBuf = new byte[4096];
        var headerEnd = -1;
        var leftoverOffset = 0;
        var leftoverLength = 0;
        byte[]? leftoverBuffer = null;

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(readBuf, cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            headerBuffer.Write(readBuf, 0, read);
            if (headerBuffer.Length > MaxHeaderBytes) throw new IOException("HTTP request headers too large.");

            var span = headerBuffer.GetBuffer().AsSpan(0, (int)headerBuffer.Length);
            headerEnd = span.IndexOf("\r\n\r\n"u8);
            if (headerEnd >= 0)
            {
                var bodyStart = headerEnd + 4;
                leftoverLength = span.Length - bodyStart;
                if (leftoverLength > 0)
                {
                    leftoverBuffer = span[bodyStart..].ToArray();
                    leftoverOffset = 0;
                }
            }
        }

        var headerText = Encoding.ASCII.GetString(headerBuffer.GetBuffer(), 0, headerEnd);
        var lines = headerText.Split("\r\n");
        if (lines.Length == 0) return null;

        var requestParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 2) return null;
        var method = requestParts[0].ToUpperInvariant();
        var target = requestParts[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long contentLength = 0;
        var isChunked = false;
        var expectContinue = false;

        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            var name = lines[i][..colon].Trim();
            var value = lines[i][(colon + 1)..].Trim();
            if (name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(value, out contentLength) || contentLength < 0)
                {
                    contentLength = 0;
                }
            }
            else if (name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase)
                     && value.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                isChunked = true;
            }
            else if (name.Equals("expect", StringComparison.OrdinalIgnoreCase)
                     && value.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                expectContinue = true;
            }

            if (!StrippedRequestHeaders.Contains(name))
            {
                headers[name] = value;
            }
        }

        if (expectContinue)
        {
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        byte[] body;
        if (isChunked)
        {
            body = await ReadChunkedBodyAsync(stream, leftoverBuffer, leftoverOffset, leftoverLength, cancellationToken).ConfigureAwait(false);
        }
        else if (contentLength > 0)
        {
            if (contentLength > MaxRequestBodyBytes) throw new IOException("Request body exceeds maximum allowed tunnel size.");
            body = new byte[contentLength];
            var copied = 0;
            if (leftoverBuffer is not null && leftoverLength > 0)
            {
                var toCopy = (int)Math.Min(contentLength, leftoverLength);
                Buffer.BlockCopy(leftoverBuffer, leftoverOffset, body, 0, toCopy);
                copied = toCopy;
            }
            while (copied < contentLength)
            {
                var n = await stream.ReadAsync(body.AsMemory(copied, (int)contentLength - copied), cancellationToken).ConfigureAwait(false);
                if (n == 0) throw new IOException("Unexpected EOF while reading HTTP request body.");
                copied += n;
            }
        }
        else
        {
            body = [];
        }

        return new ParsedHttpRequest(method, target, headers, body);
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(NetworkStream stream, byte[]? initial, int initialOffset, int initialLength, CancellationToken cancellationToken)
    {
        var reader = new BufferedStreamReader(stream, initial, initialOffset, initialLength);
        using var output = new MemoryStream();
        while (true)
        {
            var sizeLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (sizeLine is null) break;
            var semi = sizeLine.IndexOf(';');
            var hex = (semi >= 0 ? sizeLine[..semi] : sizeLine).Trim();
            if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var chunkSize) || chunkSize < 0)
            {
                throw new IOException("Malformed chunked request body.");
            }
            if (chunkSize == 0)
            {
                // Consume trailing CRLF (and any trailers until blank line).
                while (true)
                {
                    var trailer = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(trailer)) break;
                }
                break;
            }
            if (output.Length + chunkSize > MaxRequestBodyBytes)
            {
                throw new IOException("Chunked request body exceeds maximum allowed tunnel size.");
            }
            var chunk = new byte[chunkSize];
            await reader.ReadExactAsync(chunk, cancellationToken).ConfigureAwait(false);
            output.Write(chunk, 0, chunkSize);
            // Consume chunk-terminating CRLF.
            await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private static async Task WriteJsonErrorAsync(NetworkStream stream, int statusCode, string reason, string errorType, string message, CancellationToken cancellationToken)
    {
        var escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        var json = $"{{\"error\":{{\"type\":\"{errorType}\",\"message\":\"{escaped}\"}}}}";
        var body = Encoding.UTF8.GetBytes(json);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        201 => "Created",
        202 => "Accepted",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "OK",
    };

    public ValueTask DisposeAsync()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        lock (_startGate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            listener = _listener;
            _listener = null;
            cts = _cts;
            _cts = null;
        }
        try { cts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        cts?.Dispose();
        foreach (var entry in _grants.Values) entry.Revoke();
        _grants.Clear();
        foreach (var call in _activeCalls.Values) call.Fail(new ObjectDisposedException(nameof(RemoteLlmLoopbackProxy)));
        _activeCalls.Clear();
        return ValueTask.CompletedTask;
    }

    private sealed record ParsedHttpRequest(string Method, string Target, Dictionary<string, string> Headers, byte[] Body);

    private sealed class GrantEntry
    {
        private readonly object _gate = new();
        private RemoteConnection? _connection;
        private DateTimeOffset? _disconnectedAt;
        private TaskCompletionSource<RemoteConnection?>? _waiter;
        private bool _revoked;

        public GrantEntry(string grantToken, string deviceId, RemoteConnection connection)
        {
            GrantToken = grantToken;
            DeviceId = deviceId;
            _connection = connection;
        }

        public string GrantToken { get; }
        public string DeviceId { get; }

        public bool IsExpired(DateTimeOffset cutoff)
        {
            lock (_gate)
            {
                return _revoked || (_connection is null && _disconnectedAt is { } at && at < cutoff);
            }
        }

        public void Rebind(RemoteConnection newConnection)
        {
            TaskCompletionSource<RemoteConnection?>? waiter;
            lock (_gate)
            {
                if (_revoked) return;
                _connection = newConnection;
                _disconnectedAt = null;
                waiter = _waiter;
                _waiter = null;
            }
            waiter?.TrySetResult(newConnection);
        }

        public void DetachIf(RemoteConnection closed, DateTimeOffset now)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_connection, closed))
                {
                    _connection = null;
                    _disconnectedAt = now;
                }
            }
        }

        public void Revoke()
        {
            TaskCompletionSource<RemoteConnection?>? waiter;
            lock (_gate)
            {
                _revoked = true;
                _connection = null;
                waiter = _waiter;
                _waiter = null;
            }
            waiter?.TrySetResult(null);
        }

        public async Task<RemoteConnection?> GetOrWaitConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            TaskCompletionSource<RemoteConnection?> waiter;
            lock (_gate)
            {
                if (_revoked) return null;
                if (_connection is { } live && !live.Completion.IsCompleted) return live;
                waiter = _waiter ??= new TaskCompletionSource<RemoteConnection?>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(timeout);
                return await waiter.Task.WaitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    private sealed class ActiveCall(RemoteConnection connection)
    {
        private readonly Channel<byte[]> _response = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

        public RemoteConnection Connection { get; } = connection;
        public ChannelReader<byte[]> ResponseChunks => _response.Reader;

        public void PushResponseChunk(byte[] data) => _response.Writer.TryWrite(data);

        public void CompleteResponse(Exception? error) => _response.Writer.TryComplete(error);

        public void Fail(Exception error) => _response.Writer.TryComplete(error);
    }

    private sealed class BufferedStreamReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buf = new byte[4096];
        private int _offset;
        private int _count;

        public BufferedStreamReader(Stream stream, byte[]? initial, int initialOffset, int initialLength)
        {
            _stream = stream;
            if (initial is not null && initialLength > 0)
            {
                if (initialLength > _buf.Length)
                {
                    _buf = new byte[initialLength];
                }
                Buffer.BlockCopy(initial, initialOffset, _buf, 0, initialLength);
                _count = initialLength;
            }
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            using var line = new MemoryStream(64);
            while (true)
            {
                if (_offset >= _count)
                {
                    _offset = 0;
                    _count = await _stream.ReadAsync(_buf, cancellationToken).ConfigureAwait(false);
                    if (_count == 0) return line.Length == 0 ? null : Encoding.ASCII.GetString(line.ToArray());
                }
                var b = _buf[_offset++];
                if (b == '\n')
                {
                    var arr = line.ToArray();
                    var len = arr.Length > 0 && arr[^1] == '\r' ? arr.Length - 1 : arr.Length;
                    return Encoding.ASCII.GetString(arr, 0, len);
                }
                line.WriteByte(b);
            }
        }

        public async Task ReadExactAsync(byte[] destination, CancellationToken cancellationToken)
        {
            var copied = 0;
            while (copied < destination.Length)
            {
                if (_offset < _count)
                {
                    var take = Math.Min(_count - _offset, destination.Length - copied);
                    Buffer.BlockCopy(_buf, _offset, destination, copied, take);
                    _offset += take;
                    copied += take;
                }
                else
                {
                    var read = await _stream.ReadAsync(destination.AsMemory(copied, destination.Length - copied), cancellationToken).ConfigureAwait(false);
                    if (read == 0) throw new IOException("Unexpected EOF in chunked body.");
                    copied += read;
                }
            }
        }
    }
}
