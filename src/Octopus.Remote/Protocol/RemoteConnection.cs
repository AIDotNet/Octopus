using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Octopus.Remote.Security;

namespace Octopus.Remote.Protocol;

/// <summary>
/// The conversation inside a secure channel: method calls with answers, events, and the frames that carry terminal or
/// binary stream data (design/12-远程操作.md §6, design/remote/传输协议v2与多传输优化.md §3). Both sides use the same
/// class; which methods a side answers is what makes it a host or a device. Register the handlers, then call <see cref="Start"/>.
/// </summary>
public sealed class RemoteConnection : IAsyncDisposable
{
    private const int HighPriorityJsonMaxBytes = 4 * 1024;
    /// <summary>Maximum time a single frame send may block on a stalled/zombie socket before the connection is declared dead.</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);
    private delegate Task<byte[]> RequestHandler(JsonElement parameters, CancellationToken cancellationToken);

    private SecureChannel _channel;
    private CancellationTokenSource _channelReceiveCts = new();
    private readonly PrioritySendGate _sendGate = new();
    private readonly ConcurrentDictionary<string, RequestHandler> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Action<JsonElement>> _notifications = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<TimeSpan>> _pings = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private int _started;
    private volatile FrameCompressionKind _compression = FrameCompressionKind.None;
    private volatile int _compressThreshold = FrameCodec.DefaultThreshold;
    private volatile int _negotiatedVersion = RemoteProtocol.Version;
    private volatile int _maxChunkBytes = RemoteMethods.StreamChunkBytes;
    private long _lastRttTicks;
    private int _migrationCount;

    public RemoteConnection(SecureChannel channel) => _channel = channel;

    /// <summary>The device at the other end.</summary>
    public DevicePublicKey Peer => Volatile.Read(ref _channel).Peer;
    public DeviceDescription PeerDescription => Volatile.Read(ref _channel).PeerDescription;

    /// <summary>The negotiated protocol version after <see cref="RemoteHello"/> (1 for legacy peers, 2 for v2 peers).</summary>
    public int NegotiatedVersion => _negotiatedVersion;
    /// <summary>The active frame payload compression algorithm negotiated with the peer.</summary>
    public FrameCompressionKind Compression => _compression;
    /// <summary>The negotiated chunk size in bytes for snapshots and binary streams.</summary>
    public int MaxChunkBytes => _maxChunkBytes;
    /// <summary>The most recently measured round-trip time from <see cref="PingAsync"/>, or null before the first pong.</summary>
    public TimeSpan? LastRtt => Interlocked.Read(ref _lastRttTicks) is > 0 and var ticks ? TimeSpan.FromTicks(ticks) : null;
    /// <summary>How many times this connection has migrated to a new underlying <see cref="SecureChannel"/>.</summary>
    public int MigrationCount => Volatile.Read(ref _migrationCount);
    /// <summary>Raised when the underlying transport channel is seamlessly replaced via <see cref="MigrateTransportAsync"/>.</summary>
    public event Action? TransportMigrated;

    /// <summary>A terminal or binary stream data frame arrived. Called on the receiving thread, in order: copy and return.</summary>
    public Action<FrameHeader, ReadOnlyMemory<byte>>? DataReceived { get; set; }

    /// <summary>Completes when the connection has ended, for whatever reason.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Applies the protocol v2 parameters negotiated during <c>hello</c>.</summary>
    public void ConfigureNegotiatedProtocol(int version, FrameCompressionKind compression, int compressThreshold = FrameCodec.DefaultThreshold, int maxChunkBytes = RemoteMethods.StreamChunkBytes)
    {
        _negotiatedVersion = Math.Max(RemoteProtocol.Version, version);
        _compression = compression;
        _compressThreshold = Math.Max(64, compressThreshold);
        _maxChunkBytes = Math.Clamp(maxChunkBytes, 4 * 1024, 256 * 1024);
    }

