using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.Agents.Signals;
using Octopus.App.Collab;
using Octopus.App.Collab.Sessions;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Core.Terminals;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using TKCode.Terminal.Core;
using Xunit;

namespace Octopus.App.Tests;

public sealed class TerminalCollabSessionTests
{
    private sealed class ScriptedTerminalConnection : ITerminalConnection
    {
        private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource<TerminalExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 4242;
        public Task<TerminalExit> Exited => _exit.Task;
        public ConcurrentQueue<string> Written { get; } = new();
        public Func<string, ValueTask>? OnWrite { get; set; }

        public void EmitOutput(string text) => _output.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public void Exit(int code = 0)
        {
            _output.Writer.TryComplete();
            _exit.TrySetResult(new TerminalExit(code, null));
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in _output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
        {
            var text = Encoding.UTF8.GetString(input.Span);
            if (OnWrite is not null)
            {
                await OnWrite(text).ConfigureAwait(false);
            }
            Written.Enqueue(text);
        }

        public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight) => ValueTask.CompletedTask;

        public ValueTask SignalAsync(TerminalSignal signal)
        {
            Exit(0);
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);

        public ValueTask DisposeAsync()
        {
            Exit(0);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedTerminalFactory : ITerminalConnectionFactory
    {
        public List<ScriptedTerminalConnection> Connections { get; } = [];
        public Action<ScriptedTerminalConnection>? ConfigureNext { get; set; }

        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows)
        {
            var connection = new ScriptedTerminalConnection();
            ConfigureNext?.Invoke(connection);
            Connections.Add(connection);
            return connection;
        }
    }

    private sealed class StubParentBridge(ProjectItem item, string cwd) : ICollabSessionBridge
    {
        public string ItemId => item.Id;
        public string AgentId => item.AgentId ?? "claude";
        public string DisplayTitle => item.DisplayTitle;
        public string WorkingDirectory => cwd;
        public bool IsBusy => false;
        public bool IsConnected => true;
        public string? CurrentStatusDetail => null;
        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public string ReadRecentSummary(int maxTurns = 5) => "";
    }

    private static LaunchSpec DummySpec() => new("claude", [], null, new Dictionary<string, string>());

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

    private static (TerminalSessionRegistry Registry, TerminalSession Session, ScriptedTerminalConnection Pty, TerminalCollabSession Bridge) StartDirectBridge(
        string itemId,
        Action<ScriptedTerminalConnection>? configureFirst = null)
    {
        var registry = new TerminalSessionRegistry();
        var factory = new ScriptedTerminalFactory { ConfigureNext = configureFirst };
        var session = registry.GetOrStart(
            itemId,
            DummySpec(),
            factory,
            new TerminalSize(80, 24, 8, 16),
            TerminalPalettes.Dark,
            ColorScheme.Dark,
            scrollbackLines: 1000,
            out _);
        var bridge = new TerminalCollabSession(itemId, "claude", "CLI Worker", "/tmp", registry, activity: null);
        return (registry, session, factory.Connections.Single(), bridge);
    }

