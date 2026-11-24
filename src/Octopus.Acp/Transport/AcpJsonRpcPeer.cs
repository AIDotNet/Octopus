using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Octopus.Acp.Transport;

/// <summary>Context passed to an incoming Agent → Client JSON-RPC request handler.</summary>
public sealed record AcpRequestContext(string RawId, CancellationToken CancellationToken);

/// <summary>Callbacks for an <see cref="AcpJsonRpcPeer"/>.</summary>
public sealed class AcpPeerHandlers
{
    public Func<string, JsonElement?, AcpRequestContext, Task<JsonNode?>>? OnRequest { get; init; }
    public Action<string, JsonElement?>? OnNotification { get; init; }
    /// <summary>Invoked on the read loop with the request method when its reply arrives, before the caller resumes.</summary>
    public Action<string>? OnResponse { get; init; }
    public Action<string>? OnOtherLine { get; init; }
    public Action<string>? OnDiagnostic { get; init; }
    public Action<Exception>? OnClose { get; init; }
}

/// <summary>
/// Bidirectional JSON-RPC 2.0 peer over NDJSON streams (stdio).
/// Uses a single serialized <see cref="Channel{T}"/> writer so concurrent responses and requests never interleave,
/// supports request cancellation via <c>$/cancel_request</c>, and handles graceful shutdown with notification drain.
/// </summary>
public sealed class AcpJsonRpcPeer : IAsyncDisposable
{
    private const int DefaultMaxPendingRequests = 128;
    private const int DefaultMaxIncomingRequests = 64;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly AcpPeerHandlers _handlers;
    private readonly NdjsonLineFramer _framer;
    private readonly Channel<string> _writeQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, PendingCall> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _incoming = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifecycle = new();
    private readonly Task _readTask;
    private readonly Task _writeTask;
    private long _nextId;
    private volatile Exception? _closedError;
    private volatile bool _drainingNotifications;
    private int _closed;

    public AcpJsonRpcPeer(Stream input, Stream output, AcpPeerHandlers? handlers = null, int maxLineBytes = NdjsonLineFramer.DefaultMaxLineBytes)
    {
        _input = input;
        _output = output;
        _handlers = handlers ?? new AcpPeerHandlers();
        _framer = new NdjsonLineFramer(maxLineBytes);
        _writeTask = Task.Run(WriteLoopAsync);
        _readTask = Task.Run(ReadLoopAsync);
    }

    public bool IsClosed => _closedError is not null;

    /// <summary>Sends a JSON-RPC request and waits for the matching result.</summary>
    public async Task<JsonElement> RequestAsync(
        string method,
        JsonNode? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (_closedError is { } closed)
        {
            throw closed;
        }
        if (_pending.Count >= DefaultMaxPendingRequests)
        {
            throw new AcpException("ACP pending request capacity exceeded.");
        }

        var idNum = Interlocked.Increment(ref _nextId);
        var idKey = idNum.ToString();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = new PendingCall(method, tcs);
        _pending[idKey] = call;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifecycle.Token);
        if (timeout is { } limit && limit > TimeSpan.Zero)
        {
            linkedCts.CancelAfter(limit);
        }

