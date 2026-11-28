using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Octopus.App.Collab;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Xunit;

namespace Octopus.App.Tests;

public sealed class CollabLogicBugTests
{
    private sealed class ConfigurableBridge(string itemId, string agentId, string title, string cwd) : ICollabSessionBridge
    {
        public string ItemId => itemId;
        public string AgentId => agentId;
        public string DisplayTitle => title;
        public string WorkingDirectory => cwd;
        public bool IsBusy { get; set; }
        public bool IsConnected { get; set; } = true;
        public string? CurrentStatusDetail { get; set; }

        private readonly object _gate = new();
        public List<string> DeliveredPrompts { get; } = [];
        public Func<string, string?, string?, CancellationToken, Task<bool>>? OnDeliver { get; set; }

        public int DeliveredCount
        {
            get
            {
                lock (_gate)
                {
                    return DeliveredPrompts.Count;
                }
            }
        }

        public async Task<bool> DeliverCollabPromptAsync(
            string promptText,
            string? threadId = null,
            string? taskId = null,
            CancellationToken cancellationToken = default)
        {
            if (OnDeliver is not null)
            {
                var accepted = await OnDeliver(promptText, threadId, taskId, cancellationToken);
                if (accepted)
                {
                    lock (_gate)
                    {
                        DeliveredPrompts.Add(promptText);
                    }
                }
                return accepted;
            }

            lock (_gate)
            {
                DeliveredPrompts.Add(promptText);
            }
            return true;
        }

        public string ReadRecentSummary(int maxTurns = 5) => "Recent summary from configurable bridge.";
    }

    private static JsonObject Body(JsonObject toolResult) =>
        JsonNode.Parse(toolResult["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

    private static async Task<(Project Project, ProjectItem Parent, ConfigurableBridge ParentBridge, CollabCoordinator Coordinator, string Dir)>
        SetUpParentAsync(string prefix)
    {
        await Task.Yield();
        var dir = Directory.CreateTempSubdirectory(prefix).FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Parent", Surface = AgentSurface.Gui };
        projects.AddItem(project, parent);
        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        var parentBridge = new ConfigurableBridge(parent.Id, "claude", parent.Title, dir);
        coordinator.RegisterGuiSession(parentBridge);
        return (project, parent, parentBridge, coordinator, dir);
    }

    private static async Task<(Project Project, ProjectItem Parent, ConfigurableBridge ParentBridge, ProjectItem Worker, ConfigurableBridge WorkerBridge, CollabCoordinator Coordinator)>
        SetUpParentAndWorkerAsync(string prefix)
    {
        var (project, parent, parentBridge, coordinator, dir) = await SetUpParentAsync(prefix);
        var projects = AppHost.Get<IProjectService>();
        var worker = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Worker", Surface = AgentSurface.Gui };
        projects.AddItem(project, worker, activate: false);
        var workerBridge = new ConfigurableBridge(worker.Id, "codex", worker.Title, dir);
        coordinator.RegisterGuiSession(workerBridge);
        return (project, parent, parentBridge, worker, workerBridge, coordinator);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        for (var i = 0; i < 80 && !condition(); i++)
        {
            await Task.Delay(25, ct);
        }
    }

    // -------------------------------------------------------------------------
    // Issue #40
    // -------------------------------------------------------------------------

    [AvaloniaFact, FreshApp]
    public async Task Accepted_reply_before_dispatch_returns_settles_the_returned_task()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, parent, _, worker, workerBridge, coordinator) = await SetUpParentAndWorkerAsync("octopus-collab-40-reply-");

        workerBridge.OnDeliver = async (_, threadId, _, token) =>
        {
            Assert.False(string.IsNullOrWhiteSpace(threadId));
            var reply = await coordinator.CallToolAsync(
                worker.Id,
                "reply_thread",
                new JsonObject
                {
                    ["thread_id"] = threadId,
                    ["message"] = "Completed synchronously before dispatch returned",
                    ["status"] = "agreed",
                },
                token);
            Assert.Null(reply["isError"]);
            return true;
        };

        var res = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject
            {
                ["target"] = worker.Id,
                ["title"] = "Fast sync task",
                ["goal"] = "Fast sync task",
            },
            ct);

