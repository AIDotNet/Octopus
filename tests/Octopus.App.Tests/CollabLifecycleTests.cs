using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.Acp.Protocol;
using Octopus.Acp.Transport;
using Octopus.Agents.Signals;
using Octopus.App.AgentGui;
using Octopus.App.Collab;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Terminals;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Core.Terminals;
using Octopus.Storage;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using TKCode.Terminal.Core;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>
/// Regression tests for Phase 0B WP-0B-1:
/// BUG-03 (ACP connect failure, ACP unexpected exit, PTY start failure, PTY unexpected exit, item/project removal)
/// and Issue #39 hypothesis (hidden terminal startup vs GUI surface control), plus normal idle/approval non-regression.
/// </summary>
public sealed class CollabLifecycleTests
{
    private sealed class SpyGuiBridge(string itemId, string agentId, string title, string cwd) : ICollabSessionBridge
    {
        private readonly object _gate = new();
        public string ItemId => itemId;
        public string AgentId => agentId;
        public string DisplayTitle => title;
        public string WorkingDirectory => cwd;
        public bool IsBusy { get; set; }
        public bool IsConnected { get; set; } = true;
        public string? CurrentStatusDetail { get; set; }
        public List<(string Prompt, string? ThreadId, string? TaskId)> Delivered { get; } = [];
        public Func<string, string?, string?, CancellationToken, Task<bool>>? OnDeliver { get; set; }
        public Func<string, string?, string?, bool, string?, Func<bool>?, CancellationToken, Task<bool>>? OnDeliverWithMeta { get; set; }

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

        public Task<bool> DeliverCollabPromptAsync(
            string promptText,
            string? threadId,
            string? taskId,
            bool expectReply,
            string? deliveryId,
            Func<bool>? shouldCommit,
            CancellationToken cancellationToken)
        {
            if (OnDeliverWithMeta is not null)
            {
                lock (_gate)
                {
                    Delivered.Add((promptText, threadId, taskId));
                }
                return OnDeliverWithMeta(promptText, threadId, taskId, expectReply, deliveryId, shouldCommit, cancellationToken);
            }
            return DeliverCollabPromptAsync(promptText, threadId, taskId, cancellationToken);
        }

        public int CountForThread(string threadId)
        {
            lock (_gate)
            {
                return Delivered.Count(d => string.Equals(d.ThreadId, threadId, StringComparison.Ordinal));
            }
        }

        public List<string> PromptsForThread(string threadId)
        {
            lock (_gate)
            {
                return Delivered
                    .Where(d => string.Equals(d.ThreadId, threadId, StringComparison.Ordinal))
                    .Select(d => d.Prompt)
                    .ToList();
            }
        }

        public string ReadRecentSummary(int maxTurns = 5) => "";
    }

    private static JsonObject Body(JsonObject toolResult) =>
        JsonNode.Parse(toolResult["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

    private static async Task<(Project Project, ProjectItem Parent, SpyGuiBridge ParentBridge, CollabCoordinator Coordinator)> SetUpParentAsync(string prefix)
    {
        await Task.Yield();
        var dir = Directory.CreateTempSubdirectory(prefix).FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Coordinator", Surface = AgentSurface.Gui };
        projects.AddItem(project, parent);
        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        var bridge = new SpyGuiBridge(parent.Id, "claude", parent.Title, dir);
        coordinator.RegisterGuiSession(bridge);
        return (project, parent, bridge, coordinator);
    }

    private static async Task<JsonObject> DelegateAsync(
        CollabCoordinator coordinator,
        string callerId,
        string target,
        string goal,
        CancellationToken ct,
        string? surface = null)
    {
        var args = new JsonObject
        {
            ["target"] = target,
            ["title"] = goal,
            ["goal"] = goal,
        };
        if (surface is not null) args["surface"] = surface;
        var res = await coordinator.CallToolAsync(callerId, "delegate_task", args, ct);
        Assert.Null(res["isError"]);
        return Body(res);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, int maxTicks = 120)
    {
        for (var i = 0; i < maxTicks && !condition(); i++)
        {
            Frames.Pump();
            await Task.Delay(25, ct);
        }
        Frames.Pump();
        Assert.True(condition());
    }

    private static Window ShowInWindow(Control control)
    {
        var window = new Window { Width = 800, Height = 500, Content = control };
        window.Show();
        window.UpdateLayout();
        Frames.Pump();
        return window;
    }

    // ==================== BUG-03: ACP Connect Failure & Unexpected Process Exit ====================

    [AvaloniaFact, FreshApp]
    public async Task Acp_connect_failure_settles_delegated_task_and_notifies_caller_once_without_affecting_siblings()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-acp-connect-");
        var tabs = AppHost.Get<ITabContentRegistry>();
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();

        // 1. Unrelated sibling stays running throughout.
        var siblingBlock = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeAcpSession? siblingSession = null;
        fakeFactory.ConfigureNext = s =>
        {
            siblingSession = s;
            s.PromptHandler = (_, _, _, _) => siblingBlock.Task;
        };
        var siblingRes = await DelegateAsync(coordinator, parent.Id, "opencode", "Sibling work", ct, surface: "gui");
        var siblingTaskId = siblingRes["task_id"]!.GetValue<string>();
        await WaitUntilAsync(() => siblingSession is not null && siblingSession.SentPrompts.Count == 1, ct);

        // 2. Second delegate fails during ACP StartAsync handshake.
        fakeFactory.ConfigureNext = s =>
        {
            s.StartHandler = (_, _) => throw new InvalidOperationException("ACP binary crashed during handshake");
        };
        var failingRes = await DelegateAsync(coordinator, parent.Id, "opencode", "Failing ACP job", ct, surface: "gui");
        var failingTaskId = failingRes["task_id"]!.GetValue<string>();
        var failingChildId = failingRes["session_id"]!.GetValue<string>();
        var failingThreadId = failingRes["thread_id"]!.GetValue<string>();

        var failingView = Assert.IsType<AgentGuiTabView>(tabs.Find(failingChildId));
        var failingVm = Assert.IsType<AgentGuiTabViewModel>(failingView.DataContext);
        await WaitUntilAsync(() => failingVm.State == AgentGuiState.Error, ct);

        await WaitUntilAsync(() =>
            coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == failingTaskId).Status == CollabTaskStatus.Failed, ct);
        await WaitUntilAsync(() => parentBridge.CountForThread(failingThreadId) == 1, ct);

        // Let any stray async continuations settle, then verify caller was notified exactly once.
        await Task.Delay(100, ct);
        Frames.Pump();

        var failedTask = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == failingTaskId);
        Assert.Equal(CollabTaskStatus.Failed, failedTask.Status);
        Assert.Contains("ACP binary crashed during handshake", failedTask.ResultSummary ?? "", StringComparison.Ordinal);
        Assert.Equal(CollabThreadStatus.Blocked, coordinator.FindThread(failingThreadId)!.Status);
        Assert.Equal(1, parentBridge.CountForThread(failingThreadId));
        Assert.Contains("Status: blocked", parentBridge.PromptsForThread(failingThreadId)[0], StringComparison.Ordinal);

