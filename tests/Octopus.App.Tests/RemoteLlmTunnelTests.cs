using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Octopus.Remote.Client;
using Octopus.Remote.Host;
using Octopus.Remote.Protocol;
using Octopus.Remote.Security;
using Octopus.Remote.Transport;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>
/// End-to-end tests for encrypted remote LLM API tunneling (design/20 §6):
/// Remote local CLI -> Remote Octopus Loopback Proxy (127.0.0.1) -> AES-256-GCM SecureChannel ->
/// Controller RemoteLlmProxyHandler (real API key injected) -> Upstream HTTP/SSE Provider.
/// </summary>
public sealed class RemoteLlmTunnelTests
{
    private const string RealApiKey = "sk-real-controller-secret-never-on-remote";
    private const string FakeCliToken = "oct-tunnel-placeholder-token";

    [Fact]
    public async Task End_to_end_json_and_sse_streaming_with_secret_injection_and_large_body()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = new MockUpstreamServer();
        upstream.Start();

        var resolver = new StubUpstreamResolver(
            new ResolvedTunnelUpstream("anthropic", upstream.BaseUrl, RealApiKey, AuthEnvPreference: "ANTHROPIC_API_KEY"));
        using var controllerHandler = new RemoteLlmProxyHandler(resolver);
        await using var remoteProxy = new RemoteLlmLoopbackProxy { ReconnectWaitTimeout = TimeSpan.FromMilliseconds(300) };

        using var controllerId = DeviceIdentity.Create();
        using var hostId = DeviceIdentity.Create();

        var (client, hostConn) = await ConnectPairAsync(controllerId, hostId, remoteProxy, controllerHandler, ct);
        await using var clientDispose = client;
        await using var hostDispose = hostConn;

        var grantToken = controllerHandler.IssueGrant(hostId.Id, "prov-1");
        var loopbackBase = remoteProxy.RegisterGrant(grantToken, controllerId.Id, hostConn.Connection);

        using var cliHttp = new HttpClient();

