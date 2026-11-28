using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Threading.Tasks.Sources;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Octopus.Agents.Signals;
using Octopus.App.Collab;
using Octopus.App.Collab.Sessions;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Core.Terminals;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using TKCode.Terminal.Core;
using Xunit;

namespace Octopus.App.Tests;

public sealed class TerminalCancelledPasteRecoveryTests
{
    private sealed class RecordingTerminalConnection : ITerminalConnection
    {
        private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource<TerminalExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 5150;
        public Task<TerminalExit> Exited => _exit.Task;
        public ConcurrentQueue<string> Written { get; } = new();
        public Func<string, ValueTask>? OnWrite { get; set; }
        public Func<string, ValueTask?>? WriteOverride { get; set; }

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

        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
        {
            var text = Encoding.UTF8.GetString(input.Span);
            if (WriteOverride?.Invoke(text) is { } overridden)
            {
                return overridden;
            }
            return WriteCoreAsync(text);
        }

        private async ValueTask WriteCoreAsync(string text)
        {
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

    private sealed class RecordingTerminalFactory : ITerminalConnectionFactory
    {
        public List<RecordingTerminalConnection> Connections { get; } = [];

        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows)
        {
            var connection = new RecordingTerminalConnection();
            Connections.Add(connection);
            return connection;
        }
    }

    private static LaunchSpec DummySpec() => new("claude", [], null, new Dictionary<string, string>());

    private static TerminalInputRouter CreateRouter(TerminalSession session) =>
        new(
            session,
            () => new MacTerminalKeymap(),
            () => Task.FromResult<string?>(null),
            () => Task.FromResult<string?>(null),
            _ => Task.CompletedTask,
            (_, _) => Task.FromResult(true),
            () => { });

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, int maxSteps = 120)
    {
        for (var i = 0; i < maxSteps && !condition(); i++)
        {
            Frames.Pump();
            await Task.Delay(25, ct);
        }
        Assert.True(condition());
    }

    private static (TerminalSessionRegistry Registry, RecordingTerminalFactory Factory, TerminalSession Session, RecordingTerminalConnection Pty, TerminalCollabSession Bridge) StartDirect(string itemId)
    {
        var registry = new TerminalSessionRegistry();
        var factory = new RecordingTerminalFactory();
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
        return (registry, factory, session, factory.Connections.Single(), bridge);
    }

