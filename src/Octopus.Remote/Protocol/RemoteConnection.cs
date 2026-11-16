using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Octopus.Remote.Security;

namespace Octopus.Remote.Protocol;

/// <summary>
/// The conversation inside a secure channel: method calls with answers, events, and the frames that carry terminal
/// data (design/12-远程操作.md §6). Both sides use the same class; which methods a side answers is what makes it a
/// host or a device. Register the handlers, then call <see cref="Start"/>.
/// </summary>
public sealed class RemoteConnection : IAsyncDisposable
{
    private delegate Task<byte[]> RequestHandler(JsonElement parameters, CancellationToken cancellationToken);

    private readonly SecureChannel _channel;
    private readonly ConcurrentDictionary<string, RequestHandler> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Action<JsonElement>> _notifications = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private int _started;

    public RemoteConnection(SecureChannel channel) => _channel = channel;

    /// <summary>The device at the other end.</summary>
    public DevicePublicKey Peer => _channel.Peer;
    public DeviceDescription PeerDescription => _channel.PeerDescription;

    /// <summary>A terminal data frame arrived (output, a record, input or an acknowledgement). Called on the receiving thread, in order: copy and return.</summary>
    public Action<FrameHeader, ReadOnlyMemory<byte>>? DataReceived { get; set; }

    /// <summary>Completes when the connection has ended, for whatever reason.</summary>
    public Task Completion => _completion.Task;

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
                writer.WriteRawValue(JsonSerializer.SerializeToUtf8Bytes(parameters, parametersType));
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
            writer.WriteRawValue(JsonSerializer.SerializeToUtf8Bytes(parameters, parametersType));
        }, cancellationToken);

    /// <summary>Sends a terminal data frame on a channel.</summary>
    public ValueTask SendDataAsync(FrameType type, uint channel, ReadOnlySpan<byte> payload, CancellationToken cancellationToken = default) =>
        _channel.SendAsync(new FrameHeader(type, FrameFlags.None, channel).Wrap(payload), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _closed.CancelAsync().ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
        Finish(new IOException("The connection was closed."));
    }

    private ValueTask SendJsonAsync(FrameType type, Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return _channel.SendAsync(new FrameHeader(type, FrameFlags.None, 0).Wrap(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)), cancellationToken);
    }

    private async Task ReceiveLoopAsync()
    {
        Exception end = new IOException("The other side closed the connection.");
        try
        {
            while (await _channel.ReceiveAsync(_closed.Token).ConfigureAwait(false) is { } message)
            {
                if (!FrameHeader.TryRead(message, out var header)) throw new IOException("A frame is too short.");
                var payload = message.AsMemory(FrameHeader.Length);
                switch (header.Type)
                {
                    case FrameType.Request:
                        _ = AnswerAsync(Parse(payload));
                        break;
                    case FrameType.Response:
                        Complete(Parse(payload));
                        break;
                    case FrameType.Notification:
                        var note = Parse(payload);
                        if (note.TryGetProperty("method", out var name) && _notifications.TryGetValue(name.GetString() ?? "", out var listener)
                            && note.TryGetProperty("params", out var noteParams))
                        {
                            listener(noteParams);
                        }
                        break;
                    case FrameType.Output or FrameType.Record or FrameType.Input or FrameType.Ack or FrameType.Snapshot:
                        DataReceived?.Invoke(header, payload);
                        break;
                    default:
                        // A newer peer may send what this one does not know; only what it marked as optional may be skipped.
                        if (!header.Flags.HasFlag(FrameFlags.Droppable)) throw new IOException($"Unknown frame type 0x{(byte)header.Type:X2}.");
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or HandshakeException or OperationCanceledException or InvalidOperationException)
        {
            end = e is OperationCanceledException ? new IOException("The connection was closed.") : e;
        }
        await _channel.DisposeAsync().ConfigureAwait(false);
        Finish(end);
    }

    private static JsonElement Parse(ReadOnlyMemory<byte> payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : throw new JsonException("A message is not an object.");
    }

    private async Task AnswerAsync(JsonElement request)
    {
        if (!request.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id)) return;
        byte[]? result = null;
        (int Code, string Message)? error = null;
        try
        {
            var method = request.TryGetProperty("method", out var name) ? name.GetString() ?? "" : "";
            if (!_requests.TryGetValue(method, out var handler)) throw new RemoteException(RemoteProtocol.MethodNotFound, $"Unknown method '{method}'.");
            result = await handler(request.TryGetProperty("params", out var parameters) ? parameters : default, _closed.Token).ConfigureAwait(false);
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

    private void Complete(JsonElement response)
    {
        if (!response.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id) || !_pending.TryGetValue(id, out var answer)) return;
        if (response.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var number) ? number : RemoteProtocol.InternalError;
            answer.TrySetException(new RemoteException(code, error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : ""));
        }
        else if (response.TryGetProperty("result", out var result))
        {
            answer.TrySetResult(result);
        }
    }

    private void Finish(Exception reason)
    {
        _closed.Cancel();
        foreach (var answer in _pending.Values) answer.TrySetException(new IOException("The connection ended before the answer arrived.", reason));
        _completion.TrySetResult();
    }
}

/// <summary>The first exchange on a connection: both sides say what they speak and can do.</summary>
public static class RemoteHello
{
    /// <summary>Host side: answers <c>hello</c> with <paramref name="own"/>, refusing a device of another protocol version; <paramref name="received"/> gets the device's message.</summary>
    public static void Answer(RemoteConnection connection, HelloMessage own, Action<HelloMessage>? received = null) =>
        connection.Handle(RemoteProtocol.Hello, RemoteJsonContext.Default.HelloMessage, RemoteJsonContext.Default.HelloMessage, (theirs, _) =>
        {
            if (theirs.Protocol != own.Protocol)
            {
                throw new RemoteException(RemoteProtocol.VersionMismatch, theirs.Protocol < own.Protocol
                    ? $"The device speaks protocol {theirs.Protocol}, the host {own.Protocol}: update Octopus on the device."
                    : $"The device speaks protocol {theirs.Protocol}, the host {own.Protocol}: update Octopus on the host.");
            }
            received?.Invoke(theirs);
            return Task.FromResult(own);
        });

    /// <summary>Device side: sends <paramref name="own"/> and returns the host's.</summary>
    public static Task<HelloMessage> SendAsync(RemoteConnection connection, HelloMessage own, CancellationToken cancellationToken = default) =>
        connection.RequestAsync(RemoteProtocol.Hello, own, RemoteJsonContext.Default.HelloMessage, RemoteJsonContext.Default.HelloMessage, cancellationToken);

    /// <summary>The features both sides have.</summary>
    public static IReadOnlySet<string> Shared(HelloMessage a, HelloMessage b) =>
        a.Capabilities.Intersect(b.Capabilities, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
}