        using var reg = linkedCts.Token.Register(() =>
        {
            if (!_pending.TryRemove(idKey, out _)) return;
            if (cancellationToken.IsCancellationRequested)
            {
                _ = TryNotifyCancelRequestAsync(idNum);
                tcs.TrySetCanceled(cancellationToken);
            }
            else if (_closedError is { } err)
            {
                tcs.TrySetException(err);
            }
            else if (timeout is { } expired)
            {
                _ = TryNotifyCancelRequestAsync(idNum);
                tcs.TrySetException(new AcpTimeoutException(method, expired));
            }
            else
            {
                tcs.TrySetException(new AcpConnectionClosedException());
            }
        });

        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idNum,
            ["method"] = method,
        };
        if (parameters is not null)
        {
            envelope["params"] = parameters;
        }

        try
        {
            EnqueueLine(envelope.ToJsonString());
        }
        catch (Exception ex)
        {
            if (_pending.TryRemove(idKey, out _))
            {
                tcs.TrySetException(ex);
            }
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Sends a one-way JSON-RPC notification.</summary>
    public Task NotifyAsync(string method, JsonNode? parameters = null)
    {
        if (_closedError is { } closed)
        {
            return Task.FromException(closed);
        }
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (parameters is not null)
        {
            envelope["params"] = parameters;
        }
        EnqueueLine(envelope.ToJsonString());
        return Task.CompletedTask;
    }

    /// <summary>Aborts cancellation tokens of all active incoming agent→client requests.</summary>
    public void CancelIncomingRequests()
    {
        foreach (var (_, cts) in _incoming)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Rejects pending calls immediately while still allowing trailing stdout notifications to drain briefly.</summary>
    public void DrainNotifications(Exception? error = null)
    {
        _drainingNotifications = true;
        FailPending(error ?? new AcpConnectionClosedException());
    }

    public void Close(Exception? error = null)
    {
        var terminal = error ?? new AcpConnectionClosedException();
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }
        _closedError = terminal;
        _writeQueue.Writer.TryComplete();
        CancelIncomingRequests();
        FailPending(terminal);
        try { _lifecycle.Cancel(); } catch (ObjectDisposedException) { }
        try { _handlers.OnClose?.Invoke(terminal); }
        catch (Exception ex) { Diagnose($"ACP onClose listener failed: {ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        try { await Task.WhenAll(_readTask, _writeTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* Shutdown is best-effort. */ }
        _lifecycle.Dispose();
    }

    private void EnqueueLine(string json)
    {
        var byteCount = Encoding.UTF8.GetByteCount(json) + 1;
        if (byteCount > _framer.MaxLineBytes)
        {
            throw new AcpException($"Outgoing ACP line exceeds max size ({byteCount} bytes).");
        }
        if (!_writeQueue.Writer.TryWrite(json))
        {
            throw _closedError ?? new AcpConnectionClosedException();
        }
    }

    private async Task TryNotifyCancelRequestAsync(long requestId)
    {
        try
        {
            if (_closedError is null)
            {
                await NotifyAsync("$/cancel_request", new JsonObject { ["requestId"] = requestId }).ConfigureAwait(false);
            }
        }
        catch { /* Best-effort cancellation. */ }
    }

    private async Task WriteLoopAsync()
    {
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            await foreach (var line in _writeQueue.Reader.ReadAllAsync(_lifecycle.Token).ConfigureAwait(false))
            {
                var bytes = utf8NoBom.GetBytes(line + "\n");
                await _output.WriteAsync(bytes, _lifecycle.Token).ConfigureAwait(false);
                await _output.FlushAsync(_lifecycle.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Close(new AcpConnectionClosedException($"ACP write failed: {ex.Message}", ex));
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!_lifecycle.IsCancellationRequested)
            {
                var read = await _input.ReadAsync(buffer, _lifecycle.Token).ConfigureAwait(false);
                if (read == 0) break;
                _framer.Feed(buffer.AsSpan(0, read), DispatchMessage, OnOtherLine, OnOversizedLine);
            }
            _framer.Complete(DispatchMessage, OnOtherLine, OnOversizedLine);
            Close(new AcpConnectionClosedException("ACP agent closed stdout."));
        }
        catch (OperationCanceledException)
        {
            Close();
        }
        catch (Exception ex)
        {
            Close(new AcpConnectionClosedException($"ACP read failed: {ex.Message}", ex));
        }
    }

    private void OnOtherLine(string line)
    {
        try { _handlers.OnOtherLine?.Invoke(line); }
        catch (Exception ex) { Diagnose($"ACP onOtherLine listener failed: {ex.Message}"); }
    }

    private void OnOversizedLine(OversizedAcpLine oversized)
    {
        Diagnose($"Ignored oversized ACP line ({oversized.ByteCount} bytes).");
        OnOtherLine(oversized.Prefix);
        if (oversized.ExtractedRequestId is { } id && _pending.TryRemove(id, out var pending))
        {
            pending.Tcs.TrySetException(new AcpException($"ACP response for '{pending.Method}' exceeded max line size ({oversized.ByteCount} bytes)."));
        }
    }

    private void DispatchMessage(JsonDocument doc)
    {
        using (doc)
        {
            if (IsClosed && !_drainingNotifications) return;
            var root = doc.RootElement;
            var hasMethod = root.TryGetProperty("method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String;
            var hasId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind is JsonValueKind.Number or JsonValueKind.String;

            if (hasMethod)
            {
                var method = methodEl.GetString()!;
                JsonElement? parameters = root.TryGetProperty("params", out var pEl) ? pEl.Clone() : null;

                if (hasId)
                {
                    if (_drainingNotifications) return;
                    HandleIncomingRequest(idEl.Clone(), method, parameters);
                    return;
                }

                if (method == "$/cancel_request")
                {
                    HandleIncomingCancel(parameters);
                    return;
                }

                try
                {
                    _handlers.OnNotification?.Invoke(method, parameters);
                }
                catch (Exception ex)
                {
                    Diagnose($"ACP notification handler for '{method}' failed: {ex.Message}");
                }
                return;
            }

            if (_drainingNotifications || !hasId) return;

            var idKey = IdToKey(idEl);
            if (!_pending.TryRemove(idKey, out var pending))
            {
                return;
            }
            try { _handlers.OnResponse?.Invoke(pending.Method); }
            catch { /* Observers cannot break reply dispatch. */ }

            if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
            {
                var code = errEl.TryGetProperty("code", out var cEl) && cEl.TryGetInt32(out var c) ? c : -32603;
                var msg = errEl.TryGetProperty("message", out var mEl) && mEl.ValueKind == JsonValueKind.String
                    ? mEl.GetString()! : "ACP RPC error";
                JsonElement? data = errEl.TryGetProperty("data", out var dEl) ? dEl.Clone() : null;
                pending.Tcs.TrySetException(code == -32000
                    ? new AcpAuthRequiredException(msg, null, data)
                    : new AcpRpcException(code, msg, data));
                return;
            }

            if (root.TryGetProperty("result", out var resEl))
            {
                pending.Tcs.TrySetResult(resEl.Clone());
                return;
            }

            pending.Tcs.TrySetException(new AcpException($"Malformed JSON-RPC reply for '{pending.Method}'."));
        }
    }

    private void HandleIncomingRequest(JsonElement idEl, string method, JsonElement? parameters)
    {
        var key = IdToKey(idEl);
        if (_incoming.Count >= DefaultMaxIncomingRequests)
        {
            SendErrorReply(idEl, -32001, "Too many concurrent incoming ACP requests.");
            return;
        }
        if (_handlers.OnRequest is null)
        {
            SendErrorReply(idEl, -32601, $"Unsupported ACP client method: {method}");
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycle.Token);
        _incoming[key] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _handlers.OnRequest(method, parameters, new AcpRequestContext(key, cts.Token)).ConfigureAwait(false);
                SendResultReply(idEl, result ?? new JsonObject());
            }
            catch (AcpRpcException rpc)
            {
                SendErrorReply(idEl, rpc.Code, rpc.Message);
            }
            catch (OperationCanceledException)
            {
                SendErrorReply(idEl, -32800, "Request cancelled.");
            }
            catch (Exception ex)
            {
                Diagnose($"Incoming ACP request '{method}' failed: {ex.Message}");
                SendErrorReply(idEl, -32603, ex.Message);
            }
            finally
            {
                if (_incoming.TryRemove(key, out var owned))
                {
                    owned.Dispose();
                }
            }
        });
    }

    private void HandleIncomingCancel(JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } obj) return;
        if (!obj.TryGetProperty("requestId", out var idEl) && !obj.TryGetProperty("id", out idEl)) return;
        var key = IdToKey(idEl);
        if (_incoming.TryGetValue(key, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private void SendResultReply(JsonElement idEl, JsonNode result)
    {
        if (_closedError is not null) return;
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(idEl.GetRawText()),
            ["result"] = result,
        };
        try { EnqueueLine(envelope.ToJsonString()); }
        catch { /* Closing race. */ }
    }

    private void SendErrorReply(JsonElement idEl, int code, string message)
    {
        if (_closedError is not null) return;
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(idEl.GetRawText()),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
        try { EnqueueLine(envelope.ToJsonString()); }
        catch { /* Closing race. */ }
    }

    private void FailPending(Exception error)
    {
        foreach (var (key, call) in _pending)
        {
            if (_pending.TryRemove(key, out _))
            {
                call.Tcs.TrySetException(error);
            }
        }
    }

    private static string IdToKey(JsonElement id) =>
        id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : id.GetRawText();

    private void Diagnose(string message)
    {
        try { _handlers.OnDiagnostic?.Invoke(message); }
        catch { /* Diagnostics never break transport. */ }
    }

    private sealed record PendingCall(string Method, TaskCompletionSource<JsonElement> Tcs);
}
