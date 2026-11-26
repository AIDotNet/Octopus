using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Octopus.Core.Agents;

namespace Octopus.App.Collab;

/// <summary>
/// Loopback-only Streamable HTTP MCP server (<c>http://127.0.0.1:&lt;port&gt;/mcp</c>) hosted inside Octopus.
/// Zero reflection (NativeAOT safe), authenticated per session with a 256-bit bearer token bound to
/// the caller's <see cref="Octopus.Core.Projects.ProjectItem.Id"/>.
/// </summary>
public sealed class CollabMcpServer : IDisposable
{
    public const string ServerName = "octopus-collab";
    public const string ProtocolVersion = "2025-03-26";
    public const string UrlEnvVariable = "OCTOPUS_COLLAB_URL";
    public const string TokenEnvVariable = "OCTOPUS_COLLAB_TOKEN";
    public const string ItemIdEnvVariable = "OCTOPUS_ITEM_ID";
    private const int MaxBodyBytes = 4 * 1024 * 1024;
    private const int MaxHeaderBytes = 32 * 1024;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, byte[]> _itemToTokenBytes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _itemToToken = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private ICollabToolHandler? _handler;
    private bool _disposed;
    private readonly string _serverName = ServerName;
    private readonly string? _instructions;
    private readonly Func<JsonArray>? _tools;

    public CollabMcpServer()
    {
    }

    /// <summary>A second loopback MCP server with its own name, instructions and tools (e.g. the bot-only <c>octopus-bot</c>).</summary>
    internal CollabMcpServer(string serverName, string instructions, Func<JsonArray> tools)
    {
        _serverName = serverName;
        _instructions = instructions;
        _tools = tools;
    }

    public string? EndpointUrl { get; private set; }
    public bool IsRunning => _listener is not null && !_disposed;

    public void AttachHandler(ICollabToolHandler handler)
    {
        _handler = handler;
    }

