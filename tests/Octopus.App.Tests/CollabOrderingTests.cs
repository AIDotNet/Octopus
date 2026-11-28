using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Octopus.Acp.Protocol;
using Octopus.Agents;
using Octopus.Agents.Signals;
using Octopus.App.AgentGui;
using Octopus.App.Collab;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Remote.Protocol;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

public sealed class CollabOrderingTests
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
        public List<(string Prompt, string? ThreadId, string? TaskId)> Delivered { get; } = [];
        public Func<string, string?, string?, CancellationToken, Task<bool>>? OnDeliver { get; set; }
        public int TurnCancellations;

        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Delivered.Add((promptText, threadId, taskId));
            }
            return OnDeliver is not null
                ? OnDeliver(promptText, threadId, taskId, cancellationToken)
                : Task.FromResult(true);
        }

        public string ReadRecentSummary(int maxTurns = 5) => "";

        public Task CancelActiveTurnAsync()
        {
            Interlocked.Increment(ref TurnCancellations);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRemoteHosts : ICollabRemoteHosts
    {
        public Dictionary<string, FakeRemoteHost> Hosts { get; } = new(StringComparer.Ordinal);
        public FakeRemoteHost Add(string hostId, string name) => Hosts[hostId] = new FakeRemoteHost(name);
        public Task<ICollabRemoteHost> ConnectAsync(string hostId, CancellationToken ct) =>
            Hosts.TryGetValue(hostId, out var host)
                ? Task.FromResult<ICollabRemoteHost>(host)
                : Task.FromException<ICollabRemoteHost>(new IOException("offline"));
        public string HostName(string hostId) => Hosts.TryGetValue(hostId, out var host) ? host.Name : hostId;
        public void RequestSync(string hostId) { }
    }

    private sealed class FakeRemoteHost(string name) : ICollabRemoteHost
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);
        private int _items;

        public string Name => name;
        public bool SupportsAcpGui => true;
        public Task Completion => _closed.Task;
        public event Action<AcpUpdateMessage>? AcpUpdated;
        public List<(string ItemId, string Text)> Prompts { get; } = [];
        public Dictionary<string, AcpSnapshotMessage> Snapshots { get; } = new(StringComparer.Ordinal);

        public Task<ItemMessage> CreateItemAsync(CreateItemMessage request, CancellationToken ct)
        {
            var id = $"remote-order-{++_items}";
            return Task.FromResult(new ItemMessage { Id = id, ProjectId = request.ProjectId, Kind = request.Kind, AgentId = request.AgentId, Title = request.Title ?? "" });
        }

        public Task<AcpSnapshotMessage> AttachConversationAsync(string itemId, CancellationToken ct)
        {
            var snapshot = Snapshots.TryGetValue(itemId, out var configured)
                ? configured
                : new AcpSnapshotMessage { ItemId = itemId, State = AcpStates.Ready };
            _sequences[itemId] = snapshot.Sequence;
            return Task.FromResult(snapshot);
        }

        public Task DetachConversationAsync(string itemId, CancellationToken ct) => Task.CompletedTask;

        public Task SendPromptAsync(string itemId, string text, CancellationToken ct)
        {
            Prompts.Add((itemId, text));
            return Task.CompletedTask;
        }

        public Task CancelTurnAsync(string itemId, CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(AcpRespondMessage answer, CancellationToken ct) => Task.CompletedTask;

        public void Push(string itemId, string? state, params AcpEntryMessage[] upserts)
        {
            var seq = _sequences[itemId] = _sequences.GetValueOrDefault(itemId) + 1;
            AcpUpdated?.Invoke(new AcpUpdateMessage { ItemId = itemId, Sequence = seq, State = state, Upserts = [.. upserts] });
        }
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

    private static async Task<(CollabCoordinator Host, Project Project, ProjectItem Parent, StubBridge ParentBridge, ProjectItem Worker, StubBridge WorkerBridge)> SetUpStubPairAsync(string prefix)
    {
        await Task.Yield();
        var dir = Directory.CreateTempSubdirectory(prefix).FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Lead", Surface = AgentSurface.Gui };
        var worker = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Worker", Surface = AgentSurface.Gui };
        projects.AddItem(project, parent);
        projects.AddItem(project, worker, activate: false);

        var host = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        host.Server.Start();
        var parentBridge = new StubBridge(parent, dir);
        var workerBridge = new StubBridge(worker, dir);
        host.RegisterGuiSession(parentBridge);
        host.RegisterGuiSession(workerBridge);
        return (host, project, parent, parentBridge, worker, workerBridge);
    }

    private static async Task<(CollabCoordinator Host, Project Project, ProjectItem Parent, StubBridge ParentBridge, ProjectItem WorkerItem, AgentGuiTabViewModel WorkerVm, FakeAcpSession WorkerSession)> SetUpRealGuiWorkerAsync(
        string prefix,
        Func<FakeAcpSession, string, IReadOnlyList<AcpPromptAttachment>?, CancellationToken, Task<AcpPromptResult>> promptHandler)
    {
        await Task.Yield();
        var dir = Directory.CreateTempSubdirectory(prefix).FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Lead", Surface = AgentSurface.Gui };
        var worker = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Worker", Surface = AgentSurface.Gui };
        projects.AddItem(project, parent);
        projects.AddItem(project, worker, activate: false);

        var host = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        host.Server.Start();
        var parentBridge = new StubBridge(parent, dir);
        host.RegisterGuiSession(parentBridge);

        FakeAcpSession? session = null;
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = s =>
        {
            session = s;
            s.PromptHandler = promptHandler;
        };

        var tabRegistry = AppHost.Get<ITabContentRegistry>();
        var control = tabRegistry.GetOrCreate(project, worker);
        var vm = Assert.IsType<AgentGuiTabViewModel>(control.DataContext);
        await vm.ConnectAsync();
        Frames.Pump();
        Assert.NotNull(session);
        return (host, project, parent, parentBridge, worker, vm, session!);
    }

    private static async Task<(string TaskId, string ThreadId)> DelegateToExistingAsync(
        CollabCoordinator host,
        string callerId,
        string targetId,
        string goal,
        CancellationToken ct)
    {
        var result = await host.CallToolAsync(
            callerId,
            "delegate_task",
            new JsonObject { ["target"] = targetId, ["goal"] = goal },
            ct);
        Assert.Null(result["isError"]);
        var body = Body(result);
        return (body["task_id"]!.GetValue<string>(), body["thread_id"]!.GetValue<string>());
    }

    private static Task<JsonObject> ReplyAsync(
        CollabCoordinator host,
        string workerId,
        string threadId,
        string message,
        CancellationToken ct,
        string status = "agreed") =>
        host.CallToolAsync(
            workerId,
            "reply_thread",
            new JsonObject { ["thread_id"] = threadId, ["message"] = message, ["status"] = status },
            ct);

    [AvaloniaFact, FreshApp]
    public async Task Two_delegations_to_one_worker_settle_their_implicit_reports_in_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, parent, _, worker, _) = await SetUpStubPairAsync("octopus-order-fifo-");

        var (taskA, _) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "Job A", ct);
        var (taskB, _) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "Job B", ct);

        host.NotifyTurnCompleted(worker.Id, "first job finished");
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status == CollabTaskStatus.Completed, ct);

        var aAfterFirst = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA);
        var bAfterFirst = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB);
        Assert.Equal("first job finished", aAfterFirst.ResultSummary);
        Assert.Equal(CollabTaskStatus.Running, bAfterFirst.Status);
        Assert.Null(bAfterFirst.ResultSummary);

        host.NotifyTurnCompleted(worker.Id, "second job finished");
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status == CollabTaskStatus.Completed, ct);

        Assert.Equal("first job finished", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);
        Assert.Equal("second job finished", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Explicit_reply_for_first_task_followed_by_ordinary_end_turn_never_settles_queued_second_task()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, parent, _, worker, _) = await SetUpStubPairAsync("octopus-order-explicit-");

        var (taskA, threadA) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "Job A", ct);
        var (taskB, _) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "Job B", ct);

        var explicitReply = await ReplyAsync(host, worker.Id, threadA, "explicit A", ct);
        Assert.Null(explicitReply["isError"]);
        Assert.Equal(CollabTaskStatus.Completed, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status);

        // Turn A now finishes normally after calling reply_thread: it must not blindly pop queued B.
        host.NotifyTurnCompleted(worker.Id, "ordinary wrapup for A");
        await Task.Delay(80, ct);

        var bAfterAEndTurn = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB);
        Assert.Equal(CollabTaskStatus.Running, bAfterAEndTurn.Status);
        Assert.Null(bAfterAEndTurn.ResultSummary);
        Assert.Equal("explicit A", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);

        // Turn B now runs and concludes implicitly.
        host.NotifyTurnCompleted(worker.Id, "implicit B");
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("implicit B", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Real_gui_worker_correlates_explicit_report_and_ordinary_end_turn_when_second_task_queued_before_or_after_reply()
    {
        var ct = TestContext.Current.CancellationToken;
        var turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string>? releaseCurrentTurn = null;
        var turnNumber = 0;

        var (host, _, parent, _, workerItem, workerVm, _) = await SetUpRealGuiWorkerAsync(
            "octopus-order-gui-explicit-",
            async (s, _, _, token) =>
            {
                var n = Interlocked.Increment(ref turnNumber);
                var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref releaseCurrentTurn, gate);
                turnStarted.TrySetResult(n);
                var replyText = await gate.Task.WaitAsync(token);
                s.Emit(new AcpTextDeltaEvent("assistant", replyText, $"msg-{n}"));
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        // Part 1: B is queued BEFORE A explicitly calls reply_thread, then A's turn finishes normally.
        var (taskA, threadA) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "GUI Job A", ct);
        Assert.Equal(1, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        var (taskB, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "GUI Job B", ct);
        Assert.Single(workerVm.QueuedPrompts);

        Assert.Null((await ReplyAsync(host, workerItem.Id, threadA, "explicit GUI A", ct))["isError"]);

        turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult("wrapup text for A");
        Assert.Equal(2, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));

        var bWhileRunning = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB);
        Assert.Equal(CollabTaskStatus.Running, bWhileRunning.Status);
        Assert.Null(bWhileRunning.ResultSummary);

        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult("implicit GUI B");
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("explicit GUI A", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);
        Assert.Equal("implicit GUI B", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).ResultSummary);

        // Part 2: C explicitly calls reply_thread, D is dispatched AFTER that reply while C's turn is still ending.
        await WaitUntilAsync(() => !workerVm.IsTurnActive, ct);
        turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (taskC, threadC) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "GUI Job C", ct);
        Assert.Equal(3, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));

        Assert.Null((await ReplyAsync(host, workerItem.Id, threadC, "explicit GUI C", ct))["isError"]);
        var (taskD, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "GUI Job D", ct);
        Assert.Single(workerVm.QueuedPrompts);

        turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult("wrapup text for C");
        Assert.Equal(4, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));

        var dWhileRunning = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskD);
        Assert.Equal(CollabTaskStatus.Running, dWhileRunning.Status);
        Assert.Null(dWhileRunning.ResultSummary);

        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult("implicit GUI D");
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskD).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("explicit GUI C", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskC).ResultSummary);
        Assert.Equal("implicit GUI D", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskD).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Real_gui_ordinary_user_prompt_and_queue_reorder_preserve_collab_correlation()
    {
        var ct = TestContext.Current.CancellationToken;
        var turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string>? releaseCurrentTurn = null;
        var turnNumber = 0;

        var (host, _, parent, _, workerItem, workerVm, _) = await SetUpRealGuiWorkerAsync(
            "octopus-order-gui-user-",
            async (s, _, _, token) =>
            {
                var n = Interlocked.Increment(ref turnNumber);
                var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref releaseCurrentTurn, gate);
                turnStarted.TrySetResult(n);
                var replyText = await gate.Task.WaitAsync(token);
                s.Emit(new AcpTextDeltaEvent("assistant", replyText, $"msg-{n}"));
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        // Ordinary user prompt starts first on the worker tab.
        workerVm.InputText = "Explain this folder";
        var userSendTask = workerVm.SendPromptAsync();
        Assert.Equal(1, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));

        // While the user prompt runs, two collab jobs queue behind it, then the user reorders B ahead of A.
        var (taskA, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Queued Job A", ct);
        var (taskB, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Queued Job B", ct);
        Assert.Equal(2, workerVm.QueuedPrompts.Count);
        workerVm.QueuedPrompts[1].MoveUpCommand.Execute(null);

        // Ordinary user turn ends: must NOT settle either queued collab job.
        turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult("Ordinary user explanation");
        Assert.Equal(2, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));

        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status);

        // Turn 2 is Job B (moved ahead of A): its completion must settle B, leaving A running.
        turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult("Result for B");
        Assert.Equal(3, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status == CollabTaskStatus.Completed, ct);

        Assert.Equal("Result for B", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).ResultSummary);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status);
        Assert.Null(host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);

        // Turn 3 is Job A: its completion settles A.
        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult("Result for A");
        await userSendTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("Result for A", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Cancelling_queued_second_task_leaves_active_first_task_running_and_completes_normally()
    {
        var ct = TestContext.Current.CancellationToken;
        var turnAStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTurnA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (host, _, parent, _, workerItem, workerVm, workerSession) = await SetUpRealGuiWorkerAsync(
            "octopus-order-cancel-queued-",
            async (s, _, _, token) =>
            {
                turnAStarted.TrySetResult();
                var text = await releaseTurnA.Task.WaitAsync(token);
                s.Emit(new AcpTextDeltaEvent("assistant", text, "msg-a"));
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        var (taskA, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Active Job A", ct);
        await turnAStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var (taskB, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Queued Job B", ct);
        Assert.Single(workerVm.QueuedPrompts);

        var cancelRes = await host.CallToolAsync(parent.Id, "cancel_task", new JsonObject { ["task_id"] = taskB, ["reason"] = "No longer needed" }, ct);
        Assert.Null(cancelRes["isError"]);
        Frames.Pump();

        Assert.Empty(workerVm.QueuedPrompts);
        Assert.Equal(0, workerSession.CancelCount);
        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status);

        releaseTurnA.TrySetResult("Job A finished cleanly");
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("Job A finished cleanly", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);
        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status);
        Assert.Single(workerSession.SentPrompts);
    }

    [AvaloniaFact, FreshApp]
    public async Task Refused_second_delegation_does_not_overwrite_or_corrupt_first_pending_task()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, parent, _, worker, workerBridge) = await SetUpStubPairAsync("octopus-order-refusal-");

        var (taskA, _) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "Accepted Job A", ct);

        workerBridge.OnDeliver = (_, _, _, _) => Task.FromResult(false);
        var refused = await host.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["goal"] = "Refused Job B" },
            ct);
        Assert.True(refused["isError"]?.GetValue<bool>());

        host.NotifyTurnCompleted(worker.Id, "Job A succeeded after B refused");
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("Job A succeeded after B refused", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Gui_error_on_first_task_does_not_abort_queued_second_task_and_parent_idle_keeps_work_running()
    {
        var ct = TestContext.Current.CancellationToken;
        var turn1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failTurn1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn2Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishTurn2 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn = 0;

        var (host, _, parent, _, workerItem, workerVm, _) = await SetUpRealGuiWorkerAsync(
            "octopus-order-gui-abort-",
            async (s, _, _, token) =>
            {
                var n = Interlocked.Increment(ref turn);
                if (n == 1)
                {
                    turn1Started.TrySetResult();
                    await failTurn1.Task.WaitAsync(token);
                    throw new InvalidOperationException("boom on A");
                }
                turn2Started.TrySetResult();
                var text = await finishTurn2.Task.WaitAsync(token);
                s.Emit(new AcpTextDeltaEvent("assistant", text, "msg-2"));
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        var (taskA, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Failing Job A", ct);
        await turn1Started.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var (taskB, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Queued Job B", ct);
        Assert.Single(workerVm.QueuedPrompts);

        // Ordinary parent idle keeps both tasks running.
        host.NotifyTurnCompleted(parent.Id, "Waiting for worker");
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status);

        failTurn1.TrySetResult();
        await turn2Started.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status == CollabTaskStatus.Failed, ct);

        Assert.Contains("boom on A", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary, StringComparison.Ordinal);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status);

        finishTurn2.TrySetResult("Job B recovered");
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("Job B recovered", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Follow_up_on_first_task_with_second_task_queued_settles_each_without_cross_talk()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, parent, _, worker, _) = await SetUpStubPairAsync("octopus-order-followup-");

        var (taskA, threadA) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "Job A", ct);
        host.NotifyTurnCompleted(worker.Id, "Need clarification on A");
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status == CollabTaskStatus.Completed, ct);

        // Reopen A with a follow-up, and queue a separate Job B behind it.
        var followUp = await host.CallToolAsync(
            parent.Id,
            "send_message",
            new JsonObject { ["target_session_id"] = worker.Id, ["thread_id"] = threadA, ["message"] = "Use SQLite for A" },
            ct);
        Assert.Null(followUp["isError"]);
        var (taskB, _) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "Job B", ct);

        host.NotifyTurnCompleted(worker.Id, "A done with SQLite");
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("A done with SQLite", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status);

        host.NotifyTurnCompleted(worker.Id, "B finished");
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("B finished", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Terminal_worker_keeps_two_queued_tasks_and_explicit_reply_in_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, project, parent, _, _, _) = await SetUpStubPairAsync("octopus-order-term-");
        var projects = AppHost.Get<IProjectService>();
        var cli = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "CLI Worker", Surface = AgentSurface.Terminal };
        projects.AddItem(project, cli, activate: false);

        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        AppHost.Get<Octopus.Terminal.Avalonia.TerminalSessionRegistry>().GetOrStart(
            cli.Id,
            AppHost.Get<IItemLaunchService>().Plan(project, cli),
            factory,
            new TKCode.Terminal.Core.TerminalSize(80, 24, 0, 0),
            Octopus.Terminal.Avalonia.TerminalPalettes.Dark,
            TKCode.Terminal.Core.ColorScheme.Dark,
            1000,
            out _);

        var (taskA, threadA) = await DelegateToExistingAsync(host, parent.Id, cli.Id, "Terminal Job A", ct);
        var (taskB, _) = await DelegateToExistingAsync(host, parent.Id, cli.Id, "Terminal Job B", ct);

        Assert.Null((await ReplyAsync(host, cli.Id, threadA, "explicit terminal A", ct))["isError"]);

        // Stop hook #1 belongs to turn A: it must not settle queued Job B.
        AppHost.Get<ISignalHub>().Publish(new AgentSignal("claude", "stop", cli.Id, "", 0, 0, "{}"));
        await Task.Delay(80, ct);

        Assert.Equal("explicit terminal A", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status);

        // Stop hook #2 belongs to turn B: it settles Job B.
        AppHost.Get<ISignalHub>().Publish(new AgentSignal("claude", "stop", cli.Id, "", 0, 0, "{}"));
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("explicit terminal A", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);
        Assert.NotNull(host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Remote_worker_correlates_two_tasks_and_ignores_unrelated_human_turn_on_shared_session()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-order-remote-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var local = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Lead", Surface = AgentSurface.Gui };
        projects.AddItem(local, parent);

        var remote = projects.AddProject(Directory.CreateTempSubdirectory("octopus-order-remote-p-").FullName);
        remote.RemoteHost = "host-1";
        remote.RemoteId = "rp-1";
        remote.Name = "Remote Repo";
        projects.SetCurrent(local.Id);

        var ops = new ProjectItem { Id = "ops-order-1", Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Ops" };
        projects.AddItem(remote, ops, activate: false);

        var fakes = new FakeRemoteHosts();
        var host = fakes.Add("host-1", "Build Server");
        host.Snapshots[ops.Id] = new AcpSnapshotMessage { ItemId = ops.Id, Sequence = 1, State = AcpStates.Ready };

        using var coordinator = new CollabCoordinator(
            projects,
            AppHost.Get<IAgentCatalog>(),
            AppHost.Get<IAgentScanner>(),
            AppHost.Get<ISettingsStore>(),
            new CollabMcpServer(),
            remoteHostsFactory: () => fakes);
        coordinator.RegisterGuiSession(new StubBridge(parent, dir));

        var (taskA, _) = await DelegateToExistingAsync(coordinator, parent.Id, ops.Id, "Remote Job A", ct);
        var (taskB, _) = await DelegateToExistingAsync(coordinator, parent.Id, ops.Id, "Remote Job B", ct);
        Assert.Equal(2, host.Prompts.Count);
        var promptA = host.Prompts[0].Text;
        var promptB = host.Prompts[1].Text;

        // Unrelated human turn on the shared remote tab must NOT settle either queued collab task.
        host.Push(
            ops.Id,
            AcpStates.Ready,
            new AcpEntryMessage { Id = "u-human", Kind = AcpEntryKinds.User, Text = "check disk space" },
            new AcpEntryMessage { Id = "a-human", Kind = AcpEntryKinds.Assistant, Text = "50% free", IsStreaming = false });
        await Task.Delay(80, ct);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status);

        // Coalesced update: turn A finished while turn B has already started streaming.
        host.Push(
            ops.Id,
            AcpStates.Working,
            new AcpEntryMessage { Id = "u-a", Kind = AcpEntryKinds.User, Text = promptA },
            new AcpEntryMessage { Id = "a-a", Kind = AcpEntryKinds.Assistant, Text = "remote A done", IsStreaming = false },
            new AcpEntryMessage { Id = "u-b", Kind = AcpEntryKinds.User, Text = promptB },
            new AcpEntryMessage { Id = "a-b", Kind = AcpEntryKinds.Assistant, Text = "working on B...", IsStreaming = true });
        await WaitUntilAsync(() => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("remote A done", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status);

        // Turn B finishes.
        host.Push(
            ops.Id,
            AcpStates.Ready,
            new AcpEntryMessage { Id = "a-b", Kind = AcpEntryKinds.Assistant, Text = "remote B done", IsStreaming = false });
        await WaitUntilAsync(() => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("remote B done", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Old_exact_callback_after_explicit_reply_and_reopened_follow_up_never_settles_new_round()
    {
        var ct = TestContext.Current.CancellationToken;

        // Part 1: Direct exact callback (4-arg) after explicit reply and accepted follow-up on the same thread/task.
        var (host, _, parent, _, worker, _) = await SetUpStubPairAsync("octopus-order-reopen-exact-");
        var (taskA, threadA) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "Round 1 goal", ct);
        Assert.Null((await ReplyAsync(host, worker.Id, threadA, "round 1 explicit", ct))["isError"]);

        var followUp = await host.CallToolAsync(
            parent.Id,
            "send_message",
            new JsonObject { ["target_session_id"] = worker.Id, ["thread_id"] = threadA, ["message"] = "Round 2 follow-up" },
            ct);
        Assert.Null(followUp["isError"]);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status);

        // Old turn's exact callback arrives after Round 2 was accepted: must NOT complete Round 2.
        host.NotifyTurnCompleted(worker.Id, "old turn wrap-up", threadA, taskA);
        await Task.Delay(80, ct);
        var reopened = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA);
        Assert.Equal(CollabTaskStatus.Running, reopened.Status);
        Assert.Equal("round 1 explicit", reopened.ResultSummary);

        // Round 2 finishes normally and settles the reopened task.
        host.NotifyTurnCompleted(worker.Id, "round 2 real result", threadA, taskA);
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("round 2 real result", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);

        // Part 2: Real headless GUI worker with explicit reply + follow-up while Turn 1 is still finishing.
        var turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string>? releaseCurrentTurn = null;
        var turnNumber = 0;

        var (guiHost, _, guiParent, _, guiWorker, guiVm, _) = await SetUpRealGuiWorkerAsync(
            "octopus-order-gui-reopen-",
            async (s, _, _, token) =>
            {
                var n = Interlocked.Increment(ref turnNumber);
                var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref releaseCurrentTurn, gate);
                turnStarted.TrySetResult(n);
                var replyText = await gate.Task.WaitAsync(token);
                s.Emit(new AcpTextDeltaEvent("assistant", replyText, $"reopen-msg-{n}"));
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        var (guiTask, guiThread) = await DelegateToExistingAsync(guiHost, guiParent.Id, guiWorker.Id, "GUI Round 1", ct);
        Assert.Equal(1, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));

        Assert.Null((await ReplyAsync(guiHost, guiWorker.Id, guiThread, "GUI round 1 explicit", ct))["isError"]);
        var guiFollowUp = await guiHost.CallToolAsync(
            guiParent.Id,
            "send_message",
            new JsonObject { ["target_session_id"] = guiWorker.Id, ["thread_id"] = guiThread, ["message"] = "GUI Round 2 follow-up" },
            ct);
        Assert.Null(guiFollowUp["isError"]);
        Assert.Single(guiVm.QueuedPrompts);

        turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult("old GUI turn wrap-up");
        Assert.Equal(2, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));

        var guiReopened = guiHost.GetTasksForItem(guiParent.Id).Single(t => t.TaskId == guiTask);
        Assert.Equal(CollabTaskStatus.Running, guiReopened.Status);
        Assert.Equal("GUI round 1 explicit", guiReopened.ResultSummary);

        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult("GUI round 2 result");
        await WaitUntilAsync(() => guiHost.GetTasksForItem(guiParent.Id).Single(t => t.TaskId == guiTask).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("GUI round 2 result", guiHost.GetTasksForItem(guiParent.Id).Single(t => t.TaskId == guiTask).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Legacy_end_turn_after_early_explicit_reply_never_consumes_later_queued_task()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, parent, _, worker, _) = await SetUpStubPairAsync("octopus-order-legacy-late-");

        var (taskA, threadA) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "Job A", ct);
        Assert.Null((await ReplyAsync(host, worker.Id, threadA, "explicit A", ct))["isError"]);

        // Job B arrives AFTER A replied explicitly, but BEFORE A's ordinary legacy end_turn fires.
        var (taskB, _) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "Job B", ct);

        host.NotifyTurnCompleted(worker.Id, "legacy wrap-up for A");
        await Task.Delay(80, ct);

        var bAfterWrapup = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB);
        Assert.Equal(CollabTaskStatus.Running, bAfterWrapup.Status);
        Assert.Null(bAfterWrapup.ResultSummary);
        Assert.Equal("explicit A", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);

        host.NotifyTurnCompleted(worker.Id, "implicit B");
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("implicit B", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Exact_callback_requires_paired_thread_and_task_ids_to_agree()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, parent, _, worker, _) = await SetUpStubPairAsync("octopus-order-pair-agree-");

        var (taskA, threadA) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "Job A", ct);
        var (taskB, threadB) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "Job B", ct);

        // Mismatched pairs must not partially match either A or B.
        host.NotifyTurnCompleted(worker.Id, "cross pair", threadA, taskB);
        host.NotifyTurnCompleted(worker.Id, "wrong task", threadA, "task_unknown");
        host.NotifyTurnCompleted(worker.Id, "wrong thread", "t_unknown", taskB);
        host.NotifyTurnAborted(worker.Id, "abort cross pair", failed: false, threadA, taskB);
        await Task.Delay(80, ct);

        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status);
        Assert.Null(host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);
        Assert.Null(host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).ResultSummary);

        // Matching pairs settle each task cleanly.
        host.NotifyTurnCompleted(worker.Id, "matched B", threadB, taskB);
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("matched B", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).ResultSummary);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status);

        host.NotifyTurnCompleted(worker.Id, "matched A", threadA, taskA);
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("matched A", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Real_gui_edit_and_resend_queued_collab_task_preserves_correlation_and_settles_on_completion()
    {
        var ct = TestContext.Current.CancellationToken;
        var turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string>? releaseCurrentTurn = null;
        var turnNumber = 0;

        var (host, _, parent, _, workerItem, workerVm, _) = await SetUpRealGuiWorkerAsync(
            "octopus-order-gui-edit-",
            async (s, _, _, token) =>
            {
                var n = Interlocked.Increment(ref turnNumber);
                var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref releaseCurrentTurn, gate);
                turnStarted.TrySetResult(n);
                var replyText = await gate.Task.WaitAsync(token);
                s.Emit(new AcpTextDeltaEvent("assistant", replyText, $"edit-msg-{n}"));
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        var (taskA, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Active Job A", ct);
        Assert.Equal(1, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        var (taskB, threadB) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Queued Job B", ct);
        var originalDeliveryId = Assert.Single(workerVm.QueuedPrompts).CollabDeliveryId;
        Assert.False(string.IsNullOrWhiteSpace(originalDeliveryId));

        // User edits queued Job B and sends it back into the queue.
        workerVm.QueuedPrompts[0].EditCommand.Execute(null);
        Assert.Empty(workerVm.QueuedPrompts);
        workerVm.InputText += "\nExtra guidance for B";
        await workerVm.SendPromptAsync();

        var requeuedB = Assert.Single(workerVm.QueuedPrompts);
        Assert.Equal(taskB, requeuedB.CollabTaskId);
        Assert.Equal(threadB, requeuedB.CollabThreadId);
        Assert.Equal(originalDeliveryId, requeuedB.CollabDeliveryId);
        Assert.True(requeuedB.ExpectCollabReply);

        turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult("Done A");
        Assert.Equal(2, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status == CollabTaskStatus.Completed, ct);

        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult("Done edited B");
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("Done edited B", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Real_gui_cancelling_reordered_active_task_cancels_active_acp_turn_and_runs_remaining_queued_task()
    {
        var ct = TestContext.Current.CancellationToken;
        var turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<AcpPromptResult>? releaseCurrentTurn = null;
        var turnNumber = 0;

        var (host, _, parent, _, workerItem, workerVm, workerSession) = await SetUpRealGuiWorkerAsync(
            "octopus-order-gui-reorder-cancel-",
            async (s, _, _, token) =>
            {
                var n = Interlocked.Increment(ref turnNumber);
                var gate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref releaseCurrentTurn, gate);
                turnStarted.TrySetResult(n);
                using var reg = token.Register(() => gate.TrySetCanceled(token));
                var res = await gate.Task;
                if (res.StopReason != "cancelled")
                {
                    s.Emit(new AcpTextDeltaEvent("assistant", $"turn-{n}-done", $"reorder-msg-{n}"));
                }
                return res;
            });

        workerSession.CancelHandler = _ =>
        {
            Volatile.Read(ref releaseCurrentTurn)?.TrySetResult(new AcpPromptResult { StopReason = "cancelled" });
            return Task.CompletedTask;
        };

        var (taskA, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Job A", ct);
        Assert.Equal(1, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        var (taskC, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Job C", ct);
        var (taskE, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Job E", ct);
        Assert.Equal(2, workerVm.QueuedPrompts.Count);

        // Reorder E ahead of C while A is running.
        workerVm.QueuedPrompts[1].MoveUpCommand.Execute(null);
        Assert.Equal(taskE, workerVm.QueuedPrompts[0].CollabTaskId);

        // Finish A -> E becomes the active turn while C remains queued.
        turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        Assert.Equal(2, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status == CollabTaskStatus.Completed, ct);

        // Cancel E while E is actively running (even though C was dispatched earlier in the ledger).
        turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelRes = await host.CallToolAsync(parent.Id, "cancel_task", new JsonObject { ["task_id"] = taskE, ["reason"] = "Cancel reordered E" }, ct);
        Assert.Null(cancelRes["isError"]);

        await WaitUntilAsync(() => workerSession.CancelCount >= 1, ct);
        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskE).Status);

        // Remaining queued C should start and complete cleanly.
        Assert.Equal(3, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskC).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("turn-3-done", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskC).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Real_gui_send_now_on_queued_collab_task_settles_interrupted_active_task_and_completes_promoted_task()
    {
        var ct = TestContext.Current.CancellationToken;
        var turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<(string StopReason, string? Reply)>? releaseCurrentTurn = null;
        var turnNumber = 0;

        var (host, _, parent, _, workerItem, workerVm, workerSession) = await SetUpRealGuiWorkerAsync(
            "octopus-order-gui-sendnow-",
            async (s, _, _, token) =>
            {
                var n = Interlocked.Increment(ref turnNumber);
                var gate = new TaskCompletionSource<(string StopReason, string? Reply)>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref releaseCurrentTurn, gate);
                turnStarted.TrySetResult(n);
                using var reg = token.Register(() => gate.TrySetCanceled(token));
                var (stopReason, reply) = await gate.Task;
                if (reply is not null)
                {
                    s.Emit(new AcpTextDeltaEvent("assistant", reply, $"sendnow-msg-{n}"));
                }
                return new AcpPromptResult { StopReason = stopReason };
            });

        workerSession.CancelHandler = _ =>
        {
            Volatile.Read(ref releaseCurrentTurn)?.TrySetResult(("cancelled", null));
            return Task.CompletedTask;
        };

        var (taskX, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Active Job X", ct);
        Assert.Equal(1, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        var (taskY, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Queued Job Y", ct);
        Assert.Single(workerVm.QueuedPrompts);

        // Click Send Now on queued Y while X is active.
        turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendNowTask = workerVm.QueuedPrompts[0].SendNowCommand.ExecuteAsync(null);
        Assert.Equal(2, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));

        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult(("end_turn", "Result for Y"));
        await sendNowTask.WaitAsync(TimeSpan.FromSeconds(5), ct);

        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskY).Status == CollabTaskStatus.Completed, ct);
        var xFinal = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskX);
        var yFinal = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskY);
        Assert.Equal(CollabTaskStatus.Completed, yFinal.Status);
        Assert.Equal("Result for Y", yFinal.ResultSummary);
        Assert.Equal(CollabTaskStatus.Cancelled, xFinal.Status);
        Assert.Empty(workerVm.QueuedPrompts);
    }

    [AvaloniaFact, FreshApp]
    public async Task Real_gui_new_session_settles_active_and_queued_collab_tasks()
    {
        var ct = TestContext.Current.CancellationToken;
        var turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<AcpPromptResult>? releaseCurrentTurn = null;
        var turnNumber = 0;

        var (host, _, parent, _, workerItem, workerVm, workerSession) = await SetUpRealGuiWorkerAsync(
            "octopus-order-gui-newsession-",
            async (_, _, _, token) =>
            {
                var n = Interlocked.Increment(ref turnNumber);
                var gate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref releaseCurrentTurn, gate);
                turnStarted.TrySetResult(n);
                using var reg = token.Register(() => gate.TrySetCanceled(token));
                return await gate.Task;
            });

        workerSession.CancelHandler = _ =>
        {
            Volatile.Read(ref releaseCurrentTurn)?.TrySetResult(new AcpPromptResult { StopReason = "cancelled" });
            return Task.CompletedTask;
        };

        var (taskX, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Active Job X", ct);
        Assert.Equal(1, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        var (taskY, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Queued Job Y", ct);
        var (taskZ, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Edited Draft Job Z", ct);
        Assert.Equal(2, workerVm.QueuedPrompts.Count);

        // Move Z into the composer as an edited draft before starting a new session.
        workerVm.QueuedPrompts[1].EditCommand.Execute(null);
        Assert.Single(workerVm.QueuedPrompts);

        await workerVm.NewSessionAsync();
        Frames.Pump();

        Assert.Empty(workerVm.QueuedPrompts);
        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskX).Status);
        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskY).Status);
        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskZ).Status);
    }

    [AvaloniaFact, FreshApp]
    public async Task Real_gui_reordered_active_task_cascades_owned_child_on_cancel_and_completes_remaining_queued_task()
    {
        var ct = TestContext.Current.CancellationToken;
        var turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<AcpPromptResult>? releaseCurrentTurn = null;
        var turnNumber = 0;

        var (host, project, parent, _, workerItem, workerVm, workerSession) = await SetUpRealGuiWorkerAsync(
            "octopus-order-gui-join-nested-",
            async (s, _, _, token) =>
            {
                var n = Interlocked.Increment(ref turnNumber);
                var gate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref releaseCurrentTurn, gate);
                turnStarted.TrySetResult(n);
                using var reg = token.Register(() => gate.TrySetCanceled(token));
                var res = await gate.Task;
                if (res.StopReason != "cancelled")
                {
                    s.Emit(new AcpTextDeltaEvent("assistant", $"join-turn-{n}-done", $"join-msg-{n}"));
                }
                return res;
            });

        workerSession.CancelHandler = _ =>
        {
            Volatile.Read(ref releaseCurrentTurn)?.TrySetResult(new AcpPromptResult { StopReason = "cancelled" });
            return Task.CompletedTask;
        };

        var projects = AppHost.Get<IProjectService>();
        var leaf = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Leaf", Surface = AgentSurface.Gui };
        projects.AddItem(project, leaf, activate: false);
        var leafBridge = new StubBridge(leaf, project.Path);
        host.RegisterGuiSession(leafBridge);

        var (taskA, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Job A", ct);
        Assert.Equal(1, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        var (taskC, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Job C", ct);
        var (taskE, _) = await DelegateToExistingAsync(host, parent.Id, workerItem.Id, "Job E", ct);
        Assert.Equal(2, workerVm.QueuedPrompts.Count);

        // Reorder E ahead of C and finish A so E becomes the active turn.
        workerVm.QueuedPrompts[1].MoveUpCommand.Execute(null);
        turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        Assert.Equal(2, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status == CollabTaskStatus.Completed, ct);

        // While reordered E is active on reused worker, E delegates an owned child task EC to leaf (BUG-07 ownership).
        var (taskEC, _) = await DelegateToExistingAsync(host, workerItem.Id, leaf.Id, "Owned child of E", ct);

        // Cancel E: must cancel E's active ACP turn AND cascade-cancel owned child EC, while remaining queued C still runs.
        turnStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelRes = await host.CallToolAsync(parent.Id, "cancel_task", new JsonObject { ["task_id"] = taskE, ["reason"] = "Cancel reordered E with child" }, ct);
        Assert.Null(cancelRes["isError"]);

        await WaitUntilAsync(() => workerSession.CancelCount >= 1, ct);
        await WaitUntilAsync(() => Volatile.Read(ref leafBridge.TurnCancellations) == 1, ct);
        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskE).Status);
        Assert.Equal(CollabTaskStatus.Cancelled, host.GetTasksForItem(leaf.Id).Single(t => t.TaskId == taskEC).Status);

        Assert.Equal(3, await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        Volatile.Read(ref releaseCurrentTurn)!.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskC).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("join-turn-3-done", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskC).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Cancelled_thread_stays_cancelled_after_deferred_exact_completion()
    {
        var ct = TestContext.Current.CancellationToken;
        var hold = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, parent, _, worker, vm, _) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-deferred-cancel-",
            (_, _, _, t) => hold.Task.WaitAsync(t));
        var (task, thread) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "first task", ct);
        var delivery = ((ICollabSessionBridge)vm).ActiveCollabDeliveryId;
        Assert.False(string.IsNullOrWhiteSpace(delivery));

        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host)!;
        lock (ledger.Gate)
        {
            host.NotifyTurnCompleted(worker.Id, "first wrap-up", thread, task, delivery);
            var cancel = host.CallToolAsync(parent.Id, "cancel_task", new JsonObject { ["task_id"] = task }, ct);
            Assert.True(cancel.IsCompletedSuccessfully);
            Assert.Null(cancel.Result["isError"]);
            Assert.Equal(CollabThreadStatus.Cancelled, host.FindThread(thread)!.Status);
        }

        await Task.Delay(180, ct);
        Assert.Equal(CollabThreadStatus.Cancelled, host.FindThread(thread)!.Status);
    }

    [AvaloniaFact, FreshApp]
    public async Task Deferred_completion_keeps_newly_accepted_round_running()
    {
        var ct = TestContext.Current.CancellationToken;
        var hold = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, parent, _, worker, vm, _) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-deferred-reopen-",
            (_, _, _, t) => hold.Task.WaitAsync(t));
        var (task, thread) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "first task", ct);
        var delivery = ((ICollabSessionBridge)vm).ActiveCollabDeliveryId;
        Assert.False(string.IsNullOrWhiteSpace(delivery));

        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host)!;
        lock (ledger.Gate)
        {
            host.NotifyTurnCompleted(worker.Id, "first wrap-up", thread, task, delivery);
            var reply = ReplyAsync(host, worker.Id, thread, "first explicit", ct);
            Assert.True(reply.IsCompletedSuccessfully);
            Assert.Null(reply.Result["isError"]);
            var follow = host.CallToolAsync(
                parent.Id,
                "send_message",
                new JsonObject
                {
                    ["target_session_id"] = worker.Id,
                    ["thread_id"] = thread,
                    ["message"] = "second round",
                },
                ct);
            Assert.True(follow.IsCompletedSuccessfully);
            Assert.Null(follow.Result["isError"]);
            Assert.NotEqual(delivery, Assert.Single(vm.QueuedPrompts).CollabDeliveryId);
            Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == task).Status);
        }

        await Task.Delay(180, ct);
        var final = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == task);
        Assert.True(final.Status == CollabTaskStatus.Running, $"status={final.Status}, summary={final.ResultSummary}");
    }

    [AvaloniaFact, FreshApp]
    public async Task Closed_process_edited_draft_cannot_execute_as_failed_job_on_reconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var hold = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, parent, _, worker, vm, old) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-closed-draft-",
            (_, _, _, t) => hold.Task.WaitAsync(t));
        await DelegateToExistingAsync(host, parent.Id, worker.Id, "first", ct);
        var (task, _) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "second", ct);
        var failedDelivery = Assert.Single(vm.QueuedPrompts).CollabDeliveryId;
        Assert.False(string.IsNullOrWhiteSpace(failedDelivery));

        vm.EditQueuedPrompt(Assert.Single(vm.QueuedPrompts));
        old.Emit(new AcpClosedEvent(new IOException("ended")));
        Assert.Equal(CollabTaskStatus.Failed, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == task).Status);
        hold.TrySetException(new IOException("ended"));
        await WaitUntilAsync(() => !vm.IsTurnActive, ct);

        var factory = AppHost.Get<FakeAcpSessionFactory>();
        string? observedDelivery = null;
        factory.ConfigureNext = s => s.PromptHandler = (_, _, _, _) =>
        {
            observedDelivery = ((ICollabSessionBridge)vm).ActiveCollabDeliveryId;
            return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
        };

        await vm.ReconnectAsync();
        await vm.SendPromptAsync();
        var fresh = factory.Created.Last();
        Assert.False(
            fresh.SentPrompts.Any(p => p.Text.Contains("second", StringComparison.Ordinal)) && observedDelivery == failedDelivery,
            $"failed delivery={failedDelivery}, sent delivery={observedDelivery}");
    }

    [AvaloniaFact, FreshApp]
    public async Task Informational_interjection_cannot_swallow_completed_worker_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var hold = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, parent, _, worker, vm, acp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-interjection-",
            (_, _, _, t) => hold.Task.WaitAsync(t));
        var (_, thread) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "produce result", ct);
        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host)!;

        lock (ledger.Gate)
        {
            acp.Emit(new AcpTextDeltaEvent("assistant", "delivered final result", "msg-real-completion"));
            hold.SetResult(new AcpPromptResult { StopReason = "end_turn" });
            Assert.True(Frames.Until(() => !vm.IsTurnActive));
            Assert.False(ledger.Pending.ContainsKey(worker.Id));
            var interject = host.InterjectInThreadAsync(thread, "FYI from operator", ct: ct);
            Assert.True(interject.IsCompletedSuccessfully && interject.Result);
            Assert.False(ledger.Pending.ContainsKey(worker.Id));
        }

        await WaitUntilAsync(() => acp.SentPrompts.Count == 2 && !vm.IsTurnActive, ct);
        // Give the independently scheduled report ample time; both actual provider turns are finished.
        for (var i = 0; i < 60 && !host.GetTasksForItem(parent.Id).Single().IsFinished; i++)
        {
            await Task.Delay(25, ct);
        }

        var state = host.GetTasksForItem(parent.Id).Single();
        Assert.True(
            state.Status == CollabTaskStatus.Completed && state.ResultSummary == "delivered final result",
            $"two GUI turns ended; pending={ledger.Pending.ContainsKey(worker.Id)}, task={state.Status}, summary={state.ResultSummary}");
    }

    [AvaloniaFact, FreshApp]
    public async Task External_thread_panel_interjection_and_informational_send_preserve_result_while_genuine_explicit_settlement_wins()
    {
        var ct = TestContext.Current.CancellationToken;
        var hold = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, project, parent, _, worker, vm, acp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-panel-matrix-",
            (_, _, _, t) => hold.Task.WaitAsync(t));

        var unrelatedWorker = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Unrelated", Surface = AgentSurface.Gui };
        AppHost.Get<IProjectService>().AddItem(project, unrelatedWorker, activate: false);
        host.RegisterGuiSession(new StubBridge(unrelatedWorker, project.Path));
        var (unrelatedTask, _) = await DelegateToExistingAsync(host, parent.Id, unrelatedWorker.Id, "unrelated active work", ct);

        var (task, thread) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "produce panel result", ct);
        var panel = new CollabThreadPanelViewModel(
            host.FindThread(thread)!,
            (tid, text, extend) => host.InterjectInThreadAsync(tid, text, extend, ct),
            () => { });
        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host)!;

        // 1. Thread panel interjection (with budget extension) + informational send_message (expect_reply: false)
        // must not invalidate the legitimately completed worker result or affect unrelated work.
        lock (ledger.Gate)
        {
            acp.Emit(new AcpTextDeltaEvent("assistant", "panel completed result", "msg-panel-result"));
            hold.SetResult(new AcpPromptResult { StopReason = "end_turn" });
            Assert.True(Frames.Until(() => !vm.IsTurnActive));

            panel.InterjectText = "FYI via thread panel";
            Assert.True(panel.SendInterjectCommand.ExecuteAsync(null).IsCompletedSuccessfully);
            Assert.True(panel.ExtendBudgetCommand.ExecuteAsync(null).IsCompletedSuccessfully);

            var fyi = host.CallToolAsync(
                parent.Id,
                "send_message",
                new JsonObject
                {
                    ["target_session_id"] = worker.Id,
                    ["thread_id"] = thread,
                    ["message"] = "FYI no reply needed",
                    ["expect_reply"] = false,
                },
                ct);
            Assert.True(fyi.IsCompletedSuccessfully);
            Assert.Null(fyi.Result["isError"]);
        }

        await WaitUntilAsync(() => acp.SentPrompts.Count == 4 && !vm.IsTurnActive, ct);
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == task).IsFinished, ct);
        var settled = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == task);
        Assert.Equal(CollabTaskStatus.Completed, settled.Status);
        Assert.Equal("panel completed result", settled.ResultSummary);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == unrelatedTask).Status);

        // 2. Genuine explicit settlement (reply_thread on a task-less thread) must still prevent a delayed implicit fallback from duplicating a reply.
        var sendNoTask = await host.CallToolAsync(
            parent.Id,
            "send_message",
            new JsonObject { ["target_session_id"] = unrelatedWorker.Id, ["message"] = "task-less negotiation", ["expect_reply"] = true },
            ct);
        Assert.Null(sendNoTask["isError"]);
        var noTaskThread = Body(sendNoTask)["thread_id"]!.GetValue<string>();
        lock (ledger.Gate)
        {
            host.NotifyTurnCompleted(unrelatedWorker.Id, "stale implicit fallback", noTaskThread, taskId: null);
            var explicitContinue = ReplyAsync(host, unrelatedWorker.Id, noTaskThread, "explicit settlement", ct, status: "continue");
            Assert.True(explicitContinue.IsCompletedSuccessfully);
            Assert.Null(explicitContinue.Result["isError"]);
        }

        await Task.Delay(180, ct);
        var noTaskRecord = host.FindThread(noTaskThread)!;
        Assert.Equal(2, noTaskRecord.Messages.Count);
        Assert.Equal("explicit settlement", noTaskRecord.Messages[^1].Text);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == unrelatedTask).Status);
    }

    [AvaloniaFact, FreshApp]
    public async Task Manual_edited_draft_preserves_user_content_and_remains_unowned_while_later_job_waits()
    {
        var ct = TestContext.Current.CancellationToken;
        var hold = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, parent, _, worker, vm, old) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-draft-control-",
            (_, _, _, t) => hold.Task.WaitAsync(t));
        await DelegateToExistingAsync(host, parent.Id, worker.Id, "blocking", ct);
        var (failed, _) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "editable", ct);
        vm.EditQueuedPrompt(Assert.Single(vm.QueuedPrompts));
        vm.InputText = "my preserved manual draft";
        old.Emit(new AcpClosedEvent(new IOException("ended")));
        hold.SetException(new IOException("ended"));
        await WaitUntilAsync(() => !vm.IsTurnActive, ct);

        var manual = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = s => s.PromptHandler = (_, text, _, token) => (text == "my preserved manual draft" ? manual.Task : next.Task).WaitAsync(token);

        await vm.ReconnectAsync();
        Assert.Equal("my preserved manual draft", vm.InputText);
        var send = vm.SendPromptAsync();
        var bridge = (ICollabSessionBridge)vm;
        Assert.True(vm.IsTurnActive);
        Assert.Null(bridge.ActiveCollabThreadId);
        Assert.Null(bridge.ActiveCollabTaskId);
        Assert.Null(bridge.ActiveCollabDeliveryId);
        Assert.Equal("my preserved manual draft", Assert.Single(factory.Created.Last().SentPrompts).Text);

        var (later, _) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "later active job", ct);
        manual.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => bridge.ActiveCollabTaskId == later, ct);
        Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == later).Status);
        next.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await send;
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == later).IsFinished, ct);
        Assert.Equal(CollabTaskStatus.Failed, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == failed).Status);
        Assert.Equal(CollabTaskStatus.Completed, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == later).Status);
    }

    private sealed class BeforeAckBridge(ICollabSessionBridge gui, Func<string, Task> afterAccept) : ICollabSessionBridge
    {
        public string ItemId => gui.ItemId;
        public string AgentId => gui.AgentId;
        public string DisplayTitle => gui.DisplayTitle;
        public string WorkingDirectory => gui.WorkingDirectory;
        public bool IsBusy => gui.IsBusy;
        public bool IsConnected => gui.IsConnected;
        public string? CurrentStatusDetail => gui.CurrentStatusDetail;
        public bool TracksActiveTurn => true;
        public bool SupportsExactDeliveryWithdrawal => true;
        public string ReadRecentSummary(int maxTurns = 5) => gui.ReadRecentSummary(maxTurns);
        public Task<bool> DeliverCollabPromptAsync(string p, string? t = null, string? a = null, CancellationToken cancellationToken = default) =>
            gui.DeliverCollabPromptAsync(p, t, a, cancellationToken);
        public async Task<bool> DeliverCollabPromptAsync(string p, string? t, string? a, bool e, string? id, Func<bool>? guard, CancellationToken ct)
        {
            var accepted = await gui.DeliverCollabPromptAsync(p, t, a, e, id, guard, ct);
            if (accepted) await afterAccept(p).ConfigureAwait(false);
            return accepted;
        }
    }

    private static Task<JsonObject> SendOnThreadAsync(CollabCoordinator host, string from, string to, string thread, bool expectReply, CancellationToken ct, string message = "second message") =>
        host.CallToolAsync(from, "send_message", new JsonObject
        {
            ["thread_id"] = thread,
            ["target_session_id"] = to,
            ["message"] = message,
            ["expect_reply"] = expectReply,
        }, ct);

    [AvaloniaFact, FreshApp]
    public async Task Fast_own_completion_before_bridge_acknowledgement_commits_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, caller, _, worker, vm, acp) = await SetUpRealGuiWorkerAsync("octopus-ordering-fast-own-", (s, _, _, _) =>
        {
            s.Emit(new AcpTextDeltaEvent("assistant", "own fast result", "own"));
            return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
        });
        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host)!;

        host.RegisterGuiSession(new BeforeAckBridge(vm, _ =>
        {
            Assert.True(Frames.Until(() => !vm.IsTurnActive));
            return Task.CompletedTask;
        }));

        lock (ledger.Gate)
        {
            var call = DelegateToExistingAsync(host, caller.Id, worker.Id, "fast task", ct);
            Assert.True(call.IsCompletedSuccessfully);
            Assert.False(vm.IsTurnActive);
            Assert.Single(acp.SentPrompts);
            Assert.False(ledger.Pending.ContainsKey(worker.Id));
        }

        await WaitUntilAsync(() => host.GetTasksForItem(caller.Id).Single().IsFinished, ct);
        var state = Assert.Single(host.GetTasksForItem(caller.Id));
        Assert.True(state.Status == CollabTaskStatus.Completed && state.ResultSummary == "own fast result", $"status={state.Status}, summary={state.ResultSummary}");
    }

    [AvaloniaFact, FreshApp]
    public async Task Late_acknowledgement_from_earlier_delivery_cannot_invalidate_newer_accepted_completion()
    {
        var ct = TestContext.Current.CancellationToken;
        var releaseSecond = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, caller, _, worker, vm, acp) = await SetUpRealGuiWorkerAsync("octopus-ordering-late-ack-", (s, text, _, _) =>
        {
            if (text.Contains("second message", StringComparison.Ordinal)) return releaseSecond.Task;
            s.Emit(new AcpTextDeltaEvent("assistant", "first committed", "msg-first"));
            return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
        });
        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host)!;

        var locked = false;
        host.RegisterGuiSession(new BeforeAckBridge(vm, text =>
        {
            if (!text.Contains("first job", StringComparison.Ordinal)) return Task.CompletedTask;
            Assert.True(Frames.Until(() => !vm.IsTurnActive && host.GetTasksForItem(caller.Id).Single().IsFinished));
            var task = Assert.Single(host.GetTasksForItem(caller.Id));
            Assert.Equal("first committed", task.ResultSummary);

            var b = SendOnThreadAsync(host, caller.Id, worker.Id, task.ThreadId!, expectReply: true, ct);
            Assert.True(b.IsCompletedSuccessfully);
            Assert.Null(b.Result["isError"]);

            Monitor.Enter(ledger.Gate, ref locked);
            acp.Emit(new AcpTextDeltaEvent("assistant", "new legitimate answer", "msg-second"));
            releaseSecond.SetResult(new AcpPromptResult { StopReason = "end_turn" });
            Assert.True(Frames.Until(() => !vm.IsTurnActive));
            Assert.False(ledger.Pending.ContainsKey(worker.Id));
            return Task.CompletedTask;
        }));

        try
        {
            var a = DelegateToExistingAsync(host, caller.Id, worker.Id, "first job", ct);
            Assert.True(a.IsCompletedSuccessfully);
        }
        finally
        {
            if (locked) Monitor.Exit(ledger.Gate);
        }

        await WaitUntilAsync(() => host.GetTasksForItem(caller.Id).Single().IsFinished, ct);
        var finished = Assert.Single(host.GetTasksForItem(caller.Id));
        Assert.True(finished.Status == CollabTaskStatus.Completed && finished.ResultSummary == "new legitimate answer", $"status={finished.Status}, summary={finished.ResultSummary}");
    }

    [AvaloniaFact, FreshApp]
    public async Task Accepted_same_thread_successor_wins_over_earlier_completion()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, caller, _, worker, vm, acp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-old-finish-",
            (_, text, _, _) => text.Contains("second message", StringComparison.Ordinal) ? second.Task : first.Task);
        var (_, thread) = await DelegateToExistingAsync(host, caller.Id, worker.Id, "first job", ct);
        Assert.Null((await SendOnThreadAsync(host, caller.Id, worker.Id, thread, expectReply: true, ct))["isError"]);
        Assert.Single(vm.QueuedPrompts);

        acp.Emit(new AcpTextDeltaEvent("assistant", "earlier answer", "old"));
        first.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => acp.SentPrompts.Count == 2, ct);
        await Task.Delay(200, ct);
        var midway = Assert.Single(host.GetTasksForItem(caller.Id)).Status;

        acp.Emit(new AcpTextDeltaEvent("assistant", "successor answer", "new"));
        second.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => !vm.IsTurnActive, ct);
        await WaitUntilAsync(() => host.GetTasksForItem(caller.Id).Single().IsFinished, ct);
        var state = Assert.Single(host.GetTasksForItem(caller.Id));
        Assert.True(midway == CollabTaskStatus.Running && state.ResultSummary == "successor answer", $"midway={midway}, final={state.Status}, result={state.ResultSummary}");
    }

    [AvaloniaFact, FreshApp]
    public async Task Promoting_same_thread_successor_via_send_now_survives_earlier_cancel()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, caller, _, worker, vm, acp) = await SetUpRealGuiWorkerAsync("octopus-ordering-old-abort-", (s, text, _, _) =>
        {
            if (!text.Contains("second message", StringComparison.Ordinal)) return first.Task;
            s.Emit(new AcpTextDeltaEvent("assistant", "successor answer", "successor"));
            return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
        });
        acp.CancelHandler = _ =>
        {
            first.TrySetResult(new AcpPromptResult { StopReason = "cancelled" });
            return Task.CompletedTask;
        };
        var (_, thread) = await DelegateToExistingAsync(host, caller.Id, worker.Id, "first job", ct);
        Assert.Null((await SendOnThreadAsync(host, caller.Id, worker.Id, thread, expectReply: true, ct))["isError"]);
        await vm.SendQueuedPromptNowAsync(Assert.Single(vm.QueuedPrompts));
        await WaitUntilAsync(() => acp.SentPrompts.Count == 2 && !vm.IsTurnActive, ct);
        await WaitUntilAsync(() => host.GetTasksForItem(caller.Id).Single().IsFinished, ct);
        var state = Assert.Single(host.GetTasksForItem(caller.Id));
        Assert.True(
            acp.SentPrompts.Count == 2 && state.Status == CollabTaskStatus.Completed && state.ResultSummary == "successor answer",
            $"sent={acp.SentPrompts.Count}, cancel={acp.CancelCount}, status={state.Status}, result={state.ResultSummary}");
    }

    [AvaloniaFact, FreshApp]
    public async Task One_way_send_after_committed_result_keeps_task_completed()
    {
        var ct = TestContext.Current.CancellationToken;
        var hold = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, caller, _, worker, vm, acp) = await SetUpRealGuiWorkerAsync("octopus-ordering-fyi-after-", (_, _, _, _) => hold.Task);
        var (_, thread) = await DelegateToExistingAsync(host, caller.Id, worker.Id, "first job", ct);
        acp.Emit(new AcpTextDeltaEvent("assistant", "final answer", "first"));
        hold.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => host.GetTasksForItem(caller.Id).Single().IsFinished, ct);

        Assert.Null((await SendOnThreadAsync(host, caller.Id, worker.Id, thread, expectReply: false, ct))["isError"]);
        await WaitUntilAsync(() => acp.SentPrompts.Count == 2 && !vm.IsTurnActive, ct);
        var state = Assert.Single(host.GetTasksForItem(caller.Id));
        Assert.True(state.Status == CollabTaskStatus.Completed && state.ResultSummary == "final answer", $"status={state.Status}, summary={state.ResultSummary}");
    }

    [AvaloniaFact, FreshApp]
    public async Task Explicit_reply_on_different_thread_preserves_active_turn_expectation()
    {
        var ct = TestContext.Current.CancellationToken;
        var hold = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, parent, _, worker, vm, session) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-cross-thread-explicit-",
            (_, _, _, token) => hold.Task.WaitAsync(token));
        var (first, _) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "finish current job", ct);
        var (second, otherThread) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "queued second job", ct);
        Assert.True(vm.IsTurnActive);
        Assert.Single(vm.QueuedPrompts);

        Assert.Null((await ReplyAsync(host, worker.Id, otherThread, "explicit second answer", ct))["isError"]);
        session.Emit(new AcpTextDeltaEvent("assistant", "first answer", "first-id"));
        hold.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => !vm.IsTurnActive, ct);
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).All(t => t.IsFinished), ct);

        var tasks = host.GetTasksForItem(parent.Id);
        var a = tasks.Single(t => t.TaskId == first);
        var b = tasks.Single(t => t.TaskId == second);
        Assert.True(
            a.Status == CollabTaskStatus.Completed && a.ResultSummary == "first answer" && b.ResultSummary == "explicit second answer",
            $"active={a.Status}/{a.ResultSummary}, explicit={b.Status}/{b.ResultSummary}, providerPrompts={session.SentPrompts.Count}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Captured_process_exit_preserves_replacement_owner_for_same_and_distinct_threads(bool sameThread)
    {
        var ct = TestContext.Current.CancellationToken;
        var oldTurn = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newTurn = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, project, parent, _, worker, first, old) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-captured-exit-",
            (_, _, _, token) => oldTurn.Task.WaitAsync(token));
        var (original, thread) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "first job", ct);
        var (oldOnly, _) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "old queue only", ct);

        var registry = AppHost.Get<ITabContentRegistry>();
        var captured = registry.CaptureForRelease(worker.Id)!;
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = session => session.PromptHandler = (_, _, _, token) => newTurn.Task.WaitAsync(token);
        var replacement = Assert.IsType<AgentGuiTabViewModel>(Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(project, worker)).DataContext);
        Assert.NotSame(first, replacement);
        await replacement.ConnectAsync();
        var fresh = factory.Created.Last();

        var newTask = original;
        if (sameThread)
        {
            Assert.Null((await SendOnThreadAsync(host, parent.Id, worker.Id, thread, expectReply: true, ct))["isError"]);
        }
        else
        {
            (newTask, _) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "different replacement task", ct);
        }

        Assert.Single(fresh.SentPrompts);
        Assert.NotEqual(((ICollabSessionBridge)first).ActiveCollabDeliveryId, ((ICollabSessionBridge)replacement).ActiveCollabDeliveryId);

        old.IsConnected = false;
        old.Emit(new AcpClosedEvent(new IOException("captured old process ended")));
        var mid = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == newTask).Status;
        var oldOnlyStatus = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == oldOnly).Status;
        oldTurn.TrySetException(new IOException("captured old process ended"));
        await captured();

        fresh.Emit(new AcpTextDeltaEvent("assistant", "replacement result", "new-result"));
        newTurn.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => !replacement.IsTurnActive, ct);
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == newTask).IsFinished, ct);

        var state = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == newTask);
        Assert.True(
            oldOnlyStatus == CollabTaskStatus.Failed && mid == CollabTaskStatus.Running && state.Status == CollabTaskStatus.Completed && state.ResultSummary == "replacement result",
            $"oldOnly={oldOnlyStatus}, replacementAfterOldExit={mid}, final={state.Status}, summary={state.ResultSummary}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Released_captured_gui_settles_its_abandoned_queue_without_touching_replacement(bool reopened)
    {
        var ct = TestContext.Current.CancellationToken;
        var block = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementResult = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, project, parent, _, item, old, acp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-release-queue-",
            (_, _, _, token) => block.Task.WaitAsync(token));
        old.InputText = "manual turn";
        _ = old.SendPromptAsync();
        var (oldTask, oldThread) = await DelegateToExistingAsync(host, parent.Id, item.Id, "old queued request", ct);
        Assert.Single(old.QueuedPrompts);

        var tabs = AppHost.Get<ITabContentRegistry>();
        var release = tabs.CaptureForRelease(item.Id)!;
        string? freshTask = null;
        FakeAcpSession? newAcp = null;
        if (reopened)
        {
            var factory = AppHost.Get<FakeAcpSessionFactory>();
            factory.ConfigureNext = s => s.PromptHandler = (_, _, _, token) => replacementResult.Task.WaitAsync(token);
            var fresh = Assert.IsType<AgentGuiTabViewModel>(Assert.IsType<AgentGuiTabView>(tabs.GetOrCreate(project, item)).DataContext);
            await fresh.ConnectAsync();
            newAcp = factory.Created.Last();
            (freshTask, _) = await DelegateToExistingAsync(host, parent.Id, item.Id, "fresh independent work", ct);
            Assert.Single(newAcp.SentPrompts);
        }

        await release();
        Assert.Empty(old.QueuedPrompts);
        if (reopened)
        {
            Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(t => t.TaskId == freshTask).Status);
            newAcp!.Emit(new AcpTextDeltaEvent("assistant", "new result", "new"));
            replacementResult.SetResult(new AcpPromptResult { StopReason = "end_turn" });
            await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(t => t.TaskId == freshTask).IsFinished, ct);
            Assert.Equal("new result", host.GetTasksForItem(parent.Id).Single(t => t.TaskId == freshTask).ResultSummary);
        }

        var abandoned = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == oldTask);
        Assert.True(
            abandoned.IsFinished,
            $"replacement={reopened}, releasedQueue={old.QueuedPrompts.Count}, oldProviderPrompts={acp.SentPrompts.Count}, oldTask={abandoned.Status}, thread={host.FindThread(oldThread)?.Status}");
    }

    [AvaloniaFact, FreshApp]
    public async Task Cancelled_followup_before_gui_acceptance_preserves_completed_task()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, parent, _, worker, vm, acp) = await SetUpRealGuiWorkerAsync("octopus-ordering-cancel-reopen-", (s, _, _, _) =>
        {
            s.Emit(new AcpTextDeltaEvent("assistant", "first finished", "first"));
            return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
        });
        var (_, thread) = await DelegateToExistingAsync(host, parent.Id, worker.Id, "first job", ct);
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single().IsFinished, ct);
        Assert.Equal("first finished", host.GetTasksForItem(parent.Id).Single().ResultSummary);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var follow = Task.Run(() => SendOnThreadAsync(host, parent.Id, worker.Id, thread, expectReply: true, cancel.Token));
        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host)!;
        Assert.True(SpinWait.SpinUntil(() => { lock (ledger.Gate) return ledger.Pending.ContainsKey(worker.Id); }, TimeSpan.FromSeconds(3)));

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follow);
        Frames.Pump();
        Assert.Single(acp.SentPrompts);
        Assert.Empty(vm.QueuedPrompts);

        var state = Assert.Single(host.GetTasksForItem(parent.Id));
        Assert.True(
            state.Status == CollabTaskStatus.Completed && state.ResultSummary == "first finished",
            $"status={state.Status}, result={state.ResultSummary}, prompts={acp.SentPrompts.Count}, queue={vm.QueuedPrompts.Count}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(true, "agreed")]
    [InlineData(true, "blocked")]
    [InlineData(false, "blocked")]
    public async Task Followup_explicit_reply_before_legacy_bridge_receipt_updates_task_result(bool early, string status)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, parent, _, item, bridge) = await SetUpStubPairAsync("octopus-ordering-early-followup-");
        var (task, thread) = await DelegateToExistingAsync(host, parent.Id, item.Id, "original job", ct);
        host.NotifyTurnCompleted(item.Id, "original result");
        await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single().IsFinished, ct);
        Assert.Equal("original result", host.GetTasksForItem(parent.Id).Single().ResultSummary);

        if (early)
        {
            bridge.OnDeliver = async (_, tid, _, token) =>
            {
                Assert.Null((await ReplyAsync(host, item.Id, tid!, "followup answer", token, status))["isError"]);
                return true;
            };
        }

        Assert.Null((await SendOnThreadAsync(host, parent.Id, item.Id, thread, expectReply: true, ct))["isError"]);
        if (!early)
        {
            Assert.Null((await ReplyAsync(host, item.Id, thread, "followup answer", ct, status))["isError"]);
        }

        var state = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == task);
        var latest = host.FindThread(thread)!.Messages.Last();
        Assert.Equal("followup answer", latest.Text);
        var expected = status == "agreed" ? CollabTaskStatus.Completed : CollabTaskStatus.Failed;
        Assert.True(
            state.Status == expected && state.ResultSummary == latest.Text,
            $"replyBeforeReceipt={early}, thread={latest.Status}/{latest.Text}, task={state.Status}/{state.ResultSummary}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData("exit")]
    [InlineData("release")]
    [InlineData("replacement-first")]
    [InlineData("genuine-replacement-close")]
    public async Task Captured_gui_close_preserves_replacement_answer_on_plain_thread_while_genuine_replacement_close_cancels(string ordering)
    {
        var ct = TestContext.Current.CancellationToken;
        var hold = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, project, caller, _, item, _, old) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-plain-thread-",
            (_, _, _, token) => hold.Task.WaitAsync(token));

        var initial = await host.CallToolAsync(caller.Id, "send_message", new JsonObject
        {
            ["target_session_id"] = item.Id,
            ["message"] = "First question",
            ["expect_reply"] = true,
        }, ct);
        Assert.Null(initial["isError"]);
        var thread = Body(initial)["thread_id"]!.GetValue<string>();
        Assert.Single(old.SentPrompts);
        Assert.Empty(host.GetTasksForItem(caller.Id));

        var tabs = AppHost.Get<ITabContentRegistry>();
        var release = tabs.CaptureForRelease(item.Id)!;
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = s => s.PromptHandler = (_, _, _, token) => next.Task.WaitAsync(token);
        var replacement = Assert.IsType<AgentGuiTabViewModel>(tabs.GetOrCreate(project, item).DataContext);
        await replacement.ConnectAsync();
        var fresh = factory.Created.Last();

        Assert.Null((await SendOnThreadAsync(host, caller.Id, item.Id, thread, expectReply: true, ct))["isError"]);
        Assert.Single(fresh.SentPrompts);

        if (ordering == "exit")
        {
            old.IsConnected = false;
            old.Emit(new AcpClosedEvent(new IOException("retired process closed")));
        }
        if (ordering != "replacement-first")
        {
            await release();
        }

        var stateAfterOldClose = host.FindThread(thread)!.Status;
        if (ordering == "genuine-replacement-close")
        {
            Assert.Equal(CollabThreadStatus.Active, stateAfterOldClose);
            await tabs.ReleaseAsync(item.Id);
            Assert.Equal(CollabThreadStatus.Cancelled, host.FindThread(thread)!.Status);
            return;
        }

        fresh.Emit(new AcpTextDeltaEvent("assistant", "replacement answered", "new"));
        next.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => !replacement.IsTurnActive, ct);
        await WaitUntilAsync(() => host.FindThread(thread)!.Messages.Any(m => m.Text == "replacement answered"), ct);

        var state = host.FindThread(thread)!;
        Assert.True(
            stateAfterOldClose == CollabThreadStatus.Active && state.Messages.Any(m => m.Text == "replacement answered"),
            $"ordering={ordering}, beforeAnswer={stateAfterOldClose}, final={state.Status}, latest={state.Messages.Last().Text}, replacementPrompts={fresh.SentPrompts.Count}");
        if (ordering == "replacement-first")
        {
            await release();
        }
    }

    private sealed class DelayedReceiptGuardBridge(ICollabSessionBridge gui) : ICollabSessionBridge
    {
        public string ItemId => gui.ItemId;
        public string AgentId => gui.AgentId;
        public string DisplayTitle => gui.DisplayTitle;
        public string WorkingDirectory => gui.WorkingDirectory;
        public bool IsBusy => false;
        public bool IsConnected => true;
        public string? CurrentStatusDetail => null;
        public string ReadRecentSummary(int maxTurns = 5) => "";
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Receipt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<Task<bool>>? Dispatch;

        public Task<bool> DeliverCollabPromptAsync(string text, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeliverCollabPromptAsync(string text, string? tid, string? task, bool expected, string? id, Func<bool>? guard, CancellationToken ct)
        {
            Assert.True(guard?.Invoke());
            Dispatch = () => guard?.Invoke() == false ? Task.FromResult(false) : gui.DeliverCollabPromptAsync(text, tid, task, expected, id, guard, ct);
            Received.SetResult();
            return Receipt.Task;
        }
    }

    [AvaloniaTheory, FreshApp]
    [InlineData("error")]
    [InlineData("exit")]
    public async Task Latest_replacement_failure_settles_task_while_older_captured_gui_still_holds_superseded_turn(string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        var first = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, project, parent, _, item, oldVm, oldAcp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-owner-fails-first-",
            (_, _, _, token) => first.Task.WaitAsync(token));
        var (task, thread) = await DelegateToExistingAsync(host, parent.Id, item.Id, "original", ct);

        var tabs = AppHost.Get<ITabContentRegistry>();
        var release = tabs.CaptureForRelease(item.Id)!;
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = s => s.PromptHandler = (_, _, _, token) => second.Task.WaitAsync(token);
        var replacement = Assert.IsType<AgentGuiTabViewModel>(tabs.GetOrCreate(project, item).DataContext);
        await replacement.ConnectAsync();
        var fresh = factory.Created.Last();

        Assert.Null((await SendOnThreadAsync(host, parent.Id, item.Id, thread, expectReply: true, ct))["isError"]);
        Assert.Single(oldAcp.SentPrompts);
        Assert.Single(fresh.SentPrompts);
        Assert.NotEqual(((ICollabSessionBridge)oldVm).ActiveCollabDeliveryId, ((ICollabSessionBridge)replacement).ActiveCollabDeliveryId);

        if (reason == "exit")
        {
            fresh.IsConnected = false;
            fresh.Emit(new AcpClosedEvent(new IOException("current process exited")));
        }

        second.SetException(new IOException("current prompt failed"));
        await WaitUntilAsync(() => !replacement.IsTurnActive, ct);
        await release();

        var state = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == task);
        var final = host.FindThread(thread)!;
        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host)!;
        int pending;
        lock (ledger.Gate) pending = ledger.Pending.GetValueOrDefault(item.Id)?.Count ?? 0;

        Assert.True(
            state.Status == CollabTaskStatus.Failed && final.Status == CollabThreadStatus.Blocked && pending == 0,
            $"reason={reason}, task={state.Status}/{state.ResultSummary}, thread={final.Status}, last={final.Messages.Last().Text}, pending={pending}");
    }

    [AvaloniaFact, FreshApp]
    public async Task Unaccepted_followup_cannot_mask_process_exit_of_sole_running_gui()
    {
        var ct = TestContext.Current.CancellationToken;
        var work = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, parent, _, item, vm, acp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-owner-fails-before-accept-",
            (_, _, _, token) => work.Task.WaitAsync(token));
        var (task, thread) = await DelegateToExistingAsync(host, parent.Id, item.Id, "accepted original", ct);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var follow = Task.Run(() => SendOnThreadAsync(host, parent.Id, item.Id, thread, expectReply: true, cancel.Token));
        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host)!;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            lock (ledger.Gate) return ledger.Pending.TryGetValue(item.Id, out var entries) && entries.Count == 2 && !entries.Last().Accepted;
        }, TimeSpan.FromSeconds(4)));

        acp.IsConnected = false;
        acp.Emit(new AcpClosedEvent(new IOException("current process exited")));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follow);
        work.SetException(new IOException("current prompt failed"));
        await WaitUntilAsync(() => !vm.IsTurnActive, ct);

        var state = host.GetTasksForItem(parent.Id).Single(t => t.TaskId == task);
        var final = host.FindThread(thread)!;
        int pending;
        lock (ledger.Gate) pending = ledger.Pending.GetValueOrDefault(item.Id)?.Count ?? 0;

        Assert.True(
            state.Status == CollabTaskStatus.Failed && final.Status == CollabThreadStatus.Blocked && pending == 0,
            $"task={state.Status}/{state.ResultSummary}, thread={final.Status}, last={final.Messages.Last().Text}, pending={pending}, prompts={acp.SentPrompts.Count}");
    }

    [AvaloniaFact, FreshApp]
    public async Task Identity_unregister_racing_late_legacy_receipt_rolls_back_undeliverable_delegation()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, project, parent, _, item, oldVm, oldAcp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-retire-before-receipt-",
            (s, _, _, _) =>
            {
                s.Emit(new AcpTextDeltaEvent("assistant", "processed by old gui", "old"));
                return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
            });

        var legacy = new DelayedReceiptGuardBridge(oldVm);
        host.RegisterGuiSession(legacy);
        var delegation = host.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = item.Id, ["goal"] = "buffer during retirement" },
            ct);
        await legacy.Received.Task.WaitAsync(TimeSpan.FromSeconds(4), ct);

        var tabs = AppHost.Get<ITabContentRegistry>();
        var release = tabs.CaptureForRelease(item.Id)!;
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = s => s.PromptHandler = (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
        var replacement = Assert.IsType<AgentGuiTabViewModel>(tabs.GetOrCreate(project, item).DataContext);
        await replacement.ConnectAsync();

        host.UnregisterGuiSession(legacy);
        legacy.Receipt.SetResult(true);
        var receipt = await delegation;
        var ran = await legacy.Dispatch!();
        await WaitUntilAsync(() => !oldVm.IsTurnActive, ct);
        await release();
        await Task.Delay(150, ct);

        var state = host.GetTasksForItem(parent.Id).SingleOrDefault(t => t.TargetItemId == item.Id);
        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host)!;
        int pending;
        lock (ledger.Gate) pending = ledger.Pending.GetValueOrDefault(item.Id)?.Count ?? 0;

        Assert.True(
            receipt["isError"]?.GetValue<bool>() == true && !ran && oldAcp.SentPrompts.Count == 0 && (state is null || state.IsFinished) && pending == 0,
            $"receiptError={receipt["isError"]}, dispatched={ran}, oldPrompts={oldAcp.SentPrompts.Count}, task={state?.Status}, pending={pending}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Plain_thread_replacement_answer_survives_older_abort_in_pending_to_publication_interval(bool taskBacked)
    {
        var ct = TestContext.Current.CancellationToken;
        var first = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, project, parent, _, item, old, _) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-r12-plain-pub-",
            (_, _, _, token) => first.Task.WaitAsync(token));

        string thread;
        if (taskBacked)
        {
            (_, thread) = await DelegateToExistingAsync(host, parent.Id, item.Id, "first", ct);
        }
        else
        {
            thread = Body(await host.CallToolAsync(parent.Id, "send_message", new JsonObject
            {
                ["target_session_id"] = item.Id,
                ["message"] = "first",
                ["expect_reply"] = true,
            }, ct))["thread_id"]!.GetValue<string>();
        }

        var tabs = AppHost.Get<ITabContentRegistry>();
        var retire = tabs.CaptureForRelease(item.Id)!;
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = s => s.PromptHandler = (_, _, _, token) => second.Task.WaitAsync(token);
        var fresh = Assert.IsType<AgentGuiTabViewModel>(tabs.GetOrCreate(project, item).DataContext);
        await fresh.ConnectAsync();
        Assert.Null((await SendOnThreadAsync(host, parent.Id, item.Id, thread, expectReply: true, ct))["isError"]);

        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host)!;
        lock (ledger.Gate)
        {
            factory.Created.Last().Emit(new AcpTextDeltaEvent("assistant", "genuine replacement answer", "new"));
            second.SetResult(new AcpPromptResult { StopReason = "end_turn" });
            Assert.True(Frames.Until(() => !fresh.IsTurnActive));
            Assert.Single(ledger.Pending[item.Id]);
            first.SetException(new IOException("older prompt failed"));
            Assert.True(Frames.Until(() => !old.IsTurnActive));
        }

        await retire();
        await WaitUntilAsync(() => host.FindThread(thread)!.Status != CollabThreadStatus.Active, ct);
        var result = host.FindThread(thread)!;
        Assert.True(
            result.Messages.Last().Text == "genuine replacement answer",
            $"taskBacked={taskBacked}, status={result.Status}, last={result.Messages.Last().Text}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Processed_message_receipt_succeeds_after_adapter_retirement_for_expected_and_one_way(bool expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, parent, _, item, vm, acp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-r12-done-before-retire-",
            (session, _, _, _) =>
            {
                session.Emit(new AcpTextDeltaEvent("assistant", "processed once", "reply"));
                return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
            });

        BeforeAckBridge? adapter = null;
        adapter = new BeforeAckBridge(vm, _ =>
        {
            Assert.True(Frames.Until(() => !vm.IsTurnActive));
            Assert.Single(acp.SentPrompts);
            host.RegisterGuiSession(vm);
            host.UnregisterGuiSession(adapter!);
            return Task.CompletedTask;
        });
        host.RegisterGuiSession(adapter);

        var receipt = await host.CallToolAsync(parent.Id, "send_message", new JsonObject
        {
            ["target_session_id"] = item.Id,
            ["message"] = "process this once",
            ["expect_reply"] = expected,
        }, ct);
        Assert.True(receipt["isError"] is null, $"expectReply={expected}, prompts={acp.SentPrompts.Count}, receipt={receipt.ToJsonString()}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retired_adapter_settles_accepted_queued_delegation_regardless_of_receipt_order(bool retireBeforeReceipt)
    {
        var ct = TestContext.Current.CancellationToken;
        var hold = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, parent, _, item, vm, acp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-r12-revoked-queue-",
            (_, _, _, token) => hold.Task.WaitAsync(token));
        vm.InputText = "manual turn";
        var manual = vm.SendPromptAsync();

        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new BeforeAckBridge(vm, _ =>
        {
            received.SetResult();
            return release.Task;
        });
        host.RegisterGuiSession(adapter);

        var delegation = host.CallToolAsync(parent.Id, "delegate_task", new JsonObject
        {
            ["target"] = item.Id,
            ["goal"] = "accepted queued work",
        }, ct);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(4), ct);
        var queued = Assert.Single(vm.QueuedPrompts);
        Assert.True(queued.ShouldDispatch!());

        if (!retireBeforeReceipt)
        {
            release.SetResult();
            await delegation;
        }
        host.RegisterGuiSession(vm);
        host.UnregisterGuiSession(adapter);
        Assert.False(queued.ShouldDispatch!());
        if (retireBeforeReceipt)
        {
            release.SetResult();
        }

        _ = await delegation;
        hold.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await manual;

        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host)!;
        int pending;
        lock (ledger.Gate) pending = ledger.Pending.GetValueOrDefault(item.Id)?.Count ?? 0;
        var task = host.GetTasksForItem(parent.Id).SingleOrDefault();

        Assert.True(
            acp.SentPrompts.Count == 1 && vm.QueuedPrompts.Count == 0 && pending == 0 && (task is null || task.IsFinished),
            $"retiredBeforeReceipt={retireBeforeReceipt}, task={task?.Status}, pending={pending}, prompts={acp.SentPrompts.Count}, queue={vm.QueuedPrompts.Count}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retired_adapter_preserves_actively_executing_delegation_regardless_of_receipt_order(bool retireBeforeReceipt)
    {
        var ct = TestContext.Current.CancellationToken;
        var finish = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, parent, _, item, vm, acp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-r13-executing-retire-",
            (_, _, _, token) => finish.Task.WaitAsync(token));

        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new BeforeAckBridge(vm, _ =>
        {
            received.SetResult();
            return ack.Task;
        });
        host.RegisterGuiSession(adapter);

        var call = host.CallToolAsync(parent.Id, "delegate_task", new JsonObject
        {
            ["target"] = item.Id,
            ["goal"] = "deliver an answer",
        }, ct);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(4), ct);
        Assert.True(vm.IsTurnActive);
        Assert.Single(acp.SentPrompts);

        if (!retireBeforeReceipt)
        {
            ack.SetResult();
            await call;
        }
        host.RegisterGuiSession(vm);
        host.UnregisterGuiSession(adapter);
        if (retireBeforeReceipt)
        {
            ack.SetResult();
        }

        var receipt = await call;
        var atRetire = Assert.Single(host.GetTasksForItem(parent.Id)).Status;
        acp.Emit(new AcpTextDeltaEvent("assistant", "finished on original ACP", "real"));
        finish.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => !vm.IsTurnActive && host.GetTasksForItem(parent.Id).Single().IsFinished, ct);

        var state = Assert.Single(host.GetTasksForItem(parent.Id));
        var thread = host.FindThread(state.ThreadId!)!;
        Assert.True(
            receipt["isError"] is null && atRetire == CollabTaskStatus.Running && state.Status == CollabTaskStatus.Completed && state.ResultSummary == "finished on original ACP",
            $"retireBeforeReceipt={retireBeforeReceipt}, receiptError={receipt["isError"]}, atRetire={atRetire}, final={state.Status}/{state.ResultSummary}, thread={thread.Status}/{thread.Messages.Last().Text}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_older_gui_success_cannot_overwrite_replacement_answer_on_plain_or_task_thread(bool taskBacked)
    {
        var ct = TestContext.Current.CancellationToken;
        var first = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, project, parent, _, item, old, acp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-r13-stale-success-",
            (_, _, _, token) => first.Task.WaitAsync(token));

        string tid;
        if (taskBacked)
        {
            (_, tid) = await DelegateToExistingAsync(host, parent.Id, item.Id, "original", ct);
        }
        else
        {
            tid = Body(await host.CallToolAsync(parent.Id, "send_message", new JsonObject
            {
                ["target_session_id"] = item.Id,
                ["message"] = "original",
                ["expect_reply"] = true,
            }, ct))["thread_id"]!.GetValue<string>();
        }

        var registry = AppHost.Get<ITabContentRegistry>();
        var retire = registry.CaptureForRelease(item.Id)!;
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = s => s.PromptHandler = (_, _, _, token) => second.Task.WaitAsync(token);
        var next = Assert.IsType<AgentGuiTabViewModel>(registry.GetOrCreate(project, item).DataContext);
        await next.ConnectAsync();

        Assert.Null((await SendOnThreadAsync(host, parent.Id, item.Id, tid, expectReply: true, ct))["isError"]);
        factory.Created.Last().Emit(new AcpTextDeltaEvent("assistant", "current final answer", "new"));
        second.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => host.FindThread(tid)!.Messages.Last().Text == "current final answer" && !next.IsTurnActive, ct);

        acp.Emit(new AcpTextDeltaEvent("assistant", "expired older answer", "old"));
        first.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => !old.IsTurnActive, ct);
        await retire();
        Frames.Until(() => host.FindThread(tid)!.Messages.Last().Text == "expired older answer", milliseconds: 500);

        var view = Body(await host.AwaitEventsAsync(parent.Id, [tid], 1, ct));
        var latest = (string?)view["threads"]![0]!["latest_message"];
        Assert.True(
            latest == "current final answer",
            $"taskBacked={taskBacked}, latestFromAwait={latest}, messages={string.Join(" / ", host.FindThread(tid)!.Messages.Select(m => m.Text))}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task New_expected_acceptance_suppresses_older_unpublished_answer_while_informational_send_preserves_it(bool taskBacked, bool expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var a = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, parent, _, item, vm, acp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-r13-accept-pub-",
            (_, text, _, token) => (text.Contains("first job", StringComparison.Ordinal) ? a : b).Task.WaitAsync(token));

        string tid;
        if (taskBacked)
        {
            (_, tid) = await DelegateToExistingAsync(host, parent.Id, item.Id, "first job", ct);
        }
        else
        {
            tid = Body(await host.CallToolAsync(parent.Id, "send_message", new JsonObject
            {
                ["target_session_id"] = item.Id,
                ["message"] = "first job",
                ["expect_reply"] = true,
            }, ct))["thread_id"]!.GetValue<string>();
        }

        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host)!;
        lock (ledger.Gate)
        {
            acp.Emit(new AcpTextDeltaEvent("assistant", "older answer", "older"));
            a.SetResult(new AcpPromptResult { StopReason = "end_turn" });
            Assert.True(Frames.Until(() => !vm.IsTurnActive));
            Assert.False(ledger.Pending.ContainsKey(item.Id));
            var newer = SendOnThreadAsync(host, parent.Id, item.Id, tid, expected, ct);
            Assert.True(newer.IsCompletedSuccessfully);
            Assert.Null(newer.Result["isError"]);
            Assert.Equal(2, acp.SentPrompts.Count);
        }

        var observedEarlier = Frames.Until(() => host.FindThread(tid)!.Messages.Any(m => m.Text == "older answer"), milliseconds: 600);
        acp.Emit(new AcpTextDeltaEvent("assistant", "new answer", "new"));
        b.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => !vm.IsTurnActive && host.FindThread(tid)!.Messages.Any(m => m.Text == (expected ? "new answer" : "older answer")), ct);

        var messages = host.FindThread(tid)!.Messages.Select(m => m.Text).ToList();
        Assert.True(
            messages.Contains("older answer") == !expected && messages.Contains("new answer") == expected,
            $"taskBacked={taskBacked}, expected={expected}, earlierPublishedDuringNext={observedEarlier}, messages={string.Join(" / ", messages)}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_refusal_after_adapter_retirement_before_gui_acceptance_rolls_back_task_and_thread(bool retireBeforeRefusal)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, parent, _, item, vm, acp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-r13-pre-accept-refusal-",
            (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));
        var legacy = new DelayedReceiptGuardBridge(vm);
        host.RegisterGuiSession(legacy);

        var call = host.CallToolAsync(parent.Id, "delegate_task", new JsonObject
        {
            ["target"] = item.Id,
            ["goal"] = "refused before target",
        }, ct);
        await legacy.Received.Task.WaitAsync(TimeSpan.FromSeconds(4), ct);
        var task = Assert.Single(host.GetTasksForItem(parent.Id));

        if (retireBeforeRefusal)
        {
            host.RegisterGuiSession(vm);
            host.UnregisterGuiSession(legacy);
        }
        legacy.Receipt.SetResult(false);
        var receipt = await call;
        var ran = await legacy.Dispatch!();

        var taskCount = host.GetTasksForItem(parent.Id).Count;
        var thread = host.FindThread(task.ThreadId!);
        Assert.True(
            receipt["isError"]?.GetValue<bool>() == true && !ran && acp.SentPrompts.Count == 0 && taskCount == 0 && thread is null,
            $"retireBeforeRefusal={retireBeforeRefusal}, receipt={receipt.ToJsonString()}, dispatch={ran}, prompts={acp.SentPrompts.Count}, visibleTasks={taskCount}, visibleThread={thread?.Status}, task={task.Status}");
    }

    private sealed record BufferedForwardCall(
        string Text,
        string? Thread,
        string? TaskId,
        bool Expected,
        string? Id,
        Func<bool>? Guard,
        CancellationToken Token)
    {
        public TaskCompletionSource<bool> Receipt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class MultiModeForwardBufferBridge(ICollabSessionBridge gui) : ICollabSessionBridge
    {
        public List<BufferedForwardCall> Calls { get; } = [];
        public string ItemId => gui.ItemId;
        public string AgentId => gui.AgentId;
        public string DisplayTitle => gui.DisplayTitle;
        public string WorkingDirectory => gui.WorkingDirectory;
        public bool IsBusy => gui.IsBusy;
        public bool IsConnected => gui.IsConnected;
        public string? CurrentStatusDetail => gui.CurrentStatusDetail;
        public string ReadRecentSummary(int maxTurns = 5) => gui.ReadRecentSummary(maxTurns);
        public Task<bool> DeliverCollabPromptAsync(string p, string? t = null, string? a = null, CancellationToken cancellationToken = default) =>
            gui.DeliverCollabPromptAsync(p, t, a, cancellationToken);
        public Task<bool> DeliverCollabPromptAsync(string p, string? t, string? a, bool e, string? id, Func<bool>? g, CancellationToken ct)
        {
            var call = new BufferedForwardCall(p, t, a, e, id, g, ct);
            Calls.Add(call);
            return call.Receipt.Task;
        }

        public Task<bool> ForwardAsync(BufferedForwardCall call, string forwarding)
        {
            if (call.Guard?.Invoke() == false) return Task.FromResult(false);
            return forwarding switch
            {
                "legacy" => gui.DeliverCollabPromptAsync(call.Text, call.Thread, call.TaskId, call.Expected, call.Token),
                "wrapped" => gui.DeliverCollabPromptAsync(call.Text, call.Thread, call.TaskId, call.Expected, call.Id, () => call.Guard!(), call.Token),
                _ => gui.DeliverCollabPromptAsync(call.Text, call.Thread, call.TaskId, call.Expected, call.Id, call.Guard, call.Token),
            };
        }
    }

    [AvaloniaTheory, FreshApp]
    [InlineData("exact", true)]
    [InlineData("legacy", true)]
    [InlineData("exact", false)]
    [InlineData("legacy", false)]
    public async Task Executing_delegation_survives_adapter_retirement_for_exact_and_legacy_forwarders(string forwarding, bool retireBeforeReceipt)
    {
        var ct = TestContext.Current.CancellationToken;
        var finish = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, parent, _, item, vm, acp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-r14-delegate-forward-",
            (_, _, _, token) => finish.Task.WaitAsync(token));
        var adapter = new MultiModeForwardBufferBridge(vm);
        host.RegisterGuiSession(adapter);

        var result = host.CallToolAsync(parent.Id, "delegate_task", new JsonObject
        {
            ["target"] = item.Id,
            ["goal"] = "execute and return",
        }, ct);
        var c = Assert.Single(adapter.Calls);
        Assert.True(await adapter.ForwardAsync(c, forwarding));
        Assert.True(vm.IsTurnActive);
        Assert.Single(acp.SentPrompts);

        if (!retireBeforeReceipt)
        {
            c.Receipt.SetResult(true);
            await result;
        }
        host.RegisterGuiSession(vm);
        host.UnregisterGuiSession(adapter);
        if (retireBeforeReceipt)
        {
            c.Receipt.SetResult(true);
        }
        Assert.Null((await result)["isError"]);

        var afterRetire = Assert.Single(host.GetTasksForItem(parent.Id)).Status;
        acp.Emit(new AcpTextDeltaEvent("assistant", "computed successfully", "done"));
        finish.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => !vm.IsTurnActive && host.GetTasksForItem(parent.Id).Single().IsFinished, ct);

        var final = Assert.Single(host.GetTasksForItem(parent.Id));
        Assert.True(
            afterRetire == CollabTaskStatus.Running && final.Status == CollabTaskStatus.Completed && final.ResultSummary == "computed successfully",
            $"forward={forwarding}, retireBeforeReceipt={retireBeforeReceipt}, afterRetire={afterRetire}, final={final.Status}/{final.ResultSummary}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Older_tab_completion_is_suppressed_when_replacement_tab_already_queued_expected_question(bool taskBacked, bool expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var first = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manualGate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, project, parent, _, item, old, originalAcp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-r14-queued-replacement-",
            (_, _, _, token) => first.Task.WaitAsync(token));

        string tid;
        if (taskBacked)
        {
            (_, tid) = await DelegateToExistingAsync(host, parent.Id, item.Id, "first request", ct);
        }
        else
        {
            tid = (string)Body(await host.CallToolAsync(parent.Id, "send_message", new JsonObject
            {
                ["target_session_id"] = item.Id,
                ["message"] = "first request",
                ["expect_reply"] = true,
            }, ct))["thread_id"]!;
        }

        var tabs = AppHost.Get<ITabContentRegistry>();
        var release = tabs.CaptureForRelease(item.Id)!;
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = s => s.PromptHandler = (_, text, _, token) => (text == "manual" ? manualGate : second).Task.WaitAsync(token);
        var next = Assert.IsType<AgentGuiTabViewModel>(tabs.GetOrCreate(project, item).DataContext);
        await next.ConnectAsync();
        var acp = factory.Created.Last();

        next.InputText = "manual";
        var manual = next.SendPromptAsync();
        Assert.Null((await SendOnThreadAsync(host, parent.Id, item.Id, tid, expected, ct))["isError"]);
        Assert.Single(next.QueuedPrompts);

        originalAcp.Emit(new AcpTextDeltaEvent("assistant", "former tab answer", "old"));
        first.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => !old.IsTurnActive, ct);
        await release();
        Frames.Until(() => host.FindThread(tid)!.Status == CollabThreadStatus.Agreed, milliseconds: 500);

        var statusWhileQueued = host.FindThread(tid)!.Status;
        var snapshot = Body(await host.AwaitEventsAsync(parent.Id, [tid], 1, ct));
        var latestWhileQueued = (string?)snapshot["threads"]![0]!["latest_message"];
        Assert.Single(next.QueuedPrompts);
        Assert.Equal("manual", Assert.Single(acp.SentPrompts).Text);

        manualGate.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => acp.SentPrompts.Count == 2, ct);
        acp.Emit(new AcpTextDeltaEvent("assistant", "replacement tab answer", "new"));
        second.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await manual;
        if (expected)
        {
            await WaitUntilAsync(() => host.FindThread(tid)!.Messages.Last().Text == "replacement tab answer", ct);
        }

        Assert.True(
            expected
                ? statusWhileQueued == CollabThreadStatus.Active && latestWhileQueued != "former tab answer"
                : latestWhileQueued == "former tab answer",
            $"taskBacked={taskBacked}, expected={expected}, whileQueued={statusWhileQueued}/{latestWhileQueued}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retirement_while_provisional_delivery_is_entering_rolls_back_task_and_thread_cleanly(bool retire)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, project, parent, _, item, vm, acp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-r14-refusal-entering-",
            (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));
        var old = new StubBridge(item, project.Path);
        host.RegisterGuiSession(old);
        Assert.Null((await host.CallToolAsync(parent.Id, "send_message", new JsonObject
        {
            ["target_session_id"] = item.Id,
            ["message"] = "warm-up to stub",
            ["expect_reply"] = false,
        }, ct))["isError"]);

        var receiptGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        old.OnDeliver = (_, _, _, _) => receiptGate.Task;

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var lifetimes = typeof(CollabCoordinator).GetField("_bridgeLifetimes", flags)!.GetValue(host)!;
        object?[] args = [old, null];
        Assert.True((bool)lifetimes.GetType().GetMethod("TryGetValue")!.Invoke(lifetimes, args)!);
        var lifetime = args[1]!;
        var gate = lifetime.GetType().GetField("_gate", flags)!.GetValue(lifetime)!;
        var tracked = (HashSet<string>)lifetime.GetType().GetField("_deliveryIds", flags)!.GetValue(lifetime)!;
        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator).GetField("_ledger", flags)!.GetValue(host)!;

        Task<JsonObject> call;
        string tid;
        Monitor.Enter(gate);
        try
        {
            call = Task.Run(() => host.CallToolAsync(parent.Id, "delegate_task", new JsonObject
            {
                ["target"] = item.Id,
                ["goal"] = "will refuse",
            }, ct));
            Assert.True(SpinWait.SpinUntil(() => { lock (ledger.Gate) return ledger.Pending.ContainsKey(item.Id); }, TimeSpan.FromSeconds(4)));
            lock (ledger.Gate)
            {
                var p = Assert.Single(ledger.Pending[item.Id]);
                tid = p.ThreadId!;
                Assert.False(p.Accepted);
                Assert.False(p.Dispatched);
                Monitor.Exit(gate);
                try
                {
                    Assert.True(SpinWait.SpinUntil(() => { lock (gate) return tracked.Contains(p.DeliveryId!); }, TimeSpan.FromSeconds(4)));
                    if (retire)
                    {
                        host.RegisterGuiSession(vm);
                        host.UnregisterGuiSession(old);
                    }
                }
                finally
                {
                    Monitor.Enter(gate);
                }
            }
        }
        finally
        {
            Monitor.Exit(gate);
        }

        receiptGate.SetResult(false);
        var result = await call;
        var rows = host.GetTasksForItem(parent.Id);
        var thread = host.FindThread(tid);
        Assert.True(
            result["isError"]?.GetValue<bool>() == true && acp.SentPrompts.Count == 0 && rows.Count == 0 && thread is null,
            $"retire={retire}, toolError={result["isError"]}, adapterPayloadCalls={old.Delivered.Count - 1}, ACP={acp.SentPrompts.Count}, visible={rows.Count}/{thread?.Status}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Same_thread_active_one_way_turn_does_not_promote_queued_exact_question_on_adapter_retirement(bool sameThread, bool beforeReceipt)
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, caller, _, item, vm, acp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-r15-oneway-queued-",
            (_, _, _, token) => gate.Task.WaitAsync(token));

        var activeReceipt = await host.CallToolAsync(caller.Id, "send_message", new JsonObject
        {
            ["target_session_id"] = item.Id,
            ["expect_reply"] = false,
            ["message"] = "running informational notice",
        }, ct);
        var thread = (string)Body(activeReceipt)["thread_id"]!;
        Assert.True(((ICollabSessionBridge)vm).TryGetActiveCollabTurn(out var activeThread, out var activeTask, out var activeId));
        Assert.Equal(thread, activeThread);
        Assert.Null(activeTask);
        Assert.Null(activeId);

        var adapter = new MultiModeForwardBufferBridge(vm);
        host.RegisterGuiSession(adapter);
        var args = new JsonObject
        {
            ["target_session_id"] = item.Id,
            ["expect_reply"] = true,
            ["message"] = "queued question",
        };
        if (sameThread) args["thread_id"] = thread;
        var reply = host.CallToolAsync(caller.Id, "send_message", args, ct);
        var c = Assert.Single(adapter.Calls);
        Assert.NotNull(c.Id);
        Assert.True(await adapter.ForwardAsync(c, "exact"));
        Assert.Single(vm.QueuedPrompts);
        Assert.Single(acp.SentPrompts);

        if (!beforeReceipt)
        {
            c.Receipt.SetResult(true);
            await reply;
        }
        host.RegisterGuiSession(vm);
        host.UnregisterGuiSession(adapter);
        if (beforeReceipt) c.Receipt.SetResult(true);

        var result = await reply;
        var tid = (string)Body(result)["thread_id"]!;
        gate.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => !vm.IsTurnActive && vm.QueuedPrompts.Count == 0, ct);

        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host)!;
        int pending;
        lock (ledger.Gate) pending = ledger.Pending.GetValueOrDefault(item.Id)?.Count ?? 0;
        var state = host.FindThread(tid)!.Status;
        Assert.True(
            acp.SentPrompts.Count == 1 && pending == 0 && state == CollabThreadStatus.Cancelled,
            $"sameThread={sameThread}, beforeReceipt={beforeReceipt}, ACP={acp.SentPrompts.Count}, pending={pending}, thread={state}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Switching_registration_from_exact_forwarder_to_same_gui_preserves_both_fifo_answers(bool switchRegistration)
    {
        var ct = TestContext.Current.CancellationToken;
        var first = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, caller, _, item, vm, acp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-r15-fifo-switch-",
            (_, text, _, token) => (text.Contains("first plain question", StringComparison.Ordinal) ? first : second).Task.WaitAsync(token));

        var adapter = new BeforeAckBridge(vm, _ => Task.CompletedTask);
        host.RegisterGuiSession(adapter);
        var result = await host.CallToolAsync(caller.Id, "send_message", new JsonObject
        {
            ["target_session_id"] = item.Id,
            ["message"] = "first plain question",
            ["expect_reply"] = true,
        }, ct);
        var tid = (string)Body(result)["thread_id"]!;
        Assert.Single(acp.SentPrompts);

        if (switchRegistration)
        {
            host.RegisterGuiSession(vm);
            host.UnregisterGuiSession(adapter);
        }
        Assert.Null((await SendOnThreadAsync(host, caller.Id, item.Id, tid, expectReply: true, ct))["isError"]);
        Assert.Single(vm.QueuedPrompts);
        Assert.Single(acp.SentPrompts);

        acp.Emit(new AcpTextDeltaEvent("assistant", "first FIFO answer", "first"));
        first.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => acp.SentPrompts.Count == 2, ct);
        _ = Frames.Until(() => host.FindThread(tid)!.Messages.Any(m => m.Text == "first FIFO answer"), milliseconds: 750);

        acp.Emit(new AcpTextDeltaEvent("assistant", "second FIFO answer", "second"));
        second.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => !vm.IsTurnActive && host.FindThread(tid)!.Messages.Last().Text == "second FIFO answer", ct);

        var messages = host.FindThread(tid)!.Messages.Select(m => m.Text).ToList();
        Assert.True(
            messages.Contains("first FIFO answer") && messages.Contains("second FIFO answer"),
            $"switchedRegistration={switchRegistration}, messages={string.Join(" / ", messages)}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData("legacy", true)]
    [InlineData("legacy", false)]
    [InlineData("wrapped", true)]
    [InlineData("exact", false)]
    public async Task Queued_delegation_behind_manual_turn_cancels_cleanly_across_retirement_for_exact_wrapped_and_legacy_routes(string route, bool beforeReceipt)
    {
        var ct = TestContext.Current.CancellationToken;
        var manualGate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, parent, _, item, vm, acp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-r15-queued-routes-",
            (_, text, _, token) => text == "manual"
                ? manualGate.Task.WaitAsync(token)
                : Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));

        vm.InputText = "manual";
        var manual = vm.SendPromptAsync();
        var adapter = new MultiModeForwardBufferBridge(vm);
        host.RegisterGuiSession(adapter);

        var response = host.CallToolAsync(parent.Id, "delegate_task", new JsonObject
        {
            ["target"] = item.Id,
            ["goal"] = "queued work",
        }, ct);
        var c = Assert.Single(adapter.Calls);
        Assert.True(await adapter.ForwardAsync(c, route));
        Assert.Single(vm.QueuedPrompts);

        if (!beforeReceipt)
        {
            c.Receipt.SetResult(true);
            await response;
        }
        host.RegisterGuiSession(vm);
        host.UnregisterGuiSession(adapter);
        if (beforeReceipt) c.Receipt.SetResult(true);

        var result = await response;
        var afterRetire = Assert.Single(host.GetTasksForItem(parent.Id)).Status;
        manualGate.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await manual;
        await WaitUntilAsync(() => !vm.IsTurnActive, ct);

        var final = Assert.Single(host.GetTasksForItem(parent.Id));
        Assert.True(
            afterRetire == CollabTaskStatus.Cancelled
            && final.Status == CollabTaskStatus.Cancelled
            && result["isError"] is null
            && vm.QueuedPrompts.Count == 0
            && acp.SentPrompts.Count == 1,
            $"route={route}, beforeReceipt={beforeReceipt}, error={result["isError"]}, afterRetire={afterRetire}, final={final.Status}, queue={vm.QueuedPrompts.Count}, delegatedACP={acp.SentPrompts.Count - 1}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData("legacy", true, true)]
    [InlineData("legacy", true, false)]
    [InlineData("legacy", false, true)]
    [InlineData("legacy", false, false)]
    [InlineData("exact", true, true)]
    [InlineData("exact", true, false)]
    [InlineData("wrapped", true, true)]
    public async Task Queued_work_already_executing_after_drain_survives_forwarder_retirement_for_legacy_exact_and_wrapped_routes(
        string route,
        bool receiptBeforeDrain,
        bool taskBacked)
    {
        var ct = TestContext.Current.CancellationToken;
        var manualGate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, parent, _, item, vm, acp) = await SetUpRealGuiWorkerAsync(
            "octopus-ordering-r16-queued-running-",
            (_, text, _, token) => (text == "manual" ? manualGate : gate).Task.WaitAsync(token));

        vm.InputText = "manual";
        var manual = vm.SendPromptAsync();
        var adapter = new MultiModeForwardBufferBridge(vm);
        host.RegisterGuiSession(adapter);

        var response = taskBacked
            ? host.CallToolAsync(parent.Id, "delegate_task", new JsonObject { ["target"] = item.Id, ["goal"] = "calculate" }, ct)
            : host.CallToolAsync(parent.Id, "send_message", new JsonObject { ["target_session_id"] = item.Id, ["message"] = "calculate", ["expect_reply"] = true }, ct);
        var c = Assert.Single(adapter.Calls);
        Assert.True(await adapter.ForwardAsync(c, route));
        Assert.Single(vm.QueuedPrompts);
        Assert.Single(acp.SentPrompts);

        if (receiptBeforeDrain)
        {
            c.Receipt.SetResult(true);
            Assert.Null((await response)["isError"]);
        }

        manualGate.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => acp.SentPrompts.Count == 2, ct);
        Assert.True(vm.IsTurnActive);
        Assert.Empty(vm.QueuedPrompts);
        Assert.Contains("calculate", acp.SentPrompts[1].Text, StringComparison.Ordinal);
        Assert.True(((ICollabSessionBridge)vm).TryGetActiveCollabTurn(out var thread, out var task, out _));
        Assert.Equal(c.Thread, thread);
        Assert.Equal(c.TaskId, task);

        if (!receiptBeforeDrain)
        {
            c.Receipt.SetResult(true);
            Assert.Null((await response)["isError"]);
        }

        host.RegisterGuiSession(vm);
        host.UnregisterGuiSession(adapter);
        var midTask = host.GetTasksForItem(parent.Id).FirstOrDefault()?.Status;
        var midThread = host.FindThread(thread!)!.Status;

        acp.Emit(new AcpTextDeltaEvent("assistant", "computed after queue drain", "1"));
        gate.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await manual;
        await WaitUntilAsync(() => !vm.IsTurnActive && host.FindThread(thread!)!.Status != CollabThreadStatus.Active, ct);

        var final = host.FindThread(thread!)!;
        Assert.True(
            midThread == CollabThreadStatus.Active
            && (!taskBacked || midTask == CollabTaskStatus.Running)
            && final.Messages.Any(m => m.Text == "computed after queue drain"),
            $"route={route}, taskBacked={taskBacked}, receiptBeforeDrain={receiptBeforeDrain}, mid={midThread}/{midTask}, final={final.Status}/{final.Messages.Last().Text}");
    }
}
