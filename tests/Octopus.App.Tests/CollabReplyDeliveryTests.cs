using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Octopus.Acp.Protocol;
using Octopus.App.AgentGui;
using Octopus.App.Collab;
using Octopus.App.Collab.Ledger;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

public sealed class CollabReplyDeliveryTests
{
    private sealed class Bridge(ProjectItem item, string directory) : ICollabSessionBridge
    {
        public string ItemId => item.Id;
        public string AgentId => item.AgentId!;
        public string DisplayTitle => item.Title;
        public string WorkingDirectory => directory;
        public bool IsBusy => false;
        public bool IsConnected => true;
        public string? CurrentStatusDetail => null;
        public int DeliveryAttempts;
        public Func<string, string?, CancellationToken, Task<bool>> Receive { get; set; } = (_, _, _) => Task.FromResult(true);

        public Task<bool> DeliverCollabPromptAsync(
            string promptText,
            string? threadId = null,
            string? taskId = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref DeliveryAttempts);
            return Receive(promptText, threadId, cancellationToken);
        }

        public string ReadRecentSummary(int maxTurns = 5) => "";
    }

    private sealed record Fixture(CollabCoordinator Host, Bridge Parent, Bridge Worker)
    {
        public static Fixture Create(string prefix)
        {
            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(Directory.CreateTempSubdirectory(prefix).FullName);
            var host = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
            host.Server.Start();

            Bridge Add(string name)
            {
                var item = new ProjectItem
                {
                    Kind = ProjectItemKind.Agent,
                    AgentId = name,
                    Title = name,
                    Surface = AgentSurface.Gui,
                };
                projects.AddItem(project, item, activate: false);
                var bridge = new Bridge(item, project.Path);
                host.RegisterGuiSession(bridge);
                return bridge;
            }

            return new Fixture(host, Add("claude"), Add("codex"));
        }

        public async Task<(string TaskId, string ThreadId)> DispatchAsync(CancellationToken ct)
        {
            var result = await Host.CallToolAsync(
                Parent.ItemId,
                "delegate_task",
                new JsonObject { ["target"] = Worker.ItemId, ["goal"] = "Report the fixture result" },
                ct);
            Assert.Null(result["isError"]);
            var body = Body(result);
            return (body["task_id"]!.GetValue<string>(), body["thread_id"]!.GetValue<string>());
        }

        public Task<JsonObject> ReplyAsync(string threadId, string message, string status, CancellationToken ct) =>
            Host.CallToolAsync(
                Worker.ItemId,
                "reply_thread",
                new JsonObject { ["thread_id"] = threadId, ["message"] = message, ["status"] = status },
                ct);
    }

    private static JsonObject Body(JsonObject result) =>
        JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

    [AvaloniaFact, FreshApp]
    public async Task Reply_thread_when_receiver_refuses_preserves_recorded_reply_without_delivered_to_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = Fixture.Create("octopus-issue42-refuse-");
        var (taskId, threadId) = await f.DispatchAsync(ct);

        f.Parent.Receive = (_, _, _) => Task.FromResult(false);

        var reply = await f.ReplyAsync(threadId, "fixture complete", "agreed", ct);
        Assert.Equal(1, Volatile.Read(ref f.Parent.DeliveryAttempts));
        Assert.Null(reply["isError"]);

        var body = Body(reply);
        Assert.Null(body["delivered_to"]);
        Assert.True(body["recorded"]?.GetValue<bool>());
        Assert.Equal("failed", body["delivery"]?.GetValue<string>());
        Assert.Equal(f.Parent.ItemId, body["recipient_session_id"]?.GetValue<string>());
        Assert.Equal(threadId, body["thread_id"]?.GetValue<string>());
        Assert.Equal(2, body["round"]?.GetValue<int>());
        Assert.Equal("agreed", body["status"]?.GetValue<string>());
        Assert.Contains("duplicate", body["note"]?.GetValue<string>() ?? "", StringComparison.OrdinalIgnoreCase);

        var thread = f.Host.FindThread(threadId)!;
        Assert.Equal(CollabThreadStatus.Agreed, thread.Status);
        Assert.Equal(2, thread.Hops);
        Assert.Equal("fixture complete", thread.Messages[^1].Text);

        var task = Assert.Single(f.Host.GetTasksForItem(f.Parent.ItemId), t => t.TaskId == taskId);
        Assert.Equal(CollabTaskStatus.Completed, task.Status);
        Assert.Equal("fixture complete", task.ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Reply_thread_when_receiver_throws_preserves_recorded_reply_without_delivered_to_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = Fixture.Create("octopus-issue42-throw-");
        var (taskId, threadId) = await f.DispatchAsync(ct);

        f.Parent.Receive = (_, _, _) => Task.FromException<bool>(new InvalidOperationException("receiver tab closed"));

        var reply = await f.ReplyAsync(threadId, "deliverable ready", "agreed", ct);
        Assert.Equal(1, Volatile.Read(ref f.Parent.DeliveryAttempts));
        Assert.Null(reply["isError"]);

        var body = Body(reply);
        Assert.Null(body["delivered_to"]);
        Assert.True(body["recorded"]?.GetValue<bool>());
        Assert.Equal("failed", body["delivery"]?.GetValue<string>());
        Assert.Equal("wakeup_delivery_error", body["delivery_error"]?.GetValue<string>());
        Assert.Equal(f.Parent.ItemId, body["recipient_session_id"]?.GetValue<string>());
        var note = body["note"]?.GetValue<string>() ?? "";
        Assert.DoesNotContain("receiver tab closed", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("duplicate", note, StringComparison.OrdinalIgnoreCase);

        var thread = f.Host.FindThread(threadId)!;
        Assert.Equal(CollabThreadStatus.Agreed, thread.Status);
        Assert.Equal("deliverable ready", thread.Messages[^1].Text);

        var task = Assert.Single(f.Host.GetTasksForItem(f.Parent.ItemId), t => t.TaskId == taskId);
        Assert.Equal(CollabTaskStatus.Completed, task.Status);
        Assert.Equal("deliverable ready", task.ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Reply_thread_when_cancelled_propagates_cancellation_preserves_reply_and_clears_stale_pending()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = Fixture.Create("octopus-issue42-cancel-");
        var (_, threadId) = await f.DispatchAsync(ct);

        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        f.Parent.Receive = (_, _, token) =>
        {
            callCts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => f.ReplyAsync(threadId, "need clarification", "continue", callCts.Token));

        var thread = f.Host.FindThread(threadId)!;
        Assert.Equal(CollabThreadStatus.Active, thread.Status);
        Assert.Equal(2, thread.Messages.Count);
        Assert.Equal("need clarification", thread.Messages[^1].Text);

        var spuriousWakeup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Worker.Receive = (_, _, _) =>
        {
            spuriousWakeup.TrySetResult();
            return Task.FromResult(true);
        };

        f.Host.NotifyTurnCompleted(f.Parent.ItemId, "unrelated parent turn");
        await Task.WhenAny(spuriousWakeup.Task, Task.Delay(150, ct));

        Assert.False(spuriousWakeup.Task.IsCompleted, "Cancelled follow-up delivery left a stale pending expectation on the receiver.");
        Assert.Equal(2, thread.Messages.Count);
        Assert.Equal(CollabThreadStatus.Active, thread.Status);
    }

    [AvaloniaFact, FreshApp]
    public async Task Reply_thread_failed_follow_up_does_not_leave_stale_pending_on_receiver()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = Fixture.Create("octopus-issue42-pending-false-");
        var (_, threadId) = await f.DispatchAsync(ct);

        f.Parent.Receive = (_, _, _) => Task.FromResult(false);

        var reply = await f.ReplyAsync(threadId, "which format?", "continue", ct);
        Assert.Null(reply["isError"]);

        var spuriousWakeup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Worker.Receive = (_, _, _) =>
        {
            spuriousWakeup.TrySetResult();
            return Task.FromResult(true);
        };

        f.Host.NotifyTurnCompleted(f.Parent.ItemId, "unrelated parent turn");
        await Task.WhenAny(spuriousWakeup.Task, Task.Delay(150, ct));

        var thread = f.Host.FindThread(threadId)!;
        Assert.False(spuriousWakeup.Task.IsCompleted, "Refused follow-up delivery left a stale pending expectation on the receiver.");
        Assert.Equal(2, thread.Messages.Count);
        Assert.Equal(CollabThreadStatus.Active, thread.Status);

        var body = Body(reply);
        Assert.Null(body["delivered_to"]);
        Assert.True(body["recorded"]?.GetValue<bool>());
        Assert.Equal("failed", body["delivery"]?.GetValue<string>());
    }

    [AvaloniaFact, FreshApp]
    public async Task Reply_thread_throwing_follow_up_does_not_leave_stale_pending_on_receiver()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = Fixture.Create("octopus-issue42-pending-throw-");
        var (_, threadId) = await f.DispatchAsync(ct);

        f.Parent.Receive = (_, _, _) => Task.FromException<bool>(new IOException("pipe broken"));

        var reply = await f.ReplyAsync(threadId, "which format?", "continue", ct);
        Assert.Null(reply["isError"]);
        var body = Body(reply);
        Assert.Null(body["delivered_to"]);
        Assert.True(body["recorded"]?.GetValue<bool>());
        Assert.Equal("failed", body["delivery"]?.GetValue<string>());

        var spuriousWakeup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Worker.Receive = (_, _, _) =>
        {
            spuriousWakeup.TrySetResult();
            return Task.FromResult(true);
        };

        f.Host.NotifyTurnCompleted(f.Parent.ItemId, "unrelated parent turn");
        await Task.WhenAny(spuriousWakeup.Task, Task.Delay(150, ct));

        var thread = f.Host.FindThread(threadId)!;
        Assert.False(spuriousWakeup.Task.IsCompleted, "Throwing follow-up delivery left a stale pending expectation on the receiver.");
        Assert.Equal(2, thread.Messages.Count);
        Assert.Equal(CollabThreadStatus.Active, thread.Status);
    }

    [AvaloniaFact, FreshApp]
    public async Task Reply_thread_preserves_success_await_events_and_wake_disabled_paths()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = Fixture.Create("octopus-issue42-paths-");

        // 1. Success path: wake enabled, no awaiter, receiver accepts.
        var (_, successThread) = await f.DispatchAsync(ct);
        var successReply = await f.ReplyAsync(successThread, "done ok", "agreed", ct);
        Assert.Null(successReply["isError"]);
        var successBody = Body(successReply);
        Assert.True(successBody["recorded"]?.GetValue<bool>());
        Assert.Equal("delivered", successBody["delivery"]?.GetValue<string>());
        Assert.Equal(f.Parent.ItemId, successBody["delivered_to"]?.GetValue<string>());
        Assert.Equal(1, Volatile.Read(ref f.Parent.DeliveryAttempts));

        // 2. await_events path: receiver is actively awaiting; no wakeup prompt is sent even if Receive would refuse.
        var (_, awaitedThread) = await f.DispatchAsync(ct);
        f.Parent.Receive = (_, _, _) => Task.FromResult(false);
        var awaitTask = f.Host.CallToolAsync(
            f.Parent.ItemId,
            "await_events",
            new JsonObject { ["ids"] = new JsonArray(awaitedThread), ["timeout_seconds"] = 10 },
            ct);
        for (var i = 0; i < 80 && f.Host.PendingAwaitCount == 0; i++)
        {
            await Task.Delay(10, ct);
        }
        Assert.Equal(1, f.Host.PendingAwaitCount);

        var awaitedReply = await f.ReplyAsync(awaitedThread, "inline via await", "agreed", ct);
        Assert.Null(awaitedReply["isError"]);
        var awaitedBody = Body(awaitedReply);
        Assert.True(awaitedBody["recorded"]?.GetValue<bool>());
        Assert.Equal("await_events_signaled", awaitedBody["delivery"]?.GetValue<string>());
        Assert.Null(awaitedBody["delivered_to"]);
        Assert.Equal(f.Parent.ItemId, awaitedBody["recipient_session_id"]?.GetValue<string>());
        Assert.Contains("duplicate", awaitedBody["note"]?.GetValue<string>() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Volatile.Read(ref f.Parent.DeliveryAttempts));

        var awaitResult = Body(await awaitTask);
        Assert.Equal("inline via await", awaitResult["threads"]![0]!["latest_message"]?.GetValue<string>());

        // 3. Disabled wakeup policy path: AutoWakeupCaller = false, no awaiter; reply is recorded without waking or claiming delivered_to.
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.Agents.Collab.AutoWakeupCaller = false);
        var (_, disabledThread) = await f.DispatchAsync(ct);
        var disabledReply = await f.ReplyAsync(disabledThread, "recorded only", "agreed", ct);
        Assert.Null(disabledReply["isError"]);
        var disabledBody = Body(disabledReply);
        Assert.True(disabledBody["recorded"]?.GetValue<bool>());
        Assert.Equal("wakeup_disabled", disabledBody["delivery"]?.GetValue<string>());
        Assert.Null(disabledBody["delivered_to"]);
        Assert.Equal(f.Parent.ItemId, disabledBody["recipient_session_id"]?.GetValue<string>());
        var disabledNote = disabledBody["note"]?.GetValue<string>() ?? "";
        Assert.Contains("await_events", disabledNote, StringComparison.Ordinal);
        Assert.Contains(disabledThread, disabledNote, StringComparison.Ordinal);
        Assert.DoesNotContain("read_session", disabledNote, StringComparison.Ordinal);
        Assert.Equal(1, Volatile.Read(ref f.Parent.DeliveryAttempts));

        var laterAwait = Body(await f.Host.CallToolAsync(
            f.Parent.ItemId,
            "await_events",
            new JsonObject { ["ids"] = new JsonArray(disabledThread), ["timeout_seconds"] = 2 },
            ct));
        Assert.Equal("recorded only", laterAwait["threads"]![0]!["latest_message"]?.GetValue<string>());
    }

    private sealed class DeliverySpyBridge(ICollabSessionBridge inner, TaskCompletionSource entered) : ICollabSessionBridge
    {
        public string ItemId => inner.ItemId;
        public string AgentId => inner.AgentId;
        public string DisplayTitle => inner.DisplayTitle;
        public string WorkingDirectory => inner.WorkingDirectory;
        public bool IsBusy => inner.IsBusy;
        public bool IsConnected => inner.IsConnected;
        public string? CurrentStatusDetail => inner.CurrentStatusDetail;

        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default)
        {
            var task = inner.DeliverCollabPromptAsync(promptText, threadId, taskId, cancellationToken);
            entered.TrySetResult();
            return task;
        }

        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId, string? taskId, bool expectReply, CancellationToken cancellationToken)
        {
            var task = inner.DeliverCollabPromptAsync(promptText, threadId, taskId, expectReply, cancellationToken);
            entered.TrySetResult();
            return task;
        }

        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId, string? taskId, bool expectReply, string? deliveryId, Func<bool>? shouldCommit, CancellationToken cancellationToken)
        {
            var task = inner.DeliverCollabPromptAsync(promptText, threadId, taskId, expectReply, deliveryId, shouldCommit, cancellationToken);
            entered.TrySetResult();
            return task;
        }

        public bool SupportsExactDeliveryWithdrawal => inner.SupportsExactDeliveryWithdrawal;

        public int CancelQueuedCollabPrompts(string? threadId = null, string? taskId = null) =>
            inner.CancelQueuedCollabPrompts(threadId, taskId);

        public int CancelQueuedCollabPrompts(string? threadId, string? taskId, string? deliveryId) =>
            inner.CancelQueuedCollabPrompts(threadId, taskId, deliveryId);

        public Task CancelActiveTurnAsync() => inner.CancelActiveTurnAsync();
        public Task CancelActiveTurnAsync(string? threadId, string? taskId) => inner.CancelActiveTurnAsync(threadId, taskId);
        public string ReadRecentSummary(int maxTurns = 5) => inner.ReadRecentSummary(maxTurns);
    }

    private static async Task<(CollabCoordinator Host, Project Project, ProjectItem TargetItem, AgentGuiTabViewModel TargetVm, FakeAcpSession TargetSession, Bridge Peer)> SetUpRealGuiTargetAsync(
        string prefix,
        Func<FakeAcpSession, string, IReadOnlyList<AcpPromptAttachment>?, CancellationToken, Task<AcpPromptResult>> promptHandler)
    {
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(Directory.CreateTempSubdirectory(prefix).FullName);
        var host = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        host.Server.Start();

        var peerItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Peer", Surface = AgentSurface.Gui };
        var targetItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "TargetGui", Surface = AgentSurface.Gui };
        projects.AddItem(project, peerItem, activate: false);
        projects.AddItem(project, targetItem, activate: false);

        var peer = new Bridge(peerItem, project.Path);
        host.RegisterGuiSession(peer);

        FakeAcpSession? session = null;
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = s =>
        {
            session = s;
            s.PromptHandler = promptHandler;
        };

        var tabRegistry = AppHost.Get<ITabContentRegistry>();
        var control = tabRegistry.GetOrCreate(project, targetItem);
        var vm = Assert.IsType<AgentGuiTabViewModel>(control.DataContext);
        await vm.ConnectAsync();
        Frames.Pump();
        Assert.NotNull(session);
        return (host, project, targetItem, vm, session!, peer);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, int maxSteps = 120)
    {
        for (var i = 0; i < maxSteps && !condition(); i++)
        {
            Frames.Pump();
            await Task.Delay(25, ct);
        }
        Assert.True(condition());
    }

    [AvaloniaFact, FreshApp]
    public async Task Overlapping_failed_deliveries_to_same_receiver_do_not_revive_failed_expectation()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = Fixture.Create("octopus-issue42-overlap-fail-");
        var (_, threadA) = await f.DispatchAsync(ct);
        var (_, threadB) = await f.DispatchAsync(ct);

        var aEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        f.Parent.Receive = (_, threadId, _) =>
        {
            if (threadId == threadA)
            {
                aEntered.TrySetResult();
                return releaseA.Task;
            }
            if (threadId == threadB)
            {
                bEntered.TrySetResult();
                return releaseB.Task;
            }
            return Task.FromResult(true);
        };

        var replyATask = f.ReplyAsync(threadA, "question A", "continue", ct);
        await aEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        var replyBTask = f.ReplyAsync(threadB, "question B", "continue", ct);
        await bEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // Fail A first while B is still in flight, then fail B.
        releaseA.TrySetResult(false);
        var replyA = await replyATask;
        Assert.Equal("failed", Body(replyA)["delivery"]?.GetValue<string>());

        releaseB.TrySetResult(false);
        var replyB = await replyBTask;
        Assert.Equal("failed", Body(replyB)["delivery"]?.GetValue<string>());

        var spuriousWakeup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Worker.Receive = (_, _, _) =>
        {
            spuriousWakeup.TrySetResult();
            return Task.FromResult(true);
        };

        f.Host.NotifyTurnCompleted(f.Parent.ItemId, "unrelated ordinary parent turn");
        await Task.WhenAny(spuriousWakeup.Task, Task.Delay(150, ct));

        Assert.False(spuriousWakeup.Task.IsCompleted, "Overlapping failed deliveries revived a failed pending expectation.");
        Assert.Equal(2, f.Host.FindThread(threadA)!.Messages.Count);
        Assert.Equal(2, f.Host.FindThread(threadB)!.Messages.Count);
    }

    [AvaloniaFact, FreshApp]
    public async Task Explicitly_settled_predecessor_is_not_revived_when_overlapping_delivery_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = Fixture.Create("octopus-issue42-overlap-settled-");
        var (_, threadA) = await f.DispatchAsync(ct);
        var (_, threadB) = await f.DispatchAsync(ct);

        // Delivery A succeeds so parent owes a follow-up on threadA.
        var replyA = await f.ReplyAsync(threadA, "need confirmation on A", "continue", ct);
        Assert.Equal("delivered", Body(replyA)["delivery"]?.GetValue<string>());

        // Delivery B overlaps in flight on the same receiver.
        var bEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Parent.Receive = (_, threadId, _) =>
        {
            if (threadId == threadB)
            {
                bEntered.TrySetResult();
                return releaseB.Task;
            }
            return Task.FromResult(true);
        };

        var replyBTask = f.ReplyAsync(threadB, "need confirmation on B", "continue", ct);
        await bEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // While B is in flight, parent explicitly answers threadA.
        var explicitAnswerA = await f.Host.CallToolAsync(
            f.Parent.ItemId,
            "reply_thread",
            new JsonObject { ["thread_id"] = threadA, ["message"] = "explicit answer A", ["status"] = "agreed" },
            ct);
        Assert.Null(explicitAnswerA["isError"]);
        Assert.Equal(CollabThreadStatus.Agreed, f.Host.FindThread(threadA)!.Status);

        // Now B fails. Rolling back B must not revive A's settled expectation.
        releaseB.TrySetResult(false);
        var replyB = await replyBTask;
        Assert.Equal("failed", Body(replyB)["delivery"]?.GetValue<string>());

        var spuriousWakeup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Worker.Receive = (_, _, _) =>
        {
            spuriousWakeup.TrySetResult();
            return Task.FromResult(true);
        };

        f.Host.NotifyTurnCompleted(f.Parent.ItemId, "unrelated ordinary turn after B failed");
        await Task.WhenAny(spuriousWakeup.Task, Task.Delay(150, ct));

        Assert.False(spuriousWakeup.Task.IsCompleted, "Failed overlapping delivery revived an explicitly settled predecessor.");
        Assert.Equal(3, f.Host.FindThread(threadA)!.Messages.Count);
        Assert.Equal("explicit answer A", f.Host.FindThread(threadA)!.Messages[^1].Text);
        Assert.Equal(2, f.Host.FindThread(threadB)!.Messages.Count);
    }

    [AvaloniaFact, FreshApp]
    public async Task Reply_thread_ignores_cancelled_await_events_waiter_still_registered_and_wakes_receiver()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = Fixture.Create("octopus-issue42-cancel-waiter-wake-");
        var (_, threadId) = await f.DispatchAsync(ct);

        using var awaitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var awaitTask = f.Host.CallToolAsync(
            f.Parent.ItemId,
            "await_events",
            new JsonObject { ["ids"] = new JsonArray(threadId), ["timeout_seconds"] = 10 },
            awaitCts.Token);

        for (var i = 0; i < 80 && f.Host.PendingAwaitCount == 0; i++)
        {
            await Task.Delay(10, ct);
        }
        Assert.Equal(1, f.Host.PendingAwaitCount);

        // Register AFTER await_events so LIFO cancellation runs this callback while awaitCts is already cancelled
        // and the waiter is still registered in the ledger (before await_events' finally can remove it).
        Task<JsonObject>? replyTask = null;
        using var reg = awaitCts.Token.Register(() =>
        {
            replyTask = f.ReplyAsync(threadId, "reply during cancel race", "agreed", ct);
        });

        awaitCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => awaitTask);
        Assert.NotNull(replyTask);
        var reply = await replyTask!;
        Assert.Null(reply["isError"]);

        var body = Body(reply);
        Assert.True(body["recorded"]?.GetValue<bool>());
        Assert.Equal("delivered", body["delivery"]?.GetValue<string>());
        Assert.Equal(f.Parent.ItemId, body["delivered_to"]?.GetValue<string>());
        Assert.Equal(1, Volatile.Read(ref f.Parent.DeliveryAttempts));
    }

    [AvaloniaFact, FreshApp]
    public async Task Reply_thread_with_cancelled_await_events_waiter_and_refused_wakeup_does_not_claim_delivered_to()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = Fixture.Create("octopus-issue42-cancel-waiter-refuse-");
        var (_, threadId) = await f.DispatchAsync(ct);

        f.Parent.Receive = (_, _, _) => Task.FromResult(false);

        using var awaitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var awaitTask = f.Host.CallToolAsync(
            f.Parent.ItemId,
            "await_events",
            new JsonObject { ["ids"] = new JsonArray(threadId), ["timeout_seconds"] = 10 },
            awaitCts.Token);

        for (var i = 0; i < 80 && f.Host.PendingAwaitCount == 0; i++)
        {
            await Task.Delay(10, ct);
        }
        Assert.Equal(1, f.Host.PendingAwaitCount);

        Task<JsonObject>? replyTask = null;
        using var reg = awaitCts.Token.Register(() =>
        {
            replyTask = f.ReplyAsync(threadId, "reply during cancel refuse race", "agreed", ct);
        });

        awaitCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => awaitTask);
        Assert.NotNull(replyTask);
        var reply = await replyTask!;
        Assert.Null(reply["isError"]);

        var body = Body(reply);
        Assert.True(body["recorded"]?.GetValue<bool>());
        Assert.Equal("failed", body["delivery"]?.GetValue<string>());
        Assert.Equal("wakeup_not_accepted", body["delivery_error"]?.GetValue<string>());
        Assert.Null(body["delivered_to"]);
        Assert.Equal(f.Parent.ItemId, body["recipient_session_id"]?.GetValue<string>());
        Assert.Equal(1, Volatile.Read(ref f.Parent.DeliveryAttempts));

        // The cancelled await_events did not mark the reply seen, so a later await_events still retrieves it.
        var laterAwait = Body(await f.Host.CallToolAsync(
            f.Parent.ItemId,
            "await_events",
            new JsonObject { ["ids"] = new JsonArray(threadId), ["timeout_seconds"] = 2 },
            ct));
        Assert.Equal("reply during cancel refuse race", laterAwait["threads"]![0]!["latest_message"]?.GetValue<string>());
    }

    [AvaloniaFact, FreshApp]
    public async Task Await_events_cancelled_before_snapshot_leaves_recorded_reply_unseen()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = Fixture.Create("octopus-issue42-await-pre-cancel-");
        var (_, threadId) = await f.DispatchAsync(ct);

        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.Agents.Collab.AutoWakeupCaller = false);

        var reply = await f.ReplyAsync(threadId, "unseen recorded reply", "agreed", ct);
        Assert.Null(reply["isError"]);

        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Host.CallToolAsync(
            f.Parent.ItemId,
            "await_events",
            new JsonObject { ["ids"] = new JsonArray(threadId), ["timeout_seconds"] = 2 },
            alreadyCancelled.Token));

        var laterAwait = Body(await f.Host.CallToolAsync(
            f.Parent.ItemId,
            "await_events",
            new JsonObject { ["ids"] = new JsonArray(threadId), ["timeout_seconds"] = 2 },
            ct));
        Assert.True(laterAwait["threads"]![0]!["new"]?.GetValue<bool>());
        Assert.Equal("unseen recorded reply", laterAwait["threads"]![0]!["latest_message"]?.GetValue<string>());
    }

    [AvaloniaFact, FreshApp]
    public async Task Gui_cancelled_reply_follow_up_before_ui_dispatch_never_starts_or_queues_prompt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, parentItem, parentVm, parentSession, worker) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-gui-cancel-followup-",
            (s, _, _, _) =>
            {
                s.Emit(new AcpTextDeltaEvent("assistant", "unexpected gui follow-up answer", "msg-unwanted"));
                return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
            });

        var dispatch = await host.CallToolAsync(
            parentItem.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.ItemId, ["goal"] = "Ask a follow-up question" },
            ct);
        Assert.Null(dispatch["isError"]);
        var threadId = Body(dispatch)["thread_id"]!.GetValue<string>();

        var deliveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.RegisterGuiSession(new DeliverySpyBridge(parentVm, deliveryEntered));

        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var replyTask = Task.Run(() => host.CallToolAsync(
            worker.ItemId,
            "reply_thread",
            new JsonObject { ["thread_id"] = threadId, ["message"] = "need clarification", ["status"] = "continue" },
            callCts.Token));

        // Block the UI thread (do not pump) until the background delivery posts its UI mutation and awaits it.
        Assert.True(deliveryEntered.Task.Wait(TimeSpan.FromSeconds(5)));

        callCts.Cancel();
        Assert.True(SpinWait.SpinUntil(() => replyTask.IsCompleted, TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replyTask);

        // Now pump the UI queue: the stale posted UI action must not enqueue or start a turn.
        Frames.Pump();
        await Task.Delay(50, ct);
        Frames.Pump();

        Assert.Empty(parentSession.SentPrompts);
        Assert.Empty(parentVm.QueuedPrompts);
        Assert.False(parentVm.IsTurnActive);

        var thread = host.FindThread(threadId)!;
        Assert.Equal(2, thread.Messages.Count);
        Assert.Equal("need clarification", thread.Messages[^1].Text);
    }

    [AvaloniaFact, FreshApp]
    public async Task Gui_cancelled_existing_session_delegation_before_ui_dispatch_never_starts_or_queues_prompt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, workerItem, workerVm, workerSession, parent) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-gui-cancel-delegate-",
            (s, _, _, _) =>
            {
                s.Emit(new AcpTextDeltaEvent("assistant", "unexpected gui delegate answer", "msg-unwanted"));
                return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
            });

        var deliveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.RegisterGuiSession(new DeliverySpyBridge(workerVm, deliveryEntered));

        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delegateTask = Task.Run(() => host.CallToolAsync(
            parent.ItemId,
            "delegate_task",
            new JsonObject { ["target"] = workerItem.Id, ["goal"] = "Should never start after cancel" },
            callCts.Token));

        // Block the UI thread (do not pump) until the background delivery posts its UI mutation and awaits it.
        Assert.True(deliveryEntered.Task.Wait(TimeSpan.FromSeconds(5)));

        callCts.Cancel();
        Assert.True(SpinWait.SpinUntil(() => delegateTask.IsCompleted, TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delegateTask);

        // Task and thread were rolled back; pumping the UI queue must not start or queue the cancelled prompt.
        Assert.Empty(host.GetTasksForItem(parent.ItemId));
        Assert.Empty(host.GetThreadsForItem(parent.ItemId));

        Frames.Pump();
        await Task.Delay(50, ct);
        Frames.Pump();

        Assert.Empty(workerSession.SentPrompts);
        Assert.Empty(workerVm.QueuedPrompts);
        Assert.False(workerVm.IsTurnActive);
    }

    [AvaloniaFact, FreshApp]
    public async Task Gui_cancelled_delivery_while_turn_active_never_enqueues_or_runs_after_active_turn()
    {
        var ct = TestContext.Current.CancellationToken;
        var turn1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTurn1 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (host, _, workerItem, workerVm, workerSession, parent) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-gui-cancel-busy-",
            async (s, _, _, token) =>
            {
                turn1Started.TrySetResult();
                var text = await releaseTurn1.Task.WaitAsync(token);
                s.Emit(new AcpTextDeltaEvent("assistant", text, "msg-active-1"));
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        // Start active Turn 1 on the GUI worker so a second delivery would take the EnqueuePrompt branch.
        var activeDispatch = await host.CallToolAsync(
            parent.ItemId,
            "delegate_task",
            new JsonObject { ["target"] = workerItem.Id, ["goal"] = "Active Job 1" },
            ct);
        Assert.Null(activeDispatch["isError"]);
        var activeTaskId = Body(activeDispatch)["task_id"]!.GetValue<string>();
        await WaitUntilAsync(() => turn1Started.Task.IsCompleted && workerVm.IsTurnActive, ct);

        var deliveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.RegisterGuiSession(new DeliverySpyBridge(workerVm, deliveryEntered));

        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var secondDelegateTask = Task.Run(() => host.CallToolAsync(
            parent.ItemId,
            "delegate_task",
            new JsonObject { ["target"] = workerItem.Id, ["goal"] = "Cancelled Queued Job 2" },
            callCts.Token));

        // Hold UI thread (no pump) until the second background delivery posts its UI queue mutation.
        Assert.True(deliveryEntered.Task.Wait(TimeSpan.FromSeconds(5)));

        callCts.Cancel();
        Assert.True(SpinWait.SpinUntil(() => secondDelegateTask.IsCompleted, TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondDelegateTask);

        // Pump the UI queue: cancelled delivery must not enqueue behind Turn 1 or affect Turn 1.
        Frames.Pump();
        await Task.Delay(50, ct);
        Frames.Pump();

        Assert.Empty(workerVm.QueuedPrompts);
        Assert.Single(workerSession.SentPrompts);
        Assert.Single(host.GetTasksForItem(parent.ItemId), t => t.TaskId == activeTaskId && t.Status == CollabTaskStatus.Running);

        // Complete Turn 1: no zombie prompt runs afterward, and Turn 1 settles cleanly.
        releaseTurn1.TrySetResult("Active Job 1 finished");
        await WaitUntilAsync(() => host.GetTasksForItem(parent.ItemId).Single(t => t.TaskId == activeTaskId).Status == CollabTaskStatus.Completed, ct);
        await WaitUntilAsync(() => !workerVm.IsTurnActive, ct);
        Assert.Single(workerSession.SentPrompts);
        Assert.Empty(workerVm.QueuedPrompts);
        Assert.Equal("Active Job 1 finished", host.GetTasksForItem(parent.ItemId).Single(t => t.TaskId == activeTaskId).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Gui_already_accepted_follow_up_and_delegation_are_not_discarded_when_cancellation_races_commit()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1. Existing-session delegation: cancellation fires inside PromptHandler right after GUI commits the prompt.
        CancellationTokenSource? activeDeliveryCts = null;
        var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTurn = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (host, _, workerItem, workerVm, workerSession, parent) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-gui-commit-wins-",
            async (s, _, _, token) =>
            {
                // Synchronously cancels the caller's delivery token before DeliverCollabPromptAsync sets tcs result.
                Volatile.Read(ref activeDeliveryCts)?.Cancel();
                turnStarted.TrySetResult();
                var text = await releaseTurn.Task.WaitAsync(token);
                s.Emit(new AcpTextDeltaEvent("assistant", text, "msg-committed"));
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        using (var delegateCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            Volatile.Write(ref activeDeliveryCts, delegateCts);
            var delegateTask = Task.Run(() => host.CallToolAsync(
                parent.ItemId,
                "delegate_task",
                new JsonObject { ["target"] = workerItem.Id, ["goal"] = "Committed before cancel" },
                delegateCts.Token));

            await WaitUntilAsync(() => turnStarted.Task.IsCompleted, ct);
            var delegateResult = await delegateTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Null(delegateResult["isError"]);

            var taskId = Body(delegateResult)["task_id"]!.GetValue<string>();
            Assert.Single(workerSession.SentPrompts);
            Assert.Single(host.GetTasksForItem(parent.ItemId), t => t.TaskId == taskId && t.Status == CollabTaskStatus.Running);

            releaseTurn.TrySetResult("accepted delegation finished");
            await WaitUntilAsync(() => host.GetTasksForItem(parent.ItemId).Single(t => t.TaskId == taskId).Status == CollabTaskStatus.Completed, ct);
            Assert.Equal("accepted delegation finished", host.GetTasksForItem(parent.ItemId).Single(t => t.TaskId == taskId).ResultSummary);
            await WaitUntilAsync(() => !workerVm.IsTurnActive, ct);
        }

        // 2. Follow-up reply_thread: cancellation fires inside PromptHandler right after GUI commits the follow-up prompt.
        var followUpTurnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFollowUp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource? followUpCtsHolder = null;

        var (host2, _, parentItem2, parentVm2, parentSession2, worker2) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-gui-commit-followup-",
            async (s, _, _, token) =>
            {
                Volatile.Read(ref followUpCtsHolder)?.Cancel();
                followUpTurnStarted.TrySetResult();
                var text = await releaseFollowUp.Task.WaitAsync(token);
                s.Emit(new AcpTextDeltaEvent("assistant", text, "msg-followup-committed"));
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        var dispatch2 = await host2.CallToolAsync(
            parentItem2.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker2.ItemId, ["goal"] = "Ask follow-up" },
            ct);
        Assert.Null(dispatch2["isError"]);
        var thread2 = Body(dispatch2)["thread_id"]!.GetValue<string>();

        var workerWoken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker2.Receive = (_, _, _) =>
        {
            workerWoken.TrySetResult();
            return Task.FromResult(true);
        };

        using (var followUpCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            Volatile.Write(ref followUpCtsHolder, followUpCts);
            var replyTask = Task.Run(() => host2.CallToolAsync(
                worker2.ItemId,
                "reply_thread",
                new JsonObject { ["thread_id"] = thread2, ["message"] = "confirm schema?", ["status"] = "continue" },
                followUpCts.Token));

            await WaitUntilAsync(() => followUpTurnStarted.Task.IsCompleted, ct);
            var replyResult = await replyTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Null(replyResult["isError"]);

            var replyBody = Body(replyResult);
            Assert.Equal("delivered", replyBody["delivery"]?.GetValue<string>());
            Assert.Equal(parentItem2.Id, replyBody["delivered_to"]?.GetValue<string>());
            Assert.Single(parentSession2.SentPrompts);

            releaseFollowUp.TrySetResult("schema confirmed");
            await workerWoken.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            var updatedThread = host2.FindThread(thread2)!;
            Assert.Equal(3, updatedThread.Messages.Count);
            Assert.Equal("schema confirmed", updatedThread.Messages[^1].Text);
            await WaitUntilAsync(() => !parentVm2.IsTurnActive, ct);
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Reply_thread_when_receiver_throws_returns_bounded_generic_error_without_copying_exception_text()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = Fixture.Create("octopus-issue42-bounded-error-");
        var (_, threadId) = await f.DispatchAsync(ct);

        const string syntheticDetail = "synthetic-harmless-diagnostic:/local/example/workspace/path.txt";
        var longMessage = syntheticDetail + " " + new string('z', 600);
        f.Parent.Receive = (_, _, _) => Task.FromException<bool>(new InvalidOperationException(longMessage));

        var reply = await f.ReplyAsync(threadId, "deliverable with bounded error", "agreed", ct);
        Assert.Null(reply["isError"]);

        var rawJson = reply.ToJsonString();
        Assert.DoesNotContain(syntheticDetail, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('z', 64), rawJson, StringComparison.Ordinal);

        var body = Body(reply);
        Assert.True(body["recorded"]?.GetValue<bool>());
        Assert.Equal("failed", body["delivery"]?.GetValue<string>());
        Assert.Equal("wakeup_delivery_error", body["delivery_error"]?.GetValue<string>());
        Assert.Null(body["delivered_to"]);
        Assert.Equal(f.Parent.ItemId, body["recipient_session_id"]?.GetValue<string>());

        var note = body["note"]?.GetValue<string>() ?? "";
        Assert.InRange(note.Length, 1, 256);
        Assert.Contains("duplicate", note, StringComparison.OrdinalIgnoreCase);

        // Refused delivery also exposes a bounded generic delivery_error code.
        var (_, refusedThread) = await f.DispatchAsync(ct);
        f.Parent.Receive = (_, _, _) => Task.FromResult(false);
        var refusedReply = await f.ReplyAsync(refusedThread, "deliverable refused", "agreed", ct);
        var refusedBody = Body(refusedReply);
        Assert.Equal("failed", refusedBody["delivery"]?.GetValue<string>());
        Assert.Equal("wakeup_not_accepted", refusedBody["delivery_error"]?.GetValue<string>());
        Assert.Null(refusedBody["delivered_to"]);
    }

    [AvaloniaFact, FreshApp]
    public async Task Reply_thread_runs_normal_wake_when_waiter_cancels_before_signal()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = Fixture.Create("octopus-issue42-cancel-before-signal-");
        var ledger = (CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(f.Host)!;

        async Task<JsonObject> RunCancelBeforeSignalAsync(string message)
        {
            var (_, tid) = await f.DispatchAsync(ct);
            using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var awaited = f.Host.AwaitEventsAsync(f.Parent.ItemId, [tid], 30, disconnect.Token);

            // Deterministic scheduling fence: cancel the real waiter right before SignalWaiters reaches it.
            var fence = new TaskCompletionSource<bool>();
            var unrelated = new EventWaiter("unrelated", [], fence, CancellationToken.None);
            var fenceRan = fence.Task.ContinueWith(
                _ => disconnect.Cancel(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            EventWaiter target;
            lock (ledger.Gate)
            {
                target = Assert.Single(ledger.Waiters, w => w.CallerItemId == f.Parent.ItemId);
                ledger.Waiters.Insert(0, unrelated);
            }

            try
            {
                var receipt = Body(await f.ReplyAsync(tid, message, "agreed", ct));
                await fenceRan.WaitAsync(TimeSpan.FromSeconds(2), ct);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => awaited);
                Assert.False(target.Tcs.Task.IsCompleted);

                var next = Body(await f.Host.AwaitEventsAsync(f.Parent.ItemId, [tid], 1, ct));
                Assert.True(next["threads"]![0]!["new"]!.GetValue<bool>());
                Assert.Equal(message, next["threads"]![0]!["latest_message"]!.GetValue<string>());
                return receipt;
            }
            finally
            {
                lock (ledger.Gate) ledger.Waiters.Remove(unrelated);
            }
        }

        // Case 1: cancellation wins before signal, receiver accepts normal wakeup.
        var acceptedReceipt = await RunCancelBeforeSignalAsync("persisted message accepted");
        Assert.Equal("delivered", acceptedReceipt["delivery"]?.GetValue<string>());
        Assert.Equal(f.Parent.ItemId, acceptedReceipt["delivered_to"]?.GetValue<string>());
        Assert.Equal(1, Volatile.Read(ref f.Parent.DeliveryAttempts));

        // Case 2: cancellation wins before signal, receiver refuses normal wakeup (never falsely claims await_events_signaled).
        f.Parent.Receive = (_, _, _) => Task.FromResult(false);
        var refusedReceipt = await RunCancelBeforeSignalAsync("persisted message refused");
        Assert.Equal("failed", refusedReceipt["delivery"]?.GetValue<string>());
        Assert.Equal("wakeup_not_accepted", refusedReceipt["delivery_error"]?.GetValue<string>());
        Assert.Null(refusedReceipt["delivered_to"]);
        Assert.Equal(2, Volatile.Read(ref f.Parent.DeliveryAttempts));
    }

    [AvaloniaFact, FreshApp]
    public async Task Consuming_recorded_reply_before_delivery_skips_stale_gui_wakeup_without_affecting_user_turns()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, caller, callerVm, session, worker) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-stale-gui-",
            (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));

        // 1. Consumed synchronously after ledger write (during Changed), before DeliverEnvelopeAsync runs.
        var dispatch1 = Body(await host.CallToolAsync(
            caller.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.ItemId, ["goal"] = "answer 1" },
            ct));
        var tid1 = dispatch1["thread_id"]!.GetValue<string>();

        Task<JsonObject>? consumed1 = null;
        void OnChange(string? id)
        {
            if (id != worker.ItemId) return;
            host.Changed -= OnChange;
            consumed1 = host.AwaitEventsAsync(caller.Id, [tid1], 1, ct);
        }
        host.Changed += OnChange;

        var receipt1 = await host.ReplyThreadAsync(worker.ItemId, tid1, "already consumed before dispatch", "agreed", ct);
        Assert.Null(receipt1["isError"]);
        var body1 = Body(receipt1);
        Assert.Equal("await_events_consumed", body1["delivery"]?.GetValue<string>());
        Assert.Null(body1["delivered_to"]);
        Assert.Equal(caller.Id, body1["recipient_session_id"]?.GetValue<string>());

        Assert.NotNull(consumed1);
        var snapshot1 = Body(await consumed1);
        Assert.Equal("already consumed before dispatch", snapshot1["threads"]![0]!["latest_message"]?.GetValue<string>());

        Frames.Pump();
        Assert.Empty(session.SentPrompts);
        Assert.Empty(callerVm.QueuedPrompts);

        // 2. Consumed after GUI delivery posted to UI thread, before the UI thread commits the prompt.
        var dispatch2 = Body(await host.CallToolAsync(
            caller.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.ItemId, ["goal"] = "answer 2" },
            ct));
        var tid2 = dispatch2["thread_id"]!.GetValue<string>();

        var deliveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.RegisterGuiSession(new DeliverySpyBridge(callerVm, deliveryEntered));

        var reply2Task = Task.Run(() => host.ReplyThreadAsync(worker.ItemId, tid2, "already consumed before ui commit", "agreed", ct), ct);
        Assert.True(deliveryEntered.Task.Wait(TimeSpan.FromSeconds(5)));

        // Consume via await_events while the UI thread is still held before SetUi executes.
        var snapshot2 = Body(await host.AwaitEventsAsync(caller.Id, [tid2], 1, ct));
        Assert.Equal("already consumed before ui commit", snapshot2["threads"]![0]!["latest_message"]?.GetValue<string>());

        Frames.Pump();
        var receipt2 = await reply2Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Null(receipt2["isError"]);
        var body2 = Body(receipt2);
        Assert.Equal("await_events_consumed", body2["delivery"]?.GetValue<string>());
        Assert.Null(body2["delivered_to"]);
        Assert.Empty(session.SentPrompts);
        Assert.Empty(callerVm.QueuedPrompts);

        // 3. Interactive user turns remain completely unaffected, and unconsumed replies still wake the GUI.
        host.RegisterGuiSession(callerVm);
        callerVm.InputText = "interactive user prompt";
        await callerVm.SendPromptAsync();
        await WaitUntilAsync(() => session.SentPrompts.Count == 1 && !callerVm.IsTurnActive, ct);
        Assert.Equal("interactive user prompt", session.SentPrompts[0].Text);

        var dispatch3 = Body(await host.CallToolAsync(
            caller.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.ItemId, ["goal"] = "answer 3" },
            ct));
        var tid3 = dispatch3["thread_id"]!.GetValue<string>();

        var receipt3 = await host.ReplyThreadAsync(worker.ItemId, tid3, "fresh unconsumed reply", "agreed", ct);
        Assert.Null(receipt3["isError"]);
        Assert.Equal("delivered", Body(receipt3)["delivery"]?.GetValue<string>());
        await WaitUntilAsync(() => session.SentPrompts.Count == 2 && !callerVm.IsTurnActive, ct);
        Assert.Contains("fresh unconsumed reply", session.SentPrompts[1].Text, StringComparison.Ordinal);
    }

    private sealed class ReviewCleanupFence(ICollabSessionBridge target, TaskCompletionSource atCleanup, ManualResetEventSlim release) : ICollabSessionBridge
    {
        public string ItemId => target.ItemId;
        public string AgentId => target.AgentId;
        public string DisplayTitle => target.DisplayTitle;
        public string WorkingDirectory => target.WorkingDirectory;
        public bool IsBusy => target.IsBusy;
        public bool IsConnected => target.IsConnected;
        public string? CurrentStatusDetail => target.CurrentStatusDetail;
        public bool TracksActiveTurn => target.TracksActiveTurn;
        public bool HasActiveTurn => target.HasActiveTurn;
        public string? ActiveCollabThreadId => target.ActiveCollabThreadId;
        public string? ActiveCollabTaskId => target.ActiveCollabTaskId;
        public string? ActiveCollabDeliveryId => target.ActiveCollabDeliveryId;
        public bool SupportsExactDeliveryWithdrawal => target.SupportsExactDeliveryWithdrawal;
        public string ReadRecentSummary(int maxTurns = 5) => target.ReadRecentSummary(maxTurns);
        public bool TryGetActiveCollabTurn(out string? threadId, out string? taskId, out string? deliveryId) =>
            target.TryGetActiveCollabTurn(out threadId, out taskId, out deliveryId);
        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default) =>
            target.DeliverCollabPromptAsync(promptText, threadId, taskId, cancellationToken);
        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId, string? taskId, bool expectReply, string? deliveryId, Func<bool>? shouldCommit, CancellationToken ct) =>
            target.DeliverCollabPromptAsync(promptText, threadId, taskId, expectReply, deliveryId, shouldCommit, ct);
        public int CancelQueuedCollabPrompts(string? threadId = null, string? taskId = null) =>
            target.CancelQueuedCollabPrompts(threadId, taskId);
        public int CancelQueuedCollabPrompts(string? threadId, string? taskId, string? deliveryId)
        {
            atCleanup.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return target.CancelQueuedCollabPrompts(threadId, taskId, deliveryId);
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task ReviewSnapshotCleanupMustKeepNewerSameThreadWakeup()
    {
        var ct = TestContext.Current.CancellationToken;
        var releaseTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var atCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCleanup = new ManualResetEventSlim(false);
        var (host, _, caller, vm, session, worker) = await SetUpRealGuiTargetAsync(
            "review-cleanup-",
            async (_, _, _, token) =>
            {
                await releaseTurn.Task.WaitAsync(token);
                return new AcpPromptResult { StopReason = "end_turn" };
            });
        vm.InputText = "active interactive turn";
        var manual = vm.SendPromptAsync();
        try
        {
            var dispatch = Body(await host.CallToolAsync(caller.Id, "delegate_task", new JsonObject { ["target"] = worker.ItemId, ["goal"] = "answer" }, ct));
            var thread = dispatch["thread_id"]!.GetValue<string>();
            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(worker.ItemId, thread, "first update", "continue", ct))["delivery"]!.GetValue<string>());
            var first = Assert.Single(vm.QueuedPrompts);
            Assert.False(string.IsNullOrEmpty(first.CollabDeliveryId));
            host.RegisterGuiSession(new ReviewCleanupFence(vm, atCleanup, releaseCleanup));
            var snapshot = Task.Run(() => host.AwaitEventsAsync(caller.Id, [thread], 1, ct), ct);
            await atCleanup.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(worker.ItemId, thread, "second update", "continue", ct))["delivery"]!.GetValue<string>());
            var latest = vm.QueuedPrompts.Last();
            Assert.NotEqual(first.CollabDeliveryId, latest.CollabDeliveryId);
            Assert.True(latest.ShouldDispatch!());
            releaseCleanup.Set();
            Assert.Equal("first update", Body(await snapshot)["threads"]![0]!["latest_message"]!.GetValue<string>());
            releaseTurn.TrySetResult();
            await manual;
            Assert.Contains(session.SentPrompts, p => p.Text.Contains("second update", StringComparison.Ordinal));
        }
        finally
        {
            releaseCleanup.Set();
            releaseTurn.TrySetResult();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Snapshot_cleanup_removes_consumed_wakeup_and_pending_expectation_while_keeping_later_expects_reply_and_user_input()
    {
        var ct = TestContext.Current.CancellationToken;
        var releaseTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var atCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCleanup = new ManualResetEventSlim(false);
        var turnIndex = 0;

        var (host, _, caller, vm, session, worker) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-snapshot-pending-",
            async (s, text, _, token) =>
            {
                var idx = Interlocked.Increment(ref turnIndex);
                if (idx == 1)
                {
                    await releaseTurn.Task.WaitAsync(token);
                    return new AcpPromptResult { StopReason = "end_turn" };
                }
                if (text.Contains("second update", StringComparison.Ordinal))
                {
                    s.Emit(new AcpTextDeltaEvent("assistant", "reply to second update", "msg-reply-2"));
                }
                else if (text.Contains("follow-up question expecting reply", StringComparison.Ordinal))
                {
                    s.Emit(new AcpTextDeltaEvent("assistant", "reply to direct send_message", "msg-reply-3"));
                }
                else
                {
                    s.Emit(new AcpTextDeltaEvent("assistant", "normal user answer", "msg-user"));
                }
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        vm.InputText = "active interactive turn";
        var manual = vm.SendPromptAsync();
        try
        {
            var dispatch = Body(await host.CallToolAsync(
                caller.Id,
                "delegate_task",
                new JsonObject { ["target"] = worker.ItemId, ["goal"] = "answer" },
                ct));
            var thread = dispatch["thread_id"]!.GetValue<string>();

            // Queue a normal user prompt alongside the first reply wakeup.
            vm.InputText = "queued normal user prompt";
            await vm.SendPromptAsync();

            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(worker.ItemId, thread, "first update", "continue", ct))["delivery"]!.GetValue<string>());
            Assert.Equal(2, vm.QueuedPrompts.Count);

            // Pause snapshot cleanup before UI queue mutation runs.
            host.RegisterGuiSession(new ReviewCleanupFence(vm, atCleanup, releaseCleanup));
            var snapshot = Task.Run(() => host.AwaitEventsAsync(caller.Id, [thread], 1, ct), ct);
            await atCleanup.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            // While cleanup is paused, deliver a newer wakeup AND a direct expects-reply send_message on the same thread.
            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(worker.ItemId, thread, "second update", "continue", ct))["delivery"]!.GetValue<string>());
            var sendMsg = await host.CallToolAsync(
                worker.ItemId,
                "send_message",
                new JsonObject
                {
                    ["target_session_id"] = caller.Id,
                    ["thread_id"] = thread,
                    ["message"] = "follow-up question expecting reply",
                    ["expect_reply"] = true,
                },
                ct);
            Assert.Null(sendMsg["isError"]);

            releaseCleanup.Set();
            Assert.Equal("first update", Body(await snapshot)["threads"]![0]!["latest_message"]!.GetValue<string>());

            // Snapshot cleanup must have removed ONLY the consumed "first update" wakeup, leaving user prompt + second wakeup + send_message.
            Assert.Equal(3, vm.QueuedPrompts.Count);
            Assert.DoesNotContain(vm.QueuedPrompts, q => q.Text.Contains("first update", StringComparison.Ordinal));
            Assert.Contains(vm.QueuedPrompts, q => q.Text == "queued normal user prompt");
            Assert.Contains(vm.QueuedPrompts, q => q.Text.Contains("second update", StringComparison.Ordinal));
            Assert.Contains(vm.QueuedPrompts, q => q.Text.Contains("follow-up question expecting reply", StringComparison.Ordinal));

            // Drain all turns and verify both later expects-reply turns produce implicit replies and no stale pending remains.
            releaseTurn.TrySetResult();
            await manual;
            await WaitUntilAsync(() => !vm.IsTurnActive && vm.QueuedPrompts.Count == 0 && session.SentPrompts.Count == 4, ct);

            Assert.DoesNotContain(session.SentPrompts, p => p.Text.Contains("first update", StringComparison.Ordinal));
            Assert.Contains(session.SentPrompts, p => p.Text == "queued normal user prompt");
            Assert.Contains(session.SentPrompts, p => p.Text.Contains("second update", StringComparison.Ordinal));
            Assert.Contains(session.SentPrompts, p => p.Text.Contains("follow-up question expecting reply", StringComparison.Ordinal));

            await WaitUntilAsync(() =>
                host.FindThread(thread)!.Messages.Any(m => m.Text.Contains("reply to second update", StringComparison.Ordinal))
                && host.FindThread(thread)!.Messages.Any(m => m.Text.Contains("reply to direct send_message", StringComparison.Ordinal)), ct);

            var ledger = (CollabLedger)typeof(CollabCoordinator)
                .GetField("_ledger", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(host)!;
            lock (ledger.Gate)
            {
                Assert.False(
                    ledger.Pending.TryGetValue(caller.Id, out var remaining) && remaining.Any(p => !p.SettledExplicitly),
                    "Consumed wakeup left a stale pending expectation on the recipient.");
            }
        }
        finally
        {
            releaseCleanup.Set();
            releaseTurn.TrySetResult();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Read_session_cleanup_removes_only_observed_wakeup_and_retains_later_same_thread_wakeup()
    {
        var ct = TestContext.Current.CancellationToken;
        var releaseTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var atCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCleanup = new ManualResetEventSlim(false);

        var (host, _, caller, vm, session, worker) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-readsession-cleanup-",
            async (_, _, _, token) =>
            {
                await releaseTurn.Task.WaitAsync(token);
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        vm.InputText = "active interactive turn";
        var manual = vm.SendPromptAsync();
        try
        {
            var dispatch = Body(await host.CallToolAsync(
                caller.Id,
                "delegate_task",
                new JsonObject { ["target"] = worker.ItemId, ["goal"] = "answer" },
                ct));
            var thread = dispatch["thread_id"]!.GetValue<string>();

            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(worker.ItemId, thread, "first update", "continue", ct))["delivery"]!.GetValue<string>());
            var first = Assert.Single(vm.QueuedPrompts);

            host.RegisterGuiSession(new ReviewCleanupFence(vm, atCleanup, releaseCleanup));
            var readTask = Task.Run(() => host.CallToolAsync(
                caller.Id,
                "read_session",
                new JsonObject { ["target_session_id"] = worker.ItemId },
                ct), ct);
            await atCleanup.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(worker.ItemId, thread, "second update", "continue", ct))["delivery"]!.GetValue<string>());
            var latest = vm.QueuedPrompts.Last();
            Assert.NotEqual(first.CollabDeliveryId, latest.CollabDeliveryId);
            Assert.True(latest.ShouldDispatch!());

            releaseCleanup.Set();
            Assert.Null((await readTask)["isError"]);

            Assert.DoesNotContain(vm.QueuedPrompts, q => q.CollabDeliveryId == first.CollabDeliveryId);
            Assert.Contains(vm.QueuedPrompts, q => q.CollabDeliveryId == latest.CollabDeliveryId);

            releaseTurn.TrySetResult();
            await manual;
            await WaitUntilAsync(() => !vm.IsTurnActive && vm.QueuedPrompts.Count == 0, ct);
            Assert.DoesNotContain(session.SentPrompts, p => p.Text.Contains("first update", StringComparison.Ordinal));
            Assert.Contains(session.SentPrompts, p => p.Text.Contains("second update", StringComparison.Ordinal));
        }
        finally
        {
            releaseCleanup.Set();
            releaseTurn.TrySetResult();
        }
    }

    private static CollabLedger LedgerOf(CollabCoordinator host) =>
        (CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(host)!;

    private static int ActiveWakeupCount(CollabCoordinator host, string recipientItemId)
    {
        var ledger = LedgerOf(host);
        lock (ledger.Gate)
        {
            return ledger.ActiveWakeups.TryGetValue(recipientItemId, out var wakes) ? wakes.Count : 0;
        }
    }

    private static async Task<string> DelegateNewThreadAsync(CollabCoordinator host, string fromItemId, string toItemId, CancellationToken ct) =>
        Body(await host.CallToolAsync(
            fromItemId,
            "delegate_task",
            new JsonObject { ["target"] = toItemId, ["goal"] = "answer" },
            ct))["thread_id"]!.GetValue<string>();

    private sealed class GuiWakeBoundaryBridge(ICollabSessionBridge target) : ICollabSessionBridge
    {
        public Action? AfterSummary { get; init; }
        public Action? BeforeExactCancel { get; init; }
        public Action? AfterActiveTurnSample { get; init; }

        public string ItemId => target.ItemId;
        public string AgentId => target.AgentId;
        public string DisplayTitle => target.DisplayTitle;
        public string WorkingDirectory => target.WorkingDirectory;
        public bool IsBusy => target.IsBusy;
        public bool IsConnected => target.IsConnected;
        public string? CurrentStatusDetail => target.CurrentStatusDetail;
        public bool TracksActiveTurn => target.TracksActiveTurn;
        public bool HasActiveTurn => target.HasActiveTurn;
        public string? ActiveCollabThreadId => target.ActiveCollabThreadId;
        public string? ActiveCollabTaskId => target.ActiveCollabTaskId;
        public string? ActiveCollabDeliveryId => target.ActiveCollabDeliveryId;
        public string? ActiveExternalTurnKey => target.ActiveExternalTurnKey;

        public string ReadRecentSummary(int maxTurns = 5)
        {
            var result = target.ReadRecentSummary(maxTurns);
            AfterSummary?.Invoke();
            return result;
        }

        public bool TryGetActiveCollabTurn(out string? threadId, out string? taskId, out string? deliveryId)
        {
            var result = target.TryGetActiveCollabTurn(out threadId, out taskId, out deliveryId);
            AfterActiveTurnSample?.Invoke();
            return result;
        }

        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default) =>
            target.DeliverCollabPromptAsync(promptText, threadId, taskId, cancellationToken);

        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId, string? taskId, bool expectReply, string? deliveryId, Func<bool>? shouldCommit, CancellationToken cancellationToken) =>
            target.DeliverCollabPromptAsync(promptText, threadId, taskId, expectReply, deliveryId, shouldCommit, cancellationToken);

        public int CancelQueuedCollabPrompts(string? threadId = null, string? taskId = null) =>
            target.CancelQueuedCollabPrompts(threadId, taskId);

        public int CancelQueuedCollabPrompts(string? threadId, string? taskId, string? deliveryId)
        {
            BeforeExactCancel?.Invoke();
            return target.CancelQueuedCollabPrompts(threadId, taskId, deliveryId);
        }
    }

    private sealed class LegacyTwoArgQueueBridge(
        ICollabSessionBridge source,
        TaskCompletionSource? onBroadCancel = null,
        ManualResetEventSlim? releaseBroadCancel = null) : ICollabSessionBridge
    {
        private readonly List<(string Text, string? ThreadId, string? TaskId)> _entries = [];
        private int _broadCancelCount;

        public string ItemId => source.ItemId;
        public string AgentId => source.AgentId;
        public string DisplayTitle => source.DisplayTitle;
        public string WorkingDirectory => source.WorkingDirectory;
        public bool IsBusy => true;
        public bool IsConnected => true;
        public string? CurrentStatusDetail => null;
        public int BroadCancelCount => Volatile.Read(ref _broadCancelCount);

        public string ReadRecentSummary(int maxTurns = 5) => "";

        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default)
        {
            lock (_entries)
            {
                _entries.Add((promptText, threadId, taskId));
            }
            return Task.FromResult(true);
        }

        public int CancelQueuedCollabPrompts(string? threadId = null, string? taskId = null)
        {
            Interlocked.Increment(ref _broadCancelCount);
            onBroadCancel?.TrySetResult();
            if (releaseBroadCancel is not null && !releaseBroadCancel.Wait(TimeSpan.FromSeconds(8)))
            {
                throw new TimeoutException();
            }
            lock (_entries)
            {
                return _entries.RemoveAll(e =>
                    (!string.IsNullOrWhiteSpace(threadId) && string.Equals(e.ThreadId, threadId, StringComparison.Ordinal))
                    || (!string.IsNullOrWhiteSpace(taskId) && string.Equals(e.TaskId, taskId, StringComparison.Ordinal)));
            }
        }

        public bool Contains(string needle)
        {
            lock (_entries)
            {
                return _entries.Any(e => e.Text.Contains(needle, StringComparison.Ordinal));
            }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Read_session_uses_pre_summary_sequence_boundary_and_retains_later_same_thread_wakeup()
    {
        var ct = TestContext.Current.CancellationToken;
        var releaseTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var atSummary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resumeSummary = new ManualResetEventSlim(false);
        var (host, _, caller, vm, session, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-read-boundary-",
            async (_, _, _, token) =>
            {
                await releaseTurn.Task.WaitAsync(token);
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        vm.InputText = "normal user";
        var manual = vm.SendPromptAsync();
        try
        {
            var tid = await DelegateNewThreadAsync(host, caller.Id, peer.ItemId, ct);
            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, tid, "first", "continue", ct))["delivery"]!.GetValue<string>());
            host.RegisterGuiSession(new GuiWakeBoundaryBridge(peer)
            {
                AfterSummary = () =>
                {
                    atSummary.TrySetResult();
                    if (!resumeSummary.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException();
                },
            });

            var readTask = Task.Run(() => host.CallToolAsync(
                caller.Id,
                "read_session",
                new JsonObject { ["target_session_id"] = peer.ItemId, ["max_turns"] = 5 },
                ct), ct);
            await atSummary.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, tid, "after summary", "continue", ct))["delivery"]!.GetValue<string>());
            Assert.Equal(2, vm.QueuedPrompts.Count);

            resumeSummary.Set();
            var summary = Body(await readTask)["summary"]!.GetValue<string>();
            Assert.DoesNotContain("after summary", summary, StringComparison.Ordinal);

            releaseTurn.TrySetResult();
            await manual;
            await WaitUntilAsync(() => !vm.IsTurnActive && vm.QueuedPrompts.Count == 0, ct);
            Assert.DoesNotContain(session.SentPrompts, p => p.Text.Contains("first", StringComparison.Ordinal));
            Assert.Contains(session.SentPrompts, p => p.Text.Contains("after summary", StringComparison.Ordinal));
            Assert.Equal(0, ActiveWakeupCount(host, caller.Id));
        }
        finally
        {
            resumeSummary.Set();
            releaseTurn.TrySetResult();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Wake_starting_during_poll_active_identity_read_retains_expectation_and_replies()
    {
        var ct = TestContext.Current.CancellationToken;
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resumeIdentity = new ManualResetEventSlim(false);
        var (host, _, caller, vm, _, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-dispatch-ownership-",
            async (s, text, _, token) =>
            {
                if (text == "normal user")
                {
                    await releaseFirst.Task.WaitAsync(token);
                }
                else
                {
                    secondEntered.TrySetResult();
                    await releaseSecond.Task.WaitAsync(token);
                    s.Emit(new AcpTextDeltaEvent("assistant", "implicit ownership answer", "answer"));
                }
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        vm.InputText = "normal user";
        var manual = vm.SendPromptAsync();
        try
        {
            var tid = await DelegateNewThreadAsync(host, caller.Id, peer.ItemId, ct);
            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, tid, "please reply", "continue", ct))["delivery"]!.GetValue<string>());
            var deliveryId = Assert.Single(vm.QueuedPrompts).CollabDeliveryId;
            var sampleCount = 0;
            host.RegisterGuiSession(new GuiWakeBoundaryBridge(vm)
            {
                AfterActiveTurnSample = () =>
                {
                    if (Interlocked.Increment(ref sampleCount) == 1)
                    {
                        sampled.TrySetResult();
                        if (!resumeIdentity.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException();
                    }
                },
            });

            var poll = Task.Run(() => host.CallToolAsync(
                caller.Id,
                "await_events",
                new JsonObject { ["ids"] = new JsonArray(tid), ["timeout_seconds"] = 1 },
                ct), ct);
            await sampled.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            releaseFirst.TrySetResult();
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(deliveryId, ((ICollabSessionBridge)vm).ActiveCollabDeliveryId);

            resumeIdentity.Set();
            await poll;

            var ledger = LedgerOf(host);
            bool pendingWhileRunning;
            lock (ledger.Gate)
            {
                pendingWhileRunning = ledger.Pending.TryGetValue(caller.Id, out var rows)
                    && rows.Any(p => string.Equals(p.DeliveryId, deliveryId, StringComparison.Ordinal));
            }

            releaseSecond.TrySetResult();
            await manual;
            await WaitUntilAsync(() => !vm.IsTurnActive, ct);
            Assert.True(pendingWhileRunning, "Active wake lost its exact pending expectation before it finished.");
            await WaitUntilAsync(() => host.FindThread(tid)!.Messages.Any(m => m.Text.Contains("implicit ownership answer", StringComparison.Ordinal)), ct);
            Assert.Equal(0, ActiveWakeupCount(host, caller.Id));
        }
        finally
        {
            resumeIdentity.Set();
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Exact_snapshot_cleanup_retains_idless_one_way_direct_message_and_human_input()
    {
        var ct = TestContext.Current.CancellationToken;
        var releaseTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resumeCleanup = new ManualResetEventSlim(false);
        var (host, _, caller, vm, session, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-idless-fyi-",
            async (_, _, _, token) =>
            {
                await releaseTurn.Task.WaitAsync(token);
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        vm.InputText = "normal user";
        var manual = vm.SendPromptAsync();
        try
        {
            var tid = await DelegateNewThreadAsync(host, caller.Id, peer.ItemId, ct);
            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, tid, "observed", "continue", ct))["delivery"]!.GetValue<string>());
            host.RegisterGuiSession(new GuiWakeBoundaryBridge(vm)
            {
                BeforeExactCancel = () =>
                {
                    inCleanup.TrySetResult();
                    if (!resumeCleanup.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException();
                },
            });

            var poll = Task.Run(() => host.CallToolAsync(
                caller.Id,
                "await_events",
                new JsonObject { ["ids"] = new JsonArray(tid), ["timeout_seconds"] = 1 },
                ct), ct);
            await inCleanup.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            var fyiRes = await host.CallToolAsync(
                peer.ItemId,
                "send_message",
                new JsonObject
                {
                    ["target_session_id"] = caller.Id,
                    ["thread_id"] = tid,
                    ["message"] = "independent FYI",
                    ["expect_reply"] = false,
                },
                ct);
            Assert.Null(fyiRes["isError"]);
            vm.InputText = "later human input";
            await vm.SendPromptAsync();

            var fyi = Assert.Single(vm.QueuedPrompts, q => q.Text.Contains("independent FYI", StringComparison.Ordinal));
            Assert.Null(fyi.CollabDeliveryId);
            Assert.True(fyi.ShouldDispatch!());

            resumeCleanup.Set();
            Assert.Equal("observed", Body(await poll)["threads"]![0]!["latest_message"]!.GetValue<string>());

            releaseTurn.TrySetResult();
            await manual;
            await WaitUntilAsync(() => !vm.IsTurnActive && vm.QueuedPrompts.Count == 0, ct);
            Assert.DoesNotContain(session.SentPrompts, p => p.Text.Contains("observed", StringComparison.Ordinal));
            Assert.Contains(session.SentPrompts, p => p.Text.Contains("independent FYI", StringComparison.Ordinal));
            Assert.Contains(session.SentPrompts, p => p.Text == "later human input");
            Assert.Equal(0, ActiveWakeupCount(host, caller.Id));
        }
        finally
        {
            resumeCleanup.Set();
            releaseTurn.TrySetResult();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Completed_one_way_wakeups_release_active_wakeup_records()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, caller, vm, session, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-oneway-complete-",
            (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));

        for (var i = 1; i <= 6; i++)
        {
            var tid = await DelegateNewThreadAsync(host, caller.Id, peer.ItemId, ct);
            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, tid, "done " + i, "agreed", ct))["delivery"]!.GetValue<string>());
            await WaitUntilAsync(() => session.SentPrompts.Count == i && !vm.IsTurnActive && vm.QueuedPrompts.Count == 0, ct);
        }

        Assert.Equal(0, ActiveWakeupCount(host, caller.Id));
    }

    [AvaloniaFact, FreshApp]
    public async Task Stopped_one_way_active_wakeup_releases_active_wakeup_record()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, caller, vm, session, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-oneway-stop-",
            async (_, _, _, token) =>
            {
                entered.TrySetResult();
                await releaseStop.Task.WaitAsync(token);
                return new AcpPromptResult { StopReason = "cancelled" };
            });
        session.CancelHandler = _ =>
        {
            releaseStop.TrySetResult();
            return Task.CompletedTask;
        };

        try
        {
            var tid = await DelegateNewThreadAsync(host, caller.Id, peer.ItemId, ct);
            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, tid, "one way", "agreed", ct))["delivery"]!.GetValue<string>());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(1, ActiveWakeupCount(host, caller.Id));

            await vm.CancelTurnAsync();
            await WaitUntilAsync(() => !vm.IsTurnActive, ct);
            Assert.Equal(1, session.CancelCount);
            Assert.Equal(0, ActiveWakeupCount(host, caller.Id));
        }
        finally
        {
            releaseStop.TrySetResult();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Discarded_and_failed_one_way_wakeups_release_active_wakeup_records()
    {
        var ct = TestContext.Current.CancellationToken;
        var releaseTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var throwOnNext = false;
        var (host, _, caller, vm, _, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-oneway-discard-",
            async (_, text, _, token) =>
            {
                if (text == "hold queue")
                {
                    await releaseTurn.Task.WaitAsync(token);
                    return new AcpPromptResult { StopReason = "end_turn" };
                }
                if (throwOnNext)
                {
                    throw new InvalidOperationException("simulated turn failure");
                }
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        vm.InputText = "hold queue";
        var manual = vm.SendPromptAsync();
        try
        {
            // 1. Queued one-way wakeup removed manually from the queue tray.
            var tidRemoved = await DelegateNewThreadAsync(host, caller.Id, peer.ItemId, ct);
            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, tidRemoved, "discard me", "agreed", ct))["delivery"]!.GetValue<string>());
            var queued = Assert.Single(vm.QueuedPrompts);
            Assert.Equal(1, ActiveWakeupCount(host, caller.Id));
            vm.RemoveQueuedPrompt(queued);
            Assert.Equal(0, ActiveWakeupCount(host, caller.Id));

            // 2. Active one-way wakeup failing with an ACP error.
            releaseTurn.TrySetResult();
            await manual;
            await WaitUntilAsync(() => !vm.IsTurnActive, ct);

            throwOnNext = true;
            var tidFailed = await DelegateNewThreadAsync(host, caller.Id, peer.ItemId, ct);
            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, tidFailed, "fail me", "agreed", ct))["delivery"]!.GetValue<string>());
            await WaitUntilAsync(() => !vm.IsTurnActive && vm.QueuedPrompts.Count == 0, ct);
            Assert.Equal(0, ActiveWakeupCount(host, caller.Id));
        }
        finally
        {
            releaseTurn.TrySetResult();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Send_now_on_consumed_wakeup_skips_dispatch_and_does_not_interrupt_manual_turn()
    {
        var ct = TestContext.Current.CancellationToken;
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupHit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resumeCleanup = new ManualResetEventSlim(false);
        var (host, _, caller, vm, session, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-sendnow-stale-",
            async (_, text, _, token) =>
            {
                if (text == "normal user 1")
                {
                    await releaseFirst.Task.WaitAsync(token);
                    return new AcpPromptResult { StopReason = "end_turn" };
                }
                if (text == "normal user 2")
                {
                    await releaseSecond.Task.WaitAsync(token);
                    return new AcpPromptResult { StopReason = "cancelled" };
                }
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        vm.InputText = "normal user 1";
        var manual1 = vm.SendPromptAsync();
        try
        {
            // Case A: Consumed before Send Now -> does not interrupt manual turn and does not dispatch.
            var tid1 = await DelegateNewThreadAsync(host, caller.Id, peer.ItemId, ct);
            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, tid1, "consumed before promote", "agreed", ct))["delivery"]!.GetValue<string>());
            var queued1 = Assert.Single(vm.QueuedPrompts);
            host.RegisterGuiSession(new GuiWakeBoundaryBridge(vm)
            {
                BeforeExactCancel = () =>
                {
                    cleanupHit.TrySetResult();
                    if (!resumeCleanup.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException();
                },
            });

            var poll1 = Task.Run(() => host.CallToolAsync(
                caller.Id,
                "await_events",
                new JsonObject { ["ids"] = new JsonArray(tid1), ["timeout_seconds"] = 1 },
                ct), ct);
            await cleanupHit.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.False(queued1.ShouldDispatch!());

            await vm.SendQueuedPromptNowAsync(queued1).WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(0, session.CancelCount);

            resumeCleanup.Set();
            await poll1;
            releaseFirst.TrySetResult();
            await manual1;
            await WaitUntilAsync(() => !vm.IsTurnActive, ct);
            Assert.DoesNotContain(session.SentPrompts, p => p.Text.Contains("consumed before promote", StringComparison.Ordinal));
            Assert.Equal(0, ActiveWakeupCount(host, caller.Id));

            // Case B: Fresh before Send Now, consumed while active turn is cancelling -> skipped before ACP dispatch.
            host.RegisterGuiSession(vm);
            session.CancelHandler = _ =>
            {
                cancelStarted.TrySetResult();
                return Task.CompletedTask;
            };
            vm.InputText = "normal user 2";
            var manual2 = vm.SendPromptAsync();

            var tid2 = await DelegateNewThreadAsync(host, caller.Id, peer.ItemId, ct);
            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, tid2, "consumed during cancel", "agreed", ct))["delivery"]!.GetValue<string>());
            var queued2 = Assert.Single(vm.QueuedPrompts);
            Assert.True(queued2.ShouldDispatch!());

            var promoteTask = vm.SendQueuedPromptNowAsync(queued2);
            await cancelStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            await host.CallToolAsync(
                caller.Id,
                "await_events",
                new JsonObject { ["ids"] = new JsonArray(tid2), ["timeout_seconds"] = 1 },
                ct);
            releaseSecond.TrySetResult();
            await promoteTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await manual2;
            await WaitUntilAsync(() => !vm.IsTurnActive, ct);
            Assert.DoesNotContain(session.SentPrompts, p => p.Text.Contains("consumed during cancel", StringComparison.Ordinal));
            Assert.Equal(0, ActiveWakeupCount(host, caller.Id));
        }
        finally
        {
            resumeCleanup.Set();
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Two_argument_only_bridge_preserves_later_wake_without_broad_fallback_and_keeps_explicit_cancel()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = Fixture.Create("octopus-issue42-legacy-twoarg-");
        var (taskId, tid) = await f.DispatchAsync(ct);
        var broadCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseBroad = new ManualResetEventSlim(false);
        var parentBridge = new LegacyTwoArgQueueBridge(f.Parent, broadCalled, releaseBroad);
        f.Host.RegisterGuiSession(parentBridge);
        try
        {
            Assert.Equal("delivered", Body(await f.ReplyAsync(tid, "first notice", "continue", ct))["delivery"]!.GetValue<string>());
            var poll = Task.Run(() => f.Host.CallToolAsync(
                f.Parent.ItemId,
                "await_events",
                new JsonObject { ["ids"] = new JsonArray(tid), ["timeout_seconds"] = 1 },
                ct), ct);
            var winner = await Task.WhenAny(poll, broadCalled.Task).WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (ReferenceEquals(winner, broadCalled.Task))
            {
                // On unfixed code, exact cleanup falls back to 2-arg broad delete; queue newer notice before releasing it.
                Assert.Equal("delivered", Body(await f.ReplyAsync(tid, "newer notice", "continue", ct))["delivery"]!.GetValue<string>());
                Assert.True(parentBridge.Contains("newer notice"));
                releaseBroad.Set();
            }

            Assert.Equal("first notice", Body(await poll)["threads"]![0]!["latest_message"]!.GetValue<string>());
            Assert.Equal(0, parentBridge.BroadCancelCount);

            // A subsequent wake on the same legacy bridge arrives and stays queued.
            Assert.Equal("delivered", Body(await f.ReplyAsync(tid, "newer notice", "continue", ct))["delivery"]!.GetValue<string>());
            Assert.True(parentBridge.Contains("newer notice"));

            // Normal explicit task cancellation still invokes broad 2-arg cancellation on a legacy worker bridge.
            var workerBridge = new LegacyTwoArgQueueBridge(f.Worker);
            f.Host.RegisterGuiSession(workerBridge);
            var (cancelTaskId, _) = await f.DispatchAsync(ct);
            Assert.True(workerBridge.Contains("answer"));
            Assert.True(await f.Host.CancelTaskAsync(cancelTaskId, "explicit cancel", ct));
            Assert.Equal(1, workerBridge.BroadCancelCount);
            Assert.False(workerBridge.Contains("answer"));
            Assert.NotEmpty(taskId);
        }
        finally
        {
            releaseBroad.Set();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Already_active_expected_wake_completes_after_poll_and_releases_wakeup_record()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, caller, _, _, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-already-active-",
            async (s, _, _, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                s.Emit(new AcpTextDeltaEvent("assistant", "settled reply", "stable-active"));
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        try
        {
            var tid = await DelegateNewThreadAsync(host, caller.Id, peer.ItemId, ct);
            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, tid, "please respond", "continue", ct))["delivery"]!.GetValue<string>());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            await host.CallToolAsync(
                caller.Id,
                "await_events",
                new JsonObject { ["ids"] = new JsonArray(tid), ["timeout_seconds"] = 1 },
                ct);
            Assert.Equal(1, ActiveWakeupCount(host, caller.Id));

            release.TrySetResult();
            await WaitUntilAsync(() => host.FindThread(tid)!.Messages.Any(m => m.Text.Contains("settled reply", StringComparison.Ordinal)), ct);
            Assert.Equal(0, ActiveWakeupCount(host, caller.Id));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Unaccepted_wakeup_records_release_on_refusal_exception_and_cancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = Fixture.Create("octopus-issue42-unaccepted-");
        foreach (var status in new[] { "agreed", "continue" })
        {
            foreach (var outcome in new[] { "false", "throw", "cancel" })
            {
                var (_, tid) = await f.DispatchAsync(ct);
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
                f.Parent.Receive = (_, _, token) =>
                {
                    Assert.Equal(1, ActiveWakeupCount(f.Host, f.Parent.ItemId));
                    if (outcome == "cancel")
                    {
                        cancel.Cancel();
                        token.ThrowIfCancellationRequested();
                    }
                    if (outcome == "throw")
                    {
                        throw new InvalidOperationException("simulated refusal");
                    }
                    return Task.FromResult(false);
                };

                if (outcome == "cancel")
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.ReplyAsync(tid, "response", status, cancel.Token));
                }
                else
                {
                    Assert.Equal("failed", Body(await f.ReplyAsync(tid, "response", status, cancel.Token))["delivery"]!.GetValue<string>());
                }
                Assert.Equal(0, ActiveWakeupCount(f.Host, f.Parent.ItemId));
            }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Exact_cleanup_retains_newer_correlated_wake_and_manual_composer_draft()
    {
        var ct = TestContext.Current.CancellationToken;
        var holdTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var atDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resumeDelete = new ManualResetEventSlim(false);
        var (host, _, caller, vm, session, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-exact-control-",
            async (s, text, _, token) =>
            {
                if (text == "regular user")
                {
                    await holdTurn.Task.WaitAsync(token);
                }
                else
                {
                    s.Emit(new AcpTextDeltaEvent("assistant", "new wake answer", "result"));
                }
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        vm.InputText = "regular user";
        var manual = vm.SendPromptAsync();
        vm.InputText = "untouched draft";
        try
        {
            var tid = await DelegateNewThreadAsync(host, caller.Id, peer.ItemId, ct);
            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, tid, "old payload", "continue", ct))["delivery"]!.GetValue<string>());
            host.RegisterGuiSession(new GuiWakeBoundaryBridge(vm)
            {
                BeforeExactCancel = () =>
                {
                    atDelete.TrySetResult();
                    if (!resumeDelete.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException();
                },
            });

            var poll = Task.Run(() => host.CallToolAsync(
                caller.Id,
                "await_events",
                new JsonObject { ["ids"] = new JsonArray(tid), ["timeout_seconds"] = 1 },
                ct), ct);
            await atDelete.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, tid, "new payload", "continue", ct))["delivery"]!.GetValue<string>());
            resumeDelete.Set();
            Assert.Equal("old payload", Body(await poll)["threads"]![0]!["latest_message"]!.GetValue<string>());

            holdTurn.TrySetResult();
            await manual;
            await WaitUntilAsync(() => host.FindThread(tid)!.Messages.Any(m => m.Text.Contains("new wake answer", StringComparison.Ordinal)), ct);
            Assert.DoesNotContain(session.SentPrompts, p => p.Text.Contains("old payload", StringComparison.Ordinal));
            Assert.Contains(session.SentPrompts, p => p.Text.Contains("new payload", StringComparison.Ordinal));
            Assert.Equal("untouched draft", vm.InputText);
            Assert.Equal(0, session.CancelCount);
            Assert.Equal(0, ActiveWakeupCount(host, caller.Id));
        }
        finally
        {
            resumeDelete.Set();
            holdTurn.TrySetResult();
        }
    }

    private sealed class LegacyGuardForwardingBridge(ICollabSessionBridge target) : ICollabSessionBridge
    {
        public string ItemId => target.ItemId;
        public string AgentId => target.AgentId;
        public string DisplayTitle => target.DisplayTitle;
        public string WorkingDirectory => target.WorkingDirectory;
        public bool IsBusy => target.IsBusy;
        public bool IsConnected => target.IsConnected;
        public string? CurrentStatusDetail => target.CurrentStatusDetail;
        public string ReadRecentSummary(int maxTurns = 5) => target.ReadRecentSummary(maxTurns);
        public Task<bool> DeliverCollabPromptAsync(string p, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default) =>
            target.DeliverCollabPromptAsync(p, threadId, taskId, cancellationToken);
        public Task<bool> DeliverCollabPromptAsync(string p, string? t, string? s, bool e, string? id, Func<bool>? guard, CancellationToken ct) =>
            target.DeliverCollabPromptAsync(p, t, s, e, id, guard, ct);
    }

    private sealed class CapturingLegacyOneWayBridge(ProjectItem item, string dir) : ICollabSessionBridge
    {
        public Func<bool>? CapturedGuard { get; private set; }
        public string ItemId => item.Id;
        public string AgentId => item.AgentId ?? "codex";
        public string DisplayTitle => item.DisplayTitle;
        public string WorkingDirectory => dir;
        public bool IsBusy => false;
        public bool IsConnected => true;
        public string? CurrentStatusDetail => null;
        public string ReadRecentSummary(int maxTurns = 5) => "";
        public Task<bool> DeliverCollabPromptAsync(string p, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public Task<bool> DeliverCollabPromptAsync(string p, string? t, string? s, bool e, string? id, Func<bool>? guard, CancellationToken ct)
        {
            CapturedGuard = guard;
            return Task.FromResult(true);
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Legacy_guard_forwarding_adapter_keeps_queued_one_way_wakeup_and_retirement_does_not_revoke_guard()
    {
        var ct = TestContext.Current.CancellationToken;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, project, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-legacy-guard-",
            async (_, _, _, token) =>
            {
                await hold.Task.WaitAsync(token);
                return new AcpPromptResult { StopReason = "end_turn" };
            });

        vm.InputText = "manual";
        var manual = vm.SendPromptAsync();
        var tid = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        host.RegisterGuiSession(new LegacyGuardForwardingBridge(vm));

        var result = await host.ReplyThreadAsync(peer.ItemId, tid, "ordinary one-way", "agreed", ct);
        Assert.Equal("delivered", Body(result)["delivery"]!.GetValue<string>());
        var queued = Assert.Single(vm.QueuedPrompts);
        var fresh = queued.ShouldDispatch!();
        hold.SetResult();
        await manual;
        await WaitUntilAsync(() => !vm.IsTurnActive && vm.QueuedPrompts.Count == 0, ct);
        Assert.True(
            fresh && acp.SentPrompts.Any(s => s.Text.Contains("ordinary one-way", StringComparison.Ordinal)) && ActiveWakeupCount(host, item.Id) == 0,
            $"fresh on return={fresh}, provider turns={acp.SentPrompts.Count}, wake rows={ActiveWakeupCount(host, item.Id)}");

        // Non-VM legacy bridge that inherits default capabilities retires ActiveWakeups bookkeeping without revoking its guard.
        var legacyItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Legacy", Surface = AgentSurface.Gui };
        AppHost.Get<IProjectService>().AddItem(project, legacyItem, activate: false);
        var legacyBridge = new CapturingLegacyOneWayBridge(legacyItem, project.Path);
        host.RegisterGuiSession(legacyBridge);
        var legacyTid = await DelegateNewThreadAsync(host, legacyItem.Id, peer.ItemId, ct);
        var legacyReply = await host.ReplyThreadAsync(peer.ItemId, legacyTid, "legacy one-way", "agreed", ct);
        Assert.Equal("delivered", Body(legacyReply)["delivery"]!.GetValue<string>());
        Assert.Equal(0, ActiveWakeupCount(host, legacyItem.Id));
        Assert.NotNull(legacyBridge.CapturedGuard);
        Assert.True(legacyBridge.CapturedGuard!());
    }

    [AvaloniaFact, FreshApp]
    public async Task Promoted_one_way_collab_notification_releases_own_record_without_settling_active_task_while_human_send_now_still_steers()
    {
        var ct = TestContext.Current.CancellationToken;
        var hold = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-promoted-oneway-",
            (s, text, _, _) =>
            {
                if (!text.Contains("one-way notification", StringComparison.Ordinal)) return hold.Task;
                s.Emit(new AcpTextDeltaEvent("assistant", "FYI acknowledged", "fyi"));
                return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
            });

        var unrelated = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        var owned = Body(await host.CallToolAsync(
            peer.ItemId,
            "delegate_task",
            new JsonObject { ["target"] = item.Id, ["goal"] = "work for requester" },
            ct))["task_id"]!.GetValue<string>();
        Assert.True(vm.IsTurnActive);

        Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, unrelated, "one-way notification", "agreed", ct))["delivery"]!.GetValue<string>());
        var queued = Assert.Single(vm.QueuedPrompts);
        Assert.False(queued.ExpectCollabReply);
        Assert.NotNull(queued.CollabDeliveryId);

        acp.CancelHandler = _ =>
        {
            hold.TrySetResult(new AcpPromptResult { StopReason = "cancelled" });
            return Task.CompletedTask;
        };
        await vm.SendQueuedPromptNowAsync(queued);
        await WaitUntilAsync(() => acp.SentPrompts.Count == 2 && !vm.IsTurnActive, ct);
        await Task.Delay(200, ct);
        var task = host.GetTasksForItem(peer.ItemId).Single(t => t.TaskId == owned);
        Assert.True(
            ActiveWakeupCount(host, item.Id) == 0 && task.ResultSummary != "FYI acknowledged" && task.Status == CollabTaskStatus.Cancelled,
            $"rows={ActiveWakeupCount(host, item.Id)}, task={task.Status}, result={task.ResultSummary}");

        // Edited one-way collab notification promoted via SendPromptNowAsync must also keep its own identity and release its wakeup.
        var editHold = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        acp.PromptHandler = (s, text, _, _) =>
        {
            if (text.Contains("edited FYI", StringComparison.Ordinal))
            {
                s.Emit(new AcpTextDeltaEvent("assistant", "edited FYI ack", "edited-fyi"));
                return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
            }
            return editHold.Task;
        };
        acp.CancelHandler = _ =>
        {
            editHold.TrySetResult(new AcpPromptResult { StopReason = "cancelled" });
            return Task.CompletedTask;
        };
        var owned2 = Body(await host.CallToolAsync(
            peer.ItemId,
            "delegate_task",
            new JsonObject { ["target"] = item.Id, ["goal"] = "second work for requester" },
            ct))["task_id"]!.GetValue<string>();
        var unrelated2 = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, unrelated2, "second one-way", "agreed", ct))["delivery"]!.GetValue<string>());
        vm.EditQueuedPrompt(Assert.Single(vm.QueuedPrompts));
        vm.InputText = "edited FYI note";
        await vm.SendPromptNowAsync();
        await WaitUntilAsync(() => !vm.IsTurnActive && vm.QueuedPrompts.Count == 0, ct);
        await Task.Delay(150, ct);
        var task2 = host.GetTasksForItem(peer.ItemId).Single(t => t.TaskId == owned2);
        Assert.Equal(0, ActiveWakeupCount(host, item.Id));
        Assert.Equal(CollabTaskStatus.Cancelled, task2.Status);
        Assert.NotEqual("edited FYI ack", task2.ResultSummary);

        // Positive control: genuine human Send Now steering still keeps the active collaboration task alive and settles it.
        var steerHold = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        acp.PromptHandler = (s, text, _, _) =>
        {
            if (text == "human steer")
            {
                s.Emit(new AcpTextDeltaEvent("assistant", "steered result", "steer"));
                return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
            }
            return steerHold.Task;
        };
        acp.CancelHandler = _ =>
        {
            steerHold.TrySetResult(new AcpPromptResult { StopReason = "cancelled" });
            return Task.CompletedTask;
        };
        var steeredTaskId = Body(await host.CallToolAsync(
            peer.ItemId,
            "delegate_task",
            new JsonObject { ["target"] = item.Id, ["goal"] = "steerable task" },
            ct))["task_id"]!.GetValue<string>();
        Assert.True(vm.IsTurnActive);
        vm.InputText = "human steer";
        await vm.SendPromptNowAsync();
        await WaitUntilAsync(() => host.GetTasksForItem(peer.ItemId).Single(t => t.TaskId == steeredTaskId).IsFinished, ct);
        var steeredTask = host.GetTasksForItem(peer.ItemId).Single(t => t.TaskId == steeredTaskId);
        Assert.Equal(CollabTaskStatus.Completed, steeredTask.Status);
        Assert.Equal("steered result", steeredTask.ResultSummary);
    }

    private sealed class BufferedLegacyGuardBridge(ICollabSessionBridge inner, string? summary = null) : ICollabSessionBridge
    {
        private readonly List<(string Text, string? Thread, string? Task, bool Expected, string? Id, Func<bool>? Guard)> _buffered = [];
        public string ItemId => inner.ItemId;
        public string AgentId => inner.AgentId;
        public string DisplayTitle => inner.DisplayTitle;
        public string WorkingDirectory => inner.WorkingDirectory;
        public bool IsBusy => true;
        public bool IsConnected => true;
        public string? CurrentStatusDetail => null;
        public string ReadRecentSummary(int maxTurns = 5) => summary ?? inner.ReadRecentSummary(maxTurns);
        public Task<bool> DeliverCollabPromptAsync(string p, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> DeliverCollabPromptAsync(string p, string? t, string? task, bool e, string? id, Func<bool>? guard, CancellationToken ct)
        {
            _buffered.Add((p, t, task, e, id, guard));
            return Task.FromResult(true);
        }
        public async Task DrainAsync(CancellationToken ct)
        {
            foreach (var p in _buffered)
            {
                if (p.Guard?.Invoke() != false)
                {
                    await inner.DeliverCollabPromptAsync(p.Text, p.Thread, p.Task, p.Expected, p.Id, p.Guard, ct);
                }
            }
            _buffered.Clear();
        }
    }

    [AvaloniaTheory, FreshApp]
    [InlineData("read_session")]
    [InlineData("await_events")]
    [InlineData("unregister")]
    [InlineData("ordinary")]
    public async Task Retired_legacy_guard_skips_consumed_or_unregistered_delivery_and_allows_ordinary_drain(string action)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-retired-guard-",
            (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));
        var bridge = new BufferedLegacyGuardBridge(vm);
        host.RegisterGuiSession(bridge);
        var tid = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, tid, "retired notice", "agreed", ct))["delivery"]!.GetValue<string>());
        Assert.Empty(acp.SentPrompts);
        Assert.Equal(0, ActiveWakeupCount(host, item.Id));

        if (action == "read_session")
        {
            host.RegisterGuiSession(new BufferedLegacyGuardBridge(peer, "retired notice"));
            Assert.Contains("retired notice", Body(await host.ReadSessionAsync(item.Id, peer.ItemId, 5, ct))["summary"]!.GetValue<string>());
        }
        if (action == "await_events")
        {
            Assert.Equal("retired notice", Body(await host.AwaitEventsAsync(item.Id, [tid], 1, ct))["threads"]![0]!["latest_message"]!.GetValue<string>());
        }
        if (action == "unregister")
        {
            host.UnregisterGuiSession(bridge);
        }

        await bridge.DrainAsync(ct);
        await WaitUntilAsync(() => !vm.IsTurnActive, ct);
        var ran = acp.SentPrompts.Any(x => x.Text.Contains("retired notice", StringComparison.Ordinal));
        Assert.True(ran == (action == "ordinary"), $"action={action}, reachedProvider={ran}, rows={ActiveWakeupCount(host, item.Id)}");
    }

    [AvaloniaFact, FreshApp]
    public async Task Retiring_captured_gui_releases_only_its_own_one_way_wakes()
    {
        var ct = TestContext.Current.CancellationToken;
        var oldManual = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newManual = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, project, item, oldVm, old, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-retiring-wakes-",
            (_, _, _, token) => oldManual.Task.WaitAsync(token));
        oldVm.InputText = "old manual";
        _ = oldVm.SendPromptAsync();
        var tid = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, tid, "older notice", "agreed", ct))["delivery"]!.GetValue<string>());
        var older = Assert.Single(oldVm.QueuedPrompts);

        var tabs = AppHost.Get<ITabContentRegistry>();
        var captured = tabs.CaptureForRelease(item.Id)!;
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = s => s.PromptHandler = (_, _, _, token) => newManual.Task.WaitAsync(token);
        var fresh = Assert.IsType<AgentGuiTabViewModel>(Assert.IsType<AgentGuiTabView>(tabs.GetOrCreate(project, item)).DataContext);
        await fresh.ConnectAsync();
        fresh.InputText = "new manual";
        var active = fresh.SendPromptAsync();
        var tid2 = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, tid2, "newer notice", "agreed", ct))["delivery"]!.GetValue<string>());
        var newer = Assert.Single(fresh.QueuedPrompts);

        Assert.True(older.ShouldDispatch!() && newer.ShouldDispatch!());
        Assert.Equal(2, ActiveWakeupCount(host, item.Id));

        old.IsConnected = false;
        old.Emit(new AcpClosedEvent(new IOException("old closed")));
        await captured();
        var retiredGuard = older.ShouldDispatch!();
        var freshGuard = newer.ShouldDispatch!();
        var rowsAfterClose = ActiveWakeupCount(host, item.Id);

        newManual.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await active;
        await WaitUntilAsync(() => !fresh.IsTurnActive && fresh.QueuedPrompts.Count == 0, ct);
        var replacementRan = factory.Created.Last().SentPrompts.Any(x => x.Text.Contains("newer notice", StringComparison.Ordinal));
        var rowsAfterDelivery = ActiveWakeupCount(host, item.Id);

        Assert.True(
            !retiredGuard && freshGuard && rowsAfterClose == 1 && replacementRan && rowsAfterDelivery == 0,
            $"retiredGuard={retiredGuard}, newGuard={freshGuard}, rowsAfterClose={rowsAfterClose}, replacementRan={replacementRan}, rowsAfterIdle={rowsAfterDelivery}");
    }

    [AvaloniaFact, FreshApp]
    public async Task Direct_identity_unregister_of_replaced_legacy_adapter_revokes_only_old_buffered_notice()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, project, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-legacy-replaced-",
            (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));

        var first = new BufferedLegacyGuardBridge(vm);
        host.RegisterGuiSession(first);
        var thread = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, thread, "old buffered notice", "agreed", ct))["delivery"]!.GetValue<string>());

        var tabs = AppHost.Get<ITabContentRegistry>();
        var release = tabs.CaptureForRelease(item.Id)!;
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = s => s.PromptHandler = (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
        var freshVm = Assert.IsType<AgentGuiTabViewModel>(tabs.GetOrCreate(project, item).DataContext);
        Assert.NotSame(vm, freshVm);
        await freshVm.ConnectAsync();
        var freshAcp = factory.Created.Last();

        var replacement = new BufferedLegacyGuardBridge(freshVm);
        host.RegisterGuiSession(replacement);
        var newThread = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, newThread, "new buffered notice", "agreed", ct))["delivery"]!.GetValue<string>());
        Assert.Empty(acp.SentPrompts);

        // Direct public unregister-by-identity of the replaced legacy adapter must revoke its buffered notice
        // without revoking the active replacement adapter's buffered notice.
        host.UnregisterGuiSession(first);
        await first.DrainAsync(ct);
        await replacement.DrainAsync(ct);
        await WaitUntilAsync(() => !vm.IsTurnActive && !freshVm.IsTurnActive, ct);

        var oldRan = acp.SentPrompts.Count(p => p.Text.Contains("old buffered notice", StringComparison.Ordinal));
        var freshRan = freshAcp.SentPrompts.Count(p => p.Text.Contains("new buffered notice", StringComparison.Ordinal));

        // Direct public unregister-by-identity of the current replacement adapter revokes any subsequent notice buffered on it.
        var thirdThread = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, thirdThread, "after-unregister notice", "agreed", ct))["delivery"]!.GetValue<string>());
        host.UnregisterGuiSession(replacement);
        await replacement.DrainAsync(ct);
        await WaitUntilAsync(() => !freshVm.IsTurnActive, ct);
        var thirdRan = freshAcp.SentPrompts.Count(p => p.Text.Contains("after-unregister notice", StringComparison.Ordinal));

        await release();
        Assert.True(oldRan == 0 && freshRan == 1 && thirdRan == 0, $"oldRan={oldRan}, replacementRan={freshRan}, thirdRan={thirdRan}, rows={ActiveWakeupCount(host, item.Id)}");
    }

    private sealed class InlineLegacyObserverBridge(ICollabSessionBridge gui) : ICollabSessionBridge
    {
        public List<WeakReference<string>> Seen { get; } = [];
        public Func<bool>? BufferedGuard { get; private set; }
        public bool BufferNext { get; set; }
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
        public Task<bool> DeliverCollabPromptAsync(string p, string? t, string? a, bool expected, string? id, Func<bool>? guard, CancellationToken ct)
        {
            Assert.False(expected);
            if (BufferNext)
            {
                BufferedGuard = guard;
                return Task.FromResult(true);
            }
            Seen.Add(new WeakReference<string>(id!));
            return Task.FromResult(guard?.Invoke() ?? true);
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Inline_completed_one_way_notices_on_live_legacy_adapter_release_delivery_ids_while_preserving_unregister_guard_revocation()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, item, vm, _, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-legacy-inline-release-",
            (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));
        var observer = new InlineLegacyObserverBridge(vm);
        host.RegisterGuiSession(observer);

        for (var i = 0; i < 32; i++)
        {
            var thread = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
            var receipt = await host.ReplyThreadAsync(peer.ItemId, thread, $"notice {i}", "agreed", ct);
            Assert.Equal("delivered", Body(receipt)["delivery"]!.GetValue<string>());
        }

        Assert.Equal(32, observer.Seen.Count);
        Assert.Equal(0, ActiveWakeupCount(host, item.Id));
        await Task.Yield();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var surviving = observer.Seen.Count(id => id.TryGetTarget(out _));
        GC.KeepAlive(host);
        Assert.True(surviving <= 1, $"After 32 inline legacy notices, {surviving} exact IDs remained live, rows={ActiveWakeupCount(host, item.Id)}");

        // Verify a buffered notice on the same live legacy adapter is still revoked on identity unregister.
        observer.BufferNext = true;
        var bufferedThread = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        Assert.Equal("delivered", Body(await host.ReplyThreadAsync(peer.ItemId, bufferedThread, "buffered notice", "agreed", ct))["delivery"]!.GetValue<string>());
        Assert.True(observer.BufferedGuard!());
        host.UnregisterGuiSession(observer);
        Assert.False(observer.BufferedGuard!());
    }

    private sealed class DelayedForwardingBridge(ICollabSessionBridge gui, Func<string, Task> afterAccept) : ICollabSessionBridge
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

    [AvaloniaTheory, FreshApp]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Forwarded_one_way_reply_receipt_succeeds_after_adapter_retirement_with_or_without_exact_id(bool forwardsExactId)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, project, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-r12-wake-receipt-",
            (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));
        var thread = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);

        ICollabSessionBridge? adapter = null;
        void RetireAfterProcessing()
        {
            Assert.True(Frames.Until(() => !vm.IsTurnActive));
            Assert.Contains("one executed notice", Assert.Single(acp.SentPrompts).Text, StringComparison.Ordinal);
            host.RegisterGuiSession(vm);
            host.UnregisterGuiSession(adapter!);
        }

        if (forwardsExactId)
        {
            adapter = new DelayedForwardingBridge(vm, _ => { RetireAfterProcessing(); return Task.CompletedTask; });
        }
        else
        {
            var legacy = new Bridge(item, project.Path);
            legacy.Receive = async (text, tid, token) =>
            {
                var processed = await vm.DeliverCollabPromptAsync(text, tid, null, false, token);
                RetireAfterProcessing();
                return processed;
            };
            adapter = legacy;
        }

        host.RegisterGuiSession(adapter);
        var reply = Body(await host.ReplyThreadAsync(peer.ItemId, thread, "one executed notice", "agreed", ct));
        Assert.True(
            reply["delivery"]!.GetValue<string>() == "delivered" && ActiveWakeupCount(host, item.Id) == 0,
            $"forwardsExactId={forwardsExactId}, delivery={reply["delivery"]}, deliveredTo={reply["delivered_to"]}, prompts={acp.SentPrompts.Count}, wakes={ActiveWakeupCount(host, item.Id)}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Completed_one_way_ids_release_after_clearing_gui_timeline_regardless_of_receipt_order(bool finishBeforeReceipt)
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-r12-clear-timeline-",
            (_, _, _, _) => finishBeforeReceipt
                ? Task.FromResult(new AcpPromptResult { StopReason = "end_turn" })
                : gate.Task);

        ICollabSessionBridge registered = vm;
        if (finishBeforeReceipt)
        {
            registered = new DelayedForwardingBridge(vm, _ =>
            {
                Assert.True(Frames.Until(() => !vm.IsTurnActive));
                return Task.CompletedTask;
            });
            host.RegisterGuiSession(registered);
        }

        var ids = new List<WeakReference<string>>();
        for (var i = 0; i < 32; i++)
        {
            gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var tid = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
            var receipt = await host.ReplyThreadAsync(peer.ItemId, tid, $"notice {i}", "agreed", ct);
            Assert.Equal("delivered", Body(receipt)["delivery"]!.GetValue<string>());
            Assert.Equal(!finishBeforeReceipt, vm.IsTurnActive);
            gate.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
            await WaitUntilAsync(() => !vm.IsTurnActive, ct);
            ids.Add(new(vm.Timeline.OfType<UserTurnItem>().Last().CollabCorrelation!.DeliveryId!));
        }

        Assert.Equal(32, acp.SentPrompts.Count);
        Assert.Equal(0, ActiveWakeupCount(host, item.Id));
        vm.ClearTimelineCommand.Execute(null);
        Assert.Empty(vm.Timeline);
        await Task.Yield();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var after = ids.Count(id => id.TryGetTarget(out _));
        GC.KeepAlive(host);
        GC.KeepAlive(vm);
        GC.KeepAlive(registered);
        Assert.True(after <= 1, $"completedBeforeReceipt={finishBeforeReceipt}, idsAfterClear={after}, wakes={ActiveWakeupCount(host, item.Id)}");
    }

    private sealed class DelayedGuardBufferBridge(ICollabSessionBridge gui) : ICollabSessionBridge
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
        public Func<Task<bool>>? Dispatch { get; private set; }
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

    private sealed class WeakForwardingObserverBridge(ICollabSessionBridge gui) : ICollabSessionBridge
    {
        public List<WeakReference<string>> Seen { get; } = [];
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
        public Task<bool> DeliverCollabPromptAsync(string p, string? t, string? a, bool expected, string? id, Func<bool>? guard, CancellationToken ct)
        {
            if (id is not null) Seen.Add(new WeakReference<string>(id));
            return gui.DeliverCollabPromptAsync(p, t, a, expected, id, guard, ct);
        }
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Revoked_legacy_one_way_buffer_reports_failure_for_send_and_reply_while_active_buffer_succeeds(bool reply, bool retire)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-r13-legacy-buffer-",
            (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));
        var tid = reply ? await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct) : null;

        var legacy = new DelayedGuardBufferBridge(vm);
        host.RegisterGuiSession(legacy);
        var call = reply
            ? host.ReplyThreadAsync(peer.ItemId, tid!, "buffered notice", "agreed", ct)
            : host.CallToolAsync(peer.ItemId, "send_message", new JsonObject
            {
                ["target_session_id"] = item.Id,
                ["message"] = "buffered notice",
                ["expect_reply"] = false,
            }, ct);
        await legacy.Received.Task.WaitAsync(TimeSpan.FromSeconds(4), ct);
        if (retire)
        {
            host.RegisterGuiSession(vm);
            host.UnregisterGuiSession(legacy);
        }
        var dispatch = await legacy.Dispatch!();
        if (dispatch) await WaitUntilAsync(() => !vm.IsTurnActive, ct);
        legacy.Receipt.SetResult(true);
        var receipt = await call;
        var success = reply
            ? Body(receipt)["delivery"]!.GetValue<string>() == "delivered"
            : receipt["isError"] is null;
        Assert.True(
            success == dispatch && dispatch == !retire && acp.SentPrompts.Count == (retire ? 0 : 1),
            $"reply={reply}, retire={retire}, dispatch={dispatch}, prompts={acp.SentPrompts.Count}, reportedSuccess={success}, receipt={receipt.ToJsonString()}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(false, "retire")]
    [InlineData(true, "retire")]
    [InlineData(true, "await")]
    [InlineData(true, "ordinary")]
    [InlineData(false, "ordinary")]
    public async Task Forwarded_queued_one_way_notice_reports_truthful_receipt_on_retire_await_and_ordinary_drain(bool reply, string transition)
    {
        var ct = TestContext.Current.CancellationToken;
        var finish = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-r13-gui-queue-",
            (_, text, _, token) => text == "manual"
                ? finish.Task.WaitAsync(token)
                : Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));
        var tid = reply ? await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct) : null;

        vm.InputText = "manual";
        var manual = vm.SendPromptAsync();
        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new DelayedForwardingBridge(vm, _ =>
        {
            received.SetResult();
            return ack.Task;
        });
        host.RegisterGuiSession(adapter);

        var call = reply
            ? host.ReplyThreadAsync(peer.ItemId, tid!, "queued notice", "agreed", ct)
            : host.CallToolAsync(peer.ItemId, "send_message", new JsonObject
            {
                ["target_session_id"] = item.Id,
                ["message"] = "queued notice",
                ["expect_reply"] = false,
            }, ct);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(4), ct);
        Assert.True(Assert.Single(vm.QueuedPrompts).ShouldDispatch!());

        if (transition == "retire")
        {
            host.RegisterGuiSession(vm);
            host.UnregisterGuiSession(adapter);
        }
        else if (transition == "await")
        {
            var snapshot = Body(await host.AwaitEventsAsync(item.Id, [tid!], 1, ct));
            Assert.Equal("queued notice", snapshot["threads"]![0]!["latest_message"]!.GetValue<string>());
        }

        var valid = vm.QueuedPrompts.SingleOrDefault()?.ShouldDispatch!() ?? false;
        Assert.Equal(transition == "ordinary", valid);
        finish.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await manual;
        Assert.Empty(vm.QueuedPrompts);
        Assert.False(vm.IsTurnActive);
        ack.SetResult();

        var receipt = await call;
        var success = receipt["isError"] is null;
        var status = success ? (string?)Body(receipt)[reply ? "delivery" : "status"] : "failed";
        var deliveredTo = success ? (string?)Body(receipt)["delivered_to"] : null;
        var expectedStatus = transition == "ordinary"
            ? "delivered"
            : transition == "await"
                ? "await_events_consumed"
                : "failed";
        Assert.True(
            status == expectedStatus && (transition == "ordinary" || deliveredTo is null),
            $"reply={reply}, transition={transition}, receipt={status}, deliveredTo={deliveredTo}, noticePrompts={acp.SentPrompts.Count - 1}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_one_way_ids_release_from_replaced_bridge_after_clearing_gui_timeline(bool replaceWhileQueued)
    {
        var ct = TestContext.Current.CancellationToken;
        var manualGate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-r13-replaced-ids-",
            (_, text, _, token) => text == "manual"
                ? manualGate.Task.WaitAsync(token)
                : Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));

        vm.InputText = "manual";
        var manual = vm.SendPromptAsync();
        var old = new WeakForwardingObserverBridge(vm);
        host.RegisterGuiSession(old);
        for (var i = 0; i < 32; i++)
        {
            var tid = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
            var response = Body(await host.ReplyThreadAsync(peer.ItemId, tid, $"notice {i}", "agreed", ct));
            Assert.Equal("delivered", response["delivery"]!.GetValue<string>());
        }
        Assert.Equal(32, vm.QueuedPrompts.Count);
        if (replaceWhileQueued) host.RegisterGuiSession(vm);

        manualGate.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await manual;
        await WaitUntilAsync(() => !vm.IsTurnActive && acp.SentPrompts.Count == 33, ct);
        Assert.Equal(0, ActiveWakeupCount(host, item.Id));

        vm.ClearTimelineCommand.Execute(null);
        Assert.Empty(vm.Timeline);
        await Task.Yield();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var afterClear = old.Seen.Count(id => id.TryGetTarget(out _));
        host.UnregisterGuiSession(old);
        GC.KeepAlive(old);
        GC.KeepAlive(vm);
        GC.KeepAlive(host);
        Assert.True(afterClear <= 1, $"replaced={replaceWhileQueued}, actualNotices={acp.SentPrompts.Count - 1}, afterClear={afterClear}");
    }

    private sealed record BufferedOneWayCall(
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

    private sealed class OneWayForwardBufferBridge(ICollabSessionBridge gui) : ICollabSessionBridge
    {
        public List<BufferedOneWayCall> Calls { get; } = [];
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
            var call = new BufferedOneWayCall(p, t, a, e, id, g, ct);
            Calls.Add(call);
            return call.Receipt.Task;
        }

        public Task<bool> ForwardAsync(BufferedOneWayCall call, string forwarding)
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

    private sealed class SamplingActiveTurnBridge(ICollabSessionBridge gui) : ICollabSessionBridge
    {
        public Action? AfterSample { get; set; }
        public string ItemId => gui.ItemId;
        public string AgentId => gui.AgentId;
        public string DisplayTitle => gui.DisplayTitle;
        public string WorkingDirectory => gui.WorkingDirectory;
        public bool IsBusy => gui.IsBusy;
        public bool IsConnected => gui.IsConnected;
        public string? CurrentStatusDetail => gui.CurrentStatusDetail;
        public bool TracksActiveTurn => true;
        public string ReadRecentSummary(int maxTurns = 5) => gui.ReadRecentSummary(maxTurns);
        public bool TryGetActiveCollabTurn(out string? threadId, out string? taskId, out string? deliveryId)
        {
            var result = gui.TryGetActiveCollabTurn(out threadId, out taskId, out deliveryId);
            if (deliveryId is not null) AfterSample?.Invoke();
            return result;
        }
        public Task<bool> DeliverCollabPromptAsync(string p, string? t = null, string? a = null, CancellationToken cancellationToken = default) =>
            gui.DeliverCollabPromptAsync(p, t, a, cancellationToken);
        public Task<bool> DeliverCollabPromptAsync(string p, string? t, string? a, bool e, string? id, Func<bool>? g, CancellationToken ct) =>
            gui.DeliverCollabPromptAsync(p, t, a, e, id, g, ct);
    }

    [AvaloniaTheory, FreshApp]
    [InlineData("wrapped", false)]
    [InlineData("exact", false)]
    [InlineData("wrapped", true)]
    public async Task Executed_one_way_notice_succeeds_across_wrapped_and_exact_guard_forwarders(string forwarding, bool reply)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-r14-wrapped-guard-",
            (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));
        var tid = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        var adapter = new OneWayForwardBufferBridge(vm);
        host.RegisterGuiSession(adapter);

        var response = reply
            ? host.ReplyThreadAsync(peer.ItemId, tid, "processed notice", "agreed", ct)
            : host.CallToolAsync(peer.ItemId, "send_message", new JsonObject
            {
                ["thread_id"] = tid,
                ["target_session_id"] = item.Id,
                ["message"] = "processed notice",
                ["expect_reply"] = false,
            }, ct);
        var c = Assert.Single(adapter.Calls);
        Assert.True(await adapter.ForwardAsync(c, forwarding));
        await WaitUntilAsync(() => !vm.IsTurnActive, ct);
        Assert.Contains("processed notice", Assert.Single(acp.SentPrompts).Text, StringComparison.Ordinal);

        host.RegisterGuiSession(vm);
        host.UnregisterGuiSession(adapter);
        c.Receipt.SetResult(true);
        var result = await response;
        var verdict = result["isError"] is null ? (string)Body(result)[reply ? "delivery" : "status"]! : "failed";
        Assert.True(verdict == "delivered", $"forward={forwarding}, reply={reply}, receipt={verdict}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData("exact", false)]
    [InlineData("legacy", false)]
    [InlineData("wrapped", false)]
    [InlineData("exact", true)]
    [InlineData("legacy", true)]
    [InlineData("wrapped", true)]
    public async Task Overlapping_one_way_notices_attribute_each_receipt_to_its_own_outcome(string forwarding, bool reply)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-r14-overlap-",
            (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));
        var tid = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        var adapter = new OneWayForwardBufferBridge(vm);
        host.RegisterGuiSession(adapter);

        Task<JsonObject> SendNotice(string message) => reply
            ? host.ReplyThreadAsync(peer.ItemId, tid, message, "agreed", ct)
            : host.CallToolAsync(peer.ItemId, "send_message", new JsonObject
            {
                ["thread_id"] = tid,
                ["target_session_id"] = item.Id,
                ["message"] = message,
                ["expect_reply"] = false,
            }, ct);

        var firstReceipt = SendNotice("first actual payload");
        var secondReceipt = SendNotice("second buffered payload");
        Assert.Equal(2, adapter.Calls.Count);
        var first = adapter.Calls[0];
        var second = adapter.Calls[1];

        Assert.True(await adapter.ForwardAsync(first, forwarding));
        await WaitUntilAsync(() => !vm.IsTurnActive, ct);
        Assert.Contains("first actual payload", Assert.Single(acp.SentPrompts).Text, StringComparison.Ordinal);

        host.RegisterGuiSession(vm);
        host.UnregisterGuiSession(adapter);
        Assert.False(await adapter.ForwardAsync(second, forwarding));
        first.Receipt.SetResult(true);
        second.Receipt.SetResult(true);

        string Verdict(JsonObject result) => result["isError"] is null ? (string)Body(result)[reply ? "delivery" : "status"]! : "failed";
        var firstResult = await firstReceipt;
        var secondResult = await secondReceipt;
        var reportedFirst = Verdict(firstResult);
        var reportedSecond = Verdict(secondResult);
        Assert.True(
            reportedFirst == "delivered" && reportedSecond == "failed",
            $"forward={forwarding}, reply={reply}, receipts={reportedFirst}/{reportedSecond}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Completion_during_active_owner_transfer_never_reinserts_completed_wake_id(bool finishAtSample)
    {
        var ct = TestContext.Current.CancellationToken;
        TaskCompletionSource<AcpPromptResult>? turn = null;
        var (host, _, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-r14-transfer-complete-",
            (_, _, _, token) => turn!.Task.WaitAsync(token));
        var original = new WeakForwardingObserverBridge(vm);
        var current = new SamplingActiveTurnBridge(vm);

        for (var i = 0; i < 16; i++)
        {
            turn = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var tid = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
            host.RegisterGuiSession(original);
            Assert.Equal("delivered", (string?)Body(await host.ReplyThreadAsync(peer.ItemId, tid, $"one-way {i}", "agreed", ct))["delivery"]);
            Assert.True(vm.IsTurnActive);
            Assert.Equal(i + 1, acp.SentPrompts.Count);

            host.RegisterGuiSession(current);
            current.AfterSample = () =>
            {
                if (finishAtSample)
                {
                    turn.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
                    Assert.True(Frames.Until(() => !vm.IsTurnActive));
                }
            };
            host.UnregisterGuiSession(original);
            current.AfterSample = null;
            turn.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
            await WaitUntilAsync(() => !vm.IsTurnActive, ct);
            Assert.Equal(0, ActiveWakeupCount(host, item.Id));
        }

        Assert.Equal(16, original.Seen.Count);
        vm.ClearTimelineCommand.Execute(null);
        Assert.Empty(vm.Timeline);
        await Task.Yield();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var retained = original.Seen.Count(w => w.TryGetTarget(out _));
        var map = (System.Collections.IDictionary?)typeof(CollabCoordinator)
            .GetField("_deliveryLifetimeById", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(host);
        var tracked = map is null ? 0 : original.Seen.Count(w => w.TryGetTarget(out var id) && map.Contains(id));
        host.UnregisterGuiSession(current);
        GC.KeepAlive(host);
        GC.KeepAlive(current);
        GC.KeepAlive(original);
        GC.KeepAlive(vm);
        Assert.True(retained <= 1 && tracked == 0, $"finishAtSample={finishAtSample}, retained={retained}, tracked={tracked}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Competing_checked_five_arg_forwards_attribute_actual_gui_outcomes_even_when_guard_checks_interleave(bool reply, bool overlapChecks)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-r15-two-forwards-",
            (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));
        var tid = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        var adapter = new OneWayForwardBufferBridge(vm);
        host.RegisterGuiSession(adapter);

        Task<JsonObject> SendNotice(string text) => reply
            ? host.ReplyThreadAsync(peer.ItemId, tid, text, "agreed", ct)
            : host.CallToolAsync(peer.ItemId, "send_message", new JsonObject
            {
                ["thread_id"] = tid,
                ["target_session_id"] = item.Id,
                ["message"] = text,
                ["expect_reply"] = false,
            }, ct);

        var firstReceipt = SendNotice("A cancelled on UI ingress");
        var secondReceipt = SendNotice("B processed on GUI");
        var a = adapter.Calls[0];
        var b = adapter.Calls[1];

        Task<bool> Post(BufferedOneWayCall c, CancellationToken token)
        {
            var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var actualResult = Task.Run(async () =>
            {
                try
                {
                    Assert.True(c.Guard!());
                    var ingress = vm.DeliverCollabPromptAsync(c.Text, c.Thread, c.TaskId, c.Expected, token);
                    posted.SetResult();
                    return await ingress;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return false;
                }
            }, ct);
            Assert.True(posted.Task.Wait(TimeSpan.FromSeconds(4), ct));
            return actualResult;
        }

        using var discardA = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var bDispatch = Post(b, ct);
        Assert.Empty(acp.SentPrompts);
        if (!overlapChecks) Assert.True(await bDispatch);
        var aDispatch = Post(a, discardA.Token);
        discardA.Cancel();
        var bOk = await bDispatch;
        var aOk = await aDispatch;
        Assert.True(bOk);
        Assert.False(aOk);
        await WaitUntilAsync(() => !vm.IsTurnActive, ct);
        Assert.Contains("B processed on GUI", Assert.Single(acp.SentPrompts).Text, StringComparison.Ordinal);

        host.RegisterGuiSession(vm);
        host.UnregisterGuiSession(adapter);
        a.Receipt.SetResult(aOk);
        b.Receipt.SetResult(bOk);

        string Status(JsonObject r) => r["isError"] is null ? (string)Body(r)[reply ? "delivery" : "status"]! : "failed";
        var aStatus = Status(await firstReceipt);
        var bStatus = Status(await secondReceipt);
        Assert.True(
            aStatus == "failed" && bStatus == "delivered",
            $"reply={reply}, interleaved={overlapChecks}, GUI A/B={aOk}/{bOk}, tool A/B={aStatus}/{bStatus}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData("checked", false, 1, true)]
    [InlineData("checked", true, 1, true)]
    [InlineData("checked", false, 1, false)]
    [InlineData("checked", true, 1, false)]
    [InlineData("checked", false, 0, true)]
    [InlineData("checked", true, 0, true)]
    [InlineData("unchecked", false, 0, true)]
    [InlineData("unchecked", false, 1, true)]
    [InlineData("exact", false, 1, true)]
    [InlineData("wrapped", false, 1, true)]
    [InlineData("wrapped", true, 1, true)]
    [InlineData("id", true, 1, true)]
    public async Task Concurrent_guard_checks_before_gui_ingress_preserve_per_call_receipts_across_supported_routes(
        string route,
        bool reply,
        int executedIndex,
        bool bothPositive)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-r15-concurrent-checks-",
            (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));
        var thread = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        var adapter = new OneWayForwardBufferBridge(vm);
        host.RegisterGuiSession(adapter);

        Task<JsonObject> SendNotice(string text) => reply
            ? host.ReplyThreadAsync(peer.ItemId, thread, text, "agreed", ct)
            : host.CallToolAsync(peer.ItemId, "send_message", new JsonObject
            {
                ["target_session_id"] = item.Id,
                ["message"] = text,
                ["expect_reply"] = false,
                ["thread_id"] = thread,
            }, ct);

        string[] labels = ["first wire payload", "second wire payload"];
        var responses = labels.Select(SendNotice).ToArray();
        var ran = adapter.Calls[executedIndex];
        var dropped = adapter.Calls[1 - executedIndex];

        using var posted = new ManualResetEventSlim();
        var forwarded = Task.Run(async () =>
        {
            if (route != "unchecked") Assert.True(ran.Guard!());
            var delivery = route switch
            {
                "id" => vm.DeliverCollabPromptAsync(ran.Text, ran.Thread, ran.TaskId, ran.Expected, ran.Id, null, ct),
                "exact" => vm.DeliverCollabPromptAsync(ran.Text, ran.Thread, ran.TaskId, ran.Expected, ran.Id, ran.Guard, ct),
                "wrapped" => vm.DeliverCollabPromptAsync(ran.Text, ran.Thread, ran.TaskId, ran.Expected, null, () => ran.Guard!(), ct),
                _ => vm.DeliverCollabPromptAsync(ran.Text, ran.Thread, ran.TaskId, ran.Expected, ct),
            };
            posted.Set();
            return await delivery;
        }, ct);
        Assert.True(posted.Wait(TimeSpan.FromSeconds(4), ct));
        Assert.Empty(acp.SentPrompts);
        if (route != "unchecked") Assert.True(dropped.Guard!());
        Assert.True(await forwarded);
        await WaitUntilAsync(() => !vm.IsTurnActive, ct);
        Assert.Contains(labels[executedIndex], Assert.Single(acp.SentPrompts).Text, StringComparison.Ordinal);

        host.RegisterGuiSession(vm);
        host.UnregisterGuiSession(adapter);
        Assert.False(dropped.Guard!());
        dropped.Receipt.SetResult(bothPositive);
        ran.Receipt.SetResult(true);

        var results = await Task.WhenAll(responses);
        string Verdict(JsonObject r) => r["isError"] is null ? (string)Body(r)[reply ? "delivery" : "status"]! : "failed";
        var statuses = results.Select(Verdict).ToArray();
        Assert.True(
            statuses[executedIndex] == "delivered" && statuses[1 - executedIndex] == "failed",
            $"route={route}, reply={reply}, executed={executedIndex + 1}, bothPositive={bothPositive}, statuses={string.Join("/", statuses)}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_wake_releases_final_delivery_id_from_gui_thread_after_drain_and_timeline_clear(bool unregister)
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-r15-final-queued-id-",
            (_, text, _, token) => text == "manual"
                ? gate.Task.WaitAsync(token)
                : Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));

        vm.InputText = "manual";
        var manual = vm.SendPromptAsync();
        var tid = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        var receipt = await host.ReplyThreadAsync(peer.ItemId, tid, "final queued wake", "agreed", ct);
        Assert.Equal("delivered", (string?)Body(receipt)["delivery"]);

        List<WeakReference<string>> weak = [new(Assert.Single(vm.QueuedPrompts).CollabDeliveryId!)];
        gate.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await manual;
        Assert.Equal(2, acp.SentPrompts.Count);
        Assert.False(vm.IsTurnActive);
        Assert.Equal(0, ActiveWakeupCount(host, item.Id));

        vm.ClearTimelineCommand.Execute(null);
        Assert.Empty(vm.Timeline);
        if (unregister) host.UnregisterGuiSession(vm);
        await Task.Yield();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var alive = weak.Count(w => w.TryGetTarget(out _));
        GC.KeepAlive(vm);
        GC.KeepAlive(host);
        Assert.True(alive == 0, $"unregister={unregister}, finalWakeAlive={alive}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_immediate_wake_clears_on_idle_gui_thread_with_or_without_bridge_swap(bool switchToGui)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-r15-final-immediate-id-",
            (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));
        var bridge = new WeakForwardingObserverBridge(vm);
        host.RegisterGuiSession(bridge);

        var tid = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        Assert.Equal("delivered", (string?)Body(await host.ReplyThreadAsync(peer.ItemId, tid, "one real notice", "agreed", ct))["delivery"]);
        await WaitUntilAsync(() => !vm.IsTurnActive, ct);
        Assert.Single(acp.SentPrompts);
        if (switchToGui)
        {
            host.RegisterGuiSession(vm);
            host.UnregisterGuiSession(bridge);
        }
        Assert.Equal(0, ActiveWakeupCount(host, item.Id));
        vm.ClearTimelineCommand.Execute(null);
        Assert.Empty(vm.Timeline);
        await Task.Yield();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var alive = bridge.Seen.Count(w => w.TryGetTarget(out _));
        GC.KeepAlive(host);
        GC.KeepAlive(bridge);
        GC.KeepAlive(vm);
        Assert.True(alive == 0, $"switchToGui={switchToGui}, finalWakeAlive={alive}");
    }

    private sealed class InterceptingForwardBufferBridge(ICollabSessionBridge gui) : ICollabSessionBridge
    {
        public List<BufferedOneWayCall> Calls { get; } = [];
        public Action<BufferedOneWayCall>? Received { get; set; }
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
            var call = new BufferedOneWayCall(p, t, a, e, id, g, ct);
            Calls.Add(call);
            Received?.Invoke(call);
            return call.Receipt.Task;
        }
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(false, "anonymous")]
    [InlineData(true, "anonymous")]
    [InlineData(false, "hop")]
    [InlineData(true, "hop")]
    [InlineData(false, "keep-guard")]
    [InlineData(true, "keep-guard")]
    public async Task Ambiguous_interjection_ahead_of_distinguishable_queued_notice_never_marks_later_notice_dispatched(bool reply, string route)
    {
        var ct = TestContext.Current.CancellationToken;
        var manualGate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeGate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, _, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-r16-ambiguous-ahead-",
            (_, text, _, token) => (text == "manual" ? manualGate : activeGate).Task.WaitAsync(token));
        var tid = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        var adapter = new InterceptingForwardBufferBridge(vm);
        host.RegisterGuiSession(adapter);

        vm.InputText = "manual";
        var manual = vm.SendPromptAsync();
        var first = host.InterjectInThreadAsync(tid, "please continue", ct: ct);
        var second = host.InterjectInThreadAsync(tid, "please continue", ct: ct);
        var a = adapter.Calls[0];
        var b = adapter.Calls[1];
        Assert.Equal(a.Text, b.Text);
        var forwardText = new string(a.Text.ToCharArray());

        async Task<bool> CheckedOnDifferentThread()
        {
            var before = Environment.CurrentManagedThreadId;
            Assert.True(a.Guard!());
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            Assert.NotEqual(before, Environment.CurrentManagedThreadId);
            return await vm.DeliverCollabPromptAsync(forwardText, a.Thread, a.TaskId, a.Expected, a.Token);
        }

        var aAccepted = route switch
        {
            "keep-guard" => await vm.DeliverCollabPromptAsync(forwardText, a.Thread, a.TaskId, a.Expected, a.Id, a.Guard, a.Token),
            "hop" => await CheckedOnDifferentThread(),
            _ => await vm.DeliverCollabPromptAsync(forwardText, a.Thread, a.TaskId, a.Expected, a.Token),
        };
        Assert.True(aAccepted);
        Assert.Single(vm.QueuedPrompts);

        var third = reply
            ? host.ReplyThreadAsync(peer.ItemId, tid, "distinct third notice", "agreed", ct)
            : host.CallToolAsync(peer.ItemId, "send_message", new JsonObject
            {
                ["thread_id"] = tid,
                ["target_session_id"] = item.Id,
                ["message"] = "distinct third notice",
                ["expect_reply"] = false,
            }, ct);
        var c = adapter.Calls[2];
        var cAccepted = await vm.DeliverCollabPromptAsync(c.Text, c.Thread, c.TaskId, c.Expected, c.Id, c.Guard, c.Token);
        Assert.True(cAccepted);
        Assert.Equal(2, vm.QueuedPrompts.Count);

        manualGate.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => acp.SentPrompts.Count == 2, ct);
        Assert.Equal(a.Text.Trim(), acp.SentPrompts[1].Text);
        Assert.Equal(c.Text.Trim(), Assert.Single(vm.QueuedPrompts).Text);

        host.RegisterGuiSession(vm);
        host.UnregisterGuiSession(adapter);
        Assert.False(c.Guard!());
        activeGate.SetResult(new AcpPromptResult { StopReason = "end_turn" });
        await manual;
        Assert.False(vm.IsTurnActive);
        Assert.Empty(vm.QueuedPrompts);
        Assert.DoesNotContain(acp.SentPrompts, p => p.Text.Contains("distinct third notice", StringComparison.Ordinal));

        a.Receipt.SetResult(aAccepted);
        b.Receipt.SetResult(false);
        c.Receipt.SetResult(cAccepted);
        _ = await first;
        _ = await second;
        var receipt = await third;
        var status = receipt["isError"] is null ? (string)Body(receipt)[reply ? "delivery" : "status"]! : "failed";
        var named = reply && (string?)Body(receipt)["delivered_to"] == item.Id;
        Assert.True(status == "failed" && !named, $"reply={reply}, route={route}, C tool={status}, C named={named}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(false, "newline")]
    [InlineData(true, "newline")]
    [InlineData(false, "newline-hop")]
    [InlineData(true, "newline-hop")]
    [InlineData(false, "newline-delayed")]
    [InlineData(true, "newline-delayed")]
    [InlineData(false, "nested-claim")]
    [InlineData(true, "nested-claim")]
    [InlineData(false, "nested-accept")]
    [InlineData(true, "nested-accept")]
    public async Task Forwarded_one_way_notice_preserves_receipt_across_crlf_normalization_and_nested_guard_sends(bool reply, string route)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, item, vm, acp, peer) = await SetUpRealGuiTargetAsync(
            "octopus-issue42-r16-bridge-receipt-",
            (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" }));
        var thread = await DelegateNewThreadAsync(host, item.Id, peer.ItemId, ct);
        var adapter = new InterceptingForwardBufferBridge(vm);
        host.RegisterGuiSession(adapter);

        Task<JsonObject> SendNotice() => reply
            ? host.ReplyThreadAsync(peer.ItemId, thread, "repeated body", "agreed", ct)
            : host.CallToolAsync(peer.ItemId, "send_message", new JsonObject
            {
                ["thread_id"] = thread,
                ["target_session_id"] = item.Id,
                ["message"] = "repeated body",
                ["expect_reply"] = false,
            }, ct);

        var first = SendNotice();
        var a = Assert.Single(adapter.Calls);
        Task<JsonObject>? second = null;
        var forwarded = a.Text;
        bool accepted;
        if (route.StartsWith("newline", StringComparison.Ordinal))
        {
            second = SendNotice();
            var b = adapter.Calls[1];
            forwarded = a.Text.ReplaceLineEndings("\r\n");
            Assert.True(a.Guard!());
            if (route == "newline-delayed")
            {
                Assert.True(b.Guard!());
                await Task.Yield();
            }
            Task<bool> Forward() => vm.DeliverCollabPromptAsync(forwarded, a.Thread, a.TaskId, a.Expected, ct);
            async Task<bool> FromContinuation()
            {
                var source = Environment.CurrentManagedThreadId;
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                Assert.NotEqual(source, Environment.CurrentManagedThreadId);
                return await Forward();
            }
            accepted = await (route == "newline-hop" ? FromContinuation() : Forward());
        }
        else
        {
            var checks = 0;
            adapter.Received = inner => Assert.True(inner.Guard!());
            bool CheckAndSendNested()
            {
                var allowed = a.Guard!();
                if (++checks == (route == "nested-claim" ? 2 : 1)) second = SendNotice();
                return allowed;
            }
            accepted = await vm.DeliverCollabPromptAsync(a.Text, a.Thread, a.TaskId, a.Expected, a.Id, CheckAndSendNested, ct);
            Assert.NotNull(second);
        }

        Assert.True(accepted);
        Assert.Equal(2, adapter.Calls.Count);
        await WaitUntilAsync(() => !vm.IsTurnActive, ct);
        Assert.Equal(forwarded.Trim(), Assert.Single(acp.SentPrompts).Text);

        host.RegisterGuiSession(vm);
        host.UnregisterGuiSession(adapter);
        Assert.False(adapter.Calls[1].Guard!());
        adapter.Calls[1].Receipt.SetResult(false);
        a.Receipt.SetResult(accepted);

        var receipts = await Task.WhenAll(first, second!);
        string Status(JsonObject r) => r["isError"] is null ? (string)Body(r)[reply ? "delivery" : "status"]! : "failed";
        var statuses = receipts.Select(Status).ToArray();
        Assert.True(statuses.SequenceEqual(["delivered", "failed"]), $"route={route}, reply={reply}, public A/B={string.Join("/", statuses)}");
    }
}