    public bool Start()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_listener is not null) return true;

            try
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _listener = listener;
                EndpointUrl = $"http://127.0.0.1:{port}/mcp";
                _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _stop.Token));
                return true;
            }
            catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
            {
                StopLocked();
                return false;
            }
        }
    }

    /// <summary>Returns (or creates) the cryptographic bearer token bound to <paramref name="itemId"/>.</summary>
    public string IssueToken(string itemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        return _itemToToken.GetOrAdd(itemId, id =>
        {
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            _itemToTokenBytes[id] = Encoding.UTF8.GetBytes(token);
            return token;
        });
    }

    public void RevokeToken(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return;
        _itemToToken.TryRemove(itemId, out _);
        _itemToTokenBytes.TryRemove(itemId, out _);
    }

    /// <summary>
    /// Builds an <see cref="AcpMcpServerConfig"/> using <c>auto</c> transport: ACP agents supporting HTTP MCP connect
    /// directly without extra processes; stdio-only adapters automatically fall back to <c>octopus-signal --mcp</c>.
    /// </summary>
    public AcpMcpServerConfig? BuildAcpServerConfig(string itemId, string? forwarderExecutable)
    {
        if (!IsRunning || string.IsNullOrWhiteSpace(EndpointUrl))
        {
            return null;
        }
        var token = IssueToken(itemId);
        var hasForwarder = !string.IsNullOrWhiteSpace(forwarderExecutable) && File.Exists(forwarderExecutable);
        return new AcpMcpServerConfig
        {
            Name = _serverName,
            Transport = hasForwarder ? "auto" : "http",
            Url = EndpointUrl,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {token}",
            },
            Command = hasForwarder ? forwarderExecutable : null,
            Args = hasForwarder ? ["--mcp"] : [],
            Env = hasForwarder
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [UrlEnvVariable] = EndpointUrl,
                    [TokenEnvVariable] = token,
                    [ItemIdEnvVariable] = itemId,
                }
                : [],
            Enabled = true,
        };
    }

    public bool TryAuthenticateBearer(string? authorizationHeader, out string callerItemId)
    {
        callerItemId = "";
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return false;
        const string prefix = "Bearer ";
        if (!authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var candidate = authorizationHeader[prefix.Length..].Trim();
        if (candidate.Length == 0) return false;
        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        foreach (var (itemId, expected) in _itemToTokenBytes)
        {
            if (expected.Length == candidateBytes.Length
                && CryptographicOperations.FixedTimeEquals(expected, candidateBytes))
            {
                callerItemId = itemId;
                return true;
            }
        }
        return false;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            try
            {
                var req = await ReadHttpRequestAsync(stream, ct).ConfigureAwait(false);
                if (req is null) return;

                if (!string.Equals(req.Path, "/mcp", StringComparison.Ordinal))
                {
                    await WriteHttpResponseAsync(stream, 404, "Not Found", """{"error":"not_found"}""", ct).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(req.Method, "DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteHttpResponseAsync(stream, 200, "OK", "", ct).ConfigureAwait(false);
                    return;
                }
                if (string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteHttpResponseAsync(stream, 405, "Method Not Allowed", "", ct).ConfigureAwait(false);
                    return;
                }
                if (!string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteHttpResponseAsync(stream, 405, "Method Not Allowed", "", ct).ConfigureAwait(false);
                    return;
                }

                req.Headers.TryGetValue("authorization", out var authHeader);
                if (!TryAuthenticateBearer(authHeader, out var callerItemId))
                {
                    await WriteHttpResponseAsync(stream, 401, "Unauthorized", """{"error":"unauthorized"}""", ct).ConfigureAwait(false);
                    return;
                }

                // Long polls (await_events) end as soon as the client hangs up, e.g. when the user stops the agent's turn;
                // otherwise the coordinator would keep a waiter alive for up to 45 s for nobody.
                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var hangupWatch = WatchForHangupAsync(stream, requestCts);
                string? responseJson;
                try
                {
                    responseJson = await HandleRpcPayloadAsync(callerItemId, req.Body, ct, requestCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (requestCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    return; // client is gone; nobody to answer
                }
                finally
                {
                    await requestCts.CancelAsync().ConfigureAwait(false);
                    try { await hangupWatch.ConfigureAwait(false); } catch { }
                }
                if (responseJson is null)
                {
                    await WriteHttpResponseAsync(stream, 202, "Accepted", "", ct).ConfigureAwait(false);
                    return;
                }

                await WriteHttpResponseAsync(stream, 200, "OK", responseJson, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                try
                {
                    await WriteHttpResponseAsync(stream, 500, "Internal Server Error", """{"error":"internal_error"}""", CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Completes when the client closes its side (a read returns 0 bytes or fails) and then cancels <paramref name="requestCts"/>.
    /// Clients send one request per connection (we answer with Connection: close), so any read result means the request is over.
    /// </summary>
    private static async Task WatchForHangupAsync(NetworkStream stream, CancellationTokenSource requestCts)
    {
        var probe = new byte[1];
        try
        {
            _ = await stream.ReadAsync(probe, requestCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // request finished normally
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
        }
        try { await requestCts.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
    }

    /// <param name="ct">Server lifetime.</param>
    /// <param name="requestCt">Cancelled when the client hangs up; only long-polling tools observe it, so a delegation
    /// is never left half-done because the client stopped listening.</param>
    internal async Task<string?> HandleRpcPayloadAsync(string callerItemId, string body, CancellationToken ct, CancellationToken requestCt = default)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch
        {
            return SerializeError(null, -32700, "Parse error");
        }

        if (parsed is not JsonObject obj)
        {
            return SerializeError(null, -32600, "Invalid Request");
        }

        var hasId = obj.TryGetPropertyValue("id", out var idNode) && idNode is not null;
        var method = obj["method"]?.GetValue<string>() ?? "";
        var paramsObj = obj["params"] as JsonObject;

        if (!hasId)
        {
            // JSON-RPC notification (e.g. notifications/initialized): accept with no body.
            return null;
        }

        var idClone = idNode!.DeepClone();
        switch (method)
        {
            case "initialize":
                return SerializeResult(idClone, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject { ["listChanged"] = false },
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = _serverName,
                        ["version"] = "1.0.0",
                    },
                    ["instructions"] = _instructions ?? "Octopus local multi-agent collaboration bus. Use list_peers to inspect active sessions and available agents (e.g. codex, agy, claude), read_session to inspect context, delegate_task to assign work asynchronously to another agent (nested under you in Octopus), send_message to negotiate with an existing session, reply_thread to answer a thread, await_events to wait for parallel updates (each change is reported once), and cancel_task to stop a delegated task you no longer need.",
                });

            case "ping":
                return SerializeResult(idClone, new JsonObject());

            case "tools/list":
                return SerializeResult(idClone, new JsonObject
                {
                    ["tools"] = _tools?.Invoke() ?? _handler?.ListTools() ?? [],
                });

            case "tools/call":
            {
                var toolName = paramsObj?["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(toolName))
                {
                    return SerializeError(idClone, -32602, "Missing tool name.");
                }
                var arguments = paramsObj?["arguments"] as JsonObject;
                if (_handler is null)
                {
                    return SerializeError(idClone, -32000, "Collaboration coordinator is not initialized.");
                }
                try
                {
                    var toolCt = toolName == "await_events" && requestCt.CanBeCanceled ? requestCt : ct;
                    var result = await _handler.CallToolAsync(callerItemId, toolName, arguments, toolCt).ConfigureAwait(false);
                    return SerializeResult(idClone, result);
                }
                catch (OperationCanceledException) when (requestCt.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return SerializeResult(idClone, new JsonObject
                    {
                        ["isError"] = true,
                        ["content"] = new JsonArray
                        {
                            (JsonNode)new JsonObject { ["type"] = "text", ["text"] = ex.Message },
                        },
                    });
                }
            }

            default:
                return SerializeError(idClone, -32601, $"Method not found: {method}");
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions WireJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string SerializeResult(JsonNode id, JsonObject result) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result,
        }.ToJsonString(WireJsonOptions);

    private static string SerializeError(JsonNode? id, int code, string message) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        }.ToJsonString();

    private sealed record HttpReq(string Method, string Path, Dictionary<string, string> Headers, string Body);

    private static async Task<HttpReq?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        using var headerMs = new MemoryStream();
        var buf = new byte[4096];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n == 0) return null;
            headerMs.Write(buf, 0, n);
            if (headerMs.Length > MaxHeaderBytes) return null;
            headerEnd = FindHeaderTerminator(headerMs.GetBuffer(), (int)headerMs.Length);
        }

        var raw = headerMs.GetBuffer();
        var totalBuffered = (int)headerMs.Length;
        var headerText = Encoding.ASCII.GetString(raw, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) return null;

        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2) return null;
        var method = requestLine[0];
        var rawTarget = requestLine[1];
        var queryIdx = rawTarget.IndexOf('?');
        var path = queryIdx >= 0 ? rawTarget[..queryIdx] : rawTarget;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        var bodyStart = headerEnd + 4;
        var alreadyRead = totalBuffered - bodyStart;

        using var bodyMs = new MemoryStream();
        if (alreadyRead > 0)
        {
            bodyMs.Write(raw, bodyStart, alreadyRead);
        }

        if (headers.TryGetValue("transfer-encoding", out var te)
            && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = await ReadChunkedBodyAsync(stream, bodyMs, ct).ConfigureAwait(false);
            return new HttpReq(method, path, headers, decoded);
        }

        var contentLength = 0;
        if (headers.TryGetValue("content-length", out var clText) && int.TryParse(clText, out var parsedLen))
        {
            contentLength = Math.Clamp(parsedLen, 0, MaxBodyBytes);
        }

        while (bodyMs.Length < contentLength)
        {
            var remaining = contentLength - (int)bodyMs.Length;
            var n = await stream.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, remaining)), ct).ConfigureAwait(false);
            if (n == 0) break;
            bodyMs.Write(buf, 0, n);
        }

        var bodyStr = contentLength > 0
            ? Encoding.UTF8.GetString(bodyMs.GetBuffer(), 0, (int)Math.Min(bodyMs.Length, contentLength))
            : Encoding.UTF8.GetString(bodyMs.GetBuffer(), 0, (int)bodyMs.Length);
        return new HttpReq(method, path, headers, bodyStr);
    }

    private static async Task<string> ReadChunkedBodyAsync(NetworkStream stream, MemoryStream preBuffered, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var result = new MemoryStream();
        var pending = preBuffered.ToArray();
        var offset = 0;

        async Task EnsureBytesAsync(int needed)
        {
            while (pending.Length - offset < needed)
            {
                var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) throw new EndOfStreamException();
                var merged = new byte[(pending.Length - offset) + n];
                Buffer.BlockCopy(pending, offset, merged, 0, pending.Length - offset);
                Buffer.BlockCopy(buffer, 0, merged, pending.Length - offset, n);
                pending = merged;
                offset = 0;
            }
        }

        async Task<string> ReadLineAsync()
        {
            while (true)
            {
                for (var i = offset; i < pending.Length - 1; i++)
                {
                    if (pending[i] == '\r' && pending[i + 1] == '\n')
                    {
                        var line = Encoding.ASCII.GetString(pending, offset, i - offset);
                        offset = i + 2;
                        return line;
                    }
                }
                await EnsureBytesAsync(pending.Length - offset + 1).ConfigureAwait(false);
            }
        }

        while (true)
        {
            var sizeLine = (await ReadLineAsync().ConfigureAwait(false)).Split(';')[0].Trim();
            if (!int.TryParse(sizeLine, System.Globalization.NumberStyles.HexNumber, null, out var chunkSize) || chunkSize < 0)
            {
                break;
            }
            if (chunkSize == 0) break;
            if (result.Length + chunkSize > MaxBodyBytes) break;
            await EnsureBytesAsync(chunkSize + 2).ConfigureAwait(false);
            result.Write(pending, offset, chunkSize);
            offset += chunkSize + 2;
        }
        return Encoding.UTF8.GetString(result.GetBuffer(), 0, (int)result.Length);
    }

    private static int FindHeaderTerminator(byte[] data, int length)
    {
        for (var i = 0; i <= length - 4; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static async Task WriteHttpResponseAsync(NetworkStream stream, int statusCode, string reason, string body, CancellationToken ct)
    {
        var bodyBytes = string.IsNullOrEmpty(body) ? [] : Encoding.UTF8.GetBytes(body);
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(reason).Append("\r\n");
        if (bodyBytes.Length > 0)
        {
            sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        }
        sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private void StopLocked()
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;
        EndpointUrl = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            StopLocked();
        }
        _stop.Dispose();
    }
}
