using System.Net;
using System.Net.Sockets;
using System.Text;
using Octopus.Agents.Providers;
using Octopus.Core.Agents;
using Octopus.Platform;
using Octopus.Storage;

namespace Octopus.Agents.Tests;

public sealed class CoWorkGatewayProxyTests
{
    [Fact]
    public async Task Proxy_translates_x_api_key_to_bearer_streams_sse_and_retries_on_401()
    {
        var upstreamPort = FreePort();
        using var upstreamListener = new HttpListener();
        upstreamListener.Prefixes.Add($"http://127.0.0.1:{upstreamPort}/");
        upstreamListener.Start();

        var receivedAuthHeaders = new List<string>();
        var receivedApiKeyHeaders = new List<string?>();
        var receivedUserAgents = new List<string?>();
        var upstreamCalls = 0;

        using var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await upstreamListener.GetContextAsync(); }
                catch { break; }

                var call = Interlocked.Increment(ref upstreamCalls);
                receivedAuthHeaders.Add(ctx.Request.Headers["Authorization"] ?? "");
                receivedApiKeyHeaders.Add(ctx.Request.Headers["x-api-key"]);
                receivedUserAgents.Add(ctx.Request.Headers["User-Agent"]);

                if (call == 1)
                {
                    ctx.Response.StatusCode = 401;
                    var err = "{\"error\":{\"code\":\"token_expired\"}}"u8.ToArray();
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = err.Length;
                    await ctx.Response.OutputStream.WriteAsync(err);
                    ctx.Response.Close();
                    continue;
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.SendChunked = true;
                var chunk1 = Encoding.UTF8.GetBytes("data: {\"delta\":\"hello\"}\n\n");
                var chunk2 = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
                await ctx.Response.OutputStream.WriteAsync(chunk1);
                await ctx.Response.OutputStream.FlushAsync();
                await ctx.Response.OutputStream.WriteAsync(chunk2);
                await ctx.Response.OutputStream.FlushAsync();
                ctx.Response.Close();
            }
        });

        var tokenCalls = 0;
        using var proxy = new CoWorkGatewayProxy((forceRefresh, _) =>
        {
            var seq = Interlocked.Increment(ref tokenCalls);
            var jwt = seq == 1 ? "delegated-jwt-initial" : "delegated-jwt-refreshed";
            return Task.FromResult<CoWorkGatewayUpstream?>(
                new CoWorkGatewayUpstream(new Uri($"http://127.0.0.1:{upstreamPort}"), jwt, DateTimeOffset.UtcNow.AddHours(1)));
        }, userAgent: "Octopus/2.0.0");

        proxy.EnsureStarted();
        Assert.True(proxy.Port > 0);

        using var client = new HttpClient();

        // Unauthorized local caller (wrong x-api-key) is rejected locally without touching upstream.
        using (var badReq = new HttpRequestMessage(HttpMethod.Post, proxy.AnthropicBaseUrl + "/v1/messages"))
        {
            badReq.Headers.TryAddWithoutValidation("x-api-key", "wrong-key");
            badReq.Content = new StringContent("{\"model\":\"claude-sonnet-4-6\"}", Encoding.UTF8, "application/json");
            using var badResp = await client.SendAsync(badReq);
            Assert.Equal(HttpStatusCode.Unauthorized, badResp.StatusCode);
            Assert.Equal(0, upstreamCalls);
        }

        // Valid x-api-key caller: first upstream call returns 401 -> proxy refreshes JWT -> retries -> streams SSE.
        using (var req = new HttpRequestMessage(HttpMethod.Post, proxy.AnthropicBaseUrl + "/v1/messages"))
        {
            req.Headers.TryAddWithoutValidation("x-api-key", proxy.LocalApiKey);
            req.Content = new StringContent("{\"model\":\"claude-sonnet-4-6\",\"stream\":true}", Encoding.UTF8, "application/json");
            using var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("hello", body, StringComparison.Ordinal);
            Assert.Contains("[DONE]", body, StringComparison.Ordinal);
        }

        Assert.Equal(2, upstreamCalls);
        Assert.Equal(2, tokenCalls);
        Assert.Equal("Bearer delegated-jwt-initial", receivedAuthHeaders[0]);
        Assert.Equal("Bearer delegated-jwt-refreshed", receivedAuthHeaders[1]);
        Assert.All(receivedApiKeyHeaders, h => Assert.Null(h));
        Assert.All(receivedUserAgents, ua => Assert.Equal("Octopus/2.0.0", ua));

        serverCts.Cancel();
        upstreamListener.Stop();
        await serverTask;
    }

    [Fact]
    public async Task AgentBackendResolver_materializes_cowork_provider_via_live_bridge()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "octopus-cw-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new InMemoryProviderStore();
            var secrets = new NoSecretStore();
            var svc = new ModelProviderService(store, secrets);
            await svc.SaveAsync(new ModelProviderRecord
            {
                Id = CoWorkProviderIds.Anthropic,
                Name = "CoWork (Anthropic)",
                Protocol = ProviderProtocol.Anthropic,
                BaseUrl = "http://127.0.0.1:1", // stale port from previous launch
                AuthEnvPreference = "ANTHROPIC_AUTH_TOKEN",
                Enabled = true,
                IsolateConfigDir = false,
                Models = [new ManagedModelSpec { Id = "claude-sonnet-4-6", Enabled = true, Tier = ModelSlotTier.Balanced }],
            });

            var paths = new FakeAppPaths(tempDir);
            var bridge = new FakeCoWorkBridge("http://127.0.0.1:45123", "oct-cw-ephemeral-secret");
            var resolver = new AgentBackendResolver(svc, new ProviderMaterializer(paths, tempDir), accounts: null, cowork: bridge);

            var claudeDef = new AgentDefinition
            {
                Id = "claude",
                Name = "Claude Code",
                ProviderSupport = new AgentProviderCapability
                {
                    Protocols = ["anthropic"],
                    Strategy = ProviderMaterializeStrategies.Env,
                    BaseUrlEnv = "ANTHROPIC_BASE_URL",
                    ApiKeyEnv = "ANTHROPIC_API_KEY",
                    AltApiKeyEnv = "ANTHROPIC_AUTH_TOKEN",
                    ModelEnv = "ANTHROPIC_MODEL",
                },
            };

            var resolved = await resolver.ResolveAsync(
                claudeDef,
                new AgentProfile { DefaultBackend = AgentBackendRefs.Provider(CoWorkProviderIds.Anthropic) });

            Assert.True(resolved.IsProvider);
            Assert.NotNull(resolved.Environment);
            Assert.Equal("http://127.0.0.1:45123", resolved.Environment!["ANTHROPIC_BASE_URL"]);
            Assert.Equal("oct-cw-ephemeral-secret", resolved.Environment["ANTHROPIC_AUTH_TOKEN"]);
            Assert.False(resolved.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static int FreePort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        return ((IPEndPoint)tcp.LocalEndpoint).Port;
    }

    private sealed class FakeCoWorkBridge(string baseUrl, string localKey) : ICoWorkProviderBridge
    {
        public Task<CoWorkRuntimeEndpoint?> ResolveRuntimeEndpointAsync(ProviderProtocol protocol, CancellationToken cancellationToken = default) =>
            Task.FromResult<CoWorkRuntimeEndpoint?>(new CoWorkRuntimeEndpoint(baseUrl, localKey));
    }

    private sealed class FakeAppPaths(string dir) : IAppPaths
    {
        public string DataDirectory => dir;
        public string CacheDirectory => dir;
        public string LogDirectory => dir;
    }
}
