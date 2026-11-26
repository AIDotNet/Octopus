using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Acp.Protocol;
using Octopus.Acp.Transport;

namespace Octopus.Acp.Tests;

public class AcpTransportTests
{
    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task WebSocket_transport_runs_full_acp_v2_session_with_headers_and_fragmented_frames()
    {
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/acp/";
        using var http = new HttpListener();
        http.Prefixes.Add(prefix);
        http.Start();

        string? observedAuthHeader = null;
        int? observedClientVersion = null;
        var serverTask = Task.Run(async () =>
        {
            var ctx = await http.GetContextAsync();
            observedAuthHeader = ctx.Request.Headers["Authorization"];
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
            using var ws = wsCtx.WebSocket;

            async Task<JsonDocument> RecvAsync()
            {
                var buf = new byte[8192];
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, CancellationToken.None);
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                return JsonDocument.Parse(ms.ToArray());
            }

            async Task SendAsync(JsonNode node, bool fragment = false)
            {
                var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
                if (fragment && bytes.Length > 16)
                {
                    var mid = bytes.Length / 2;
                    await ws.SendAsync(bytes.AsMemory(0, mid), WebSocketMessageType.Text, endOfMessage: false, CancellationToken.None);
                    await ws.SendAsync(bytes.AsMemory(mid), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                }
                else
                {
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                }
            }

            // 1. initialize
            using (var initDoc = await RecvAsync())
            {
                observedClientVersion = initDoc.RootElement.GetProperty("params").GetProperty("protocolVersion").GetInt32();
                await SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = initDoc.RootElement.GetProperty("id").GetInt32(),
                    ["result"] = new JsonObject
                    {
                        ["protocolVersion"] = 2,
                        ["agentInfo"] = new JsonObject { ["name"] = "remote-ws-agent", ["version"] = "2.0.0" },
                    },
                });
            }

            // 2. session/new
            using (var newDoc = await RecvAsync())
            {
                await SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = newDoc.RootElement.GetProperty("id").GetInt32(),
                    ["result"] = new JsonObject { ["sessionId"] = "ws-sess-1" },
                });
            }

            // 3. session/prompt -> fragmented chunk + final reply
            using (var promptDoc = await RecvAsync())
            {
                await SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "session/update",
                    ["params"] = new JsonObject
                    {
                        ["sessionId"] = "ws-sess-1",
                        ["update"] = new JsonObject
                        {
                            ["sessionUpdate"] = "agent_message_chunk",
                            ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Hello over WS!" },
                        },
                    },
                }, fragment: true);

                await SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = promptDoc.RootElement.GetProperty("id").GetInt32(),
                    ["result"] = new JsonObject { ["stopReason"] = "end_turn" },
                });
            }

            try
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            catch { }
        });

        var events = new ConcurrentQueue<AcpRuntimeEvent>();
        await using var transport = new AcpWebSocketTransport(
            new Uri($"ws://127.0.0.1:{port}/acp/"),
            headers: new Dictionary<string, string> { ["Authorization"] = "Bearer ws-secret" });
        await using var runtime = new AcpSessionRuntime(
            transport,
            new AcpSessionRuntimeOptions { PreferredProtocolVersion = 2 });
        runtime.EventReceived += events.Enqueue;

        var started = await runtime.StartSessionAsync("/tmp/ws-repo");
        Assert.Equal("ws-sess-1", started.SessionId);
        Assert.Equal(2, runtime.Initialized!.ProtocolVersion);
        Assert.Equal(2, observedClientVersion);
        Assert.Equal("Bearer ws-secret", observedAuthHeader);

        var promptResult = await runtime.PromptAsync("ping");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("end_turn", promptResult.StopReason);
        var chunk = Assert.Single(events.OfType<AcpTextDeltaEvent>());
        Assert.Equal("Hello over WS!", chunk.Text);
    }

    [Fact]
    public async Task StreamableHttp_transport_supports_json_post_session_header_and_inline_sse()
    {
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/acp/";
        using var http = new HttpListener();
        http.Prefixes.Add(prefix);
        http.Start();

        string? observedApiKey = null;
        string? observedEchoedSessionHeader = null;
        var getHandled = 0;

        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = Task.Run(async () =>
        {
            var postsHandled = 0;
            while (postsHandled < 3 && !serverCts.IsCancellationRequested)
            {
                var ctx = await http.GetContextAsync().WaitAsync(serverCts.Token);
                if (string.Equals(ctx.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref getHandled);
                    ctx.Response.StatusCode = 405;
                    ctx.Response.Close();
                    continue;
                }

                using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await sr.ReadToEndAsync(serverCts.Token);
                using var doc = JsonDocument.Parse(body);
                var method = doc.RootElement.GetProperty("method").GetString();
                var id = doc.RootElement.GetProperty("id").GetInt32();
                postsHandled++;

                if (method == "initialize")
                {
                    observedApiKey = ctx.Request.Headers["X-Api-Key"];
                    ctx.Response.Headers["Acp-Session-Id"] = "remote-hdr-99";
                    ctx.Response.ContentType = "application/json";
                    var payload = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id,
                        ["result"] = new JsonObject
                        {
                            ["protocolVersion"] = 2,
                            ["agentInfo"] = new JsonObject { ["name"] = "remote-http-agent", ["version"] = "2.0.0" },
                        },
                    }.ToJsonString();
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    await ctx.Response.OutputStream.WriteAsync(bytes, serverCts.Token);
                    ctx.Response.Close();
                }
                else if (method == "session/new")
                {
                    observedEchoedSessionHeader = ctx.Request.Headers["Acp-Session-Id"];
                    ctx.Response.ContentType = "application/json";
                    var payload = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id,
                        ["result"] = new JsonObject { ["sessionId"] = "http-sess-7" },
                    }.ToJsonString();
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    await ctx.Response.OutputStream.WriteAsync(bytes, serverCts.Token);
                    ctx.Response.Close();
                }
                else if (method == "session/prompt")
                {
                    ctx.Response.ContentType = "text/event-stream";
                    var notif = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["method"] = "session/update",
                        ["params"] = new JsonObject
                        {
                            ["sessionId"] = "http-sess-7",
                            ["update"] = new JsonObject
                            {
                                ["sessionUpdate"] = "agent_message_chunk",
                                ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Hello via SSE!" },
                            },
                        },
                    }.ToJsonString();
                    var reply = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id,
                        ["result"] = new JsonObject { ["stopReason"] = "end_turn" },
                    }.ToJsonString();

                    var sse = $": heartbeat\ndata: {notif}\n\ndata: {reply}\n\n";
                    var bytes = Encoding.UTF8.GetBytes(sse);
                    await ctx.Response.OutputStream.WriteAsync(bytes, serverCts.Token);
                    ctx.Response.Close();
                }
            }
        });

        var events = new ConcurrentQueue<AcpRuntimeEvent>();
        await using var transport = new AcpStreamableHttpTransport(
            new Uri(prefix),
            headers: new Dictionary<string, string> { ["X-Api-Key"] = "http-key-42" },
            enableSseStream: true);
        await using var runtime = new AcpSessionRuntime(
            transport,
            new AcpSessionRuntimeOptions { PreferredProtocolVersion = 2 });
        runtime.EventReceived += events.Enqueue;

        var started = await runtime.StartSessionAsync("/tmp/http-repo");
        Assert.Equal("http-sess-7", started.SessionId);
        Assert.Equal("http-key-42", observedApiKey);
        Assert.Equal("remote-hdr-99", transport.RemoteSessionHeader);
        Assert.Equal("remote-hdr-99", observedEchoedSessionHeader);

        var promptResult = await runtime.PromptAsync("stream please");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("end_turn", promptResult.StopReason);
        var chunk = Assert.Single(events.OfType<AcpTextDeltaEvent>());
        Assert.Equal("Hello via SSE!", chunk.Text);
        Assert.True(getHandled >= 0);
    }
}