    [AvaloniaFact, FreshApp]
    public async Task Cancelled_bracketed_paste_before_enter_does_not_consume_next_terminal_completion()
    {
        var ct = TestContext.Current.CancellationToken;
        var pasteAReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (registry, session, pty, bridge) = StartDirectBridge("term-bp-cancel");
        try
        {
            pty.EmitOutput("\x1b[?2004h");
            await WaitUntilAsync(() => session.CachedInputModes.BracketedPaste, ct);

            pty.OnWrite = text =>
            {
                if (text.Contains("Job A", StringComparison.Ordinal))
                {
                    pasteAReceived.TrySetResult();
                }
                return ValueTask.CompletedTask;
            };

            using var ctsA = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var deliverA = bridge.DeliverCollabPromptAsync("Job A", "t-a", "task-a", expectReply: true, ctsA.Token);

            // Intermediate write acceptance: paste arrived, now cancel before Enter.
            await pasteAReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            ctsA.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deliverA);
            Assert.DoesNotContain("\r", pty.Written);

            // Because Job A's bracketed paste landed without Enter, the link stays fenced until the user
            // explicitly confirms the terminal input is clean (or starts a fresh generation).
            var recovery = session.GetPendingInputRecovery();
            Assert.NotNull(recovery);
            Assert.True(recovery!.HasUnresolvedCancelledPaste);
            Assert.False(recovery.HasHeldInput);
            Assert.False(await bridge.DeliverCollabPromptAsync("Job B", "t-b", "task-b", expectReply: true, ct));
            Assert.DoesNotContain("Job B", string.Concat(pty.Written), StringComparison.Ordinal);

            // After explicit confirmation that the child editor is clean, handoff to B succeeds and sends paste + Enter.
            Assert.True(session.MarkInputClean());
            Assert.Null(session.GetPendingInputRecovery());
            var acceptedB = await bridge.DeliverCollabPromptAsync("Job B", "t-b", "task-b", expectReply: true, ct);
            Assert.True(acceptedB);
            await WaitUntilAsync(() => pty.Written.Any(w => w.Contains("Job B", StringComparison.Ordinal)) && pty.Written.Contains("\r"), ct);

            pty.EmitOutput("Answer for B\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("Answer for B", StringComparison.Ordinal), ct);

            var answer = bridge.TakeAnswer(3500);
            Assert.NotNull(answer);
            Assert.Equal("task-b", answer!.Value.TaskId);
            Assert.Equal("t-b", answer.Value.ThreadId);
            Assert.Contains("Answer for B", answer.Value.Text, StringComparison.Ordinal);
            Assert.Null(bridge.TakeAnswer(3500));
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Rejected_write_and_exited_pty_between_paste_and_enter_leave_no_stale_reservation()
    {
        var ct = TestContext.Current.CancellationToken;

        // Case 1: non-bracketed write rejected by PTY connection failure.
        var (registry1, _, pty1, bridge1) = StartDirectBridge("term-reject-write");
        try
        {
            pty1.OnWrite = _ => ValueTask.FromException(new IOException("broken pipe"));
            var accepted = await bridge1.DeliverCollabPromptAsync("Job A", "t-a", "task-a", expectReply: true, ct);
            Assert.False(accepted);
            Assert.Null(bridge1.TakeAnswer(3500));
        }
        finally
        {
            await registry1.CloseAllAsync();
        }

        // Case 2: bracketed paste accepted, then PTY exits before Enter; after restart, B gets the next Done.
        var registry2 = new TerminalSessionRegistry();
        var factory2 = new ScriptedTerminalFactory();
        var session2 = registry2.GetOrStart(
            "term-exit-mid-paste",
            DummySpec(),
            factory2,
            new TerminalSize(80, 24, 8, 16),
            TerminalPalettes.Dark,
            ColorScheme.Dark,
            scrollbackLines: 1000,
            out _);
        var bridge2 = new TerminalCollabSession("term-exit-mid-paste", "claude", "CLI Worker", "/tmp", registry2, activity: null);
        try
        {
            var firstPty = factory2.Connections.Single();
            firstPty.EmitOutput("\x1b[?2004h");
            await WaitUntilAsync(() => session2.CachedInputModes.BracketedPaste, ct);

            firstPty.OnWrite = text =>
            {
                if (text.Contains("Job A", StringComparison.Ordinal))
                {
                    firstPty.Exit(1);
                }
                return ValueTask.CompletedTask;
            };

            var acceptedA = await bridge2.DeliverCollabPromptAsync("Job A", "t-a", "task-a", expectReply: true, ct);
            Assert.False(acceptedA);
            Assert.DoesNotContain("\r", firstPty.Written);

            await session2.RestartAsync();
            var secondPty = factory2.Connections.Last();
            var acceptedB = await bridge2.DeliverCollabPromptAsync("Job B", "t-b", "task-b", expectReply: true, ct);
            Assert.True(acceptedB);
            await WaitUntilAsync(() => secondPty.Written.Any(w => w.Contains("Job B", StringComparison.Ordinal)), ct);

            secondPty.EmitOutput("Recovered B\r\n");
            await WaitUntilAsync(() => session2.ReadText().Contains("Recovered B", StringComparison.Ordinal), ct);

            var answer = bridge2.TakeAnswer(3500);
            Assert.NotNull(answer);
            Assert.Equal("task-b", answer!.Value.TaskId);
            Assert.Equal("t-b", answer.Value.ThreadId);
            Assert.Contains("Recovered B", answer.Value.Text, StringComparison.Ordinal);
            Assert.Null(bridge2.TakeAnswer(3500));
        }
        finally
        {
            await registry2.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Process_restart_drops_prior_generation_terminal_reservations()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = new TerminalSessionRegistry();
        var factory = new ScriptedTerminalFactory();
        var session = registry.GetOrStart(
            "term-restart-gen",
            DummySpec(),
            factory,
            new TerminalSize(80, 24, 8, 16),
            TerminalPalettes.Dark,
            ColorScheme.Dark,
            scrollbackLines: 1000,
            out _);
        var bridge = new TerminalCollabSession("term-restart-gen", "claude", "CLI Worker", "/tmp", registry, activity: null);
        try
        {
            Assert.True(await bridge.DeliverCollabPromptAsync("Job A", "t-a", "task-a", expectReply: true, ct));

            factory.Connections.Single().Exit(1);
            await session.RestartAsync();

            // Prior-generation reservation must not be consumed after restart.
            Assert.Null(bridge.TakeAnswer(3500));

            Assert.True(await bridge.DeliverCollabPromptAsync("Job B", "t-b", "task-b", expectReply: true, ct));
            factory.Connections.Last().EmitOutput("Fresh process B\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("Fresh process B", StringComparison.Ordinal), ct);

            var answer = bridge.TakeAnswer(3500);
            Assert.NotNull(answer);
            Assert.Equal("task-b", answer!.Value.TaskId);
            Assert.Equal("t-b", answer.Value.ThreadId);
            Assert.Contains("Fresh process B", answer.Value.Text, StringComparison.Ordinal);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Fresh_cli_initial_dispatch_maps_first_done_and_preserves_later_queued_task()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-term-initial-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Lead", Surface = AgentSurface.Gui };
        projects.AddItem(project, parent);

        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        coordinator.RegisterGuiSession(new StubParentBridge(parent, dir));

        var tabs = AppHost.Get<ITabContentRegistry>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        var signals = AppHost.Get<ISignalHub>();
        var registry = AppHost.Get<TerminalSessionRegistry>();

        try
        {
            // Part 1: Fresh spawned CLI settles its initial delegated job on its first Done signal.
            var res1 = await coordinator.CallToolAsync(
                parent.Id,
                "delegate_task",
                new JsonObject
                {
                    ["target"] = "claude",
                    ["goal"] = "Initial CLI job A",
                    ["surface"] = "terminal",
                },
                ct);
            Assert.Null(res1["isError"]);
            var body1 = Body(res1);
            var taskA = body1["task_id"]!.GetValue<string>();
            var childId1 = body1["session_id"]!.GetValue<string>();
            var childItem1 = projects.FindItem(childId1)!;
            Assert.Equal(AgentSurface.Terminal, childItem1.Surface);

            // Host the real TerminalTabView in a headless window so EnsureStarted/TryStartSession launches the fake PTY.
            var tab1 = tabs.GetOrCreate(project, childItem1);
            var hostWindow1 = new Window { Width = 800, Height = 600, Content = tab1 };
            hostWindow1.Show();
            try
            {
                await WaitUntilAsync(() => registry.Find(childId1) is { IsRunning: true }, ct);
                Assert.Contains(factory.Started, s => s.Arguments.Any(a => a.Contains("Initial CLI job A", StringComparison.Ordinal)));

                signals.Publish(new AgentSignal("claude", "stop", childId1, "", 0, 0, "{}"));
                await WaitUntilAsync(() => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status == CollabTaskStatus.Completed, ct);
            }
            finally
            {
                hostWindow1.Close();
                Frames.Pump();
            }

            // Part 2: Fresh spawned CLI with a second job B queued before initial Done settles A first, then B.
            var res2A = await coordinator.CallToolAsync(
                parent.Id,
                "delegate_task",
                new JsonObject
                {
                    ["target"] = "claude",
                    ["goal"] = "Initial CLI job A2",
                    ["surface"] = "terminal",
                },
                ct);
            Assert.Null(res2A["isError"]);
            var body2A = Body(res2A);
            var taskA2 = body2A["task_id"]!.GetValue<string>();
            var childId2 = body2A["session_id"]!.GetValue<string>();
            var childItem2 = projects.FindItem(childId2)!;

            var tab2 = tabs.GetOrCreate(project, childItem2);
            var hostWindow2 = new Window { Width = 800, Height = 600, Content = tab2 };
            hostWindow2.Show();
            try
            {
                await WaitUntilAsync(() => registry.Find(childId2) is { IsRunning: true }, ct);

                var res2B = await coordinator.CallToolAsync(
                    parent.Id,
                    "delegate_task",
                    new JsonObject
                    {
                        ["target"] = childId2,
                        ["goal"] = "Follow-up CLI job B2",
                    },
                    ct);
                Assert.Null(res2B["isError"]);
                var taskB2 = Body(res2B)["task_id"]!.GetValue<string>();

                // First Done belongs to initial launch A2 and must not consume queued B2.
                signals.Publish(new AgentSignal("claude", "stop", childId2, "", 0, 0, "{}"));
                await WaitUntilAsync(() => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA2).Status == CollabTaskStatus.Completed, ct);
                Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB2).Status);

                // Second Done settles queued B2.
                signals.Publish(new AgentSignal("claude", "stop", childId2, "", 0, 0, "{}"));
                await WaitUntilAsync(() => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB2).Status == CollabTaskStatus.Completed, ct);
            }
            finally
            {
                hostWindow2.Close();
                Frames.Pump();
            }
        }
        finally
        {
            await registry.CloseAllAsync();
            Frames.Pump();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Disposing_terminal_collab_session_aborts_trailing_bracketed_paste_and_clears_reservations()
    {
        var ct = TestContext.Current.CancellationToken;
        var pasteReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (registry, session, pty, bridge) = StartDirectBridge("term-dispose-paste");
        try
        {
            pty.EmitOutput("\x1b[?2004h");
            await WaitUntilAsync(() => session.CachedInputModes.BracketedPaste, ct);

            pty.OnWrite = text =>
            {
                if (text.Contains("Trailing Job", StringComparison.Ordinal))
                {
                    pasteReceived.TrySetResult();
                }
                return ValueTask.CompletedTask;
            };

            var deliver = bridge.DeliverCollabPromptAsync("Trailing Job", "t-d", "task-d", expectReply: true, ct);
            await pasteReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            var disposable = Assert.IsAssignableFrom<IDisposable>(bridge);
            disposable.Dispose();

            var accepted = await deliver.WaitAsync(TimeSpan.FromSeconds(2), ct);
            Assert.False(accepted);

            // Wait past the 60ms bracketed-paste delay to ensure no trailing Enter is sent.
            await Task.Delay(120, ct);
            Assert.DoesNotContain("\r", pty.Written);
            Assert.Null(bridge.TakeAnswer(3500));
            Assert.False(await bridge.DeliverCollabPromptAsync("After Dispose", "t-after", "task-after", expectReply: true, ct));
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Terminal_reservations_stay_bounded_when_done_never_fires()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, _, _, bridge) = StartDirectBridge("term-bounded-fifo");
        try
        {
            // Submit 80 fire-and-forget prompts followed by 20 tracked prompts without ever emitting Done.
            for (var i = 0; i < 80; i++)
            {
                Assert.True(await bridge.DeliverCollabPromptAsync($"fyi-{i}", $"t-fyi-{i}", taskId: null, expectReply: false, ct));
            }
            for (var i = 0; i < 20; i++)
            {
                Assert.True(await bridge.DeliverCollabPromptAsync($"job-{i}", $"t-{i}", $"task-{i}", expectReply: true, ct));
            }

            var drained = new List<TerminalTurnAnswer>();
            var emptyCount = 0;
            for (var i = 0; i < 120; i++)
            {
                if (bridge.TakeAnswer(3500) is { } answer)
                {
                    drained.Add(answer);
                }
                else
                {
                    emptyCount++;
                }
            }

            // Total retained queue entries must be capped (<= 64), so fewer than 64 fire-and-forget slots remain before the 20 tracked jobs.
            Assert.Equal(20, drained.Count);
            Assert.Equal("task-0", drained[0].TaskId);
            Assert.Equal("task-19", drained[^1].TaskId);

            // Verify with 100 tracked prompts that total retained entries never exceed 64, evicted completions are
            // discarded instead of shifting onto newer tasks, and the newest 64 entries align ordinally.
            for (var i = 0; i < 100; i++)
            {
                Assert.True(await bridge.DeliverCollabPromptAsync($"tracked-{i}", $"t-tr-{i}", $"task-tr-{i}", expectReply: true, ct));
            }

            var evictedNulls = 0;
            var retained = new List<TerminalTurnAnswer>();
            for (var i = 0; i < 100; i++)
            {
                if (bridge.TakeAnswer(3500) is { } next)
                {
                    retained.Add(next);
                }
                else
                {
                    evictedNulls++;
                }
            }

            Assert.Equal(36, evictedNulls);
            Assert.Equal(64, retained.Count);
            Assert.Equal("task-tr-36", retained[0].TaskId);
            Assert.Equal("task-tr-99", retained[^1].TaskId);
            Assert.Null(bridge.TakeAnswer(3500));
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Targeted_cancel_active_turn_ignores_non_head_terminal_task_and_interrupts_head()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, _, pty, bridge) = StartDirectBridge("term-targeted-cancel");
        try
        {
            Assert.True(await bridge.DeliverCollabPromptAsync("Job A", "t-a", "task-a", expectReply: true, ct));
            Assert.True(await bridge.DeliverCollabPromptAsync("Job B", "t-b", "task-b", expectReply: true, ct));
            await WaitUntilAsync(() => pty.Written.Any(w => w.Contains("Job B", StringComparison.Ordinal)), ct);

            ICollabSessionBridge iface = bridge;
            await iface.CancelActiveTurnAsync("t-b", "task-b");
            await Task.Delay(50, ct);
            Assert.DoesNotContain("\x1b", pty.Written);

            await iface.CancelActiveTurnAsync("t-a", "task-a");
            await WaitUntilAsync(() => pty.Written.Contains("\x1b"), ct);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Terminal_closed_between_bracketed_paste_and_submit_does_not_confirm_wakeup_or_keep_reservation()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-term-close-submit-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var cli = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "CLI", Surface = AgentSurface.Terminal };
        var worker = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Worker", Surface = AgentSurface.Gui };
        projects.AddItem(project, cli, activate: false);
        projects.AddItem(project, worker, activate: false);

        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        coordinator.RegisterGuiSession(new StubParentBridge(worker, dir));

        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new ScriptedTerminalFactory();
        var pasteReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var term = registry.GetOrStart(
            cli.Id,
            AppHost.Get<IItemLaunchService>().Plan(project, cli),
            factory,
            new TerminalSize(80, 24, 8, 16),
            TerminalPalettes.Dark,
            ColorScheme.Dark,
            scrollbackLines: 1000,
            out _);
        try
        {
            var pty = factory.Connections.Single();
            pty.EmitOutput("\x1b[?2004h");
            await WaitUntilAsync(() => term.CachedInputModes.BracketedPaste, ct);
            pty.OnWrite = text =>
            {
                if (text.Contains("return to terminal", StringComparison.Ordinal))
                {
                    pasteReceived.TrySetResult();
                }
                return ValueTask.CompletedTask;
            };

            var sent = Body(await coordinator.CallToolAsync(
                cli.Id,
                "delegate_task",
                new JsonObject { ["target"] = worker.Id, ["goal"] = "report" },
                ct));
            var threadId = sent["thread_id"]!.GetValue<string>();

            var replyTask = coordinator.CallToolAsync(
                worker.Id,
                "reply_thread",
                new JsonObject { ["thread_id"] = threadId, ["message"] = "return to terminal", ["status"] = "agreed" },
                ct);

            await pasteReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await registry.CloseAsync(cli.Id);

            var receipt = Body(await replyTask);
            Assert.Contains(pty.Written, w => w.Contains("return to terminal", StringComparison.Ordinal));
            Assert.DoesNotContain("\r", pty.Written);
            Assert.Null(receipt["delivered_to"]);
            Assert.Equal("failed", receipt["delivery"]!.GetValue<string>());
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Terminal_respun_between_bracketed_paste_and_submit_does_not_confirm_wakeup_or_enter_new_link()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-term-respin-submit-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var cli = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "CLI", Surface = AgentSurface.Terminal };
        var worker = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Worker", Surface = AgentSurface.Gui };
        projects.AddItem(project, cli, activate: false);
        projects.AddItem(project, worker, activate: false);

        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        coordinator.RegisterGuiSession(new StubParentBridge(worker, dir));

        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new ScriptedTerminalFactory();
        var spec = AppHost.Get<IItemLaunchService>().Plan(project, cli);
        var pasteReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSession = registry.GetOrStart(
            cli.Id,
            spec,
            factory,
            new TerminalSize(80, 24, 8, 16),
            TerminalPalettes.Dark,
            ColorScheme.Dark,
            scrollbackLines: 1000,
            out _);
        try
        {
            var firstPty = factory.Connections.Single();
            firstPty.EmitOutput("\x1b[?2004h");
            await WaitUntilAsync(() => firstSession.CachedInputModes.BracketedPaste, ct);
            firstPty.OnWrite = text =>
            {
                if (text.Contains("wakeup before respin", StringComparison.Ordinal))
                {
                    pasteReceived.TrySetResult();
                }
                return ValueTask.CompletedTask;
            };

            var sent = Body(await coordinator.CallToolAsync(
                cli.Id,
                "delegate_task",
                new JsonObject { ["target"] = worker.Id, ["goal"] = "report" },
                ct));
            var threadId = sent["thread_id"]!.GetValue<string>();

            var replyTask = coordinator.CallToolAsync(
                worker.Id,
                "reply_thread",
                new JsonObject { ["thread_id"] = threadId, ["message"] = "wakeup before respin", ["status"] = "agreed" },
                ct);

            await pasteReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            // Close and immediately respin a brand-new TerminalSession under the same item id before Enter.
            await registry.CloseAsync(cli.Id);
            registry.GetOrStart(
                cli.Id,
                spec,
                factory,
                new TerminalSize(80, 24, 8, 16),
                TerminalPalettes.Dark,
                ColorScheme.Dark,
                scrollbackLines: 1000,
                out _);
            var respunPty = factory.Connections.Last();

            var receipt = Body(await replyTask);
            Assert.Contains(firstPty.Written, w => w.Contains("wakeup before respin", StringComparison.Ordinal));
            Assert.DoesNotContain("\r", firstPty.Written);
            Assert.Empty(respunPty.Written);
            Assert.Null(receipt["delivered_to"]);
            Assert.Equal("failed", receipt["delivery"]!.GetValue<string>());
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Closing_terminal_while_submit_write_is_in_flight_does_not_acknowledge_or_retain_reservation()
    {
        var ct = TestContext.Current.CancellationToken;
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (registry, _, pty, bridge) = StartDirectBridge("term-close-inflight-write");
        try
        {
            pty.OnWrite = async text =>
            {
                if (text.Contains("Job A", StringComparison.Ordinal))
                {
                    writeEntered.TrySetResult();
                    await releaseWrite.Task.ConfigureAwait(false);
                }
            };

            var deliverTask = bridge.DeliverCollabPromptAsync("Job A", "t-a", "task-a", expectReply: true, ct);
            await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            // Close the terminal while the submit write is still in flight on the underlying connection.
            var closeTask = registry.CloseAsync("term-close-inflight-write");
            await Task.Delay(30, ct);
            releaseWrite.TrySetResult();
            await closeTask;

            var accepted = await deliverTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.False(accepted);
            Assert.Null(bridge.TakeAnswer(3500));
        }
        finally
        {
            releaseWrite.TrySetResult();
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Late_done_from_replaced_link_cannot_settle_fresh_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("review-latedone-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var cli = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "CLI", Surface = AgentSurface.Terminal };
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Parent", Surface = AgentSurface.Gui };
        projects.AddItem(project, cli);
        projects.AddItem(project, parent);
        var host = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        host.Server.Start();
        host.RegisterGuiSession(new StubParentBridge(parent, dir));
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new ScriptedTerminalFactory();
        var term = registry.GetOrStart(cli.Id, DummySpec(), factory, new TerminalSize(80, 24, 8, 16), TerminalPalettes.Dark, ColorScheme.Dark, 1000, out _);
        async Task<string> Job(string goal) => Body(await host.CallToolAsync(parent.Id, "delegate_task",
            new JsonObject { ["target"] = cli.Id, ["goal"] = goal }, ct))["task_id"]!.GetValue<string>();
        try
        {
            await Job("first");
            var beforeRestart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var lateDone = new AgentSignal("claude", "stop", cli.Id, "", beforeRestart, beforeRestart, "{}");
            await term.RestartAsync();
            var fresh = await Job("second");
            Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(x => x.TaskId == fresh).Status);
            var signals = AppHost.Get<ISignalHub>();
            signals.Publish(lateDone);
            await Task.Delay(250, ct);
            Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(x => x.TaskId == fresh).Status);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Overflow_cannot_give_first_done_to_second_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, session, pty, bridge) = StartDirectBridge("review-overflow");
        try
        {
            for (var i = 0; i < 65; i++)
            {
                Assert.True(await bridge.DeliverCollabPromptAsync($"goal {i}", $"thread-{i}", $"task-{i}", true, ct));
            }
            pty.EmitOutput("answer from job zero\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("answer from job zero", StringComparison.Ordinal), ct);
            var first = bridge.TakeAnswer(3500);
            Assert.True(first is null || first.Value.TaskId == "task-0", $"First Done routed to {first?.TaskId} with {first?.Text}");

            if (first is null)
            {
                pty.EmitOutput("answer from job one\r\n");
                await WaitUntilAsync(() => session.ReadText().Contains("answer from job one", StringComparison.Ordinal), ct);
                var second = bridge.TakeAnswer(3500);
                Assert.NotNull(second);
                Assert.Equal("task-1", second!.Value.TaskId);
                Assert.Contains("answer from job one", second.Value.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("answer from job zero", second.Value.Text, StringComparison.Ordinal);
            }
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Manual_input_during_bracketed_submit_cannot_be_submitted_by_collab_enter()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, session, pty, bridge) = StartDirectBridge("review-manual");
        try
        {
            pty.EmitOutput("\x1b[?2004h");
            await WaitUntilAsync(() => session.CachedInputModes.BracketedPaste, ct);
            pty.OnWrite = bytes =>
            {
                if (bytes.Contains("agent goal", StringComparison.Ordinal))
                {
                    session.Send("manual draft");
                }
                return ValueTask.CompletedTask;
            };
            var accepted = await bridge.DeliverCollabPromptAsync("agent goal", "t", "j", true, ct);
            Assert.False(accepted && pty.Written.ToArray() is [_, "manual draft", "\r"], "Wire received collab paste, keyboard draft, collab Enter; delivery reported success.");

            // User draft must remain preserved (not dropped or cleared) and never precede collab Enter.
            await WaitUntilAsync(() => pty.Written.Contains("manual draft"), ct);
            var written = pty.Written.ToArray();
            if (accepted)
            {
                var enterIndex = Array.IndexOf(written, "\r");
                var draftIndex = Array.IndexOf(written, "manual draft");
                Assert.True(enterIndex >= 0 && draftIndex > enterIndex, $"Expected collab Enter before preserved manual draft; got [{string.Join(", ", written)}]");
            }
            Assert.DoesNotContain("\x15", written);
            Assert.DoesNotContain("\x03", written);

            // While the user's unsubmitted draft remains in the prompt, a follow-up collab delivery must not submit or clear it.
            Assert.False(await bridge.DeliverCollabPromptAsync("second goal", "t2", "j2", true, ct));
            Assert.DoesNotContain("second goal", string.Concat(pty.Written), StringComparison.Ordinal);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Cancellation_during_last_enter_preserves_task_owning_its_done()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, session, pty, bridge) = StartDirectBridge("review-last-enter");
        var inWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            pty.EmitOutput("\x1b[?2004h");
            await WaitUntilAsync(() => session.CachedInputModes.BracketedPaste, ct);
            pty.OnWrite = async bytes =>
            {
                if (bytes == "\r")
                {
                    inWrite.TrySetResult();
                    await release.Task;
                }
            };
            var delivery = bridge.DeliverCollabPromptAsync("A", "ta", "A", true, cancel.Token);
            await inWrite.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            cancel.Cancel();
            release.TrySetResult();
            try { await delivery; } catch (OperationCanceledException) { }
            await WaitUntilAsync(() => pty.Written.Contains("\r"), ct);
            Assert.True(await bridge.DeliverCollabPromptAsync("B", "tb", "B", true, ct));
            var a = bridge.TakeAnswer(3500);
            Assert.Equal("A", a?.TaskId);
            var b = bridge.TakeAnswer(3500);
            Assert.Equal("B", b?.TaskId);
        }
        finally
        {
            release.TrySetResult();
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Manual_turn_done_cannot_settle_following_collab_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, session, pty, bridge) = StartDirectBridge("review-manual-turn");
        try
        {
            session.Send("interactive question\r");
            await WaitUntilAsync(() => pty.Written.Contains("interactive question\r"), ct);
            Assert.True(await bridge.DeliverCollabPromptAsync("queued goal", "t", "job", true, ct));

            // Targeted cancel for the queued collab job must not interrupt the running manual turn at the head.
            ICollabSessionBridge iface = bridge;
            await iface.CancelActiveTurnAsync("t", "job");
            await Task.Delay(50, ct);
            Assert.DoesNotContain("\x1b", pty.Written);

            pty.EmitOutput("interactive answer\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("interactive answer", StringComparison.Ordinal), ct);
            var manualDone = bridge.TakeAnswer(3500);
            Assert.True(manualDone is null, $"Manual Done assigned to {manualDone?.TaskId} with {manualDone?.Text}");

            pty.EmitOutput("collab answer\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("collab answer", StringComparison.Ordinal), ct);
            var collabDone = bridge.TakeAnswer(3500);
            Assert.NotNull(collabDone);
            Assert.Equal("job", collabDone!.Value.TaskId);
            Assert.Contains("collab answer", collabDone.Value.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("interactive answer", collabDone.Value.Text, StringComparison.Ordinal);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Identified_done_for_new_link_settles_fresh_job_while_stale_identified_done_is_ignored()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("review-identified-link-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var cli = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "CLI", Surface = AgentSurface.Terminal };
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Parent", Surface = AgentSurface.Gui };
        projects.AddItem(project, cli);
        projects.AddItem(project, parent);
        var host = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        host.Server.Start();
        host.RegisterGuiSession(new StubParentBridge(parent, dir));
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new ScriptedTerminalFactory();
        var term = registry.GetOrStart(cli.Id, DummySpec(), factory, new TerminalSize(80, 24, 8, 16), TerminalPalettes.Dark, ColorScheme.Dark, 1000, out _);
        async Task<string> Job(string goal) => Body(await host.CallToolAsync(parent.Id, "delegate_task",
            new JsonObject { ["target"] = cli.Id, ["goal"] = goal }, ct))["task_id"]!.GetValue<string>();
        try
        {
            var oldLinkId = term.CurrentLinkId;
            Assert.False(string.IsNullOrWhiteSpace(oldLinkId));
            await Job("first");
            await term.RestartAsync();
            var newLinkId = term.CurrentLinkId;
            Assert.False(string.IsNullOrWhiteSpace(newLinkId));
            Assert.NotEqual(oldLinkId, newLinkId);

            var fresh = await Job("second");
            var signals = AppHost.Get<ISignalHub>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Stale identified Done from the old link must be ignored.
            signals.Publish(new AgentSignal("claude", "stop", cli.Id, "", now, now, "{}", oldLinkId));
            await Task.Delay(150, ct);
            Assert.Equal(CollabTaskStatus.Running, host.GetTasksForItem(parent.Id).Single(x => x.TaskId == fresh).Status);

            // Identified Done from the new link settles the fresh job.
            factory.Connections.Last().EmitOutput("fresh link answer\r\n");
            await WaitUntilAsync(() => term.ReadText().Contains("fresh link answer", StringComparison.Ordinal), ct);
            signals.Publish(new AgentSignal("claude", "stop", cli.Id, "", now, now, "{}", newLinkId));
            await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(x => x.TaskId == fresh).Status == CollabTaskStatus.Completed, ct);
            Assert.Contains("fresh link answer", host.GetTasksForItem(parent.Id).Single(x => x.TaskId == fresh).ResultSummary ?? "", StringComparison.Ordinal);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Manual_turn_completed_before_collab_attaches_does_not_swallow_later_collab_done()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("review-manual-pre-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var cli = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "CLI", Surface = AgentSurface.Terminal };
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Parent", Surface = AgentSurface.Gui };
        projects.AddItem(project, cli);
        projects.AddItem(project, parent);
        var host = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        host.Server.Start();
        host.RegisterGuiSession(new StubParentBridge(parent, dir));
        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new ScriptedTerminalFactory();
        var term = registry.GetOrStart(cli.Id, DummySpec(), factory, new TerminalSize(80, 24, 8, 16), TerminalPalettes.Dark, ColorScheme.Dark, 1000, out _);
        var signals = AppHost.Get<ISignalHub>();
        try
        {
            // User runs and finishes a manual turn before any collab bridge attaches.
            term.Send("user question\r");
            await WaitUntilAsync(() => factory.Connections.Single().Written.Contains("user question\r"), ct);
            signals.Publish(new AgentSignal("claude", "stop", cli.Id, "", 0, 0, "{}", term.CurrentLinkId));

            var taskId = Body(await host.CallToolAsync(parent.Id, "delegate_task",
                new JsonObject { ["target"] = cli.Id, ["goal"] = "collab after manual" }, ct))["task_id"]!.GetValue<string>();
            factory.Connections.Single().EmitOutput("collab result\r\n");
            await WaitUntilAsync(() => term.ReadText().Contains("collab result", StringComparison.Ordinal), ct);
            signals.Publish(new AgentSignal("claude", "stop", cli.Id, "", 0, 0, "{}", term.CurrentLinkId));
            await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(x => x.TaskId == taskId).Status == CollabTaskStatus.Completed, ct);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    private static TerminalInputRouter CreateFrontEndRouter(
        TerminalSession session,
        Func<Task<string?>>? getClipboardText = null) =>
        new(
            session,
            () => new MacTerminalKeymap(),
            getClipboardText ?? (() => Task.FromResult<string?>(null)),
            () => Task.FromResult<string?>(null),
            _ => Task.CompletedTask,
            (_, _) => Task.FromResult(true),
            () => { });

    [AvaloniaFact, FreshApp]
    public async Task Empty_enter_from_input_router_does_not_claim_following_collab_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, session, pty, bridge) = StartDirectBridge("front-empty-enter");
        using var router = CreateFrontEndRouter(session);
        try
        {
            router.SendKey(TerminalKey.Enter);
            await WaitUntilAsync(() => pty.Written.Contains("\r"), ct);
            Assert.True(await bridge.DeliverCollabPromptAsync("hello", "t", "j", true, ct));
            pty.EmitOutput("hello answer\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("hello answer", StringComparison.Ordinal), ct);
            Assert.Equal("j", bridge.TakeAnswer(3500)?.TaskId);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Single_character_draft_erased_with_backspace_unblocks_collab_delivery()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, session, pty, bridge) = StartDirectBridge("front-backspace-erase");
        using var router = CreateFrontEndRouter(session);
        try
        {
            router.HandleText("x");
            router.SendKey(TerminalKey.Backspace);
            await WaitUntilAsync(() => pty.Written.Count == 2, ct);
            Assert.Equal("x\x7f", string.Concat(pty.Written));
            Assert.True(await bridge.DeliverCollabPromptAsync("hello", "t", "j", true, ct), "Fully erased one-character draft still blocks delivery");
            pty.EmitOutput("hello answer\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("hello answer", StringComparison.Ordinal), ct);
            Assert.Equal("j", bridge.TakeAnswer(3500)?.TaskId);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Kitty_printable_draft_is_protected_and_unblocks_when_erased_with_backspace()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, session, pty, bridge) = StartDirectBridge("front-kitty-draft");
        using var router = CreateFrontEndRouter(session);
        try
        {
            pty.EmitOutput("\x1b[>31u");
            await WaitUntilAsync(() => session.CachedInputModes.KittyFlags != 0, ct);
            router.HandleKey(new TKCode.Terminal.Avalonia.TerminalKeyEventArgs(
                new KeyInput(TerminalKey.X, KeyAction.Press, KeyMods.None),
                global::Avalonia.Input.KeyModifiers.None));
            await WaitUntilAsync(() => pty.Written.Count == 1, ct);
            Assert.StartsWith("\x1b[120", pty.Written.Single());

            // Key release in Kitty ReportEvents mode must not double-count the character.
            router.HandleKey(new TKCode.Terminal.Avalonia.TerminalKeyEventArgs(
                new KeyInput(TerminalKey.X, KeyAction.Release, KeyMods.None),
                global::Avalonia.Input.KeyModifiers.None));
            await WaitUntilAsync(() => pty.Written.Count == 2, ct);

            var acceptedWhileDraft = await bridge.DeliverCollabPromptAsync("hello", "t", "j", true, ct);
            Assert.False(acceptedWhileDraft, "Collaboration appended to extended-keyboard draft: " + string.Concat(pty.Written).Replace("\x1b", "ESC").Replace("\r", "<CR>"));
            Assert.DoesNotContain("hello", string.Concat(pty.Written), StringComparison.Ordinal);

            // Erasing the single Kitty character with Backspace clears the draft and unblocks collaboration.
            router.SendKey(TerminalKey.Backspace);
            await WaitUntilAsync(() => pty.Written.Count == 3, ct);
            Assert.True(await bridge.DeliverCollabPromptAsync("hello", "t", "j", true, ct));
            pty.EmitOutput("kitty ok\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("kitty ok", StringComparison.Ordinal), ct);
            Assert.Equal("j", bridge.TakeAnswer(3500)?.TaskId);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Unobservable_cursor_edit_on_nonempty_draft_stays_protected_until_cleared_or_submitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, session, pty, bridge) = StartDirectBridge("front-unobservable-edit");
        using var router = CreateFrontEndRouter(session);
        try
        {
            // Type "x", move cursor left, press Backspace (at start of line in a real shell/TUI, 'x' is NOT deleted).
            router.HandleText("x");
            router.SendKey(TerminalKey.ArrowLeft);
            router.SendKey(TerminalKey.Backspace);
            await WaitUntilAsync(() => pty.Written.Count == 3, ct);

            // Must stay conservatively protected rather than assuming Backspace emptied the buffer.
            Assert.False(await bridge.DeliverCollabPromptAsync("hello", "t", "j", true, ct));
            Assert.DoesNotContain("hello", string.Concat(pty.Written), StringComparison.Ordinal);

            // Explicit line kill-to-start (Ctrl+U) after cursor motion can leave a suffix, so draft stays protected;
            // explicit line cancel (Ctrl+C) clears the prompt line and allows delivery.
            router.SendKey(TerminalKey.U, KeyMods.Ctrl);
            await WaitUntilAsync(() => pty.Written.Count == 4, ct);
            Assert.False(await bridge.DeliverCollabPromptAsync("hello", "t", "j", true, ct));
            Assert.DoesNotContain("hello", string.Concat(pty.Written), StringComparison.Ordinal);

            router.SendKey(TerminalKey.C, KeyMods.Ctrl);
            await WaitUntilAsync(() => pty.Written.Count == 5, ct);
            Assert.True(await bridge.DeliverCollabPromptAsync("hello", "t", "j", true, ct));
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Normal_clipboard_paste_with_trailing_lf_submits_and_allows_following_collab_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, session, pty, bridge) = StartDirectBridge("front-paste-lf");
        using var router = CreateFrontEndRouter(session, () => Task.FromResult<string?>("manual\n"));
        try
        {
            await router.PasteAsync();
            await WaitUntilAsync(() => pty.Written.Count == 1, ct);
            Assert.Equal("manual\r", pty.Written.Single());

            // Non-bracketed paste flattened LF to CR, submitting the manual turn; collab delivery must succeed
            // and the manual turn must own the first Done while collab owns the second.
            Assert.True(await bridge.DeliverCollabPromptAsync("hello", "t", "j", true, ct));
            pty.EmitOutput("manual answer\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("manual answer", StringComparison.Ordinal), ct);
            Assert.Null(bridge.TakeAnswer(3500));

            pty.EmitOutput("collab answer\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("collab answer", StringComparison.Ordinal), ct);
            var collab = bridge.TakeAnswer(3500);
            Assert.NotNull(collab);
            Assert.Equal("j", collab!.Value.TaskId);
            Assert.Contains("collab answer", collab.Value.Text, StringComparison.Ordinal);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Tab_from_empty_editor_protects_draft_from_collab_delivery()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, session, pty, bridge) = StartDirectBridge("front-tab-empty");
        using var router = CreateFrontEndRouter(session);
        try
        {
            router.SendKey(TerminalKey.Tab);
            await WaitUntilAsync(() => pty.Written.Count == 1, ct);
            Assert.Equal("\t", pty.Written.Single());

            Assert.False(await bridge.DeliverCollabPromptAsync("hello", "t", "j", true, ct));
            Assert.DoesNotContain("hello", string.Concat(pty.Written), StringComparison.Ordinal);

            // Clearing or submitting resolves the unobservable Tab edit.
            router.SendKey(TerminalKey.C, KeyMods.Ctrl);
            await WaitUntilAsync(() => pty.Written.Count == 2, ct);
            Assert.True(await bridge.DeliverCollabPromptAsync("hello", "t", "j", true, ct));
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Raw_kitty_modifier_and_functional_keys_do_not_block_collab_delivery()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, session, pty, bridge) = StartDirectBridge("raw-kitty-functional");
        try
        {
            pty.EmitOutput("\x1b[>31u");
            await WaitUntilAsync(() => session.CachedInputModes.KittyFlags != 0, ct);

            // Simulate raw PTY bytes (such as HostWorkspace.Input) carrying Kitty Left Shift press/release and F13.
            using var encoder = new InputEncoder { OptionAsAlt = OptionAsAlt.Both };
            encoder.Apply(session.CachedInputModes);
            var buffer = new byte[64];
            var n = encoder.EncodeKey(new KeyInput(TerminalKey.ShiftLeft, KeyAction.Press, KeyMods.Shift), buffer);
            Assert.True(n > 0);
            session.Send(buffer.AsSpan(0, n));
            session.Send("\x1b[57441;1:3u\x1b[57376u"u8);
            await WaitUntilAsync(() => pty.Written.Count == 2, ct);

            Assert.True(await bridge.DeliverCollabPromptAsync("hello", "t", "j", true, ct));
            pty.EmitOutput("kitty raw ok\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("kitty raw ok", StringComparison.Ordinal), ct);
            Assert.Equal("j", bridge.TakeAnswer(3500)?.TaskId);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Raw_utf8_character_erased_with_backspace_unblocks_collab_delivery()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, session, pty, bridge) = StartDirectBridge("raw-utf8-backspace");
        try
        {
            session.Send("界"u8);
            session.Send("\x7f"u8);
            await WaitUntilAsync(() => pty.Written.Count == 2, ct);

            Assert.True(await bridge.DeliverCollabPromptAsync("hello", "t", "j", true, ct));
            pty.EmitOutput("utf8 ok\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("utf8 ok", StringComparison.Ordinal), ct);
            Assert.Equal("j", bridge.TakeAnswer(3500)?.TaskId);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Cursor_move_then_kill_to_start_keeps_draft_protected_while_linear_kill_clears()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, session, pty, bridge) = StartDirectBridge("front-cursor-kill-to-start");
        using var router = CreateFrontEndRouter(session);
        try
        {
            // Linear draft cleared with Cmd+Backspace (kill-to-start at end of line) empties the draft.
            router.HandleText("ab");
            router.SendKey(TerminalKey.Backspace, KeyMods.Super);
            await WaitUntilAsync(() => pty.Written.Count == 2, ct);
            Assert.True(await bridge.DeliverCollabPromptAsync("first", "t1", "j1", true, ct));
            pty.EmitOutput("first ok\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("first ok", StringComparison.Ordinal), ct);
            Assert.Equal("j1", bridge.TakeAnswer(3500)?.TaskId);

            // Moving cursor left before Cmd+Backspace can leave a suffix, so the draft must stay protected.
            var before = pty.Written.Count;
            router.HandleText("ab");
            router.SendKey(TerminalKey.ArrowLeft);
            router.SendKey(TerminalKey.Backspace, KeyMods.Super);
            await WaitUntilAsync(() => pty.Written.Count == before + 3, ct);

            Assert.False(await bridge.DeliverCollabPromptAsync("second", "t2", "j2", true, ct));
            Assert.DoesNotContain("second", string.Concat(pty.Written), StringComparison.Ordinal);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Accepted_delivery_tracking_releases_one_way_and_evicted_completed_deliveries_without_unbounded_growth()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, session, _, bridge) = StartDirectBridge("term-accepted-delivery-bounds");
        try
        {
            // 1. Informational / one-way wakeups (expectReply: false) must never accumulate in retirement tracking.
            for (var batch = 0; batch < 3; batch++)
            {
                for (var i = 0; i < 160; i++)
                {
                    Assert.True(await bridge.DeliverCollabPromptAsync("wake", "thread", taskId: null, expectReply: false, $"wake-{batch}-{i}", shouldCommit: null, ct));
                }
                for (var i = 0; i < 160; i++)
                {
                    Assert.Null(bridge.TakeAnswer(256));
                }
            }
            Assert.Equal(0, bridge.TakeAcceptedDeliveries(session)?.Count ?? 0);

            // 2. Overflowed expectReply: true batches (> MaxPendingTurns) must release evicted deliveries as each Done arrives.
            for (var batch = 0; batch < 3; batch++)
            {
                for (var i = 0; i < 100; i++)
                {
                    Assert.True(await bridge.DeliverCollabPromptAsync($"job-{batch}-{i}", $"t-{batch}-{i}", $"task-{batch}-{i}", expectReply: true, $"del-{batch}-{i}", shouldCommit: null, ct));
                }
                for (var i = 0; i < 100; i++)
                {
                    _ = bridge.TakeAnswer(256);
                }
            }
            Assert.Equal(0, bridge.TakeAcceptedDeliveries(session)?.Count ?? 0);

            // 3. Even when Done never fires, per-session accepted delivery tracking stays bounded.
            for (var i = 0; i < 600; i++)
            {
                Assert.True(await bridge.DeliverCollabPromptAsync($"unanswered-{i}", $"tu-{i}", $"tasku-{i}", expectReply: true, $"unanswered-del-{i}", shouldCommit: null, ct));
            }
            var trackedUnanswered = bridge.TakeAcceptedDeliveries(session);
            Assert.NotNull(trackedUnanswered);
            Assert.True(trackedUnanswered!.Count <= 512, $"Per-session accepted delivery tracking exceeded bound: {trackedUnanswered.Count}");
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }
}