        // 1. Standard JSON POST: fake token stripped, real API key injected on controller!
        using (var req = new HttpRequestMessage(HttpMethod.Post, $"{loopbackBase}/v1/messages"))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FakeCliToken);
            req.Headers.TryAddWithoutValidation("x-api-key", FakeCliToken);
            req.Content = new StringContent("""{"model":"claude-sonnet-4-5","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json");
            using var resp = await cliHttp.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("hello-from-upstream", body);
            Assert.Equal(RealApiKey, upstream.LastApiKeyHeader);
            Assert.Null(upstream.LastAuthHeader);
            Assert.DoesNotContain(FakeCliToken, upstream.LastRawHeaders);
        }

        // 2. Real-time SSE streaming: chunks arrive incrementally with event-stream content-type!
        using (var req = new HttpRequestMessage(HttpMethod.Post, $"{loopbackBase}/v1/messages?stream=1"))
        {
            req.Headers.TryAddWithoutValidation("x-api-key", FakeCliToken);
            req.Content = new StringContent("""{"stream":true}""", Encoding.UTF8, "application/json");
            using var resp = await cliHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var events = new List<string>();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal)) events.Add(line);
            }
            Assert.Equal(["data: chunk-1", "data: chunk-2", "data: [DONE]"], events);
        }

        // 3. Large request body (> 32 KiB) streamed over high-bit StreamData channel!
        var bigPayload = new string('A', 80 * 1024);
        using (var req = new HttpRequestMessage(HttpMethod.Post, $"{loopbackBase}/v1/messages"))
        {
            req.Content = new StringContent(bigPayload, Encoding.UTF8, "application/json");
            using var resp = await cliHttp.SendAsync(req, ct);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(bigPayload.Length, upstream.LastBodyLength);
        }
    }

    [Fact]
    public async Task Ssrf_path_traversal_and_disallowed_paths_are_rejected_without_touching_upstream()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = new MockUpstreamServer();
        upstream.Start();

        var resolver = new StubUpstreamResolver(
            new ResolvedTunnelUpstream("anthropic", upstream.BaseUrl, RealApiKey, AuthEnvPreference: "ANTHROPIC_API_KEY"));
        using var controllerHandler = new RemoteLlmProxyHandler(resolver);
        await using var remoteProxy = new RemoteLlmLoopbackProxy { ReconnectWaitTimeout = TimeSpan.FromMilliseconds(300) };

        using var controllerId = DeviceIdentity.Create();
        using var hostId = DeviceIdentity.Create();

        var (client, hostConn) = await ConnectPairAsync(controllerId, hostId, remoteProxy, controllerHandler, ct);
        await using var clientDispose = client;
        await using var hostDispose = hostConn;

        var grantToken = controllerHandler.IssueGrant(hostId.Id, "prov-1");
        var loopbackBase = remoteProxy.RegisterGrant(grantToken, controllerId.Id, hostConn.Connection);

        using var cliHttp = new HttpClient();

        // Disallowed admin path for anthropic protocol
        using (var resp = await cliHttp.GetAsync($"{loopbackBase}/v1/admin/billing", ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        // Direct validator SSRF checks
        Assert.False(RemoteLlmProxyHandler.TryBuildSafeUpstreamUri("anthropic", "https://api.anthropic.com", "//evil.com/v1/messages", out _, out _));
        Assert.False(RemoteLlmProxyHandler.TryBuildSafeUpstreamUri("anthropic", "https://api.anthropic.com", "/v1/messages/../../etc/passwd", out _, out _));
        Assert.False(RemoteLlmProxyHandler.TryBuildSafeUpstreamUri("anthropic", "https://api.anthropic.com", "/v1/messages/%2e%2e/secret", out _, out _));
        Assert.True(RemoteLlmProxyHandler.TryBuildSafeUpstreamUri("openaiResponses", "https://api.openai.com/v1", "/v1/responses", out var deduped, out _));
        Assert.Equal("https://api.openai.com/v1/responses", deduped!.ToString());

        Assert.Equal(0, upstream.RequestCount);
    }

    [Fact]
    public async Task Disconnect_returns_503_then_reconnect_rebinds_grant_without_restarting_cli_and_revoke_blocks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = new MockUpstreamServer();
        upstream.Start();

        var resolver = new StubUpstreamResolver(
            new ResolvedTunnelUpstream("anthropic", upstream.BaseUrl, RealApiKey, AuthEnvPreference: "ANTHROPIC_API_KEY"));
        using var controllerHandler = new RemoteLlmProxyHandler(resolver);
        await using var remoteProxy = new RemoteLlmLoopbackProxy { ReconnectWaitTimeout = TimeSpan.FromMilliseconds(250) };

        using var controllerId = DeviceIdentity.Create();
        using var hostId = DeviceIdentity.Create();

        var (client1, hostConn1) = await ConnectPairAsync(controllerId, hostId, remoteProxy, controllerHandler, ct);
        var grantToken = controllerHandler.IssueGrant(hostId.Id, "prov-1");
        var loopbackBase = remoteProxy.RegisterGrant(grantToken, controllerId.Id, hostConn1.Connection);

        using var cliHttp = new HttpClient();

        // Works while connected
        using (var resp1 = await cliHttp.PostAsync($"{loopbackBase}/v1/messages", new StringContent("{}", Encoding.UTF8, "application/json"), ct))
        {
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        }

        // Controller disconnects -> proxy returns 503 controller_offline after brief wait
        await client1.DisposeAsync();
        await hostConn1.DisposeAsync();

        using (var respOffline = await cliHttp.PostAsync($"{loopbackBase}/v1/messages", new StringContent("{}", Encoding.UTF8, "application/json"), ct))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, respOffline.StatusCode);
            var errBody = await respOffline.Content.ReadAsStringAsync(ct);
            Assert.Contains("controller_offline", errBody);
        }

        // Controller reconnects -> Attach() automatically sends llm/proxy/rebind for existing grants!
        var (client2, hostConn2) = await ConnectPairAsync(controllerId, hostId, remoteProxy, controllerHandler, ct);
        await using var clientDispose2 = client2;
        await using var hostDispose2 = hostConn2;

        // Wait briefly for async rebind RPC to complete, then same loopbackBase works again!
        var recoveredStatus = HttpStatusCode.ServiceUnavailable;
        for (var i = 0; i < 10; i++)
        {
            using var resp2 = await cliHttp.PostAsync($"{loopbackBase}/v1/messages", new StringContent("{}", Encoding.UTF8, "application/json"), ct);
            recoveredStatus = resp2.StatusCode;
            if (recoveredStatus == HttpStatusCode.OK) break;
            await Task.Delay(50, ct);
        }
        Assert.Equal(HttpStatusCode.OK, recoveredStatus);

        // Revoke grant -> immediately returns 401 Unauthorized
        await controllerHandler.RevokeGrantAsync(grantToken, client2, ct);
        using (var respRevoked = await cliHttp.PostAsync($"{loopbackBase}/v1/messages", new StringContent("{}", Encoding.UTF8, "application/json"), ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, respRevoked.StatusCode);
        }
    }

    private static async Task<(RemoteHostClient Client, HostConnection Host)> ConnectPairAsync(
        DeviceIdentity controllerId,
        DeviceIdentity hostId,
        RemoteLlmLoopbackProxy remoteProxy,
        RemoteLlmProxyHandler controllerHandler,
        CancellationToken ct)
    {
        var (deviceSide, hostSide) = InMemoryTransport.CreatePair();
        var hello = new HelloMessage
        {
            Protocol = RemoteProtocol.Version,
            MinProtocol = RemoteProtocol.Version,
            MaxProtocol = RemoteProtocol.LatestVersion,
            AppVersion = "1.0.0-test",
            Capabilities = [RemoteMethods.CapabilityWorkspace, RemoteMethods.CapabilityLlmTunnel],
            HostInfo = new HostInfoMessage { Name = "RemoteLinux", Os = "linux", AppVersion = "1.0.0-test", Home = "/home/test" },
        };

        var acceptTask = Task.Run(async () =>
        {
            var channel = await SecureChannel.AcceptAsync(
                hostSide,
                hostId,
                new DeviceDescription("RemoteLinux", DeviceKind.Desktop),
                new PairedDoor(controllerId.PublicKey),
                ct);
            var hostConn = new HostConnection(new RemoteConnection(channel), new DummyWorkspace(), hello, llmProxy: remoteProxy);
            hostConn.Start();
            return hostConn;
        }, ct);

        var client = await RemoteHostClient.ConnectAsync(
            deviceSide,
            controllerId,
            new DeviceDescription("MyMac", DeviceKind.Desktop),
            PeerExpectation.Paired(hostId.PublicKey),
            hello,
            cancellationToken: ct);
        var host = await acceptTask;

        controllerHandler.Attach(hostId.Id, client);
        return (client, host);
    }

    private sealed class PairedDoor(DevicePublicKey allowed) : IAcceptPolicy
    {
        public AcceptDecision Decide(PeerOffer offer) =>
            offer.Key.Equals(allowed)
                ? AcceptDecision.Accept
                : AcceptDecision.Reject(HandshakeStatus.PairingClosed);
    }

    private sealed class StubUpstreamResolver(ResolvedTunnelUpstream upstream) : ILlmTunnelProviderResolver
    {
        public Task<ResolvedTunnelUpstream?> ResolveUpstreamAsync(string providerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ResolvedTunnelUpstream?>(upstream);
    }

    private sealed class DummyWorkspace : IHostWorkspace
    {
        public HostInfoMessage Info { get; } = new() { Name = "RemoteLinux", Os = "linux", AppVersion = "1.0.0-test", Home = "/home/test" };
        public event Action? Changed { add { } remove { } }
        public event Action<ItemMessage>? ItemChanged { add { } remove { } }
        public Task<IReadOnlyList<ProjectMessage>> ListProjectsAsync() => Task.FromResult<IReadOnlyList<ProjectMessage>>([]);
        public Task<ProjectMessage> CreateProjectAsync(CreateProjectMessage request) => throw new NotImplementedException();
        public Task<IReadOnlyList<ItemMessage>> ListItemsAsync(string projectId) => Task.FromResult<IReadOnlyList<ItemMessage>>([]);
        public Task<ItemMessage> CreateItemAsync(CreateItemMessage request) => throw new NotImplementedException();
        public Task RemoveItemAsync(string itemId) => Task.CompletedTask;
        public Task RenameItemAsync(string itemId, string? title) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentMessage>> ListAgentsAsync() => Task.FromResult<IReadOnlyList<AgentMessage>>([]);
        public Task<ISessionAttachment> AttachAsync(string itemId, int columns, int rows, bool nativeSnapshot, bool claimSize, ISessionSink sink) => throw new NotImplementedException();
        public Task RestartAsync(string itemId) => Task.CompletedTask;
        public Task<string> ResolveAsync(string path) => throw new NotImplementedException();
        public Task<IReadOnlyList<FileEntryMessage>> ListAsync(string directory) => throw new NotImplementedException();
        public Task<bool> ExistsAsync(string path) => throw new NotImplementedException();
        public Task<(byte[] Data, long Size)> ReadAsync(string path, long offset, int length) => throw new NotImplementedException();
        public Task WriteAsync(string path, string upload, byte[] data, bool last) => throw new NotImplementedException();
        public Task CreateFileAsync(string path) => throw new NotImplementedException();
        public Task CreateDirectoryAsync(string path) => throw new NotImplementedException();
        public Task RenameAsync(string path, string newPath) => throw new NotImplementedException();
        public Task DeleteAsync(string path, bool isDirectory) => throw new NotImplementedException();
        public Task<IDisposable> WatchAsync(string folder, Action changed) => throw new NotImplementedException();
        public Task<BrowseFolderResultMessage> BrowseFolderAsync(BrowseFolderMessage request) => throw new NotImplementedException();
        public Task<string> BrowseCreateDirectoryAsync(CreateBrowseFolderMessage request) => throw new NotImplementedException();
        public Task<string> GitAsync(string method, string arguments) => throw new NotImplementedException();
    }

    private sealed class MockUpstreamServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();

        public string BaseUrl { get; private set; } = "";
        public int RequestCount { get; private set; }
        public string? LastApiKeyHeader { get; private set; }
        public string? LastAuthHeader { get; private set; }
        public string LastRawHeaders { get; private set; } = "";
        public int LastBodyLength { get; private set; }

        public void Start()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _ = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                await using var stream = client.GetStream();
                var buf = new byte[4096];
                using var ms = new MemoryStream();
                var headerEnd = -1;
                while (headerEnd < 0)
                {
                    var n = await stream.ReadAsync(buf.AsMemory(), _cts.Token);
                    if (n == 0) return;
                    ms.Write(buf, 0, n);
                    headerEnd = ms.GetBuffer().AsSpan(0, (int)ms.Length).IndexOf("\r\n\r\n"u8);
                }

                var headerText = Encoding.ASCII.GetString(ms.GetBuffer(), 0, headerEnd);
                var lines = headerText.Split("\r\n");
                var target = lines[0].Split(' ')[1];

                RequestCount++;
                LastRawHeaders = headerText;
                LastApiKeyHeader = null;
                LastAuthHeader = null;
                var contentLength = 0;

                foreach (var line in lines.Skip(1))
                {
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    var k = line[..idx].Trim();
                    var v = line[(idx + 1)..].Trim();
                    if (k.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)) LastApiKeyHeader = v;
                    if (k.Equals("authorization", StringComparison.OrdinalIgnoreCase)) LastAuthHeader = v;
                    if (k.Equals("content-length", StringComparison.OrdinalIgnoreCase) && !int.TryParse(v, out contentLength)) contentLength = 0;
                }

                var bodyHave = (int)ms.Length - (headerEnd + 4);
                var totalBody = bodyHave;
                while (totalBody < contentLength)
                {
                    var n = await stream.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, contentLength - totalBody)), _cts.Token);
                    if (n == 0) break;
                    totalBody += n;
                }
                LastBodyLength = totalBody;

                if (target.Contains("stream=1", StringComparison.Ordinal))
                {
                    var head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _cts.Token);
                    foreach (var ev in new[] { "data: chunk-1\n\n", "data: chunk-2\n\n", "data: [DONE]\n\n" })
                    {
                        var bytes = Encoding.UTF8.GetBytes(ev);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{bytes.Length:x}\r\n"), _cts.Token);
                        await stream.WriteAsync(bytes, _cts.Token);
                        await stream.WriteAsync("\r\n"u8.ToArray(), _cts.Token);
                        await stream.FlushAsync(_cts.Token);
                        await Task.Delay(20, _cts.Token);
                    }
                    await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                }
                else
                {
                    var json = Encoding.UTF8.GetBytes("""{"id":"msg_1","content":[{"type":"text","text":"hello-from-upstream"}]}""");
                    var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _cts.Token);
                    await stream.WriteAsync(json, _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
