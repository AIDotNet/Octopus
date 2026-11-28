using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Octopus.Acp.Protocol;
using Octopus.Agents;
using Octopus.Agents.Signals;
using Octopus.App.AgentGui;
using Octopus.App.Collab;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Settings.Agents;
using Octopus.App.Shell;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

public sealed class CollabTests
{
    private sealed class FakeGuiBridge(string itemId, string agentId, string title, string cwd) : ICollabSessionBridge
    {
        public string ItemId => itemId;
        public string AgentId => agentId;
        public string DisplayTitle => title;
        public string WorkingDirectory => cwd;
        public bool IsBusy { get; set; }
        public bool IsConnected { get; set; } = true;
        public string? CurrentStatusDetail { get; set; }
        private readonly object _promptGate = new();
        public List<string> DeliveredPrompts { get; } = [];
        public List<(string? ThreadId, string? TaskId)> CancelledLookups { get; } = [];
        public List<string> CancelledDeliveryIds { get; } = [];
        public string Summary { get; set; } = "Recent summary from fake GUI bridge.";
        public Func<string?, string?, CancellationToken, Task<bool>>? OnDeliver { get; set; }
        public bool SupportsExactDeliveryWithdrawal => true;

        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default)
        {
            lock (_promptGate)
            {
                DeliveredPrompts.Add(promptText);
            }
            return OnDeliver is { } handler
                ? handler(threadId, taskId, cancellationToken)
                : Task.FromResult(true);
        }

        public bool HasPromptContaining(string value)
        {
            lock (_promptGate)
            {
                return DeliveredPrompts.Any(p => p.Contains(value, StringComparison.Ordinal));
            }
        }

        public int CancelQueuedCollabPrompts(string? threadId = null, string? taskId = null)
        {
            CancelledLookups.Add((threadId, taskId));
            return 1;
        }

        public int CancelQueuedCollabPrompts(string? threadId, string? taskId, string? deliveryId)
        {
            CancelledLookups.Add((threadId, taskId));
            if (!string.IsNullOrWhiteSpace(deliveryId))
            {
                CancelledDeliveryIds.Add(deliveryId);
            }
            return 1;
        }

        public string ReadRecentSummary(int maxTurns = 5) => Summary;

        public int TurnCancellations;

