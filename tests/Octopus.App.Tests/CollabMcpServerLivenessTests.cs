using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Octopus.App.Collab;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Xunit;

namespace Octopus.App.Tests;

public sealed class CollabMcpServerLivenessTests
{
    private static async Task<(ProjectItem Parent, string ChildId, CollabCoordinator Coordinator)> SetUpRunningDelegationAsync(
        string prefix,
        CancellationToken ct)
    {
        await Task.Yield();
        var dir = Directory.CreateTempSubdirectory(prefix).FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Coordinator", Surface = AgentSurface.Gui };
        projects.AddItem(project, parent);

        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        Assert.True(coordinator.Server.Start());

        // Keep the delegated child's fake ACP turn in flight until the test settles it explicitly or tears down.
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        fakeFactory.ConfigureNext = session =>
        {
            session.PromptHandler = async (_, _, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new Octopus.Acp.Protocol.AcpPromptResult { StopReason = "cancelled" };
            };
        };

        var res = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["title"] = "Background task", ["goal"] = "Background task", ["surface"] = "gui" },
            ct);
        Assert.Null(res["isError"]);
        var body = JsonNode.Parse(res["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        return (parent, body["session_id"]!.GetValue<string>(), coordinator);
    }

    private static string BuildPostRequest(Uri uri, string token, string jsonBody) =>
        $"POST /mcp HTTP/1.1\r\n"
        + $"Host: {uri.Host}\r\n"
        + $"Authorization: Bearer {token}\r\n"
        + "Content-Type: application/json\r\n"
        + $"Content-Length: {Encoding.UTF8.GetByteCount(jsonBody)}\r\n"
        + "\r\n"
        + jsonBody;

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, int attempts = 120)
    {
        for (var i = 0; i < attempts && !condition(); i++)
        {
            await Task.Delay(25, ct);
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Await_events_stays_alive_when_cooperative_client_sends_non_eof_request_on_same_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, childId, coordinator) = await SetUpRunningDelegationAsync("octopus-mcp-liveness-noneof-", ct);
        var token = coordinator.Server.IssueToken(parent.Id);
        var uri = new Uri(coordinator.Server.EndpointUrl!);

        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port, ct);
        var stream = client.GetStream();

        var awaitBody = """{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"await_events","arguments":{"timeout_seconds":15}}}""";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(BuildPostRequest(uri, token, awaitBody)), ct);

        await WaitUntilAsync(() => coordinator.PendingAwaitCount == 1, ct);
        Assert.Equal(1, coordinator.PendingAwaitCount);

        // Send a normal cooperative pipelined HTTP/1.1 JSON-RPC ping while await_events is still waiting (non-EOF).
        var pingBody = """{"jsonrpc":"2.0","id":12,"method":"ping"}""";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(BuildPostRequest(uri, token, pingBody)), ct);
        await stream.FlushAsync(ct);

        await Task.Delay(250, ct);
        Assert.Equal(1, coordinator.PendingAwaitCount);

        coordinator.NotifyTurnCompleted(childId, "completed after pipelined ping");

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var responseText = await reader.ReadToEndAsync(ct);
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", responseText, StringComparison.Ordinal);

        var bodySeparator = responseText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(bodySeparator > 0);
        var rpc = JsonNode.Parse(responseText[(bodySeparator + 4)..])!.AsObject();
        Assert.Equal(11, rpc["id"]!.GetValue<int>());
        var toolPayload = JsonNode.Parse(rpc["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        var task = Assert.Single(toolPayload["tasks"]!.AsArray())!;
        Assert.Equal("completed", task["status"]!.GetValue<string>());
        Assert.Contains("completed after pipelined ping", task["result"]!.GetValue<string>(), StringComparison.Ordinal);

        await WaitUntilAsync(() => coordinator.PendingAwaitCount == 0, ct);
        Assert.Equal(0, coordinator.PendingAwaitCount);
    }

    [AvaloniaFact, FreshApp]
    public async Task Await_events_still_cancels_when_client_closes_after_non_eof_bytes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, _, coordinator) = await SetUpRunningDelegationAsync("octopus-mcp-liveness-close-", ct);
        var token = coordinator.Server.IssueToken(parent.Id);
        var uri = new Uri(coordinator.Server.EndpointUrl!);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(uri.Host, uri.Port, ct);
            var stream = client.GetStream();

            var awaitBody = """{"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"await_events","arguments":{"timeout_seconds":30}}}""";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(BuildPostRequest(uri, token, awaitBody)), ct);

            await WaitUntilAsync(() => coordinator.PendingAwaitCount == 1, ct);
            Assert.Equal(1, coordinator.PendingAwaitCount);

            var pingBody = """{"jsonrpc":"2.0","id":22,"method":"ping"}""";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(BuildPostRequest(uri, token, pingBody)), ct);
            await stream.FlushAsync(ct);

            await Task.Delay(200, ct);
            Assert.Equal(1, coordinator.PendingAwaitCount);
        }

        await WaitUntilAsync(() => coordinator.PendingAwaitCount == 0, ct);
        Assert.Equal(0, coordinator.PendingAwaitCount);
    }

    [AvaloniaFact, FreshApp]
    public async Task Await_events_cancels_promptly_on_ordinary_http_client_cancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, _, coordinator) = await SetUpRunningDelegationAsync("octopus-mcp-liveness-http-", ct);
        var token = coordinator.Server.IssueToken(parent.Id);

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var requestCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var awaitBody = """{"jsonrpc":"2.0","id":31,"method":"tools/call","params":{"name":"await_events","arguments":{"timeout_seconds":40}}}""";

        using var req = new HttpRequestMessage(HttpMethod.Post, coordinator.Server.EndpointUrl)
        {
            Content = new StringContent(awaitBody, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var sendTask = http.SendAsync(req, requestCancel.Token);
        await WaitUntilAsync(() => coordinator.PendingAwaitCount == 1, ct);
        Assert.Equal(1, coordinator.PendingAwaitCount);

        await requestCancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sendTask);

        await WaitUntilAsync(() => coordinator.PendingAwaitCount == 0, ct);
        Assert.Equal(0, coordinator.PendingAwaitCount);
    }
}