        Assert.Null(res["isError"]);
        var body = Body(res);
        Assert.Equal("completed", body["status"]?.GetValue<string>());

        var task = Assert.Single(coordinator.GetTasksForItem(parent.Id));
        Assert.Equal(CollabTaskStatus.Completed, task.Status);
    }

    [AvaloniaFact, FreshApp]
    public async Task Failed_delivery_on_reused_session_does_not_leave_running_task()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, parent, _, worker, workerBridge, coordinator) = await SetUpParentAndWorkerAsync("octopus-collab-40-fail-");

        workerBridge.OnDeliver = (_, _, _, _) => Task.FromResult(false);

        var res = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject
            {
                ["target"] = worker.Id,
                ["title"] = "Rejected delivery",
                ["goal"] = "Rejected delivery",
            },
            ct);

        Assert.True(res["isError"]?.GetValue<bool>());
        Assert.Empty(coordinator.GetTasksForItem(parent.Id));
    }

    // -------------------------------------------------------------------------
    // Issue #41
    // -------------------------------------------------------------------------

    [AvaloniaFact, FreshApp]
    public async Task Two_delegations_to_one_worker_keep_their_implicit_reports_in_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, parent, parentBridge, worker, _, coordinator) = await SetUpParentAndWorkerAsync("octopus-collab-41-order-");

        var firstRes = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["title"] = "First job", ["goal"] = "First job" },
            ct);
        Assert.Null(firstRes["isError"]);
        var firstTaskId = Body(firstRes)["task_id"]!.GetValue<string>();

        var secondRes = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["title"] = "Second job", ["goal"] = "Second job" },
            ct);
        Assert.Null(secondRes["isError"]);
        var secondTaskId = Body(secondRes)["task_id"]!.GetValue<string>();

        coordinator.NotifyTurnCompleted(worker.Id, "first job finished");
        await WaitUntilAsync(
            () => parentBridge.DeliveredCount >= 1
                  && coordinator.GetTasksForItem(parent.Id).First(t => t.TaskId == firstTaskId).Status == CollabTaskStatus.Completed,
            ct);

        var tasksAfterFirst = coordinator.GetTasksForItem(parent.Id);
        var firstTask = tasksAfterFirst.First(t => t.TaskId == firstTaskId);
        var secondTask = tasksAfterFirst.First(t => t.TaskId == secondTaskId);
        Assert.Equal(CollabTaskStatus.Completed, firstTask.Status);
        Assert.Equal("first job finished", firstTask.ResultSummary);
        Assert.Equal(CollabTaskStatus.Running, secondTask.Status);

        coordinator.NotifyTurnCompleted(worker.Id, "second job finished");
        await WaitUntilAsync(
            () => parentBridge.DeliveredCount >= 2
                  && coordinator.GetTasksForItem(parent.Id).First(t => t.TaskId == secondTaskId).Status == CollabTaskStatus.Completed,
            ct);

        var secondTaskAfter = coordinator.GetTasksForItem(parent.Id).First(t => t.TaskId == secondTaskId);
        Assert.Equal(CollabTaskStatus.Completed, secondTaskAfter.Status);
        Assert.Equal("second job finished", secondTaskAfter.ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Explicit_reply_to_second_delegation_preserves_first_implicit_expectation()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, parent, parentBridge, worker, _, coordinator) = await SetUpParentAndWorkerAsync("octopus-collab-41-explicit-");

        var firstRes = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["title"] = "First job", ["goal"] = "First job" },
            ct);
        Assert.Null(firstRes["isError"]);
        var firstTaskId = Body(firstRes)["task_id"]!.GetValue<string>();

        var secondRes = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["title"] = "Second job", ["goal"] = "Second job" },
            ct);
        Assert.Null(secondRes["isError"]);
        var secondBody = Body(secondRes);
        var secondTaskId = secondBody["task_id"]!.GetValue<string>();
        var secondThreadId = secondBody["thread_id"]!.GetValue<string>();

        var reply = await coordinator.CallToolAsync(
            worker.Id,
            "reply_thread",
            new JsonObject
            {
                ["thread_id"] = secondThreadId,
                ["message"] = "second explicit",
                ["status"] = "agreed",
            },
            ct);
        Assert.Null(reply["isError"]);

        var deliveredBeforeImplicit = parentBridge.DeliveredCount;
        coordinator.NotifyTurnCompleted(worker.Id, "first implicit");
        await WaitUntilAsync(
            () => parentBridge.DeliveredCount > deliveredBeforeImplicit
                  && coordinator.GetTasksForItem(parent.Id).First(t => t.TaskId == firstTaskId).Status == CollabTaskStatus.Completed,
            ct);

        var tasks = coordinator.GetTasksForItem(parent.Id);
        var firstTask = tasks.First(t => t.TaskId == firstTaskId);
        var secondTask = tasks.First(t => t.TaskId == secondTaskId);
        Assert.Equal(CollabTaskStatus.Completed, firstTask.Status);
        Assert.Equal("first implicit", firstTask.ResultSummary);
        Assert.Equal(CollabTaskStatus.Completed, secondTask.Status);
    }

    [AvaloniaFact, FreshApp]
    public async Task Failed_delivery_does_not_corrupt_pending_queue()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, parent, parentBridge, worker, workerBridge, coordinator) = await SetUpParentAndWorkerAsync("octopus-collab-41-failed-");

        var firstRes = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["title"] = "First job", ["goal"] = "First job" },
            ct);
        Assert.Null(firstRes["isError"]);
        var firstTaskId = Body(firstRes)["task_id"]!.GetValue<string>();

        workerBridge.OnDeliver = (_, _, _, _) => Task.FromResult(false);
        var secondRes = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["title"] = "Second job fails", ["goal"] = "Second job fails" },
            ct);
        Assert.True(secondRes["isError"]?.GetValue<bool>());

        coordinator.NotifyTurnCompleted(worker.Id, "first ok");
        await WaitUntilAsync(
            () => parentBridge.DeliveredCount >= 1
                  && coordinator.GetTasksForItem(parent.Id).First(t => t.TaskId == firstTaskId).Status == CollabTaskStatus.Completed,
            ct);

        var firstTask = Assert.Single(coordinator.GetTasksForItem(parent.Id));
        Assert.Equal(firstTaskId, firstTask.TaskId);
        Assert.Equal(CollabTaskStatus.Completed, firstTask.Status);
        Assert.Equal("first ok", firstTask.ResultSummary);
    }

    // -------------------------------------------------------------------------
    // Issue #42
    // -------------------------------------------------------------------------

    [AvaloniaFact, FreshApp]
    public async Task A_parent_bridge_rejecting_delivery_never_yields_a_delivered_receipt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, parent, parentBridge, worker, _, coordinator) = await SetUpParentAndWorkerAsync("octopus-collab-42-reject-");

        var delegated = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["title"] = "Deliverable", ["goal"] = "Deliverable" },
            ct);
        Assert.Null(delegated["isError"]);
        var delegatedBody = Body(delegated);
        var taskId = delegatedBody["task_id"]!.GetValue<string>();
        var threadId = delegatedBody["thread_id"]!.GetValue<string>();

        var attempts = 0;
        parentBridge.OnDeliver = (_, _, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(false);
        };

        var reply = await coordinator.CallToolAsync(
            worker.Id,
            "reply_thread",
            new JsonObject
            {
                ["thread_id"] = threadId,
                ["message"] = "Work complete",
                ["status"] = "agreed",
            },
            ct);

        Assert.Equal(1, Volatile.Read(ref attempts));
        Assert.Null(reply["isError"]);

        var replyBody = Body(reply);
        Assert.Null(replyBody["delivered_to"]);
        Assert.True(replyBody["recorded"]?.GetValue<bool>());
        Assert.False(replyBody["wakeup_delivered"]?.GetValue<bool>());

        var task = Assert.Single(coordinator.GetTasksForItem(parent.Id));
        Assert.Equal(taskId, task.TaskId);
        Assert.Equal(CollabTaskStatus.Completed, task.Status);
    }

    [AvaloniaFact, FreshApp]
    public async Task Parent_accepting_delivery_yields_delivered_to()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, parent, parentBridge, worker, _, coordinator) = await SetUpParentAndWorkerAsync("octopus-collab-42-accept-");

        var delegated = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = worker.Id, ["title"] = "Deliverable", ["goal"] = "Deliverable" },
            ct);
        Assert.Null(delegated["isError"]);
        var threadId = Body(delegated)["thread_id"]!.GetValue<string>();

        parentBridge.OnDeliver = (_, _, _, _) => Task.FromResult(true);

        var reply = await coordinator.CallToolAsync(
            worker.Id,
            "reply_thread",
            new JsonObject
            {
                ["thread_id"] = threadId,
                ["message"] = "Work complete",
                ["status"] = "agreed",
            },
            ct);

        Assert.Null(reply["isError"]);
        var replyBody = Body(reply);
        Assert.Equal(parent.Id, replyBody["delivered_to"]?.GetValue<string>());
        Assert.True(replyBody["wakeup_delivered"]?.GetValue<bool>());
    }

    // -------------------------------------------------------------------------
    // Issue #39
    // -------------------------------------------------------------------------

    [AvaloniaFact, FreshApp]
    public async Task Duplicate_active_delegation_to_same_agent_and_goal_is_deduplicated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, parent, _, coordinator, _) = await SetUpParentAsync("octopus-collab-39-dedup-");

        var first = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject
            {
                ["target"] = "codex",
                ["title"] = "Analyze architecture",
                ["goal"] = "Analyze architecture",
                ["surface"] = "gui",
            },
            ct);
        Assert.Null(first["isError"]);
        var firstBody = Body(first);

        var second = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject
            {
                ["target"] = "codex",
                ["title"] = "Analyze architecture",
                ["goal"] = "Analyze architecture",
                ["surface"] = "gui",
            },
            ct);
        Assert.Null(second["isError"]);
        var secondBody = Body(second);

        Assert.Equal(firstBody["task_id"]!.GetValue<string>(), secondBody["task_id"]!.GetValue<string>());
        Assert.Equal(firstBody["session_id"]!.GetValue<string>(), secondBody["session_id"]!.GetValue<string>());
        Assert.True(secondBody["deduplicated"]?.GetValue<bool>());

        Assert.Single(project.Items, i => i.ParentItemId == parent.Id);
    }

    [AvaloniaFact, FreshApp]
    public async Task Cancelling_dedicated_child_clears_launch_prompt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, parent, _, coordinator, _) = await SetUpParentAsync("octopus-collab-39-cancel-");

        var delegated = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject
            {
                ["target"] = "codex",
                ["title"] = "Inspect logs",
                ["goal"] = "Inspect logs",
                ["surface"] = "gui",
            },
            ct);
        Assert.Null(delegated["isError"]);
        var delegatedBody = Body(delegated);
        var taskId = delegatedBody["task_id"]!.GetValue<string>();
        var childId = delegatedBody["session_id"]!.GetValue<string>();

        var childItem = Assert.Single(project.Items, i => i.Id == childId);
        Assert.False(string.IsNullOrWhiteSpace(childItem.Launch?.Prompt));

        var cancelled = await coordinator.CallToolAsync(
            parent.Id,
            "cancel_task",
            new JsonObject { ["task_id"] = taskId },
            ct);
        Assert.Null(cancelled["isError"]);

        Assert.Null(childItem.Launch?.Prompt);
    }
}