        public Task CancelActiveTurnAsync()
        {
            Interlocked.Increment(ref TurnCancellations);
            return Task.CompletedTask;
        }
    }

    private static JsonObject Body(JsonObject toolResult) =>
        JsonNode.Parse(toolResult["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

    private static async Task<(ProjectItem Parent, FakeGuiBridge Bridge, CollabCoordinator Coordinator, string Dir)> SetUpParentAsync(string prefix)
    {
        await Task.Yield();
        var dir = Directory.CreateTempSubdirectory(prefix).FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Main", Surface = AgentSurface.Gui };
        projects.AddItem(project, parent);
        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        var bridge = new FakeGuiBridge(parent.Id, "claude", parent.Title, dir);
        coordinator.RegisterGuiSession(bridge);
        return (parent, bridge, coordinator, dir);
    }

    /// <summary>
    /// Keeps fake ACP turns for delegated GUI children in flight until the test explicitly settles them
    /// (via <c>NotifyTurnCompleted</c>, <c>NotifyTurnAborted</c>, <c>reply_thread</c>, or <c>cancel_task</c>) or tears down.
    /// </summary>
    private static void HoldDelegatedGuiTurnsInFlight()
    {
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        fakeFactory.ConfigureNext = session =>
        {
            session.PromptHandler = async (_, _, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new Octopus.Acp.Protocol.AcpPromptResult { StopReason = "cancelled" };
            };
        };
    }

    private static async Task<JsonObject> DelegateAsync(CollabCoordinator coordinator, string callerId, string title, CancellationToken ct)
    {
        HoldDelegatedGuiTurnsInFlight();
        var res = await coordinator.CallToolAsync(
            callerId,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["title"] = title, ["goal"] = title, ["surface"] = "gui" },
            ct);
        Assert.Null(res["isError"]);
        return Body(res);
    }

    private static Task<JsonObject> AwaitEventsAsync(CollabCoordinator coordinator, string callerId, int timeoutSeconds, CancellationToken ct, params string[] ids)
    {
        var args = new JsonObject { ["timeout_seconds"] = timeoutSeconds };
        if (ids.Length > 0) args["ids"] = new JsonArray(ids.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());
        return coordinator.CallToolAsync(callerId, "await_events", args, ct);
    }

    [AvaloniaFact, FreshApp]
    public async Task Await_events_reports_a_finished_task_once_and_returns_at_once_when_nothing_is_pending()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, _, coordinator, _) = await SetUpParentAsync("octopus-collab-once-");
        var delegated = await DelegateAsync(coordinator, parent.Id, "计算 1+1", ct);
        var taskId = delegated["task_id"]!.GetValue<string>();
        var childId = delegated["session_id"]!.GetValue<string>();

        // Tool results keep CJK and '+' readable instead of \uXXXX escapes.
        var raw = (await AwaitEventsAsync(coordinator, parent.Id, 1, ct))["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("计算 1+1", raw, StringComparison.Ordinal);

        coordinator.NotifyTurnCompleted(childId, "1 + 1 = 2");
        var first = Body(await AwaitEventsAsync(coordinator, parent.Id, 5, ct));
        var reported = Assert.Single(first["tasks"]!.AsArray());
        Assert.Equal(taskId, reported!["task_id"]!.GetValue<string>());
        Assert.Equal("completed", reported["status"]!.GetValue<string>());
        Assert.True(reported["new"]!.GetValue<bool>());

        // The same completion is not reported again, and with nothing pending the call does not sit out its timeout.
        var started = DateTimeOffset.UtcNow;
        var second = Body(await AwaitEventsAsync(coordinator, parent.Id, 20, ct));
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Empty(second["tasks"]!.AsArray());
        Assert.NotNull(second["note"]);

        // Naming the task explicitly still shows it (as already seen), and an unknown id is called out.
        var named = Body(await AwaitEventsAsync(coordinator, parent.Id, 20, ct, taskId, "task_404_dead"));
        Assert.False(Assert.Single(named["tasks"]!.AsArray())!["new"]!.GetValue<bool>());
        Assert.Equal("task_404_dead", Assert.Single(named["unknown_ids"]!.AsArray())!.GetValue<string>());
    }

    [AvaloniaFact, FreshApp]
    public async Task Cancel_task_stops_the_delegate_and_cascades_to_what_it_delegated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, _, coordinator, dir) = await SetUpParentAsync("octopus-collab-cancel-");
        var child = await DelegateAsync(coordinator, parent.Id, "Refactor login", ct);
        var childId = child["session_id"]!.GetValue<string>();
        var childBridge = new FakeGuiBridge(childId, "codex", "Refactor login", dir);
        coordinator.RegisterGuiSession(childBridge);
        var grandchild = await DelegateAsync(coordinator, childId, "Write tests", ct);
        var grandchildId = grandchild["session_id"]!.GetValue<string>();
        var grandchildBridge = new FakeGuiBridge(grandchildId, "codex", "Write tests", dir);
        coordinator.RegisterGuiSession(grandchildBridge);

        // Only the delegator may cancel.
        var denied = await coordinator.CallToolAsync(grandchildId, "cancel_task", new JsonObject { ["task_id"] = child["task_id"]!.GetValue<string>() }, ct);
        Assert.True(denied["isError"]?.GetValue<bool>());

        var res = Body(await coordinator.CallToolAsync(
            parent.Id,
            "cancel_task",
            new JsonObject { ["task_id"] = child["task_id"]!.GetValue<string>(), ["reason"] = "User changed plans" },
            ct));
        Assert.Equal("cancelled", res["status"]!.GetValue<string>());
        Assert.Equal(2, res["cancelled_count"]!.GetValue<int>());

        var childTask = coordinator.GetTasksForItem(parent.Id).Single();
        Assert.Equal(CollabTaskStatus.Cancelled, childTask.Status);
        Assert.Equal("User changed plans", childTask.ResultSummary);
        Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(grandchildId).Single().Status);
        Assert.Equal(CollabThreadStatus.Cancelled, coordinator.FindThread(child["thread_id"]!.GetValue<string>())!.Status);

        for (var i = 0; i < 40 && (Volatile.Read(ref childBridge.TurnCancellations) == 0 || Volatile.Read(ref grandchildBridge.TurnCancellations) == 0); i++)
        {
            await Task.Delay(25, ct);
        }
        Assert.Equal(1, childBridge.TurnCancellations);
        Assert.Equal(1, grandchildBridge.TurnCancellations);

        // A late answer from the stopped child does not resurrect the task.
        coordinator.NotifyTurnCompleted(childId, "done anyway");
        Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(parent.Id).Single().Status);
    }

    [AvaloniaFact, FreshApp]
    public async Task Stopping_a_delegate_settles_its_task_and_tells_the_delegator()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, parentBridge, coordinator, _) = await SetUpParentAsync("octopus-collab-stop-");
        var child = await DelegateAsync(coordinator, parent.Id, "Long audit", ct);
        var childId = child["session_id"]!.GetValue<string>();
        coordinator.RegisterGuiSession(parentBridge);

        coordinator.NotifyTurnAborted(childId, "The user stopped this session before it finished.", failed: false);

        var task = coordinator.GetTasksForItem(parent.Id).Single();
        Assert.Equal(CollabTaskStatus.Cancelled, task.Status);
        for (var i = 0; i < 40 && parentBridge.DeliveredPrompts.Count == 0; i++)
        {
            await Task.Delay(25, ct);
        }
        Assert.Contains(parentBridge.DeliveredPrompts, p => p.Contains("Status: cancelled", StringComparison.Ordinal));

        // Stopping the parent's turn cancels what it delegated during that turn, but not older delegations.
        var older = await DelegateAsync(coordinator, parent.Id, "Older task", ct);
        var since = DateTimeOffset.UtcNow;
        await Task.Delay(5, ct);
        var newer = await DelegateAsync(coordinator, parent.Id, "Newer task", ct);
        coordinator.NotifyTurnAborted(parent.Id, "stopped", failed: false, cancelDelegationsSince: since);
        for (var i = 0; i < 40 && coordinator.GetTasksForItem(parent.Id).First(t => t.TaskId == newer["task_id"]!.GetValue<string>()).Status != CollabTaskStatus.Cancelled; i++)
        {
            await Task.Delay(25, ct);
        }
        var tasks = coordinator.GetTasksForItem(parent.Id);
        Assert.Equal(CollabTaskStatus.Cancelled, tasks.First(t => t.TaskId == newer["task_id"]!.GetValue<string>()).Status);
        Assert.Equal(CollabTaskStatus.Running, tasks.First(t => t.TaskId == older["task_id"]!.GetValue<string>()).Status);
    }

    [AvaloniaFact, FreshApp]
    public async Task Replies_in_a_three_way_thread_go_to_whoever_asked_and_outsiders_cannot_reply()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-collab-route-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var a = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "A", Surface = AgentSurface.Gui };
        var b = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "B", Surface = AgentSurface.Gui };
        var c = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "agy", Title = "C", Surface = AgentSurface.Gui };
        var outsider = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "gemini", Title = "D", Surface = AgentSurface.Gui };
        foreach (var item in new[] { a, b, c, outsider }) projects.AddItem(project, item, activate: false);
        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        var bridges = new[] { a, b, c, outsider }.ToDictionary(i => i.Id, i => new FakeGuiBridge(i.Id, i.AgentId!, i.Title, dir));
        foreach (var bridge in bridges.Values) coordinator.RegisterGuiSession(bridge);

        var first = Body(await coordinator.CallToolAsync(a.Id, "send_message", new JsonObject { ["target_session_id"] = b.Id, ["message"] = "B, check the API" }, ct));
        var threadId = first["thread_id"]!.GetValue<string>();
        await coordinator.CallToolAsync(b.Id, "send_message", new JsonObject { ["target_session_id"] = c.Id, ["thread_id"] = threadId, ["message"] = "C, what does the client send?" }, ct);

        // C answers B (who asked it), not A (the first other participant).
        var reply = Body(await coordinator.CallToolAsync(c.Id, "reply_thread", new JsonObject { ["thread_id"] = threadId, ["message"] = "camelCase" }, ct));
        Assert.Equal(b.Id, reply["delivered_to"]!.GetValue<string>());
        Assert.Contains(bridges[b.Id].DeliveredPrompts, p => p.Contains("camelCase", StringComparison.Ordinal));
        Assert.DoesNotContain(bridges[a.Id].DeliveredPrompts, p => p.Contains("camelCase", StringComparison.Ordinal));

        // Like replying to an email: B's reply answers the latest message B received (C's), not the thread's first.
        var back = Body(await coordinator.CallToolAsync(b.Id, "reply_thread", new JsonObject { ["thread_id"] = threadId, ["message"] = "Thanks C" }, ct));
        Assert.Equal(c.Id, back["delivered_to"]!.GetValue<string>());

        // A participant nobody has written to yet answers the thread's initiator.
        var thread = coordinator.FindThread(threadId)!;
        thread.ParticipantItemIds.Add(outsider.Id);
        var fromNewcomer = Body(await coordinator.CallToolAsync(outsider.Id, "reply_thread", new JsonObject { ["thread_id"] = threadId, ["message"] = "FYI" }, ct));
        Assert.Equal(a.Id, fromNewcomer["delivered_to"]!.GetValue<string>());
        thread.ParticipantItemIds.Remove(outsider.Id);

        var denied = await coordinator.CallToolAsync(outsider.Id, "reply_thread", new JsonObject { ["thread_id"] = threadId, ["message"] = "hijack" }, ct);
        Assert.True(denied["isError"]?.GetValue<bool>());
    }

    [AvaloniaFact, FreshApp]
    public async Task An_automatic_end_of_turn_report_says_how_to_answer_and_a_follow_up_reopens_the_task()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, parentBridge, coordinator, dir) = await SetUpParentAsync("octopus-collab-reopen-");
        var child = await DelegateAsync(coordinator, parent.Id, "Add persistence", ct);
        var childId = child["session_id"]!.GetValue<string>();
        var threadId = child["thread_id"]!.GetValue<string>();
        coordinator.RegisterGuiSession(parentBridge);
        var childBridge = new FakeGuiBridge(childId, "codex", "Add persistence", dir);
        coordinator.RegisterGuiSession(childBridge);

        // The child ends its turn with a question instead of calling reply_thread.
        coordinator.NotifyTurnCompleted(childId, "Should I use SQLite or Postgres?");
        for (var i = 0; i < 40 && parentBridge.DeliveredPrompts.Count == 0; i++) await Task.Delay(25, ct);
        var wakeup = Assert.Single(parentBridge.DeliveredPrompts);
        Assert.Contains($"send_message(target_session_id=\"{childId}\", thread_id=\"{threadId}\"", wakeup, StringComparison.Ordinal);
        Assert.Equal(CollabTaskStatus.Completed, coordinator.GetTasksForItem(parent.Id).Single().Status);

        // The timeline still shows only the reply text, not the instruction footer.
        Assert.Equal("Should I use SQLite or Postgres?", new Octopus.App.AgentGui.UserTurnItem(wakeup, onReuse: _ => { }).DisplayText);

        // Answering on the same thread reopens the task; the child's next turn completes it with the real result.
        await coordinator.CallToolAsync(parent.Id, "send_message", new JsonObject { ["target_session_id"] = childId, ["thread_id"] = threadId, ["message"] = "SQLite" }, ct);
        var reopened = coordinator.GetTasksForItem(parent.Id).Single();
        Assert.Equal(CollabTaskStatus.Running, reopened.Status);
        Assert.Contains(childBridge.DeliveredPrompts, p => p.Contains("SQLite", StringComparison.Ordinal));

        coordinator.NotifyTurnCompleted(childId, "Done with SQLite.");
        for (var i = 0; i < 40 && coordinator.GetTasksForItem(parent.Id).Single().Status != CollabTaskStatus.Completed; i++) await Task.Delay(25, ct);
        var done = coordinator.GetTasksForItem(parent.Id).Single();
        Assert.Equal(CollabTaskStatus.Completed, done.Status);
        Assert.Equal("Done with SQLite.", done.ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Await_events_stops_waiting_when_the_mcp_client_hangs_up()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, _, coordinator, _) = await SetUpParentAsync("octopus-collab-hangup-");
        await DelegateAsync(coordinator, parent.Id, "Slow work", ct);
        var token = coordinator.Server.IssueToken(parent.Id);
        var uri = new Uri(coordinator.Server.EndpointUrl!);

        using (var client = new System.Net.Sockets.TcpClient())
        {
            await client.ConnectAsync(uri.Host, uri.Port, ct);
            var body = """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"await_events","arguments":{"timeout_seconds":40}}}""";
            var request = $"POST /mcp HTTP/1.1\r\nHost: {uri.Host}\r\nAuthorization: Bearer {token}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(request), ct);
            for (var i = 0; i < 80 && coordinator.PendingAwaitCount == 0; i++) await Task.Delay(25, ct);
            Assert.Equal(1, coordinator.PendingAwaitCount);
        }

        for (var i = 0; i < 120 && coordinator.PendingAwaitCount > 0; i++) await Task.Delay(25, ct);
        Assert.Equal(0, coordinator.PendingAwaitCount);
    }

    private sealed class FakeRemoteHosts : ICollabRemoteHosts
    {
        public Dictionary<string, FakeRemoteHost> Hosts { get; } = new(StringComparer.Ordinal);
        public List<string> Syncs { get; } = [];

        public FakeRemoteHost Add(string hostId, string name) => Hosts[hostId] = new FakeRemoteHost(name);

        public Task<ICollabRemoteHost> ConnectAsync(string hostId, CancellationToken ct) =>
            Hosts.TryGetValue(hostId, out var host)
                ? Task.FromResult<ICollabRemoteHost>(host)
                : Task.FromException<ICollabRemoteHost>(new IOException("offline"));

        public string HostName(string hostId) => Hosts.TryGetValue(hostId, out var host) ? host.Name : hostId;

        public void RequestSync(string hostId) => Syncs.Add(hostId);
    }

    private sealed class FakeRemoteHost(string name) : ICollabRemoteHost
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);
        private int _items;

        public string Name => name;
        public bool SupportsAcpGui { get; set; } = true;
        public bool SupportsScopedCancel { get; set; } = true;
        public Task Completion => _closed.Task;
        public event Action<Octopus.Remote.Protocol.AcpUpdateMessage>? AcpUpdated;
        public List<Octopus.Remote.Protocol.CreateItemMessage> Created { get; } = [];
        public List<string> Attached { get; } = [];
        public List<(string ItemId, string Text)> Prompts { get; } = [];
        public System.Collections.Concurrent.ConcurrentQueue<string> Cancels { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<Octopus.Remote.Protocol.AcpRespondMessage> Responses { get; } = new();
        public Dictionary<string, Octopus.Remote.Protocol.AcpSnapshotMessage> Snapshots { get; } = new(StringComparer.Ordinal);

        public Task<Octopus.Remote.Protocol.ItemMessage> CreateItemAsync(Octopus.Remote.Protocol.CreateItemMessage request, CancellationToken ct)
        {
            Created.Add(request);
            var id = $"remote-item-{++_items}";
            return Task.FromResult(new Octopus.Remote.Protocol.ItemMessage { Id = id, ProjectId = request.ProjectId, Kind = request.Kind, AgentId = request.AgentId, Title = request.AgentId ?? "" });
        }

        public Task<Octopus.Remote.Protocol.AcpSnapshotMessage> AttachConversationAsync(string itemId, CancellationToken ct)
        {
            Attached.Add(itemId);
            var snapshot = Snapshots.TryGetValue(itemId, out var configured)
                ? configured
                : new Octopus.Remote.Protocol.AcpSnapshotMessage { ItemId = itemId, State = Octopus.Remote.Protocol.AcpStates.Connecting };
            _sequences[itemId] = snapshot.Sequence;
            return Task.FromResult(snapshot);
        }

        public Task DetachConversationAsync(string itemId, CancellationToken ct) => Task.CompletedTask;

        public Task SendPromptAsync(string itemId, string text, CancellationToken ct)
        {
            Prompts.Add((itemId, text));
            return Task.CompletedTask;
        }

        public Task CancelTurnAsync(string itemId, CancellationToken ct)
        {
            Cancels.Enqueue(itemId);
            return Task.CompletedTask;
        }

        public Task CancelTurnAsync(string itemId, string? collabThreadId, string? collabTaskId, string? collabDeliveryId, CancellationToken ct) =>
            CancelTurnAsync(itemId, ct);

        public Task RespondAsync(Octopus.Remote.Protocol.AcpRespondMessage answer, CancellationToken ct)
        {
            Responses.Enqueue(answer);
            return Task.CompletedTask;
        }

        /// <summary>Sends the next update of <paramref name="itemId"/> (its sequence is filled in).</summary>
        public void Push(string itemId, string? state, params Octopus.Remote.Protocol.AcpEntryMessage[] upserts)
        {
            var seq = _sequences[itemId] = _sequences.GetValueOrDefault(itemId) + 1;
            AcpUpdated?.Invoke(new Octopus.Remote.Protocol.AcpUpdateMessage { ItemId = itemId, Sequence = seq, State = state, Upserts = [.. upserts] });
        }

        public void Disconnect() => _closed.TrySetResult();
    }

    private static Octopus.Remote.Protocol.AcpEntryMessage UserEntry(string id, string text) =>
        new() { Id = id, Kind = Octopus.Remote.Protocol.AcpEntryKinds.User, Text = text };

    private static Octopus.Remote.Protocol.AcpEntryMessage AnswerEntry(string id, string text, bool streaming = false) =>
        new() { Id = id, Kind = Octopus.Remote.Protocol.AcpEntryKinds.Assistant, Text = text, IsStreaming = streaming };

    private static async Task<(CollabCoordinator Coordinator, ProjectItem Parent, FakeGuiBridge ParentBridge, Project Remote, FakeRemoteHost Host)> SetUpRemoteAsync(string prefix)
    {
        await Task.Yield();
        var dir = Directory.CreateTempSubdirectory(prefix).FullName;
        var projects = AppHost.Get<IProjectService>();
        var local = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Lead", Surface = AgentSurface.Gui };
        projects.AddItem(local, parent);
        // A project of a paired host, made without AddRemoteProject: that one makes it current, and the app's real
        // RemoteProjects then keeps reconnecting to the made-up host in the background, past the end of the test.
        var remote = projects.AddProject(Directory.CreateTempSubdirectory(prefix + "remote-").FullName);
        remote.RemoteHost = "host-1";
        remote.RemoteId = "rp-1";
        remote.Name = "App on server";
        projects.SetCurrent(local.Id);
        var fakes = new FakeRemoteHosts();
        var host = fakes.Add("host-1", "Build Server");
        var coordinator = new CollabCoordinator(
            projects,
            AppHost.Get<IAgentCatalog>(),
            AppHost.Get<IAgentScanner>(),
            AppHost.Get<ISettingsStore>(),
            new CollabMcpServer(),
            remoteHostsFactory: () => fakes);
        var bridge = new FakeGuiBridge(parent.Id, "claude", parent.Title, dir);
        coordinator.RegisterGuiSession(bridge);
        return (coordinator, parent, bridge, remote, host);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        for (var i = 0; i < 80 && !condition(); i++) await Task.Delay(25, ct);
        Assert.True(condition());
    }

    [AvaloniaFact, FreshApp]
    public async Task An_agent_delegates_to_a_paired_host_and_gets_the_answer_from_its_timeline()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, parentBridge, remote, host) = await SetUpRemoteAsync("octopus-collab-remote-");
        using var _ = coordinator;

        var peers = Body(await coordinator.CallToolAsync(parent.Id, "list_peers", null, ct));
        var listed = Assert.Single(peers["remote_projects"]!.AsArray())!;
        Assert.Equal(remote.Id, listed["project_id"]!.GetValue<string>());
        Assert.Equal("Build Server", listed["host"]!.GetValue<string>());

        // Worktree / read-only cannot be enforced over the wire yet: refused, not silently widened.
        var refused = await coordinator.CallToolAsync(parent.Id, "delegate_task",
            new JsonObject { ["target"] = "codex", ["goal"] = "x", ["project_id"] = remote.Id, ["isolation"] = "read_only" }, ct);
        Assert.True(refused["isError"]?.GetValue<bool>());

        var res = Body(await coordinator.CallToolAsync(parent.Id, "delegate_task",
            new JsonObject { ["target"] = "codex", ["title"] = "Integration suite", ["goal"] = "Run the integration suite and report failures.", ["project_id"] = remote.Id }, ct));
        Assert.Equal("Build Server", res["host"]!.GetValue<string>());
        Assert.Equal("running", res["status"]!.GetValue<string>());
        var childId = res["session_id"]!.GetValue<string>();

        var created = Assert.Single(host.Created);
        Assert.Equal("rp-1", created.ProjectId);
        Assert.Equal("codex", created.AgentId);
        Assert.Equal(Octopus.Remote.Protocol.ItemSurfaces.Gui, created.Surface);
        Assert.Contains("Run the integration suite", created.Prompt, StringComparison.Ordinal);
        Assert.Contains("Do not call reply_thread", created.Prompt, StringComparison.Ordinal);
        // The other computer learns what this is and where it came from: title, this device, the delegating session.
        Assert.Equal("Integration suite", created.Title);
        Assert.False(string.IsNullOrWhiteSpace(created.OriginDevice));
        Assert.Contains("Lead", created.OriginSession, StringComparison.Ordinal);
        Assert.Contains("From device " + created.OriginDevice, created.Prompt, StringComparison.Ordinal);
        Assert.Contains(childId, host.Attached);

        // Mirrored here, nested under the delegating session, and listed as a remote peer.
        var mirrored = AppHost.Get<IProjectService>().FindItem(childId)!;
        Assert.Equal(parent.Id, mirrored.ParentItemId);
        Assert.Same(remote, AppHost.Get<IProjectService>().FindProjectOf(childId));
        peers = Body(await coordinator.CallToolAsync(parent.Id, "list_peers", null, ct));
        Assert.Contains(peers["active_sessions"]!.AsArray(), p => p!["session_id"]!.GetValue<string>() == childId && p["host"]!.GetValue<string>() == "Build Server");

        // Labels name the computer a remote session runs on, so threads and wake-ups say where the answer came from.
        Assert.Equal("Build Server", coordinator.HostNameOf(childId));
        Assert.Contains(coordinator.GetSpawnedChildren(parent.Id), c => c.Id == childId);

        // A streaming answer is not the end of the turn; ready with a finished answer is.
        host.Push(childId, Octopus.Remote.Protocol.AcpStates.Working, UserEntry("u1", created.Prompt!));
        host.Push(childId, null, AnswerEntry("a1", "Running…", streaming: true));
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single().Status);
        host.Push(childId, Octopus.Remote.Protocol.AcpStates.Ready, AnswerEntry("a1", "All 212 tests passed."));

        await WaitUntilAsync(() => coordinator.GetTasksForItem(parent.Id).Single().Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("All 212 tests passed.", coordinator.GetTasksForItem(parent.Id).Single().ResultSummary);
        await WaitUntilAsync(() => parentBridge.DeliveredPrompts.Any(p => p.Contains("All 212 tests passed.", StringComparison.Ordinal)), ct);
        Assert.Contains(parentBridge.DeliveredPrompts, p => p.Contains("@ Build Server", StringComparison.Ordinal));
    }

    [AvaloniaFact, FreshApp]
    public void The_sidebar_nests_delegates_from_other_projects_and_computers_and_says_where_work_came_from()
    {
        var projects = AppHost.Get<IProjectService>();
        var home = projects.AddProject(Directory.CreateTempSubdirectory("octopus-collab-side-a-").FullName);
        var lead = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Lead", Surface = AgentSurface.Gui };
        projects.AddItem(home, lead);
        var incoming = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Incoming", CollabOriginDevice = "MacBook Pro", CollabOriginSession = "Claude Code · Boss" };
        projects.AddItem(home, incoming, activate: false);

        var other = projects.AddProject(Directory.CreateTempSubdirectory("octopus-collab-side-b-").FullName, activate: false);
        var localChild = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Local child", ParentItemId = lead.Id };
        projects.AddItem(other, localChild, activate: false);
        // Made remote without AddRemoteProject (which would make the app reconnect to a made-up host in the background).
        var remote = projects.AddProject(Directory.CreateTempSubdirectory("octopus-collab-side-r-").FullName, activate: false);
        remote.RemoteHost = "host-9";
        remote.RemoteId = "rp-9";
        var remoteChild = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Remote child", ParentItemId = lead.Id };
        projects.AddItem(remote, remoteChild, activate: false);
        Frames.Pump();
        Assert.Same(home, projects.Current);

        var panel = AppHost.Get<ShellViewModel>().Panel;
        var leadRow = panel.Items.Single(r => r.Id == lead.Id);
        Assert.Equal(new[] { localChild.Id, remoteChild.Id }.Order(StringComparer.Ordinal), leadRow.SpawnedChildren.Select(c => c.Id).Order(StringComparer.Ordinal));
        Assert.Equal(other.Name, leadRow.SpawnedChildren.Single(c => c.Id == localChild.Id).HostBadge);
        Assert.Equal("host-9", leadRow.SpawnedChildren.Single(c => c.Id == remoteChild.Id).HostBadge);
        Assert.True(leadRow.SpawnedChildren.All(c => c.HasHostBadge));

        // Work another computer delegated here says so, with the session that sent it in the tooltip.
        var incomingRow = panel.Items.Single(r => r.Id == incoming.Id);
        Assert.True(incomingRow.HasOrigin);
        Assert.Contains("MacBook Pro", incomingRow.OriginText, StringComparison.Ordinal);
        Assert.Contains("Claude Code · Boss", incomingRow.OriginTip, StringComparison.Ordinal);

    }

    [AvaloniaFact, FreshApp]
    public void A_delegate_in_another_project_names_the_session_of_this_computer_that_started_it()
    {
        var projects = AppHost.Get<IProjectService>();
        var home = projects.AddProject(Directory.CreateTempSubdirectory("octopus-collab-origin-a-").FullName);
        var lead = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Lead", Surface = AgentSurface.Gui };
        projects.AddItem(home, lead);
        var other = projects.AddProject(Directory.CreateTempSubdirectory("octopus-collab-origin-b-").FullName, activate: false);
        var child = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Child", ParentItemId = lead.Id };
        projects.AddItem(other, child, activate: false);
        var panel = AppHost.Get<ShellViewModel>().Panel;
        Frames.Pump();

        // As clicking its nested row does: the delegate's own project comes to the front with it.
        projects.ActivateItem(child.Id);
        Frames.Pump();
        Assert.Same(other, projects.Current);
        var childRow = panel.Items.Single(r => r.Id == child.Id);
        Assert.True(childRow.HasOrigin);
        Assert.Contains("Lead", childRow.OriginText, StringComparison.Ordinal);
    }

    [AvaloniaFact, FreshApp]
    public async Task A_remote_delegate_raises_its_permission_cards_here_and_can_be_cancelled_or_lost()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, _, remote, host) = await SetUpRemoteAsync("octopus-collab-remote-perm-");
        using var _ = coordinator;
        var res = Body(await coordinator.CallToolAsync(parent.Id, "delegate_task",
            new JsonObject { ["target"] = "codex", ["goal"] = "Install deps", ["project_id"] = remote.Id }, ct));
        var childId = res["session_id"]!.GetValue<string>();

        var card = new Octopus.Remote.Protocol.AcpEntryMessage
        {
            Id = "p1",
            Kind = Octopus.Remote.Protocol.AcpEntryKinds.Permission,
            Permission = new Octopus.Remote.Protocol.AcpPermissionMessage
            {
                Title = "npm install",
                Options =
                [
                    new Octopus.Remote.Protocol.AcpOptionMessage { Id = "o-allow", Label = "Allow", Detail = "allow_once" },
                    new Octopus.Remote.Protocol.AcpOptionMessage { Id = "o-reject", Label = "Reject", Detail = "reject_once" },
                ],
            },
        };
        host.Push(childId, Octopus.Remote.Protocol.AcpStates.Waiting, UserEntry("u1", "Install deps"), card);
        var bubbled = Assert.Single(coordinator.GetBubbledApprovalsForParent(parent.Id));
        Assert.Contains("Build Server", bubbled.ChildTitle, StringComparison.Ordinal);
        Assert.Equal(CollabTaskStatus.WaitingApproval, coordinator.GetTasksForItem(parent.Id).Single().Status);

        Assert.True(bubbled.TryRespond(allow: true));
        await WaitUntilAsync(() => host.Responses.Any(r => r.EntryId == "p1" && r.OptionId == "o-allow"), ct);

        // The host resolving the card (here or on the host itself) clears it here.
        card.Permission.IsResolved = true;
        host.Push(childId, Octopus.Remote.Protocol.AcpStates.Working, card);
        Assert.Empty(coordinator.GetBubbledApprovalsForParent(parent.Id));

        // cancel_task stops the remote turn.
        var taskId = res["task_id"]!.GetValue<string>();
        await coordinator.CallToolAsync(parent.Id, "cancel_task", new JsonObject { ["task_id"] = taskId }, ct);
        await WaitUntilAsync(() => host.Cancels.Contains(childId), ct);

        // A second delegate is still running when the connection drops: it fails with an honest reason.
        var second = Body(await coordinator.CallToolAsync(parent.Id, "delegate_task",
            new JsonObject { ["target"] = "codex", ["goal"] = "Long job", ["project_id"] = remote.Id }, ct));
        host.Disconnect();
        await WaitUntilAsync(() => coordinator.GetTasksForItem(parent.Id).First(t => t.TaskId == second["task_id"]!.GetValue<string>()).Status == CollabTaskStatus.Failed, ct);
        Assert.Contains("Lost the connection to Build Server", coordinator.GetTasksForItem(parent.Id).First(t => t.TaskId == second["task_id"]!.GetValue<string>()).ResultSummary, StringComparison.Ordinal);
    }

    [AvaloniaFact, FreshApp]
    public async Task Messaging_an_existing_remote_session_relays_only_its_new_answer()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, parentBridge, remote, host) = await SetUpRemoteAsync("octopus-collab-remote-msg-");
        using var _ = coordinator;
        var projects = AppHost.Get<IProjectService>();
        projects.AddItem(remote, new ProjectItem { Id = "ops-1", Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Ops" }, activate: false);
        host.Snapshots["ops-1"] = new Octopus.Remote.Protocol.AcpSnapshotMessage
        {
            ItemId = "ops-1",
            Sequence = 5,
            State = Octopus.Remote.Protocol.AcpStates.Ready,
            Entries = [UserEntry("old-u", "earlier"), AnswerEntry("old-a", "an old answer")],
        };

        var sent = Body(await coordinator.CallToolAsync(parent.Id, "send_message",
            new JsonObject { ["target_session_id"] = "ops-1", ["message"] = "Is the deploy green?" }, ct));
        var threadId = sent["thread_id"]!.GetValue<string>();
        var prompt = Assert.Single(host.Prompts);
        Assert.Equal("ops-1", prompt.ItemId);
        Assert.Contains("comes from another computer", prompt.Text, StringComparison.Ordinal);

        // What it said before is not a reply to us.
        host.Push("ops-1", Octopus.Remote.Protocol.AcpStates.Ready);
        Assert.Single(coordinator.FindThread(threadId)!.Messages);

        host.Push("ops-1", Octopus.Remote.Protocol.AcpStates.Working, UserEntry("new-u", prompt.Text));
        host.Push("ops-1", Octopus.Remote.Protocol.AcpStates.Ready, AnswerEntry("new-a", "Deploy is green."));
        await WaitUntilAsync(() => coordinator.FindThread(threadId)!.Messages.Last().Text == "Deploy is green.", ct);
        await WaitUntilAsync(() => parentBridge.DeliveredPrompts.Any(p => p.Contains("Deploy is green.", StringComparison.Ordinal)), ct);

        var read = Body(await coordinator.CallToolAsync(parent.Id, "read_session", new JsonObject { ["target_session_id"] = "ops-1" }, ct));
        Assert.Contains("Deploy is green.", read["summary"]!.GetValue<string>(), StringComparison.Ordinal);

        // Turning remote hosts off hides them from agents.
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.Collab.AllowRemoteHosts = false);
        var peers = Body(await coordinator.CallToolAsync(parent.Id, "list_peers", null, ct));
        Assert.Null(peers["remote_projects"]);
        Assert.DoesNotContain(peers["active_sessions"]!.AsArray(), p => p!["session_id"]!.GetValue<string>() == "ops-1");
    }

    [AvaloniaFact, FreshApp]
    public async Task A_child_with_several_open_permission_cards_shows_all_of_them_on_its_parent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, _, coordinator, _) = await SetUpParentAsync("octopus-collab-cards-");
        var child = await DelegateAsync(coordinator, parent.Id, "Install and build", ct);
        var childId = child["session_id"]!.GetValue<string>();
        CollabBubbledApproval Card(string id, string tool) => new(id, parent.Id, childId, "codex", "Install and build", tool, null, null, _ => { });

        coordinator.ReportChildApproval(Card("p1", "npm install"));
        coordinator.ReportChildApproval(Card("p2", "dotnet build"));
        Assert.Equal(["npm install", "dotnet build"], coordinator.GetBubbledApprovalsForParent(parent.Id).Select(a => a.ToolTitle).Order().Reverse());
        Assert.Equal(CollabTaskStatus.WaitingApproval, coordinator.GetTasksForItem(parent.Id).Single().Status);

        // One answered: the other still shows, and the task still waits.
        coordinator.ClearChildApproval(childId, "p1");
        Assert.Equal("dotnet build", Assert.Single(coordinator.GetBubbledApprovalsForParent(parent.Id)).ToolTitle);
        Assert.Equal(CollabTaskStatus.WaitingApproval, coordinator.GetTasksForItem(parent.Id).Single().Status);

        coordinator.ClearChildApproval(childId, "p2");
        Assert.Empty(coordinator.GetBubbledApprovalsForParent(parent.Id));
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single().Status);
    }

    private sealed class EchoTool(string name = "echo") : Octopus.App.Collab.Tools.ICollabTool
    {
        public string Name => name;
        public string Description => "Echoes who called.";
        public JsonObject InputSchema => new() { ["type"] = "object" };

        public Task<JsonObject> CallAsync(Octopus.App.Collab.Tools.ICollabOperations ops, string callerItemId, Octopus.App.Collab.Tools.CollabToolArgs args, CancellationToken ct) =>
            Task.FromResult(Octopus.App.Collab.Tools.CollabToolResult.Ok(new JsonObject { ["caller"] = callerItemId, ["peers"] = ops.ListPeers(callerItemId).Count }));
    }

    [AvaloniaFact, FreshApp]
    public async Task A_new_tool_is_one_class_it_is_listed_called_and_can_replace_a_built_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(Directory.CreateTempSubdirectory("octopus-collab-tool-").FullName);
        var me = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Me", Surface = AgentSurface.Gui };
        projects.AddItem(project, me);
        using var coordinator = new CollabCoordinator(
            projects,
            AppHost.Get<IAgentCatalog>(),
            AppHost.Get<IAgentScanner>(),
            AppHost.Get<ISettingsStore>(),
            new CollabMcpServer(),
            tools: [.. Octopus.App.Collab.Tools.CollabToolRegistry.BuiltIn, new EchoTool(), new EchoTool("read_session")]);

        var names = coordinator.ListTools().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(8, names.Count);
        Assert.Contains("echo", names);
        Assert.Single(names, n => n == "read_session");

        var echoed = Body(await coordinator.CallToolAsync(me.Id, "echo", null, ct));
        Assert.Equal(me.Id, echoed["caller"]!.GetValue<string>());
        // Registered later under a built-in's name: it replaces that built-in.
        Assert.Equal(me.Id, Body(await coordinator.CallToolAsync(me.Id, "read_session", null, ct))["caller"]!.GetValue<string>());
        Assert.True((await coordinator.CallToolAsync(me.Id, "no_such_tool", null, ct))["isError"]?.GetValue<bool>());
    }

    [AvaloniaFact, FreshApp]
    public async Task A_terminal_agent_gets_messages_pasted_answers_when_its_hook_says_done_and_esc_cancels_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, parentBridge, coordinator, _) = await SetUpParentAsync("octopus-collab-term-");
        var projects = AppHost.Get<IProjectService>();
        var project = projects.FindProjectOf(parent.Id)!;
        var cli = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "CLI Claude", Surface = AgentSurface.Terminal };
        projects.AddItem(project, cli, activate: false);
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        AppHost.Get<Octopus.Terminal.Avalonia.TerminalSessionRegistry>().GetOrStart(
            cli.Id, AppHost.Get<IItemLaunchService>().Plan(project, cli), factory, new TKCode.Terminal.Core.TerminalSize(80, 24, 0, 0),
            Octopus.Terminal.Avalonia.TerminalPalettes.Dark, TKCode.Terminal.Core.ColorScheme.Dark, 1000, out _);
        var pty = factory.Connections.Last();
        string AllWritten() => string.Concat(pty.Written);

        // A message is pasted into the CLI as one submission.
        var sent = Body(await coordinator.CallToolAsync(parent.Id, "send_message",
            new JsonObject { ["target_session_id"] = cli.Id, ["message"] = "Is the build green?" }, ct));
        var threadId = sent["thread_id"]!.GetValue<string>();
        await WaitUntilAsync(() => AllWritten().Contains("Is the build green?", StringComparison.Ordinal), ct);

        // Its turn ends when its Stop hook fires: whatever it printed since is the answer, relayed to the asker.
        AppHost.Get<ISignalHub>().Publish(new AgentSignal("claude", "stop", cli.Id, "", 0, 0, "{}"));
        await WaitUntilAsync(() => coordinator.FindThread(threadId)!.Messages.Last().FromItemId == cli.Id, ct);
        await WaitUntilAsync(() => parentBridge.DeliveredPrompts.Any(p => p.Contains("Terminal session reported turn completion.", StringComparison.Ordinal)), ct);

        // A task handed to it and then cancelled interrupts its turn with Esc.
        var task = Body(await coordinator.CallToolAsync(parent.Id, "delegate_task", new JsonObject { ["target"] = cli.Id, ["goal"] = "Run the slow suite" }, ct));
        await coordinator.CallToolAsync(parent.Id, "cancel_task", new JsonObject { ["task_id"] = task["task_id"]!.GetValue<string>() }, ct);
        await WaitUntilAsync(() => AllWritten().EndsWith('\u001b'), ct);
        Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task["task_id"]!.GetValue<string>()).Status);
    }

    [AvaloniaFact, FreshApp]
    public async Task Ledger_survives_a_restart_and_reports_interrupted_tasks_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, _, coordinator, _) = await SetUpParentAsync("octopus-collab-persist-");
        var running = await DelegateAsync(coordinator, parent.Id, "Unfinished work", ct);
        var runningId = running["task_id"]!.GetValue<string>();
        coordinator.Dispose(); // flushes the ledger, as on app exit

        var store = AppHost.Get<CollabStateStore>();
        Assert.True(File.Exists(store.FilePath));
        using var restored = new CollabCoordinator(
            AppHost.Get<IProjectService>(),
            AppHost.Get<IAgentCatalog>(),
            AppHost.Get<IAgentScanner>(),
            AppHost.Get<ISettingsStore>(),
            new CollabMcpServer(),
            store: store);

        var task = restored.GetTasksForItem(parent.Id).Single();
        Assert.Equal(runningId, task.TaskId);
        Assert.Equal(CollabTaskStatus.Cancelled, task.Status);
        Assert.Contains("restarted", task.ResultSummary, StringComparison.Ordinal);

        var first = Body(await AwaitEventsAsync(restored, parent.Id, 5, ct));
        Assert.True(Assert.Single(first["tasks"]!.AsArray())!["new"]!.GetValue<bool>());
        var second = Body(await AwaitEventsAsync(restored, parent.Id, 5, ct));
        Assert.Empty(second["tasks"]!.AsArray());

        // Ids keep counting up instead of restarting at 1, so an old task id never names a new task.
        static int SeqOf(string id) => int.Parse(id.Split('_')[1], System.Globalization.CultureInfo.InvariantCulture);
        var fresh = await DelegateAsync(restored, parent.Id, "Fresh work", ct);
        Assert.True(SeqOf(fresh["task_id"]!.GetValue<string>()) > SeqOf(runningId));
    }

    [AvaloniaFact, FreshApp]
    public async Task Local_mcp_gateway_authenticates_per_session_token_and_serves_collab_tools()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-collab-mcp-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var claude = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Backend Auth", Surface = AgentSurface.Gui };
        var codexPeer = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Frontend Login", Surface = AgentSurface.Gui };
        projects.AddItem(project, claude);
        projects.AddItem(project, codexPeer, activate: false);

        var coordinator = AppHost.Get<ICollabCoordinator>();
        var server = coordinator.Server;
        Assert.True(server.Start());
        Assert.False(string.IsNullOrWhiteSpace(server.EndpointUrl));

        var token = server.IssueToken(claude.Id);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });

        // Unauthorized call without Bearer token must be rejected with 401.
        using (var unauthReq = new HttpRequestMessage(HttpMethod.Post, server.EndpointUrl)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        })
        {
            using var unauthRes = await http.SendAsync(unauthReq, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthRes.StatusCode);
        }

        // tools/list returns all 6 collaboration primitives.
        var listResp = await PostMcpAsync(http, server.EndpointUrl!, token, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", ct);
        var toolNames = listResp["result"]!["tools"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("list_peers", toolNames);
        Assert.Contains("read_session", toolNames);
        Assert.Contains("delegate_task", toolNames);
        Assert.Contains("send_message", toolNames);
        Assert.Contains("reply_thread", toolNames);
        Assert.Contains("await_events", toolNames);

        // list_peers sees the sibling Codex session in the same project.
        var peersResp = await PostMcpAsync(http, server.EndpointUrl!, token, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_peers","arguments":{}}}""", ct);
        var peersText = peersResp["result"]!["content"]![0]!["text"]!.GetValue<string>();
        var peersJson = JsonNode.Parse(peersText)!.AsObject();
        var activeIds = peersJson["active_sessions"]!.AsArray().Select(n => n!["session_id"]!.GetValue<string>()).ToList();
        Assert.Contains(claude.Id, activeIds);
        Assert.Contains(codexPeer.Id, activeIds);
    }

    [AvaloniaFact, FreshApp]
    public async Task Delegating_spawns_nested_child_under_parent_in_sidebar_with_default_shared_and_override_isolation()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-collab-tree-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var settings = AppHost.Get<ISettingsStore>();
        var project = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Main Claude", Surface = AgentSurface.Gui };
        projects.AddItem(project, parent);

        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        var parentBridge = new FakeGuiBridge(parent.Id, "claude", parent.Title, dir);
        coordinator.RegisterGuiSession(parentBridge);

        // Default global isolation must be Shared.
        Assert.Equal(CollabIsolationMode.Shared, settings.Current.Agents.Collab.DefaultIsolation);

        HoldDelegatedGuiTurnsInFlight();

        // Delegate to "codex" with no isolation override -> uses default Shared & nests under parent.
        var res1 = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject
            {
                ["target"] = "codex",
                ["title"] = "Write JWT tests",
                ["goal"] = "Add unit tests for JWT refresh token rotation.",
                ["surface"] = "gui",
            },
            ct);
        var body1 = JsonNode.Parse(res1["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        var codexChildId = body1["session_id"]!.GetValue<string>();
        Assert.Equal("shared", body1["isolation"]!.GetValue<string>());

        // Delegate to Chinese alias "反重力" with explicit read_only override -> resolves to agy in plan mode.
        var res2 = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject
            {
                ["target"] = "反重力",
                ["title"] = "Security Audit",
                ["goal"] = "Audit AuthController for privilege escalation.",
                ["isolation"] = "read_only",
                ["surface"] = "gui",
            },
            ct);
        var body2 = JsonNode.Parse(res2["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        var agyChildId = body2["session_id"]!.GetValue<string>();
        Assert.Equal("agy", body2["agent_id"]!.GetValue<string>());
        Assert.Equal("read_only", body2["isolation"]!.GetValue<string>());

        var agyItem = projects.FindItem(agyChildId)!;
        Assert.Equal(parent.Id, agyItem.ParentItemId);
        Assert.Equal(PermissionModes.Plan, agyItem.Launch?.Permission);

        // Verify Scheme C sidebar nests BOTH spawned children directly under the parent row (not duplicated at root).
        Frames.Pump();
        var panel = AppHost.Get<ShellViewModel>().Panel;
        Assert.Single(panel.Items);
        var parentRow = panel.Items[0];
        Assert.Equal(parent.Id, parentRow.Id);
        Assert.True(parentRow.HasSpawnedChildren);
        Assert.Equal(2, parentRow.SpawnedChildrenCount);
        Assert.Equal([codexChildId, agyChildId], parentRow.SpawnedChildren.Select(c => c.Id).ToArray());
        Assert.Equal("ro", parentRow.SpawnedChildren[1].IsolationBadge);

        // Collapsing parent children hides sub-rows while keeping count pill intact.
        parentRow.ToggleChildrenExpandedCommand.Execute(null);
        Assert.False(parentRow.ShowSpawnedChildren);
        parentRow.ToggleChildrenExpandedCommand.Execute(null);
        Assert.True(parentRow.ShowSpawnedChildren);

        // Implicit turn completion on child automatically completes the task and wakes up the parent session!
        // Re-register our spy bridge after UI pump (or inspect the real tab's queue) so wakeup delivery is deterministic.
        coordinator.RegisterGuiSession(parentBridge);
        coordinator.NotifyTurnCompleted(codexChildId, "All 14 JWT unit tests passed.");
        await WaitUntilAsync(() => parentBridge.HasPromptContaining("All 14 JWT unit tests passed."), ct);
        Assert.True(parentBridge.HasPromptContaining("All 14 JWT unit tests passed."));
    }

    [AvaloniaFact, FreshApp]
    public void Agent_gui_shows_scheme_c_collab_ribbon_and_unified_at_mention_picker()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-collab-ui-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var claude = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Main Claude", Surface = AgentSurface.Gui };
        var codexChild = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Child Codex", Surface = AgentSurface.Gui, ParentItemId = claude.Id, CollabIsolation = "worktree" };
        var peer = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "agy", Title = "Peer Agy", Surface = AgentSurface.Gui };
        projects.AddItem(project, claude);
        projects.AddItem(project, codexChild, activate: false);
        projects.AddItem(project, peer, activate: false);

        var coordinator = AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();

        var tabRegistry = AppHost.Get<Octopus.App.Workspace.ITabContentRegistry>();
        var control = tabRegistry.GetOrCreate(project, claude);
        var vm = Assert.IsType<Octopus.App.AgentGui.AgentGuiTabViewModel>(control.DataContext);
        Frames.Pump();

        // Scheme C top Collab Ribbon shows self + direct spawned child with 'wt' badge.
        Assert.True(vm.HasCollabTopology);
        Assert.Contains(vm.CollabRibbonNodes, n => n.Kind == "self" && n.Id == claude.Id);
        Assert.Contains(vm.CollabRibbonNodes, n => n.Kind == "child" && n.Id == codexChild.Id && n.Meta == "wt");

        // Unified '@' mention popup finds active peer sessions AND installed agents by Chinese alias ("反重力").
        vm.InputText = "@Peer";
        Frames.Pump();
        Assert.True(vm.IsFilePopupOpen);
        Assert.Contains(vm.FilteredFiles, m => m.IsSession && m.TargetId == peer.Id);

        // Selecting a session mention writes an inline token in place of "@Peer" and exposes the Direct Relay toggle.
        var sessionSuggestion = vm.FilteredFiles.First(m => m.IsSession && m.TargetId == peer.Id);
        vm.InsertFileMention(sessionSuggestion);
        Assert.True(vm.HasSessionMention);
        Assert.False(vm.IsFilePopupOpen);
        var peerToken = Assert.Single(vm.MentionTokens);
        Assert.Equal(peer.Id, peerToken.TargetId);
        Assert.StartsWith("@", peerToken.Text, StringComparison.Ordinal);
        Assert.Equal(peerToken.Text + " ", vm.InputText);
        Assert.DoesNotContain(vm.Attachments, c => c.Attachment.Kind == "session");

        // A second mention typed later in the same sentence is added, not swapped: several targets per prompt.
        vm.InputText += "和 @codex";
        vm.ComposerCaretIndex = vm.InputText.Length;
        Frames.Pump();
        Assert.True(vm.IsFilePopupOpen);
        vm.InsertFileMention(vm.FilteredFiles.First(m => m.IsAgent && m.TargetId == "codex"));
        Assert.Equal(2, vm.MentionTokens.Count);
        Assert.Equal($"{peerToken.Text} 和 {vm.MentionTokens[1].Text} ", vm.InputText);

        // Deleting the session token's text turns the direct relay toggle off again.
        vm.IsDirectRelayMode = true;
        vm.InputText = vm.InputText.Replace(peerToken.Text, "", StringComparison.Ordinal);
        Assert.False(vm.HasSessionMention);
        Assert.False(vm.IsDirectRelayMode);
    }

    [AvaloniaFact, FreshApp]
    public async Task User_broadcast_delivers_one_message_to_every_mentioned_session_in_one_thread()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-collab-broadcast-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var me = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Lead", Surface = AgentSurface.Gui };
        var codex = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Login", Surface = AgentSurface.Gui };
        var agy = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "agy", Title = "Docs", Surface = AgentSurface.Gui };
        projects.AddItem(project, me);
        projects.AddItem(project, codex, activate: false);
        projects.AddItem(project, agy, activate: false);

        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        var codexBridge = new FakeGuiBridge(codex.Id, "codex", codex.Title, dir);
        var agyBridge = new FakeGuiBridge(agy.Id, "agy", agy.Title, dir);
        coordinator.RegisterGuiSession(new FakeGuiBridge(me.Id, "claude", me.Title, dir));
        coordinator.RegisterGuiSession(codexBridge);
        coordinator.RegisterGuiSession(agyBridge);

        var thread = await coordinator.SendUserBroadcastAsync(me.Id, [codex.Id, agy.Id, codex.Id], "Ship the login page together.", ct);

        Assert.NotNull(thread);
        Assert.Equal([me.Id, codex.Id, agy.Id], thread.ParticipantItemIds.ToArray());
        var toCodex = Assert.Single(codexBridge.DeliveredPrompts);
        var toAgy = Assert.Single(agyBridge.DeliveredPrompts);
        Assert.Contains("Ship the login page together.", toCodex, StringComparison.Ordinal);
        Assert.Contains("Docs", toCodex, StringComparison.Ordinal);   // Codex learns Agy was addressed too
        Assert.Contains("Login", toAgy, StringComparison.Ordinal);    // and the other way round
        Assert.True(thread.MaxHops > thread.Hops, "the fan-out must leave budget for replies");
    }

    [AvaloniaFact, FreshApp]
    public async Task Cross_session_negotiation_enforces_hop_budget_and_supports_user_interjection_and_permission_bubbling()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-collab-nego-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.Agents.Collab.MaxNegotiationHops = 2);

        var project = projects.AddProject(dir);
        var claude = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Backend", Surface = AgentSurface.Gui };
        var codex = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Frontend", Surface = AgentSurface.Gui };
        projects.AddItem(project, claude);
        projects.AddItem(project, codex, activate: false);

        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        var claudeBridge = new FakeGuiBridge(claude.Id, "claude", claude.Title, dir);
        var codexBridge = new FakeGuiBridge(codex.Id, "codex", codex.Title, dir);
        coordinator.RegisterGuiSession(claudeBridge);
        coordinator.RegisterGuiSession(codexBridge);

        // Round 1: Claude sends negotiation message to existing Codex session.
        var send1 = await coordinator.CallToolAsync(
            claude.Id,
            "send_message",
            new JsonObject
            {
                ["target_session_id"] = codex.Id,
                ["title"] = "API contract",
                ["message"] = "Should refreshToken be camelCase?",
            },
            ct);
        var s1Body = JsonNode.Parse(send1["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        var threadId = s1Body["thread_id"]!.GetValue<string>();
        Assert.Equal(1, s1Body["round"]!.GetValue<int>());
        Assert.Single(codexBridge.DeliveredPrompts);

        // Round 2: Codex replies explicitly via reply_thread -> hits hop budget (2/2) and wakes Claude.
        var reply2 = await coordinator.CallToolAsync(
            codex.Id,
            "reply_thread",
            new JsonObject
            {
                ["thread_id"] = threadId,
                ["message"] = "Yes, camelCase works cleanly for web interceptors.",
                ["status"] = "continue",
            },
            ct);
        Assert.Null(reply2["isError"]);
        Assert.Single(claudeBridge.DeliveredPrompts);

        // Round 3: Exceeding hop limit is blocked by loop guard until user extends budget.
        var blocked = await coordinator.CallToolAsync(
            claude.Id,
            "send_message",
            new JsonObject
            {
                ["target_session_id"] = codex.Id,
                ["thread_id"] = threadId,
                ["message"] = "One more ping",
            },
            ct);
        Assert.True(blocked["isError"]?.GetValue<bool>());

        // User interjects and grants +4 rounds -> broadcasts to both participants and re-activates thread.
        var interjected = await coordinator.InterjectInThreadAsync(threadId, "Use camelCase and ship it.", extendBudget: true, ct);
        Assert.True(interjected);
        Assert.Equal(2, claudeBridge.DeliveredPrompts.Count);
        Assert.Equal(2, codexBridge.DeliveredPrompts.Count);
        Assert.Equal(6, coordinator.FindThread(threadId)!.MaxHops);

        // Permission bubbling: child approval surfaces on parent and resolves child callback cleanly.
        bool? approvedDecision = null;
        coordinator.ReportChildApproval(new CollabBubbledApproval(
            "apr-test",
            claude.Id,
            codex.Id,
            "codex",
            "Child Codex",
            "Bash",
            "dotnet test",
            null,
            allow => approvedDecision = allow));
        var bubbled = coordinator.GetBubbledApprovalsForParent(claude.Id);
        Assert.Single(bubbled);
        Assert.True(bubbled[0].TryRespond(allow: true));
        Assert.True(approvedDecision);
    }

    [AvaloniaFact, FreshApp]
    public void Terminal_cli_mcp_injector_plans_claude_and_codex_arguments_and_cleans_up()
    {
        var server = AppHost.Get<CollabMcpServer>();
        Assert.True(server.Start());
        var injector = AppHost.Get<CollabCliMcpInjector>();
        var catalog = AppHost.Get<IAgentCatalog>();

        // Create a dummy forwarder file so File.Exists succeeds in unit tests.
        var tmpDir = Directory.CreateTempSubdirectory("octopus-fwd-").FullName;
        var fakeForwarder = Path.Combine(tmpDir, "octopus-signal");
        File.WriteAllText(fakeForwarder, "");

        var claudeItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude CLI" };
        var claudeAttach = injector.Plan(catalog.Find("claude")!, claudeItem, fakeForwarder);
        Assert.NotNull(claudeAttach);
        Assert.Equal(2, claudeAttach!.Arguments.Count);
        Assert.Equal("--mcp-config", claudeAttach.Arguments[0]);
        Assert.True(File.Exists(claudeAttach.Arguments[1]));
        var writtenJson = File.ReadAllText(claudeAttach.Arguments[1]);
        Assert.Contains("octopus-collab", writtenJson, StringComparison.Ordinal);
        Assert.Contains("--mcp", writtenJson, StringComparison.Ordinal);

        var codexItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Codex CLI" };
        var codexAttach = injector.Plan(catalog.Find("codex")!, codexItem, fakeForwarder);
        Assert.NotNull(codexAttach);
        Assert.Contains(codexAttach!.Arguments, a => a.Contains("mcp_servers.octopus_collab.command=", StringComparison.Ordinal));
        Assert.True(codexAttach.Environment.ContainsKey(CollabMcpServer.TokenEnvVariable));

        // Cleanup removes temporary Claude MCP config file and revokes session token.
        var cfgPath = claudeAttach.Arguments[1];
        injector.CleanupItem(claudeItem.Id);
        Assert.False(File.Exists(cfgPath));
    }

    [AvaloniaFact, FreshApp]
    public void Global_agent_settings_expose_default_isolation_and_persist_changes()
    {
        var settings = AppHost.Get<ISettingsStore>();
        using var vm = AppHost.Get<AgentsSettingsViewModel>();

        Assert.True(vm.CollabEnabled);
        Assert.Equal(nameof(CollabIsolationMode.Shared), vm.CollabDefaultIsolationKey);
        Assert.True(vm.CollabEnableTerminalBridge);

        vm.CollabDefaultIsolationKey = nameof(CollabIsolationMode.Worktree);
        Assert.Equal(CollabIsolationMode.Worktree, settings.Current.Agents.Collab.DefaultIsolation);

        vm.CollabMaxHopsKey = "10";
        Assert.Equal(10, settings.Current.Agents.Collab.MaxNegotiationHops);
    }

    [AvaloniaFact, FreshApp]
    public async Task Await_events_suppresses_duplicate_wakeup_and_cancels_stale_queued_wakeups()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-collab-dedup-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "agy", Title = "Calculate 1+1", Surface = AgentSurface.Gui };
        projects.AddItem(project, parent);

        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        var parentBridge = new FakeGuiBridge(parent.Id, "agy", parent.Title, dir);
        coordinator.RegisterGuiSession(parentBridge);

        // 1. Parent delegates task and immediately blocks in await_events.
        HoldDelegatedGuiTurnsInFlight();
        var delegateRes = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject
            {
                ["target"] = "claude",
                ["title"] = "计算 1+1",
                ["goal"] = "计算 1+1=? 并直接回复结果。",
                ["surface"] = "gui",
            },
            ct);
        var delegateBody = JsonNode.Parse(delegateRes["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        var childId = delegateBody["session_id"]!.GetValue<string>();
        var threadId = delegateBody["thread_id"]!.GetValue<string>();

        coordinator.RegisterGuiSession(parentBridge);
        var awaitTask = coordinator.CallToolAsync(
            parent.Id,
            "await_events",
            new JsonObject
            {
                ["ids"] = new JsonArray(threadId),
                ["timeout_seconds"] = 10,
            },
            ct);

        // Give await_events a moment to register its waiter before the child replies.
        await Task.Delay(30, ct);

        var replyRes = await coordinator.CallToolAsync(
            childId,
            "reply_thread",
            new JsonObject
            {
                ["thread_id"] = threadId,
                ["message"] = "1 + 1 = 2",
                ["status"] = "agreed",
            },
            ct);
        Assert.Null(replyRes["isError"]);

        var awaitRes = await awaitTask;
        var awaitBody = JsonNode.Parse(awaitRes["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        Assert.Equal("agreed", awaitBody["threads"]![0]!["status"]!.GetValue<string>());
        Assert.Equal("1 + 1 = 2", awaitBody["threads"]![0]!["latest_message"]!.GetValue<string>());

        // Because await_events delivered the reply inline, no duplicate wakeup envelope was injected into parent,
        // and no unnecessary broad thread-wide cancellation ran when nothing was queued.
        Assert.Empty(parentBridge.DeliveredPrompts);
        Assert.Empty(parentBridge.CancelledLookups);

        // 2. When a wakeup was queued while parent wasn't awaiting, consuming it via read_session cancels that exact stale queued wakeup.
        var queuedReplyRes = await coordinator.CallToolAsync(
            childId,
            "reply_thread",
            new JsonObject
            {
                ["thread_id"] = threadId,
                ["message"] = "follow-up detail",
                ["status"] = "agreed",
            },
            ct);
        Assert.Null(queuedReplyRes["isError"]);
        Assert.Single(parentBridge.DeliveredPrompts);

        var readRes = await coordinator.CallToolAsync(
            parent.Id,
            "read_session",
            new JsonObject { ["target_session_id"] = childId },
            ct);
        Assert.Null(readRes["isError"]);
        Assert.Single(parentBridge.CancelledDeliveryIds);
        Assert.Contains(parentBridge.CancelledLookups, c => c.ThreadId == threadId);
    }

    [AvaloniaFact, FreshApp]
    public async Task Initial_collab_prompt_dispatches_after_off_ui_connect_without_sticking_in_input_box()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-collab-init-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);

        var fakeFactory = (FakeAcpSessionFactory)AppHost.Get<Octopus.Acp.Process.IAcpSessionFactory>();
        FakeAcpSession? created = null;
        var promptReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fakeFactory.ConfigureNext = s =>
        {
            created = s;
            // Force ApplyInitialLaunchPreferencesAsync to yield onto a thread-pool thread,
            // reproducing the real-world ConfigureAwait(false) race before initial prompt dispatch.
            s.Models = new Octopus.Acp.Protocol.AcpSessionModelState
            {
                CurrentModelId = "default-model",
                AvailableModels =
                [
                    new Octopus.Acp.Protocol.AcpModelInfo { ModelId = "default-model", Name = "Default" },
                    new Octopus.Acp.Protocol.AcpModelInfo { ModelId = "sonnet", Name = "Sonnet" },
                ],
            };
            s.SetModelHandler = async (_, _, token) => await Task.Run(() => Task.Delay(15, token), token).ConfigureAwait(false);
            s.PromptHandler = (_, text, _, _) =>
            {
                promptReceived.TrySetResult(text);
                return Task.FromResult(new Octopus.Acp.Protocol.AcpPromptResult { StopReason = "end_turn" });
            };
        };

        var collabPrompt = "[Octopus Collab Task #task_1_8a24 · Thread #t_2_a0e3 · Delegated by @Antigravity CLI · Calculate 1+1]\n"
            + "计算 1+1=? 并直接回复结果。\n\n"
            + "When finished (or if you need clarification from the delegating agent), either call `reply_thread(thread_id=\"t_2_a0e3\", message=..., status=\"agreed\"|\"continue\")` or simply conclude with a concise summary — Octopus will automatically report your final response back.";

        var child = new ProjectItem
        {
            Kind = ProjectItemKind.Agent,
            AgentId = "claude",
            Title = "计算 1+1",
            Surface = AgentSurface.Gui,
            Launch = new AgentLaunchOptions
            {
                Model = "sonnet",
                Prompt = collabPrompt,
            },
        };
        projects.AddItem(project, child);

        var tabRegistry = AppHost.Get<Octopus.App.Workspace.ITabContentRegistry>();
        var control = tabRegistry.GetOrCreate(project, child);
        var vm = Assert.IsType<Octopus.App.AgentGui.AgentGuiTabViewModel>(control.DataContext);

        await vm.ConnectAsync();
        Frames.Pump();

        var sent = await promptReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(collabPrompt, sent);
        Assert.NotNull(created);
        Assert.Single(created!.SentPrompts);
        Assert.Equal("", vm.InputText);
        // Delegated child keeps its clean title rather than overwriting it with "[Octopus Collab Task ...".
        Assert.Equal("计算 1+1", child.Title);

        // Timeline renders the injected collab task as a distinct collab strip, not a regular user bubble.
        var userTurn = Assert.IsType<Octopus.App.AgentGui.UserTurnItem>(vm.Timeline[0]);
        Assert.True(userTurn.IsCollabMessage);
        Assert.False(userTurn.IsRegularUserMessage);
        Assert.False(userTurn.CanReuse);
        Assert.StartsWith("Collab Task #task_1_8a24", userTurn.CollabHeader, StringComparison.Ordinal);
        Assert.Equal("计算 1+1=? 并直接回复结果。", userTurn.DisplayText);
    }

    [AvaloniaFact, FreshApp]
    public void User_turn_item_parses_collab_reply_and_leaves_regular_user_messages_untouched()
    {
        var regular = new Octopus.App.AgentGui.UserTurnItem("计算一下1+1=?", onReuse: _ => { });
        Assert.False(regular.IsCollabMessage);
        Assert.True(regular.IsRegularUserMessage);
        Assert.True(regular.CanReuse);
        Assert.Equal("计算一下1+1=?", regular.DisplayText);

        var wakeup = "[Octopus Collab Reply · From @Claude Code · 1+1 计算 · Thread #t_2_a0e3 (计算 1+1) · Round 2/6 · Status: agreed]\n"
            + "1 + 1 = 2\n\n"
            + "(The other session marked this thread as agreed/completed; summarize or integrate the result for the user unless further work is needed.)";
        var collab = new Octopus.App.AgentGui.UserTurnItem(wakeup, onReuse: _ => { });
        Assert.True(collab.IsCollabMessage);
        Assert.False(collab.IsRegularUserMessage);
        Assert.False(collab.CanReuse);
        Assert.StartsWith("Collab Reply · From @Claude Code", collab.CollabHeader, StringComparison.Ordinal);
        Assert.Equal("1 + 1 = 2", collab.DisplayText);
    }

    private static async Task<(ProjectItem Parent, FakeGuiBridge ParentBridge, ProjectItem Worker, FakeGuiBridge WorkerBridge, CollabCoordinator Coordinator)> SetUpParentAndWorkerAsync(string prefix)
    {
        var (parent, parentBridge, coordinator, dir) = await SetUpParentAsync(prefix);
        var projects = AppHost.Get<IProjectService>();
        var project = projects.FindProjectOf(parent.Id)!;
        var worker = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Worker", Surface = AgentSurface.Gui };
        projects.AddItem(project, worker, activate: false);
        var workerBridge = new FakeGuiBridge(worker.Id, "codex", worker.Title, dir);
        coordinator.RegisterGuiSession(workerBridge);
        return (parent, parentBridge, worker, workerBridge, coordinator);
    }

    [AvaloniaFact, FreshApp]
    public async Task Delegating_to_existing_session_preserves_terminal_status_when_worker_replies_before_dispatch_returns()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, _, worker, workerBridge, coordinator) = await SetUpParentAndWorkerAsync("octopus-collab-early-reply-");

        string? deliveredTaskId = null;
        workerBridge.OnDeliver = async (threadId, taskId, token) =>
        {
            deliveredTaskId = taskId;
            var reply = await coordinator.CallToolAsync(
                worker.Id,
                "reply_thread",
                new JsonObject { ["thread_id"] = threadId, ["message"] = "fixture complete", ["status"] = "agreed" },
                token);
            Assert.Null(reply["isError"]);
            return true;
        };

        var dispatch = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["goal"] = "Report the fixture result" },
            ct);
        Assert.Null(dispatch["isError"]);
        var agreedTask = Assert.Single(coordinator.GetTasksForItem(parent.Id));
        Assert.Equal(CollabTaskStatus.Completed, agreedTask.Status);

        var body = Body(dispatch);
        Assert.True(body["reused_existing_session"]!.GetValue<bool>());
        Assert.Equal("completed", body["status"]!.GetValue<string>());
        Assert.Equal(body["task_id"]!.GetValue<string>(), agreedTask.TaskId);
        Assert.Equal(body["task_id"]!.GetValue<string>(), deliveredTaskId);
        Assert.Equal(body["thread_id"]!.GetValue<string>(), agreedTask.ThreadId);
        Assert.Equal("fixture complete", agreedTask.ResultSummary);

        workerBridge.OnDeliver = async (threadId, _, token) =>
        {
            var reply = await coordinator.CallToolAsync(
                worker.Id,
                "reply_thread",
                new JsonObject { ["thread_id"] = threadId, ["message"] = "missing spec", ["status"] = "blocked" },
                token);
            Assert.Null(reply["isError"]);
            return true;
        };

        var blockedDispatch = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["goal"] = "Blocked task" },
            ct);
        Assert.Null(blockedDispatch["isError"]);
        var blockedBody = Body(blockedDispatch);
        var blockedTask = Assert.Single(coordinator.GetTasksForItem(parent.Id), t => t.TaskId == blockedBody["task_id"]!.GetValue<string>());
        Assert.Equal(CollabTaskStatus.Failed, blockedTask.Status);
        Assert.Equal("failed", blockedBody["status"]!.GetValue<string>());
        Assert.Equal("missing spec", blockedTask.ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Delegating_to_existing_session_cleans_up_undelivered_task_on_false_throw_and_cancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, _, worker, workerBridge, coordinator) = await SetUpParentAndWorkerAsync("octopus-collab-early-fail-");

        workerBridge.OnDeliver = (_, _, _) => Task.FromResult(false);
        var rejected = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["goal"] = "Rejected delivery" },
            ct);
        Assert.True(rejected["isError"]?.GetValue<bool>());
        Assert.Empty(coordinator.GetTasksForItem(parent.Id));
        Assert.Empty(coordinator.GetThreadsForItem(parent.Id));

        workerBridge.OnDeliver = (_, _, _) => throw new InvalidOperationException("bridge failure");
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["goal"] = "Throwing delivery" },
            ct));
        Assert.Empty(coordinator.GetTasksForItem(parent.Id));
        Assert.Empty(coordinator.GetThreadsForItem(parent.Id));

        using var cts = new CancellationTokenSource();
        workerBridge.OnDeliver = (_, _, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["goal"] = "Cancelled delivery" },
            cts.Token));
        Assert.Empty(coordinator.GetTasksForItem(parent.Id));
        Assert.Empty(coordinator.GetThreadsForItem(parent.Id));

        var awaited = Body(await AwaitEventsAsync(coordinator, parent.Id, 5, ct));
        Assert.Empty(awaited["tasks"]!.AsArray());
        Assert.Empty(awaited["threads"]!.AsArray());
        Assert.NotNull(awaited["note"]);
    }

    [AvaloniaFact, FreshApp]
    public async Task Delegating_to_existing_session_preserves_prior_reply_when_delivery_returns_false_throws_or_cancels()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, _, worker, workerBridge, coordinator) = await SetUpParentAndWorkerAsync("octopus-collab-early-preserve-");

        string? falseThread = null;
        workerBridge.OnDeliver = async (threadId, _, token) =>
        {
            falseThread = threadId;
            var reply = await coordinator.CallToolAsync(
                worker.Id,
                "reply_thread",
                new JsonObject { ["thread_id"] = threadId, ["message"] = "reply before false", ["status"] = "agreed" },
                token);
            Assert.Null(reply["isError"]);
            return false;
        };
        var falseDispatch = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["goal"] = "Reply then return false" },
            ct);
        Assert.True(falseDispatch["isError"]?.GetValue<bool>());
        var falseTask = Assert.Single(coordinator.GetTasksForItem(parent.Id), t => t.ThreadId == falseThread);
        Assert.Equal(CollabTaskStatus.Completed, falseTask.Status);
        Assert.Equal("reply before false", falseTask.ResultSummary);

        string? throwThread = null;
        workerBridge.OnDeliver = async (threadId, _, token) =>
        {
            throwThread = threadId;
            var reply = await coordinator.CallToolAsync(
                worker.Id,
                "reply_thread",
                new JsonObject { ["thread_id"] = threadId, ["message"] = "reply before throw", ["status"] = "agreed" },
                token);
            Assert.Null(reply["isError"]);
            throw new InvalidOperationException("post-reply bridge failure");
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["goal"] = "Reply then throw" },
            ct));
        var throwTask = Assert.Single(coordinator.GetTasksForItem(parent.Id), t => t.ThreadId == throwThread);
        Assert.Equal(CollabTaskStatus.Completed, throwTask.Status);
        Assert.Equal("reply before throw", throwTask.ResultSummary);

        string? cancelThread = null;
        using var cts = new CancellationTokenSource();
        workerBridge.OnDeliver = async (threadId, _, token) =>
        {
            cancelThread = threadId;
            var reply = await coordinator.CallToolAsync(
                worker.Id,
                "reply_thread",
                new JsonObject { ["thread_id"] = threadId, ["message"] = "reply before cancel", ["status"] = "agreed" },
                token);
            Assert.Null(reply["isError"]);
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return true;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["goal"] = "Reply then cancel" },
            cts.Token));
        var cancelTask = Assert.Single(coordinator.GetTasksForItem(parent.Id), t => t.ThreadId == cancelThread);
        Assert.Equal(CollabTaskStatus.Completed, cancelTask.Status);
        Assert.Equal("reply before cancel", cancelTask.ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Delegating_to_existing_gui_session_cancelled_before_acceptance_never_reaches_worker()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-collab-cancel-preaccept-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Lead", Surface = AgentSurface.Gui };
        var worker = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Worker", Surface = AgentSurface.Gui };
        projects.AddItem(project, parent);
        projects.AddItem(project, worker, activate: false);

        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        var parentBridge = new FakeGuiBridge(parent.Id, "opencode", parent.Title, dir);
        coordinator.RegisterGuiSession(parentBridge);

        FakeAcpSession? workerSession = null;
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = s =>
        {
            workerSession = s;
            s.PromptHandler = (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
        };

        var tabRegistry = AppHost.Get<ITabContentRegistry>();
        var control = tabRegistry.GetOrCreate(project, worker);
        var workerVm = Assert.IsType<AgentGuiTabViewModel>(control.DataContext);
        await workerVm.ConnectAsync();
        Frames.Pump();
        Assert.NotNull(workerSession);

        // Hold the UI dispatcher while a background delegate_task publishes the task and posts GUI acceptance.
        var dispatchTask = Task.Run(
            () => coordinator.CallToolAsync(
                parent.Id,
                "delegate_task",
                new JsonObject { ["target"] = worker.Id, ["goal"] = "Must not reach worker after cancel" },
                ct),
            ct);

        Assert.True(SpinWait.SpinUntil(() => coordinator.GetTasksForItem(parent.Id).Count == 1, TimeSpan.FromSeconds(5)));
        Thread.Sleep(30);

        var visibleTask = Assert.Single(coordinator.GetTasksForItem(parent.Id));
        Assert.Equal(CollabTaskStatus.Running, visibleTask.Status);
        Assert.Empty(workerSession!.SentPrompts);

        // Cancel on the UI thread before the posted acceptance lambda executes.
        var cancelResult = coordinator.CallToolAsync(
            parent.Id,
            "cancel_task",
            new JsonObject { ["task_id"] = visibleTask.TaskId, ["reason"] = "Cancelled before GUI acceptance" },
            ct).GetAwaiter().GetResult();
        Assert.Null(cancelResult["isError"]);
        Assert.Equal(CollabTaskStatus.Cancelled, Assert.Single(coordinator.GetTasksForItem(parent.Id)).Status);
        Assert.Empty(workerSession.SentPrompts);

        // Resume the dispatcher so the posted delivery callback runs.
        Frames.Pump();
        var dispatchResult = await dispatchTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Frames.Pump();

        Assert.True(dispatchResult["isError"]?.GetValue<bool>());
        Assert.Empty(workerSession.SentPrompts);
        Assert.Empty(workerVm.QueuedPrompts);
        var finalTask = Assert.Single(coordinator.GetTasksForItem(parent.Id));
        Assert.Equal(visibleTask.TaskId, finalTask.TaskId);
        Assert.Equal(CollabTaskStatus.Cancelled, finalTask.Status);
    }

    [AvaloniaFact, FreshApp]
    public async Task Delegating_to_busy_gui_session_cancelled_before_acceptance_leaves_active_turn_uninterrupted()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-collab-cancel-busy-preaccept-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Lead", Surface = AgentSurface.Gui };
        var worker = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Worker", Surface = AgentSurface.Gui };
        projects.AddItem(project, parent);
        projects.AddItem(project, worker, activate: false);

        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        var parentBridge = new FakeGuiBridge(parent.Id, "opencode", parent.Title, dir);
        coordinator.RegisterGuiSession(parentBridge);

        var turn1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTurn1 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeAcpSession? workerSession = null;
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = s =>
        {
            workerSession = s;
            s.PromptHandler = async (session, _, _, token) =>
            {
                turn1Started.TrySetResult();
                var reply = await releaseTurn1.Task.WaitAsync(token);
                session.Emit(new AcpTextDeltaEvent("assistant", reply, "busy-preaccept-1"));
                return new AcpPromptResult { StopReason = "end_turn" };
            };
        };

        var tabRegistry = AppHost.Get<ITabContentRegistry>();
        var control = tabRegistry.GetOrCreate(project, worker);
        var workerVm = Assert.IsType<AgentGuiTabViewModel>(control.DataContext);
        await workerVm.ConnectAsync();
        Frames.Pump();
        Assert.NotNull(workerSession);

        var task1Res = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["goal"] = "Active Turn 1" },
            ct);
        Assert.Null(task1Res["isError"]);
        var task1Id = Body(task1Res)["task_id"]!.GetValue<string>();
        await turn1Started.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // Hold the UI dispatcher while background delegate_task #2 publishes and posts GUI acceptance.
        var dispatch2Task = Task.Run(
            () => coordinator.CallToolAsync(
                parent.Id,
                "delegate_task",
                new JsonObject { ["target"] = worker.Id, ["goal"] = "Cancelled Turn 2 before accept" },
                ct),
            ct);

        Assert.True(SpinWait.SpinUntil(() => coordinator.GetTasksForItem(parent.Id).Count == 2, TimeSpan.FromSeconds(5)));
        Thread.Sleep(30);

        var task2 = Assert.Single(coordinator.GetTasksForItem(parent.Id), t => t.TaskId != task1Id);
        Assert.Equal(CollabTaskStatus.Running, task2.Status);

        var cancelResult = coordinator.CallToolAsync(
            parent.Id,
            "cancel_task",
            new JsonObject { ["task_id"] = task2.TaskId, ["reason"] = "Cancelled while queued behind active turn" },
            ct).GetAwaiter().GetResult();
        Assert.Null(cancelResult["isError"]);

        Frames.Pump();
        var dispatch2Result = await dispatch2Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Frames.Pump();

        Assert.True(dispatch2Result["isError"]?.GetValue<bool>());
        Assert.Empty(workerVm.QueuedPrompts);
        Assert.Single(workerSession!.SentPrompts);
        Assert.Equal(0, workerSession.CancelCount);
        Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task2.TaskId).Status);

        releaseTurn1.TrySetResult("Active Turn 1 finished cleanly");
        var deadline = Environment.TickCount64 + 5000;
        while (coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task1Id).Status != CollabTaskStatus.Completed
            && Environment.TickCount64 < deadline)
        {
            Frames.Pump();
            await Task.Delay(20, ct);
        }
        var task1Final = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task1Id);
        Assert.Equal(CollabTaskStatus.Completed, task1Final.Status);
        Assert.Equal("Active Turn 1 finished cleanly", task1Final.ResultSummary);
        Assert.Single(workerSession.SentPrompts);
    }

    [AvaloniaFact, FreshApp]
    public async Task Delegating_to_existing_session_preserves_reopened_task_when_original_bridge_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, _, worker, workerBridge, coordinator) = await SetUpParentAndWorkerAsync("octopus-collab-reopen-rollback-");

        async Task RunReopenCaseAsync(string mode)
        {
            string? capturedThread = null;
            string? capturedTask = null;
            var reopenedFollowUp = false;
            using var cts = new CancellationTokenSource();

            workerBridge.OnDeliver = async (threadId, taskId, token) =>
            {
                if (reopenedFollowUp)
                {
                    return true;
                }
                capturedThread = threadId;
                capturedTask = taskId;

                var reply = await coordinator.CallToolAsync(
                    worker.Id,
                    "reply_thread",
                    new JsonObject { ["thread_id"] = threadId, ["message"] = $"agreed before {mode}", ["status"] = "agreed" },
                    token);
                Assert.Null(reply["isError"]);

                reopenedFollowUp = true;
                var followUp = await coordinator.CallToolAsync(
                    parent.Id,
                    "send_message",
                    new JsonObject { ["target_session_id"] = worker.Id, ["thread_id"] = threadId, ["message"] = $"follow-up during {mode}" },
                    token);
                Assert.Null(followUp["isError"]);

                if (mode == "false") return false;
                if (mode == "throw") throw new InvalidOperationException("original bridge failure after reopen");
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return true;
            };

            if (mode == "false")
            {
                var outer = await coordinator.CallToolAsync(
                    parent.Id,
                    "delegate_task",
                    new JsonObject { ["target"] = worker.Id, ["goal"] = "Reopen then return false" },
                    ct);
                Assert.True(outer["isError"]?.GetValue<bool>());
            }
            else if (mode == "throw")
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CallToolAsync(
                    parent.Id,
                    "delegate_task",
                    new JsonObject { ["target"] = worker.Id, ["goal"] = "Reopen then throw" },
                    ct));
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.CallToolAsync(
                    parent.Id,
                    "delegate_task",
                    new JsonObject { ["target"] = worker.Id, ["goal"] = "Reopen then cancel" },
                    cts.Token));
            }

            var retained = Assert.Single(coordinator.GetTasksForItem(parent.Id), t => t.TaskId == capturedTask);
            Assert.Equal(CollabTaskStatus.Running, retained.Status);
            Assert.Equal(capturedThread, retained.ThreadId);
            var thread = coordinator.FindThread(capturedThread!);
            Assert.NotNull(thread);
            Assert.Equal(CollabThreadStatus.Active, thread!.Status);
            Assert.Equal(3, thread.Messages.Count);
        }

        await RunReopenCaseAsync("false");
        await RunReopenCaseAsync("throw");
        await RunReopenCaseAsync("cancel");
    }

    [AvaloniaFact, FreshApp]
    public async Task Approval_during_refused_delegation_does_not_keep_a_task()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, _, worker, workerBridge, coordinator) = await SetUpParentAndWorkerAsync("octopus-collab-approval-refusal-");
        workerBridge.OnDeliver = (_, _, _) =>
        {
            coordinator.ReportChildApproval(new CollabBubbledApproval("a", parent.Id, worker.Id, "codex", "Worker", "Tool", null, null, _ => { }));
            coordinator.ClearChildApproval(worker.Id, "a");
            return Task.FromResult(false);
        };

        var result = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["goal"] = "undelivered" },
            ct);

        Assert.True(result["isError"]?.GetValue<bool>());
        Assert.Empty(coordinator.GetTasksForItem(parent.Id));
    }

    [AvaloniaFact, FreshApp]
    public async Task Owned_nested_followup_before_original_gui_acceptance_survives_original_cancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var originalToken = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var hold = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var dir = Directory.CreateTempSubdirectory("octopus-collab-live-owner-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Lead", Surface = AgentSurface.Gui };
        var middle = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Worker", Surface = AgentSurface.Gui };
        var leaf = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Leaf", Surface = AgentSurface.Gui };
        projects.AddItem(project, parent);
        projects.AddItem(project, middle, activate: false);
        projects.AddItem(project, leaf, activate: false);

        var host = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        host.Server.Start();
        host.RegisterGuiSession(new FakeGuiBridge(parent.Id, "opencode", parent.Title, dir));
        host.RegisterGuiSession(new FakeGuiBridge(leaf.Id, "opencode", leaf.Title, dir));

        FakeAcpSession? acp = null;
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = s =>
        {
            acp = s;
            s.PromptHandler = (_, _, _, t) => hold.Task.WaitAsync(t);
        };
        var tabRegistry = AppHost.Get<ITabContentRegistry>();
        var control = tabRegistry.GetOrCreate(project, middle);
        var vm = Assert.IsType<AgentGuiTabViewModel>(control.DataContext);
        await vm.ConnectAsync();
        Frames.Pump();
        Assert.NotNull(acp);

        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host)!;

        // The GUI thread is occupied here, so the original background delivery cannot commit.
        var original = Task.Run(() => host.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = middle.Id, ["goal"] = "original before acceptance" },
            originalToken.Token));
        Assert.True(SpinWait.SpinUntil(() => { lock (ledger.Gate) return ledger.Pending.TryGetValue(middle.Id, out var p) && p.Count == 1; }, TimeSpan.FromSeconds(3)));
        var advertised = Assert.Single(host.GetTasksForItem(parent.Id));
        Assert.Empty(acp!.SentPrompts);

        // A later send is accepted inline on the UI thread. It runs and acquires a real child.
        var follow = host.CallToolAsync(
            parent.Id,
            "send_message",
            new JsonObject { ["thread_id"] = advertised.ThreadId, ["target_session_id"] = middle.Id, ["message"] = "follow-up with a child" },
            ct);
        Assert.True(follow.IsCompletedSuccessfully);
        Assert.Null(follow.Result["isError"]);
        Assert.Contains("follow-up with a child", Assert.Single(acp.SentPrompts).Text);
        var live = (ICollabSessionBridge)vm;
        Assert.Equal(advertised.ThreadId, live.ActiveCollabThreadId);

        var nested = host.CallToolAsync(
            middle.Id,
            "delegate_task",
            new JsonObject { ["target"] = leaf.Id, ["goal"] = "nested child" },
            ct);
        Assert.True(nested.IsCompletedSuccessfully);
        Assert.Null(nested.Result["isError"]);
        var child = Body(nested.Result)["task_id"]!.GetValue<string>();
        lock (ledger.Gate) Assert.Equal(advertised.TaskId, ledger.GetParentTaskId(child));

        lock (ledger.Gate)
        {
            // Prevent the original background call from rolling back until the accepted GUI turn
            // has ended and the coordinator has recorded that it is waiting for its own child.
            originalToken.Cancel();
            hold.SetResult(new AcpPromptResult { StopReason = "end_turn" });
            Assert.True(Frames.Until(() => !vm.IsTurnActive));
            Assert.Equal("Waiting for delegated work", host.GetTasksForItem(parent.Id).Single().LatestActivity);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original.WaitAsync(TimeSpan.FromSeconds(4), ct));
        Assert.True(
            host.GetTasksForItem(parent.Id).Any(t => t.TaskId == advertised.TaskId),
            $"accepted follow-up lost parent {advertised.TaskId}; nested child is {host.GetTasksForItem(middle.Id).Single(t => t.TaskId == child).Status}");
        var cancel = await host.CancelDelegatedTaskAsync(parent.Id, advertised.TaskId, "stop child", ct);
        Assert.Null(cancel["isError"]);
        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(middle.Id).Single(t => t.TaskId == child).Status);
    }

    [AvaloniaFact, FreshApp]
    public async Task Accepted_followup_without_nested_child_survives_original_refusal_while_unrelated_refusal_rolls_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, _, worker, workerBridge, coordinator) = await SetUpParentAndWorkerAsync("octopus-collab-accepted-successor-");

        var firstDelivery = true;
        string? capturedTaskId = null;
        string? capturedThreadId = null;
        workerBridge.OnDeliver = async (threadId, taskId, token) =>
        {
            if (firstDelivery)
            {
                firstDelivery = false;
                capturedThreadId = threadId;
                capturedTaskId = taskId;
                // A follow-up on the same thread is accepted while the original dispatch is still in flight.
                var follow = await coordinator.CallToolAsync(
                    parent.Id,
                    "send_message",
                    new JsonObject { ["thread_id"] = threadId, ["target_session_id"] = worker.Id, ["message"] = "accepted successor" },
                    token);
                Assert.Null(follow["isError"]);
                return false;
            }
            return true;
        };

        var outer = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["goal"] = "original refused after successor accepted" },
            ct);
        Assert.True(outer["isError"]?.GetValue<bool>());

        var retained = Assert.Single(coordinator.GetTasksForItem(parent.Id), t => t.TaskId == capturedTaskId);
        Assert.Equal(CollabTaskStatus.Running, retained.Status);
        Assert.Equal(capturedThreadId, retained.ThreadId);

        // An unrelated refused delegation with approval churn on the same busy worker still rolls back cleanly.
        workerBridge.OnDeliver = (_, _, _) =>
        {
            coordinator.ReportChildApproval(new CollabBubbledApproval("ap-unrelated", parent.Id, worker.Id, "codex", "Worker", "Tool", null, null, _ => { }));
            coordinator.ClearChildApproval(worker.Id, "ap-unrelated");
            return Task.FromResult(false);
        };
        var unrelatedRefusal = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["goal"] = "unrelated undelivered" },
            ct);
        Assert.True(unrelatedRefusal["isError"]?.GetValue<bool>());
        Assert.Single(coordinator.GetTasksForItem(parent.Id));
    }

    [AvaloniaFact, FreshApp]
    public async Task Rollback_during_successor_execution_preserves_parent_task_and_cancelled_delivery_cannot_accept()
    {
        var ct = TestContext.Current.CancellationToken;
        using var originalToken = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var hold = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<JsonObject>? original = null;
        var originalReturnedDuringSuccessor = false;

        var dir = Directory.CreateTempSubdirectory("octopus-collab-rollback-during-accept-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var caller = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Lead", Surface = AgentSurface.Gui };
        var worker = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Worker", Surface = AgentSurface.Gui };
        projects.AddItem(project, caller);
        projects.AddItem(project, worker, activate: false);

        var host = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        host.Server.Start();
        host.RegisterGuiSession(new FakeGuiBridge(caller.Id, "opencode", caller.Title, dir));

        FakeAcpSession? acp = null;
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = s =>
        {
            acp = s;
            s.PromptHandler = (_, _, _, _) =>
            {
                // Original cancellation and rollback finish before the successor's GUI bridge returns.
                originalToken.Cancel();
                originalReturnedDuringSuccessor = SpinWait.SpinUntil(() => original!.IsCompleted, TimeSpan.FromSeconds(4));
                return hold.Task;
            };
        };
        var tabRegistry = AppHost.Get<ITabContentRegistry>();
        var control = tabRegistry.GetOrCreate(project, worker);
        var vm = Assert.IsType<AgentGuiTabViewModel>(control.DataContext);
        await vm.ConnectAsync();
        Frames.Pump();
        Assert.NotNull(acp);

        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host)!;

        original = Task.Run(() => host.CallToolAsync(
            caller.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["goal"] = "never accepted" },
            originalToken.Token));
        Assert.True(SpinWait.SpinUntil(() => { lock (ledger.Gate) return ledger.Pending.ContainsKey(worker.Id); }, TimeSpan.FromSeconds(4)));
        var task = Assert.Single(host.GetTasksForItem(caller.Id));

        var follow = await host.CallToolAsync(
            caller.Id,
            "send_message",
            new JsonObject
            {
                ["thread_id"] = task.ThreadId,
                ["target_session_id"] = worker.Id,
                ["message"] = "second message",
                ["expect_reply"] = true,
            },
            ct);
        Assert.Null(follow["isError"]);
        Assert.True(originalReturnedDuringSuccessor);
        Assert.True(vm.IsTurnActive);
        Assert.Contains("second message", Assert.Single(acp!.SentPrompts).Text);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original);

        acp.Emit(new AcpTextDeltaEvent("assistant", "successor finished", "msg-succ"));
        hold.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        Assert.True(Frames.Until(() => !vm.IsTurnActive));
        Assert.True(SpinWait.SpinUntil(
            () => host.GetTasksForItem(caller.Id).Any(t => t.TaskId == task.TaskId && t.Status == CollabTaskStatus.Completed),
            TimeSpan.FromSeconds(4)));
        var completed = Assert.Single(host.GetTasksForItem(caller.Id), t => t.TaskId == task.TaskId);
        Assert.Equal("successor finished", completed.ResultSummary);

        // Acceptance-after-cancellation control: a delivery parked off-UI whose task is explicitly cancelled before
        // GUI acceptance must be refused without executing or resurrecting the task.
        var parked = Task.Run(() => host.CallToolAsync(
            caller.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["goal"] = "cancelled before acceptance" },
            ct), ct);
        Assert.True(SpinWait.SpinUntil(() => host.GetTasksForItem(caller.Id).Count == 2, TimeSpan.FromSeconds(4)));
        var cancelledTask = host.GetTasksForItem(caller.Id).Single(t => t.TaskId != task.TaskId);
        var cancelRes = await host.CancelDelegatedTaskAsync(caller.Id, cancelledTask.TaskId, "stop before accept", ct);
        Assert.Null(cancelRes["isError"]);
        Frames.Pump();
        var parkedRes = await parked.WaitAsync(TimeSpan.FromSeconds(4), ct);
        Assert.True(parkedRes["isError"]?.GetValue<bool>());
        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(caller.Id).Single(t => t.TaskId == cancelledTask.TaskId).Status);
        Assert.Single(acp.SentPrompts);
    }

    private static async Task<JsonObject> PostMcpAsync(HttpClient http, string url, string token, string json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(body)!.AsObject();
    }
}
