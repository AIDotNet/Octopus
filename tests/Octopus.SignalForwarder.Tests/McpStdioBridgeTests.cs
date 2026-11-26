using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Octopus.SignalForwarder.Tests;

public sealed class McpStdioBridgeTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string? Auth, string Body)> Received { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Received.Add((request.Headers.Authorization?.ToString(), body));
            return respond(request);
        }
    }

    [Fact]
    public async Task Forwards_ndjson_requests_with_bearer_token_and_swallows_202_notifications()
    {
        var handler = new FakeHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("id", out _))
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"result":{"ok":true}}""", Encoding.UTF8, "application/json"),
            };
        });

        var input = """
            {"jsonrpc":"2.0","id":1,"method":"ping"}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            """;
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(input + "\n"));
        using var stdout = new MemoryStream();
        var env = new Dictionary<string, string>
        {
            [McpStdioBridge.UrlVariable] = "http://127.0.0.1:45678/mcp",
            [McpStdioBridge.TokenVariable] = "secret-token-123",
        };

        var code = await McpStdioBridge.RunAsync(["--mcp"], env, stdin, stdout, handler, TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.Equal(2, handler.Received.Count);
        Assert.Equal("Bearer secret-token-123", handler.Received[0].Auth);

        var lines = Encoding.UTF8.GetString(stdout.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Contains("\"ok\":true", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emits_jsonrpc_error_when_gateway_returns_http_error_for_request()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":7,"method":"tools/list"}""" + "\n"));
        using var stdout = new MemoryStream();
        var env = new Dictionary<string, string>
        {
            [McpStdioBridge.UrlVariable] = "http://127.0.0.1:45678/mcp",
            [McpStdioBridge.TokenVariable] = "bad",
        };

        var code = await McpStdioBridge.RunAsync(["--mcp"], env, stdin, stdout, handler, TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        var output = Encoding.UTF8.GetString(stdout.ToArray()).Trim();
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(7, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(-32000, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }
}