        // Unrelated sibling was neither settled nor cancelled.
        var siblingTask = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == siblingTaskId);
        Assert.Equal(CollabTaskStatus.Running, siblingTask.Status);
        Assert.Equal(0, siblingSession!.CancelCount);

        siblingBlock.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await tabs.ReleaseAllAsync();
    }

    [AvaloniaFact, FreshApp]
    public async Task Acp_auth_required_connect_failure_settles_delegated_task_and_notifies_caller_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-acp-auth-");
        var tabs = AppHost.Get<ITabContentRegistry>();
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();

        fakeFactory.ConfigureNext = s =>
        {
            s.StartHandler = (_, _) => throw new AcpAuthRequiredException("Sign in required", []);
        };

        var delegated = await DelegateAsync(coordinator, parent.Id, "opencode", "Auth-gated ACP job", ct, surface: "gui");
        var taskId = delegated["task_id"]!.GetValue<string>();
        var childId = delegated["session_id"]!.GetValue<string>();
        var threadId = delegated["thread_id"]!.GetValue<string>();

        var view = Assert.IsType<AgentGuiTabView>(tabs.Find(childId));
        var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);
        await WaitUntilAsync(() => vm.IsAuthRequired && vm.State == AgentGuiState.Disconnected, ct);

        await WaitUntilAsync(() =>
            coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status == CollabTaskStatus.Failed, ct);
        await WaitUntilAsync(() => parentBridge.CountForThread(threadId) == 1, ct);

        await Task.Delay(100, ct);
        Frames.Pump();

        Assert.Equal(CollabTaskStatus.Failed, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status);
        Assert.Equal(CollabThreadStatus.Blocked, coordinator.FindThread(threadId)!.Status);
        Assert.Equal(1, parentBridge.CountForThread(threadId));

        await tabs.ReleaseAllAsync();
    }

    [AvaloniaFact, FreshApp]
    public async Task Acp_unexpected_process_exit_settles_task_clears_approval_and_notifies_caller_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-acp-exit-");
        var tabs = AppHost.Get<ITabContentRegistry>();
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();

        // Unrelated sibling stays running.
        var siblingBlock = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeAcpSession? siblingSession = null;
        fakeFactory.ConfigureNext = s =>
        {
            siblingSession = s;
            s.PromptHandler = (_, _, _, _) => siblingBlock.Task;
        };
        var siblingRes = await DelegateAsync(coordinator, parent.Id, "opencode", "Healthy sibling", ct, surface: "gui");
        var siblingTaskId = siblingRes["task_id"]!.GetValue<string>();
        await WaitUntilAsync(() => siblingSession is not null && siblingSession.SentPrompts.Count == 1, ct);

        // Target child connects, starts its prompt, and opens a permission card (WaitingApproval).
        var promptTcs = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeAcpSession? childSession = null;
        fakeFactory.ConfigureNext = s =>
        {
            childSession = s;
            s.PromptHandler = async (session, _, _, token) =>
            {
                _ = session.Callbacks.OnPermissionRequest!(new AcpPermissionRequest
                {
                    ToolCall = new AcpToolCallUpdate { ToolCallId = "tc-1", Title = "Run tests", Name = "Bash" },
                    Options =
                    [
                        new AcpPermissionOption { OptionId = "allow", Name = "Allow", Kind = "allow_once" },
                        new AcpPermissionOption { OptionId = "deny", Name = "Deny", Kind = "reject_once" },
                    ],
                }, token);
                return await promptTcs.Task;
            };
        };

        var childRes = await DelegateAsync(coordinator, parent.Id, "opencode", "Crashing ACP job", ct, surface: "gui");
        var childTaskId = childRes["task_id"]!.GetValue<string>();
        var childThreadId = childRes["thread_id"]!.GetValue<string>();

        await WaitUntilAsync(() => coordinator.GetBubbledApprovalsForParent(parent.Id).Count == 1, ct);
        Assert.Equal(CollabTaskStatus.WaitingApproval, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == childTaskId).Status);

        // Process dies unexpectedly: emit AcpClosedEvent while PromptAsync is still pending.
        childSession!.IsConnected = false;
        childSession.Emit(new AcpClosedEvent(new IOException("ACP process exited with code 137")));

        await WaitUntilAsync(() =>
            coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == childTaskId).Status == CollabTaskStatus.Failed, ct);
        await WaitUntilAsync(() => parentBridge.CountForThread(childThreadId) == 1, ct);

        Assert.Empty(coordinator.GetBubbledApprovalsForParent(parent.Id));

        // Even when the in-flight PromptAsync subsequently faults with the same transport error, caller is still notified only once.
        promptTcs.TrySetException(new IOException("ACP process exited with code 137"));
        await Task.Delay(100, ct);
        Frames.Pump();

        var failed = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == childTaskId);
        Assert.Equal(CollabTaskStatus.Failed, failed.Status);
        Assert.Contains("137", failed.ResultSummary ?? "", StringComparison.Ordinal);
        Assert.Equal(CollabThreadStatus.Blocked, coordinator.FindThread(childThreadId)!.Status);
        Assert.Equal(1, parentBridge.CountForThread(childThreadId));

        // Unrelated sibling stays running and uncancelled.
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == siblingTaskId).Status);
        Assert.Equal(0, siblingSession!.CancelCount);

        siblingBlock.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await tabs.ReleaseAllAsync();
    }

    [AvaloniaFact, FreshApp]
    public async Task Acp_unexpected_exit_with_await_events_watching_task_id_reports_inline_without_duplicate_wakeup()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-acp-await-");
        var tabs = AppHost.Get<ITabContentRegistry>();
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();

        var promptTcs = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeAcpSession? childSession = null;
        fakeFactory.ConfigureNext = s =>
        {
            childSession = s;
            s.PromptHandler = (_, _, _, _) => promptTcs.Task;
        };

        var childRes = await DelegateAsync(coordinator, parent.Id, "opencode", "Awaited crashing job", ct, surface: "gui");
        var taskId = childRes["task_id"]!.GetValue<string>();
        var threadId = childRes["thread_id"]!.GetValue<string>();
        await WaitUntilAsync(() => childSession is not null && childSession.SentPrompts.Count == 1, ct);

        // Caller awaits specifically by task_id (not thread_id).
        var awaitCall = coordinator.CallToolAsync(
            parent.Id,
            "await_events",
            new JsonObject { ["ids"] = new JsonArray(taskId), ["timeout_seconds"] = 10 },
            ct);
        await WaitUntilAsync(() => coordinator.PendingAwaitCount == 1, ct);

        childSession!.IsConnected = false;
        childSession.Emit(new AcpClosedEvent(new IOException("ACP died mid-turn")));

        var awaitBody = Body(await awaitCall.WaitAsync(TimeSpan.FromSeconds(5), ct));
        var reported = Assert.Single(awaitBody["tasks"]!.AsArray())!;
        Assert.Equal(taskId, reported["task_id"]!.GetValue<string>());
        Assert.Equal("failed", reported["status"]!.GetValue<string>());

        await Task.Delay(100, ct);
        Frames.Pump();
        Assert.Equal(0, parentBridge.CountForThread(threadId));

        promptTcs.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await tabs.ReleaseAllAsync();
    }

    // ==================== BUG-03: PTY Start Failure & Unexpected Process Exit ====================

    [AvaloniaFact, FreshApp]
    public async Task Pty_start_failure_settles_delegated_task_and_notifies_caller_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-pty-startfail-");
        var tabs = AppHost.Get<ITabContentRegistry>();
        var ptyFactory = AppHost.Get<FakeTerminalConnectionFactory>();

        // Healthy sibling terminal task (shown so PTY starts on both baseline and fixed code).
        var siblingRes = await DelegateAsync(coordinator, parent.Id, "codex", "Healthy PTY sibling", ct, surface: "terminal");
        var siblingTaskId = siblingRes["task_id"]!.GetValue<string>();
        var siblingChildId = siblingRes["session_id"]!.GetValue<string>();
        var siblingWindow = ShowInWindow(Assert.IsType<TerminalTabView>(tabs.Find(siblingChildId)));

        try
        {
            await WaitUntilAsync(() => ptyFactory.Connections.Count == 1, ct);

            // Inject PTY spawn failure on next start.
            ptyFactory.NextStartFailure = new InvalidOperationException("pty spawn failed");
            var failingRes = await DelegateAsync(coordinator, parent.Id, "codex", "Failing PTY job", ct, surface: "terminal");
            var failingTaskId = failingRes["task_id"]!.GetValue<string>();
            var failingChildId = failingRes["session_id"]!.GetValue<string>();
            var failingThreadId = failingRes["thread_id"]!.GetValue<string>();

            // Mount the failing terminal in a window too so this test isolates BUG-03 start-failure handling from hidden-terminal deferral.
            var failingWindow = ShowInWindow(Assert.IsType<TerminalTabView>(tabs.Find(failingChildId)));
            try
            {
                await WaitUntilAsync(() => ptyFactory.Started.Count >= 2, ct);
                await WaitUntilAsync(() =>
                    coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == failingTaskId).Status == CollabTaskStatus.Failed, ct);
                await WaitUntilAsync(() => parentBridge.CountForThread(failingThreadId) == 1, ct);

                await Task.Delay(100, ct);
                Frames.Pump();

                var failedTask = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == failingTaskId);
                Assert.Equal(CollabTaskStatus.Failed, failedTask.Status);
                Assert.Contains("pty spawn failed", failedTask.ResultSummary ?? "", StringComparison.Ordinal);
                Assert.Equal(CollabThreadStatus.Blocked, coordinator.FindThread(failingThreadId)!.Status);
                Assert.Equal(1, parentBridge.CountForThread(failingThreadId));

                // Healthy sibling PTY stays running.
                Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == siblingTaskId).Status);
                Assert.False(ptyFactory.Connections[0].IsEnded);
            }
            finally
            {
                failingWindow.Content = null;
                failingWindow.Close();
            }
        }
        finally
        {
            siblingWindow.Content = null;
            siblingWindow.Close();
            await tabs.ReleaseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Pty_unexpected_process_exit_settles_delegated_task_and_notifies_caller_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-pty-exit-");
        var tabs = AppHost.Get<ITabContentRegistry>();
        var ptyFactory = AppHost.Get<FakeTerminalConnectionFactory>();

        var siblingRes = await DelegateAsync(coordinator, parent.Id, "codex", "Healthy PTY sibling", ct, surface: "terminal");
        var siblingTaskId = siblingRes["task_id"]!.GetValue<string>();
        var siblingChildId = siblingRes["session_id"]!.GetValue<string>();
        var siblingWindow = ShowInWindow(Assert.IsType<TerminalTabView>(tabs.Find(siblingChildId)));

        var targetRes = await DelegateAsync(coordinator, parent.Id, "codex", "Crashing PTY job", ct, surface: "terminal");
        var targetTaskId = targetRes["task_id"]!.GetValue<string>();
        var targetChildId = targetRes["session_id"]!.GetValue<string>();
        var targetThreadId = targetRes["thread_id"]!.GetValue<string>();
        var targetWindow = ShowInWindow(Assert.IsType<TerminalTabView>(tabs.Find(targetChildId)));

        try
        {
            await WaitUntilAsync(() => ptyFactory.Connections.Count == 2, ct);
            var targetPty = ptyFactory.Connections[1];

            // Unexpected non-zero exit while delegated task is still running.
            targetPty.Exit(1);

            await WaitUntilAsync(() =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == targetTaskId).Status == CollabTaskStatus.Failed, ct);
            await WaitUntilAsync(() => parentBridge.CountForThread(targetThreadId) == 1, ct);

            await Task.Delay(100, ct);
            Frames.Pump();

            var failed = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == targetTaskId);
            Assert.Equal(CollabTaskStatus.Failed, failed.Status);
            Assert.Equal(CollabThreadStatus.Blocked, coordinator.FindThread(targetThreadId)!.Status);
            Assert.Equal(1, parentBridge.CountForThread(targetThreadId));

            // Sibling PTY was untouched.
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == siblingTaskId).Status);
            Assert.False(ptyFactory.Connections[0].IsEnded);
        }
        finally
        {
            targetWindow.Content = null;
            targetWindow.Close();
            siblingWindow.Content = null;
            siblingWindow.Close();
            await tabs.ReleaseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Pty_exit_on_registry_session_without_tab_settles_task_once_and_ignores_already_completed_task()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-pty-reg-exit-");
        var projects = AppHost.Get<IProjectService>();
        var ptyFactory = AppHost.Get<FakeTerminalConnectionFactory>();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var launcher = AppHost.Get<IItemLaunchService>();

        // 1. Registry-only PTY session (no TerminalTabView built) exits unexpectedly while owing a task.
        var cli = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Headless CLI", Surface = AgentSurface.Terminal };
        projects.AddItem(project, cli, activate: false);
        sessions.GetOrStart(
            cli.Id, launcher.Plan(project, cli), ptyFactory, new TerminalSize(80, 24, 0, 0),
            TerminalPalettes.Dark, ColorScheme.Dark, 1000, out _);
        var cliPty = ptyFactory.Connections.Last();

        var taskRes = await DelegateAsync(coordinator, parent.Id, cli.Id, "Headless PTY task", ct);
        var taskId = taskRes["task_id"]!.GetValue<string>();
        var threadId = taskRes["thread_id"]!.GetValue<string>();

        cliPty.Exit(137);
        await WaitUntilAsync(() =>
            coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status == CollabTaskStatus.Failed, ct);
        await WaitUntilAsync(() => parentBridge.CountForThread(threadId) == 1, ct);

        // 2. A second registry-only PTY session completes normally via reply_thread, then exits cleanly:
        //    must stay Completed and send no failure notification.
        var doneCli = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Done CLI", Surface = AgentSurface.Terminal };
        projects.AddItem(project, doneCli, activate: false);
        sessions.GetOrStart(
            doneCli.Id, launcher.Plan(project, doneCli), ptyFactory, new TerminalSize(80, 24, 0, 0),
            TerminalPalettes.Dark, ColorScheme.Dark, 1000, out _);
        var donePty = ptyFactory.Connections.Last();

        var doneRes = await DelegateAsync(coordinator, parent.Id, doneCli.Id, "Completing PTY task", ct);
        var doneTaskId = doneRes["task_id"]!.GetValue<string>();
        var doneThreadId = doneRes["thread_id"]!.GetValue<string>();
        Assert.Null((await coordinator.CallToolAsync(doneCli.Id, "reply_thread", new JsonObject
        {
            ["thread_id"] = doneThreadId,
            ["message"] = "All green",
            ["status"] = "agreed",
        }, ct))["isError"]);
        await WaitUntilAsync(() => parentBridge.CountForThread(doneThreadId) == 1, ct);

        donePty.Exit(0);
        await Task.Delay(100, ct);
        Frames.Pump();

        Assert.Equal(CollabTaskStatus.Completed, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == doneTaskId).Status);
        Assert.Equal(1, parentBridge.CountForThread(doneThreadId));

        await sessions.CloseAllAsync();
    }

    // ==================== BUG-03: Removal (Item & Project) ====================

    [AvaloniaFact, FreshApp]
    public async Task Removing_delegated_item_or_project_settles_affected_tasks_without_interrupting_unrelated_sessions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, parent, _, coordinator) = await SetUpParentAsync("octopus-0b-removal-");
        var projects = AppHost.Get<IProjectService>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();

        // Keep spawned ACP turns pending so we control when they settle.
        var blocks = new List<TaskCompletionSource<AcpPromptResult>>();
        fakeFactory.ConfigureNext = s =>
        {
            var tcs = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            blocks.Add(tcs);
            s.PromptHandler = (_, _, _, _) => tcs.Task;
        };

        var sibling = await DelegateAsync(coordinator, parent.Id, "opencode", "Survivor sibling", ct, surface: "gui");
        var siblingTaskId = sibling["task_id"]!.GetValue<string>();

        // 1. Remove a delegated GUI child item while it holds a bubbled approval.
        var guiChild = await DelegateAsync(coordinator, parent.Id, "opencode", "Removed GUI child", ct, surface: "gui");
        var guiChildId = guiChild["session_id"]!.GetValue<string>();
        var guiTaskId = guiChild["task_id"]!.GetValue<string>();
        coordinator.ReportChildApproval(new CollabBubbledApproval("apr-rm", parent.Id, guiChildId, "opencode", "Removed GUI child", "Bash", null, null, _ => { }));
        Assert.Single(coordinator.GetBubbledApprovalsForParent(parent.Id));

        var awaitRemoved = coordinator.CallToolAsync(parent.Id, "await_events", new JsonObject { ["ids"] = new JsonArray(guiTaskId), ["timeout_seconds"] = 5 }, ct);
        await WaitUntilAsync(() => coordinator.PendingAwaitCount == 1, ct);

        projects.RemoveItem(guiChildId);
        var awaitBody = Body(await awaitRemoved.WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Equal("cancelled", Assert.Single(awaitBody["tasks"]!.AsArray())!["status"]!.GetValue<string>());
        Assert.Empty(coordinator.GetBubbledApprovalsForParent(parent.Id));
        Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == guiTaskId).Status);

        // 2. Remove a delegated Terminal child item.
        var termChild = await DelegateAsync(coordinator, parent.Id, "codex", "Removed PTY child", ct, surface: "terminal");
        var termChildId = termChild["session_id"]!.GetValue<string>();
        var termTaskId = termChild["task_id"]!.GetValue<string>();
        projects.RemoveItem(termChildId);
        Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == termTaskId).Status);

        // 3. Remove an entire sibling project in a merged workspace group that owns a delegated child item.
        var homeProject = projects.FindProjectOf(parent.Id)!;
        var group = projects.AddGroup("Merged");
        projects.SetGroupMergeWorkspaces(group.Id, true);
        var otherDir = Directory.CreateTempSubdirectory("octopus-0b-removal-other-").FullName;
        var otherProject = projects.AddProject(otherDir, activate: false);
        projects.MoveProjectToGroup(homeProject.Id, group.Id);
        projects.MoveProjectToGroup(otherProject.Id, group.Id);
        var crossPeer = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Cross Project Peer", Surface = AgentSurface.Gui };
        projects.AddItem(otherProject, crossPeer, activate: false);
        coordinator.RegisterGuiSession(new SpyGuiBridge(crossPeer.Id, "opencode", crossPeer.Title, otherDir));

        var crossRes = await DelegateAsync(coordinator, parent.Id, crossPeer.Id, "Cross-project task", ct);
        var crossTaskId = crossRes["task_id"]!.GetValue<string>();
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == crossTaskId).Status);

        projects.RemoveProject(otherProject.Id);
        Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == crossTaskId).Status);

        // Unrelated survivor sibling stays Running throughout all three removals.
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == siblingTaskId).Status);

        foreach (var b in blocks) b.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await tabs.ReleaseAllAsync();
    }

    // ==================== Issue #39 Hypothesis: Hidden Terminal Startup vs GUI Surface ====================

    [AvaloniaFact, FreshApp]
    public async Task Hidden_terminal_delegation_starts_pty_immediately_without_double_spawn_when_shown()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);

        var (_, parent, _, coordinator) = await SetUpParentAsync("octopus-0b-issue39-pty-");
        var tabs = AppHost.Get<ITabContentRegistry>();
        var ptyFactory = AppHost.Get<FakeTerminalConnectionFactory>();
        var sessions = AppHost.Get<TerminalSessionRegistry>();

        // Delegate to "反重力" (agy) with no surface override: defaults to Terminal, added with activate: false (never shown).
        var delegated = await DelegateAsync(coordinator, parent.Id, "反重力", "Inspect startup", ct);
        var childId = delegated["session_id"]!.GetValue<string>();
        Assert.Equal("agy", delegated["agent_id"]!.GetValue<string>());
        Assert.Equal("terminal", delegated["surface"]!.GetValue<string>());

        Frames.Pump();

        // BEFORE the user ever clicks the hidden tab, the PTY must already have spawned with the initial delegated task.
        Assert.Single(ptyFactory.Connections);
        var startedSpec = Assert.Single(ptyFactory.Started);
        Assert.Contains(startedSpec.Arguments, a => a.Contains("Inspect startup", StringComparison.Ordinal));
        var session = sessions.Find(childId);
        Assert.NotNull(session);
        Assert.True(session!.IsRunning);

        // Status reported by list_peers must not mislead the caller with "idle" right after delegation.
        var peers = Body(await coordinator.CallToolAsync(parent.Id, "list_peers", null, ct));
        var childPeer = peers["active_sessions"]!.AsArray().Single(p => p!["session_id"]!.GetValue<string>() == childId)!;
        Assert.NotEqual("idle", childPeer["status"]!.GetValue<string>());

        // Clicking/showing the hidden tab later attaches it to a window and lays it out: must NOT spawn a second PTY.
        var tabView = Assert.IsType<TerminalTabView>(tabs.Find(childId));
        var window = ShowInWindow(tabView);
        try
        {
            Assert.Single(ptyFactory.Connections);
            Assert.Same(session, sessions.Find(childId));
        }
        finally
        {
            window.Content = null;
            window.Close();
            await tabs.ReleaseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Hidden_gui_delegation_starts_without_double_connect_when_shown()
    {
        var ct = TestContext.Current.CancellationToken;
        AppHost.Get<ISettingsStore>().Update(s =>
        {
            s.Agents.Profiles["agy"] = new AgentProfile { Command = "/usr/local/bin/agy" };
        });

        var (_, parent, _, coordinator) = await SetUpParentAsync("octopus-0b-issue39-gui-");
        var tabs = AppHost.Get<ITabContentRegistry>();
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();

        var promptReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fakeFactory.ConfigureNext = s =>
        {
            s.PromptHandler = (_, text, _, _) =>
            {
                promptReceived.TrySetResult(text);
                return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
            };
        };

        var delegated = await DelegateAsync(coordinator, parent.Id, "反重力", "Inspect GUI startup", ct, surface: "gui");
        var childId = delegated["session_id"]!.GetValue<string>();

        var prompt = await promptReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Contains("Inspect GUI startup", prompt, StringComparison.Ordinal);
        Assert.Single(fakeFactory.Created);

        // Showing the GUI tab later attaches it to the visual tree: must NOT start a second ACP session or re-send prompt.
        var view = Assert.IsType<AgentGuiTabView>(tabs.Find(childId));
        var window = ShowInWindow(view);
        try
        {
            Assert.Single(fakeFactory.Created);
            Assert.Single(fakeFactory.Created[0].SentPrompts);
        }
        finally
        {
            window.Content = null;
            window.Close();
            await tabs.ReleaseAllAsync();
        }
    }

    // ==================== Non-Regression Control: Normal Idle & Approval ====================

    [AvaloniaFact, FreshApp]
    public async Task Normal_parent_idle_and_approval_do_not_cancel_existing_children()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-0b-idle-approval-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();

        // 1. Start a real parent AgentGuiTabViewModel (opencode native ACP).
        var parentItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Lead", Surface = AgentSurface.Gui };
        projects.AddItem(project, parentItem);
        var parentApprovalPrompt = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentPromptDone = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeAcpSession? parentSession = null;
        fakeFactory.ConfigureNext = s =>
        {
            parentSession = s;
            s.PromptHandler = async (session, _, _, token) =>
            {
                var decision = await session.Callbacks.OnPermissionRequest!(new AcpPermissionRequest
                {
                    ToolCall = new AcpToolCallUpdate { ToolCallId = "parent-tc-1", Title = "Read repo", Name = "Read" },
                    Options =
                    [
                        new AcpPermissionOption { OptionId = "allow", Name = "Allow", Kind = "allow_once" },
                        new AcpPermissionOption { OptionId = "deny", Name = "Deny", Kind = "reject_once" },
                    ],
                }, token);
                parentApprovalPrompt.TrySetResult(decision);
                return await parentPromptDone.Task;
            };
        };
        var parentControl = Assert.IsType<AgentGuiTabView>(tabs.GetOrCreate(project, parentItem));
        var parentVm = Assert.IsType<AgentGuiTabViewModel>(parentControl.DataContext);
        await parentVm.ConnectAsync();
        Assert.Equal(AgentGuiState.Ready, parentVm.State);

        // 2. Parent delegates a child GUI task whose turn requests permission and then finishes when allowed.
        var childApprovalDone = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeAcpSession? childSession = null;
        fakeFactory.ConfigureNext = s =>
        {
            childSession = s;
            s.PromptHandler = async (session, _, _, token) =>
            {
                var decision = await session.Callbacks.OnPermissionRequest!(new AcpPermissionRequest
                {
                    ToolCall = new AcpToolCallUpdate { ToolCallId = "child-tc-1", Title = "Edit file", Name = "Edit" },
                    Options =
                    [
                        new AcpPermissionOption { OptionId = "allow", Name = "Allow", Kind = "allow_once" },
                        new AcpPermissionOption { OptionId = "deny", Name = "Deny", Kind = "reject_once" },
                    ],
                }, token);
                childApprovalDone.TrySetResult(decision);
                return new AcpPromptResult { StopReason = "end_turn" };
            };
        };
        var childRes = await DelegateAsync(coordinator, parentItem.Id, "opencode", "Child task", ct, surface: "gui");
        var childTaskId = childRes["task_id"]!.GetValue<string>();
        await WaitUntilAsync(() => coordinator.GetBubbledApprovalsForParent(parentItem.Id).Count == 1, ct);
        Assert.Equal(CollabTaskStatus.WaitingApproval, coordinator.GetTasksForItem(parentItem.Id).Single(t => t.TaskId == childTaskId).Status);

        // 3. Parent starts its own turn, enters Waiting (approval), gets approved, and finishes normally (ordinary idle).
        parentVm.InputText = "Plan next step";
        var parentTurn = parentVm.SendPromptAsync();
        await WaitUntilAsync(() => parentVm.State == AgentGuiState.Waiting && parentVm.PendingApprovals.Count == 1, ct);

        // Parent waiting on its own approval must NOT cancel the child.
        Assert.Equal(CollabTaskStatus.WaitingApproval, coordinator.GetTasksForItem(parentItem.Id).Single(t => t.TaskId == childTaskId).Status);
        Assert.Equal(0, childSession!.CancelCount);

        Assert.True(parentVm.PendingApprovals[0].TryRespond("allow"));
        Assert.Equal("allow", await parentApprovalPrompt.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        parentPromptDone.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await parentTurn;
        Frames.Pump();

        // Parent returning to normal idle must NOT cancel the child.
        Assert.Equal(AgentGuiState.Ready, parentVm.State);
        Assert.Equal(CollabTaskStatus.WaitingApproval, coordinator.GetTasksForItem(parentItem.Id).Single(t => t.TaskId == childTaskId).Status);
        Assert.Equal(0, childSession.CancelCount);

        // 4. Approving the child's bubbled permission lets the child resume and complete cleanly.
        var bubbled = Assert.Single(coordinator.GetBubbledApprovalsForParent(parentItem.Id));
        Assert.True(bubbled.TryRespond(allow: true));
        Assert.Equal("allow", await childApprovalDone.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        await WaitUntilAsync(() =>
            coordinator.GetTasksForItem(parentItem.Id).Single(t => t.TaskId == childTaskId).Status == CollabTaskStatus.Completed, ct);

        await tabs.ReleaseAllAsync();
    }

    // ==================== Reconciliation: Integrated Queue + Process Loss + No Duplicate Wake After Reply ====================

    [AvaloniaFact, FreshApp]
    public async Task Acp_process_loss_settles_all_queued_unsettled_tasks_without_duplicate_wake_after_explicit_reply()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-acp-queue-loss-");
        var projects = AppHost.Get<IProjectService>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();

        var turn1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn1Block = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeAcpSession? workerSession = null;
        fakeFactory.ConfigureNext = s =>
        {
            workerSession = s;
            s.PromptHandler = (_, _, _, _) =>
            {
                turn1Started.TrySetResult();
                return turn1Block.Task;
            };
        };

        var workerItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Shared Worker", Surface = AgentSurface.Gui };
        projects.AddItem(project, workerItem, activate: false);
        var workerView = Assert.IsType<AgentGuiTabView>(tabs.GetOrCreate(project, workerItem));
        var workerVm = Assert.IsType<AgentGuiTabViewModel>(workerView.DataContext);
        await workerVm.ConnectAsync();
        Assert.Equal(AgentGuiState.Ready, workerVm.State);

        var resA = await DelegateAsync(coordinator, parent.Id, workerItem.Id, "Queued Job A", ct);
        var taskA = resA["task_id"]!.GetValue<string>();
        var threadA = resA["thread_id"]!.GetValue<string>();
        await turn1Started.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        var resB = await DelegateAsync(coordinator, parent.Id, workerItem.Id, "Queued Job B", ct);
        var taskB = resB["task_id"]!.GetValue<string>();
        var threadB = resB["thread_id"]!.GetValue<string>();

        var resC = await DelegateAsync(coordinator, parent.Id, workerItem.Id, "Queued Job C", ct);
        var taskC = resC["task_id"]!.GetValue<string>();
        var threadC = resC["thread_id"]!.GetValue<string>();
        Assert.Equal(2, workerVm.QueuedPrompts.Count);

        // Worker explicitly replies to Job A before the process crashes (leaving a SettledExplicitly marker at slot 0).
        Assert.Null((await coordinator.CallToolAsync(workerItem.Id, "reply_thread", new JsonObject
        {
            ["thread_id"] = threadA,
            ["message"] = "Job A already done",
            ["status"] = "agreed",
        }, ct))["isError"]);
        await WaitUntilAsync(() => parentBridge.CountForThread(threadA) == 1, ct);
        Assert.Equal(CollabTaskStatus.Completed, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status);

        // Genuine ACP process loss: must settle ALL unsettled queued tasks (B and C), without touching already-replied A.
        workerSession!.IsConnected = false;
        workerSession.Emit(new AcpClosedEvent(new IOException("ACP worker crashed")));

        await WaitUntilAsync(() =>
            coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status == CollabTaskStatus.Failed
            && coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskC).Status == CollabTaskStatus.Failed, ct);
        await WaitUntilAsync(() => parentBridge.CountForThread(threadB) == 1 && parentBridge.CountForThread(threadC) == 1, ct);

        // In-flight PromptAsync faults afterwards: must NOT produce duplicate wakes or flip state back to Ready.
        turn1Block.TrySetException(new IOException("ACP worker crashed"));
        await Task.Delay(100, ct);
        Frames.Pump();

        Assert.Equal(CollabTaskStatus.Completed, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status);
        Assert.Equal(CollabThreadStatus.Agreed, coordinator.FindThread(threadA)!.Status);
        Assert.Equal(1, parentBridge.CountForThread(threadA));

        Assert.Equal(CollabTaskStatus.Failed, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status);
        Assert.Equal(CollabThreadStatus.Blocked, coordinator.FindThread(threadB)!.Status);
        Assert.Equal(1, parentBridge.CountForThread(threadB));

        Assert.Equal(CollabTaskStatus.Failed, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskC).Status);
        Assert.Equal(CollabThreadStatus.Blocked, coordinator.FindThread(threadC)!.Status);
        Assert.Equal(1, parentBridge.CountForThread(threadC));

        Assert.Empty(workerVm.QueuedPrompts);
        Assert.Equal(AgentGuiState.Disconnected, workerVm.State);

        await tabs.ReleaseAllAsync();
    }

    [AvaloniaFact, FreshApp]
    public async Task Pty_process_loss_settles_all_queued_unsettled_tasks_without_duplicate_wake_after_explicit_reply()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-pty-queue-loss-");
        var projects = AppHost.Get<IProjectService>();
        var ptyFactory = AppHost.Get<FakeTerminalConnectionFactory>();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var launcher = AppHost.Get<IItemLaunchService>();

        var cli = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Shared CLI", Surface = AgentSurface.Terminal };
        projects.AddItem(project, cli, activate: false);
        sessions.GetOrStart(
            cli.Id, launcher.Plan(project, cli), ptyFactory, new TerminalSize(80, 24, 0, 0),
            TerminalPalettes.Dark, ColorScheme.Dark, 1000, out _);
        var cliPty = ptyFactory.Connections.Last();

        var resA = await DelegateAsync(coordinator, parent.Id, cli.Id, "CLI Job A", ct);
        var taskA = resA["task_id"]!.GetValue<string>();
        var threadA = resA["thread_id"]!.GetValue<string>();

        var resB = await DelegateAsync(coordinator, parent.Id, cli.Id, "CLI Job B", ct);
        var taskB = resB["task_id"]!.GetValue<string>();
        var threadB = resB["thread_id"]!.GetValue<string>();

        var resC = await DelegateAsync(coordinator, parent.Id, cli.Id, "CLI Job C", ct);
        var taskC = resC["task_id"]!.GetValue<string>();
        var threadC = resC["thread_id"]!.GetValue<string>();

        // Explicit reply on Job A leaves a SettledExplicitly marker at slot 0 while B and C remain queued.
        Assert.Null((await coordinator.CallToolAsync(cli.Id, "reply_thread", new JsonObject
        {
            ["thread_id"] = threadA,
            ["message"] = "CLI Job A complete",
            ["status"] = "agreed",
        }, ct))["isError"]);
        await WaitUntilAsync(() => parentBridge.CountForThread(threadA) == 1, ct);

        // Genuine PTY process exit settles B and C without touching A or sending a duplicate wake.
        cliPty.Exit(137);
        await WaitUntilAsync(() =>
            coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status == CollabTaskStatus.Failed
            && coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskC).Status == CollabTaskStatus.Failed, ct);
        await WaitUntilAsync(() => parentBridge.CountForThread(threadB) == 1 && parentBridge.CountForThread(threadC) == 1, ct);

        await Task.Delay(100, ct);
        Frames.Pump();

        Assert.Equal(CollabTaskStatus.Completed, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status);
        Assert.Equal(CollabThreadStatus.Agreed, coordinator.FindThread(threadA)!.Status);
        Assert.Equal(1, parentBridge.CountForThread(threadA));

        Assert.Equal(CollabTaskStatus.Failed, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status);
        Assert.Equal(CollabThreadStatus.Blocked, coordinator.FindThread(threadB)!.Status);
        Assert.Equal(1, parentBridge.CountForThread(threadB));

        Assert.Equal(CollabTaskStatus.Failed, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskC).Status);
        Assert.Equal(CollabThreadStatus.Blocked, coordinator.FindThread(threadC)!.Status);
        Assert.Equal(1, parentBridge.CountForThread(threadC));

        await sessions.CloseAllAsync();
    }

    // ==================== Finding 4: Hidden Resume Retries ====================

    private static string? ExtractMcpConfigPath(LaunchSpec spec)
    {
        for (var i = 0; i < spec.Arguments.Count; i++)
        {
            if (spec.Arguments[i] == "--mcp-config" && i + 1 < spec.Arguments.Count)
            {
                return spec.Arguments[i + 1];
            }
            var arg = spec.Arguments[i];
            var idx = arg.IndexOf("--mcp-config", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var rest = arg[(idx + "--mcp-config".Length)..].TrimStart();
                if (rest.Length == 0) continue;
                if (rest[0] is '\'' or '"')
                {
                    var quote = rest[0];
                    var end = rest.IndexOf(quote, 1);
                    if (end > 1) return rest[1..end];
                }
                var space = rest.IndexOfAny([' ', '\t', '\r', '\n']);
                return space > 0 ? rest[..space] : rest;
            }
        }
        return null;
    }

    [AvaloniaFact, FreshApp]
    public async Task Hidden_resumed_terminal_quick_exit_settles_accepted_work_retries_hidden_and_handles_replacement_task()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-hidden-resume-retry-");
        var projects = AppHost.Get<IProjectService>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var ptyFactory = AppHost.Get<FakeTerminalConnectionFactory>();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var signals = AppHost.Get<ISignalHub>();

        var item = new ProjectItem
        {
            Kind = ProjectItemKind.Agent,
            AgentId = "claude",
            Title = "Resumed Hidden CLI",
            Surface = AgentSurface.Terminal,
            Launch = new AgentLaunchOptions { Resume = true },
        };
        projects.AddItem(project, item, activate: false);
        Assert.True(AppHost.Get<IItemLaunchService>().Resumes(item));

        var first = await DelegateAsync(coordinator, parent.Id, item.Id, "Work on resumed PTY", ct);
        var firstTaskId = first["task_id"]!.GetValue<string>();
        var firstThreadId = first["thread_id"]!.GetValue<string>();

        var pty1 = Assert.Single(ptyFactory.Connections);
        var session1 = sessions.Find(item.Id);
        Assert.NotNull(session1);
        Assert.True(session1!.IsRunning);

        // Quick non-zero exit triggers the automatic fresh retry while the tab is still hidden.
        pty1.Exit(1);
        await WaitUntilAsync(
            () => ptyFactory.Connections.Count == 2
                && sessions.Find(item.Id) is { IsRunning: true } s
                && !ReferenceEquals(s, session1)
                && coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == firstTaskId).Status == CollabTaskStatus.Failed,
            ct,
            maxTicks: 60);
        await WaitUntilAsync(() => parentBridge.CountForThread(firstThreadId) == 1, ct, maxTicks: 60);

        Assert.Equal(CollabTaskStatus.Failed, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == firstTaskId).Status);
        Assert.Equal(CollabThreadStatus.Blocked, coordinator.FindThread(firstThreadId)!.Status);
        Assert.Equal(1, parentBridge.CountForThread(firstThreadId));

        var session2 = sessions.Find(item.Id);
        Assert.NotNull(session2);
        Assert.True(session2!.IsRunning);
        Assert.NotSame(session1, session2);

        // Control: replacement work delivered to the retried hidden PTY completes cleanly on its valid Done signal.
        var second = await DelegateAsync(coordinator, parent.Id, item.Id, "Replacement work on retried PTY", ct);
        var secondTaskId = second["task_id"]!.GetValue<string>();
        var secondThreadId = second["thread_id"]!.GetValue<string>();
        Assert.Equal(2, ptyFactory.Connections.Count);

        var pty2 = ptyFactory.Connections[1];
        await WaitUntilAsync(() => pty2.Written.Any(w => w.Contains("Replacement work on retried PTY", StringComparison.Ordinal)), ct, maxTicks: 60);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        signals.Publish(new AgentSignal("claude", "stop", item.Id, "", now, now, "{}", session2.CurrentLinkId));

        await WaitUntilAsync(() =>
            coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == secondTaskId).Status == CollabTaskStatus.Completed, ct, maxTicks: 60);
        await WaitUntilAsync(() => parentBridge.CountForThread(secondThreadId) == 1, ct, maxTicks: 60);

        // Showing the hidden tab later reuses the retried session without spawning a third PTY.
        var view = Assert.IsType<TerminalTabView>(tabs.Find(item.Id));
        var window = ShowInWindow(view);
        try
        {
            Assert.Equal(2, ptyFactory.Connections.Count);
            Assert.Same(session2, sessions.Find(item.Id));
        }
        finally
        {
            window.Content = null;
            window.Close();
            await tabs.ReleaseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task User_visible_resumed_terminal_quick_exit_still_retries_fresh()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, _, _, _) = await SetUpParentAsync("octopus-0b-visible-resume-retry-");
        var projects = AppHost.Get<IProjectService>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var ptyFactory = AppHost.Get<FakeTerminalConnectionFactory>();
        var sessions = AppHost.Get<TerminalSessionRegistry>();

        var item = new ProjectItem
        {
            Kind = ProjectItemKind.Agent,
            AgentId = "claude",
            Title = "Visible Resumed CLI",
            Surface = AgentSurface.Terminal,
            Launch = new AgentLaunchOptions { Resume = true },
        };
        projects.AddItem(project, item, activate: false);
        Assert.True(AppHost.Get<IItemLaunchService>().Resumes(item));

        var tabView = Assert.IsType<TerminalTabView>(tabs.GetOrCreate(project, item));
        var window = ShowInWindow(tabView);
        try
        {
            await WaitUntilAsync(() => ptyFactory.Connections.Count == 1, ct, maxTicks: 60);
            var firstSession = sessions.Find(item.Id);
            Assert.NotNull(firstSession);

            ptyFactory.Connections[0].Exit(1);
            await WaitUntilAsync(
                () => ptyFactory.Connections.Count == 2
                    && sessions.Find(item.Id) is { IsRunning: true } next
                    && !ReferenceEquals(next, firstSession),
                ct,
                maxTicks: 60);
        }
        finally
        {
            window.Content = null;
            window.Close();
            await tabs.ReleaseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Hidden_resumed_terminal_quick_exit_rotates_credential_retries_hidden_and_settles_prior_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpFwd = Directory.CreateTempSubdirectory("octopus-0b-hidden-cred-fwd-").FullName;
        var prevForwarder = Environment.GetEnvironmentVariable(ItemLaunchService.ForwarderVariable);
        try
        {
            var fakeForwarder = Path.Combine(tmpFwd, "octopus-signal");
            File.WriteAllText(fakeForwarder, "");
            Environment.SetEnvironmentVariable(ItemLaunchService.ForwarderVariable, fakeForwarder);

            var (project, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-hidden-cred-retry-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();
            var ptyFactory = AppHost.Get<FakeTerminalConnectionFactory>();
            var sessions = AppHost.Get<TerminalSessionRegistry>();
            var signals = AppHost.Get<ISignalHub>();
            var server = coordinator.Server;

            var item = new ProjectItem
            {
                Kind = ProjectItemKind.Agent,
                AgentId = "claude",
                Title = "Hidden Credential Retry CLI",
                Surface = AgentSurface.Terminal,
                Launch = new AgentLaunchOptions { Resume = true },
            };
            projects.AddItem(project, item, activate: false);

            var first = await DelegateAsync(coordinator, parent.Id, item.Id, "Work on resumed credentialed PTY", ct);
            var firstTaskId = first["task_id"]!.GetValue<string>();
            var firstThreadId = first["thread_id"]!.GetValue<string>();

            var spec1 = Assert.Single(ptyFactory.Started);
            var token1 = spec1.Environment[CollabMcpServer.TokenEnvVariable];
            var cfg1 = ExtractMcpConfigPath(spec1);
            Assert.False(string.IsNullOrWhiteSpace(token1));
            Assert.NotNull(cfg1);
            Assert.True(File.Exists(cfg1));
            Assert.True(server.TryAuthenticateBearer($"Bearer {token1}", out _));

            var session1 = sessions.Find(item.Id)!;
            ptyFactory.Connections[0].Exit(1);

            await WaitUntilAsync(
                () => ptyFactory.Started.Count == 2
                    && sessions.Find(item.Id) is { IsRunning: true } s
                    && !ReferenceEquals(s, session1)
                    && coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == firstTaskId).Status == CollabTaskStatus.Failed,
                ct,
                maxTicks: 60);
            await WaitUntilAsync(() => parentBridge.CountForThread(firstThreadId) == 1, ct, maxTicks: 60);

            var spec2 = ptyFactory.Started[1];
            var token2 = spec2.Environment[CollabMcpServer.TokenEnvVariable];
            var cfg2 = ExtractMcpConfigPath(spec2);
            Assert.NotEqual(token1, token2);
            Assert.False(server.TryAuthenticateBearer($"Bearer {token1}", out _), "Exited resumed process token remained valid.");
            Assert.False(File.Exists(cfg1), "Exited resumed process config remained on disk.");
            Assert.True(server.TryAuthenticateBearer($"Bearer {token2}", out var caller2), "Retried hidden process token was revoked.");
            Assert.Equal(item.Id, caller2);
            Assert.NotNull(cfg2);
            Assert.True(File.Exists(cfg2), "Retried hidden process config was removed.");

            var session2 = sessions.Find(item.Id)!;
            var second = await DelegateAsync(coordinator, parent.Id, item.Id, "Replacement work with fresh credential", ct);
            var secondTaskId = second["task_id"]!.GetValue<string>();
            await WaitUntilAsync(() => ptyFactory.Connections[1].Written.Any(w => w.Contains("Replacement work with fresh credential", StringComparison.Ordinal)), ct, maxTicks: 60);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            signals.Publish(new AgentSignal("claude", "stop", item.Id, "", now, now, "{}", session2.CurrentLinkId));
            await WaitUntilAsync(() => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == secondTaskId).Status == CollabTaskStatus.Completed, ct, maxTicks: 60);

            await tabs.ReleaseAllAsync();
            Assert.False(server.TryAuthenticateBearer($"Bearer {token2}", out _));
            Assert.False(File.Exists(cfg2));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ItemLaunchService.ForwarderVariable, prevForwarder);
            try { Directory.Delete(tmpFwd, recursive: true); } catch (IOException) { }
        }
    }

    // ==================== Finding 5: Retired ACP Process Callback Ownership ====================

    private static string ExtractAcpBearerToken(AcpMcpServerConfig config)
    {
        var header = config.Headers["Authorization"];
        const string prefix = "Bearer ";
        Assert.StartsWith(prefix, header, StringComparison.Ordinal);
        return header[prefix.Length..];
    }

    [AvaloniaFact, FreshApp]
    public async Task Retired_acp_process_exit_cannot_fail_replacement_work_and_valid_replacement_completes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-retired-acp-exit-");
        var projects = AppHost.Get<IProjectService>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var server = coordinator.Server;

        var worker = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Worker", Surface = AgentSurface.Gui };
        projects.AddItem(project, worker, activate: false);

        fakeFactory.ConfigureNext = s => s.PromptHandler = (_, _, _, _) => Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
        var control = Assert.IsType<AgentGuiTabView>(tabs.GetOrCreate(project, worker));
        var vm = Assert.IsType<AgentGuiTabViewModel>(control.DataContext);
        await vm.ConnectAsync();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var unblock = new ManualResetEventSlim();
        fakeFactory.ConfigureNext = session => session.EventReceived += e =>
        {
            if (e is AcpClosedEvent)
            {
                entered.TrySetResult();
                if (!unblock.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
        };
        await vm.ReconnectAsync();
        var retiring = fakeFactory.Created.Last();
        var retiringToken = ExtractAcpBearerToken(Assert.Single(retiring.Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName));
        Assert.True(server.TryAuthenticateBearer($"Bearer {retiringToken}", out _));

        var oldExit = Task.Run(() => retiring.Emit(new AcpClosedEvent(new IOException("old process exit"))));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        var activeTurn = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fakeFactory.ConfigureNext = session => session.PromptHandler = (_, _, _, token) => activeTurn.Task.WaitAsync(token);

        string taskId;
        string threadId;
        FakeAcpSession replacement;
        string replacementToken;
        try
        {
            await vm.ReconnectAsync();
            replacement = fakeFactory.Created.Last();
            replacementToken = ExtractAcpBearerToken(Assert.Single(replacement.Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName));
            Assert.NotEqual(retiringToken, replacementToken);
            Assert.False(server.TryAuthenticateBearer($"Bearer {retiringToken}", out _));
            Assert.True(server.TryAuthenticateBearer($"Bearer {replacementToken}", out _));

            var delegated = await DelegateAsync(coordinator, parent.Id, worker.Id, "Replacement work", ct);
            taskId = delegated["task_id"]!.GetValue<string>();
            threadId = delegated["thread_id"]!.GetValue<string>();
            await WaitUntilAsync(() => replacement.SentPrompts.Count == 1, ct, maxTicks: 60);
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status);
        }
        finally
        {
            unblock.Set();
        }

        await oldExit.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Frames.Pump();

        var afterOldExit = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId);
        Assert.Equal(CollabTaskStatus.Running, afterOldExit.Status);
        Assert.Equal(AgentGuiState.Working, vm.State);
        Assert.Equal(0, parentBridge.CountForThread(threadId));
        Assert.True(server.TryAuthenticateBearer($"Bearer {replacementToken}", out _), "Retired close revoked replacement credential.");

        // Control: valid late completion from the replacement session still completes normally.
        replacement.Emit(new AcpTextDeltaEvent("assistant", "Replacement finished cleanly", null));
        activeTurn.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });

        await WaitUntilAsync(() =>
            coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status == CollabTaskStatus.Completed, ct, maxTicks: 60);
        await WaitUntilAsync(() => parentBridge.CountForThread(threadId) == 1, ct, maxTicks: 60);
        Assert.Equal(CollabThreadStatus.Agreed, coordinator.FindThread(threadId)!.Status);
        Assert.Contains("Replacement finished cleanly", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).ResultSummary ?? "", StringComparison.Ordinal);

        // Control: genuine unexpected exit of the current replacement session still fails its active task and revokes its credential.
        var secondTurn = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        replacement.PromptHandler = (_, _, _, token) => secondTurn.Task.WaitAsync(token);
        var delegated2 = await DelegateAsync(coordinator, parent.Id, worker.Id, "Work on crashing replacement", ct);
        var taskId2 = delegated2["task_id"]!.GetValue<string>();
        var threadId2 = delegated2["thread_id"]!.GetValue<string>();
        await WaitUntilAsync(() => replacement.SentPrompts.Count == 2, ct, maxTicks: 60);

        replacement.IsConnected = false;
        replacement.Emit(new AcpClosedEvent(new IOException("replacement crashed")));
        await WaitUntilAsync(() =>
            coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId2).Status == CollabTaskStatus.Failed, ct, maxTicks: 60);
        await WaitUntilAsync(() => parentBridge.CountForThread(threadId2) == 1, ct, maxTicks: 60);
        Assert.False(server.TryAuthenticateBearer($"Bearer {replacementToken}", out _));

        secondTurn.TrySetException(new IOException("replacement crashed"));
        await tabs.ReleaseAllAsync();
    }

    [AvaloniaFact, FreshApp]
    public async Task Retired_acp_permission_callback_cannot_hijack_replacement_session()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-retired-acp-perm-");
        var projects = AppHost.Get<IProjectService>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();

        var worker = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Worker", Surface = AgentSurface.Gui, ParentItemId = parent.Id };
        projects.AddItem(project, worker, activate: false);

        var control = Assert.IsType<AgentGuiTabView>(tabs.GetOrCreate(project, worker));
        var vm = Assert.IsType<AgentGuiTabViewModel>(control.DataContext);
        await vm.ConnectAsync();
        var retiring = fakeFactory.Created.Last();

        var replacementTurn = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fakeFactory.ConfigureNext = s => s.PromptHandler = (_, _, _, token) => replacementTurn.Task.WaitAsync(token);
        await vm.ReconnectAsync();
        var replacement = fakeFactory.Created.Last();

        var delegated = await DelegateAsync(coordinator, parent.Id, worker.Id, "Protected replacement turn", ct);
        var taskId = delegated["task_id"]!.GetValue<string>();
        var threadId = delegated["thread_id"]!.GetValue<string>();
        await WaitUntilAsync(() => replacement.SentPrompts.Count == 1 && vm.State == AgentGuiState.Working, ct, maxTicks: 60);

        // A late permission callback from the retired ACP session must be rejected without touching UI or approvals.
        using var stalePermCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var permTask = retiring.Callbacks.OnPermissionRequest!(new AcpPermissionRequest
        {
            ToolCall = new AcpToolCallUpdate { ToolCallId = "stale-tc", Title = "Stale bash", Name = "Bash" },
            Options = [new AcpPermissionOption { OptionId = "allow", Name = "Allow", Kind = "allow_once" }],
        }, stalePermCts.Token);
        Frames.Pump();
        try
        {
            Assert.True(permTask.IsCompletedSuccessfully, "Retired ACP session permission callback should resolve immediately without blocking UI.");
            Assert.Null(await permTask);
            Assert.Equal(AgentGuiState.Working, vm.State);
            Assert.Empty(vm.PendingApprovals);
            Assert.Empty(coordinator.GetBubbledApprovalsForParent(parent.Id));
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status);
        }
        finally
        {
            stalePermCts.Cancel();
        }

        // Valid late completion on the replacement session still succeeds.
        replacement.Emit(new AcpTextDeltaEvent("assistant", "Replacement completed", null));
        replacementTurn.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() =>
            coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status == CollabTaskStatus.Completed, ct, maxTicks: 60);
        await WaitUntilAsync(() => parentBridge.CountForThread(threadId) == 1, ct, maxTicks: 60);

        await tabs.ReleaseAllAsync();
    }

    [AvaloniaFact, FreshApp]
    public async Task Superseded_acp_connect_failure_revokes_own_credential_without_failing_replacement_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-superseded-acp-fail-");
        var projects = AppHost.Get<IProjectService>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var server = coordinator.Server;

        var worker = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Worker", Surface = AgentSurface.Gui };
        projects.AddItem(project, worker, activate: false);

        var gen1Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gen1Fail = new TaskCompletionSource<AcpSessionStartResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fakeFactory.ConfigureNext = s =>
        {
            s.StartAsyncHandler = (_, _, _) =>
            {
                gen1Entered.TrySetResult();
                return gen1Fail.Task;
            };
        };

        var control = Assert.IsType<AgentGuiTabView>(tabs.GetOrCreate(project, worker));
        var vm = Assert.IsType<AgentGuiTabViewModel>(control.DataContext);
        var connect1 = vm.ConnectAsync();
        await gen1Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var session1 = fakeFactory.Created.Last();
        var token1 = ExtractAcpBearerToken(Assert.Single(session1.Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName));

        // Supersede generation 1 with generation 2 and start active work on generation 2.
        var gen2Turn = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fakeFactory.ConfigureNext = s => s.PromptHandler = (_, _, _, token) => gen2Turn.Task.WaitAsync(token);
        await vm.ReconnectAsync();
        var session2 = fakeFactory.Created.Last();
        var token2 = ExtractAcpBearerToken(Assert.Single(session2.Callbacks.McpServers, s => s.Name == CollabMcpServer.ServerName));
        Assert.NotEqual(token1, token2);
        Assert.False(server.TryAuthenticateBearer($"Bearer {token1}", out _));
        Assert.True(server.TryAuthenticateBearer($"Bearer {token2}", out _));

        var delegated = await DelegateAsync(coordinator, parent.Id, worker.Id, "Work on generation 2", ct);
        var taskId = delegated["task_id"]!.GetValue<string>();
        var threadId = delegated["thread_id"]!.GetValue<string>();
        await WaitUntilAsync(() => session2.SentPrompts.Count == 1 && vm.State == AgentGuiState.Working, ct, maxTicks: 60);

        // Superseded generation 1 now faults with a non-cancellation IOException.
        gen1Fail.TrySetException(new IOException("stale connect handshake failed"));
        await connect1.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Frames.Pump();

        Assert.False(server.TryAuthenticateBearer($"Bearer {token1}", out _));
        Assert.True(server.TryAuthenticateBearer($"Bearer {token2}", out _), "Superseded connect failure revoked replacement credential.");
        Assert.Equal(AgentGuiState.Working, vm.State);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status);

        session2.Emit(new AcpTextDeltaEvent("assistant", "Generation 2 completed", null));
        gen2Turn.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() =>
            coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status == CollabTaskStatus.Completed, ct, maxTicks: 60);
        await WaitUntilAsync(() => parentBridge.CountForThread(threadId) == 1, ct, maxTicks: 60);

        await tabs.ReleaseAllAsync();
        Assert.False(server.TryAuthenticateBearer($"Bearer {token2}", out _));
    }

    // ==================== Finding 6: Terminal Teardown / Tab Release ====================

    private sealed class DelayedDisposeTerminalConnection(TaskCompletionSource disposeStarted, TaskCompletionSource disposeGate) : ITerminalConnection
    {
        private readonly TaskCompletionSource<TerminalExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProcessId => 4243;
        public Task<TerminalExit> Exited => _exit.Task;
        public System.Collections.Concurrent.ConcurrentQueue<string> Written { get; } = new();

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.WhenAny(Task.Delay(Timeout.Infinite, cancellationToken), _exit.Task).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
        {
            Written.Enqueue(System.Text.Encoding.UTF8.GetString(input.Span));
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight) => ValueTask.CompletedTask;
        public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;
        public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);

        public async ValueTask DisposeAsync()
        {
            disposeStarted.TrySetResult();
            await disposeGate.Task.ConfigureAwait(false);
            _exit.TrySetResult(new TerminalExit(0, null));
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Releasing_existing_terminal_tab_settles_open_work_preserves_completed_work_and_allows_replacement()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-tab-release-pty-");
        var projects = AppHost.Get<IProjectService>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var ptyFactory = AppHost.Get<FakeTerminalConnectionFactory>();
        var signals = AppHost.Get<ISignalHub>();

        var openItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Open PTY", Surface = AgentSurface.Terminal };
        var doneItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Completed PTY", Surface = AgentSurface.Terminal };
        projects.AddItem(project, openItem, activate: false);
        projects.AddItem(project, doneItem, activate: false);

        var openRes = await DelegateAsync(coordinator, parent.Id, openItem.Id, "Work interrupted by release", ct);
        var openTaskId = openRes["task_id"]!.GetValue<string>();
        var openThreadId = openRes["thread_id"]!.GetValue<string>();
        Assert.True(registry.Find(openItem.Id)!.IsRunning);

        var doneRes = await DelegateAsync(coordinator, parent.Id, doneItem.Id, "Work completed before release", ct);
        var doneTaskId = doneRes["task_id"]!.GetValue<string>();
        var doneThreadId = doneRes["thread_id"]!.GetValue<string>();
        Assert.Null((await coordinator.CallToolAsync(doneItem.Id, "reply_thread", new JsonObject
        {
            ["thread_id"] = doneThreadId,
            ["message"] = "Finished cleanly before release",
            ["status"] = "agreed",
        }, ct))["isError"]);
        await WaitUntilAsync(() => parentBridge.CountForThread(doneThreadId) == 1, ct, maxTicks: 60);

        await tabs.ReleaseAsync(openItem.Id);
        await tabs.ReleaseAsync(doneItem.Id);
        Frames.Pump();

        Assert.Null(registry.Find(openItem.Id));
        Assert.Null(registry.Find(doneItem.Id));

        var settledOpen = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == openTaskId);
        Assert.True(settledOpen.IsFinished, "PTY closed while the ledger task stayed open");
        Assert.Equal(CollabTaskStatus.Cancelled, settledOpen.Status);
        Assert.Equal(CollabThreadStatus.Cancelled, coordinator.FindThread(openThreadId)!.Status);

        // Control: work that already completed before tab release stays Completed without duplicate notification.
        var preservedDone = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == doneTaskId);
        Assert.Equal(CollabTaskStatus.Completed, preservedDone.Status);
        Assert.Equal(CollabThreadStatus.Agreed, coordinator.FindThread(doneThreadId)!.Status);
        Assert.Equal(1, parentBridge.CountForThread(doneThreadId));

        // Control: delegating to the released item later starts a fresh replacement PTY and completes normally.
        var replacementRes = await DelegateAsync(coordinator, parent.Id, openItem.Id, "Fresh work after release", ct);
        var replacementTaskId = replacementRes["task_id"]!.GetValue<string>();
        var replacementThreadId = replacementRes["thread_id"]!.GetValue<string>();
        var replacementSession = registry.Find(openItem.Id);
        Assert.NotNull(replacementSession);
        Assert.True(replacementSession!.IsRunning);

        var replacementPty = ptyFactory.Connections.Last();
        await WaitUntilAsync(() => replacementPty.Written.Any(w => w.Contains("Fresh work after release", StringComparison.Ordinal)), ct, maxTicks: 60);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        signals.Publish(new AgentSignal("claude", "stop", openItem.Id, "", now, now, "{}", replacementSession.CurrentLinkId));

        await WaitUntilAsync(() =>
            coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == replacementTaskId).Status == CollabTaskStatus.Completed, ct, maxTicks: 60);
        await WaitUntilAsync(() => parentBridge.CountForThread(replacementThreadId) == 1, ct, maxTicks: 60);

        await tabs.ReleaseAllAsync();
    }

    [AvaloniaFact, FreshApp]
    public async Task Releasing_headless_terminal_session_settles_open_work_and_unblocks_await_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, parent, _, coordinator) = await SetUpParentAsync("octopus-0b-headless-release-pty-");
        var projects = AppHost.Get<IProjectService>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var ptyFactory = AppHost.Get<FakeTerminalConnectionFactory>();
        var launcher = AppHost.Get<IItemLaunchService>();

        var cli = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Headless Released CLI", Surface = AgentSurface.Terminal };
        projects.AddItem(project, cli, activate: false);
        registry.GetOrStart(
            cli.Id, launcher.Plan(project, cli), ptyFactory, new TerminalSize(80, 24, 0, 0),
            TerminalPalettes.Dark, ColorScheme.Dark, 1000, out _);

        var delegated = await DelegateAsync(coordinator, parent.Id, cli.Id, "Headless work before release", ct);
        var taskId = delegated["task_id"]!.GetValue<string>();

        var awaitCall = coordinator.CallToolAsync(
            parent.Id,
            "await_events",
            new JsonObject { ["ids"] = new JsonArray(taskId), ["timeout_seconds"] = 5 },
            ct);
        await WaitUntilAsync(() => coordinator.PendingAwaitCount == 1, ct, maxTicks: 60);

        await tabs.ReleaseAsync(cli.Id);
        Assert.Null(registry.Find(cli.Id));

        var awaitBody = Body(await awaitCall.WaitAsync(TimeSpan.FromSeconds(5), ct));
        var reported = Assert.Single(awaitBody["tasks"]!.AsArray())!;
        Assert.Equal(taskId, reported["task_id"]!.GetValue<string>());
        Assert.Equal("cancelled", reported["status"]!.GetValue<string>());
        Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status);
    }

    [AvaloniaFact, FreshApp]
    public async Task Stale_terminal_release_settles_retired_work_without_cancelling_replacement_work_or_credential()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpFwd = Directory.CreateTempSubdirectory("octopus-0b-stale-term-fwd-").FullName;
        var prevForwarder = Environment.GetEnvironmentVariable(ItemLaunchService.ForwarderVariable);
        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var fakeForwarder = Path.Combine(tmpFwd, "octopus-signal");
            File.WriteAllText(fakeForwarder, "");
            Environment.SetEnvironmentVariable(ItemLaunchService.ForwarderVariable, fakeForwarder);

            var (project, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-stale-term-rel-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();
            var registry = AppHost.Get<TerminalSessionRegistry>();
            var ptyFactory = AppHost.Get<FakeTerminalConnectionFactory>();
            var signals = AppHost.Get<ISignalHub>();
            var server = coordinator.Server;

            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Slow Closing PTY", Surface = AgentSurface.Terminal };
            projects.AddItem(project, item, activate: false);

            // Generation 1 uses a connection whose DisposeAsync pauses while generation 2 is created.
            ptyFactory.StartOverride = (_, _, _) =>
            {
                ptyFactory.StartOverride = null;
                return new DelayedDisposeTerminalConnection(disposeStarted, disposeGate);
            };
            var first = await DelegateAsync(coordinator, parent.Id, item.Id, "Work on gen1 PTY", ct);
            var task1 = first["task_id"]!.GetValue<string>();
            var spec1 = Assert.Single(ptyFactory.Started);
            var token1 = spec1.Environment[CollabMcpServer.TokenEnvVariable];
            var cfg1 = ExtractMcpConfigPath(spec1);
            Assert.True(server.TryAuthenticateBearer($"Bearer {token1}", out _));
            Assert.True(File.Exists(cfg1));

            // Start releasing generation 1; wait until DisposeAsync has begun (registry entry already removed).
            var release1 = tabs.ReleaseAsync(item.Id);
            await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task1).Status);
            Assert.False(server.TryAuthenticateBearer($"Bearer {token1}", out _));
            Assert.False(File.Exists(cfg1));

            // While generation 1 release is still awaiting disposal, delegate fresh replacement work on generation 2.
            var second = await DelegateAsync(coordinator, parent.Id, item.Id, "Work on gen2 PTY", ct);
            var task2 = second["task_id"]!.GetValue<string>();
            var thread2 = second["thread_id"]!.GetValue<string>();
            Assert.Equal(2, ptyFactory.Started.Count);
            var spec2 = ptyFactory.Started[1];
            var token2 = spec2.Environment[CollabMcpServer.TokenEnvVariable];
            var cfg2 = ExtractMcpConfigPath(spec2);
            var session2 = registry.Find(item.Id);
            Assert.NotNull(session2);
            Assert.True(server.TryAuthenticateBearer($"Bearer {token2}", out _));
            Assert.True(File.Exists(cfg2));
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task2).Status);

            // Finish stale generation 1 release: must NOT cancel generation 2's task or revoke generation 2's credential.
            disposeGate.TrySetResult();
            await release1.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Frames.Pump();

            Assert.Same(session2, registry.Find(item.Id));
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task2).Status);
            Assert.True(server.TryAuthenticateBearer($"Bearer {token2}", out _), "Stale terminal release revoked replacement credential.");
            Assert.True(File.Exists(cfg2), "Stale terminal release deleted replacement config.");

            // Control: generation 2 completes cleanly on its valid Done signal.
            await WaitUntilAsync(() => ptyFactory.Connections.Last().Written.Any(w => w.Contains("Work on gen2 PTY", StringComparison.Ordinal)), ct, maxTicks: 60);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            signals.Publish(new AgentSignal("claude", "stop", item.Id, "", now, now, "{}", session2!.CurrentLinkId));
            await WaitUntilAsync(() => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task2).Status == CollabTaskStatus.Completed, ct, maxTicks: 60);
            await WaitUntilAsync(() => parentBridge.CountForThread(thread2) == 1, ct, maxTicks: 60);

            await tabs.ReleaseAllAsync();
            Assert.False(server.TryAuthenticateBearer($"Bearer {token2}", out _));
            Assert.False(File.Exists(cfg2));
        }
        finally
        {
            disposeGate.TrySetResult();
            Environment.SetEnvironmentVariable(ItemLaunchService.ForwarderVariable, prevForwarder);
            try { Directory.Delete(tmpFwd, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Headless_terminal_release_when_replacement_attaches_before_removal_observed_settles_only_retired_work_and_preserves_lease()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpFwd = Directory.CreateTempSubdirectory("octopus-0b-headless-gen1-fwd-").FullName;
        var prevForwarder = Environment.GetEnvironmentVariable(ItemLaunchService.ForwarderVariable);
        var registry = AppHost.Get<TerminalSessionRegistry>();
        using var resumeClose = new ManualResetEventSlim();
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? watched = null;

        void PauseRemoval(string id)
        {
            if (id != watched || registry.Find(id) is not null) return;
            removed.TrySetResult();
            if (!resumeClose.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        }

        // Subscribe before resolving CollabCoordinator so this listener holds the removal notification before
        // TerminalSessionProvider observes it, selecting the replacement-before-observation ordering via public APIs.
        registry.Changed += PauseRemoval;
        try
        {
            var fakeForwarder = Path.Combine(tmpFwd, "octopus-signal");
            File.WriteAllText(fakeForwarder, "");
            Environment.SetEnvironmentVariable(ItemLaunchService.ForwarderVariable, fakeForwarder);

            var (project, parent, _, coordinator) = await SetUpParentAsync("octopus-0b-headless-gen1-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();
            var ptyFactory = AppHost.Get<FakeTerminalConnectionFactory>();
            var launcher = AppHost.Get<IItemLaunchService>();
            var server = coordinator.Server;

            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Headless Replaced PTY", Surface = AgentSurface.Terminal };
            projects.AddItem(project, item, activate: false);

            TerminalSession AttachWithLease() =>
                registry.GetOrStart(
                    item.Id,
                    leaseId => launcher.PlanLaunch(project, item, leaseId: leaseId),
                    ptyFactory,
                    new TerminalSize(80, 24, 0, 0),
                    TerminalPalettes.Dark,
                    ColorScheme.Dark,
                    1000,
                    out _);

            var old = AttachWithLease();
            var spec1 = Assert.Single(ptyFactory.Started);
            var token1 = spec1.Environment[CollabMcpServer.TokenEnvVariable];
            var cfg1 = ExtractMcpConfigPath(spec1);
            Assert.True(server.TryAuthenticateBearer($"Bearer {token1}", out _));
            Assert.True(File.Exists(cfg1));

            var firstRes = await DelegateAsync(coordinator, parent.Id, item.Id, "old generation work", ct);
            var firstTaskId = firstRes["task_id"]!.GetValue<string>();
            var firstThreadId = firstRes["thread_id"]!.GetValue<string>();
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == firstTaskId).Status);

            watched = item.Id;
            var closing = Task.Run(() => tabs.ReleaseAsync(item.Id));
            await removed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Null(registry.Find(item.Id));

            var replacement = AttachWithLease();
            Assert.NotSame(old, replacement);
            Assert.Equal(2, ptyFactory.Started.Count);
            var spec2 = ptyFactory.Started[1];
            var token2 = spec2.Environment[CollabMcpServer.TokenEnvVariable];
            var cfg2 = ExtractMcpConfigPath(spec2);
            Assert.True(server.TryAuthenticateBearer($"Bearer {token2}", out _));
            Assert.True(File.Exists(cfg2));

            var secondRes = await DelegateAsync(coordinator, parent.Id, item.Id, "replacement generation work", ct);
            var secondTaskId = secondRes["task_id"]!.GetValue<string>();
            var secondThreadId = secondRes["thread_id"]!.GetValue<string>();

            resumeClose.Set();
            await closing.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Frames.Pump();

            // Ordering 1: right after release returns, retired work is Cancelled while replacement session, work, and SEC-04 lease remain live.
            Assert.Same(replacement, registry.Find(item.Id));
            var retiredTask = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == firstTaskId);
            Assert.Equal(CollabTaskStatus.Cancelled, retiredTask.Status);
            Assert.Equal(CollabThreadStatus.Cancelled, coordinator.FindThread(firstThreadId)!.Status);

            var replacementTask = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == secondTaskId);
            Assert.Equal(CollabTaskStatus.Running, replacementTask.Status);
            Assert.Equal(CollabThreadStatus.Active, coordinator.FindThread(secondThreadId)!.Status);

            Assert.False(server.TryAuthenticateBearer($"Bearer {token1}", out _), "Retired headless PTY credential was not revoked.");
            Assert.False(File.Exists(cfg1), "Retired headless PTY config was not deleted.");
            Assert.True(server.TryAuthenticateBearer($"Bearer {token2}", out _), "Headless release revoked replacement credential.");
            Assert.True(File.Exists(cfg2), "Headless release deleted replacement config.");

            await tabs.ReleaseAllAsync();
            Assert.False(server.TryAuthenticateBearer($"Bearer {token2}", out _));
            Assert.False(File.Exists(cfg2));
        }
        finally
        {
            resumeClose.Set();
            registry.Changed -= PauseRemoval;
            Environment.SetEnvironmentVariable(ItemLaunchService.ForwarderVariable, prevForwarder);
            try { Directory.Delete(tmpFwd, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Headless_terminal_release_when_replacement_completes_work_before_check_settles_retired_work_and_preserves_manual_turns()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        using var resumeClose = new ManualResetEventSlim();
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? watched = null;

        void PauseRemoval(string id)
        {
            if (id != watched || registry.Find(id) is not null) return;
            removed.TrySetResult();
            if (!resumeClose.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        }

        registry.Changed += PauseRemoval;
        try
        {
            var (project, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-headless-gen2-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();
            var ptyFactory = AppHost.Get<FakeTerminalConnectionFactory>();
            var launcher = AppHost.Get<IItemLaunchService>();
            var signals = AppHost.Get<ISignalHub>();

            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Headless Gen2 PTY", Surface = AgentSurface.Terminal };
            projects.AddItem(project, item, activate: false);

            TerminalSession Attach() =>
                registry.GetOrStart(
                    item.Id,
                    launcher.Plan(project, item),
                    ptyFactory,
                    new TerminalSize(80, 24, 0, 0),
                    TerminalPalettes.Dark,
                    ColorScheme.Dark,
                    1000,
                    out _);

            var old = Attach();
            var firstRes = await DelegateAsync(coordinator, parent.Id, item.Id, "old work", ct);
            var firstTaskId = firstRes["task_id"]!.GetValue<string>();
            var firstThreadId = firstRes["thread_id"]!.GetValue<string>();

            watched = item.Id;
            var closing = Task.Run(() => tabs.ReleaseAsync(item.Id));
            await removed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Null(registry.Find(item.Id));

            var replacement = Attach();
            Assert.NotSame(old, replacement);
            var secondRes = await DelegateAsync(coordinator, parent.Id, item.Id, "replacement work", ct);
            var secondTaskId = secondRes["task_id"]!.GetValue<string>();
            var secondThreadId = secondRes["thread_id"]!.GetValue<string>();

            resumeClose.Set();
            await closing.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Frames.Pump();

            Assert.Same(replacement, registry.Find(item.Id));
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == secondTaskId).Status);

            var replacementPty = ptyFactory.Connections.Last();
            await WaitUntilAsync(() => replacementPty.Written.Any(w => w.Contains("replacement work", StringComparison.Ordinal)), ct, maxTicks: 60);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            signals.Publish(new AgentSignal("claude", "stop", item.Id, "", now, now, "{}", replacement.CurrentLinkId));

            await WaitUntilAsync(() =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == secondTaskId).Status == CollabTaskStatus.Completed, ct, maxTicks: 60);
            await WaitUntilAsync(() => parentBridge.CountForThread(secondThreadId) == 1, ct, maxTicks: 60);

            // Ordering 2: after replacement work completes on the new link's stop signal, retired work is Cancelled (never left Running).
            Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == firstTaskId).Status);
            Assert.Equal(CollabThreadStatus.Cancelled, coordinator.FindThread(firstThreadId)!.Status);

            // Control: later manual work before a delegated turn on the replacement session preserves #41/#42 turn ordering.
            replacement.Send("manual check\r");
            var thirdRes = await DelegateAsync(coordinator, parent.Id, item.Id, "post-manual replacement work", ct);
            var thirdTaskId = thirdRes["task_id"]!.GetValue<string>();
            var thirdThreadId = thirdRes["thread_id"]!.GetValue<string>();
            await WaitUntilAsync(() => replacementPty.Written.Any(w => w.Contains("post-manual replacement work", StringComparison.Ordinal)), ct, maxTicks: 60);

            var t1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            signals.Publish(new AgentSignal("claude", "stop", item.Id, "", t1, t1, "{}", replacement.CurrentLinkId));
            await Task.Delay(100, ct);
            Frames.Pump();
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == thirdTaskId).Status);

            var t2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            signals.Publish(new AgentSignal("claude", "stop", item.Id, "", t2, t2, "{}", replacement.CurrentLinkId));
            await WaitUntilAsync(() =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == thirdTaskId).Status == CollabTaskStatus.Completed, ct, maxTicks: 60);
            await WaitUntilAsync(() => parentBridge.CountForThread(thirdThreadId) == 1, ct, maxTicks: 60);

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            resumeClose.Set();
            registry.Changed -= PauseRemoval;
        }
    }

    private sealed class HeldWriteTerminalConnection : ITerminalConnection
    {
        public FakeTerminalConnection Inner { get; } = new();
        public TaskCompletionSource FirstWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProcessId => Inner.ProcessId;
        public Task<TerminalExit> Exited => Inner.Exited;
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken cancellationToken = default) => Inner.ReadOutputAsync(cancellationToken);

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
        {
            FirstWriteStarted.TrySetResult();
            await ReleaseFirstWrite.Task.WaitAsync(cancellationToken);
            await Inner.WriteAsync(input, cancellationToken);
        }

        public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight) => Inner.ResizeAsync(columns, rows, pixelWidth, pixelHeight);
        public ValueTask SignalAsync(TerminalSignal signal) => Inner.SignalAsync(signal);
        public ValueTask<string?> GetForegroundProcessNameAsync() => Inner.GetForegroundProcessNameAsync();
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }

    [AvaloniaFact, FreshApp]
    public async Task Headless_terminal_retirement_does_not_settle_cancelled_never_written_delivery()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        using var resumeClose = new ManualResetEventSlim();
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? watched = null;

        void PauseRemoval(string id)
        {
            if (id != watched || registry.Find(id) is not null) return;
            removed.TrySetResult();
            if (!resumeClose.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        }

        registry.Changed += PauseRemoval;
        var held = new HeldWriteTerminalConnection();
        try
        {
            var (project, parent, _, coordinator) = await SetUpParentAsync("octopus-0b-never-written-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();
            var launcher = AppHost.Get<IItemLaunchService>();

            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Held Write PTY", Surface = AgentSurface.Terminal };
            projects.AddItem(project, item, activate: false);

            var heldFactory = new FakeTerminalConnectionFactory { StartOverride = (_, _, _) => held };
            var old = registry.GetOrStart(
                item.Id,
                launcher.Plan(project, item),
                heldFactory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            // Hold an earlier control write so the queued delegation write has not started yet.
            Assert.True(old.TrySendCollabControl("\x1b", old.Generation));
            await held.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            using var cancelDelegation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delegating = coordinator.CallToolAsync(
                parent.Id,
                "delegate_task",
                new JsonObject { ["target"] = item.Id, ["goal"] = "must never reach pty" },
                cancelDelegation.Token);
            Assert.False(delegating.IsCompleted);

            var candidate = Assert.Single(coordinator.GetTasksForItem(parent.Id));
            Assert.Equal(CollabTaskStatus.Running, candidate.Status);

            watched = item.Id;
            var closing = Task.Run(() => tabs.ReleaseAsync(item.Id));
            await removed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            var replacement = registry.GetOrStart(
                item.Id,
                launcher.Plan(project, item),
                new FakeTerminalConnectionFactory(),
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
            Assert.NotSame(old, replacement);

            cancelDelegation.Cancel();
            try { await delegating.WaitAsync(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { }

            resumeClose.Set();
            held.ReleaseFirstWrite.TrySetResult();
            await closing.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Frames.Pump();

            Assert.DoesNotContain(held.Inner.Written, w => w.Contains("must never reach pty", StringComparison.Ordinal));
            var residue = coordinator.GetTasksForItem(parent.Id).SingleOrDefault(t => t.TaskId == candidate.TaskId);
            Assert.Null(residue);
            Assert.Null(coordinator.FindThread(candidate.ThreadId!));

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            held.ReleaseFirstWrite.TrySetResult();
            resumeClose.Set();
            registry.Changed -= PauseRemoval;
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Old_terminal_close_cannot_cancel_replacement_outbound_delegation_while_still_cancelling_prior_outbound_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, parent, _, coordinator) = await SetUpParentAsync("octopus-0b-old-close-outbound-");
        var projects = AppHost.Get<IProjectService>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        var launcher = AppHost.Get<IItemLaunchService>();

        var sibling = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Sibling Worker", Surface = AgentSurface.Gui };
        var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Headless Caller", Surface = AgentSurface.Terminal };
        projects.AddItem(project, sibling, activate: false);
        projects.AddItem(project, item, activate: false);
        var siblingBridge = new SpyGuiBridge(sibling.Id, "codex", sibling.Title, project.Path);
        coordinator.RegisterGuiSession(siblingBridge);

        TerminalSession Attach() =>
            registry.GetOrStart(
                item.Id,
                launcher.Plan(project, item),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

        var old = Attach();
        var inboundRes = await DelegateAsync(coordinator, parent.Id, item.Id, "old inbound work", ct);
        var inboundTaskId = inboundRes["task_id"]!.GetValue<string>();

        // Prior outbound task delegated by the old session before close must still be cancelled when old closes.
        var priorOutboundRes = await DelegateAsync(coordinator, item.Id, sibling.Id, "old outbound asks sibling", ct);
        var priorOutboundTaskId = priorOutboundRes["task_id"]!.GetValue<string>();

        using var resumeClose = new ManualResetEventSlim();
        var pausedAfterInboundSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;

        void HoldCloseAfterOldTaskSettles(string? changed)
        {
            if (changed != item.Id
                || registry.Find(item.Id) is not null
                || coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == inboundTaskId).Status != CollabTaskStatus.Cancelled
                || Interlocked.Exchange(ref entered, 1) != 0)
            {
                return;
            }
            pausedAfterInboundSettled.TrySetResult();
            if (!resumeClose.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        }

        coordinator.Changed += HoldCloseAfterOldTaskSettles;
        try
        {
            var closing = Task.Run(() => tabs.ReleaseAsync(item.Id));
            await pausedAfterInboundSettled.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            var fresh = Attach();
            Assert.NotSame(old, fresh);
            var replacementOutboundRes = await DelegateAsync(coordinator, item.Id, sibling.Id, "replacement asks sibling", ct);
            var replacementOutboundTaskId = replacementOutboundRes["task_id"]!.GetValue<string>();
            Assert.Contains(siblingBridge.Delivered, d => d.Prompt.Contains("replacement asks sibling", StringComparison.Ordinal));
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == replacementOutboundTaskId).Status);

            resumeClose.Set();
            await closing.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Frames.Pump();

            Assert.Same(fresh, registry.Find(item.Id));
            Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == inboundTaskId).Status);
            Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == priorOutboundTaskId).Status);
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == replacementOutboundTaskId).Status);

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            resumeClose.Set();
            coordinator.Changed -= HoldCloseAfterOldTaskSettles;
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Stale_same_session_exit_fails_only_exited_link_work_without_failing_restarted_link_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Restarted Headless PTY", Surface = AgentSurface.Terminal };

        // Start session and subscribe to Exited before resolving CollabCoordinator so this handler runs first.
        var term = registry.GetOrStart(
            item.Id,
            new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
            factory,
            new TerminalSize(80, 24, 0, 0),
            TerminalPalettes.Dark,
            ColorScheme.Dark,
            1000,
            out _);

        using var resumeExit = new ManualResetEventSlim();
        var exitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausedOnce = 0;

        void PauseFirstExit(TerminalExit _)
        {
            if (Interlocked.Exchange(ref pausedOnce, 1) != 0) return;
            exitEntered.TrySetResult();
            if (!resumeExit.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        }

        term.Exited += PauseFirstExit;
        try
        {
            var (project, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-stale-link-exit-");
            AppHost.Get<IProjectService>().AddItem(project, item, activate: false);
            var signals = AppHost.Get<ISignalHub>();

            var oldRes = await DelegateAsync(coordinator, parent.Id, item.Id, "old link work", ct);
            var oldTaskId = oldRes["task_id"]!.GetValue<string>();
            var oldThreadId = oldRes["thread_id"]!.GetValue<string>();
            var oldLinkId = term.CurrentLinkId;

            var restartedAndDelegated = Task.Run(async () =>
            {
                try
                {
                    await exitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                    await term.RestartAsync();
                    Assert.NotEqual(oldLinkId, term.CurrentLinkId);
                    return await DelegateAsync(coordinator, parent.Id, item.Id, "new link work", ct);
                }
                finally
                {
                    resumeExit.Set();
                }
            });

            factory.Connections[0].Exit(7);
            var freshRes = await restartedAndDelegated.WaitAsync(TimeSpan.FromSeconds(9), ct);
            var freshTaskId = freshRes["task_id"]!.GetValue<string>();
            var freshThreadId = freshRes["thread_id"]!.GetValue<string>();

            await WaitUntilAsync(() =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == oldTaskId).Status == CollabTaskStatus.Failed, ct, maxTicks: 80);
            await WaitUntilAsync(() => parentBridge.CountForThread(oldThreadId) == 1, ct, maxTicks: 60);

            Assert.Contains(factory.Connections[1].Written, w => w.Contains("new link work", StringComparison.Ordinal));
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == freshTaskId).Status);

            // Control 1: valid Done signal for the restarted link completes freshTask cleanly.
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            signals.Publish(new AgentSignal("claude", "stop", item.Id, "", now, now, "{}", term.CurrentLinkId));
            await WaitUntilAsync(() =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == freshTaskId).Status == CollabTaskStatus.Completed, ct, maxTicks: 60);
            await WaitUntilAsync(() => parentBridge.CountForThread(freshThreadId) == 1, ct, maxTicks: 60);

            // Control 2: genuine exit of the active restarted link still fails its outstanding task.
            var liveRes = await DelegateAsync(coordinator, parent.Id, item.Id, "gen2 crashing work", ct);
            var liveTaskId = liveRes["task_id"]!.GetValue<string>();
            factory.Connections[1].Exit(13);
            await WaitUntilAsync(() =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == liveTaskId).Status == CollabTaskStatus.Failed, ct, maxTicks: 80);

            await AppHost.Get<ITabContentRegistry>().ReleaseAllAsync();
        }
        finally
        {
            resumeExit.Set();
            term.Exited -= PauseFirstExit;
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(513, 0)]
    [InlineData(65, 1)]
    [InlineData(65, 65)]
    public async Task Scoped_terminal_release_settles_over_512_unanswered_and_evicted_delegated_turns(int count, int doneCount)
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        string? replaceId = null;

        void Reopen(string id)
        {
            if (id == replaceId && registry.Find(id) is null)
            {
                registry.GetOrStart(
                    id,
                    new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                    factory,
                    new TerminalSize(80, 24, 0, 0),
                    TerminalPalettes.Dark,
                    ColorScheme.Dark,
                    1000,
                    out _);
            }
        }

        registry.Changed += Reopen;
        try
        {
            var (project, parent, _, coordinator) = await SetUpParentAsync("octopus-0b-overflow-release-");
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Overflow PTY", Surface = AgentSurface.Terminal };
            AppHost.Get<IProjectService>().AddItem(project, item, activate: false);
            var signals = AppHost.Get<ISignalHub>();

            var old = registry.GetOrStart(
                item.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            var tasks = new List<(string TaskId, string ThreadId)>(count);
            for (var i = 0; i < count; i++)
            {
                var res = await DelegateAsync(coordinator, parent.Id, item.Id, $"overflow-prompt[{i}]", ct);
                tasks.Add((res["task_id"]!.GetValue<string>(), res["thread_id"]!.GetValue<string>()));
            }
            Assert.Equal(count, factory.Connections[0].Written.Count(w => w.Contains("overflow-prompt[", StringComparison.Ordinal)));

            for (var i = 0; i < doneCount; i++)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                signals.Publish(new AgentSignal("claude", "stop", item.Id, "", now, now, "{}", old.CurrentLinkId));
            }

            replaceId = item.Id;
            await AppHost.Get<ITabContentRegistry>().ReleaseAsync(item.Id);

            var fresh = registry.Find(item.Id);
            Assert.NotNull(fresh);
            Assert.NotSame(old, fresh);
            var laterRes = await DelegateAsync(coordinator, parent.Id, item.Id, "replacement sentinel", ct);
            var laterTaskId = laterRes["task_id"]!.GetValue<string>();
            var tNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            signals.Publish(new AgentSignal("claude", "stop", item.Id, "", tNow, tNow, "{}", fresh!.CurrentLinkId));
            await WaitUntilAsync(() =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == laterTaskId).Status == CollabTaskStatus.Completed, ct, maxTicks: 80);

            var oldIds = tasks.Select(t => t.TaskId).ToHashSet(StringComparer.Ordinal);
            var openOld = coordinator.GetTasksForItem(parent.Id).Where(t => oldIds.Contains(t.TaskId) && !t.IsFinished).ToList();
            Assert.Empty(openOld);
            Assert.NotEqual(CollabThreadStatus.Active, coordinator.FindThread(tasks[0].ThreadId)!.Status);

            replaceId = null;
            await AppHost.Get<ITabContentRegistry>().ReleaseAllAsync();
        }
        finally
        {
            replaceId = null;
            registry.Changed -= Reopen;
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Single_delegation_evicted_by_64_manual_turns_settles_on_scoped_release()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        string? replaceId = null;

        void Reopen(string id)
        {
            if (id == replaceId && registry.Find(id) is null)
            {
                registry.GetOrStart(
                    id,
                    new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                    factory,
                    new TerminalSize(80, 24, 0, 0),
                    TerminalPalettes.Dark,
                    ColorScheme.Dark,
                    1000,
                    out _);
            }
        }

        registry.Changed += Reopen;
        try
        {
            var (project, parent, _, coordinator) = await SetUpParentAsync("octopus-0b-manual-evict-");
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Manual Evict PTY", Surface = AgentSurface.Terminal };
            AppHost.Get<IProjectService>().AddItem(project, item, activate: false);
            var signals = AppHost.Get<ISignalHub>();

            var old = registry.GetOrStart(
                item.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            var res = await DelegateAsync(coordinator, parent.Id, item.Id, "single delegated turn", ct);
            var taskId = res["task_id"]!.GetValue<string>();
            var threadId = res["thread_id"]!.GetValue<string>();

            for (var i = 0; i < 64; i++)
            {
                Assert.True(old.TrySend($"manual line {i}\r"));
            }
            await WaitUntilAsync(() => factory.Connections[0].Written.Count(w => w.StartsWith("manual line ", StringComparison.Ordinal)) == 64, ct, maxTicks: 80);

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            signals.Publish(new AgentSignal("claude", "stop", item.Id, "", now, now, "{}", old.CurrentLinkId));

            replaceId = item.Id;
            await AppHost.Get<ITabContentRegistry>().ReleaseAsync(item.Id);

            var settled = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId);
            Assert.Equal(CollabTaskStatus.Cancelled, settled.Status);
            Assert.Equal(CollabThreadStatus.Cancelled, coordinator.FindThread(threadId)!.Status);

            replaceId = null;
            await AppHost.Get<ITabContentRegistry>().ReleaseAllAsync();
        }
        finally
        {
            replaceId = null;
            registry.Changed -= Reopen;
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Delayed_same_session_exit_fails_all_513_accepted_tasks_after_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Overflow Exit PTY", Surface = AgentSurface.Terminal };

        var term = registry.GetOrStart(
            item.Id,
            new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
            factory,
            new TerminalSize(80, 24, 0, 0),
            TerminalPalettes.Dark,
            ColorScheme.Dark,
            1000,
            out _);

        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausedOnce = 0;
        void Pause(TerminalExit _)
        {
            if (Interlocked.Exchange(ref pausedOnce, 1) != 0) return;
            entered.TrySetResult();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        }

        term.Exited += Pause;
        try
        {
            var (project, parent, _, coordinator) = await SetUpParentAsync("octopus-0b-513-exit-");
            AppHost.Get<IProjectService>().AddItem(project, item, activate: false);
            var signals = AppHost.Get<ISignalHub>();

            var tasks = new List<(string TaskId, string ThreadId)>(513);
            for (var i = 0; i < 513; i++)
            {
                var res = await DelegateAsync(coordinator, parent.Id, item.Id, $"old-exit[{i}]", ct);
                tasks.Add((res["task_id"]!.GetValue<string>(), res["thread_id"]!.GetValue<string>()));
            }
            Assert.Equal(513, factory.Connections[0].Written.Count(w => w.Contains("old-exit[", StringComparison.Ordinal)));

            var restarted = Task.Run(async () =>
            {
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                    await term.RestartAsync();
                    return await DelegateAsync(coordinator, parent.Id, item.Id, "restart sentinel", ct);
                }
                finally
                {
                    resume.Set();
                }
            });

            factory.Connections[0].Exit(7);
            var freshRes = await restarted.WaitAsync(TimeSpan.FromSeconds(9), ct);
            var freshTaskId = freshRes["task_id"]!.GetValue<string>();

            await WaitUntilAsync(() =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == tasks[^1].TaskId).IsFinished, ct, maxTicks: 80);

            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == freshTaskId).Status);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            signals.Publish(new AgentSignal("claude", "stop", item.Id, "", now, now, "{}", term.CurrentLinkId));
            await WaitUntilAsync(() =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == freshTaskId).Status == CollabTaskStatus.Completed, ct, maxTicks: 80);

            var all = coordinator.GetTasksForItem(parent.Id);
            Assert.Equal(513, all.Count(t => t.Status == CollabTaskStatus.Failed));
            Assert.Equal(CollabTaskStatus.Failed, all.Single(t => t.TaskId == tasks[0].TaskId).Status);

            await AppHost.Get<ITabContentRegistry>().ReleaseAllAsync();
        }
        finally
        {
            resume.Set();
            term.Exited -= Pause;
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(1)]
    [InlineData(513)]
    public async Task Direct_terminal_restart_settles_accepted_work_and_keeps_new_link_live(int count)
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, parent, _, coordinator) = await SetUpParentAsync("octopus-0b-direct-restart-");
        var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Direct Restart PTY", Surface = AgentSurface.Terminal };
        AppHost.Get<IProjectService>().AddItem(project, item, activate: false);
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        var signals = AppHost.Get<ISignalHub>();

        var term = registry.GetOrStart(
            item.Id,
            new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
            factory,
            new TerminalSize(80, 24, 0, 0),
            TerminalPalettes.Dark,
            ColorScheme.Dark,
            1000,
            out _);

        var oldIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var res = await DelegateAsync(coordinator, parent.Id, item.Id, $"pre-restart[{i}]", ct);
            oldIds.Add(res["task_id"]!.GetValue<string>());
        }

        var oldLink = term.CurrentLinkId;
        await term.RestartAsync();
        Assert.NotEqual(oldLink, term.CurrentLinkId);

        var freshRes = await DelegateAsync(coordinator, parent.Id, item.Id, "post-restart sentinel", ct);
        var freshTaskId = freshRes["task_id"]!.GetValue<string>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        signals.Publish(new AgentSignal("claude", "stop", item.Id, "", now, now, "{}", term.CurrentLinkId));
        await WaitUntilAsync(() =>
            coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == freshTaskId).Status == CollabTaskStatus.Completed, ct, maxTicks: 80);

        var remainingOpen = coordinator.GetTasksForItem(parent.Id).Where(t => oldIds.Contains(t.TaskId) && !t.IsFinished).ToList();
        Assert.Empty(remainingOpen);

        await AppHost.Get<ITabContentRegistry>().ReleaseAllAsync();
    }

    [AvaloniaFact, FreshApp]
    public async Task Captured_early_replacement_cancels_retired_terminal_outbound_work_without_affecting_replacement_or_unrelated_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        string? replaceId = null;

        void Reopen(string id)
        {
            if (id == replaceId && registry.Find(id) is null)
            {
                registry.GetOrStart(
                    id,
                    new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                    factory,
                    new TerminalSize(80, 24, 0, 0),
                    TerminalPalettes.Dark,
                    ColorScheme.Dark,
                    1000,
                    out _);
            }
        }

        registry.Changed += Reopen;
        try
        {
            var (project, parent, _, coordinator) = await SetUpParentAsync("octopus-0b-captured-outbound-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();

            var sibling = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Sibling", Surface = AgentSurface.Gui };
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Captured Terminal", Surface = AgentSurface.Terminal };
            projects.AddItem(project, sibling, activate: false);
            projects.AddItem(project, item, activate: false);
            var siblingBridge = new SpyGuiBridge(sibling.Id, "codex", sibling.Title, project.Path);
            coordinator.RegisterGuiSession(siblingBridge);

            var old = registry.GetOrStart(
                item.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            var incomingRes = await DelegateAsync(coordinator, parent.Id, item.Id, "ask old owner", ct);
            var incomingTaskId = incomingRes["task_id"]!.GetValue<string>();
            var oldOutboundRes = await DelegateAsync(coordinator, item.Id, sibling.Id, "old owner to sibling", ct);
            var oldOutboundTaskId = oldOutboundRes["task_id"]!.GetValue<string>();
            var unrelatedRes = await DelegateAsync(coordinator, parent.Id, sibling.Id, "unrelated sibling work", ct);
            var unrelatedTaskId = unrelatedRes["task_id"]!.GetValue<string>();

            // Reopen synchronously from registry.Changed inside CaptureForRelease before TerminalSessionProvider observes removal.
            replaceId = item.Id;
            var release = tabs.CaptureForRelease(item.Id);
            Assert.NotNull(release);
            var fresh = registry.Find(item.Id);
            Assert.NotNull(fresh);
            Assert.NotSame(old, fresh);

            var replacementOutboundRes = await DelegateAsync(coordinator, item.Id, sibling.Id, "fresh owner to sibling", ct);
            var replacementOutboundTaskId = replacementOutboundRes["task_id"]!.GetValue<string>();

            await release!();
            Frames.Pump();

            Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == incomingTaskId).Status);
            Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == oldOutboundTaskId).Status);
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == replacementOutboundTaskId).Status);
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == unrelatedTaskId).Status);
            Assert.Contains(siblingBridge.Delivered, d => d.Prompt.Contains("old owner to sibling", StringComparison.Ordinal));
            Assert.Contains(siblingBridge.Delivered, d => d.Prompt.Contains("fresh owner to sibling", StringComparison.Ordinal));

            replaceId = null;
            await tabs.ReleaseAllAsync();
        }
        finally
        {
            replaceId = null;
            registry.Changed -= Reopen;
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Tab_owned_delayed_same_object_exit_fails_only_exited_link_work_and_preserves_restarted_link_and_ui_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Tab Owned Restarted PTY", Surface = AgentSurface.Terminal };

        var term = registry.GetOrStart(
            item.Id,
            new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
            factory,
            new TerminalSize(80, 24, 0, 0),
            TerminalPalettes.Dark,
            ColorScheme.Dark,
            1000,
            out _);

        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausedOnce = 0;
        void PauseFirstExit(TerminalExit _)
        {
            if (Interlocked.Exchange(ref pausedOnce, 1) != 0) return;
            entered.TrySetResult();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        }

        term.Exited += PauseFirstExit;
        try
        {
            var (project, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-tab-stale-exit-");
            AppHost.Get<IProjectService>().AddItem(project, item, activate: false);
            var tabs = AppHost.Get<ITabContentRegistry>();
            var signals = AppHost.Get<ISignalHub>();

            var tab = Assert.IsType<TerminalTabView>(tabs.GetOrCreate(project, item));
            tab.EnsureStarted(startWhenHidden: true);

            var oldRes = await DelegateAsync(coordinator, parent.Id, item.Id, "first tab link", ct);
            var oldTaskId = oldRes["task_id"]!.GetValue<string>();
            var oldThreadId = oldRes["thread_id"]!.GetValue<string>();

            var restarted = Task.Run(async () =>
            {
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                    await term.RestartAsync();
                    return await DelegateAsync(coordinator, parent.Id, item.Id, "restarted tab link", ct);
                }
                finally
                {
                    resume.Set();
                }
            });

            factory.Connections[0].Exit(7);
            var freshRes = await restarted.WaitAsync(TimeSpan.FromSeconds(9), ct);
            var freshTaskId = freshRes["task_id"]!.GetValue<string>();
            var freshThreadId = freshRes["thread_id"]!.GetValue<string>();

            await WaitUntilAsync(() =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == oldTaskId).Status == CollabTaskStatus.Failed, ct, maxTicks: 80);
            await WaitUntilAsync(() => parentBridge.CountForThread(oldThreadId) == 1, ct, maxTicks: 60);

            Assert.Contains(factory.Connections[1].Written, w => w.Contains("restarted tab link", StringComparison.Ordinal));
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == freshTaskId).Status);
            Assert.True(tab.Terminal.IsRunning, "Stale link exit flipped TerminalControl.IsRunning to false on a live restarted link.");

            // Control 1: valid Done signal on restarted link completes freshTask cleanly.
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            signals.Publish(new AgentSignal("claude", "stop", item.Id, "", now, now, "{}", term.CurrentLinkId));
            await WaitUntilAsync(() =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == freshTaskId).Status == CollabTaskStatus.Completed, ct, maxTicks: 60);
            await WaitUntilAsync(() => parentBridge.CountForThread(freshThreadId) == 1, ct, maxTicks: 60);

            // Control 2: genuine exit on the active restarted link still flips IsRunning and fails its pending task.
            var liveRes = await DelegateAsync(coordinator, parent.Id, item.Id, "gen2 crashing tab task", ct);
            var liveTaskId = liveRes["task_id"]!.GetValue<string>();
            factory.Connections[1].Exit(13);
            await WaitUntilAsync(() =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == liveTaskId).Status == CollabTaskStatus.Failed, ct, maxTicks: 80);
            Assert.False(tab.Terminal.IsRunning);

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            resume.Set();
            term.Exited -= PauseFirstExit;
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Capturing_after_natural_exit_and_immediate_restart_settles_earlier_inbound_link_and_preserves_replacement()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        string? replaceId = null;

        void Reopen(string changedId)
        {
            if (changedId != replaceId || registry.Find(changedId) is not null) return;
            replaceId = null;
            registry.GetOrStart(
                changedId,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
        }

        // Subscribe before CollabCoordinator initializes so replacement attaches before TerminalSessionProvider observes removal.
        registry.Changed += Reopen;
        try
        {
            var (project, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-nat-exit-restart-cap-");
            var tabs = AppHost.Get<ITabContentRegistry>();
            var signals = AppHost.Get<ISignalHub>();
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Natural Exit Restart Capture", Surface = AgentSurface.Terminal };
            AppHost.Get<IProjectService>().AddItem(project, item, activate: false);

            var term = registry.GetOrStart(
                item.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            var firstRes = await DelegateAsync(coordinator, parent.Id, item.Id, "gen1 accepted before exit", ct);
            var firstTaskId = firstRes["task_id"]!.GetValue<string>();
            var firstThreadId = firstRes["thread_id"]!.GetValue<string>();

            // Gen 1 exits naturally; restart and scoped capture run synchronously before the UI dispatcher handles Gen 1's exit.
            factory.Connections[0].Exit(9);
            term.RestartAsync().GetAwaiter().GetResult();
            Assert.Equal(2, term.Generation);

            replaceId = item.Id;
            var release = tabs.CaptureForRelease(item.Id);
            Assert.NotNull(release);
            var replacement = registry.Find(item.Id);
            Assert.NotNull(replacement);
            Assert.NotSame(term, replacement);

            var secondRes = await DelegateAsync(coordinator, parent.Id, item.Id, "replacement task after capture", ct);
            var secondTaskId = secondRes["task_id"]!.GetValue<string>();
            var secondThreadId = secondRes["thread_id"]!.GetValue<string>();

            await release!();
            Frames.Pump();

            var firstTask = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == firstTaskId);
            var firstThread = coordinator.GetThreadsForItem(parent.Id).Single(t => t.ThreadId == firstThreadId);
            Assert.Equal(CollabTaskStatus.Cancelled, firstTask.Status);
            Assert.Equal(CollabThreadStatus.Cancelled, firstThread.Status);

            var secondTask = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == secondTaskId);
            Assert.Equal(CollabTaskStatus.Running, secondTask.Status);

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            signals.Publish(new AgentSignal("claude", "stop", item.Id, "", now, now, "{}", replacement!.CurrentLinkId));
            await WaitUntilAsync(() =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == secondTaskId).Status == CollabTaskStatus.Completed, ct, maxTicks: 60);
            await WaitUntilAsync(() => parentBridge.CountForThread(secondThreadId) == 1, ct, maxTicks: 60);

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            replaceId = null;
            registry.Changed -= Reopen;
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Capturing_after_multiple_natural_exits_and_restarts_settles_all_earlier_generations_and_preserves_replacement()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        string? replaceId = null;

        void Reopen(string changedId)
        {
            if (changedId != replaceId || registry.Find(changedId) is not null) return;
            replaceId = null;
            registry.GetOrStart(
                changedId,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
        }

        // Subscribe before CollabCoordinator initializes so replacement attaches before TerminalSessionProvider observes removal.
        registry.Changed += Reopen;
        try
        {
            var (project, parent, parentBridge, coordinator) = await SetUpParentAsync("octopus-0b-multigen-exit-cap-");
            var tabs = AppHost.Get<ITabContentRegistry>();
            var signals = AppHost.Get<ISignalHub>();
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Multi-Gen Exit Restart Capture", Surface = AgentSurface.Terminal };
            AppHost.Get<IProjectService>().AddItem(project, item, activate: false);

            var term = registry.GetOrStart(
                item.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            var gen1Res = await DelegateAsync(coordinator, parent.Id, item.Id, "gen1 task", ct);
            var gen1TaskId = gen1Res["task_id"]!.GetValue<string>();
            var gen1ThreadId = gen1Res["thread_id"]!.GetValue<string>();
            factory.Connections[0].Exit(11);
            term.RestartAsync().GetAwaiter().GetResult();
            Assert.Equal(2, term.Generation);

            // Keep the UI dispatcher un-pumped so both Gen 1 and Gen 2 exit posts remain pending when capture runs.
            var gen2Res = Task.Run(() => DelegateAsync(coordinator, parent.Id, item.Id, "gen2 task", ct)).WaitAsync(TimeSpan.FromSeconds(5), ct).GetAwaiter().GetResult();
            var gen2TaskId = gen2Res["task_id"]!.GetValue<string>();
            var gen2ThreadId = gen2Res["thread_id"]!.GetValue<string>();
            factory.Connections[1].Exit(12);
            term.RestartAsync().GetAwaiter().GetResult();
            Assert.Equal(3, term.Generation);

            var gen3Res = Task.Run(() => DelegateAsync(coordinator, parent.Id, item.Id, "gen3 live task", ct)).WaitAsync(TimeSpan.FromSeconds(5), ct).GetAwaiter().GetResult();
            var gen3TaskId = gen3Res["task_id"]!.GetValue<string>();
            var gen3ThreadId = gen3Res["thread_id"]!.GetValue<string>();

            replaceId = item.Id;
            var release = tabs.CaptureForRelease(item.Id);
            Assert.NotNull(release);
            var replacement = registry.Find(item.Id);
            Assert.NotNull(replacement);
            Assert.NotSame(term, replacement);

            var replacementRes = await DelegateAsync(coordinator, parent.Id, item.Id, "replacement task", ct);
            var replacementTaskId = replacementRes["task_id"]!.GetValue<string>();
            var replacementThreadId = replacementRes["thread_id"]!.GetValue<string>();

            await release!();
            Frames.Pump();

            var tasks = coordinator.GetTasksForItem(parent.Id);
            var threads = coordinator.GetThreadsForItem(parent.Id);
            Assert.Equal(CollabTaskStatus.Cancelled, tasks.Single(t => t.TaskId == gen1TaskId).Status);
            Assert.Equal(CollabThreadStatus.Cancelled, threads.Single(t => t.ThreadId == gen1ThreadId).Status);
            Assert.Equal(CollabTaskStatus.Cancelled, tasks.Single(t => t.TaskId == gen2TaskId).Status);
            Assert.Equal(CollabThreadStatus.Cancelled, threads.Single(t => t.ThreadId == gen2ThreadId).Status);
            Assert.Equal(CollabTaskStatus.Cancelled, tasks.Single(t => t.TaskId == gen3TaskId).Status);
            Assert.Equal(CollabThreadStatus.Cancelled, threads.Single(t => t.ThreadId == gen3ThreadId).Status);
            Assert.Equal(CollabTaskStatus.Running, tasks.Single(t => t.TaskId == replacementTaskId).Status);

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            signals.Publish(new AgentSignal("claude", "stop", item.Id, "", now, now, "{}", replacement!.CurrentLinkId));
            await WaitUntilAsync(() =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == replacementTaskId).Status == CollabTaskStatus.Completed, ct, maxTicks: 60);
            await WaitUntilAsync(() => parentBridge.CountForThread(replacementThreadId) == 1, ct, maxTicks: 60);

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            replaceId = null;
            registry.Changed -= Reopen;
            await registry.CloseAllAsync();
        }
    }

    private static int ReadTaskOwnershipEpoch(CollabCoordinator coordinator, string taskId)
    {
        var ledger = (Octopus.App.Collab.Ledger.CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        lock (ledger.Gate)
        {
            return ledger.GetTaskOwnershipEpoch(taskId);
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Failed_original_outbound_dispatch_with_capture_preserves_accepted_same_thread_replacement_followup()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        CollabCoordinator? activeCoordinator = null;
        string? watchedItemId = null;
        string? siblingItemId = null;
        string? sharedThreadId = null;
        Task<JsonObject>? followupCall = null;

        void ReopenAndFollowUp(string changedId)
        {
            if (changedId != watchedItemId || registry.Find(changedId) is not null) return;
            watchedItemId = null;
            registry.GetOrStart(
                changedId,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
            followupCall = activeCoordinator!.CallToolAsync(changedId, "send_message", new JsonObject
            {
                ["target_session_id"] = siblingItemId!,
                ["thread_id"] = sharedThreadId!,
                ["message"] = "replacement same-thread followup",
                ["expect_reply"] = true,
            }, ct);
        }

        registry.Changed += ReopenAndFollowUp;
        try
        {
            var (project, _, _, coordinator) = await SetUpParentAsync("octopus-0b-same-thread-followup-");
            activeCoordinator = coordinator;
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();

            var sibling = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Sibling", Surface = AgentSurface.Gui };
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Caller PTY", Surface = AgentSurface.Terminal };
            projects.AddItem(project, sibling, activate: false);
            projects.AddItem(project, item, activate: false);
            siblingItemId = sibling.Id;

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var refusal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var siblingBridge = new SpyGuiBridge(sibling.Id, "codex", sibling.Title, project.Path)
            {
                OnDeliver = (prompt, _, _, _) =>
                {
                    if (prompt.Contains("original refused dispatch", StringComparison.Ordinal))
                    {
                        entered.TrySetResult();
                        return refusal.Task;
                    }
                    return Task.FromResult(true);
                },
            };
            coordinator.RegisterGuiSession(siblingBridge);

            registry.GetOrStart(
                item.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            var pending = coordinator.CallToolAsync(item.Id, "delegate_task", new JsonObject
            {
                ["target"] = sibling.Id,
                ["title"] = "original refused dispatch",
                ["goal"] = "original refused dispatch",
            }, ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            var owned = coordinator.GetTasksForItem(item.Id).Single();
            sharedThreadId = owned.ThreadId!;
            watchedItemId = item.Id;

            var release = tabs.CaptureForRelease(item.Id);
            Assert.NotNull(release);
            Assert.NotNull(followupCall);
            var followupRes = await followupCall!.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Null(followupRes["isError"]);

            refusal.TrySetResult(false);
            var originalRes = await pending.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(true, originalRes["isError"]?.GetValue<bool>());

            await release!();
            Frames.Pump();

            var task = coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == owned.TaskId);
            var thread = coordinator.GetThreadsForItem(item.Id).Single(t => t.ThreadId == owned.ThreadId);
            Assert.Equal(CollabTaskStatus.Running, task.Status);
            Assert.Equal(CollabThreadStatus.Active, thread.Status);

            var replyRes = await coordinator.CallToolAsync(sibling.Id, "reply_thread", new JsonObject
            {
                ["thread_id"] = owned.ThreadId!,
                ["message"] = "replacement answer",
                ["status"] = "agreed",
            }, ct);
            Assert.Null(replyRes["isError"]);
            Assert.Equal(CollabTaskStatus.Completed, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == owned.TaskId).Status);

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            watchedItemId = null;
            registry.Changed -= ReopenAndFollowUp;
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Capturing_caller_while_target_rejects_outbound_dispatch_rolls_back_unaccepted_task_and_thread()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        string? replaceId = null;

        void Reopen(string changedId)
        {
            if (changedId != replaceId || registry.Find(changedId) is not null) return;
            replaceId = null;
            registry.GetOrStart(
                changedId,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
        }

        registry.Changed += Reopen;
        try
        {
            var (project, _, _, coordinator) = await SetUpParentAsync("octopus-0b-refused-outbound-rollback-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();

            var sibling = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Sibling", Surface = AgentSurface.Gui };
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Caller PTY", Surface = AgentSurface.Terminal };
            projects.AddItem(project, sibling, activate: false);
            projects.AddItem(project, item, activate: false);

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var refusal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var siblingBridge = new SpyGuiBridge(sibling.Id, "codex", sibling.Title, project.Path)
            {
                OnDeliver = (prompt, _, _, _) =>
                {
                    if (prompt.Contains("refused outbound", StringComparison.Ordinal))
                    {
                        entered.TrySetResult();
                        return refusal.Task;
                    }
                    return Task.FromResult(true);
                },
            };
            coordinator.RegisterGuiSession(siblingBridge);

            registry.GetOrStart(
                item.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            var pending = coordinator.CallToolAsync(item.Id, "delegate_task", new JsonObject
            {
                ["target"] = sibling.Id,
                ["title"] = "refused outbound",
                ["goal"] = "refused outbound",
            }, ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            var taskRecord = coordinator.GetTasksForItem(item.Id).Single();
            var taskId = taskRecord.TaskId;
            var threadId = taskRecord.ThreadId!;

            replaceId = item.Id;
            var release = tabs.CaptureForRelease(item.Id);
            Assert.NotNull(release);

            var replacementRes = await DelegateAsync(coordinator, item.Id, sibling.Id, "replacement accepted outbound", ct);
            var replacementTaskId = replacementRes["task_id"]!.GetValue<string>();

            refusal.TrySetResult(false);
            var refusedRes = await pending.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(true, refusedRes["isError"]?.GetValue<bool>());

            await release!();
            Frames.Pump();

            Assert.DoesNotContain(coordinator.GetTasksForItem(item.Id), t => t.TaskId == taskId);
            Assert.DoesNotContain(coordinator.GetThreadsForItem(item.Id), t => t.ThreadId == threadId);
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == replacementTaskId).Status);

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            replaceId = null;
            registry.Changed -= Reopen;
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Capturing_caller_while_target_pty_write_never_starts_rolls_back_unaccepted_outbound_task_and_thread()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var callerFactory = new FakeTerminalConnectionFactory();
        var held = new HeldWriteTerminalConnection();
        var targetFactory = new FakeTerminalConnectionFactory { StartOverride = (_, _, _) => held };
        string? replaceId = null;

        void Reopen(string changedId)
        {
            if (changedId != replaceId || registry.Find(changedId) is not null) return;
            replaceId = null;
            registry.GetOrStart(
                changedId,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                callerFactory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
        }

        registry.Changed += Reopen;
        try
        {
            var (project, _, _, coordinator) = await SetUpParentAsync("octopus-0b-held-outbound-rollback-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();

            var caller = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Caller PTY", Surface = AgentSurface.Terminal };
            var target = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Target PTY", Surface = AgentSurface.Terminal };
            var sibling = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Sibling", Surface = AgentSurface.Gui };
            projects.AddItem(project, caller, activate: false);
            projects.AddItem(project, target, activate: false);
            projects.AddItem(project, sibling, activate: false);
            coordinator.RegisterGuiSession(new SpyGuiBridge(sibling.Id, "codex", sibling.Title, project.Path));

            registry.GetOrStart(
                caller.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                callerFactory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
            var targetTerm = registry.GetOrStart(
                target.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                targetFactory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            Assert.True(targetTerm.TrySendCollabControl("\x1b", targetTerm.Generation));
            await held.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            using var delegateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pending = coordinator.CallToolAsync(caller.Id, "delegate_task", new JsonObject
            {
                ["target"] = target.Id,
                ["title"] = "never started target write",
                ["goal"] = "never started target write",
            }, delegateCts.Token);

            await WaitUntilAsync(() => coordinator.GetTasksForItem(caller.Id).Count == 1, ct, maxTicks: 60);
            var taskRecord = coordinator.GetTasksForItem(caller.Id).Single();
            var taskId = taskRecord.TaskId;
            var threadId = taskRecord.ThreadId!;

            replaceId = caller.Id;
            var release = tabs.CaptureForRelease(caller.Id);
            Assert.NotNull(release);

            var replacementRes = await DelegateAsync(coordinator, caller.Id, sibling.Id, "replacement accepted task", ct);
            var replacementTaskId = replacementRes["task_id"]!.GetValue<string>();

            delegateCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), ct));

            held.ReleaseFirstWrite.TrySetResult();
            await release!();
            Frames.Pump();

            Assert.DoesNotContain(held.Inner.Written, w => w.Contains("never started target write", StringComparison.Ordinal));
            Assert.DoesNotContain(coordinator.GetTasksForItem(caller.Id), t => t.TaskId == taskId);
            Assert.DoesNotContain(coordinator.GetThreadsForItem(caller.Id), t => t.ThreadId == threadId);
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(caller.Id).Single(t => t.TaskId == replacementTaskId).Status);

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            held.ReleaseFirstWrite.TrySetResult();
            replaceId = null;
            registry.Changed -= Reopen;
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Accepted_same_thread_followup_from_old_caller_is_cancelled_on_capture_while_replacement_survives()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        string? replaceId = null;

        void Reopen(string changedId)
        {
            if (changedId != replaceId || registry.Find(changedId) is not null) return;
            replaceId = null;
            registry.GetOrStart(
                changedId,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
        }

        registry.Changed += Reopen;
        try
        {
            var (project, _, _, coordinator) = await SetUpParentAsync("octopus-0b-reopened-followup-retire-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();

            var sibling = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Sibling", Surface = AgentSurface.Gui };
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Caller PTY", Surface = AgentSurface.Terminal };
            projects.AddItem(project, sibling, activate: false);
            projects.AddItem(project, item, activate: false);
            coordinator.RegisterGuiSession(new SpyGuiBridge(sibling.Id, "codex", sibling.Title, project.Path));

            registry.GetOrStart(
                item.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            var firstRes = await DelegateAsync(coordinator, item.Id, sibling.Id, "initial task", ct);
            var firstTaskId = firstRes["task_id"]!.GetValue<string>();
            var firstThreadId = firstRes["thread_id"]!.GetValue<string>();

            var doneRes = await coordinator.CallToolAsync(sibling.Id, "reply_thread", new JsonObject
            {
                ["thread_id"] = firstThreadId,
                ["message"] = "done once",
                ["status"] = "agreed",
            }, ct);
            Assert.Null(doneRes["isError"]);
            Assert.Equal(CollabTaskStatus.Completed, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == firstTaskId).Status);

            var followupRes = await coordinator.CallToolAsync(item.Id, "send_message", new JsonObject
            {
                ["target_session_id"] = sibling.Id,
                ["thread_id"] = firstThreadId,
                ["message"] = "reopen before capture",
                ["expect_reply"] = true,
            }, ct);
            Assert.Null(followupRes["isError"]);
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == firstTaskId).Status);

            replaceId = item.Id;
            var release = tabs.CaptureForRelease(item.Id);
            Assert.NotNull(release);

            var replacementRes = await DelegateAsync(coordinator, item.Id, sibling.Id, "replacement outbound", ct);
            var replacementTaskId = replacementRes["task_id"]!.GetValue<string>();

            await release!();
            Frames.Pump();

            Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == firstTaskId).Status);
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == replacementTaskId).Status);

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            replaceId = null;
            registry.Changed -= Reopen;
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Late_accepted_outbound_dispatch_after_caller_capture_is_cancelled_without_affecting_replacement()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        string? replaceId = null;

        void Reopen(string changedId)
        {
            if (changedId != replaceId || registry.Find(changedId) is not null) return;
            replaceId = null;
            registry.GetOrStart(
                changedId,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
        }

        registry.Changed += Reopen;
        try
        {
            var (project, _, _, coordinator) = await SetUpParentAsync("octopus-0b-late-accepted-outbound-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();

            var sibling = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Sibling", Surface = AgentSurface.Gui };
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Caller PTY", Surface = AgentSurface.Terminal };
            projects.AddItem(project, sibling, activate: false);
            projects.AddItem(project, item, activate: false);

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptLate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var siblingBridge = new SpyGuiBridge(sibling.Id, "codex", sibling.Title, project.Path)
            {
                OnDeliver = (prompt, _, _, _) =>
                {
                    if (prompt.Contains("late accepted outbound", StringComparison.Ordinal))
                    {
                        entered.TrySetResult();
                        return acceptLate.Task;
                    }
                    return Task.FromResult(true);
                },
            };
            coordinator.RegisterGuiSession(siblingBridge);

            registry.GetOrStart(
                item.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            var pending = coordinator.CallToolAsync(item.Id, "delegate_task", new JsonObject
            {
                ["target"] = sibling.Id,
                ["title"] = "late accepted outbound",
                ["goal"] = "late accepted outbound",
            }, ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            var lateTaskId = coordinator.GetTasksForItem(item.Id).Single().TaskId;

            replaceId = item.Id;
            var release = tabs.CaptureForRelease(item.Id);
            Assert.NotNull(release);

            var replacementRes = await DelegateAsync(coordinator, item.Id, sibling.Id, "replacement accepted task", ct);
            var replacementTaskId = replacementRes["task_id"]!.GetValue<string>();

            acceptLate.TrySetResult(true);
            var lateRes = await pending.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Null(lateRes["isError"]);

            await release!();
            Frames.Pump();

            Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == lateTaskId).Status);
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == replacementTaskId).Status);

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            replaceId = null;
            registry.Changed -= Reopen;
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Gui_bridge_accepting_before_receipt_cancels_on_caller_capture_and_avoids_double_epoch_bump()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        string? replaceId = null;

        void Reopen(string changedId)
        {
            if (changedId != replaceId || registry.Find(changedId) is not null) return;
            replaceId = null;
            registry.GetOrStart(
                changedId,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
        }

        registry.Changed += Reopen;
        try
        {
            var (project, _, _, coordinator) = await SetUpParentAsync("octopus-0b-gui-early-accept-retire-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();

            var sibling = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "EarlyAccept GUI", Surface = AgentSurface.Gui };
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Caller PTY", Surface = AgentSurface.Terminal };
            projects.AddItem(project, sibling, activate: false);
            projects.AddItem(project, item, activate: false);

            var acceptedFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptedSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var siblingBridge = new SpyGuiBridge(sibling.Id, "codex", sibling.Title, project.Path)
            {
                OnDeliverWithMeta = (prompt, threadId, taskId, expectReply, deliveryId, shouldCommit, _) =>
                {
                    if (!coordinator.TryAcceptCollabDelivery(sibling.Id, threadId, taskId, expectReply, deliveryId, shouldCommit))
                    {
                        return Task.FromResult(false);
                    }
                    if (prompt.Contains("early accept then retire", StringComparison.Ordinal))
                    {
                        acceptedFirst.TrySetResult();
                        return releaseFirst.Task;
                    }
                    if (prompt.Contains("early accept no double bump", StringComparison.Ordinal))
                    {
                        acceptedSecond.TrySetResult();
                        return releaseSecond.Task;
                    }
                    return Task.FromResult(true);
                },
            };
            coordinator.RegisterGuiSession(siblingBridge);

            registry.GetOrStart(
                item.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            // Part 1: GUI bridge accepts via TryAcceptCollabDelivery before outer receipt; retiring old caller cancels immediately.
            var firstPending = coordinator.CallToolAsync(item.Id, "delegate_task", new JsonObject
            {
                ["target"] = sibling.Id,
                ["title"] = "early accept then retire",
                ["goal"] = "early accept then retire",
            }, ct);
            await acceptedFirst.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            var firstTaskId = coordinator.GetTasksForItem(item.Id).Single().TaskId;
            Assert.Equal(1, ReadTaskOwnershipEpoch(coordinator, firstTaskId));

            replaceId = item.Id;
            var release = tabs.CaptureForRelease(item.Id);
            Assert.NotNull(release);

            // Cancelled immediately at retirement, even before the outer receipt returns.
            Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == firstTaskId).Status);

            releaseFirst.TrySetResult(true);
            await firstPending.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await release!();

            // Part 2: Replacement delegates via TryAcceptCollabDelivery before receipt -> exactly one epoch bump, no double bump on receipt return.
            var secondPending = coordinator.CallToolAsync(item.Id, "delegate_task", new JsonObject
            {
                ["target"] = sibling.Id,
                ["title"] = "early accept no double bump",
                ["goal"] = "early accept no double bump",
            }, ct);
            await acceptedSecond.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            var secondTaskId = coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId != firstTaskId).TaskId;
            Assert.Equal(1, ReadTaskOwnershipEpoch(coordinator, secondTaskId));

            releaseSecond.TrySetResult(true);
            var secondRes = await secondPending.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Null(secondRes["isError"]);
            Assert.Equal(1, ReadTaskOwnershipEpoch(coordinator, secondTaskId));
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == secondTaskId).Status);

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            replaceId = null;
            registry.Changed -= Reopen;
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Gui_bridge_accepting_before_receipt_does_not_let_stale_late_receipt_reclaim_accepted_replacement_followup()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        string? replaceId = null;

        void Reopen(string changedId)
        {
            if (changedId != replaceId || registry.Find(changedId) is not null) return;
            replaceId = null;
            registry.GetOrStart(
                changedId,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
        }

        registry.Changed += Reopen;
        try
        {
            var (project, parent, _, coordinator) = await SetUpParentAsync("octopus-0b-gui-stale-late-receipt-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();

            var sibling = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "EarlyAccept GUI", Surface = AgentSurface.Gui };
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Caller PTY", Surface = AgentSurface.Terminal };
            projects.AddItem(project, sibling, activate: false);
            projects.AddItem(project, item, activate: false);

            var acceptedOriginalA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOriginalA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptedOriginalB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOriginalB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var siblingBridge = new SpyGuiBridge(sibling.Id, "codex", sibling.Title, project.Path)
            {
                OnDeliverWithMeta = (prompt, threadId, taskId, expectReply, deliveryId, shouldCommit, _) =>
                {
                    if (!coordinator.TryAcceptCollabDelivery(sibling.Id, threadId, taskId, expectReply, deliveryId, shouldCommit))
                    {
                        return Task.FromResult(false);
                    }
                    if (prompt.Contains("original early accepted handoff", StringComparison.Ordinal))
                    {
                        acceptedOriginalA.TrySetResult();
                        return releaseOriginalA.Task;
                    }
                    if (prompt.Contains("original early accepted reopen", StringComparison.Ordinal))
                    {
                        acceptedOriginalB.TrySetResult();
                        return releaseOriginalB.Task;
                    }
                    return Task.FromResult(true);
                },
            };
            coordinator.RegisterGuiSession(siblingBridge);

            registry.GetOrStart(
                item.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            // Part A: Original terminal dispatch accepted early via TryAcceptCollabDelivery; peer takes over thread before old caller retires;
            // old caller retires; stale late receipt returns true -> must neither bump epoch nor reclaim/cancel peer's accepted work.
            var originalPendingA = coordinator.CallToolAsync(item.Id, "delegate_task", new JsonObject
            {
                ["target"] = sibling.Id,
                ["title"] = "original early accepted handoff",
                ["goal"] = "original early accepted handoff",
            }, ct);
            await acceptedOriginalA.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            var taskA = coordinator.GetTasksForItem(item.Id).Single();
            Assert.Equal(1, ReadTaskOwnershipEpoch(coordinator, taskA.TaskId));

            var peerTakeoverRes = await coordinator.CallToolAsync(parent.Id, "send_message", new JsonObject
            {
                ["target_session_id"] = sibling.Id,
                ["thread_id"] = taskA.ThreadId!,
                ["message"] = "gui peer takeover followup",
                ["expect_reply"] = true,
            }, ct);
            Assert.Null(peerTakeoverRes["isError"]);
            var epochAfterTakeoverA = ReadTaskOwnershipEpoch(coordinator, taskA.TaskId);
            Assert.Equal(2, epochAfterTakeoverA);

            replaceId = item.Id;
            var releaseA = tabs.CaptureForRelease(item.Id);
            Assert.NotNull(releaseA);

            releaseOriginalA.TrySetResult(true);
            await originalPendingA.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await releaseA!();
            Frames.Pump();

            Assert.Equal(epochAfterTakeoverA, ReadTaskOwnershipEpoch(coordinator, taskA.TaskId));
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == taskA.TaskId).Status);

            Assert.Null((await coordinator.CallToolAsync(sibling.Id, "reply_thread", new JsonObject
            {
                ["thread_id"] = taskA.ThreadId!,
                ["message"] = "peer takeover answer",
                ["status"] = "agreed",
            }, ct))["isError"]);
            Assert.Equal(CollabTaskStatus.Completed, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == taskA.TaskId).Status);

            // Part B: Original dispatch accepted early via TryAcceptCollabDelivery and explicitly completed before outer receipt returns;
            // caller is captured/replaced and replacement reopens the same thread with an accepted follow-up;
            // stale late receipt returns true -> must neither bump epoch nor reclaim/cancel replacement's reopened task.
            var originalPendingB = coordinator.CallToolAsync(item.Id, "delegate_task", new JsonObject
            {
                ["target"] = sibling.Id,
                ["title"] = "original early accepted reopen",
                ["goal"] = "original early accepted reopen",
            }, ct);
            await acceptedOriginalB.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            var taskB = coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId != taskA.TaskId);
            Assert.Equal(1, ReadTaskOwnershipEpoch(coordinator, taskB.TaskId));

            Assert.Null((await coordinator.CallToolAsync(sibling.Id, "reply_thread", new JsonObject
            {
                ["thread_id"] = taskB.ThreadId!,
                ["message"] = "completed before receipt",
                ["status"] = "agreed",
            }, ct))["isError"]);
            Assert.Equal(CollabTaskStatus.Completed, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == taskB.TaskId).Status);

            replaceId = item.Id;
            var releaseB = tabs.CaptureForRelease(item.Id);
            Assert.NotNull(releaseB);

            var replacementReopenRes = await coordinator.CallToolAsync(item.Id, "send_message", new JsonObject
            {
                ["target_session_id"] = sibling.Id,
                ["thread_id"] = taskB.ThreadId!,
                ["message"] = "replacement terminal reopen followup",
                ["expect_reply"] = true,
            }, ct);
            Assert.Null(replacementReopenRes["isError"]);
            var epochAfterReopenB = ReadTaskOwnershipEpoch(coordinator, taskB.TaskId);

            releaseOriginalB.TrySetResult(true);
            await originalPendingB.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await releaseB!();
            Frames.Pump();

            Assert.Equal(epochAfterReopenB, ReadTaskOwnershipEpoch(coordinator, taskB.TaskId));
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == taskB.TaskId).Status);

            Assert.Null((await coordinator.CallToolAsync(sibling.Id, "reply_thread", new JsonObject
            {
                ["thread_id"] = taskB.ThreadId!,
                ["message"] = "replacement reopen answer",
                ["status"] = "agreed",
            }, ct))["isError"]);
            Assert.Equal(CollabTaskStatus.Completed, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == taskB.TaskId).Status);

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            replaceId = null;
            registry.Changed -= Reopen;
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Legacy_bridge_stale_late_receipt_cannot_steal_ownership_from_accepted_replacement_followup()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        CollabCoordinator? activeCoordinator = null;
        string? watchedItemId = null;
        string? siblingItemId = null;
        string? sharedThreadId = null;
        Task<JsonObject>? followupCall = null;

        void ReopenAndFollowUp(string changedId)
        {
            if (changedId != watchedItemId || registry.Find(changedId) is not null) return;
            watchedItemId = null;
            registry.GetOrStart(
                changedId,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
            followupCall = activeCoordinator!.CallToolAsync(changedId, "send_message", new JsonObject
            {
                ["target_session_id"] = siblingItemId!,
                ["thread_id"] = sharedThreadId!,
                ["message"] = "legacy replacement followup",
                ["expect_reply"] = true,
            }, ct);
        }

        registry.Changed += ReopenAndFollowUp;
        try
        {
            var (project, _, _, coordinator) = await SetUpParentAsync("octopus-0b-legacy-stale-late-receipt-");
            activeCoordinator = coordinator;
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();

            var sibling = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Legacy Sibling", Surface = AgentSurface.Gui };
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Caller PTY", Surface = AgentSurface.Terminal };
            projects.AddItem(project, sibling, activate: false);
            projects.AddItem(project, item, activate: false);
            siblingItemId = sibling.Id;

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOriginal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var siblingBridge = new SpyGuiBridge(sibling.Id, "codex", sibling.Title, project.Path)
            {
                OnDeliver = (prompt, _, _, _) =>
                {
                    if (prompt.Contains("legacy original late receipt", StringComparison.Ordinal))
                    {
                        entered.TrySetResult();
                        return releaseOriginal.Task;
                    }
                    return Task.FromResult(true);
                },
            };
            coordinator.RegisterGuiSession(siblingBridge);

            registry.GetOrStart(
                item.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            var originalPending = coordinator.CallToolAsync(item.Id, "delegate_task", new JsonObject
            {
                ["target"] = sibling.Id,
                ["title"] = "legacy original late receipt",
                ["goal"] = "legacy original late receipt",
            }, ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            var owned = coordinator.GetTasksForItem(item.Id).Single();
            sharedThreadId = owned.ThreadId!;
            watchedItemId = item.Id;

            var release = tabs.CaptureForRelease(item.Id);
            Assert.NotNull(release);
            Assert.NotNull(followupCall);
            var followupRes = await followupCall!.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Null(followupRes["isError"]);

            var epochAfterSuccessor = ReadTaskOwnershipEpoch(coordinator, owned.TaskId);
            Assert.Equal(1, epochAfterSuccessor);

            // Original unaccepted legacy bridge receipt returns true late after replacement already accepted and old caller retired.
            releaseOriginal.TrySetResult(true);
            await originalPending.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await release!();
            Frames.Pump();

            Assert.Equal(epochAfterSuccessor, ReadTaskOwnershipEpoch(coordinator, owned.TaskId));
            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == owned.TaskId).Status);

            var replyRes = await coordinator.CallToolAsync(sibling.Id, "reply_thread", new JsonObject
            {
                ["thread_id"] = owned.ThreadId!,
                ["message"] = "legacy successor answer",
                ["status"] = "agreed",
            }, ct);
            Assert.Null(replyRes["isError"]);
            Assert.Equal(CollabTaskStatus.Completed, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == owned.TaskId).Status);

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            watchedItemId = null;
            registry.Changed -= ReopenAndFollowUp;
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Nonaccepted_followup_control_does_not_reopen_completed_task_or_overwrite_replacement_terminal_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        string? replaceId = null;

        void Reopen(string changedId)
        {
            if (changedId != replaceId || registry.Find(changedId) is not null) return;
            replaceId = null;
            registry.GetOrStart(
                changedId,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
        }

        registry.Changed += Reopen;
        try
        {
            var (project, parent, _, coordinator) = await SetUpParentAsync("octopus-0b-nonaccepted-followup-ctrl-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();

            var sibling = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Sibling", Surface = AgentSurface.Gui };
            var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Caller PTY", Surface = AgentSurface.Terminal };
            projects.AddItem(project, sibling, activate: false);
            projects.AddItem(project, item, activate: false);

            var refuseFollowups = false;
            var siblingBridge = new SpyGuiBridge(sibling.Id, "codex", sibling.Title, project.Path)
            {
                OnDeliver = (prompt, _, _, _) => Task.FromResult(!refuseFollowups || !prompt.Contains("refused followup", StringComparison.Ordinal)),
            };
            coordinator.RegisterGuiSession(siblingBridge);

            registry.GetOrStart(
                item.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            // Part A: Refused follow-up on a Completed task must NOT reopen it.
            var firstRes = await DelegateAsync(coordinator, item.Id, sibling.Id, "task to complete", ct);
            var firstTaskId = firstRes["task_id"]!.GetValue<string>();
            var firstThreadId = firstRes["thread_id"]!.GetValue<string>();
            Assert.Null((await coordinator.CallToolAsync(sibling.Id, "reply_thread", new JsonObject
            {
                ["thread_id"] = firstThreadId,
                ["message"] = "finished",
                ["status"] = "agreed",
            }, ct))["isError"]);
            Assert.Equal(CollabTaskStatus.Completed, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == firstTaskId).Status);

            refuseFollowups = true;
            var refusedReopen = await coordinator.CallToolAsync(item.Id, "send_message", new JsonObject
            {
                ["target_session_id"] = sibling.Id,
                ["thread_id"] = firstThreadId,
                ["message"] = "refused followup reopen",
                ["expect_reply"] = true,
            }, ct);
            Assert.Equal(true, refusedReopen["isError"]?.GetValue<bool>());
            Assert.Equal(CollabTaskStatus.Completed, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == firstTaskId).Status);

            // Part B: Refused follow-up from GUI parent on a Running terminal-owned task must NOT bump epoch or clear terminal owner.
            var secondRes = await DelegateAsync(coordinator, item.Id, sibling.Id, "terminal owned running task", ct);
            var secondTaskId = secondRes["task_id"]!.GetValue<string>();
            var secondThreadId = secondRes["thread_id"]!.GetValue<string>();
            var epochBeforeRefused = ReadTaskOwnershipEpoch(coordinator, secondTaskId);

            var refusedInterloper = await coordinator.CallToolAsync(parent.Id, "send_message", new JsonObject
            {
                ["target_session_id"] = sibling.Id,
                ["thread_id"] = secondThreadId,
                ["message"] = "refused followup from gui",
                ["expect_reply"] = true,
            }, ct);
            Assert.Equal(true, refusedInterloper["isError"]?.GetValue<bool>());
            Assert.Equal(epochBeforeRefused, ReadTaskOwnershipEpoch(coordinator, secondTaskId));

            // Capture terminal caller -> secondTaskId is still owned by that terminal link and cancels cleanly.
            replaceId = item.Id;
            var release = tabs.CaptureForRelease(item.Id);
            Assert.NotNull(release);
            await release!();
            Frames.Pump();

            Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(item.Id).Single(t => t.TaskId == secondTaskId).Status);

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            replaceId = null;
            registry.Changed -= Reopen;
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Real_gui_accepted_queued_followup_reopens_cancelled_task_and_survives_stale_old_receipt()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        string? watch = null;

        void Reopen(string id)
        {
            if (id != watch || registry.Find(id) is not null) return;
            watch = null;
            registry.GetOrStart(
                id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
        }

        registry.Changed += Reopen;
        var initialTurn = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedTurn = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var (project, _, _, coordinator) = await SetUpParentAsync("octopus-0b-gui-reopen-queued-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();

            var caller = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Caller PTY", Surface = AgentSurface.Terminal };
            var target = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Connected GUI target", Surface = AgentSurface.Gui };
            projects.AddItem(project, caller, activate: false);
            projects.AddItem(project, target, activate: false);

            var old = registry.GetOrStart(
                caller.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            string? sharedThread = null;
            Func<Task>? release = null;
            var freshQueued = false;

            var view = tabs.GetOrCreate(project, target);
            var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);
            AppHost.Get<FakeAcpSessionFactory>().ConfigureNext = s => s.PromptHandler = (_, prompt, _, token) =>
            {
                if (prompt.Contains("first round", StringComparison.Ordinal))
                {
                    return initialTurn.Task.WaitAsync(token);
                }
                if (prompt.Contains("old round", StringComparison.Ordinal))
                {
                    watch = caller.Id;
                    release = tabs.CaptureForRelease(caller.Id);
                    Assert.NotNull(release);
                    Assert.NotSame(old, registry.Find(caller.Id));
                    var next = coordinator.CallToolAsync(caller.Id, "send_message", new JsonObject
                    {
                        ["target_session_id"] = target.Id,
                        ["thread_id"] = sharedThread!,
                        ["message"] = "fresh round",
                        ["expect_reply"] = true,
                    }, ct).GetAwaiter().GetResult();
                    Assert.Null(next["isError"]);
                    freshQueued = vm.HasQueuedPrompts;
                }
                return delayedTurn.Task.WaitAsync(token);
            };
            await vm.ConnectAsync();
            Frames.Pump();

            var rootRes = await DelegateAsync(coordinator, caller.Id, target.Id, "first round", ct);
            var rootTaskId = rootRes["task_id"]!.GetValue<string>();
            sharedThread = rootRes["thread_id"]!.GetValue<string>();
            initialTurn.SetResult(new AcpPromptResult { StopReason = "end_turn" });
            await WaitUntilAsync(() =>
                coordinator.GetTasksForItem(caller.Id).Single(t => t.TaskId == rootTaskId).Status == CollabTaskStatus.Completed
                && vm.State == AgentGuiState.Ready, ct, maxTicks: 80);

            var oldFollow = await coordinator.CallToolAsync(caller.Id, "send_message", new JsonObject
            {
                ["target_session_id"] = target.Id,
                ["thread_id"] = sharedThread,
                ["message"] = "old round",
                ["expect_reply"] = true,
            }, ct);
            Assert.Null(oldFollow["isError"]);
            Assert.True(freshQueued);
            Assert.NotNull(release);
            await release!();

            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(caller.Id).Single(t => t.TaskId == rootTaskId).Status);

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            initialTurn.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
            delayedTurn.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
            watch = null;
            registry.Changed -= Reopen;
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Earlier_link_exit_then_capture_preserves_replacement_same_thread_inbound_followup(bool extraGeneration)
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        string? watch = null;
        Action? sameThreadFollow = null;

        void Swap(string id)
        {
            if (id != watch) return;
            if (registry.Find(id) is null)
            {
                registry.GetOrStart(
                    id,
                    new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                    factory,
                    new TerminalSize(80, 24, 0, 0),
                    TerminalPalettes.Dark,
                    ColorScheme.Dark,
                    1000,
                    out _);
            }
            else
            {
                var run = sameThreadFollow;
                sameThreadFollow = null;
                run?.Invoke();
            }
        }

        registry.Changed += Swap;
        try
        {
            var (project, parent, _, coordinator) = await SetUpParentAsync("octopus-0b-inbound-same-thread-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();
            var signals = AppHost.Get<ISignalHub>();

            var target = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Target PTY", Surface = AgentSurface.Terminal };
            projects.AddItem(project, target, activate: false);

            var old = registry.GetOrStart(
                target.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            var rootRes = await DelegateAsync(coordinator, parent.Id, target.Id, "original inbound", ct);
            var rootTaskId = rootRes["task_id"]!.GetValue<string>();
            var rootThreadId = rootRes["thread_id"]!.GetValue<string>();
            if (extraGeneration)
            {
                factory.Connections[0].Exit(9);
                old.RestartAsync().GetAwaiter().GetResult();
            }

            watch = target.Id;
            sameThreadFollow = () =>
            {
                Assert.NotSame(old, registry.Find(target.Id));
                var successor = coordinator.CallToolAsync(parent.Id, "send_message", new JsonObject
                {
                    ["target_session_id"] = target.Id,
                    ["thread_id"] = rootThreadId,
                    ["message"] = "replacement owns same thread",
                    ["expect_reply"] = true,
                }, ct).GetAwaiter().GetResult();
                Assert.Null(successor["isError"]);
                Assert.Contains(factory.Connections[^1].Written, w => w.Contains("replacement owns same thread", StringComparison.Ordinal));
            };

            var release = tabs.CaptureForRelease(target.Id);
            Assert.NotNull(release);
            Assert.Null(sameThreadFollow);
            await release!();
            Frames.Pump();

            Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == rootTaskId).Status);

            var fresh = registry.Find(target.Id)!;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            signals.Publish(new AgentSignal("claude", "stop", target.Id, "", now, now, "{}", fresh.CurrentLinkId));
            await WaitUntilAsync(() =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == rootTaskId).Status == CollabTaskStatus.Completed, ct, maxTicks: 60);

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            watch = null;
            sameThreadFollow = null;
            registry.Changed -= Swap;
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Direct_gui_dispatch_refuses_before_acp_when_caller_terminal_retires_before_ingress(bool retireBeforeUi, bool explicitReply)
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        string? watch = null;

        void Reopen(string id)
        {
            if (id != watch || registry.Find(id) is not null) return;
            watch = null;
            registry.GetOrStart(
                id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
        }

        registry.Changed += Reopen;
        try
        {
            var (project, _, _, coordinator) = await SetUpParentAsync("octopus-0b-direct-gui-retire-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();

            var caller = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Caller PTY", Surface = AgentSurface.Terminal };
            var target = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Direct GUI", Surface = AgentSurface.Gui };
            projects.AddItem(project, caller, activate: false);
            projects.AddItem(project, target, activate: false);

            var old = registry.GetOrStart(
                caller.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            const string answer = "Synchronous direct answer";
            string? threadId = null;
            var vm = Assert.IsType<AgentGuiTabViewModel>(tabs.GetOrCreate(project, target).DataContext);
            AppHost.Get<FakeAcpSessionFactory>().ConfigureNext = s => s.PromptHandler = async (_, _, _, _) =>
            {
                if (explicitReply)
                {
                    await coordinator.CallToolAsync(target.Id, "reply_thread", new JsonObject
                    {
                        ["thread_id"] = threadId!,
                        ["message"] = answer,
                        ["status"] = "agreed",
                    }, ct);
                }
                s.Emit(new AcpTextDeltaEvent("assistant", answer, "direct-msg"));
                return new AcpPromptResult { StopReason = "end_turn" };
            };
            await vm.ConnectAsync();
            var acp = AppHost.Get<FakeAcpSessionFactory>().Created.Last();

            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var gate = typeof(AgentGuiTabViewModel).GetField("_collabDeliveryGate", flags)!.GetValue(vm)!;
            var pendingQueue = (System.Collections.ICollection)typeof(AgentGuiTabViewModel).GetField("_pendingCollabDeliveries", flags)!.GetValue(vm)!;

            var operation = Task.Run(() => coordinator.CallToolAsync(caller.Id, "delegate_task", new JsonObject
            {
                ["target"] = target.Id,
                ["goal"] = "old caller direct job",
            }, ct));
            Assert.True(SpinWait.SpinUntil(() => { lock (gate) return pendingQueue.Count == 1; }, TimeSpan.FromSeconds(2)));
            Assert.Empty(acp.SentPrompts);

            var row = Assert.Single(coordinator.GetTasksForItem(caller.Id));
            threadId = row.ThreadId;
            if (retireBeforeUi)
            {
                watch = caller.Id;
                var release = tabs.CaptureForRelease(caller.Id);
                Assert.NotNull(release);
                Assert.NotSame(old, registry.Find(caller.Id));
                await release!();
            }

            Frames.Pump();
            var returned = await operation;
            await WaitUntilAsync(() => !vm.IsTurnActive, ct, maxTicks: 80);
            Frames.Until(() => factory.Connections[^1].Written.Any(w => w.Contains(answer, StringComparison.Ordinal)), milliseconds: 400);

            var finalTask = coordinator.GetTasksForItem(caller.Id).SingleOrDefault(t => t.TaskId == row.TaskId);
            var wrote = factory.Connections[^1].Written.Any(w => w.Contains(answer, StringComparison.Ordinal));
            if (retireBeforeUi)
            {
                Assert.Equal(true, returned["isError"]?.GetValue<bool>());
                Assert.Empty(acp.SentPrompts);
                Assert.False(wrote);
                Assert.True(finalTask is null || finalTask.Status == CollabTaskStatus.Cancelled);
            }
            else
            {
                Assert.Null(returned["isError"]);
                Assert.Single(acp.SentPrompts);
                Assert.True(wrote);
                Assert.Equal(CollabTaskStatus.Completed, finalTask?.Status);
            }

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            watch = null;
            registry.Changed -= Reopen;
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaTheory, FreshApp]
    [InlineData("replacement", "explicit")]
    [InlineData("live-restart", "implicit")]
    public async Task Held_acknowledged_delegation_suppresses_explicit_and_live_restart_answers_from_retired_caller(string takeover, string completion)
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        string? watch = null;

        void Reopen(string id)
        {
            if (id != watch || registry.Find(id) is not null) return;
            watch = null;
            registry.GetOrStart(
                id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
        }

        registry.Changed += Reopen;
        var acked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var (project, _, _, coordinator) = await SetUpParentAsync("octopus-0b-held-routes-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();

            var caller = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Caller PTY", Surface = AgentSurface.Terminal };
            var target = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Target Bridge", Surface = AgentSurface.Gui };
            projects.AddItem(project, caller, activate: false);
            projects.AddItem(project, target, activate: false);

            var old = registry.GetOrStart(
                caller.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            var targetFactory = new FakeTerminalConnectionFactory();
            registry.GetOrStart(
                target.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                targetFactory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
            using var ptyTarget = new Octopus.App.Collab.Sessions.TerminalCollabSession(target.Id, "codex", target.Title, project.Path, registry, null);

            var bridge = new SpyGuiBridge(target.Id, "codex", target.Title, project.Path)
            {
                OnDeliver = async (prompt, thread, taskId, token) =>
                {
                    Assert.True(await ptyTarget.DeliverCollabPromptAsync(prompt, thread, taskId, token));
                    acked.SetResult();
                    await receipt.Task;
                    return true;
                },
            };
            coordinator.RegisterGuiSession(bridge);

            var call = coordinator.CallToolAsync(caller.Id, "delegate_task", new JsonObject
            {
                ["target"] = target.Id,
                ["goal"] = "accepted before caller retired",
            }, ct);
            await acked.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
            var task = Assert.Single(coordinator.GetTasksForItem(caller.Id));

            watch = caller.Id;
            if (takeover == "live-restart")
            {
                var oldLink = old.CurrentLinkId;
                await old.RestartAsync();
                Assert.NotEqual(oldLink, old.CurrentLinkId);
            }
            else
            {
                var release = tabs.CaptureForRelease(caller.Id);
                Assert.NotNull(release);
                await release!();
                Assert.NotSame(old, registry.Find(caller.Id));
            }

            const string leakedText = "answer from retired caller turn";
            if (completion == "explicit")
            {
                await coordinator.CallToolAsync(target.Id, "reply_thread", new JsonObject
                {
                    ["thread_id"] = task.ThreadId!,
                    ["message"] = leakedText,
                    ["status"] = "agreed",
                }, ct);
            }
            else
            {
                coordinator.NotifyTurnCompleted(target.Id, leakedText, task.ThreadId, task.TaskId);
            }

            await WaitUntilAsync(() => coordinator.GetTasksForItem(caller.Id).Single(t => t.TaskId == task.TaskId).IsFinished, ct, maxTicks: 80);
            receipt.SetResult();
            await call;

            var finalState = coordinator.GetTasksForItem(caller.Id).Single(t => t.TaskId == task.TaskId).Status;
            Assert.Equal(CollabTaskStatus.Cancelled, finalState);
            Assert.DoesNotContain(factory.Connections[1].Written, w => w.Contains(leakedText, StringComparison.Ordinal));

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            receipt.TrySetResult();
            watch = null;
            registry.Changed -= Reopen;
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Gui_stop_cancel_task_unrelated_peer_and_refused_followup_preserve_cancellation_and_answers()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new FakeTerminalConnectionFactory();
        string? watch = null;

        void Reopen(string id)
        {
            if (id != watch || registry.Find(id) is not null) return;
            watch = null;
            registry.GetOrStart(
                id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);
        }

        registry.Changed += Reopen;
        try
        {
            var (project, sibling, _, coordinator) = await SetUpParentAsync("octopus-0b-cancel-ownership-");
            var projects = AppHost.Get<IProjectService>();
            var tabs = AppHost.Get<ITabContentRegistry>();

            var caller = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Caller PTY", Surface = AgentSurface.Terminal };
            var target = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Real Worker GUI", Surface = AgentSurface.Gui };
            projects.AddItem(project, caller, activate: false);
            projects.AddItem(project, target, activate: false);

            registry.GetOrStart(
                caller.Id,
                new LaunchSpec("fake", [], null, new Dictionary<string, string>()),
                factory,
                new TerminalSize(80, 24, 0, 0),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                1000,
                out _);

            var held = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var later = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var vm = Assert.IsType<AgentGuiTabViewModel>(tabs.GetOrCreate(project, target).DataContext);
            AppHost.Get<FakeAcpSessionFactory>().ConfigureNext = s =>
            {
                s.PromptHandler = (_, prompt, _, token) =>
                    (prompt.Contains("first stopped job", StringComparison.Ordinal) ? held : later).Task.WaitAsync(token);
                s.CancelHandler = _ =>
                {
                    held.TrySetResult(new AcpPromptResult { StopReason = "cancelled" });
                    return Task.CompletedTask;
                };
            };
            await vm.ConnectAsync();
            var acp = AppHost.Get<FakeAcpSessionFactory>().Created.Last();

            // 1. GUI Stop + later question on same thread: original task keeps Cancelled & user-stop reason while later question receives its answer.
            var stoppedRes = await DelegateAsync(coordinator, caller.Id, target.Id, "first stopped job", ct);
            var stoppedTaskId = stoppedRes["task_id"]!.GetValue<string>();
            var stoppedThreadId = stoppedRes["thread_id"]!.GetValue<string>();
            await WaitUntilAsync(() => acp.SentPrompts.Count == 1 && vm.IsTurnActive, ct, maxTicks: 80);
            await vm.CancelTurnAsync();
            await WaitUntilAsync(() => !vm.IsTurnActive && coordinator.GetTasksForItem(caller.Id).Single(t => t.TaskId == stoppedTaskId).Status == CollabTaskStatus.Cancelled, ct, maxTicks: 80);
            var stopReason = coordinator.GetTasksForItem(caller.Id).Single(t => t.TaskId == stoppedTaskId).ResultSummary;
            Assert.Contains("user stopped", stopReason, StringComparison.Ordinal);

            var laterFollow = await coordinator.CallToolAsync(caller.Id, "send_message", new JsonObject
            {
                ["target_session_id"] = target.Id,
                ["thread_id"] = stoppedThreadId,
                ["message"] = "separate later question",
                ["expect_reply"] = true,
            }, ct);
            Assert.Null(laterFollow["isError"]);
            await WaitUntilAsync(() => acp.SentPrompts.Any(p => p.Text.Contains("separate later question", StringComparison.Ordinal)), ct, maxTicks: 80);
            Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(caller.Id).Single(t => t.TaskId == stoppedTaskId).Status);

            acp.Emit(new AcpTextDeltaEvent("assistant", "Answer for later question", "later-msg"));
            later.SetResult(new AcpPromptResult { StopReason = "end_turn" });
            await WaitUntilAsync(() => !vm.IsTurnActive, ct, maxTicks: 80);
            await WaitUntilAsync(() => coordinator.FindThread(stoppedThreadId)!.Messages.Any(m => m.Text == "Answer for later question"), ct, maxTicks: 80);

            var stoppedTaskAfter = coordinator.GetTasksForItem(caller.Id).Single(t => t.TaskId == stoppedTaskId);
            Assert.Equal(CollabTaskStatus.Cancelled, stoppedTaskAfter.Status);
            Assert.Equal(stopReason, stoppedTaskAfter.ResultSummary);

            // 2. Direct 5-arg GUI overload after Stop must NOT reopen Cancelled task.
            var deliveredDirect = await vm.DeliverCollabPromptAsync("direct five-arg call", stoppedThreadId, stoppedTaskId, true, ct);
            Assert.True(deliveredDirect);
            Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(caller.Id).Single(t => t.TaskId == stoppedTaskId).Status);

            // 3. Held legacy positive receipt after cancel_task must NOT reopen Cancelled task.
            var stubTarget = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Stub Worker", Surface = AgentSurface.Gui };
            projects.AddItem(project, stubTarget, activate: false);
            var receiptTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var enteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var refuseNext = false;
            var stubBridge = new SpyGuiBridge(stubTarget.Id, "codex", stubTarget.Title, project.Path)
            {
                OnDeliver = (prompt, _, _, _) =>
                {
                    if (prompt.Contains("held before cancel_task", StringComparison.Ordinal))
                    {
                        enteredTcs.TrySetResult();
                        return receiptTcs.Task;
                    }
                    return Task.FromResult(!refuseNext);
                },
            };
            coordinator.RegisterGuiSession(stubBridge);

            var explicitCancelRes = await DelegateAsync(coordinator, caller.Id, stubTarget.Id, "to be cancelled explicitly", ct);
            var explicitCancelTaskId = explicitCancelRes["task_id"]!.GetValue<string>();
            var explicitCancelThreadId = explicitCancelRes["thread_id"]!.GetValue<string>();
            var heldFollow = coordinator.CallToolAsync(caller.Id, "send_message", new JsonObject
            {
                ["target_session_id"] = stubTarget.Id,
                ["thread_id"] = explicitCancelThreadId,
                ["message"] = "held before cancel_task",
                ["expect_reply"] = true,
            }, ct);
            await enteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);

            var cancelRes = await coordinator.CallToolAsync(caller.Id, "cancel_task", new JsonObject
            {
                ["task_id"] = explicitCancelTaskId,
                ["reason"] = "deliberately closed",
            }, ct);
            Assert.Null(cancelRes["isError"]);
            receiptTcs.SetResult(true);
            await heldFollow;

            var explicitlyCancelled = coordinator.GetTasksForItem(caller.Id).Single(t => t.TaskId == explicitCancelTaskId);
            Assert.Equal(CollabTaskStatus.Cancelled, explicitlyCancelled.Status);
            Assert.Equal(CollabThreadStatus.Cancelled, coordinator.FindThread(explicitCancelThreadId)!.Status);
            Assert.Equal("deliberately closed", explicitlyCancelled.ResultSummary);

            // 4. After caller retirement: refused successor keeps Cancelled/Cancelled, and unrelated peer gets its answer while retired task stays Cancelled.
            var retiredRes = await DelegateAsync(coordinator, caller.Id, stubTarget.Id, "retiring terminal task", ct);
            var retiredTaskId = retiredRes["task_id"]!.GetValue<string>();
            var retiredThreadId = retiredRes["thread_id"]!.GetValue<string>();
            watch = caller.Id;
            await tabs.ReleaseAsync(caller.Id);
            Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(caller.Id).Single(t => t.TaskId == retiredTaskId).Status);
            Assert.Equal(CollabThreadStatus.Cancelled, coordinator.FindThread(retiredThreadId)!.Status);

            refuseNext = true;
            var refusedSuccessor = await coordinator.CallToolAsync(caller.Id, "send_message", new JsonObject
            {
                ["target_session_id"] = stubTarget.Id,
                ["thread_id"] = retiredThreadId,
                ["message"] = "refused successor",
                ["expect_reply"] = true,
            }, ct);
            Assert.Equal(true, refusedSuccessor["isError"]?.GetValue<bool>());
            Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(caller.Id).Single(t => t.TaskId == retiredTaskId).Status);
            Assert.Equal(CollabThreadStatus.Cancelled, coordinator.FindThread(retiredThreadId)!.Status);

            refuseNext = false;
            var unrelatedFollow = await coordinator.CallToolAsync(sibling.Id, "send_message", new JsonObject
            {
                ["target_session_id"] = stubTarget.Id,
                ["thread_id"] = retiredThreadId,
                ["message"] = "unrelated peer question",
                ["expect_reply"] = true,
            }, ct);
            Assert.Null(unrelatedFollow["isError"]);
            Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(caller.Id).Single(t => t.TaskId == retiredTaskId).Status);
            coordinator.NotifyTurnCompleted(stubTarget.Id, "answer for unrelated peer", retiredThreadId, null);
            await WaitUntilAsync(() => coordinator.FindThread(retiredThreadId)!.Messages.Last().Text == "answer for unrelated peer", ct, maxTicks: 80);
            Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(caller.Id).Single(t => t.TaskId == retiredTaskId).Status);
            Assert.Equal(sibling.Id, coordinator.FindThread(retiredThreadId)!.Messages.Last().ToItemId);

            await tabs.ReleaseAllAsync();
        }
        finally
        {
            watch = null;
            registry.Changed -= Reopen;
            await registry.CloseAllAsync();
        }
    }
}
