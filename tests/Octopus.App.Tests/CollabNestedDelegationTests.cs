using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Octopus.Acp.Protocol;
using Octopus.App.AgentGui;
using Octopus.App.Collab;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Xunit;

namespace Octopus.App.Tests;

public sealed class CollabNestedDelegationTests
{
    private sealed class StubBridge(ProjectItem item, string cwd) : ICollabSessionBridge
    {
        private readonly object _gate = new();

        public string ItemId => item.Id;
        public string AgentId => item.AgentId ?? "codex";
        public string DisplayTitle => item.DisplayTitle;
        public string WorkingDirectory => cwd;
        public bool IsBusy { get; set; }
        public bool IsConnected { get; set; } = true;
        public string? CurrentStatusDetail => null;
        public List<(string Prompt, string? ThreadId, string? TaskId, bool ExpectReply)> Delivered { get; } = [];
        public int TurnCancellations;
        public List<(string? ThreadId, string? TaskId)> CancelledQueuedPrompts { get; } = [];

        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default) =>
            DeliverCollabPromptAsync(promptText, threadId, taskId, expectReply: true, cancellationToken);

        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId, string? taskId, bool expectReply, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Delivered.Add((promptText, threadId, taskId, expectReply));
            }
            return Task.FromResult(true);
        }

        public int CancelQueuedCollabPrompts(string? threadId = null, string? taskId = null)
        {
            lock (_gate)
            {
                CancelledQueuedPrompts.Add((threadId, taskId));
            }
            return 0;
        }

        public Task CancelActiveTurnAsync()
        {
            Interlocked.Increment(ref TurnCancellations);
            return Task.CompletedTask;
        }

        public string ReadRecentSummary(int maxTurns = 5) => "";
    }

    private static JsonObject Body(JsonObject toolResult) =>
        JsonNode.Parse(toolResult["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, int maxSteps = 120)
    {
        for (var i = 0; i < maxSteps && !condition(); i++)
        {
            Frames.Pump();
            await Task.Delay(25, ct);
        }
        Assert.True(condition());
    }

    private static async Task<(CollabCoordinator Host, IProjectService Projects, Project Project, string Dir)> SetUpHostAsync(string prefix)
    {
        await Task.Yield();
        var dir = Directory.CreateTempSubdirectory(prefix).FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var host = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        host.Server.Start();
        return (host, projects, project, dir);
    }

    private static (ProjectItem Item, StubBridge Bridge) AddStubAgent(
        CollabCoordinator host,
        IProjectService projects,
        Project project,
        string dir,
        string title,
        string agentId = "codex",
        string? parentItemId = null)
    {
        var item = new ProjectItem
        {
            Kind = ProjectItemKind.Agent,
            AgentId = agentId,
            Title = title,
            Surface = AgentSurface.Gui,
            ParentItemId = parentItemId,
        };
        projects.AddItem(project, item, activate: false);
        var bridge = new StubBridge(item, dir);
        host.RegisterGuiSession(bridge);
        return (item, bridge);
    }

    private static async Task<(ProjectItem Item, AgentGuiTabViewModel Vm, FakeAcpSession Session)> AddRealGuiAgentAsync(
        IProjectService projects,
        Project project,
        string title,
        Func<FakeAcpSession, string, IReadOnlyList<AcpPromptAttachment>?, CancellationToken, Task<AcpPromptResult>> promptHandler,
        string? parentItemId = null)
    {
        var item = new ProjectItem
        {
            Kind = ProjectItemKind.Agent,
            AgentId = "opencode",
            Title = title,
            Surface = AgentSurface.Gui,
            ParentItemId = parentItemId,
        };
        projects.AddItem(project, item, activate: false);

        FakeAcpSession? session = null;
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = s =>
        {
            session = s;
            s.PromptHandler = promptHandler;
        };

        var tabRegistry = AppHost.Get<ITabContentRegistry>();
        var control = tabRegistry.GetOrCreate(project, item);
        var vm = Assert.IsType<AgentGuiTabViewModel>(control.DataContext);
        await vm.ConnectAsync();
        Frames.Pump();
        Assert.NotNull(session);
        return (item, vm, session!);
    }

    private static async Task<(string TaskId, string ThreadId, string SessionId)> DelegateAsync(
        CollabCoordinator host,
        string callerId,
        string target,
        string goal,
        CancellationToken ct)
    {
        var result = await host.CallToolAsync(
            callerId,
            "delegate_task",
            new JsonObject { ["target"] = target, ["goal"] = goal, ["surface"] = "gui" },
            ct);
        Assert.Null(result["isError"]);
        var body = Body(result);
        return (
            body["task_id"]!.GetValue<string>(),
            body["thread_id"]!.GetValue<string>(),
            body["session_id"]!.GetValue<string>());
    }

    private static Task<JsonObject> ReplyAsync(
        CollabCoordinator host,
        string responderId,
        string threadId,
        string message,
        CancellationToken ct,
        string status = "agreed") =>
        host.CallToolAsync(
            responderId,
            "reply_thread",
            new JsonObject { ["thread_id"] = threadId, ["message"] = message, ["status"] = status },
            ct);

    [AvaloniaFact, FreshApp]
    public async Task Three_level_ordinary_middle_idle_before_child_replies_keeps_responsibility_and_child_running()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, projects, project, dir) = await SetUpHostAsync("octopus-nested-idle-");
        var (a, aBridge) = AddStubAgent(host, projects, project, dir, "Root A", "claude");
        var (b, _) = AddStubAgent(host, projects, project, dir, "Middle B", "codex", parentItemId: a.Id);
        var (c, cBridge) = AddStubAgent(host, projects, project, dir, "Leaf C", "codex", parentItemId: b.Id);

        var (taskAB, threadAB, _) = await DelegateAsync(host, a.Id, b.Id, "Investigate subsystem", ct);
        var (taskBC, threadBC, _) = await DelegateAsync(host, b.Id, c.Id, "Inspect leaf module", ct);

        // B ends its turn normally before C replies (both exact and legacy end-turn paths must preserve A->B).
        host.NotifyTurnCompleted(b.Id, "Delegated leaf inspection to C; waiting for C.", threadAB, taskAB);
        await Task.Delay(100, ct);

        var abAfterIdle = host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB);
        var bcAfterIdle = host.GetTasksForItem(c.Id).Single(t => t.TaskId == taskBC);
        Assert.Equal(CollabTaskStatus.Running, abAfterIdle.Status);
        Assert.Null(abAfterIdle.ResultSummary);
        Assert.Equal(CollabThreadStatus.Active, host.FindThread(threadAB)!.Status);
        Assert.Empty(aBridge.Delivered);

        Assert.Equal(CollabTaskStatus.Running, bcAfterIdle.Status);
        Assert.Equal(CollabThreadStatus.Active, host.FindThread(threadBC)!.Status);
        Assert.Equal(0, Volatile.Read(ref cBridge.TurnCancellations));
    }

    [AvaloniaFact, FreshApp]
    public async Task Three_level_legacy_middle_idle_followed_by_child_leaf_completion_and_deliberate_middle_report_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, projects, project, dir) = await SetUpHostAsync("octopus-nested-deliberate-");
        var (a, aBridge) = AddStubAgent(host, projects, project, dir, "Root A", "claude");
        var (b, bBridge) = AddStubAgent(host, projects, project, dir, "Middle B", "codex", parentItemId: a.Id);
        var (c, _) = AddStubAgent(host, projects, project, dir, "Leaf C", "codex", parentItemId: b.Id);

        var (taskAB, threadAB, _) = await DelegateAsync(host, a.Id, b.Id, "Coordinate fix", ct);
        var (taskBC, _, _) = await DelegateAsync(host, b.Id, c.Id, "Collect leaf diagnostics", ct);

        // B goes ordinary-idle via legacy 2-arg callback before C finishes.
        host.NotifyTurnCompleted(b.Id, "Waiting on C diagnostics");
        await Task.Delay(100, ct);

        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB).Status);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(c.Id).Single(t => t.TaskId == taskBC).Status);
        Assert.Empty(aBridge.Delivered);

        // C finishes as a normal leaf via implicit turn completion -> wakes B with C's findings.
        host.NotifyTurnCompleted(c.Id, "Leaf diagnostics: cache miss on cold start");
        await WaitUntilAsync(() => host.GetTasksForItem(c.Id).Single(t => t.TaskId == taskBC).Status == CollabTaskStatus.Completed, ct);
        await WaitUntilAsync(() => bBridge.Delivered.Any(d => d.Prompt.Contains("cache miss on cold start", StringComparison.Ordinal)), ct);

        // A->B is still open until B deliberately reports.
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB).Status);
        Assert.Empty(aBridge.Delivered);

        // B deliberately reports synthesized result to A.
        var reply = await ReplyAsync(host, b.Id, threadAB, "Root cause confirmed via C: cache miss on cold start", ct);
        Assert.Null(reply["isError"]);
        Assert.Equal(a.Id, Body(reply)["delivered_to"]!.GetValue<string>());

        var abDone = host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB);
        Assert.Equal(CollabTaskStatus.Completed, abDone.Status);
        Assert.Equal("Root cause confirmed via C: cache miss on cold start", abDone.ResultSummary);
        Assert.Equal(CollabThreadStatus.Agreed, host.FindThread(threadAB)!.Status);
        Assert.Contains(aBridge.Delivered, d => d.Prompt.Contains("Root cause confirmed via C", StringComparison.Ordinal));
    }

    [AvaloniaFact, FreshApp]
    public async Task Normal_leaf_and_already_finished_child_preserve_implicit_reports_and_parent_idle_control()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, projects, project, dir) = await SetUpHostAsync("octopus-nested-leaf-");
        var (a, aBridge) = AddStubAgent(host, projects, project, dir, "Root A", "claude");
        var (leaf, _) = AddStubAgent(host, projects, project, dir, "Leaf Worker", "codex", parentItemId: a.Id);

        // Control 1: Single-level parent ordinary idle keeps child running.
        var (leafTask, _, _) = await DelegateAsync(host, a.Id, leaf.Id, "Compute checksum", ct);
        host.NotifyTurnCompleted(a.Id, "Waiting for leaf worker");
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(a.Id).Single(t => t.TaskId == leafTask).Status);

        // Control 2: Normal leaf implicit report still completes and wakes parent.
        host.NotifyTurnCompleted(leaf.Id, "checksum=abc123");
        await WaitUntilAsync(() => host.GetTasksForItem(a.Id).Single(t => t.TaskId == leafTask).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("checksum=abc123", host.GetTasksForItem(a.Id).Single(t => t.TaskId == leafTask).ResultSummary);
        await WaitUntilAsync(() => aBridge.Delivered.Any(d => d.Prompt.Contains("checksum=abc123", StringComparison.Ordinal)), ct);

        // Control 3: Three-level A->B->C where C already completed BEFORE B ends Turn 1 still allows B's implicit report.
        var (b, _) = AddStubAgent(host, projects, project, dir, "Middle B", "codex", parentItemId: a.Id);
        var (c, _) = AddStubAgent(host, projects, project, dir, "Leaf C", "codex", parentItemId: b.Id);
        var (taskAB, threadAB, _) = await DelegateAsync(host, a.Id, b.Id, "Run sync subcheck", ct);
        var (taskBC, _, _) = await DelegateAsync(host, b.Id, c.Id, "Quick subcheck", ct);

        host.NotifyTurnCompleted(c.Id, "subcheck green");
        await WaitUntilAsync(() => host.GetTasksForItem(c.Id).Single(t => t.TaskId == taskBC).Status == CollabTaskStatus.Completed, ct);

        host.NotifyTurnCompleted(b.Id, "All subchecks green", threadAB, taskAB);
        await WaitUntilAsync(() => host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("All subchecks green", host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Real_gui_middle_idle_keeps_parent_running_through_child_wakeup_until_deliberate_report()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, projects, project, dir) = await SetUpHostAsync("octopus-nested-gui-");
        var (a, aBridge) = AddStubAgent(host, projects, project, dir, "Root A", "claude");
        var (c, cBridge) = AddStubAgent(host, projects, project, dir, "Leaf C", "codex");

        var turnNumber = 0;
        var turn1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTurn1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn2Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTurn2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (bItem, _, _) = await AddRealGuiAgentAsync(
            projects,
            project,
            "Real GUI Middle B",
            async (s, _, _, token) =>
            {
                var n = Interlocked.Increment(ref turnNumber);
                if (n == 1)
                {
                    turn1Started.TrySetResult();
                    await releaseTurn1.Task.WaitAsync(token);
                    s.Emit(new AcpTextDeltaEvent("assistant", "Dispatched C and pausing until C replies.", "msg-1"));
                    return new AcpPromptResult { StopReason = "end_turn" };
                }

                turn2Started.TrySetResult();
                await releaseTurn2.Task.WaitAsync(token);
                s.Emit(new AcpTextDeltaEvent("assistant", "Reviewed C reply; preparing final report.", "msg-2"));
                return new AcpPromptResult { StopReason = "end_turn" };
            },
            parentItemId: a.Id);

        var (taskAB, threadAB, _) = await DelegateAsync(host, a.Id, bItem.Id, "Investigate with C", ct);
        await turn1Started.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // During Turn 1 on real GUI B, B delegates to C, then ends Turn 1 normally.
        var (taskBC, threadBC, _) = await DelegateAsync(host, bItem.Id, c.Id, "Check leaf state", ct);
        releaseTurn1.TrySetResult();
        await Task.Delay(120, ct);
        Frames.Pump();

        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB).Status);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(c.Id).Single(t => t.TaskId == taskBC).Status);
        Assert.Empty(aBridge.Delivered);
        Assert.Equal(0, Volatile.Read(ref cBridge.TurnCancellations));

        // C completes and wakes real GUI B (Turn 2 has expectReply=false).
        Assert.Null((await ReplyAsync(host, c.Id, threadBC, "Leaf state is healthy", ct))["isError"]);
        await turn2Started.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // Even if Turn 2 ends without calling reply_thread(threadAB), 0B does not fabricate a premature completion.
        releaseTurn2.TrySetResult();
        await Task.Delay(120, ct);
        Frames.Pump();
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB).Status);
        Assert.Empty(aBridge.Delivered);

        // B's deliberate report settles A->B and notifies A.
        var deliberate = await ReplyAsync(host, bItem.Id, threadAB, "Verified via C: healthy", ct);
        Assert.Null(deliberate["isError"]);
        Assert.Equal(CollabTaskStatus.Completed, host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB).Status);
        Assert.Equal("Verified via C: healthy", host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB).ResultSummary);
        Assert.Contains(aBridge.Delivered, d => d.Prompt.Contains("Verified via C: healthy", StringComparison.Ordinal));
    }

    [AvaloniaFact, FreshApp]
    public async Task Reused_middle_session_isolates_nested_delegation_from_human_turn_and_queued_leaf_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, projects, project, dir) = await SetUpHostAsync("octopus-nested-reused-");
        var (a, aBridge) = AddStubAgent(host, projects, project, dir, "Caller A", "claude");
        var (d, dBridge) = AddStubAgent(host, projects, project, dir, "Caller D", "claude");
        var (cHuman, cHumanBridge) = AddStubAgent(host, projects, project, dir, "Human Child", "codex");
        var (c1, c1Bridge) = AddStubAgent(host, projects, project, dir, "Task A Child", "codex");

        var turnNumber = 0;
        var turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string>? releaseCurrentTurn = null;

        // Reused session B has ParentItemId == null (shared general-purpose session).
        var (bItem, bVm, _) = await AddRealGuiAgentAsync(
            projects,
            project,
            "Reused Session B",
            async (s, _, _, token) =>
            {
                var n = Interlocked.Increment(ref turnNumber);
                var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref releaseCurrentTurn, gate);
                turnStarted.TrySetResult(n);
                var text = await gate.Task.WaitAsync(token);
                s.Emit(new AcpTextDeltaEvent("assistant", text, $"msg-{n}"));
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        // Turn 1: Ordinary human prompt starts on reused B.
        bVm.InputText = "Start background human research";
        var humanSend = bVm.SendPromptAsync();
        Assert.Equal(1, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));

        // While the human turn is active, A and D queue two tasks on reused B.
        var (taskAB1, threadAB1, _) = await DelegateAsync(host, a.Id, bItem.Id, "Nested work for A", ct);
        var (taskDB2, _, _) = await DelegateAsync(host, d.Id, bItem.Id, "Leaf work for D", ct);
        Assert.Equal(2, bVm.QueuedPrompts.Count);

        // During the human turn, B delegates unrelated human work to cHuman -> must NOT be owned by queued taskAB1.
        var (taskHuman, _, _) = await DelegateAsync(host, bItem.Id, cHuman.Id, "Human research subtask", ct);

        // End Turn 1 (human) -> Turn 2 (taskAB1) begins.
        turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult("Started human research");
        Assert.Equal(2, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));

        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB1).Status);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(d.Id).Single(t => t.TaskId == taskDB2).Status);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(cHuman.Id).Single(t => t.TaskId == taskHuman).Status);

        // During Turn 2 (taskAB1), B delegates taskBC1 to c1, then ends Turn 2 normally (waiting on c1).
        var (taskBC1, _, _) = await DelegateAsync(host, bItem.Id, c1.Id, "Subtask for A", ct);
        turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult("Delegated to c1; waiting");
        Assert.Equal(3, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));

        // taskAB1 must stay Running (not false success), while Turn 3 (taskDB2) runs independently as a normal leaf.
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB1).Status);
        Assert.Equal(CollabThreadStatus.Active, host.FindThread(threadAB1)!.Status);
        Assert.Empty(aBridge.Delivered);

        // End Turn 3 (taskDB2, leaf with no delegations) -> taskDB2 MUST complete implicitly without being blocked by taskBC1 or taskHuman!
        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult("Leaf result for D");
        await humanSend.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await WaitUntilAsync(() => host.GetTasksForItem(d.Id).Single(t => t.TaskId == taskDB2).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("Leaf result for D", host.GetTasksForItem(d.Id).Single(t => t.TaskId == taskDB2).ResultSummary);
        await WaitUntilAsync(() => dBridge.Delivered.Any(x => x.Prompt.Contains("Leaf result for D", StringComparison.Ordinal)), ct);

        // taskAB1, taskBC1, and taskHuman are still Running.
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB1).Status);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(c1.Id).Single(t => t.TaskId == taskBC1).Status);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(cHuman.Id).Single(t => t.TaskId == taskHuman).Status);

        // Cancelling taskAB1 on reused B cancels its owned descendant taskBC1, while leaving unrelated human work taskHuman untouched.
        var cancelRes = await host.CallToolAsync(a.Id, "cancel_task", new JsonObject { ["task_id"] = taskAB1, ["reason"] = "A aborted" }, ct);
        Assert.Null(cancelRes["isError"]);
        Assert.Equal(2, Body(cancelRes)["cancelled_count"]!.GetValue<int>());

        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB1).Status);
        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(c1.Id).Single(t => t.TaskId == taskBC1).Status);
        await WaitUntilAsync(() => Volatile.Read(ref c1Bridge.TurnCancellations) == 1, ct);

        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(cHuman.Id).Single(t => t.TaskId == taskHuman).Status);
        Assert.Equal(0, Volatile.Read(ref cHumanBridge.TurnCancellations));
    }

    [AvaloniaFact, FreshApp]
    public async Task Explicit_stop_on_middle_session_cancels_owned_pending_descendants_without_affecting_unrelated_human_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, projects, project, dir) = await SetUpHostAsync("octopus-nested-stop-");
        var (a, aBridge) = AddStubAgent(host, projects, project, dir, "Root A", "claude");
        var (b, _) = AddStubAgent(host, projects, project, dir, "Reused Middle B", "codex");
        var (cHuman, cHumanBridge) = AddStubAgent(host, projects, project, dir, "Human Child", "codex");
        var (cOwned, cOwnedBridge) = AddStubAgent(host, projects, project, dir, "Owned Child", "codex", parentItemId: b.Id);
        var (dGrandchild, dGrandchildBridge) = AddStubAgent(host, projects, project, dir, "Grandchild D", "codex", parentItemId: cOwned.Id);

        // Unrelated human delegation on reused B before A's task arrives.
        var (taskHuman, _, _) = await DelegateAsync(host, b.Id, cHuman.Id, "Unrelated human background task", ct);

        // A delegates taskAB to reused B; B delegates taskBC (owned by taskAB) to dedicated child cOwned, which delegates taskCD to dGrandchild.
        var (taskAB, _, _) = await DelegateAsync(host, a.Id, b.Id, "Scoped task for A", ct);
        var (taskBC, _, _) = await DelegateAsync(host, b.Id, cOwned.Id, "Owned child work", ct);
        var (taskCD, _, _) = await DelegateAsync(host, cOwned.Id, dGrandchild.Id, "Owned grandchild work", ct);

        // Explicit stop on B (even without cancelDelegationsSince) must settle taskAB and cancel owned descendants taskBC -> taskCD,
        // without touching unrelated taskHuman.
        host.NotifyTurnAborted(b.Id, "The user stopped this session before it finished.", failed: false);

        await WaitUntilAsync(() =>
            host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB).Status == CollabTaskStatus.Cancelled
            && host.GetTasksForItem(cOwned.Id).Single(t => t.TaskId == taskBC).Status == CollabTaskStatus.Cancelled
            && host.GetTasksForItem(dGrandchild.Id).Single(t => t.TaskId == taskCD).Status == CollabTaskStatus.Cancelled, ct);
        await WaitUntilAsync(() =>
            Volatile.Read(ref cOwnedBridge.TurnCancellations) == 1
            && Volatile.Read(ref dGrandchildBridge.TurnCancellations) == 1
            && aBridge.Delivered.Any(d => d.Prompt.Contains("Status: cancelled", StringComparison.Ordinal)), ct);

        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(cHuman.Id).Single(t => t.TaskId == taskHuman).Status);
        Assert.Equal(0, Volatile.Read(ref cHumanBridge.TurnCancellations));
    }

    [AvaloniaFact, FreshApp]
    public async Task Removing_middle_project_item_cancels_owed_tasks_and_owned_pending_descendants_without_disrupting_unrelated_reused_target()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, projects, project, dir) = await SetUpHostAsync("octopus-nested-remove-");
        var (a, _) = AddStubAgent(host, projects, project, dir, "Root A", "claude");
        var (x, _) = AddStubAgent(host, projects, project, dir, "Unrelated Caller X", "claude");
        var (b, _) = AddStubAgent(host, projects, project, dir, "Middle B", "codex", parentItemId: a.Id);
        var (cDedicated, cDedicatedBridge) = AddStubAgent(host, projects, project, dir, "Dedicated Child C", "codex", parentItemId: b.Id);
        var (cReused, cReusedBridge) = AddStubAgent(host, projects, project, dir, "Reused Peer C", "codex");

        // Unrelated caller X already has an active head task on cReused.
        var (taskXC, _, _) = await DelegateAsync(host, x.Id, cReused.Id, "Unrelated active work on C", ct);

        // Three-level A -> B -> cDedicated, and B also queues a task behind X on cReused.
        var (taskAB, threadAB, _) = await DelegateAsync(host, a.Id, b.Id, "Middle job", ct);
        var (taskBC, threadBC, _) = await DelegateAsync(host, b.Id, cDedicated.Id, "Dedicated child job", ct);
        var (taskBReused, _, _) = await DelegateAsync(host, b.Id, cReused.Id, "Queued job from B", ct);

        // B goes ordinary idle before removal -> taskAB stays open, descendants stay running.
        host.NotifyTurnCompleted(b.Id, "Waiting on children", threadAB, taskAB);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB).Status);

        // Removing B must settle taskAB and cancel B's pending descendants (taskBC and taskBReused),
        // while leaving unrelated active taskXC on cReused running and uninterrupted.
        projects.RemoveItem(b.Id);

        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB).Status);
        Assert.Equal(CollabThreadStatus.Cancelled, host.FindThread(threadAB)!.Status);
        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(cDedicated.Id).Single(t => t.TaskId == taskBC).Status);
        Assert.Equal(CollabThreadStatus.Cancelled, host.FindThread(threadBC)!.Status);
        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(cReused.Id).Single(t => t.TaskId == taskBReused).Status);

        await WaitUntilAsync(() => Volatile.Read(ref cDedicatedBridge.TurnCancellations) == 1, ct);

        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(x.Id).Single(t => t.TaskId == taskXC).Status);
        Assert.Equal(0, Volatile.Read(ref cReusedBridge.TurnCancellations));
        Assert.Contains(cReusedBridge.CancelledQueuedPrompts, q => q.TaskId == taskBReused);
    }

    [AvaloniaFact, FreshApp]
    public async Task Removing_root_project_item_cancels_owned_descendants_on_reused_middle_without_disrupting_human_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, projects, project, dir) = await SetUpHostAsync("octopus-nested-remove-root-");
        var (a, _) = AddStubAgent(host, projects, project, dir, "Root A", "claude");
        var (bReused, _) = AddStubAgent(host, projects, project, dir, "Reused Middle B", "codex");
        var (cHuman, cHumanBridge) = AddStubAgent(host, projects, project, dir, "Human Child", "codex");
        var (cOwned, cOwnedBridge) = AddStubAgent(host, projects, project, dir, "Owned Child", "codex", parentItemId: bReused.Id);
        var (dGrandchild, dGrandchildBridge) = AddStubAgent(host, projects, project, dir, "Grandchild D", "codex", parentItemId: cOwned.Id);

        // Unrelated human work started on reused B before A's task.
        var (taskHuman, _, _) = await DelegateAsync(host, bReused.Id, cHuman.Id, "Human child work", ct);

        // A delegates to reused B, B delegates owned taskBC to dedicated cOwned, which delegates taskCD to dGrandchild.
        var (taskAB, threadAB, _) = await DelegateAsync(host, a.Id, bReused.Id, "Root A job", ct);
        var (taskBC, _, _) = await DelegateAsync(host, bReused.Id, cOwned.Id, "Owned child job", ct);
        var (taskCD, _, _) = await DelegateAsync(host, cOwned.Id, dGrandchild.Id, "Grandchild job", ct);
        host.NotifyTurnCompleted(bReused.Id, "Waiting on owned child", threadAB, taskAB);

        projects.RemoveItem(a.Id);

        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(bReused.Id).Single(t => t.TaskId == taskAB).Status);
        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(cOwned.Id).Single(t => t.TaskId == taskBC).Status);
        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(dGrandchild.Id).Single(t => t.TaskId == taskCD).Status);
        await WaitUntilAsync(() =>
            Volatile.Read(ref cOwnedBridge.TurnCancellations) == 1
            && Volatile.Read(ref dGrandchildBridge.TurnCancellations) == 1, ct);

        // Unrelated human work on reused B must remain untouched.
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(cHuman.Id).Single(t => t.TaskId == taskHuman).Status);
        Assert.Equal(0, Volatile.Read(ref cHumanBridge.TurnCancellations));
    }

    [AvaloniaFact, FreshApp]
    public async Task Spawned_middle_reused_by_human_does_not_cancel_human_child()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, projects, project, dir) = await SetUpHostAsync("octopus-nested-spawned-reused-");
        var (a, aBridge) = AddStubAgent(host, projects, project, dir, "Caller A", "claude");
        var (d, dBridge) = AddStubAgent(host, projects, project, dir, "Caller D", "claude");
        var (cHuman, cHumanBridge) = AddStubAgent(host, projects, project, dir, "Human Child", "codex");
        var (c1, c1Bridge) = AddStubAgent(host, projects, project, dir, "Task A Child", "codex");

        var turnNumber = 0;
        var turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string>? releaseCurrentTurn = null;

        // Spawned session B (ParentItemId == A) reused by a human prompt before A's queued task runs.
        var (bItem, bVm, _) = await AddRealGuiAgentAsync(
            projects,
            project,
            "Spawned Reused Session B",
            async (s, _, _, token) =>
            {
                var n = Interlocked.Increment(ref turnNumber);
                var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref releaseCurrentTurn, gate);
                turnStarted.TrySetResult(n);
                var text = await gate.Task.WaitAsync(token);
                s.Emit(new AcpTextDeltaEvent("assistant", text, $"msg-{n}"));
                return new AcpPromptResult { StopReason = "end_turn" };
            },
            parentItemId: a.Id);

        // Turn 1: Ordinary human prompt starts on spawned B.
        bVm.InputText = "Start background human research";
        var humanSend = bVm.SendPromptAsync();
        Assert.Equal(1, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));

        // While the human turn is active, A and D queue two tasks on B.
        var (taskAB1, threadAB1, _) = await DelegateAsync(host, a.Id, bItem.Id, "Nested work for A", ct);
        var (taskDB2, _, _) = await DelegateAsync(host, d.Id, bItem.Id, "Leaf work for D", ct);
        Assert.Equal(2, bVm.QueuedPrompts.Count);

        // During the human turn, B delegates unrelated human work to cHuman -> must NOT be owned by queued taskAB1.
        var (taskHuman, _, _) = await DelegateAsync(host, bItem.Id, cHuman.Id, "Human research subtask", ct);

        // End Turn 1 (human) -> Turn 2 (taskAB1) begins.
        turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult("Started human research");
        Assert.Equal(2, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));

        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB1).Status);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(d.Id).Single(t => t.TaskId == taskDB2).Status);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(cHuman.Id).Single(t => t.TaskId == taskHuman).Status);

        // During Turn 2 (taskAB1), B delegates taskBC1 to c1, then ends Turn 2 normally (waiting on c1).
        var (taskBC1, _, _) = await DelegateAsync(host, bItem.Id, c1.Id, "Subtask for A", ct);
        turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult("Delegated to c1; waiting");
        Assert.Equal(3, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));

        // taskAB1 must stay Running (not false success), while Turn 3 (taskDB2) runs independently as a normal leaf.
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB1).Status);
        Assert.Equal(CollabThreadStatus.Active, host.FindThread(threadAB1)!.Status);
        Assert.Empty(aBridge.Delivered);

        // End Turn 3 (taskDB2, leaf with no delegations) -> taskDB2 MUST complete implicitly without being blocked by taskBC1 or taskHuman.
        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult("Leaf result for D");
        await humanSend.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await WaitUntilAsync(() => host.GetTasksForItem(d.Id).Single(t => t.TaskId == taskDB2).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("Leaf result for D", host.GetTasksForItem(d.Id).Single(t => t.TaskId == taskDB2).ResultSummary);
        await WaitUntilAsync(() => dBridge.Delivered.Any(x => x.Prompt.Contains("Leaf result for D", StringComparison.Ordinal)), ct);

        // Cancelling taskAB1 on spawned-but-reused B cancels only its owned descendant taskBC1, leaving human work taskHuman untouched.
        var cancelRes = await host.CallToolAsync(a.Id, "cancel_task", new JsonObject { ["task_id"] = taskAB1, ["reason"] = "A aborted" }, ct);
        Assert.Null(cancelRes["isError"]);
        Assert.Equal(2, Body(cancelRes)["cancelled_count"]!.GetValue<int>());

        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(a.Id).Single(t => t.TaskId == taskAB1).Status);
        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(c1.Id).Single(t => t.TaskId == taskBC1).Status);
        await WaitUntilAsync(() => Volatile.Read(ref c1Bridge.TurnCancellations) == 1, ct);

        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(cHuman.Id).Single(t => t.TaskId == taskHuman).Status);
        Assert.Equal(0, Volatile.Read(ref cHumanBridge.TurnCancellations));
    }

    [AvaloniaFact, FreshApp]
    public async Task Manual_terminal_child_survives_cancelling_queued_collab_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, projects, project, dir) = await SetUpHostAsync("octopus-nested-pty-owner-");
        var (a, _) = AddStubAgent(host, projects, project, dir, "Caller A", "claude");
        var (c, cBridge) = AddStubAgent(host, projects, project, dir, "Human child C");
        var b = new ProjectItem
        {
            Kind = ProjectItemKind.Agent,
            AgentId = "claude",
            Surface = AgentSurface.Terminal,
            Title = "Shared manual CLI",
        };
        projects.AddItem(project, b, activate: false);

        var registry = AppHost.Get<Octopus.Terminal.Avalonia.TerminalSessionRegistry>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        var term = registry.GetOrStart(
            b.Id,
            new Octopus.Core.Terminals.LaunchSpec("mock", [], dir, new Dictionary<string, string>()),
            factory,
            new TKCode.Terminal.Core.TerminalSize(80, 24, 8, 16),
            Octopus.Terminal.Avalonia.TerminalPalettes.Dark,
            TKCode.Terminal.Core.ColorScheme.Dark,
            100,
            out _);
        try
        {
            term.Send("Human job, delegate legwork to C\r");
            await WaitUntilAsync(() => factory.Connections.Single().Written.Any(), ct);

            var queued = await DelegateAsync(host, a.Id, b.Id, "Unrelated queued collaboration task", ct);
            var human = await DelegateAsync(host, b.Id, c.Id, "Human's legwork", ct);

            var cancel = await host.CallToolAsync(a.Id, "cancel_task", new JsonObject { ["task_id"] = queued.TaskId }, ct);
            Assert.Null(cancel["isError"]);
            Assert.Equal(1, Body(cancel)["cancelled_count"]!.GetValue<int>());

            Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(a.Id).Single(t => t.TaskId == queued.TaskId).Status);
            Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(c.Id).Single(t => t.TaskId == human.TaskId).Status);
            Assert.Equal(0, Volatile.Read(ref cBridge.TurnCancellations));
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Closing_deferred_middle_does_not_leave_live_child_on_cancelled_thread()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, projects, project, dir) = await SetUpHostAsync("octopus-nested-close-");
        var (a, _) = AddStubAgent(host, projects, project, dir, "Caller A");
        var (c, cBridge) = AddStubAgent(host, projects, project, dir, "Leaf C");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (b, vm, _) = await AddRealGuiAgentAsync(
            projects,
            project,
            "Middle B",
            async (_, _, _, tok) =>
            {
                await release.Task.WaitAsync(tok);
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        var ab = await DelegateAsync(host, a.Id, b.Id, "Nested job", ct);
        await WaitUntilAsync(() => vm.IsTurnActive, ct);
        var bc = await DelegateAsync(host, b.Id, c.Id, "Leaf work", ct);
        release.TrySetResult();
        await WaitUntilAsync(() => !vm.IsTurnActive, ct);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(a.Id).Single(t => t.TaskId == ab.TaskId).Status);

        await AppHost.Get<ITabContentRegistry>().ReleaseAsync(b.Id);
        Frames.Pump();

        var parent = host.GetTasksForItem(a.Id).Single(t => t.TaskId == ab.TaskId);
        var leaf = host.GetTasksForItem(c.Id).Single(t => t.TaskId == bc.TaskId);
        var thread = host.FindThread(bc.ThreadId)!;

        Assert.Equal(CollabTaskStatus.Cancelled, parent.Status);
        Assert.False(thread.Status == CollabThreadStatus.Cancelled && !leaf.IsFinished);
        Assert.Equal(CollabTaskStatus.Cancelled, leaf.Status);
        Assert.Equal(CollabThreadStatus.Cancelled, thread.Status);
        await WaitUntilAsync(() => Volatile.Read(ref cBridge.TurnCancellations) == 1, ct);
    }

    [AvaloniaFact, FreshApp]
    public async Task Leaf_waiting_for_approval_keeps_middle_responsible()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, projects, project, dir) = await SetUpHostAsync("octopus-nested-apr-");
        var (a, root) = AddStubAgent(host, projects, project, dir, "root");
        var (b, _) = AddStubAgent(host, projects, project, dir, "middle", parentItemId: a.Id);
        var (c, _) = AddStubAgent(host, projects, project, dir, "leaf", parentItemId: b.Id);

        var ab = await DelegateAsync(host, a.Id, b.Id, "parent", ct);
        var bc = await DelegateAsync(host, b.Id, c.Id, "leaf", ct);
        host.ReportChildApproval(new CollabBubbledApproval("card", b.Id, c.Id, "codex", "leaf", "read", "file", null, _ => { }));
        host.NotifyTurnCompleted(b.Id, "Waiting on approval", ab.ThreadId, ab.TaskId);

        Assert.Equal(CollabTaskStatus.WaitingApproval, host.GetTasksForItem(c.Id).Single(t => t.TaskId == bc.TaskId).Status);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(a.Id).Single(t => t.TaskId == ab.TaskId).Status);
        Assert.Empty(root.Delivered);

        host.ClearChildApproval(c.Id, "card");
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(c.Id).Single(t => t.TaskId == bc.TaskId).Status);
    }
}