    [AvaloniaFact, FreshApp]
    public async Task Cancelled_bracketed_paste_holds_deferred_manual_input_fences_collab_and_recovers_on_explicit_send()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, _, session, pty, bridge) = StartDirect("recover-send-held");
        using var router = CreateRouter(session);
        var pasted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var recoveryEvents = 0;
        session.PendingInputRecoveryChanged += () => Interlocked.Increment(ref recoveryEvents);
        try
        {
            pty.EmitOutput("\x1b[?2004h");
            await WaitUntilAsync(() => session.CachedInputModes.BracketedPaste, ct);
            pty.OnWrite = bytes =>
            {
                if (bytes.Contains("cancelled", StringComparison.Ordinal))
                {
                    pasted.TrySetResult();
                }
                return ValueTask.CompletedTask;
            };

            var deliver = bridge.DeliverCollabPromptAsync("cancelled", "t-a", "task-a", expectReply: true, cancel.Token);
            await pasted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            router.HandleText("manual");
            router.SendKey(TerminalKey.Enter);
            cancel.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deliver);
            await Task.Delay(120, ct);

            // Only the cancelled paste reached the wire: no automatic Enter, no automatic erase, no held replay.
            Assert.Equal(["\x1b[200~cancelled\x1b[201~"], pty.Written.ToArray());
            Assert.True(Volatile.Read(ref recoveryEvents) >= 1);

            var recovery = session.GetPendingInputRecovery();
            Assert.NotNull(recovery);
            Assert.True(recovery!.HasUnresolvedCancelledPaste);
            Assert.True(recovery.HasHeldInput);
            Assert.Equal("manual\r", recovery.HeldInputText);
            Assert.Equal("manual↵", recovery.HeldInputPreview);
            Assert.Equal(Encoding.UTF8.GetBytes("manual\r"), recovery.HeldInputBytes);

            // Marking clean must refuse while held bytes remain unrecovered (never silently drop user input).
            Assert.False(session.MarkInputClean());

            // Unrelated live keys and waiting must neither auto-replay held input nor unfence collaboration.
            router.SendKey(TerminalKey.Escape);
            router.SendKey(TerminalKey.ArrowUp);
            await WaitUntilAsync(() => pty.Written.Count == 3, ct);
            await Task.Delay(100, ct);
            Assert.DoesNotContain("manual", pty.Written);
            Assert.DoesNotContain("\r", pty.Written);
            Assert.False(await bridge.DeliverCollabPromptAsync("Job B", "t-b", "task-b", expectReply: true, ct));
            Assert.DoesNotContain("Job B", string.Concat(pty.Written), StringComparison.Ordinal);

            // Explicit SendHeldManualInput replays the held batch in exact FIFO order and unfences collaboration.
            Assert.True(session.SendHeldManualInput());
            await WaitUntilAsync(() => pty.Written.Count == 5, ct);
            var afterSend = pty.Written.ToArray();
            Assert.Equal("manual", afterSend[3]);
            Assert.Equal("\r", afterSend[4]);
            Assert.Null(session.GetPendingInputRecovery());

            // The explicitly sent manual turn owns the next Done; Job B owns the Done after that.
            Assert.True(await bridge.DeliverCollabPromptAsync("Job B", "t-b", "task-b", expectReply: true, ct));
            pty.EmitOutput("manual answer\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("manual answer", StringComparison.Ordinal), ct);
            Assert.Null(bridge.TakeAnswer(3500));

            pty.EmitOutput("collab B answer\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("collab B answer", StringComparison.Ordinal), ct);
            var answer = bridge.TakeAnswer(3500);
            Assert.NotNull(answer);
            Assert.Equal("task-b", answer!.Value.TaskId);
            Assert.Contains("collab B answer", answer.Value.Text, StringComparison.Ordinal);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Cancelled_bracketed_paste_discard_held_keeps_fence_until_marked_clean()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, _, session, pty, bridge) = StartDirect("recover-discard-held");
        using var router = CreateRouter(session);
        var pasted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            pty.EmitOutput("\x1b[?2004h");
            await WaitUntilAsync(() => session.CachedInputModes.BracketedPaste, ct);
            pty.OnWrite = bytes =>
            {
                if (bytes.Contains("cancelled", StringComparison.Ordinal)) pasted.TrySetResult();
                return ValueTask.CompletedTask;
            };

            var deliver = bridge.DeliverCollabPromptAsync("cancelled", "t-a", "task-a", expectReply: true, cancel.Token);
            await pasted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            router.HandleText("manual");
            router.SendKey(TerminalKey.Enter);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deliver);

            // Discarding held input drops it without writing to the PTY, while keeping the cancelled-paste fence.
            Assert.True(session.DiscardHeldManualInput());
            var remaining = session.GetPendingInputRecovery();
            Assert.NotNull(remaining);
            Assert.True(remaining!.HasUnresolvedCancelledPaste);
            Assert.False(remaining.HasHeldInput);
            Assert.Empty(remaining.HeldInputBytes);
            Assert.Equal(["\x1b[200~cancelled\x1b[201~"], pty.Written.ToArray());

            Assert.False(await bridge.DeliverCollabPromptAsync("Job B", "t-b", "task-b", expectReply: true, ct));

            // Confirming the terminal input is clean unfences collaboration without sending discarded bytes.
            Assert.True(session.MarkInputClean());
            Assert.Null(session.GetPendingInputRecovery());
            Assert.True(await bridge.DeliverCollabPromptAsync("Job B", "t-b", "task-b", expectReply: true, ct));
            await WaitUntilAsync(() => pty.Written.Any(w => w.Contains("Job B", StringComparison.Ordinal)) && pty.Written.Contains("\r"), ct);
            Assert.DoesNotContain("manual", pty.Written);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Cancelled_bracketed_paste_recovery_clears_automatically_on_clean_generation_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, factory, session, firstPty, bridge) = StartDirect("recover-restart");
        using var router = CreateRouter(session);
        var pasted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            firstPty.EmitOutput("\x1b[?2004h");
            await WaitUntilAsync(() => session.CachedInputModes.BracketedPaste, ct);
            firstPty.OnWrite = bytes =>
            {
                if (bytes.Contains("cancelled", StringComparison.Ordinal)) pasted.TrySetResult();
                return ValueTask.CompletedTask;
            };

            var deliver = bridge.DeliverCollabPromptAsync("cancelled", "t-a", "task-a", expectReply: true, cancel.Token);
            await pasted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            router.HandleText("manual");
            router.SendKey(TerminalKey.Enter);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deliver);
            Assert.NotNull(session.GetPendingInputRecovery());

            // Starting a fresh generation clears old-generation held bytes and fence automatically.
            await session.RestartAsync();
            Assert.Null(session.GetPendingInputRecovery());
            Assert.Equal(["\x1b[200~cancelled\x1b[201~"], firstPty.Written.ToArray());

            var secondPty = factory.Connections.Last();
            Assert.NotSame(firstPty, secondPty);
            Assert.Empty(secondPty.Written);

            Assert.True(await bridge.DeliverCollabPromptAsync("Job B", "t-b", "task-b", expectReply: true, ct));
            await WaitUntilAsync(() => secondPty.Written.Any(w => w.Contains("Job B", StringComparison.Ordinal)), ct);
            Assert.DoesNotContain("manual", string.Concat(secondPty.Written), StringComparison.Ordinal);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Send_held_manual_draft_without_enter_still_protects_live_draft_from_collab()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, _, session, pty, bridge) = StartDirect("recover-held-draft");
        using var router = CreateRouter(session);
        var pasted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            pty.EmitOutput("\x1b[?2004h");
            await WaitUntilAsync(() => session.CachedInputModes.BracketedPaste, ct);
            pty.OnWrite = bytes =>
            {
                if (bytes.Contains("cancelled", StringComparison.Ordinal)) pasted.TrySetResult();
                return ValueTask.CompletedTask;
            };

            var deliver = bridge.DeliverCollabPromptAsync("cancelled", "t-a", "task-a", expectReply: true, cancel.Token);
            await pasted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            router.HandleText("draft");
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deliver);

            Assert.True(session.SendHeldManualInput());
            await WaitUntilAsync(() => pty.Written.Contains("draft"), ct);

            // Replaying unsubmitted text does not prove the cancelled paste was cleared: held bytes are gone,
            // but the fence stays active until explicit clean confirmation or submit, and backspacing "draft"
            // cannot unblock collaboration over the residual cancelled paste.
            var remaining = session.GetPendingInputRecovery();
            Assert.NotNull(remaining);
            Assert.True(remaining!.HasUnresolvedCancelledPaste);
            Assert.False(remaining.HasHeldInput);
            for (var i = 0; i < 5; i++) router.SendKey(TerminalKey.Backspace);
            await WaitUntilAsync(() => pty.Written.Count == 7, ct);
            Assert.False(await bridge.DeliverCollabPromptAsync("Job B", "t-b", "task-b", expectReply: true, ct));
            Assert.DoesNotContain("Job B", string.Concat(pty.Written), StringComparison.Ordinal);

            Assert.True(session.MarkInputClean());
            Assert.Null(session.GetPendingInputRecovery());
            Assert.True(await bridge.DeliverCollabPromptAsync("Job B", "t-b", "task-b", expectReply: true, ct));
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task TerminalControl_recovery_bar_exposes_explicit_ui_actions_and_copies_held_input()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = new TerminalSessionRegistry();
        var factory = new RecordingTerminalFactory();
        var control = new TerminalControl();
        var window = new Window { Width = 800, Height = 500, Content = control };
        window.Show();
        try
        {
            control.Start(DummySpec(), factory, registry, "ui-recover");
            await WaitUntilAsync(() => registry.Find("ui-recover") is { IsRunning: true }, ct);

            var session = registry.Find("ui-recover")!;
            var pty = factory.Connections.Single();
            var bridge = new TerminalCollabSession("ui-recover", "claude", "CLI Worker", "/tmp", registry, activity: null);
            using var router = CreateRouter(session);

            pty.EmitOutput("\x1b[?2004h");
            await WaitUntilAsync(() => session.CachedInputModes.BracketedPaste, ct);

            var pasted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pty.OnWrite = bytes =>
            {
                if (bytes.Contains("cancelled", StringComparison.Ordinal)) pasted.TrySetResult();
                return ValueTask.CompletedTask;
            };

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var deliver = bridge.DeliverCollabPromptAsync("cancelled", "t-a", "task-a", expectReply: true, cancel.Token);
            await pasted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            router.HandleText("manual");
            router.SendKey(TerminalKey.Enter);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deliver);

            var bar = control.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PendingInputRecoveryBar");
            var sendBtn = control.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SendHeldInputButton");
            var copyBtn = control.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CopyHeldInputButton");
            var discardBtn = control.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DiscardHeldInputButton");
            var cleanBtn = control.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MarkInputCleanButton");

            await WaitUntilAsync(() => bar.IsVisible, ct);
            Assert.True(sendBtn.IsVisible);
            Assert.True(copyBtn.IsVisible);
            Assert.True(discardBtn.IsVisible);
            Assert.False(cleanBtn.IsVisible);
            Assert.Equal("manual\r", control.PendingInputRecovery?.HeldInputText);

            // Copy held input to clipboard via the UI control action without altering wire bytes or recovery state.
            Assert.True(await control.CopyHeldManualInputAsync());
            if (TopLevel.GetTopLevel(control)?.Clipboard is { } clipboard)
            {
                var copied = await clipboard.TryGetTextAsync();
                if (copied is not null)
                {
                    Assert.Equal("manual\r", copied);
                }
            }
            Assert.True(bar.IsVisible);
            Assert.DoesNotContain("\r", pty.Written);

            // Discard held input via the UI control action: bar switches to clean-confirmation mode and keeps fence.
            Assert.True(control.DiscardHeldManualInput());
            Frames.Pump();
            Assert.True(bar.IsVisible);
            Assert.False(sendBtn.IsVisible);
            Assert.False(copyBtn.IsVisible);
            Assert.False(discardBtn.IsVisible);
            Assert.True(cleanBtn.IsVisible);
            Assert.False(await bridge.DeliverCollabPromptAsync("Job B", "t-b", "task-b", expectReply: true, ct));

            // Confirm clean via the UI control action: bar hides and collaboration resumes cleanly.
            Assert.True(control.MarkInputClean());
            Frames.Pump();
            Assert.False(bar.IsVisible);
            Assert.Null(control.PendingInputRecovery);
            Assert.True(await bridge.DeliverCollabPromptAsync("Job B", "t-b", "task-b", expectReply: true, ct));
            await WaitUntilAsync(() => pty.Written.Any(w => w.Contains("Job B", StringComparison.Ordinal)), ct);
        }
        finally
        {
            window.Close();
            Frames.Pump();
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Normal_committed_bracketed_paste_still_replays_deferred_manual_input_after_collab_enter()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, _, session, pty, bridge) = StartDirect("recover-normal-commit");
        using var router = CreateRouter(session);
        try
        {
            pty.EmitOutput("\x1b[?2004h");
            await WaitUntilAsync(() => session.CachedInputModes.BracketedPaste, ct);
            pty.OnWrite = bytes =>
            {
                if (bytes.Contains("committed", StringComparison.Ordinal))
                {
                    router.HandleText("after");
                }
                return ValueTask.CompletedTask;
            };

            Assert.True(await bridge.DeliverCollabPromptAsync("committed", "t-a", "task-a", expectReply: true, ct));
            await WaitUntilAsync(() => pty.Written.Contains("after"), ct);
            Assert.Null(session.GetPendingInputRecovery());

            var written = pty.Written.ToArray();
            Assert.Equal(["\x1b[200~committed\x1b[201~", "\r", "after"], written);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    private static async Task TriggerCancelledPasteAsync(
        TerminalSession session,
        RecordingTerminalConnection pty,
        TerminalCollabSession bridge,
        string prompt,
        Action duringPaste,
        CancellationToken ct)
    {
        pty.EmitOutput("\x1b[?2004h");
        await WaitUntilAsync(() => session.CachedInputModes.BracketedPaste, ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = $"\x1b[200~{prompt}\x1b[201~";
        pty.OnWrite = async bytes =>
        {
            if (bytes == expected)
            {
                ready.TrySetResult();
                await finish.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
        };
        var delivery = bridge.DeliverCollabPromptAsync(prompt, "t-cancel", "task-cancel", expectReply: true, cancel.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        try
        {
            duringPaste();
            cancel.Cancel();
        }
        finally
        {
            finish.TrySetResult();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        pty.OnWrite = null;
        Assert.True(session.GetPendingInputRecovery()?.HasUnresolvedCancelledPaste);
    }

    [AvaloniaFact, FreshApp]
    public async Task Stale_send_button_does_not_act_on_restarted_generation_recovery_and_refreshes_view()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, factory, session, oldPty, bridge) = StartDirect("stale-restart-send");
        var control = new TerminalControl();
        var window = new Window { Width = 800, Height = 500, Content = control };
        window.Show();
        try
        {
            control.Start(DummySpec(), factory, registry, "stale-restart-send");
            using var router = CreateRouter(session);
            await TriggerCancelledPasteAsync(session, oldPty, bridge, "first", () => router.HandleText("old held"), ct);

            var bar = control.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PendingInputRecoveryBar");
            var sendBtn = bar.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "SendHeldInputButton");
            await WaitUntilAsync(() => bar.IsVisible, ct);
            var label = bar.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Text?.Contains("paste was cancelled", StringComparison.Ordinal) == true);
            await WaitUntilAsync(() => bar.IsVisible && label.Text!.Contains("old held", StringComparison.Ordinal), ct);

            // Restart and enter a second recovery synchronously on this dispatcher turn before queued UI refresh runs.
            session.RestartAsync().GetAwaiter().GetResult();
            var nextPty = factory.Connections.Last();
            nextPty.EmitOutput("\x1b[?2004h");
            Assert.True(SpinWait.SpinUntil(() => session.CachedInputModes.BracketedPaste, 3000));
            using var cancel = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            nextPty.OnWrite = async bytes =>
            {
                if (bytes == "\x1b[200~first\x1b[201~")
                {
                    started.TrySetResult();
                    await finish.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
                }
            };
            var second = bridge.DeliverCollabPromptAsync("first", "t-new", "task-new", expectReply: true, cancel.Token);
            Assert.True(started.Task.Wait(3000, ct));
            try
            {
                router.HandleText("new held");
                cancel.Cancel();
            }
            finally
            {
                finish.TrySetResult();
            }
            Assert.ThrowsAny<OperationCanceledException>(() => second.GetAwaiter().GetResult());
            Assert.Equal("new held", session.GetPendingInputRecovery()?.HeldInputText);
            Assert.Contains("old held", label.Text, StringComparison.Ordinal);

            // Clicking the stale "Send held" button must NOT send "new held" and must refresh the bar to "new held".
            sendBtn.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100, ct);
            Assert.DoesNotContain("new held", nextPty.Written);
            Assert.Equal("new held", session.GetPendingInputRecovery()?.HeldInputText);
            Assert.Contains("new held", label.Text, StringComparison.Ordinal);

            // Clicking again now that the bar displays "new held" sends it cleanly.
            sendBtn.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => nextPty.Written.Contains("new held"), ct);
        }
        finally
        {
            window.Close();
            Frames.Pump();
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Stale_recovery_buttons_do_not_act_on_later_recovery_on_same_session_generation()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, factory, session, pty, bridge) = StartDirect("stale-same-gen");
        var control = new TerminalControl();
        var window = new Window { Width = 800, Height = 500, Content = control };
        window.Show();
        try
        {
            control.Start(DummySpec(), factory, registry, "stale-same-gen");
            using var router = CreateRouter(session);
            await TriggerCancelledPasteAsync(session, pty, bridge, "first", () => router.HandleText("first held"), ct);

            var bar = control.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PendingInputRecoveryBar");
            var discardBtn = bar.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "DiscardHeldInputButton");
            var cleanBtn = bar.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "MarkInputCleanButton");
            await WaitUntilAsync(() => bar.IsVisible, ct);
            var label = bar.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Text?.Contains("paste was cancelled", StringComparison.Ordinal) == true);
            await WaitUntilAsync(() => bar.IsVisible && label.Text!.Contains("first held", StringComparison.Ordinal), ct);

            // Resolve recovery #1 directly and trigger recovery #2 on the SAME link generation before UI refresh runs.
            Assert.True(session.DiscardHeldManualInput());
            Assert.True(session.MarkInputClean());
            using (var cancel = new CancellationTokenSource())
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pty.OnWrite = async bytes =>
                {
                    if (bytes == "\x1b[200~second\x1b[201~")
                    {
                        started.TrySetResult();
                        await finish.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
                    }
                };
                var second = bridge.DeliverCollabPromptAsync("second", "t-2", "task-2", expectReply: true, cancel.Token);
                Assert.True(started.Task.Wait(3000, ct));
                try
                {
                    router.HandleText("second held");
                    cancel.Cancel();
                }
                finally
                {
                    finish.TrySetResult();
                }
                Assert.ThrowsAny<OperationCanceledException>(() => second.GetAwaiter().GetResult());
                pty.OnWrite = null;
            }
            Assert.Contains("first held", label.Text, StringComparison.Ordinal);

            // Stale Discard click bound to recovery #1 must NOT discard recovery #2's held input; it refreshes the bar.
            discardBtn.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("second held", session.GetPendingInputRecovery()?.HeldInputText);
            Assert.Contains("second held", label.Text, StringComparison.Ordinal);

            // Now discard recovery #2 cleanly, then transition to recovery #3 (clean-confirm) before UI refreshes.
            discardBtn.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(cleanBtn.IsVisible);
            Assert.True(session.MarkInputClean());
            using (var cancel = new CancellationTokenSource())
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pty.OnWrite = async bytes =>
                {
                    if (bytes == "\x1b[200~third\x1b[201~")
                    {
                        started.TrySetResult();
                        await finish.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
                    }
                };
                var third = bridge.DeliverCollabPromptAsync("third", "t-3", "task-3", expectReply: true, cancel.Token);
                Assert.True(started.Task.Wait(3000, ct));
                try
                {
                    cancel.Cancel();
                }
                finally
                {
                    finish.TrySetResult();
                }
                Assert.ThrowsAny<OperationCanceledException>(() => third.GetAwaiter().GetResult());
                pty.OnWrite = null;
            }

            // Stale "Input is clean" click bound to recovery #2 must NOT clear recovery #3's fence.
            cleanBtn.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(session.GetPendingInputRecovery()?.HasUnresolvedCancelledPaste);
            Assert.False(await bridge.DeliverCollabPromptAsync("Job B", "t-b", "task-b", expectReply: true, ct));

            // Clicking again now that the bar is refreshed to recovery #3 clears the fence and allows Job B.
            cleanBtn.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Null(session.GetPendingInputRecovery());
            Assert.True(await bridge.DeliverCollabPromptAsync("Job B", "t-b", "task-b", expectReply: true, ct));
        }
        finally
        {
            window.Close();
            Frames.Pump();
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Send_held_cursor_navigation_alone_retains_cancelled_paste_fence_until_confirmed_clean()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, _, session, pty, bridge) = StartDirect("recover-held-nav");
        using var router = CreateRouter(session);
        try
        {
            await TriggerCancelledPasteAsync(session, pty, bridge, "first", () => router.SendKey(TerminalKey.ArrowLeft), ct);

            Assert.True(session.SendHeldManualInput());
            await WaitUntilAsync(() => pty.Written.Count == 2, ct);

            // Held ArrowLeft did not submit or clear the cancelled paste residue: fence must remain active.
            var remaining = session.GetPendingInputRecovery();
            Assert.NotNull(remaining);
            Assert.True(remaining!.HasUnresolvedCancelledPaste);
            Assert.False(remaining.HasHeldInput);
            Assert.False(await bridge.DeliverCollabPromptAsync("second", "t-b", "task-b", expectReply: true, ct));
            Assert.DoesNotContain("second", string.Concat(pty.Written), StringComparison.Ordinal);

            // Explicit clean confirmation resolves the fence and unblocks the next job.
            Assert.True(session.MarkInputClean());
            Assert.Null(session.GetPendingInputRecovery());
            Assert.True(await bridge.DeliverCollabPromptAsync("second", "t-b", "task-b", expectReply: true, ct));
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Send_held_enter_alone_records_manual_turn_for_cancelled_paste_residue()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, _, session, pty, bridge) = StartDirect("recover-held-enter-only");
        using var router = CreateRouter(session);
        try
        {
            await TriggerCancelledPasteAsync(session, pty, bridge, "first", () => router.SendKey(TerminalKey.Enter), ct);

            Assert.True(session.SendHeldManualInput());
            Assert.Null(session.GetPendingInputRecovery());
            Assert.True(await bridge.DeliverCollabPromptAsync("second", "t-b", "task-b", expectReply: true, ct));
            await WaitUntilAsync(() => pty.Written.Count == 4, ct);
            Assert.Equal(["\x1b[200~first\x1b[201~", "\r", "\x1b[200~second\x1b[201~", "\r"], pty.Written.ToArray());

            // First Done belongs to the submitted cancelled-paste residue (unattributed/manual), NOT Job B.
            pty.EmitOutput("first manual response\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("first manual response", StringComparison.Ordinal), ct);
            Assert.Null(bridge.TakeAnswer(3500));

            // Second Done settles Job B.
            pty.EmitOutput("second collab response\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("second collab response", StringComparison.Ordinal), ct);
            var answer = bridge.TakeAnswer(3500);
            Assert.NotNull(answer);
            Assert.Equal("task-b", answer!.Value.TaskId);
            Assert.Contains("second collab response", answer.Value.Text, StringComparison.Ordinal);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Mark_input_clean_after_indeterminate_keyboard_cleanup_unblocks_next_collab_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, _, session, pty, bridge) = StartDirect("recover-clean-after-tab-ctrlu");
        using var router = CreateRouter(session);
        try
        {
            await TriggerCancelledPasteAsync(session, pty, bridge, "first", () => { }, ct);
            router.SendKey(TerminalKey.Tab);
            router.SendKey(TerminalKey.U, KeyMods.Ctrl);
            await WaitUntilAsync(() => pty.Written.Count == 3, ct);

            Assert.True(session.MarkInputClean());
            Assert.Null(session.GetPendingInputRecovery());
            Assert.True(await bridge.DeliverCollabPromptAsync("second", "t-b", "task-b", expectReply: true, ct));
            await WaitUntilAsync(() => pty.Written.Any(w => w.Contains("second", StringComparison.Ordinal)), ct);
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(false, "SendHeldInputButton")]
    [InlineData(true, "SendHeldInputButton")]
    [InlineData(false, "DiscardHeldInputButton")]
    [InlineData(true, "DiscardHeldInputButton")]
    [InlineData(false, "CopyHeldInputButton")]
    [InlineData(true, "CopyHeldInputButton")]
    [InlineData(false, "MarkInputCleanButton")]
    [InlineData(true, "MarkInputCleanButton")]
    public async Task Pointer_press_on_old_recovery_released_after_new_preview_cannot_affect_new_recovery(bool restart, string buttonName)
    {
        var ct = TestContext.Current.CancellationToken;
        var clean = buttonName == "MarkInputCleanButton";
        var (registry, factory, session, oldPty, bridge) = StartDirect($"gesture-{restart}-{buttonName}");
        var control = new TerminalControl();
        var window = new Window { Width = 840, Height = 480, Content = control };
        window.Show();
        try
        {
            control.Start(DummySpec(), factory, registry, $"gesture-{restart}-{buttonName}");
            Frames.Pump();
            if (window.Clipboard is not null)
            {
                await window.Clipboard.SetTextAsync("sentinel");
            }
            using var router = CreateRouter(session);
            await TriggerCancelledPasteAsync(session, oldPty, bridge, "first", () => { if (!clean) router.HandleText("old"); }, ct);
            Frames.Pump();

            var bar = control.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PendingInputRecoveryBar");
            var button = bar.GetVisualDescendants().OfType<Button>().Single(x => x.Name == buttonName);
            await WaitUntilAsync(() => bar.IsVisible && button.IsEffectivelyVisible && button.Bounds.Width > 0, ct);
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;

            window.MouseMove(point);
            window.MouseDown(point, global::Avalonia.Input.MouseButton.Left);
            Assert.True(button.IsPressed);
            var oldRecoveryId = session.GetPendingInputRecovery()!.RecoveryId;
            var oldLinkId = session.CurrentLinkId;

            if (restart)
            {
                session.RestartAsync().GetAwaiter().GetResult();
            }
            else
            {
                if (!clean) Assert.True(session.DiscardHeldManualInput());
                Assert.True(session.MarkInputClean());
            }

            var currentPty = factory.Connections.Last();
            await TriggerCancelledPasteAsync(session, currentPty, bridge, "second", () => { if (!clean) session.Send("new\r"); }, ct);
            Frames.Pump();

            var replacement = session.GetPendingInputRecovery()!;
            Assert.NotEqual(oldRecoveryId, replacement.RecoveryId);
            Assert.Equal(restart, oldLinkId != session.CurrentLinkId);

            // Releasing the pointer gesture started on recovery A must NOT affect recovery B.
            window.MouseUp(point, global::Avalonia.Input.MouseButton.Left);
            Frames.Pump();
            await Task.Delay(80, ct);

            var afterRelease = session.GetPendingInputRecovery();
            Assert.NotNull(afterRelease);
            Assert.Equal(replacement.RecoveryId, afterRelease!.RecoveryId);
            Assert.Equal(clean ? "" : "new\r", afterRelease.HeldInputText);
            Assert.DoesNotContain("new\r", currentPty.Written);
            Assert.False(await bridge.DeliverCollabPromptAsync("third", "t-3", "task-3", expectReply: true, ct));
            if (window.Clipboard is not null)
            {
                var clip = await window.Clipboard.TryGetTextAsync();
                if (clip is not null) Assert.Equal("sentinel", clip);
            }
        }
        finally
        {
            window.Close();
            Frames.Pump();
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Rejected_held_write_preserves_unaccepted_suffix_without_duplicating_prefix_or_blindly_retrying()
    {
        var ct = TestContext.Current.CancellationToken;
        // Part 1: First held entry rejected with IOException -> entire held batch remains available.
        var (r1, _, s1, p1, b1) = StartDirect("reject-first");
        using var router1 = CreateRouter(s1);
        try
        {
            await TriggerCancelledPasteAsync(s1, p1, b1, "first", () =>
            {
                router1.HandleText("recover me");
                router1.SendKey(TerminalKey.Enter);
            }, ct);

            var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            p1.OnWrite = async data =>
            {
                if (data == "recover me")
                {
                    attempted.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                    throw new IOException("rejected first held write");
                }
            };
            try
            {
                _ = s1.SendHeldManualInput();
                await attempted.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
            }
            finally
            {
                release.TrySetResult();
            }

            await Task.Delay(80, ct);
            Assert.Equal(["\x1b[200~first\x1b[201~"], p1.Written.ToArray());
            Assert.Equal("recover me\r", s1.GetPendingInputRecovery()?.HeldInputText);
            Assert.False(await b1.DeliverCollabPromptAsync("second", "t-2", "task-2", expectReply: true, ct));
            // Must not blindly retry an uncertain/rejected write.
            Assert.False(s1.SendHeldManualInput());
            // Explicit discard still works so the user can abandon held bytes after copying them.
            Assert.True(s1.DiscardHeldManualInput());
            Assert.False(s1.GetPendingInputRecovery()!.HasHeldInput);
        }
        finally
        {
            await r1.CloseAllAsync();
        }

        // Part 2: First held entry accepted, second rejected -> accepted prefix is NOT duplicated, unaccepted suffix remains.
        var (r2, _, s2, p2, b2) = StartDirect("reject-second");
        using var router2 = CreateRouter(s2);
        try
        {
            await TriggerCancelledPasteAsync(s2, p2, b2, "first", () =>
            {
                router2.HandleText("prefix");
                router2.HandleText("suffix");
                router2.SendKey(TerminalKey.Enter);
            }, ct);

            p2.OnWrite = data => data == "suffix"
                ? ValueTask.FromException(new IOException("rejected second held write"))
                : ValueTask.CompletedTask;

            _ = s2.SendHeldManualInput();
            await WaitUntilAsync(() => p2.Written.Contains("prefix"), ct);
            await Task.Delay(80, ct);

            Assert.Equal(["\x1b[200~first\x1b[201~", "prefix"], p2.Written.ToArray());
            Assert.Equal("suffix\r", s2.GetPendingInputRecovery()?.HeldInputText);
            Assert.False(s2.SendHeldManualInput());
            Assert.Equal(["\x1b[200~first\x1b[201~", "prefix"], p2.Written.ToArray());
            Assert.False(await b2.DeliverCollabPromptAsync("second", "t-2", "task-2", expectReply: true, ct));
        }
        finally
        {
            await r2.CloseAllAsync();
        }
    }

    [AvaloniaTheory, FreshApp]
    [InlineData("send")]
    [InlineData("copy")]
    [InlineData("discard")]
    [InlineData("clean")]
    public async Task Public_TerminalControl_recovery_methods_work_before_initial_view_refresh(string op)
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, factory, session, pty, bridge) = StartDirect($"pub-pre-refresh-{op}");
        var control = new TerminalControl();
        var window = new Window { Width = 800, Height = 500, Content = control };
        window.Show();
        try
        {
            control.Start(DummySpec(), factory, registry, $"pub-pre-refresh-{op}");
            Frames.Pump();
            pty.EmitOutput("\x1b[?2004h");
            Assert.True(SpinWait.SpinUntil(() => session.CachedInputModes.BracketedPaste, 3000));
            using (var cancel = new CancellationTokenSource())
            {
                var hit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pty.OnWrite = async data =>
                {
                    if (data == "\x1b[200~first\x1b[201~")
                    {
                        hit.TrySetResult();
                        await go.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                    }
                };
                var delivery = bridge.DeliverCollabPromptAsync("first", "t", "task", expectReply: true, cancel.Token);
                try
                {
                    Assert.True(hit.Task.Wait(3000, ct));
                    if (op != "clean") session.Send("mine");
                    cancel.Cancel();
                }
                finally
                {
                    go.TrySetResult();
                }
                Assert.ThrowsAny<OperationCanceledException>(() => delivery.GetAwaiter().GetResult());
                pty.OnWrite = null;
            }

            // Immediately after PendingInputRecovery becomes non-null (before queued UI refresh), public control APIs must work.
            Assert.NotNull(control.PendingInputRecovery);
            var bar = control.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PendingInputRecoveryBar");
            Assert.False(bar.IsVisible);

            var ok = op switch
            {
                "send" => control.SendHeldManualInput(),
                "copy" => await control.CopyHeldManualInputAsync(),
                "discard" => control.DiscardHeldManualInput(),
                _ => control.MarkInputClean(),
            };
            Assert.True(ok);
        }
        finally
        {
            window.Close();
            Frames.Pump();
            await registry.CloseAllAsync();
        }
    }

    [Fact]
    public void TerminalPendingInputRecovery_preserves_five_argument_constructor_and_five_field_deconstruction()
    {
        var bytes = Encoding.UTF8.GetBytes("abc\r");
        var recovery = new TerminalPendingInputRecovery(true, true, "abc↵", "abc\r", bytes);
        var (unresolved, hasHeld, preview, fullText, rawBytes) = recovery;
        Assert.True(unresolved);
        Assert.True(hasHeld);
        Assert.Equal("abc↵", preview);
        Assert.Equal("abc\r", fullText);
        Assert.Same(bytes, rawBytes);
        Assert.Equal(0, recovery.RecoveryId);

        var withId = recovery with { RecoveryId = 42 };
        Assert.Equal(42, withId.RecoveryId);
        Assert.NotEqual(recovery, withId);

        // Preserve six-argument constructor and six-output deconstructor alongside the five-field members,
        // including PascalCase named arguments on both constructors and both Deconstruct overloads.
        var fiveNamed = new TerminalPendingInputRecovery(
            HasUnresolvedCancelledPaste: true,
            HasHeldInput: true,
            HeldInputPreview: "abc↵",
            HeldInputText: "abc\r",
            HeldInputBytes: bytes);
        fiveNamed.Deconstruct(
            HasUnresolvedCancelledPaste: out var nu5,
            HasHeldInput: out var nh5,
            HeldInputPreview: out var np5,
            HeldInputText: out var nt5,
            HeldInputBytes: out var nb5);
        Assert.True(nu5 && nh5);
        Assert.Equal("abc↵", np5);
        Assert.Equal("abc\r", nt5);
        Assert.Same(bytes, nb5);

        var sixArg = new TerminalPendingInputRecovery(true, true, "abc↵", "abc\r", bytes, RecoveryId: 17L);
        var sixNamed = new TerminalPendingInputRecovery(
            HasUnresolvedCancelledPaste: true,
            HasHeldInput: true,
            HeldInputPreview: "abc↵",
            HeldInputText: "abc\r",
            HeldInputBytes: bytes,
            RecoveryId: 17L);
        Assert.Equal(sixArg, sixNamed);
        var (u6, h6, p6, t6, b6, id6) = sixArg;
        Assert.True(u6);
        Assert.True(h6);
        Assert.Equal("abc↵", p6);
        Assert.Equal("abc\r", t6);
        Assert.Same(bytes, b6);
        Assert.Equal(17L, id6);

        sixNamed.Deconstruct(
            HasUnresolvedCancelledPaste: out var nu6,
            HasHeldInput: out var nh6,
            HeldInputPreview: out var np6,
            HeldInputText: out var nt6,
            HeldInputBytes: out var nb6,
            RecoveryId: out var nid6);
        Assert.True(nu6 && nh6);
        Assert.Equal("abc↵", np6);
        Assert.Equal("abc\r", nt6);
        Assert.Same(bytes, nb6);
        Assert.Equal(17L, nid6);
    }

    private static void ClickControlButton(Window window, Control root, string buttonName)
    {
        var button = root.GetVisualDescendants().OfType<Button>().Single(x => x.Name == buttonName);
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, global::Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, global::Avalonia.Input.MouseButton.Left);
    }

    [AvaloniaFact, FreshApp]
    public async Task Live_text_typed_during_delayed_held_enter_stays_protected_from_collab()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, factory, session, pty, bridge) = StartDirect("delayed-enter-live-draft");
        var control = new TerminalControl();
        var window = new Window { Width = 840, Height = 480, Content = control };
        window.Show();
        try
        {
            control.Start(DummySpec(), factory, registry, "delayed-enter-live-draft");
            Frames.Pump();
            using var router = CreateRouter(session);
            await TriggerCancelledPasteAsync(session, pty, bridge, "first", () => router.SendKey(TerminalKey.Enter), ct);
            Frames.Pump();

            var bar = control.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PendingInputRecoveryBar");
            await WaitUntilAsync(() => bar.IsVisible, ct);

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pty.OnWrite = async data =>
            {
                if (data == "\r")
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                }
            };
            try
            {
                ClickControlButton(window, control, "SendHeldInputButton");
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
                router.HandleText("private draft界");
            }
            finally
            {
                release.TrySetResult();
            }

            await WaitUntilAsync(() => pty.Written.Count == 3, ct);
            Assert.Equal(["\x1b[200~first\x1b[201~", "\r", "private draft界"], pty.Written.ToArray());

            // The live draft typed after clicking Send must stay protected from automated collaboration.
            Assert.False(await bridge.DeliverCollabPromptAsync("second", "t-2", "task-2", expectReply: true, ct));
            Assert.DoesNotContain("second", string.Concat(pty.Written), StringComparison.Ordinal);

            // Submitting the live draft records a second manual turn and unblocks collaboration cleanly.
            pty.OnWrite = null;
            router.SendKey(TerminalKey.Enter);
            await WaitUntilAsync(() => pty.Written.Count == 4, ct);
            Assert.True(await bridge.DeliverCollabPromptAsync("second", "t-2", "task-2", expectReply: true, ct));
            Assert.Null(bridge.TakeAnswer(3500));
            Assert.Null(bridge.TakeAnswer(3500));
            Assert.Equal("task-2", bridge.TakeAnswer(3500)?.TaskId);
        }
        finally
        {
            window.Close();
            Frames.Pump();
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Delayed_successful_held_send_does_not_show_failure_or_allow_discard_while_in_flight()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, factory, session, pty, bridge) = StartDirect("delayed-send-ui-status");
        var control = new TerminalControl();
        var window = new Window { Width = 840, Height = 480, Content = control };
        window.Show();
        try
        {
            control.Start(DummySpec(), factory, registry, "delayed-send-ui-status");
            Frames.Pump();
            using var router = CreateRouter(session);
            await TriggerCancelledPasteAsync(session, pty, bridge, "first", () => router.SendKey(TerminalKey.Enter), ct);
            Frames.Pump();

            var bar = control.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PendingInputRecoveryBar");
            var discardBtn = bar.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "DiscardHeldInputButton");
            var label = bar.GetVisualDescendants().OfType<TextBlock>().First(x => x.TextWrapping == global::Avalonia.Media.TextWrapping.Wrap);
            await WaitUntilAsync(() => bar.IsVisible, ct);

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pty.OnWrite = async data =>
            {
                if (data == "\r")
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                }
            };

            string inFlightMessage;
            bool discardAvailable;
            try
            {
                ClickControlButton(window, control, "SendHeldInputButton");
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
                Frames.Pump();
                inFlightMessage = bar.IsVisible ? (label.Text ?? string.Empty) : string.Empty;
                discardAvailable = bar.IsVisible && discardBtn.IsEffectivelyVisible && discardBtn.IsEffectivelyEnabled;
            }
            finally
            {
                release.TrySetResult();
            }

            await WaitUntilAsync(() => pty.Written.Count == 2 && control.PendingInputRecovery is null, ct);
            Assert.Equal(["\x1b[200~first\x1b[201~", "\r"], pty.Written.ToArray());
            Assert.DoesNotContain("failed", inFlightMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(discardAvailable);
        }
        finally
        {
            window.Close();
            Frames.Pump();
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Copy_during_pending_replay_copies_only_remaining_unaccepted_suffix()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, factory, session, pty, bridge) = StartDirect("copy-pending-suffix");
        var control = new TerminalControl();
        var window = new Window { Width = 840, Height = 480, Content = control };
        window.Show();
        try
        {
            control.Start(DummySpec(), factory, registry, "copy-pending-suffix");
            Frames.Pump();
            using var router = CreateRouter(session);
            await TriggerCancelledPasteAsync(session, pty, bridge, "first", () =>
            {
                router.HandleText("prefix");
                router.HandleText("uncertain");
                router.SendKey(TerminalKey.Enter);
            }, ct);
            Frames.Pump();

            var bar = control.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PendingInputRecoveryBar");
            var copyBtn = bar.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "CopyHeldInputButton");
            await WaitUntilAsync(() => bar.IsVisible, ct);

            var prefixEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptPrefix = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rejectSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pty.OnWrite = async data =>
            {
                if (data == "prefix")
                {
                    prefixEntered.TrySetResult();
                    await acceptPrefix.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                }
                else if (data == "uncertain")
                {
                    secondEntered.TrySetResult();
                    await rejectSecond.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                    throw new IOException("simulated uncertain write");
                }
            };

            string? remaining;
            string? copied = null;
            try
            {
                ClickControlButton(window, control, "SendHeldInputButton");
                await prefixEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
                acceptPrefix.TrySetResult();
                await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
                Frames.Pump();
                remaining = control.PendingInputRecovery?.HeldInputText;
                Assert.True(copyBtn.IsEffectivelyVisible);
                ClickControlButton(window, control, "CopyHeldInputButton");
                Frames.Pump();
                if (window.Clipboard is not null)
                {
                    copied = await window.Clipboard.TryGetTextAsync();
                }
            }
            finally
            {
                acceptPrefix.TrySetResult();
                rejectSecond.TrySetResult();
            }

            Assert.Equal("uncertain\r", remaining);
            if (copied is not null)
            {
                Assert.Equal("uncertain\r", copied);
            }
        }
        finally
        {
            window.Close();
            Frames.Pump();
            await registry.CloseAllAsync();
        }
    }

    private sealed class StubParentBridge(ProjectItem item, string cwd) : ICollabSessionBridge
    {
        public string ItemId => item.Id;
        public string AgentId => "claude";
        public string DisplayTitle => item.DisplayTitle;
        public string WorkingDirectory => cwd;
        public bool IsBusy => false;
        public bool IsConnected => true;
        public string? CurrentStatusDetail => null;
        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public string ReadRecentSummary(int maxTurns = 5) => "";
    }

    private sealed class GatedSuccessfulWrite : IValueTaskSource
    {
        public TaskCompletionSource Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeWriter { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void GetResult(short token) => Result.Task.GetAwaiter().GetResult();

        public ValueTaskSourceStatus GetStatus(short token) =>
            Result.Task.IsCompletedSuccessfully ? ValueTaskSourceStatus.Succeeded : ValueTaskSourceStatus.Pending;

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            Subscribed.TrySetResult();
            _ = ResumeAsync();
            async Task ResumeAsync()
            {
                await Result.Task.ConfigureAwait(false);
                await ResumeWriter.Task.ConfigureAwait(false);
                continuation(state);
            }
        }
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Real_coordinator_completes_next_delegated_job_when_manual_done_precedes_writer_continuation(bool pauseSuccessfulWriter, bool heldEnter)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("octopus-fifo-coord-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(root);
        var parent = new ProjectItem { AgentId = "claude", Kind = ProjectItemKind.Agent, Title = "Lead", Surface = AgentSurface.Gui };
        var child = new ProjectItem { AgentId = "claude", Kind = ProjectItemKind.Agent, Title = "Console", Surface = AgentSurface.Terminal };
        projects.AddItem(project, parent);
        projects.AddItem(project, child);

        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        coordinator.RegisterGuiSession(new StubParentBridge(parent, root));

        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new RecordingTerminalFactory();
        var control = new TerminalControl();
        var window = new Window { Width = 840, Height = 480, Content = control };
        window.Show();
        try
        {
            control.Start(DummySpec(), factory, registry, child.Id);
            await WaitUntilAsync(() => registry.Find(child.Id) is { IsRunning: true }, ct);
            var session = registry.Find(child.Id)!;
            var pty = factory.Connections.Single();
            var signals = AppHost.Get<ISignalHub>();
            var bridge = new TerminalCollabSession(child.Id, "claude", "Console", root, registry, activity: null);

            void PublishDone()
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                signals.Publish(new AgentSignal("claude", "stop", child.Id, "", now, now, "{}", session.CurrentLinkId));
            }

            async Task<string> DelegateAsync(string goal)
            {
                var response = await coordinator.CallToolAsync(
                    parent.Id,
                    "delegate_task",
                    new JsonObject { ["target"] = child.Id, ["goal"] = goal },
                    ct);
                Assert.Null(response["isError"]);
                return JsonNode.Parse(response["content"]![0]!["text"]!.GetValue<string>())!["task_id"]!.GetValue<string>();
            }

            CollabTaskStatus TaskStatus(string id) =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == id).Status;

            var warmup = await DelegateAsync("warmup");
            PublishDone();
            await WaitUntilAsync(() => TaskStatus(warmup) == CollabTaskStatus.Completed, ct);

            using var router = CreateRouter(session);
            await TriggerCancelledPasteAsync(session, pty, bridge, "first", () =>
            {
                if (heldEnter) router.SendKey(TerminalKey.Enter);
                else router.SendKey(TerminalKey.C, KeyMods.Ctrl);
            }, ct);
            Frames.Pump();

            var bar = control.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PendingInputRecoveryBar");
            await WaitUntilAsync(() => bar.IsVisible, ct);

            var heldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gatedKey = heldEnter ? "\r" : "\u0003";
            var manualEnter = new GatedSuccessfulWrite();
            var enterWrites = 0;
            pty.OnWrite = async data =>
            {
                if (data == gatedKey)
                {
                    heldStarted.TrySetResult();
                    await releaseHeld.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                }
            };
            pty.WriteOverride = data => data == "\r" && Interlocked.Increment(ref enterWrites) == (heldEnter ? 2 : 1)
                ? new ValueTask(manualEnter, 0)
                : null;
            try
            {
                ClickControlButton(window, control, "SendHeldInputButton");
                await heldStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
                router.PasteText("manual界");
                router.SendKey(TerminalKey.Enter);
                releaseHeld.TrySetResult();
                await manualEnter.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);

                if (heldEnter) PublishDone();
                pty.Written.Enqueue("\r");
                manualEnter.Result.TrySetResult(true);
                Assert.Equal(ValueTaskSourceStatus.Succeeded, manualEnter.GetStatus(0));
                if (pauseSuccessfulWriter) PublishDone();
            }
            finally
            {
                releaseHeld.TrySetResult();
                manualEnter.Result.TrySetResult(true);
                manualEnter.ResumeWriter.TrySetResult();
            }

            const string barrier = "\x1b[1;1R";
            Assert.True(session.TrySendCollabControl(barrier, session.Generation));
            await WaitUntilAsync(() => pty.Written.Contains(barrier), ct);
            if (!pauseSuccessfulWriter) PublishDone();

            var taskB = await DelegateAsync("follow-up B");
            Assert.Equal(CollabTaskStatus.Running, TaskStatus(taskB));
            pty.EmitOutput("answer-for-B\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("answer-for-B", StringComparison.Ordinal), ct);
            PublishDone();

            var completedOnOwnDone = Frames.Until(() => TaskStatus(taskB) == CollabTaskStatus.Completed, milliseconds: 500);
            Assert.True(completedOnOwnDone, "Delegated task B remained Running after its own identified Done.");
        }
        finally
        {
            window.Close();
            Frames.Pump();
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Manual_submit_queued_behind_held_replay_owns_early_done_without_stealing_next_collab_done(bool earlyDone)
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, _, session, pty, bridge) = StartDirect($"early-done-{earlyDone}");
        using var router = CreateRouter(session);
        try
        {
            await TriggerCancelledPasteAsync(session, pty, bridge, "first", () => router.SendKey(TerminalKey.C, KeyMods.Ctrl), ct);
            var ctrlCStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCtrlC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var enterReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseEnter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pty.OnWrite = async data =>
            {
                if (data == "\u0003")
                {
                    ctrlCStarted.TrySetResult();
                    await releaseCtrlC.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                }
                if (data == "\r")
                {
                    enterReceived.TrySetResult();
                    await releaseEnter.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                }
            };

            TerminalTurnAnswer? ConsumeIdentifiedDone()
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var sig = new AgentSignal("claude", "stop", $"early-done-{earlyDone}", "", now, now, "{}", session.CurrentLinkId);
                return bridge.TakeAnswer(3500, sig);
            }

            try
            {
                _ = session.SendHeldManualInput();
                await ctrlCStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
                router.PasteText("manual界");
                router.SendKey(TerminalKey.Enter);
                releaseCtrlC.TrySetResult();
                await enterReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
                if (earlyDone) Assert.Null(ConsumeIdentifiedDone());
            }
            finally
            {
                releaseCtrlC.TrySetResult();
                releaseEnter.TrySetResult();
            }

            Assert.True(session.TrySendCollabControl("\x1b[1;1R", session.Generation));
            await WaitUntilAsync(() => pty.Written.Contains("\x1b[1;1R"), ct);
            if (!earlyDone) Assert.Null(ConsumeIdentifiedDone());

            Assert.True(await bridge.DeliverCollabPromptAsync("second", "t-b", "task-b", expectReply: true, ct));
            Assert.Equal("task-b", ConsumeIdentifiedDone()?.TaskId);
            Assert.Null(ConsumeIdentifiedDone());
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Rejected_deferred_manual_enter_rolls_back_turn_slot_and_leaves_no_phantom_turn()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, _, session, pty, _) = StartDirect("rollback-phantom-turn");
        using var router = CreateRouter(session);
        try
        {
            await TriggerCancelledPasteAsync(session, pty, new TerminalCollabSession("rollback-phantom-turn", "claude", "CLI", "/tmp", registry, activity: null), "first", () => router.SendKey(TerminalKey.C, KeyMods.Ctrl), ct);
            var ctrlCStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCtrlC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pty.OnWrite = async data =>
            {
                if (data == "\u0003")
                {
                    ctrlCStarted.TrySetResult();
                    await releaseCtrlC.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                }
                if (data == "\r")
                {
                    throw new IOException("refused deferred enter");
                }
            };
            try
            {
                _ = session.SendHeldManualInput();
                await ctrlCStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
                router.PasteText("manual界");
                router.SendKey(TerminalKey.Enter);
            }
            finally
            {
                releaseCtrlC.TrySetResult();
            }

            await WaitUntilAsync(() => pty.Written.Contains("\x1b[200~manual界\x1b[201~"), ct);
            await Task.Delay(80, ct);

            // Refused deferred Enter must have rolled back its pre-registered manual turn so zero phantom turn exists.
            List<long>? drained = null;
            session.DrainManualTurns(session.Generation, ref drained, out var evicted);
            Assert.Equal(0, evicted);
            Assert.True(drained is null || drained.Count == 0);
            Assert.False(session.TryConsumeLeadingManualTurn());
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Rejected_held_enter_rolls_back_turn_slot_and_preserves_uncertain_recovery()
    {
        var ct = TestContext.Current.CancellationToken;
        var (registry, _, session, pty, _) = StartDirect("rollback-held-enter");
        using var router = CreateRouter(session);
        try
        {
            await TriggerCancelledPasteAsync(session, pty, new TerminalCollabSession("rollback-held-enter", "claude", "CLI", "/tmp", registry, activity: null), "first", () => router.SendKey(TerminalKey.Enter), ct);
            var enterFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pty.OnWrite = data =>
            {
                if (data == "\r")
                {
                    enterFailed.TrySetResult();
                    throw new IOException("refused held enter");
                }
                return ValueTask.CompletedTask;
            };

            Assert.False(session.SendHeldManualInput());
            await enterFailed.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);

            // Failed held Enter must preserve the held entry as uncertain (blocking blind retry) AND roll back its
            // pre-registered turn slot so zero phantom turn survives.
            var recovery = session.GetPendingInputRecovery();
            Assert.NotNull(recovery);
            Assert.True(recovery.HasUnresolvedCancelledPaste);
            Assert.True(recovery.HasHeldInput);
            Assert.Equal("\r", recovery.HeldInputText);
            Assert.False(session.SendHeldManualInput());
            Assert.False(session.TryBeginCollabSubmission(session.Generation, out _));

            List<long>? drained = null;
            session.DrainManualTurns(session.Generation, ref drained, out var evicted);
            Assert.Equal(0, evicted);
            Assert.True(drained is null || drained.Count == 0);
            Assert.False(session.TryConsumeLeadingManualTurn());
        }
        finally
        {
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(0, false, false)]
    [InlineData(0, false, true)]
    [InlineData(1, false, true)]
    [InlineData(62, false, true)]
    [InlineData(63, false, true)]
    [InlineData(63, true, false)]
    [InlineData(63, true, true)]
    public async Task Earlier_delegated_job_completes_when_provisional_manual_enter_fails_with_or_without_concurrent_done(
        int laterSubmitted,
        bool heldFail,
        bool duringWrite)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("octopus-rollback-enter-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(root);
        var parent = new ProjectItem { AgentId = "claude", Kind = ProjectItemKind.Agent, Title = "Lead", Surface = AgentSurface.Gui };
        var child = new ProjectItem { AgentId = "claude", Kind = ProjectItemKind.Agent, Title = "Console", Surface = AgentSurface.Terminal };
        projects.AddItem(project, parent);
        projects.AddItem(project, child);

        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        coordinator.RegisterGuiSession(new StubParentBridge(parent, root));

        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new RecordingTerminalFactory();
        var control = new TerminalControl();
        var window = new Window { Width = 840, Height = 480, Content = control };
        window.Show();
        try
        {
            control.Start(DummySpec(), factory, registry, child.Id);
            await WaitUntilAsync(() => registry.Find(child.Id) is { IsRunning: true }, ct);
            var session = registry.Find(child.Id)!;
            var pty = factory.Connections.Single();
            var signals = AppHost.Get<ISignalHub>();

            void PublishDone(string? linkId)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                signals.Publish(new AgentSignal("claude", "stop", child.Id, "", now, now, "{}", linkId));
            }

            async Task<string> DelegateAsync(string goal)
            {
                var response = await coordinator.CallToolAsync(
                    parent.Id,
                    "delegate_task",
                    new JsonObject { ["target"] = child.Id, ["goal"] = goal },
                    ct);
                Assert.Null(response["isError"]);
                var id = JsonNode.Parse(response["content"]![0]!["text"]!.GetValue<string>())!["task_id"]!.GetValue<string>();
                Assert.Equal(CollabTaskStatus.Running, TaskStatus(id));
                return id;
            }

            CollabTaskStatus TaskStatus(string id) =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == id).Status;

            var taskB = await DelegateAsync("job-B-before-manual-queue");
            var oldLink = session.CurrentLinkId;

            using var router = CreateRouter(session);
            for (var i = 0; i < laterSubmitted; i++)
            {
                router.HandleText($"subsequent-command-{i}");
                router.SendKey(TerminalKey.Enter);
            }
            const string barrier = "\x1b[1;1R";
            Assert.True(session.TrySendCollabControl(barrier, session.Generation));
            await WaitUntilAsync(() => pty.Written.Contains(barrier), ct);
            Assert.Equal(laterSubmitted, pty.Written.Count(x => x == "\r"));

            var cancelBridge = new TerminalCollabSession(child.Id, "claude", "Console", root, registry, activity: null);
            await TriggerCancelledPasteAsync(session, pty, cancelBridge, "first", () =>
            {
                if (heldFail) router.SendKey(TerminalKey.Enter);
                else router.HandleText("held-prefix");
            }, ct);
            Frames.Pump();

            var prefixStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePrefix = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var enterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rejectEnter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pty.OnWrite = async data =>
            {
                if (data == "held-prefix")
                {
                    prefixStarted.TrySetResult();
                    await releasePrefix.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                }
                if (data == "\r")
                {
                    enterStarted.TrySetResult();
                    await rejectEnter.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                    throw new IOException("rejected Enter before receipt");
                }
            };

            CollabTaskStatus statusOnOwnDone;
            try
            {
                ClickControlButton(window, control, "SendHeldInputButton");
                if (!heldFail)
                {
                    await prefixStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
                    router.SendKey(TerminalKey.Enter);
                    releasePrefix.TrySetResult();
                }
                await enterStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
                if (!heldFail)
                {
                    Assert.True(Frames.Until(() =>
                        control.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MarkInputCleanButton").IsEffectivelyVisible));
                    Frames.Pump();
                    ClickControlButton(window, control, "MarkInputCleanButton");
                    Frames.Pump();
                    Assert.Null(control.PendingInputRecovery);
                }
                Assert.False(session.TryBeginCollabSubmission(session.Generation, out _));
                if (duringWrite)
                {
                    pty.EmitOutput("answer from B\r\n");
                    await WaitUntilAsync(() => session.ReadText().Contains("answer from B", StringComparison.Ordinal), ct);
                    PublishDone(oldLink);
                    Frames.Until(() => TaskStatus(taskB) == CollabTaskStatus.Completed, milliseconds: 500);
                    statusOnOwnDone = TaskStatus(taskB);
                }
                else
                {
                    Assert.Equal(CollabTaskStatus.Running, TaskStatus(taskB));
                    statusOnOwnDone = CollabTaskStatus.Running;
                }
            }
            finally
            {
                releasePrefix.TrySetResult();
                rejectEnter.TrySetResult();
            }

            await WaitUntilAsync(() => !session.TrySendCollabControl("", session.Generation), ct);
            if (!duringWrite)
            {
                pty.EmitOutput("answer from B\r\n");
                await WaitUntilAsync(() => session.ReadText().Contains("answer from B", StringComparison.Ordinal), ct);
                PublishDone(oldLink);
                Frames.Until(() => TaskStatus(taskB) == CollabTaskStatus.Completed, milliseconds: 500);
                statusOnOwnDone = TaskStatus(taskB);
            }

            Assert.Equal(CollabTaskStatus.Completed, statusOnOwnDone);
            if (heldFail)
            {
                Assert.Equal("\r", control.PendingInputRecovery?.HeldInputText);
                Frames.Pump();
                ClickControlButton(window, control, "DiscardHeldInputButton");
                Frames.Pump();
                ClickControlButton(window, control, "MarkInputCleanButton");
                Frames.Pump();
            }
            Assert.Null(control.PendingInputRecovery);
            Assert.True(session.TryBeginCollabSubmission(session.Generation, out var cleanScope));
            cleanScope!.Dispose();
            Assert.Equal(laterSubmitted, pty.Written.Count(x => x == "\r"));

            for (var i = 0; i < laterSubmitted; i++) PublishDone(oldLink);
            Frames.Pump();

            // No dangling provisional or evicted turns remain on either the session or the coordinator bridge.
            Assert.Equal(0, CountCoordinatorBridgeTurns(coordinator, child.Id));
            List<long>? drained = null;
            session.DrainManualTurns(session.Generation, ref drained, out var evicted);
            Assert.Equal(0, evicted);
            Assert.True(drained is null || drained.Count == 0);
            Assert.False(session.TryConsumeLeadingManualTurn());

            await session.RestartAsync();
            Assert.NotEqual(oldLink, session.CurrentLinkId);
            var fresh = await DelegateAsync("fresh B after restart");
            PublishDone(oldLink);
            Frames.Pump();
            Assert.Equal(CollabTaskStatus.Running, TaskStatus(fresh));
            var freshPty = factory.Connections[^1];
            freshPty.EmitOutput("fresh answer\r\n");
            await WaitUntilAsync(() => session.ReadText().Contains("fresh answer", StringComparison.Ordinal), ct);
            PublishDone(session.CurrentLinkId);
            Assert.True(Frames.Until(() => TaskStatus(fresh) == CollabTaskStatus.Completed, milliseconds: 500));
        }
        finally
        {
            window.Close();
            Frames.Pump();
            await registry.CloseAllAsync();
        }
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(63, true)]
    [InlineData(64, true)]
    [InlineData(64, false)]
    [InlineData(65, false)]
    [InlineData(127, false)]
    [InlineData(128, false)]
    public async Task Rejected_unbracketed_multiline_paste_preserves_earlier_delegated_job_at_exact_boundaries(
        int submittedLines,
        bool duringWrite)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("octopus-rollback-paste-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(root);
        var parent = new ProjectItem { AgentId = "claude", Kind = ProjectItemKind.Agent, Title = "Lead", Surface = AgentSurface.Gui };
        var child = new ProjectItem { AgentId = "claude", Kind = ProjectItemKind.Agent, Title = "Console", Surface = AgentSurface.Terminal };
        projects.AddItem(project, parent);
        projects.AddItem(project, child);

        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        coordinator.RegisterGuiSession(new StubParentBridge(parent, root));

        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new RecordingTerminalFactory();
        var control = new TerminalControl { ConfirmMultilinePaste = false };
        var window = new Window { Width = 840, Height = 480, Content = control };
        window.Show();
        try
        {
            control.Start(DummySpec(), factory, registry, child.Id);
            await WaitUntilAsync(() => registry.Find(child.Id) is { IsRunning: true }, ct);
            var session = registry.Find(child.Id)!;
            var pty = factory.Connections.Single();
            var signals = AppHost.Get<ISignalHub>();

            void PublishDone(string? linkId)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                signals.Publish(new AgentSignal("claude", "stop", child.Id, "", now, now, "{}", linkId));
            }

            async Task<string> DelegateAsync(string goal)
            {
                var response = await coordinator.CallToolAsync(
                    parent.Id,
                    "delegate_task",
                    new JsonObject { ["target"] = child.Id, ["goal"] = goal },
                    ct);
                Assert.Null(response["isError"]);
                var id = JsonNode.Parse(response["content"]![0]!["text"]!.GetValue<string>())!["task_id"]!.GetValue<string>();
                Assert.Equal(CollabTaskStatus.Running, TaskStatus(id));
                return id;
            }

            CollabTaskStatus TaskStatus(string id) =>
                coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == id).Status;

            var taskB = await DelegateAsync("only-live-job-B");
            var oldLink = session.CurrentLinkId;

            using var router = CreateRouter(session);
            var cancelBridge = new TerminalCollabSession(child.Id, "claude", "Console", root, registry, activity: null);
            await TriggerCancelledPasteAsync(session, pty, cancelBridge, "first", () => router.SendKey(TerminalKey.C, KeyMods.Ctrl), ct);
            Frames.Pump();

            // Disable bracketed paste so clipboard paste sends raw multiline payload as one write.
            pty.EmitOutput("\x1b[?2004l");
            await WaitUntilAsync(() => !session.CachedInputModes.BracketedPaste, ct);

            var heldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pasteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rejectPaste = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string? attemptedPayload = null;
            pty.OnWrite = async data =>
            {
                if (data == "\u0003")
                {
                    heldStarted.TrySetResult();
                    await releaseHeld.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                }
                if (data.StartsWith("line0", StringComparison.Ordinal))
                {
                    attemptedPayload = data;
                    pasteStarted.TrySetResult();
                    await rejectPaste.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                    throw new IOException("no byte of raw clipboard paste accepted");
                }
            };

            var body = string.Concat(Enumerable.Range(0, submittedLines).Select(i => $"line{i}\n"));
            var statusOnOwnDone = CollabTaskStatus.Running;
            async Task FinishBAsync()
            {
                pty.EmitOutput("answer for the only live job\r\n");
                await WaitUntilAsync(() => session.ReadText().Contains("answer for the only live job", StringComparison.Ordinal), ct);
                PublishDone(oldLink);
                Frames.Until(() => TaskStatus(taskB) == CollabTaskStatus.Completed, milliseconds: 500);
                statusOnOwnDone = TaskStatus(taskB);
            }

            try
            {
                ClickControlButton(window, control, "SendHeldInputButton");
                await heldStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
                router.PasteText(body);
                releaseHeld.TrySetResult();
                await pasteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
                Assert.Equal(body, attemptedPayload!.Replace('\r', '\n'));
                Assert.False(session.TryBeginCollabSubmission(session.Generation, out _));
                if (duringWrite) await FinishBAsync();
            }
            finally
            {
                releaseHeld.TrySetResult();
                rejectPaste.TrySetResult();
            }

            await WaitUntilAsync(() => !session.TrySendCollabControl("", session.Generation), ct);
            if (!duringWrite) await FinishBAsync();

            Assert.Equal(CollabTaskStatus.Completed, statusOnOwnDone);
            Assert.DoesNotContain(pty.Written, data => data.StartsWith("line0", StringComparison.Ordinal));
            Assert.False(pty.Exited.IsCompleted);

            // Rollback must leave zero provisional or evicted turns at every clipboard size (63, 64, 65, 127, 128).
            Assert.Equal(0, CountCoordinatorBridgeTurns(coordinator, child.Id));
            List<long>? drained = null;
            session.DrainManualTurns(session.Generation, ref drained, out var evicted);
            Assert.Equal(0, evicted);
            Assert.True(drained is null || drained.Count == 0);
            Assert.False(session.TryConsumeLeadingManualTurn());
        }
        finally
        {
            window.Close();
            Frames.Pump();
            await registry.CloseAllAsync();
        }
    }

    private static int CountCoordinatorBridgeTurns(CollabCoordinator coordinator, string childItemId)
    {
        var sessions = (CollabSessions)typeof(CollabCoordinator)
            .GetField("_sessions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(coordinator)!;
        var bridge = (TerminalCollabSession)sessions.Find(childItemId)!;
        var turns = (System.Collections.IList)typeof(TerminalCollabSession)
            .GetField("_turns", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(bridge)!;
        return turns.Count;
    }
}