    /// <summary>Answers calls of <paramref name="method"/>. An exception from the handler goes back as an error; a <see cref="RemoteException"/> with its own code.</summary>
    public void Handle<TParams, TResult>(string method, JsonTypeInfo<TParams> parameters, JsonTypeInfo<TResult> result,
        Func<TParams, CancellationToken, Task<TResult>> handler)
    {
        _requests[method] = async (json, cancellationToken) =>
        {
            if (json.ValueKind == JsonValueKind.Undefined) throw new RemoteException(RemoteProtocol.InvalidParams, "Parameters are missing.");
            TParams? value;
            try { value = json.Deserialize(parameters); }
            catch (JsonException e) { throw new RemoteException(RemoteProtocol.InvalidParams, e.Message); }
            if (value is null) throw new RemoteException(RemoteProtocol.InvalidParams, "Parameters are missing.");
            return JsonSerializer.SerializeToUtf8Bytes(await handler(value, cancellationToken).ConfigureAwait(false), result);
        };
    }

    /// <summary>Listens to the event <paramref name="method"/>. Called on the receiving thread, in order.</summary>
    public void On<TParams>(string method, JsonTypeInfo<TParams> parameters, Action<TParams> handler)
    {
        _notifications[method] = json =>
        {
            if (json.Deserialize(parameters) is { } value) handler(value);
        };
    }

    /// <summary>Begins receiving. Everything registered before this is in place for the first message.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _ = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>Calls a method on the other side and waits for its answer. Throws <see cref="RemoteException"/> for an error answer and <see cref="IOException"/> when the connection ends first.</summary>
    public async Task<TResult> RequestAsync<TParams, TResult>(string method, TParams parameters, JsonTypeInfo<TParams> parametersType,
        JsonTypeInfo<TResult> resultType, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            await SendJsonAsync(FrameType.Request, writer =>
            {
                writer.WriteNumber("id", id);
                writer.WriteString("method", method);
                writer.WritePropertyName("params");
                JsonSerializer.Serialize(writer, parameters, parametersType);
            }, cancellationToken).ConfigureAwait(false);
            if (_closed.IsCancellationRequested) answer.TrySetException(new IOException("The connection ended."));
            var json = await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return json.Deserialize(resultType) ?? throw new RemoteException(RemoteProtocol.InternalError, "The answer is empty.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends an event: nothing comes back.</summary>
    public ValueTask NotifyAsync<TParams>(string method, TParams parameters, JsonTypeInfo<TParams> parametersType, CancellationToken cancellationToken = default) =>
        SendJsonAsync(FrameType.Notification, writer =>
        {
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            JsonSerializer.Serialize(writer, parameters, parametersType);
        }, cancellationToken);

    /// <summary>Sends a terminal or stream data frame on a channel.</summary>
    public ValueTask SendDataAsync(FrameType type, uint channel, ReadOnlySpan<byte> payload, CancellationToken cancellationToken = default) =>
        SendFrameAsync(new FrameHeader(type, DefaultFlags(type), channel), payload.ToArray(), cancellationToken);

    /// <summary>Sends a terminal or stream data frame on a channel (async-friendly overload avoiding temporary array copies at call sites).</summary>
    public ValueTask SendDataAsync(FrameType type, uint channel, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        SendFrameAsync(new FrameHeader(type, DefaultFlags(type), channel), payload, cancellationToken);

    /// <summary>
    /// Sends a high-priority droppable <see cref="FrameType.Ping"/> frame and awaits the peer's <see cref="FrameType.Pong"/>,
    /// updating <see cref="LastRtt"/> and returning the measured round-trip time.
    /// </summary>
    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        var stamp = Stopwatch.GetTimestamp();
        while (!_pings.TryAdd(stamp, new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously)))
        {
            stamp++;
        }
        try
        {
            var payload = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(payload, stamp);
            var header = new FrameHeader(FrameType.Ping, FrameFlags.Droppable | FrameFlags.HighPriority, 0);
            await SendFrameAsync(header, payload, cancellationToken).ConfigureAwait(false);
            if (_closed.IsCancellationRequested) throw new IOException("The connection ended.");
            return await _pings[stamp].Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pings.TryRemove(stamp, out _);
        }
    }

    /// <summary>
    /// Seamlessly migrates this connection to <paramref name="newChannel"/> (e.g. upgrading from a relay tunnel to a
    /// direct QUIC or WebRTC P2P transport) without dropping pending requests or attached terminal/conversation channels.
    /// Both ends must have authenticated the same <see cref="Peer"/> device key.
    /// </summary>
    public async Task MigrateTransportAsync(SecureChannel newChannel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newChannel);
        if (!newChannel.Peer.Equals(Peer))
        {
            await newChannel.DisposeAsync().ConfigureAwait(false);
            throw new HandshakeException(HandshakeFailure.WrongPeer, "Cannot migrate to a channel authenticated with a different device key.");
        }

        await _sendGate.WaitAsync(highPriority: true, cancellationToken).ConfigureAwait(false);
        SecureChannel oldChannel;
        CancellationTokenSource oldCts;
        try
        {
            if (_closed.IsCancellationRequested)
            {
                await newChannel.DisposeAsync().ConfigureAwait(false);
                throw new IOException("The connection is closed.");
            }
            oldChannel = _channel;
            oldCts = _channelReceiveCts;
            _channelReceiveCts = new CancellationTokenSource();
            Volatile.Write(ref _channel, newChannel);
            Interlocked.Increment(ref _migrationCount);
        }
        finally
        {
            _sendGate.Release();
        }

        await oldCts.CancelAsync().ConfigureAwait(false);
        _ = Task.Run(async () =>
        {
            try { await oldChannel.DisposeAsync().ConfigureAwait(false); }
            catch { }
        });
        TransportMigrated?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await _closed.CancelAsync().ConfigureAwait(false);
        try { await _channelReceiveCts.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
        // Unblock pending requests and completion listeners immediately before waiting on network close handshake.
        Finish(new IOException("The connection was closed."));
        await Volatile.Read(ref _channel).DisposeAsync().ConfigureAwait(false);
    }

    private static FrameFlags DefaultFlags(FrameType type) =>
        type is FrameType.Input or FrameType.Ack or FrameType.StreamWindow ? FrameFlags.HighPriority
        : type is FrameType.Ping or FrameType.Pong ? FrameFlags.Droppable | FrameFlags.HighPriority
        : FrameFlags.None;

    private ValueTask SendJsonAsync(FrameType type, Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        using var buffer = new PooledBufferWriter(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        var written = buffer.WrittenMemory;
        var flags = written.Length <= HighPriorityJsonMaxBytes ? FrameFlags.HighPriority : FrameFlags.None;
        // Copy out of the writer's pooled buffer only if SendFrameAsync suspends; SendFrameAsync packs immediately under the gate.
        return SendFrameAsync(new FrameHeader(type, flags, 0), written.ToArray(), cancellationToken);
    }

    private async ValueTask SendFrameAsync(FrameHeader header, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(header.IsHighPriority, cancellationToken).ConfigureAwait(false);
        try
        {
            var (buffer, length) = FrameCodec.PackPooled(header, payload.Span, _compression, _compressThreshold);
            try
            {
                using var sendLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
                sendLimit.CancelAfter(SendTimeout);
                try
                {
                    await Volatile.Read(ref _channel).SendAsync(buffer.AsMemory(0, length), sendLimit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_closed.IsCancellationRequested)
                {
                    // Stalled/half-open socket (e.g. after mobile backgrounding): tear down the dead connection so callers reconnect.
                    _ = Task.Run(() => DisposeAsync().AsTask());
                    throw new IOException("The connection timed out while sending.");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        Exception end = new IOException("The other side closed the connection.");
        try
        {
            while (!_closed.IsCancellationRequested)
            {
                var currentChannel = Volatile.Read(ref _channel);
                var currentCts = _channelReceiveCts;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token, currentCts.Token);
                byte[]? message;
                try
                {
                    message = await currentChannel.ReceiveAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_closed.IsCancellationRequested && !ReferenceEquals(currentChannel, Volatile.Read(ref _channel)))
                {
                    // Transport migrated to a new SecureChannel: continue receiving from the new one.
                    continue;
                }
                catch (Exception) when (!_closed.IsCancellationRequested && !ReferenceEquals(currentChannel, Volatile.Read(ref _channel)))
                {
                    continue;
                }

                if (message is null)
                {
                    if (!_closed.IsCancellationRequested && !ReferenceEquals(currentChannel, Volatile.Read(ref _channel)))
                    {
                        continue;
                    }
                    break;
                }

                if (!FrameHeader.TryRead(message, out var header)) throw new IOException("A frame is too short.");
                var payload = message.AsMemory(FrameHeader.Length);
                if (header.Flags.HasFlag(FrameFlags.Compressed))
                {
                    payload = FrameCodec.Decompress(payload.Span);
                }

                switch (header.Type)
                {
                    case FrameType.Request:
                        DispatchRequest(payload);
                        break;
                    case FrameType.Response:
                        CompleteResponse(payload);
                        break;
                    case FrameType.Notification:
                        DispatchNotification(payload);
                        break;
                    case FrameType.Ping:
                        if (payload.Length >= 8)
                        {
                            var echo = payload.Slice(0, 8).ToArray();
                            _ = SendFrameAsync(new FrameHeader(FrameType.Pong, FrameFlags.Droppable | FrameFlags.HighPriority, 0), echo, _closed.Token).AsTask();
                        }
                        break;
                    case FrameType.Pong:
                        if (payload.Length >= 8)
                        {
                            var sentStamp = BinaryPrimitives.ReadInt64LittleEndian(payload.Span);
                            var rtt = Stopwatch.GetElapsedTime(sentStamp);
                            Interlocked.Exchange(ref _lastRttTicks, Math.Max(1, rtt.Ticks));
                            if (_pings.TryRemove(sentStamp, out var waiter)) waiter.TrySetResult(rtt);
                        }
                        break;
                    case FrameType.Output or FrameType.Record or FrameType.Input or FrameType.Ack or FrameType.Snapshot
                        or FrameType.StreamData or FrameType.StreamEnd or FrameType.StreamWindow:
                        DataReceived?.Invoke(header, payload);
                        break;
                    default:
                        // A newer peer may send what this one does not know; only what it marked as optional may be skipped.
                        if (!header.Flags.HasFlag(FrameFlags.Droppable)) throw new IOException($"Unknown frame type 0x{(byte)header.Type:X2}.");
                        break;
                }
            }
        }
        catch (Exception e)
        {
            end = e is OperationCanceledException ? new IOException("The connection was closed.") : e;
        }
        try { await Volatile.Read(ref _channel).DisposeAsync().ConfigureAwait(false); }
        catch { }
        Finish(end);
    }

    private void DispatchRequest(ReadOnlyMemory<byte> payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("A message is not an object.");
        if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id)) return;
        var method = root.TryGetProperty("method", out var name) ? name.GetString() ?? "" : "";
        var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
        _ = AnswerAsync(id, method, parameters);
    }

    private void DispatchNotification(ReadOnlyMemory<byte> payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("A message is not an object.");
        if (root.TryGetProperty("method", out var name)
            && _notifications.TryGetValue(name.GetString() ?? "", out var listener)
            && root.TryGetProperty("params", out var noteParams))
        {
            listener(noteParams);
        }
    }

    private void CompleteResponse(ReadOnlyMemory<byte> payload)
    {
        using var document = JsonDocument.Parse(payload);
        var response = document.RootElement;
        if (response.ValueKind != JsonValueKind.Object) throw new JsonException("A message is not an object.");
        if (!response.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id) || !_pending.TryGetValue(id, out var answer)) return;
        if (response.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var number) ? number : RemoteProtocol.InternalError;
            answer.TrySetException(new RemoteException(code, error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : ""));
        }
        else if (response.TryGetProperty("result", out var result))
        {
            answer.TrySetResult(result.Clone());
        }
    }

    private async Task AnswerAsync(long id, string method, JsonElement parameters)
    {
        byte[]? result = null;
        (int Code, string Message)? error = null;
        try
        {
            if (!_requests.TryGetValue(method, out var handler)) throw new RemoteException(RemoteProtocol.MethodNotFound, $"Unknown method '{method}'.");
            result = await handler(parameters, _closed.Token).ConfigureAwait(false);
        }
        catch (RemoteException e) { error = (e.Code, e.Message); }
        catch (OperationCanceledException) { return; }
        catch (Exception e) { error = (RemoteProtocol.InternalError, e.Message); }
        try
        {
            await SendJsonAsync(FrameType.Response, writer =>
            {
                writer.WriteNumber("id", id);
                if (error is { } failure)
                {
                    writer.WriteStartObject("error");
                    writer.WriteNumber("code", failure.Code);
                    writer.WriteString("message", failure.Message);
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WritePropertyName("result");
                    writer.WriteRawValue(result!);
                }
            }, _closed.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private void Finish(Exception reason)
    {
        _closed.Cancel();
        foreach (var answer in _pending.Values) answer.TrySetException(new IOException("The connection ended before the answer arrived.", reason));
        foreach (var ping in _pings.Values) ping.TrySetException(new IOException("The connection ended before the pong arrived.", reason));
        _completion.TrySetResult();
    }

    /// <summary>
    /// Serializes outgoing frames while letting high-priority frames (keystrokes, ACKs, Ping/Pong, small control RPCs)
    /// jump ahead of waiting bulk data frames when the wire is busy. When uncontended, acquires synchronously with zero allocation.
    /// </summary>
    private sealed class PrioritySendGate
    {
        private readonly object _lock = new();
        private readonly Queue<Waiter> _high = new();
        private readonly Queue<Waiter> _normal = new();
        private bool _busy;

        private sealed class Waiter : TaskCompletionSource
        {
            public CancellationTokenRegistration Registration;
            public Waiter() : base(TaskCreationOptions.RunContinuationsAsynchronously) { }
        }

        public ValueTask WaitAsync(bool highPriority, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled(cancellationToken);
            lock (_lock)
            {
                if (!_busy)
                {
                    _busy = true;
                    return ValueTask.CompletedTask;
                }
                var waiter = new Waiter();
                (highPriority ? _high : _normal).Enqueue(waiter);
                if (cancellationToken.CanBeCanceled)
                {
                    waiter.Registration = cancellationToken.Register(static state =>
                    {
                        var (gate, w, ct) = ((PrioritySendGate, Waiter, CancellationToken))state!;
                        lock (gate._lock)
                        {
                            w.TrySetCanceled(ct);
                        }
                    }, (this, waiter, cancellationToken));
                }
                return new ValueTask(waiter.Task);
            }
        }

        public void Release()
        {
            while (true)
            {
                Waiter? next = null;
                lock (_lock)
                {
                    if (_high.Count > 0)
                    {
                        next = _high.Dequeue();
                    }
                    else if (_normal.Count > 0)
                    {
                        next = _normal.Dequeue();
                    }
                    else
                    {
                        _busy = false;
                        return;
                    }
                }
                next.Registration.Dispose();
                if (next.TrySetResult()) return;
            }
        }
    }

    /// <summary>An <see cref="IBufferWriter{T}"/> backed by <see cref="ArrayPool{T}.Shared"/> for allocation-free JSON writing.</summary>
    private sealed class PooledBufferWriter(int initialCapacity) : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        private int _index;

        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _index);

        public void Advance(int count) => _index += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_index);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_index);
        }

        private void EnsureCapacity(int sizeHint)
        {
            var needed = Math.Max(sizeHint, 256);
            if (_buffer.Length - _index >= needed) return;
            var next = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _index + needed));
            _buffer.AsSpan(0, _index).CopyTo(next);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = next;
        }

        public void Dispose()
        {
            var buf = _buffer;
            _buffer = [];
            if (buf.Length > 0) ArrayPool<byte>.Shared.Return(buf);
        }
    }
}

/// <summary>The first exchange on a connection: both sides say what they speak and can do.</summary>
public static class RemoteHello
{
    /// <summary>
    /// Host side: answers <c>hello</c> with <paramref name="own"/>, negotiating the highest common protocol version in
    /// <c>[MinProtocol..MaxProtocol]</c> and configuring frame compression when both sides speak v2; refuses a device
    /// with a non-overlapping version range.
    /// </summary>
    public static void Answer(RemoteConnection connection, HelloMessage own, Action<HelloMessage>? received = null) =>
        connection.Handle(RemoteProtocol.Hello, RemoteJsonContext.Default.HelloMessage, RemoteJsonContext.Default.HelloMessage, (theirs, _) =>
        {
            var (ownMin, ownMax) = VersionRange(own);
            var (theirMin, theirMax) = VersionRange(theirs);
            if (theirMax < ownMin || theirMin > ownMax)
            {
                throw new RemoteException(RemoteProtocol.VersionMismatch, theirMax < ownMin
                    ? $"The device speaks protocol {theirMax}, the host {ownMin}: update Octopus on the device."
                    : $"The device speaks protocol {theirMin}, the host {ownMax}: update Octopus on the host.");
            }

            var negotiated = Math.Min(ownMax, theirMax);
            TransportNegotiation? negotiatedTransport = null;
            if (negotiated >= 2 && (own.Transport is not null || theirs.Transport is not null))
            {
                var chosenCompression = NegotiateCompression(own.Transport?.Compression, theirs.Transport?.Compression);
                var threshold = Math.Max(own.Transport?.CompressThreshold ?? FrameCodec.DefaultThreshold, theirs.Transport?.CompressThreshold ?? FrameCodec.DefaultThreshold);
                var maxChunk = Math.Min(own.Transport?.MaxChunkBytes ?? RemoteMethods.StreamChunkBytes, theirs.Transport?.MaxChunkBytes ?? RemoteMethods.StreamChunkBytes);
                negotiatedTransport = new TransportNegotiation
                {
                    FrameVersion = negotiated,
                    Compression = chosenCompression is not null ? [chosenCompression] : null,
                    CompressThreshold = threshold,
                    MaxChunkBytes = maxChunk,
                    Transports = own.Transport?.Transports,
                };
                connection.ConfigureNegotiatedProtocol(negotiated, FrameCodec.ParseKind(chosenCompression), threshold, maxChunk);
            }

            received?.Invoke(theirs);
            return Task.FromResult(new HelloMessage
            {
                Protocol = theirs.Protocol,
                MinProtocol = own.MinProtocol,
                MaxProtocol = own.MaxProtocol,
                AppVersion = own.AppVersion,
                Capabilities = own.Capabilities,
                TerminalBuild = own.TerminalBuild,
                Transport = negotiatedTransport,
                HostInfo = own.HostInfo,
            });
        });

    /// <summary>Device side: sends <paramref name="own"/>, applies any v2 transport negotiation from the host's answer, and returns the host's message.</summary>
    public static async Task<HelloMessage> SendAsync(RemoteConnection connection, HelloMessage own, CancellationToken cancellationToken = default)
    {
        var answer = await connection.RequestAsync(RemoteProtocol.Hello, own, RemoteJsonContext.Default.HelloMessage, RemoteJsonContext.Default.HelloMessage, cancellationToken).ConfigureAwait(false);
        if (answer.Transport is { FrameVersion: >= 2 } transport)
        {
            var chosen = transport.Compression?.FirstOrDefault();
            connection.ConfigureNegotiatedProtocol(
                transport.FrameVersion,
                FrameCodec.ParseKind(chosen),
                transport.CompressThreshold ?? FrameCodec.DefaultThreshold,
                transport.MaxChunkBytes ?? RemoteMethods.StreamChunkBytes);
        }
        return answer;
    }

    /// <summary>The features both sides have.</summary>
    public static IReadOnlySet<string> Shared(HelloMessage a, HelloMessage b) =>
        a.Capabilities.Intersect(b.Capabilities, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

    /// <summary>The highest protocol version both sides support, or 0 when their ranges do not overlap.</summary>
    public static int NegotiatedVersion(HelloMessage a, HelloMessage b)
    {
        var (aMin, aMax) = VersionRange(a);
        var (bMin, bMax) = VersionRange(b);
        return aMax < bMin || bMax < aMin ? 0 : Math.Min(aMax, bMax);
    }

    private static (int Min, int Max) VersionRange(HelloMessage message)
    {
        var min = message.MinProtocol ?? message.Protocol;
        var max = message.MaxProtocol ?? (message.Transport is { FrameVersion: > 1 } t && message.Protocol == RemoteProtocol.Version ? Math.Max(message.Protocol, t.FrameVersion) : message.Protocol);
        return (min, Math.Max(min, max));
    }

    private static string? NegotiateCompression(IReadOnlyList<string>? hostList, IReadOnlyList<string>? deviceList)
    {
        if (hostList is not { Count: > 0 } || deviceList is not { Count: > 0 }) return null;
        foreach (var preferred in hostList)
        {
            if (deviceList.Any(c => string.Equals(c, preferred, StringComparison.OrdinalIgnoreCase))
                && FrameCodec.ParseKind(preferred) != FrameCompressionKind.None)
            {
                return FrameCodec.NameOf(FrameCodec.ParseKind(preferred));
            }
        }
        return null;
    }
}
