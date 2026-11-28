using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Octopus.Agents;
using Octopus.App.Activity;
using Octopus.App.Bots;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Remote;
using Octopus.App.Services;
using Octopus.App.Terminals;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Bots;
using Octopus.Core.Terminals;
using Octopus.FileSystem;
using Octopus.Git;
using Octopus.Platform;
using Octopus.Storage;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using Xunit;

namespace Octopus.App.Tests;

public sealed class BotLifecycleCaptureTests
{
    private sealed class GatedDisposeTerminalConnection(
        TaskCompletionSource disposeStarted,
        TaskCompletionSource allowDispose) : ITerminalConnection
    {
        private readonly TaskCompletionSource<TerminalExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        public int ProcessId => 4243;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public Task<TerminalExit> Exited => _exit.Task;
        public Func<ValueTask<string?>>? QueryForeground { get; set; }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.WhenAny(Task.Delay(Timeout.Infinite, cancellationToken), _exit.Task).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight) => ValueTask.CompletedTask;
        public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;
        public ValueTask<string?> GetForegroundProcessNameAsync() => QueryForeground?.Invoke() ?? ValueTask.FromResult<string?>(null);

        public async ValueTask DisposeAsync()
        {
            disposeStarted.TrySetResult();
            await allowDispose.Task.ConfigureAwait(false);
            Interlocked.Increment(ref _disposeCount);
            _exit.TrySetResult(new TerminalExit(0, null));
        }
    }

    private static HostWorkspace CreateHost() => new(
        AppHost.Get<IProjectService>(),
        AppHost.Get<IItemLaunchService>(),
        AppHost.Get<ITerminalConnectionFactory>(),
        AppHost.Get<TerminalSessionRegistry>(),
        AppHost.Get<ITabContentRegistry>(),
        AppHost.Get<IItemActivityService>(),
        AppHost.Get<IAgentCatalog>(),
        AppHost.Get<IAgentScanner>(),
        AppHost.Get<ISettingsStore>(),
        AppHost.Get<IShellResolver>(),
        AppHost.Get<IFileTreeService>(),
        AppHost.Get<IGitService>(),
        AppHost.Get<PathComparer>(),
        bots: AppHost.Get<IBotService>());

    [AvaloniaTheory, FreshApp]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Second_host_close_or_delete_joins_retiring_teardown_in_progress(bool delete)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("CounterBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "count")!;
        var backing = bots.GetBackingProject(bot);
        var tabs = AppHost.Get<ITabContentRegistry>();
        var item = backing.Items.Single(i => i.Id == chat.Id);
        var view = Assert.IsType<TerminalTabView>(tabs.GetOrCreate(backing, item));

        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new GatedDisposeTerminalConnection(disposeStarted, allowDispose);
        AppHost.Get<FakeTerminalConnectionFactory>().StartOverride = (_, _, _) => connection;
        view.EnsureStarted(startWhenHidden: true);

        using var host = CreateHost();
        var first = host.CloseBotChatAsync(bot.Id, chat.Id);
        try
        {
            await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var second = delete
                ? host.DeleteBotChatAsync(bot.Id, chat.Id)
                : host.CloseBotChatAsync(bot.Id, chat.Id);

            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted, "Second host operation returned while the first fake process was still disposing.");
            Assert.Equal(0, connection.DisposeCount);

            allowDispose.TrySetResult();
            await second;
            Assert.Equal(1, connection.DisposeCount);
        }
        finally
        {
            allowDispose.TrySetResult();
            await first;
            await tabs.ReleaseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Reopened_terminal_bot_chat_starts_distinct_process_before_deferred_close()
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("TermBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "term")!;
        var tabs = AppHost.Get<ITabContentRegistry>();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        var fakeQueue = new Queue<Action>();

        using var dock = new BotDockController(
            bot,
            bots,
            tabs,
            new CloseConfirmation(tabs, AppHost.Get<IDialogService>()),
            new Border(),
            postBackground: fakeQueue.Enqueue);

        var oldView = Assert.IsType<TerminalTabView>(tabs.Find(chat.Id));
        oldView.EnsureStarted(startWhenHidden: true);
        var originalSession = sessions.Find(chat.Id);
        Assert.NotNull(originalSession);
        Assert.Single(factory.Started);

        // Close queues deferred disposal, then reopen starts a replacement terminal before the queued close runs.
        bots.CloseChat(bot.Id, chat.Id);
        Assert.Null(sessions.Find(chat.Id));

        bots.OpenChat(bot.Id, chat.Id);
        var replacementView = Assert.IsType<TerminalTabView>(tabs.Find(chat.Id));
        Assert.NotSame(oldView, replacementView);
        replacementView.EnsureStarted(startWhenHidden: true);
        var reopenedSession = sessions.Find(chat.Id);
        Assert.NotNull(reopenedSession);
        Assert.NotSame(originalSession, reopenedSession);
        Assert.Equal(2, factory.Started.Count);

        while (fakeQueue.TryDequeue(out var runDeferred))
        {
            runDeferred();
        }
        Frames.Pump();

        Assert.Same(replacementView, tabs.Find(chat.Id));
        Assert.True(factory.Connections[0].IsEnded);
        Assert.False(factory.Connections[1].IsEnded);
        Assert.Same(reopenedSession, sessions.Find(chat.Id));
        Assert.True(reopenedSession!.IsRunning);

        await tabs.ReleaseAllAsync();
        Assert.True(factory.Connections[1].IsEnded);
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_bot_close_or_delete_notification_failure_still_releases_captured_owner(bool delete)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("NotifyBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "notify")!;
        var backing = bots.GetBackingProject(bot);
        var item = backing.Items.Single(i => i.Id == chat.Id);
        var tabs = AppHost.Get<ITabContentRegistry>();
        var control = tabs.GetOrCreate(backing, item);
        Assert.IsType<TerminalTabView>(control).EnsureStarted(startWhenHidden: true);
        var connection = Assert.Single(AppHost.Get<FakeTerminalConnectionFactory>().Connections);

        using var host = CreateHost();
        void ThrowOnChatClose(object? sender, BotChangedEventArgs e)
        {
            if (e.Kind == BotChangeKind.Chats && e.Chat?.Id == chat.Id)
            {
                throw new InvalidOperationException("observer failed");
            }
        }

        bots.Changed += ThrowOnChatClose;
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                delete ? host.DeleteBotChatAsync(bot.Id, chat.Id) : host.CloseBotChatAsync(bot.Id, chat.Id));
            Assert.Equal("observer failed", ex.Message);
            Assert.DoesNotContain(backing.Items, i => i.Id == chat.Id);
            Assert.Null(tabs.Find(chat.Id));
            Assert.True(connection.IsEnded, "Fake process is still live after close/delete notification failed.");
            Assert.Null(control.DataContext);
        }
        finally
        {
            bots.Changed -= ThrowOnChatClose;
            await tabs.ReleaseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Dock_pane_close_notification_failure_still_schedules_captured_release()
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("DockNotifyBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "pane-close")!;
        var tabs = AppHost.Get<ITabContentRegistry>();
        var dialogs = AppHost.Get<IDialogService>();
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var fakeQueue = new Queue<Action>();

        using var dock = new BotDockController(
            bot,
            bots,
            tabs,
            new CloseConfirmation(tabs, dialogs),
            new Border(),
            postBackground: fakeQueue.Enqueue);

        var view = Assert.IsType<Octopus.App.AgentGui.AgentGuiTabView>(tabs.Find(chat.Id));
        var vm = Assert.IsType<Octopus.App.AgentGui.AgentGuiTabViewModel>(view.DataContext);
        await vm.ConnectAsync();
        var session = Assert.Single(fakeFactory.Created);
        Assert.True(session.IsConnected);
        Assert.Equal(chat.Id, dock.Layout.ActivePane?.Id);

        void ThrowOnClose(object? sender, BotChangedEventArgs e)
        {
            if (e.Kind == BotChangeKind.Chats && e.Chat?.Id == chat.Id)
            {
                throw new InvalidOperationException("dock observer failed");
            }
        }

        bots.Changed += ThrowOnClose;
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => dock.CloseActive());
            Assert.Equal("dock observer failed", ex.Message);
        }
        finally
        {
            bots.Changed -= ThrowOnClose;
        }

        Assert.Single(fakeQueue);
        while (fakeQueue.TryDequeue(out var runDeferred))
        {
            runDeferred();
        }
        Frames.Pump();

        Assert.Null(tabs.Find(chat.Id));
        Assert.False(session.IsConnected);
        Assert.Null(view.DataContext);
        await tabs.ReleaseAllAsync();
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Gui_bot_reopen_updates_visible_dock_pane_regardless_of_observer_order(bool observesFirst)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("GuiOrderBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "gui-order")!;
        var backing = bots.GetBackingProject(bot);
        var tabs = AppHost.Get<ITabContentRegistry>();

        Control? replacement = null;
        var reopened = false;
        void Reopen(object? sender, BotChangedEventArgs e)
        {
            if (reopened || e.Kind != BotChangeKind.Chats || e.Chat?.Id != chat.Id || e.Chat.IsOpen) return;
            reopened = true;
            bots.OpenChat(bot.Id, chat.Id);
            replacement = tabs.GetOrCreate(backing, backing.Items.Single(i => i.Id == chat.Id));
        }

        if (observesFirst) bots.Changed += Reopen;
        using var dock = new BotDockController(
            bot,
            bots,
            tabs,
            new CloseConfirmation(tabs, AppHost.Get<IDialogService>()),
            new Border());
        if (!observesFirst) bots.Changed += Reopen;

        using var host = CreateHost();
        try
        {
            await host.CloseBotChatAsync(bot.Id, chat.Id);
            Assert.NotNull(replacement);
            Assert.Same(replacement, tabs.Find(chat.Id));
            Assert.Equal(chat.Id, dock.Layout.ActivePane?.Id);
            Assert.Same(replacement, dock.Layout.ActivePane!.Content);
        }
        finally
        {
            bots.Changed -= Reopen;
            await tabs.ReleaseAllAsync();
        }
    }

    private sealed class DiscardingSessionSink : Octopus.Remote.Host.ISessionSink
    {
        public void Output(ulong position, ReadOnlySpan<byte> bytes) { }
        public void Resized(ulong position, int columns, int rows) { }
        public void Reset(ulong position) { }
        public void Exited(int? exitCode) { }
    }

    // New regression introduced on dc5410a: double-Take in HostWorkspace captured a replacement attached during Changed.
    [AvaloniaFact, FreshApp]
    public async Task Host_close_does_not_take_replacement_session_started_during_terminal_removal()
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("DoubleTakeBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "double-take")!;
        var backing = bots.GetBackingProject(bot);
        var item = backing.Items.Single(i => i.Id == chat.Id);
        var tabs = AppHost.Get<ITabContentRegistry>();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();

        var original = Assert.IsType<TerminalTabView>(tabs.GetOrCreate(backing, item));
        original.EnsureStarted(startWhenHidden: true);

        TerminalTabView? replacement = null;
        void AttachOnRetirement(string key)
        {
            if (key != chat.Id || sessions.Find(key) is not null || replacement is not null) return;
            replacement = Assert.IsType<TerminalTabView>(tabs.GetOrCreate(backing, item));
            replacement.EnsureStarted(startWhenHidden: true);
        }
        void Reopen(object? sender, BotChangedEventArgs e)
        {
            if (e.Kind == BotChangeKind.Chats && e.Chat?.Id == chat.Id && !e.Chat.IsOpen)
            {
                bots.OpenChat(bot.Id, chat.Id);
            }
        }

        using var host = CreateHost();
        sessions.Changed += AttachOnRetirement;
        bots.Changed += Reopen;
        try
        {
            await host.CloseBotChatAsync(bot.Id, chat.Id);
            Assert.NotNull(replacement);
            Assert.NotSame(original, replacement);
            Assert.Same(replacement, tabs.Find(chat.Id));
            Assert.Contains(backing.Items, i => i.Id == chat.Id);
            Assert.Equal(2, factory.Connections.Count);
            Assert.True(factory.Connections[0].IsEnded);
            Assert.False(factory.Connections[1].IsEnded, "Host close ended the replacement connection after the chat reopened.");
            Assert.True(sessions.Find(chat.Id)?.IsRunning);
        }
        finally
        {
            sessions.Changed -= AttachOnRetirement;
            bots.Changed -= Reopen;
            await tabs.ReleaseAllAsync();
        }
    }

    // Baseline failure: throwing TerminalSessionRegistry.Changed observer stranded the detached session beyond shutdown.
    [AvaloniaFact, FreshApp]
    public async Task Throwing_terminal_removal_observer_does_not_strand_bot_owner_at_shutdown()
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("ThrowTermBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "throw-term")!;
        var backing = bots.GetBackingProject(bot);
        var item = backing.Items.Single(i => i.Id == chat.Id);
        var tabs = AppHost.Get<ITabContentRegistry>();
        var sessions = AppHost.Get<TerminalSessionRegistry>();

        var view = Assert.IsType<TerminalTabView>(tabs.GetOrCreate(backing, item));
        view.EnsureStarted(startWhenHidden: true);
        var original = sessions.Find(chat.Id)!;
        var connection = Assert.Single(AppHost.Get<FakeTerminalConnectionFactory>().Connections);
        var failure = new InvalidOperationException("terminal removal observer");
        void OnChange(string id)
        {
            if (id == chat.Id && sessions.Find(id) is null) throw failure;
        }

        using var host = CreateHost();
        sessions.Changed += OnChange;
        try
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.CloseBotChatAsync(bot.Id, chat.Id));
            Assert.Same(failure, thrown);
            await tabs.ReleaseAllAsync();
            Assert.True(connection.IsEnded, "Fake process remained live after shutdown when terminal removal observer threw.");
        }
        finally
        {
            sessions.Changed -= OnChange;
            await original.DisposeAsync();
        }
    }

    // Baseline failure: left sidebar close/delete bypassed owner capture when an earlier Bots.Changed observer threw.
    [AvaloniaTheory, FreshApp]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Left_sidebar_close_or_delete_disposes_even_if_earlier_bot_observer_throws(bool delete)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("LeftSidebarThrowBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "left-throw")!;
        var backing = bots.GetBackingProject(bot);
        var tabs = AppHost.Get<ITabContentRegistry>();
        var panel = AppHost.Get<BotPanelViewModel>();
        var row = panel.BotNodes.SelectMany(b => b.Chats).Single(c => c.ChatId == chat.Id);

        var failure = new InvalidOperationException("earlier left sidebar observer");
        void Fail(object? sender, BotChangedEventArgs e)
        {
            if (e.Kind == BotChangeKind.Chats && e.Chat?.Id == chat.Id) throw failure;
        }

        bots.Changed += Fail;
        var queue = new Queue<Action>();
        using var dock = new BotDockController(
            bots.Bots.Single(b => b.Id == bot.Id),
            bots,
            tabs,
            new CloseConfirmation(tabs, AppHost.Get<IDialogService>()),
            new Border(),
            queue.Enqueue);
        var original = Assert.IsType<TerminalTabView>(tabs.Find(chat.Id));
        original.EnsureStarted(startWhenHidden: true);
        var conn = Assert.Single(AppHost.Get<FakeTerminalConnectionFactory>().Connections);

        try
        {
            var thrown = Assert.Throws<InvalidOperationException>(() =>
                (delete ? row.DeleteChatCommand : row.CloseChatCommand).Execute(null));
            Assert.Same(failure, thrown);
            while (queue.TryDequeue(out var action)) action();
            Frames.Pump();
            Assert.DoesNotContain(backing.Items, i => i.Id == chat.Id);
            Assert.Null(tabs.Find(chat.Id));
            Assert.True(conn.IsEnded, "Left sidebar command removed backing item but left its process live.");
        }
        finally
        {
            bots.Changed -= Fail;
            await tabs.ReleaseAllAsync();
        }
    }

    // Baseline failure: right sidebar close/delete bypassed owner capture when an earlier Bots.Changed observer threw.
    [AvaloniaTheory, FreshApp]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Right_sidebar_close_or_delete_disposes_even_if_earlier_bot_observer_throws(bool delete)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("RightSidebarThrowBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "right-throw")!;
        var backing = bots.GetBackingProject(bot);
        var tabs = AppHost.Get<ITabContentRegistry>();
        var rightPanel = AppHost.Get<BotRightPanelViewModel>();
        var row = rightPanel.FilteredChats.Single(c => c.ChatId == chat.Id);

        var failure = new InvalidOperationException("earlier right sidebar observer");
        void Fail(object? sender, BotChangedEventArgs e)
        {
            if (e.Kind == BotChangeKind.Chats && e.Chat?.Id == chat.Id) throw failure;
        }

        bots.Changed += Fail;
        var queue = new Queue<Action>();
        using var dock = new BotDockController(
            bots.Bots.Single(b => b.Id == bot.Id),
            bots,
            tabs,
            new CloseConfirmation(tabs, AppHost.Get<IDialogService>()),
            new Border(),
            queue.Enqueue);
        var original = Assert.IsType<TerminalTabView>(tabs.Find(chat.Id));
        original.EnsureStarted(startWhenHidden: true);
        var conn = Assert.Single(AppHost.Get<FakeTerminalConnectionFactory>().Connections);

        try
        {
            var thrown = Assert.Throws<InvalidOperationException>(() =>
                (delete ? row.DeleteChatCommand : row.CloseChatCommand).Execute(null));
            Assert.Same(failure, thrown);
            while (queue.TryDequeue(out var action)) action();
            Frames.Pump();
            Assert.DoesNotContain(backing.Items, i => i.Id == chat.Id);
            Assert.Null(tabs.Find(chat.Id));
            Assert.True(conn.IsEnded, "Right sidebar command removed backing item but left its process live.");
        }
        finally
        {
            bots.Changed -= Fail;
            await tabs.ReleaseAllAsync();
        }
    }

    // Baseline failure: left sidebar close/delete of headless inactive bot left hosted terminal session running.
    [AvaloniaTheory, FreshApp]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Left_sidebar_close_or_delete_of_headless_inactive_bot_ends_hosted_process(bool delete)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("InactiveBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "headless")!;
        var backing = bots.GetBackingProject(bot);
        bots.CreateBot(new NewBotRequest("OtherBot", "opencode", "#2f9e63", null, null));

        var workspace = AppHost.Get<WorkspaceViewModel>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var panel = AppHost.Get<BotPanelViewModel>();
        using var host = CreateHost();
        using var attachment = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
        Assert.Null(tabs.Find(chat.Id));
        var conn = Assert.Single(AppHost.Get<FakeTerminalConnectionFactory>().Connections);
        var row = panel.BotNodes.SelectMany(b => b.Chats).Single(c => c.ChatId == chat.Id);

        try
        {
            (delete ? row.DeleteChatCommand : row.CloseChatCommand).Execute(null);
            Frames.Pump();
            Assert.DoesNotContain(backing.Items, i => i.Id == chat.Id);
            Assert.True(conn.IsEnded, "Sidebar command removed inactive bot chat but left headless process running.");
        }
        finally
        {
            await workspace.CloseAllAsync();
        }
    }

    // Control: overlapping generations join closes, delete, and shared shutdown.
    [AvaloniaFact, FreshApp]
    public async Task Overlapping_generations_join_closes_delete_and_shared_shutdown()
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("OverlapBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "overlap")!;
        var backing = bots.GetBackingProject(bot);
        var tabs = AppHost.Get<ITabContentRegistry>();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();

        var gates = Enumerable.Range(0, 2).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var starts = Enumerable.Range(0, 2).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var index = 0;
        factory.StartOverride = (_, _, _) =>
        {
            var i = index++;
            return new GatedDisposeTerminalConnection(starts[i], gates[i]);
        };

        using var host = CreateHost();
        var firstView = Assert.IsType<TerminalTabView>(tabs.GetOrCreate(backing, backing.Items.Single(i => i.Id == chat.Id)));
        firstView.EnsureStarted(startWhenHidden: true);
        var oldSession = sessions.Find(chat.Id);
        var first = host.CloseBotChatAsync(bot.Id, chat.Id);
        try
        {
            await starts[0].Task.WaitAsync(TimeSpan.FromSeconds(3));
            bots.OpenChat(bot.Id, chat.Id);
            var secondView = Assert.IsType<TerminalTabView>(tabs.GetOrCreate(backing, backing.Items.Single(i => i.Id == chat.Id)));
            secondView.EnsureStarted(startWhenHidden: true);
            Assert.NotSame(oldSession, sessions.Find(chat.Id));

            var second = host.CloseBotChatAsync(bot.Id, chat.Id);
            await starts[1].Task.WaitAsync(TimeSpan.FromSeconds(3));
            var third = host.DeleteBotChatAsync(bot.Id, chat.Id);
            var shutdown1 = tabs.ReleaseAllAsync();
            var shutdown2 = tabs.ReleaseAllAsync();

            gates[1].TrySetResult();
            for (var i = 0; i < 20 && secondView.DataContext is not null; i++) await Task.Delay(5);
            Assert.Null(secondView.DataContext);
            foreach (var task in new[] { first, second, third, shutdown1, shutdown2 })
            {
                Assert.False(task.IsCompleted, "An operation completed before the oldest generation released.");
            }

            gates[0].TrySetResult();
            await Task.WhenAll(first, second, third, shutdown1, shutdown2).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Null(firstView.DataContext);
            Assert.Null(sessions.Find(chat.Id));
            Assert.Equal(2, index);
        }
        finally
        {
            foreach (var gate in gates) gate.TrySetResult();
            await tabs.ReleaseAllAsync();
        }
    }

    // Control: primary notification error survives a secondary view cleanup error.
    [AvaloniaFact, FreshApp]
    public async Task Primary_notification_error_survives_secondary_view_cleanup_error()
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("PrimaryErrorBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "primary-err")!;
        var backing = bots.GetBackingProject(bot);
        var tabs = AppHost.Get<ITabContentRegistry>();
        var view = Assert.IsType<TerminalTabView>(tabs.GetOrCreate(backing, backing.Items.Single(i => i.Id == chat.Id)));
        view.EnsureStarted(startWhenHidden: true);
        var conn = Assert.Single(AppHost.Get<FakeTerminalConnectionFactory>().Connections);

        var primary = new InvalidOperationException("bot-close notification");
        var secondaryHit = false;
        void BotFail(object? sender, BotChangedEventArgs e)
        {
            if (e.Kind == BotChangeKind.Chats && e.Chat?.Id == chat.Id) throw primary;
        }
        void ViewFail(object? sender, EventArgs e)
        {
            if (view.DataContext is null)
            {
                secondaryHit = true;
                throw new ArgumentException("view cleanup notification");
            }
        }

        using var host = CreateHost();
        bots.Changed += BotFail;
        view.DataContextChanged += ViewFail;
        try
        {
            var reported = await Assert.ThrowsAsync<InvalidOperationException>(() => host.CloseBotChatAsync(bot.Id, chat.Id));
            Assert.Same(primary, reported);
            Assert.True(secondaryHit);
            Assert.True(conn.IsEnded);
        }
        finally
        {
            view.DataContextChanged -= ViewFail;
            bots.Changed -= BotFail;
            await tabs.ReleaseAllAsync();
        }
    }

    // Regression #1 (left/right with earlyReopen=false) & shared failures (host active/inactive with earlyReopen=false):
    // reactive dock/workspace sync must not recapture a replacement session attached during initiating retirement.
    [AvaloniaTheory, FreshApp]
    [InlineData(false, false, "host")]
    [InlineData(false, true, "host")]
    [InlineData(true, false, "host")]
    [InlineData(true, true, "host")]
    [InlineData(false, false, "left")]
    [InlineData(false, true, "left")]
    [InlineData(false, false, "right")]
    [InlineData(false, true, "right")]
    public async Task Reattach_during_removal_survives_later_reopen_with_workspace(bool inactive, bool earlyReopen, string route)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("ReattachBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "reattach")!;
        var backing = bots.GetBackingProject(bot);
        if (inactive)
        {
            bots.CreateBot(new NewBotRequest("OtherBot", "opencode", "#2f9e63", null, null));
        }

        void Reopen(object? sender, BotChangedEventArgs e)
        {
            if (e.Kind == BotChangeKind.Chats && e.Chat?.Id == chat.Id && !e.Chat.IsOpen)
            {
                bots.OpenChat(bot.Id, chat.Id);
            }
        }

        if (earlyReopen) bots.Changed += Reopen;
        var workspace = AppHost.Get<WorkspaceViewModel>();
        if (!earlyReopen) bots.Changed += Reopen;

        using var host = CreateHost();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        using var first = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());

        Octopus.Remote.Host.ISessionAttachment? reattached = null;
        TerminalSession? replacement = null;
        FakeTerminalConnection? replacementProcess = null;
        var attaching = false;
        void OnRemoval(string key)
        {
            if (key != chat.Id || sessions.Find(key) is not null || attaching) return;
            attaching = true;
            reattached = host.AttachAsync(key, 80, 24, false, false, new DiscardingSessionSink()).GetAwaiter().GetResult();
            replacement = sessions.Find(key);
            replacementProcess = factory.Connections.Last();
        }

        sessions.Changed += OnRemoval;
        try
        {
            if (route == "host")
            {
                await host.CloseBotChatAsync(bot.Id, chat.Id);
            }
            else if (route == "left")
            {
                AppHost.Get<BotPanelViewModel>().BotNodes.SelectMany(b => b.Chats).Single(c => c.ChatId == chat.Id).CloseChatCommand.Execute(null);
            }
            else
            {
                AppHost.Get<BotRightPanelViewModel>().FilteredChats.Single(c => c.ChatId == chat.Id).CloseChatCommand.Execute(null);
            }

            Frames.Pump();
            await AppHost.Get<ITabContentRegistry>().WaitForReleaseAsync(chat.Id);
            Assert.Contains(backing.Items, i => i.Id == chat.Id);
            Assert.NotNull(replacement);
            Assert.True(factory.Connections[0].IsEnded);
            Assert.NotNull(replacementProcess);
            Assert.NotSame(factory.Connections[0], replacementProcess);
            Assert.False(replacementProcess.IsEnded, "Replacement connection ended while chat reopened.");
            Assert.True(replacement.IsRunning, "Replacement session stopped running after reopen.");
            Assert.Same(replacement, sessions.Find(chat.Id));
        }
        finally
        {
            sessions.Changed -= OnRemoval;
            bots.Changed -= Reopen;
            reattached?.Dispose();
            await workspace.CloseAllAsync();
        }
    }

    // Regression #2: workspace close must resolve the actual item owner after confirmation even if bot selection changes.
    [AvaloniaFact, FreshApp]
    public async Task Workspace_close_binds_actual_owner_when_selection_changes_during_confirmation()
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("OwnerBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "confirm")!;
        var backing = bots.GetBackingProject(bot);
        var other = bots.CreateBot(new NewBotRequest("OtherBot", "opencode", "#2f9e63", null, null));
        bots.SelectBot(bot.Id);

        var workspace = AppHost.Get<WorkspaceViewModel>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        allowDispose.SetResult();
        var connection = new GatedDisposeTerminalConnection(new(), allowDispose)
        {
            QueryForeground = () =>
            {
                checking.TrySetResult();
                return new ValueTask<string?>(answer.Task);
            },
        };
        AppHost.Get<FakeTerminalConnectionFactory>().StartOverride = (_, _, _) => connection;
        Assert.IsType<TerminalTabView>(tabs.Find(chat.Id)).EnsureStarted(startWhenHidden: true);
        var original = sessions.Find(chat.Id)!;

        try
        {
            var close = workspace.CloseItemAsync(chat.Id);
            await checking.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(close.IsCompleted);
            bots.SelectBot(other.Id);
            answer.SetResult(null);
            await close.WaitAsync(TimeSpan.FromSeconds(3));
            await tabs.WaitForReleaseAsync(chat.Id);
            Assert.DoesNotContain(backing.Items, i => i.Id == chat.Id);
            Assert.Null(sessions.Find(chat.Id));
            Assert.False(original.IsRunning);
        }
        finally
        {
            answer.TrySetResult(null);
            await workspace.CloseAllAsync();
        }
    }

    // Regression #2 (inactive headless chat): workspace close must resolve and close the actual inactive bot's backing item.
    [AvaloniaFact, FreshApp]
    public async Task Workspace_close_of_inactive_bot_chat_closes_backing_and_process_consistently()
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("InactiveOwnerBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "inactive-ws")!;
        var backing = bots.GetBackingProject(bot);
        bots.CreateBot(new NewBotRequest("SelectedBot", "opencode", "#2f9e63", null, null));

        var workspace = AppHost.Get<WorkspaceViewModel>();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        using var host = CreateHost();
        using var attachment = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
        var original = sessions.Find(chat.Id)!;

        try
        {
            await workspace.CloseItemAsync(chat.Id);
            Frames.Pump();
            Assert.DoesNotContain(backing.Items, i => i.Id == chat.Id);
            Assert.Null(sessions.Find(chat.Id));
            Assert.False(original.IsRunning);
        }
        finally
        {
            await workspace.CloseAllAsync();
        }
    }

    // Shared failures #3 & #4: direct dock close with synchronous reopen must reconcile the reopened pane in both observer orders.
    [AvaloniaTheory, FreshApp]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_dock_close_and_reopen_reconciles_reopened_pane(bool earlyReopen)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("DockReopenBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "dock-reopen")!;
        var backing = bots.GetBackingProject(bot);

        void Reopen(object? sender, BotChangedEventArgs e)
        {
            if (e.Kind == BotChangeKind.Chats && e.Chat?.Id == chat.Id && !e.Chat.IsOpen)
            {
                bots.OpenChat(bot.Id, chat.Id);
            }
        }

        if (earlyReopen) bots.Changed += Reopen;
        var workspace = AppHost.Get<WorkspaceViewModel>();
        if (!earlyReopen) bots.Changed += Reopen;
        var tabs = AppHost.Get<ITabContentRegistry>();
        var view = Assert.IsType<TerminalTabView>(tabs.Find(chat.Id));
        view.EnsureStarted(startWhenHidden: true);
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();

        try
        {
            workspace.CloseActiveTab();
            Frames.Pump();
            await tabs.WaitForReleaseAsync(chat.Id);
            Assert.Contains(backing.Items, i => i.Id == chat.Id);
            Assert.True(factory.Connections[0].IsEnded);
            var layout = Assert.IsType<TKCode.Arc.DockLayout>(workspace.CurrentLayout);
            Assert.NotNull(tabs.Find(chat.Id));
            Assert.Equal(chat.Id, layout.ActivePane?.Id);
            var reopened = Assert.IsType<TerminalTabView>(layout.ActivePane!.Content);
            Assert.Same(reopened, tabs.Find(chat.Id));
            Assert.NotSame(view, reopened);
            reopened.EnsureStarted(startWhenHidden: true);
            Assert.False(factory.Connections[1].IsEnded);
        }
        finally
        {
            bots.Changed -= Reopen;
            await workspace.CloseAllAsync();
        }
    }

    // Shared failure #5 (workspace) + controls (host-delete-bot, host-removal-error): async close routes await original disposal.
    [AvaloniaTheory, FreshApp]
    [InlineData("workspace")]
    [InlineData("host-delete-bot")]
    [InlineData("host-removal-error")]
    public async Task Async_close_waits_for_original_connection_disposal(string route)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("AsyncCloseBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "async-close")!;
        var workspace = AppHost.Get<WorkspaceViewModel>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        using var host = CreateHost();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var conn = new GatedDisposeTerminalConnection(started, gate);
        AppHost.Get<FakeTerminalConnectionFactory>().StartOverride = (_, _, _) => conn;
        Assert.IsType<TerminalTabView>(tabs.Find(chat.Id)).EnsureStarted(startWhenHidden: true);

        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var failure = new InvalidOperationException("removal notification");
        void ThrowOnRemoval(string id)
        {
            if (id == chat.Id && sessions.Find(id) is null) throw failure;
        }
        if (route == "host-removal-error") sessions.Changed += ThrowOnRemoval;
        var original = sessions.Find(chat.Id)!;

        try
        {
            var closing = route switch
            {
                "workspace" => workspace.CloseItemAsync(chat.Id),
                "host-delete-bot" => host.DeleteBotAsync(bot.Id),
                _ => host.CloseBotChatAsync(bot.Id, chat.Id),
            };
            Frames.Pump();
            if (closing.IsFaulted && !started.Task.IsCompleted)
            {
                Assert.Fail("Close failed before process disposal started.");
            }
            await started.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.False(conn.Exited.IsCompleted);
            Assert.False(closing.IsCompleted, "Async close returned while original connection disposal was gated.");
            gate.TrySetResult();
            if (route == "host-removal-error")
            {
                Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => closing));
            }
            else
            {
                await closing;
            }
            Assert.True(conn.Exited.IsCompletedSuccessfully);
        }
        finally
        {
            sessions.Changed -= ThrowOnRemoval;
            gate.TrySetResult();
            await original.DisposeAsync();
            await workspace.CloseAllAsync();
        }
    }

    // Finding #1 (c15d9c4): permanent chat/bot deletion must leave no replacement process alive for the deleted owner.
    [AvaloniaTheory, FreshApp]
    [InlineData("host", false)]
    [InlineData("host", true)]
    [InlineData("left", false)]
    [InlineData("left", true)]
    [InlineData("right", false)]
    [InlineData("host-bot", false)]
    [InlineData("host-bot", true)]
    public async Task Permanent_delete_disposes_any_replacement_attached_during_removal(string route, bool inactive)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("PermDeleteBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "perm-delete")!;
        bots.GetBackingProject(bot);
        if (inactive)
        {
            bots.CreateBot(new NewBotRequest("OtherBot", "opencode", "#2f9e63", null, null));
        }

        var workspace = AppHost.Get<WorkspaceViewModel>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        using var host = CreateHost();
        using var original = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());

        Octopus.Remote.Host.ISessionAttachment? second = null;
        var attempted = false;
        void AttachDuringRemoval(string id)
        {
            if (attempted || id != chat.Id || sessions.Find(id) is not null) return;
            attempted = true;
            try
            {
                second = host.AttachAsync(id, 80, 24, false, false, new DiscardingSessionSink()).GetAwaiter().GetResult();
            }
            catch (Octopus.Remote.Protocol.RemoteException ex) when (ex.Code == Octopus.Remote.Protocol.RemoteMethods.NotFound)
            {
            }
        }

        sessions.Changed += AttachDuringRemoval;
        try
        {
            switch (route)
            {
                case "left":
                    AppHost.Get<BotPanelViewModel>().BotNodes.SelectMany(b => b.Chats).Single(c => c.ChatId == chat.Id).DeleteChatCommand.Execute(null);
                    break;
                case "right":
                    AppHost.Get<BotRightPanelViewModel>().FilteredChats.Single(c => c.ChatId == chat.Id).DeleteChatCommand.Execute(null);
                    break;
                case "host-bot":
                    await host.DeleteBotAsync(bot.Id);
                    break;
                default:
                    await host.DeleteBotChatAsync(bot.Id, chat.Id);
                    break;
            }

            Frames.Pump();
            await tabs.WaitForReleaseAsync(chat.Id);
            Assert.DoesNotContain(bots.Bots.SelectMany(b => b.Chats), c => c.Id == chat.Id);
            Assert.True(factory.Connections[0].IsEnded, "Original process survived permanent deletion.");
            Assert.True(attempted, "Removal notification was not observed.");
            Assert.Null(sessions.Find(chat.Id));
            Assert.All(factory.Connections, c => Assert.True(c.IsEnded));
        }
        finally
        {
            sessions.Changed -= AttachDuringRemoval;
            second?.Dispose();
            await workspace.CloseAllAsync();
        }
    }

    // Finding #2 (c15d9c4 & baseline): workspace confirmation must bind to original owner and generation across await.
    [AvaloniaTheory, FreshApp]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Old_workspace_confirmation_preserves_separately_reopened_generation(bool switchSelection)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("GenConfirmBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "gen-confirm")!;
        var backing = bots.GetBackingProject(bot);
        var other = bots.CreateBot(new NewBotRequest("OtherBot", "opencode", "#2f9e63", null, null));
        bots.SelectBot(bot.Id);

        var workspace = AppHost.Get<WorkspaceViewModel>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        using var host = CreateHost();

        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        allowDispose.SetResult();
        var old = new GatedDisposeTerminalConnection(new(), allowDispose)
        {
            QueryForeground = () =>
            {
                checking.TrySetResult();
                return new ValueTask<string?>(answer.Task);
            },
        };
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        factory.StartOverride = (_, _, _) => old;
        Assert.IsType<TerminalTabView>(tabs.Find(chat.Id)).EnsureStarted(startWhenHidden: true);

        try
        {
            var pending = workspace.CloseItemAsync(chat.Id);
            await checking.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await host.CloseBotChatAsync(bot.Id, chat.Id);
            Assert.True(old.Exited.IsCompletedSuccessfully);

            factory.StartOverride = null;
            await host.OpenBotChatAsync(bot.Id, chat.Id);
            using var second = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
            var replacement = sessions.Find(chat.Id);
            Assert.True(second.IsRunning);
            if (switchSelection) bots.SelectBot(other.Id);

            answer.SetResult(null);
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Frames.Pump();
            await tabs.WaitForReleaseAsync(chat.Id);

            Assert.Contains(backing.Items, i => i.Id == chat.Id);
            var replacementConn = Assert.Single(factory.Connections);
            Assert.False(replacementConn.IsEnded, "Old workspace confirmation ended a newer reopened generation.");
            Assert.Same(replacement, sessions.Find(chat.Id));
        }
        finally
        {
            answer.TrySetResult(null);
            await workspace.CloseAllAsync();
        }
    }

    // Gated controls: reopening while original disposal is gated preserves the replacement across all five routes.
    [AvaloniaTheory, FreshApp]
    [InlineData("host")]
    [InlineData("workspace")]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("dock")]
    public async Task Reopen_while_original_disposal_is_gated_keeps_replacement(string route)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("GatedReopenBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "gated-reopen")!;
        var backing = bots.GetBackingProject(bot);
        var workspace = AppHost.Get<WorkspaceViewModel>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        using var host = CreateHost();

        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new GatedDisposeTerminalConnection(disposing, allow);
        factory.StartOverride = (_, _, _) => old;
        Assert.IsType<TerminalTabView>(tabs.Find(chat.Id)).EnsureStarted(startWhenHidden: true);
        var original = sessions.Find(chat.Id);

        try
        {
            Task? closing = null;
            switch (route)
            {
                case "host": closing = host.CloseBotChatAsync(bot.Id, chat.Id); break;
                case "workspace": closing = workspace.CloseItemAsync(chat.Id); break;
                case "left":
                    AppHost.Get<BotPanelViewModel>().BotNodes.SelectMany(b => b.Chats).Single(c => c.ChatId == chat.Id).CloseChatCommand.Execute(null);
                    break;
                case "right":
                    AppHost.Get<BotRightPanelViewModel>().FilteredChats.Single(c => c.ChatId == chat.Id).CloseChatCommand.Execute(null);
                    break;
                default: workspace.CloseActiveTab(); break;
            }

            Frames.Pump();
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.DoesNotContain(backing.Items, i => i.Id == chat.Id);
            Assert.False(old.Exited.IsCompleted);
            var returnedEarly = closing?.IsCompleted == true;

            factory.StartOverride = null;
            await host.OpenBotChatAsync(bot.Id, chat.Id);
            using var second = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
            var replacement = sessions.Find(chat.Id);
            Assert.NotNull(replacement);
            Assert.NotSame(original, replacement);
            var proc = Assert.Single(factory.Connections);

            allow.SetResult();
            if (closing is not null) await closing.WaitAsync(TimeSpan.FromSeconds(3));
            await tabs.WaitForReleaseAsync(chat.Id);
            Frames.Pump();

            Assert.True(old.Exited.IsCompletedSuccessfully);
            Assert.Same(replacement, sessions.Find(chat.Id));
            Assert.False(proc.IsEnded);
            Assert.True(replacement.IsRunning);
            var layout = Assert.IsType<TKCode.Arc.DockLayout>(workspace.CurrentLayout);
            Assert.Equal(chat.Id, layout.ActivePane?.Id);
            Assert.Same(tabs.Find(chat.Id), layout.ActivePane?.Content);
            Assert.False(returnedEarly, "Async close returned before original disposal finished.");
        }
        finally
        {
            allow.TrySetResult();
            await workspace.CloseAllAsync();
        }
    }

    // Finding #3 (c15d9c4): public sidebar constructors using default tabs=null must preserve dock cleanup fallback.
    [AvaloniaTheory, FreshApp]
    [InlineData("left")]
    [InlineData("right")]
    public async Task Sidebar_with_default_optional_registry_closes_visible_chat(string route)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("DefaultCtorBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "default-ctor")!;
        var backing = bots.GetBackingProject(bot);
        var workspace = AppHost.Get<WorkspaceViewModel>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        Assert.IsType<TerminalTabView>(tabs.Find(chat.Id)).EnsureStarted(startWhenHidden: true);
        var proc = Assert.Single(AppHost.Get<FakeTerminalConnectionFactory>().Connections);

        try
        {
            if (route == "left")
            {
                using var left = new BotPanelViewModel(
                    bots,
                    AppHost.Get<IDialogService>(),
                    AppHost.Get<IFileOpener>(),
                    AppHost.Get<Octopus.App.Shell.Keymap.IAppKeymap>());
                left.BotNodes.SelectMany(b => b.Chats).Single(c => c.ChatId == chat.Id).CloseChatCommand.Execute(null);
            }
            else
            {
                using var right = new BotRightPanelViewModel(
                    bots,
                    AppHost.Get<IDialogService>(),
                    AppHost.Get<IAgentCatalog>(),
                    AppHost.Get<IFileOpener>(),
                    AppHost.Get<Octopus.App.Explorer.Files.FileTreeViewModel>(),
                    AppHost.Get<Octopus.App.Explorer.Search.FileSearchViewModel>());
                right.FilteredChats.Single(c => c.ChatId == chat.Id).CloseChatCommand.Execute(null);
            }

            Frames.Pump();
            await tabs.WaitForReleaseAsync(chat.Id);
            Assert.DoesNotContain(backing.Items, i => i.Id == chat.Id);
            Assert.True(proc.IsEnded);
            Assert.Null(sessions.Find(chat.Id));
            Assert.Null(tabs.Find(chat.Id));
        }
        finally
        {
            await workspace.CloseAllAsync();
        }
    }

    private sealed class RejectNextBotStore : IBotStore
    {
        private readonly InMemoryBotStore _inner = new();
        public Exception? NextFailure { get; set; }
        public Octopus.Core.Bots.BotsDocument Current => _inner.Current;
        public event EventHandler? Changed { add => _inner.Changed += value; remove => _inner.Changed -= value; }
        public void Load() => _inner.Load();
        public void Flush() => _inner.Flush();
        public void Update(Action<Octopus.Core.Bots.BotsDocument> mutate)
        {
            if (NextFailure is { } error)
            {
                NextFailure = null;
                throw error;
            }
            _inner.Update(mutate);
        }
    }

    private static Task InvokeSync(Action action)
    {
        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    // Regression #1 (d338fa5): a delete rejected before store commit must not drain a replacement for the still-open chat.
    [AvaloniaTheory, FreshApp]
    [InlineData("host-chat")]
    [InlineData("host-bot")]
    [InlineData("left")]
    [InlineData("right")]
    public async Task Rejected_delete_preserves_replacement_of_still_open_chat(string route)
    {
        var originalOverrides = AppHost.ConfigureOverrides;
        var store = new RejectNextBotStore();
        AppHost.ConfigureOverrides = services =>
        {
            originalOverrides!(services);
            services.AddSingleton<IBotStore>(store);
        };
        AppHost.Reset();
        try
        {
            AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
            var bots = AppHost.Get<IBotService>();
            var bot = bots.CreateBot(new NewBotRequest("RejectDelBot", "opencode", "#2f9e63", null, null));
            var chat = bots.CreateChat(bot.Id, "opencode", "reject-del")!;
            bots.GetBackingProject(bot);
            var workspace = AppHost.Get<WorkspaceViewModel>();
            using var host = CreateHost();
            var sessions = AppHost.Get<TerminalSessionRegistry>();
            var tabs = AppHost.Get<ITabContentRegistry>();
            var factory = AppHost.Get<FakeTerminalConnectionFactory>();
            var failure = new IOException("Store rejected delete");

            using var first = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
            Octopus.Remote.Host.ISessionAttachment? second = null;
            TerminalSession? next = null;
            void Reattach(string id)
            {
                if (id != chat.Id || sessions.Find(id) is not null || second is not null) return;
                second = host.AttachAsync(id, 80, 24, false, false, new DiscardingSessionSink()).GetAwaiter().GetResult();
                next = sessions.Find(id);
                store.NextFailure = failure;
            }

            sessions.Changed += Reattach;
            try
            {
                var error = await Record.ExceptionAsync(() => route switch
                {
                    "host-chat" => host.DeleteBotChatAsync(bot.Id, chat.Id),
                    "host-bot" => host.DeleteBotAsync(bot.Id),
                    "left" => InvokeSync(() => AppHost.Get<BotPanelViewModel>().BotNodes.SelectMany(b => b.Chats).Single(c => c.ChatId == chat.Id).DeleteChatCommand.Execute(null)),
                    _ => InvokeSync(() => AppHost.Get<BotRightPanelViewModel>().FilteredChats.Single(c => c.ChatId == chat.Id).DeleteChatCommand.Execute(null)),
                });

                Assert.Same(failure, error);
                Assert.True(bots.Bots.Single(b => b.Id == bot.Id).Chats.Single(c => c.Id == chat.Id).IsOpen);
                Assert.Contains(bots.GetBackingProject(bots.Bots.Single(b => b.Id == bot.Id)).Items, i => i.Id == chat.Id);
                await tabs.WaitForReleaseAsync(chat.Id);
                Frames.Pump();
                Assert.Equal(2, factory.Connections.Count);
                Assert.True(factory.Connections[0].IsEnded);
                Assert.NotNull(next);
                Assert.False(factory.Connections[1].IsEnded, "Rejected delete ended replacement of a still-open chat.");
                Assert.Same(next, sessions.Find(chat.Id));
            }
            finally
            {
                sessions.Changed -= Reattach;
                second?.Dispose();
                await workspace.CloseAllAsync();
            }
        }
        finally
        {
            AppHost.ConfigureOverrides = originalOverrides;
        }
    }

    // Regression #2 (d338fa5): singleton tab registry must not retain deleted chat IDs after retirement/shutdown.
    [AvaloniaFact, FreshApp]
    public async Task Deleted_chat_ids_do_not_accumulate_in_singleton_tab_registry()
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("ChurnBot", "opencode", "#2f9e63", null, null));
        var workspace = AppHost.Get<WorkspaceViewModel>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        using var host = CreateHost();

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 32; i++)
        {
            var chat = bots.CreateChat(bot.Id, "opencode", $"churn-{i}")!;
            using var attachment = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
            ids.Add(chat.Id);
            await host.DeleteBotChatAsync(bot.Id, chat.Id);
            Assert.Null(sessions.Find(chat.Id));
            Assert.Null(tabs.Find(chat.Id));
        }

        await workspace.CloseAllAsync();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        Assert.Equal(32, factory.Connections.Count);
        Assert.All(factory.Connections, c => Assert.True(c.IsEnded));
        var retained = tabs.GetType()
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Where(field => field.GetValue(tabs) is System.Collections.IDictionary)
            .Select(field => (field.Name, Count: ((System.Collections.IDictionary)field.GetValue(tabs)!).Keys.OfType<string>().Count(ids.Contains)))
            .Where(pair => pair.Count > 0)
            .ToArray();
        Assert.Empty(retained);
    }

    // Shared behavior pair #1: in-place session restart across confirmation must not be closed by the stale check.
    [AvaloniaTheory, FreshApp]
    [InlineData("workspace", true)]
    [InlineData("dock", true)]
    [InlineData("workspace", false)]
    [InlineData("dock", false)]
    public async Task Old_process_check_does_not_close_restarted_successor(string route, bool restartInPlace)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("RestartGenBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "restart-gen")!;
        var backing = bots.GetBackingProject(bot);
        var workspace = AppHost.Get<WorkspaceViewModel>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        using var host = CreateHost();

        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var immediate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        immediate.SetResult();
        var old = new GatedDisposeTerminalConnection(new(), immediate)
        {
            QueryForeground = () =>
            {
                checking.TrySetResult();
                return new ValueTask<string?>(answer.Task);
            },
        };
        factory.StartOverride = (_, _, _) => old;
        Assert.IsType<TerminalTabView>(tabs.Find(chat.Id)).EnsureStarted(startWhenHidden: true);
        var original = Assert.IsType<TerminalSession>(sessions.Find(chat.Id));
        var firstGen = original.Generation;

        try
        {
            Task? pending = null;
            if (route == "workspace") pending = workspace.CloseItemAsync(chat.Id);
            else workspace.CloseActiveTab();
            await checking.Task.WaitAsync(TimeSpan.FromSeconds(4));

            factory.StartOverride = null;
            if (restartInPlace)
            {
                await host.RestartAsync(chat.Id);
            }
            else
            {
                await host.CloseBotChatAsync(bot.Id, chat.Id);
                await host.OpenBotChatAsync(bot.Id, chat.Id);
            }

            using var attachment = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
            var current = Assert.IsType<TerminalSession>(sessions.Find(chat.Id));
            Assert.True(old.Exited.IsCompletedSuccessfully && attachment.IsRunning);
            Assert.True(restartInPlace ? ReferenceEquals(current, original) && current.Generation > firstGen : !ReferenceEquals(current, original));
            var successor = Assert.Single(factory.Connections);

            answer.SetResult(null);
            if (pending is not null) await pending.WaitAsync(TimeSpan.FromSeconds(4));
            await Task.Delay(30);
            Frames.Pump();
            await tabs.WaitForReleaseAsync(chat.Id);

            Assert.Contains(backing.Items, i => i.Id == chat.Id);
            Assert.False(successor.IsEnded, "Stale process check closed the restarted/reopened successor.");
            Assert.Same(current, sessions.Find(chat.Id));
        }
        finally
        {
            answer.TrySetResult(null);
            await workspace.CloseAllAsync();
        }
    }

    // Shared behavior pair #2: optional-registry sidebar close with early reopen retires original and preserves replacement.
    [AvaloniaTheory, FreshApp]
    [InlineData("left", true)]
    [InlineData("left", false)]
    [InlineData("right", true)]
    [InlineData("right", false)]
    public async Task Optional_registry_reopen_retires_original_and_preserves_next(string route, bool reopenBeforeDock)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("OptReopenBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "opt-reopen")!;
        var backing = bots.GetBackingProject(bot);

        void Reopen(object? sender, BotChangedEventArgs e)
        {
            if (e.Kind == BotChangeKind.Chats && e.Chat?.Id == chat.Id && !e.Chat.IsOpen)
            {
                bots.OpenChat(bot.Id, chat.Id);
            }
        }

        if (reopenBeforeDock) bots.Changed += Reopen;
        var workspace = AppHost.Get<WorkspaceViewModel>();
        if (!reopenBeforeDock) bots.Changed += Reopen;

        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        using var host = CreateHost();
        using var first = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
        var original = Assert.IsType<TerminalSession>(sessions.Find(chat.Id));
        using var left = new BotPanelViewModel(
            bots,
            AppHost.Get<IDialogService>(),
            AppHost.Get<IFileOpener>(),
            AppHost.Get<Octopus.App.Shell.Keymap.IAppKeymap>());
        using var right = new BotRightPanelViewModel(
            bots,
            AppHost.Get<IDialogService>(),
            AppHost.Get<IAgentCatalog>(),
            AppHost.Get<IFileOpener>(),
            AppHost.Get<Octopus.App.Explorer.Files.FileTreeViewModel>(),
            AppHost.Get<Octopus.App.Explorer.Search.FileSearchViewModel>());

        try
        {
            if (route == "left")
            {
                left.BotNodes.SelectMany(b => b.Chats).Single(c => c.ChatId == chat.Id).CloseChatCommand.Execute(null);
            }
            else
            {
                right.FilteredChats.Single(c => c.ChatId == chat.Id).CloseChatCommand.Execute(null);
            }

            Frames.Pump();
            await tabs.WaitForReleaseAsync(chat.Id);
            Assert.Contains(backing.Items, i => i.Id == chat.Id);
            using var second = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
            var current = sessions.Find(chat.Id);
            Assert.True(factory.Connections[0].IsEnded);
            Assert.Equal(2, factory.Connections.Count);
            Assert.True(current?.IsRunning);
            Assert.NotSame(original, current);
        }
        finally
        {
            bots.Changed -= Reopen;
            await workspace.CloseAllAsync();
        }
    }

    // Regression #1 (4684697): valid first attachment on a newly created chat must stay live across host and both sidebars.
    [AvaloniaTheory, FreshApp]
    [InlineData("host", true)]
    [InlineData("host", false)]
    [InlineData("left", true)]
    [InlineData("left", false)]
    [InlineData("right", true)]
    [InlineData("right", false)]
    public async Task First_attachment_on_new_chat_stays_live(string route, bool attachBeforeDock)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("CreateAttachBot", "opencode", "#2f9e63", null, null));
        bots.CreateChat(bot.Id, "opencode", "seed");
        var backing = bots.GetBackingProject(bot);
        using var host = CreateHost();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        var tabs = AppHost.Get<ITabContentRegistry>();

        Octopus.Remote.Host.ISessionAttachment? attachment = null;
        TerminalSession? owner = null;
        string? createdId = null;
        void OnCreate(object? sender, BotChangedEventArgs e)
        {
            if (e.Kind != BotChangeKind.Chats || e.Chat is not { IsOpen: true } chat || createdId is not null) return;
            createdId = chat.Id;
            attachment = host.AttachAsync(createdId, 80, 24, false, false, new DiscardingSessionSink()).GetAwaiter().GetResult();
            owner = sessions.Find(createdId);
            Assert.True(attachment.IsRunning);
        }

        if (attachBeforeDock) bots.Changed += OnCreate;
        var workspace = AppHost.Get<WorkspaceViewModel>();
        if (!attachBeforeDock) bots.Changed += OnCreate;
        try
        {
            if (route == "host")
            {
                await host.CreateBotChatAsync(new() { BotId = bot.Id, AgentId = "opencode", Title = "fresh" });
            }
            else if (route == "left")
            {
                AppHost.Get<BotPanelViewModel>().BotNodes.Single(b => b.BotId == bot.Id).NewChatCommand.Execute(null);
            }
            else
            {
                AppHost.Get<BotRightPanelViewModel>().NewChatCommand.Execute(null);
            }

            Assert.NotNull(createdId);
            Assert.NotNull(attachment);
            Assert.NotNull(owner);
            await tabs.WaitForReleaseAsync(createdId);
            Frames.Pump();
            Assert.Contains(bots.Bots.Single(b => b.Id == bot.Id).Chats, c => c.Id == createdId && c.IsOpen);
            Assert.Contains(backing.Items, i => i.Id == createdId);
            var connection = Assert.Single(factory.Connections);
            attachment.Input("hello"u8);
            var wrote = connection.IsEnded ? connection.Written.Contains("hello") : Frames.Until(() => connection.Written.Contains("hello"));
            Assert.False(connection.IsEnded, "Newly created chat's first attachment was ended.");
            Assert.True(wrote, "Input through the first attachment did not reach the process.");
            Assert.Same(owner, sessions.Find(createdId));
        }
        finally
        {
            bots.Changed -= OnCreate;
            attachment?.Dispose();
            await workspace.CloseAllAsync();
        }
    }

    // Regression #2 (4684697): optional-registry sidebar close must not kill a successor already restarted before dock observes close.
    [AvaloniaTheory, FreshApp]
    [InlineData("left")]
    [InlineData("right")]
    public async Task Early_reopen_preserves_process_restarted_before_dock_observes_close(string route)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("OptRestartBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "opt-restart")!;
        var backing = bots.GetBackingProject(bot);
        using var host = CreateHost();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        var tabs = AppHost.Get<ITabContentRegistry>();

        long originalGeneration = 0;
        long newGeneration = 0;
        TerminalSession? successor = null;
        void OnClose(object? sender, BotChangedEventArgs e)
        {
            if (e.Kind != BotChangeKind.Chats || e.Chat?.Id != chat.Id || e.Chat.IsOpen) return;
            host.OpenBotChatAsync(bot.Id, chat.Id).GetAwaiter().GetResult();
            host.RestartAsync(chat.Id).GetAwaiter().GetResult();
            successor = sessions.Find(chat.Id);
            newGeneration = successor!.Generation;
            Assert.True(successor.IsRunning && newGeneration > originalGeneration);
        }

        bots.Changed += OnClose;
        var workspace = AppHost.Get<WorkspaceViewModel>();
        using var initial = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
        originalGeneration = sessions.Find(chat.Id)!.Generation;
        using var left = new BotPanelViewModel(
            bots,
            AppHost.Get<IDialogService>(),
            AppHost.Get<IFileOpener>(),
            AppHost.Get<Octopus.App.Shell.Keymap.IAppKeymap>());
        using var right = new BotRightPanelViewModel(
            bots,
            AppHost.Get<IDialogService>(),
            AppHost.Get<IAgentCatalog>(),
            AppHost.Get<IFileOpener>(),
            AppHost.Get<Octopus.App.Explorer.Files.FileTreeViewModel>(),
            AppHost.Get<Octopus.App.Explorer.Search.FileSearchViewModel>());

        try
        {
            if (route == "left")
            {
                left.BotNodes.Single(b => b.BotId == bot.Id).Chats.Single(c => c.ChatId == chat.Id).CloseChatCommand.Execute(null);
            }
            else
            {
                right.FilteredChats.Single(c => c.ChatId == chat.Id).CloseChatCommand.Execute(null);
            }

            await tabs.WaitForReleaseAsync(chat.Id);
            Frames.Pump();
            Assert.Contains(backing.Items, i => i.Id == chat.Id);
            Assert.Equal(2, factory.Connections.Count);
            Assert.True(factory.Connections[0].IsEnded);
            Assert.False(factory.Connections[1].IsEnded, "Dock retired the already-restarted generation 2 successor.");
            Assert.Same(successor, sessions.Find(chat.Id));
        }
        finally
        {
            bots.Changed -= OnClose;
            await workspace.CloseAllAsync();
        }
    }

    // Confirmation handle lifetime: release pending confirmation tracking once approved, before slow disposal completes.
    [AvaloniaFact, FreshApp]
    public async Task Approved_workspace_close_releases_confirmation_tracking_before_slow_disposal()
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("ConfirmLifetimeBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "confirm-life")!;
        var backing = bots.GetBackingProject(bot);
        var workspace = AppHost.Get<WorkspaceViewModel>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        var tabs = AppHost.Get<ITabContentRegistry>();

        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new GatedDisposeTerminalConnection(disposing, gate)
        {
            QueryForeground = () => new ValueTask<string?>(answer.Task),
        };
        factory.StartOverride = (_, _, _) => old;
        Assert.IsType<TerminalTabView>(tabs.Find(chat.Id)).EnsureStarted(startWhenHidden: true);

        static int PendingCount(ITabContentRegistry registry)
        {
            var field = registry.GetType().GetField("_pendingConfirmations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return field?.GetValue(registry) is System.Collections.IDictionary dict ? dict.Count : 0;
        }

        try
        {
            var close = workspace.CloseItemAsync(chat.Id);
            Assert.Equal(1, PendingCount(tabs));
            answer.SetResult(null);
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.DoesNotContain(backing.Items, i => i.Id == chat.Id);
            Assert.False(close.IsCompleted);
            Assert.Equal(0, PendingCount(tabs));
            gate.SetResult();
            await close.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(old.Exited.IsCompletedSuccessfully);
            Assert.Equal(0, PendingCount(tabs));
        }
        finally
        {
            answer.TrySetResult(null);
            gate.TrySetResult();
            await workspace.CloseAllAsync();
        }
    }

    // Regression #1 (e1b5366): repeating a default-sidebar close while a synchronous store observer reopens the
    // already-closed chat must not manufacture a removal or capture the distinct replacement.
    [AvaloniaTheory, FreshApp]
    [InlineData("left", true)]
    [InlineData("right", true)]
    [InlineData("left", false)]
    [InlineData("right", false)]
    public async Task Default_sidebar_close_preserves_store_observer_reopen_on_first_and_repeated_close(
        string route,
        bool repeatedClose)
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("StoreReopenBot", "opencode", "#2f9e63", null, null));
        var chat = bots.CreateChat(bot.Id, "opencode", "store-reopen")!;
        var backing = bots.GetBackingProject(bot);
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        var store = AppHost.Get<IBotStore>();
        using var host = CreateHost();

        var armed = false;
        var sawRemovalEvent = false;
        long? eventGeneration = null;
        object? eventSession = null;
        TerminalSession? successor = null;
        Octopus.Remote.Host.ISessionAttachment? replacement = null;

        void OnStoreChanged(object? sender, EventArgs e)
        {
            if (!armed || chat.IsOpen) return;
            armed = false;
            host.OpenBotChatAsync(bot.Id, chat.Id).GetAwaiter().GetResult();
            if (sessions.Find(chat.Id) is { IsRunning: true })
            {
                host.RestartAsync(chat.Id).GetAwaiter().GetResult();
            }
            replacement = host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink()).GetAwaiter().GetResult();
            successor = sessions.Find(chat.Id);
            Assert.True(successor?.IsRunning);
        }

        void OnBotsChanged(object? sender, BotChangedEventArgs e)
        {
            if (e.Kind != BotChangeKind.Chats || e.Chat?.Id != chat.Id || !e.IsRemoved) return;
            sawRemovalEvent = true;
            var snapshot = e.GetType()
                .GetProperty("SupersededOwner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                .GetValue(e);
            eventGeneration = (long?)snapshot?.GetType().GetProperty("SessionGeneration")?.GetValue(snapshot);
            eventSession = snapshot?.GetType().GetProperty("Session")?.GetValue(snapshot);
        }

        bots.Changed += OnBotsChanged;
        var workspace = AppHost.Get<WorkspaceViewModel>();
        using var first = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
        var original = Assert.IsType<TerminalSession>(sessions.Find(chat.Id));
        var originalGeneration = original.Generation;
        using var left = new BotPanelViewModel(
            bots,
            AppHost.Get<IDialogService>(),
            AppHost.Get<IFileOpener>(),
            AppHost.Get<Octopus.App.Shell.Keymap.IAppKeymap>());
        using var right = new BotRightPanelViewModel(
            bots,
            AppHost.Get<IDialogService>(),
            AppHost.Get<IAgentCatalog>(),
            AppHost.Get<IFileOpener>(),
            AppHost.Get<Octopus.App.Explorer.Files.FileTreeViewModel>(),
            AppHost.Get<Octopus.App.Explorer.Search.FileSearchViewModel>());

        void ExecuteSidebarClose()
        {
            if (route == "left")
            {
                left.BotNodes.Single(b => b.BotId == bot.Id).Chats.Single(c => c.ChatId == chat.Id).CloseChatCommand.Execute(null);
            }
            else
            {
                right.FilteredChats.Single(c => c.ChatId == chat.Id).CloseChatCommand.Execute(null);
            }
        }

        try
        {
            if (repeatedClose)
            {
                ExecuteSidebarClose();
                Frames.Pump();
                await tabs.WaitForReleaseAsync(chat.Id);
                Assert.False(chat.IsOpen);
                Assert.Null(sessions.Find(chat.Id));
                Assert.DoesNotContain(backing.Items, i => i.Id == chat.Id);
                sawRemovalEvent = false;
                eventGeneration = null;
                eventSession = null;
            }

            armed = true;
            store.Changed += OnStoreChanged;
            ExecuteSidebarClose();
            Frames.Pump();
            await tabs.WaitForReleaseAsync(chat.Id);

            Assert.True(chat.IsOpen);
            Assert.Contains(backing.Items, i => i.Id == chat.Id);
            Assert.NotNull(replacement);
            Assert.NotNull(successor);
            Assert.Equal(2, factory.Connections.Count);
            Assert.True(factory.Connections[0].IsEnded);
            var successorConnection = factory.Connections[1];
            replacement.Input("r9-reopened"u8);
            var wrote = !successorConnection.IsEnded && Frames.Until(() => successorConnection.Written.Contains("r9-reopened"));
            Assert.False(successorConnection.IsEnded, "Sidebar close retired the replacement created during the store callback.");
            Assert.True(wrote, "Input through the reopened attachment did not reach the replacement process.");
            Assert.Same(successor, sessions.Find(chat.Id));

            if (repeatedClose)
            {
                Assert.False(sawRemovalEvent, "Repeating close on an already-closed, ownerless chat manufactured IsRemoved=true.");
                Assert.NotSame(original, successor);
            }
            else
            {
                Assert.True(sawRemovalEvent);
                Assert.Same(original, eventSession);
                Assert.Equal(originalGeneration, eventGeneration);
                Assert.True(successor.Generation > originalGeneration);
            }
        }
        finally
        {
            bots.Changed -= OnBotsChanged;
            store.Changed -= OnStoreChanged;
            replacement?.Dispose();
            await workspace.CloseAllAsync();
        }
    }

    // DeleteChat ordering: pre-commit store rejection must leave the original backing ProjectItem untouched,
    // and post-commit observer failure must clean up backing state and live owners while preserving the primary error.
    [AvaloniaTheory, FreshApp]
    [InlineData("host", true)]
    [InlineData("left", true)]
    [InlineData("right", true)]
    [InlineData("host", false)]
    [InlineData("left", false)]
    [InlineData("right", false)]
    public async Task Delete_chat_aligns_backing_project_and_cleanup_with_store_commit(string route, bool rejectBeforeCommit)
    {
        var originalOverrides = AppHost.ConfigureOverrides;
        var store = new RejectNextBotStore();
        AppHost.ConfigureOverrides = services =>
        {
            originalOverrides!(services);
            services.AddSingleton<IBotStore>(store);
        };
        AppHost.Reset();
        try
        {
            AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
            var bots = AppHost.Get<IBotService>();
            var bot = bots.CreateBot(new NewBotRequest("DeleteCommitBot", "opencode", "#2f9e63", null, null));
            var chat = bots.CreateChat(bot.Id, "opencode", "delete-commit")!;
            var backing = bots.GetBackingProject(bot);
            var originalItem = backing.Items.Single(i => i.Id == chat.Id);
            var workspace = AppHost.Get<WorkspaceViewModel>();
            using var host = CreateHost();
            var sessions = AppHost.Get<TerminalSessionRegistry>();
            var tabs = AppHost.Get<ITabContentRegistry>();
            var factory = AppHost.Get<FakeTerminalConnectionFactory>();
            var primary = new IOException(rejectBeforeCommit ? "Store rejected DeleteChat before commit" : "Store observer failed after DeleteChat commit");

            using var first = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
            Octopus.Remote.Host.ISessionAttachment? second = null;
            TerminalSession? replacement = null;

            void Reattach(string id)
            {
                if (id != chat.Id || sessions.Find(id) is not null || second is not null) return;
                second = host.AttachAsync(id, 80, 24, false, false, new DiscardingSessionSink()).GetAwaiter().GetResult();
                replacement = sessions.Find(id);
                if (rejectBeforeCommit) store.NextFailure = primary;
            }

            void OnStoreChanged(object? sender, EventArgs e)
            {
                if (!rejectBeforeCommit
                    && store.Current.Bots.FirstOrDefault(b => b.Id == bot.Id)?.Chats.All(c => c.Id != chat.Id) == true)
                {
                    throw primary;
                }
            }

            sessions.Changed += Reattach;
            store.Changed += OnStoreChanged;
            try
            {
                var error = await Record.ExceptionAsync(() => route switch
                {
                    "host" => host.DeleteBotChatAsync(bot.Id, chat.Id),
                    "left" => InvokeSync(() => AppHost.Get<BotPanelViewModel>().BotNodes.Single(b => b.BotId == bot.Id).Chats.Single(c => c.ChatId == chat.Id).DeleteChatCommand.Execute(null)),
                    _ => InvokeSync(() => AppHost.Get<BotRightPanelViewModel>().FilteredChats.Single(c => c.ChatId == chat.Id).DeleteChatCommand.Execute(null)),
                });

                Assert.Same(primary, error);
                await tabs.WaitForReleaseAsync(chat.Id);
                Frames.Pump();
                Assert.Equal(2, factory.Connections.Count);
                Assert.True(factory.Connections[0].IsEnded);
                Assert.NotNull(replacement);

                if (rejectBeforeCommit)
                {
                    Assert.Contains(store.Current.Bots.Single(b => b.Id == bot.Id).Chats, c => c.Id == chat.Id && c.IsOpen);
                    var currentItem = Assert.Single(backing.Items, i => i.Id == chat.Id);
                    Assert.Same(originalItem, currentItem);
                    Assert.False(factory.Connections[1].IsEnded);
                    Assert.Same(replacement, sessions.Find(chat.Id));
                }
                else
                {
                    Assert.DoesNotContain(store.Current.Bots.Single(b => b.Id == bot.Id).Chats, c => c.Id == chat.Id);
                    Assert.DoesNotContain(backing.Items, i => i.Id == chat.Id);
                    Assert.True(factory.Connections[1].IsEnded);
                    Assert.Null(sessions.Find(chat.Id));
                }
            }
            finally
            {
                store.Changed -= OnStoreChanged;
                sessions.Changed -= Reattach;
                second?.Dispose();
                await workspace.CloseAllAsync();
            }
        }
        finally
        {
            AppHost.ConfigureOverrides = originalOverrides;
        }
    }

    // Regression (1f2836f): host attach during committed close must reopen the authoritative closed chat even when
    // a cached backing item is still present, preserving the distinct successor, open UI, and persisted JsonBotStore record.
    [AvaloniaTheory, FreshApp]
    [InlineData("host", false)]
    [InlineData("host", true)]
    [InlineData("left", false)]
    [InlineData("left", true)]
    [InlineData("right", false)]
    [InlineData("right", true)]
    public async Task Host_attach_during_or_after_close_reopens_and_persists_chat_in_json_store(
        string route,
        bool duringClose)
    {
        var originalOverrides = AppHost.ConfigureOverrides;
        AppHost.ConfigureOverrides = services =>
        {
            originalOverrides!(services);
            services.AddSingleton<IBotStore, JsonBotStore>();
        };
        AppHost.Reset();
        try
        {
            AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
            var bots = AppHost.Get<IBotService>();
            var bot = bots.CreateBot(new NewBotRequest("JsonReopenBot", "opencode", "#2f9e63", null, null));
            var chat = bots.CreateChat(bot.Id, "opencode", "json-reopen")!;
            var backing = bots.GetBackingProject(bot);
            var workspace = AppHost.Get<WorkspaceViewModel>();
            var tabs = AppHost.Get<ITabContentRegistry>();
            var sessions = AppHost.Get<TerminalSessionRegistry>();
            var factory = AppHost.Get<FakeTerminalConnectionFactory>();
            var store = Assert.IsType<JsonBotStore>(AppHost.Get<IBotStore>());
            using var host = CreateHost();

            using var first = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
            var original = Assert.IsType<TerminalSession>(sessions.Find(chat.Id));
            var armed = duringClose;
            Octopus.Remote.Host.ISessionAttachment? replacement = null;

            void OnStoreChanged(object? sender, EventArgs e)
            {
                if (!armed || chat.IsOpen) return;
                armed = false;
                replacement = host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink()).GetAwaiter().GetResult();
            }

            store.Changed += OnStoreChanged;
            try
            {
                if (route == "host")
                {
                    await host.CloseBotChatAsync(bot.Id, chat.Id);
                }
                else if (route == "left")
                {
                    AppHost.Get<BotPanelViewModel>().BotNodes.Single(b => b.BotId == bot.Id).Chats.Single(c => c.ChatId == chat.Id).CloseChatCommand.Execute(null);
                }
                else
                {
                    AppHost.Get<BotRightPanelViewModel>().FilteredChats.Single(c => c.ChatId == chat.Id).CloseChatCommand.Execute(null);
                }

                Frames.Pump();
                await tabs.WaitForReleaseAsync(chat.Id);

                if (!duringClose)
                {
                    Assert.False(chat.IsOpen);
                    replacement = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
                    Frames.Pump();
                }

                Assert.NotNull(replacement);
                Assert.Equal(2, factory.Connections.Count);
                Assert.True(factory.Connections[0].IsEnded);
                var successor = Assert.IsType<TerminalSession>(sessions.Find(chat.Id));
                Assert.NotSame(original, successor);
                Assert.True(successor.IsRunning);

                replacement.Input("r10-persisted"u8);
                Assert.True(Frames.Until(() => factory.Connections[1].Written.Contains("r10-persisted")));
                Assert.True(chat.IsOpen, "Reattached chat remained closed in memory.");
                Assert.Contains(backing.Items, i => i.Id == chat.Id);

                store.Flush();
                using var reloaded = new JsonBotStore(AppHost.Get<IAppPaths>());
                var persisted = reloaded.Current.Bots.Single(b => b.Id == bot.Id).Chats.Single(c => c.Id == chat.Id);
                Assert.True(persisted.IsOpen, "Reattached chat was persisted as closed on disk.");
            }
            finally
            {
                store.Changed -= OnStoreChanged;
                replacement?.Dispose();
                await workspace.CloseAllAsync();
                await tabs.ReleaseAsync(chat.Id);
            }
        }
        finally
        {
            AppHost.ConfigureOverrides = originalOverrides;
        }
    }

    // Passive host lookup control: a passive read (UploadAsync) during committed close must not implicitly reopen the closing chat.
    [AvaloniaFact, FreshApp]
    public async Task Passive_host_read_during_close_does_not_reopen_closing_bot_chat()
    {
        var originalOverrides = AppHost.ConfigureOverrides;
        AppHost.ConfigureOverrides = services =>
        {
            originalOverrides!(services);
            services.AddSingleton<IBotStore, JsonBotStore>();
        };
        AppHost.Reset();
        try
        {
            AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
            var bots = AppHost.Get<IBotService>();
            var bot = bots.CreateBot(new NewBotRequest("PassiveReadBot", "opencode", "#2f9e63", null, null));
            var chat = bots.CreateChat(bot.Id, "opencode", "passive-read")!;
            var backing = bots.GetBackingProject(bot);
            var workspace = AppHost.Get<WorkspaceViewModel>();
            var tabs = AppHost.Get<ITabContentRegistry>();
            var sessions = AppHost.Get<TerminalSessionRegistry>();
            var factory = AppHost.Get<FakeTerminalConnectionFactory>();
            var store = Assert.IsType<JsonBotStore>(AppHost.Get<IBotStore>());
            using var host = CreateHost();

            using var first = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
            var armed = true;
            string? uploadedPath = null;

            void OnStoreChanged(object? sender, EventArgs e)
            {
                if (!armed || chat.IsOpen) return;
                armed = false;
                uploadedPath = host.UploadAsync(chat.Id, "r10passive", "shot.png", [1, 2, 3], last: true).GetAwaiter().GetResult();
            }

            store.Changed += OnStoreChanged;
            try
            {
                await host.CloseBotChatAsync(bot.Id, chat.Id);
                Frames.Pump();
                await tabs.WaitForReleaseAsync(chat.Id);

                Assert.NotNull(uploadedPath);
                Assert.False(chat.IsOpen, "Passive UploadAsync read unexpectedly reopened the closing bot chat.");
                Assert.DoesNotContain(backing.Items, i => i.Id == chat.Id);
                Assert.Null(sessions.Find(chat.Id));
                Assert.True(Assert.Single(factory.Connections).IsEnded);

                store.Flush();
                using var reloaded = new JsonBotStore(AppHost.Get<IAppPaths>());
                var persisted = reloaded.Current.Bots.Single(b => b.Id == bot.Id).Chats.Single(c => c.Id == chat.Id);
                Assert.False(persisted.IsOpen);
            }
            finally
            {
                store.Changed -= OnStoreChanged;
                if (uploadedPath is not null && File.Exists(uploadedPath))
                {
                    File.Delete(uploadedPath);
                }
                await workspace.CloseAllAsync();
                await tabs.ReleaseAsync(chat.Id);
            }
        }
        finally
        {
            AppHost.ConfigureOverrides = originalOverrides;
        }
    }

    // Blocker #1 (ed9b998): wrong-surface host requests (terminal attach on GUI chat, GUI attach/prompt on terminal chat)
    // must reject with NotAllowed BEFORE reopening, leaving a closing or closed chat closed in memory, backing, and disk.
    [AvaloniaTheory, FreshApp]
    [InlineData("terminal-on-gui", true)]
    [InlineData("terminal-on-gui", false)]
    [InlineData("gui-on-terminal", true)]
    [InlineData("gui-on-terminal", false)]
    [InlineData("prompt-on-terminal", true)]
    [InlineData("prompt-on-terminal", false)]
    public async Task Wrong_surface_host_request_leaves_closed_bot_chat_closed_in_json_store(
        string mode,
        bool duringClose)
    {
        var originalOverrides = AppHost.ConfigureOverrides;
        AppHost.ConfigureOverrides = services =>
        {
            originalOverrides!(services);
            services.AddSingleton<IBotStore, JsonBotStore>();
        };
        AppHost.Reset();
        try
        {
            var chatIsTerminal = mode != "terminal-on-gui";
            AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = chatIsTerminal ? AgentSurface.Terminal : AgentSurface.Gui);
            var bots = AppHost.Get<IBotService>();
            var bot = bots.CreateBot(new NewBotRequest("WrongSurfaceBot", "opencode", "#2f9e63", null, null));
            var chat = bots.CreateChat(bot.Id, "opencode", "wrong-surface")!;
            var backing = bots.GetBackingProject(bot);
            var store = Assert.IsType<JsonBotStore>(AppHost.Get<IBotStore>());
            var workspace = AppHost.Get<WorkspaceViewModel>();
            var tabs = AppHost.Get<ITabContentRegistry>();
            var sessions = AppHost.Get<TerminalSessionRegistry>();
            var terminals = AppHost.Get<FakeTerminalConnectionFactory>();
            var acps = AppHost.Get<FakeAcpSessionFactory>();
            using var host = CreateHost();

            using IDisposable first = chatIsTerminal
                ? await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink())
                : await host.AttachConversationAsync(chat.Id, _ => { });
            if (!chatIsTerminal)
            {
                Assert.True(Frames.Until(() => acps.Created.Count == 1 && acps.Created[0].IsConnected));
            }

            Task? wrongRequest = null;
            var armed = duringClose;
            Task InvokeWrongSurface() => mode switch
            {
                "terminal-on-gui" => host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink()),
                "gui-on-terminal" => host.AttachConversationAsync(chat.Id, _ => { }),
                _ => host.PromptAsync(new() { ItemId = chat.Id, Text = "wrong-surface-prompt" }),
            };

            void OnStoreChanged(object? sender, EventArgs e)
            {
                if (!armed || chat.IsOpen) return;
                armed = false;
                wrongRequest = InvokeWrongSurface();
            }

            store.Changed += OnStoreChanged;
            try
            {
                await host.CloseBotChatAsync(bot.Id, chat.Id);
                if (!duringClose)
                {
                    wrongRequest = InvokeWrongSurface();
                }

                var ex = await Record.ExceptionAsync(() => wrongRequest!);
                var remote = Assert.IsType<Octopus.Remote.Protocol.RemoteException>(ex);
                Assert.Equal(Octopus.Remote.Protocol.RemoteMethods.NotAllowed, remote.Code);

                Frames.Pump();
                await tabs.WaitForReleaseAsync(chat.Id);
                store.Flush();
                Assert.Null(store.LastError);

                using var disk = new JsonBotStore(AppHost.Get<IAppPaths>());
                var persisted = disk.Current.Bots.Single(b => b.Id == bot.Id).Chats.Single(c => c.Id == chat.Id);
                Assert.False(chat.IsOpen, "Wrong-surface rejection left chat open in memory.");
                Assert.Equal(BotChatStatus.Closed, chat.Status);
                Assert.False(persisted.IsOpen, "Wrong-surface rejection persisted chat open on disk.");
                Assert.Equal(BotChatStatus.Closed, persisted.Status);
                Assert.DoesNotContain(backing.Items, i => i.Id == chat.Id);
                Assert.Null(sessions.Find(chat.Id));
                if (chatIsTerminal)
                {
                    Assert.Single(terminals.Connections);
                    Assert.True(terminals.Connections[0].IsEnded);
                    Assert.Empty(acps.Created);
                }
                else
                {
                    Assert.Single(acps.Created);
                    Assert.False(acps.Created[0].IsConnected);
                    Assert.Empty(terminals.Connections);
                }
            }
            finally
            {
                store.Changed -= OnStoreChanged;
                await workspace.CloseAllAsync();
                await tabs.ReleaseAllAsync();
            }
        }
        finally
        {
            AppHost.ConfigureOverrides = originalOverrides;
        }
    }

    // Blocker #2 (ed9b998): an intervening close or delete committed during OpenChat's store update must prevail across
    // terminal attach, GUI attach, and direct GUI prompt without leaving a live successor, backing item, or prompt delivery.
    [AvaloniaTheory, FreshApp]
    [InlineData("terminal", "reclose-during")]
    [InlineData("gui", "reclose-during")]
    [InlineData("prompt", "reclose-during")]
    [InlineData("terminal", "reclose-after")]
    [InlineData("gui", "reclose-after")]
    [InlineData("prompt", "reclose-after")]
    [InlineData("terminal", "sidebar-reclose")]
    [InlineData("gui", "sidebar-reclose")]
    [InlineData("prompt", "sidebar-reclose")]
    [InlineData("terminal", "delete-during")]
    [InlineData("gui", "delete-during")]
    [InlineData("prompt", "delete-during")]
    [InlineData("terminal", "delete-after")]
    [InlineData("gui", "delete-after")]
    [InlineData("prompt", "delete-after")]
    public async Task Intervening_close_or_delete_during_open_chat_prevails_without_successor(
        string surface,
        string scenario)
    {
        var originalOverrides = AppHost.ConfigureOverrides;
        AppHost.ConfigureOverrides = services =>
        {
            originalOverrides!(services);
            services.AddSingleton<IBotStore, JsonBotStore>();
        };
        AppHost.Reset();
        try
        {
            var isTerminal = surface == "terminal";
            AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = isTerminal ? AgentSurface.Terminal : AgentSurface.Gui);
            var bots = AppHost.Get<IBotService>();
            var bot = bots.CreateBot(new NewBotRequest("ReenterOpenBot", "opencode", "#2f9e63", null, null));
            var chat = bots.CreateChat(bot.Id, "opencode", "reenter-open")!;
            var backing = bots.GetBackingProject(bot);
            var store = Assert.IsType<JsonBotStore>(AppHost.Get<IBotStore>());
            var workspace = AppHost.Get<WorkspaceViewModel>();
            var tabs = AppHost.Get<ITabContentRegistry>();
            var sessions = AppHost.Get<TerminalSessionRegistry>();
            var terminals = AppHost.Get<FakeTerminalConnectionFactory>();
            var acps = AppHost.Get<FakeAcpSessionFactory>();
            using var host = CreateHost();

            using IDisposable first = isTerminal
                ? await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink())
                : await host.AttachConversationAsync(chat.Id, _ => { });
            if (!isTerminal)
            {
                Assert.True(Frames.Until(() => acps.Created.Count == 1 && acps.Created[0].IsConnected));
            }

            var isAfter = scenario.EndsWith("-after", StringComparison.Ordinal);
            var isDelete = scenario.StartsWith("delete-", StringComparison.Ordinal);
            var isSidebar = scenario == "sidebar-reclose";
            var armedOuter = !isAfter;
            var nestedRan = false;
            Task? activeTask = null;
            Octopus.Remote.Host.ISessionAttachment? nextTerm = null;
            Octopus.Remote.Host.IAcpAttachment? nextGui = null;

            async Task RunActive()
            {
                if (surface == "terminal")
                {
                    nextTerm = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
                }
                else if (surface == "gui")
                {
                    nextGui = await host.AttachConversationAsync(chat.Id, _ => { });
                }
                else
                {
                    await host.PromptAsync(new() { ItemId = chat.Id, Text = "r11-blocked-prompt" });
                }
            }

            void OnStoreChanged(object? sender, EventArgs e)
            {
                if (armedOuter && !chat.IsOpen)
                {
                    armedOuter = false;
                    activeTask = RunActive();
                }
                else if (!nestedRan && chat.IsOpen)
                {
                    nestedRan = true;
                    if (isSidebar)
                    {
                        AppHost.Get<BotPanelViewModel>().BotNodes.Single(b => b.BotId == bot.Id).Chats.Single(c => c.ChatId == chat.Id).CloseChatCommand.Execute(null);
                    }
                    else if (isDelete)
                    {
                        bots.DeleteChat(bot.Id, chat.Id);
                    }
                    else
                    {
                        bots.CloseChat(bot.Id, chat.Id);
                    }
                }
            }

            store.Changed += OnStoreChanged;
            try
            {
                await host.CloseBotChatAsync(bot.Id, chat.Id);
                if (isAfter)
                {
                    activeTask = RunActive();
                }

                var ex = await Record.ExceptionAsync(() => activeTask!);
                var remote = Assert.IsType<Octopus.Remote.Protocol.RemoteException>(ex);
                Assert.Equal(Octopus.Remote.Protocol.RemoteMethods.NotFound, remote.Code);
                Assert.True(nestedRan);

                Frames.Pump();
                await tabs.WaitForReleaseAsync(chat.Id);
                store.Flush();
                Assert.Null(store.LastError);

                using var disk = new JsonBotStore(AppHost.Get<IAppPaths>());
                var liveChat = store.Current.Bots.Single(b => b.Id == bot.Id).Chats.FirstOrDefault(c => c.Id == chat.Id);
                var persisted = disk.Current.Bots.Single(b => b.Id == bot.Id).Chats.FirstOrDefault(c => c.Id == chat.Id);
                if (isDelete)
                {
                    Assert.Null(liveChat);
                    Assert.Null(persisted);
                }
                else
                {
                    Assert.NotNull(liveChat);
                    Assert.NotNull(persisted);
                    Assert.False(liveChat.IsOpen);
                    Assert.Equal(BotChatStatus.Closed, liveChat.Status);
                    Assert.False(persisted.IsOpen);
                    Assert.Equal(BotChatStatus.Closed, persisted.Status);
                }

                Assert.DoesNotContain(backing.Items, i => i.Id == chat.Id);
                Assert.Null(sessions.Find(chat.Id));
                if (isTerminal)
                {
                    Assert.Single(terminals.Connections);
                    Assert.True(terminals.Connections[0].IsEnded);
                }
                else
                {
                    Assert.Single(acps.Created);
                    Assert.False(acps.Created[0].IsConnected);
                    Assert.DoesNotContain(acps.Created[0].SentPrompts, p => p.Text == "r11-blocked-prompt");
                }
            }
            finally
            {
                store.Changed -= OnStoreChanged;
                nextTerm?.Dispose();
                nextGui?.Dispose();
                await workspace.CloseAllAsync();
                await tabs.ReleaseAllAsync();
            }
        }
        finally
        {
            AppHost.ConfigureOverrides = originalOverrides;
        }
    }

    // Normal-entry controls: same-surface GUI attach and direct GUI prompt during/after close reopen and persist open
    // in production JsonBotStore, deliver prompts/updates only to the distinct successor, and retire the original.
    [AvaloniaTheory, FreshApp]
    [InlineData("gui", true)]
    [InlineData("gui", false)]
    [InlineData("prompt", true)]
    [InlineData("prompt", false)]
    public async Task Same_surface_gui_attach_and_prompt_reopen_and_deliver_to_distinct_successor(
        string surface,
        bool duringClose)
    {
        var originalOverrides = AppHost.ConfigureOverrides;
        AppHost.ConfigureOverrides = services =>
        {
            originalOverrides!(services);
            services.AddSingleton<IBotStore, JsonBotStore>();
        };
        AppHost.Reset();
        try
        {
            AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);
            var bots = AppHost.Get<IBotService>();
            var bot = bots.CreateBot(new NewBotRequest("GuiReopenBot", "opencode", "#2f9e63", null, null));
            var chat = bots.CreateChat(bot.Id, "opencode", "gui-reopen")!;
            var backing = bots.GetBackingProject(bot);
            var store = Assert.IsType<JsonBotStore>(AppHost.Get<IBotStore>());
            var workspace = AppHost.Get<WorkspaceViewModel>();
            var tabs = AppHost.Get<ITabContentRegistry>();
            var acps = AppHost.Get<FakeAcpSessionFactory>();
            var mirrorUpdates = new List<Octopus.Remote.Protocol.AcpUpdateMessage>();
            using var host = CreateHost();

            using var first = await host.AttachConversationAsync(chat.Id, _ => { });
            var firstTab = tabs.Find(chat.Id);
            Assert.True(Frames.Until(() => acps.Created.Count == 1 && acps.Created[0].IsConnected));

            var armed = duringClose;
            Task? activeTask = null;
            Octopus.Remote.Host.IAcpAttachment? mirror = null;

            async Task RunActive()
            {
                if (surface == "gui")
                {
                    mirror = await host.AttachConversationAsync(chat.Id, mirrorUpdates.Add);
                }
                else
                {
                    await host.PromptAsync(new() { ItemId = chat.Id, Text = "r11-gui-prompt" });
                }
            }

            void OnStoreChanged(object? sender, EventArgs e)
            {
                if (!armed || chat.IsOpen) return;
                armed = false;
                activeTask = RunActive();
            }

            store.Changed += OnStoreChanged;
            try
            {
                await host.CloseBotChatAsync(bot.Id, chat.Id);
                if (!duringClose)
                {
                    Assert.False(chat.IsOpen);
                    activeTask = RunActive();
                }

                await activeTask!;
                Frames.Pump();
                await tabs.WaitForReleaseAsync(chat.Id);

                if (surface == "gui")
                {
                    Assert.NotNull(mirror);
                    Assert.True(Frames.Until(() => acps.Created.Count == 2 && acps.Created[1].IsConnected));
                    await host.PromptAsync(new() { ItemId = chat.Id, Text = "r11-gui-prompt" });
                    Assert.True(Frames.Until(() => mirrorUpdates.Any(u => u.Upserts.Any(e => e.Text?.Contains("r11-gui-prompt") == true)), milliseconds: 700));
                }

                Assert.True(Frames.Until(() => acps.Created.Count == 2 && acps.Created[1].SentPrompts.Any(p => p.Text == "r11-gui-prompt")));
                Assert.False(acps.Created[0].IsConnected);
                Assert.DoesNotContain(acps.Created[0].SentPrompts, p => p.Text == "r11-gui-prompt");
                Assert.True(acps.Created[1].IsConnected);
                Assert.NotSame(firstTab, tabs.Find(chat.Id));
                Assert.Contains(backing.Items, i => i.Id == chat.Id);
                Assert.True(chat.IsOpen);
                Assert.NotEqual(BotChatStatus.Closed, chat.Status);

                store.Flush();
                Assert.Null(store.LastError);
                using var disk = new JsonBotStore(AppHost.Get<IAppPaths>());
                var persisted = disk.Current.Bots.Single(b => b.Id == bot.Id).Chats.Single(c => c.Id == chat.Id);
                Assert.True(persisted.IsOpen);
                Assert.NotEqual(BotChatStatus.Closed, persisted.Status);
            }
            finally
            {
                store.Changed -= OnStoreChanged;
                mirror?.Dispose();
                await workspace.CloseAllAsync();
                await tabs.ReleaseAllAsync();
            }
        }
        finally
        {
            AppHost.ConfigureOverrides = originalOverrides;
        }
    }

    // Changed-owner interleaving: if a store observer during OpenChat transfers the chat ID to another bot owner,
    // HostWorkspace.AttachAsync resolves and launches under the latest authoritative owner rather than the stale bot.
    [AvaloniaFact, FreshApp]
    public async Task Host_attach_resolves_latest_owner_when_reparented_during_open_chat()
    {
        var originalOverrides = AppHost.ConfigureOverrides;
        AppHost.ConfigureOverrides = services =>
        {
            originalOverrides!(services);
            services.AddSingleton<IBotStore, JsonBotStore>();
        };
        AppHost.Reset();
        try
        {
            AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Terminal);
            var bots = AppHost.Get<IBotService>();
            var botA = bots.CreateBot(new NewBotRequest("OwnerA", "opencode", "#2f9e63", null, null));
            var botB = bots.CreateBot(new NewBotRequest("OwnerB", "opencode", "#3b82f6", null, null));
            var chat = bots.CreateChat(botA.Id, "opencode", "moving-chat")!;
            var backingA = bots.GetBackingProject(botA);
            var backingB = bots.GetBackingProject(botB);
            var store = Assert.IsType<JsonBotStore>(AppHost.Get<IBotStore>());
            var workspace = AppHost.Get<WorkspaceViewModel>();
            var tabs = AppHost.Get<ITabContentRegistry>();
            var sessions = AppHost.Get<TerminalSessionRegistry>();
            var terminals = AppHost.Get<FakeTerminalConnectionFactory>();
            using var host = CreateHost();

            using var first = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
            await host.CloseBotChatAsync(botA.Id, chat.Id);
            Frames.Pump();
            await tabs.WaitForReleaseAsync(chat.Id);
            Assert.False(chat.IsOpen);

            var transferred = false;
            void OnStoreChanged(object? sender, EventArgs e)
            {
                if (transferred || !chat.IsOpen) return;
                transferred = true;
                store.Update(d =>
                {
                    var from = d.Bots.FirstOrDefault(b => b.Id == botA.Id);
                    var to = d.Bots.FirstOrDefault(b => b.Id == botB.Id);
                    var moving = from?.Chats.FirstOrDefault(c => c.Id == chat.Id);
                    if (from is null || to is null || moving is null) return;
                    from.Chats.Remove(moving);
                    moving.IsOpen = true;
                    moving.Status = BotChatStatus.Idle;
                    to.Chats.Add(moving);
                });
            }

            store.Changed += OnStoreChanged;
            Octopus.Remote.Host.ISessionAttachment? next = null;
            try
            {
                next = await host.AttachAsync(chat.Id, 80, 24, false, false, new DiscardingSessionSink());
                Frames.Pump();
                Assert.True(transferred);
                Assert.Equal(2, terminals.Connections.Count);
                Assert.True(terminals.Connections[0].IsEnded);
                next.Input("r11-moved"u8);
                Assert.True(Frames.Until(() => terminals.Connections[1].Written.Contains("r11-moved")));
                Assert.DoesNotContain(backingA.Items, i => i.Id == chat.Id);
                Assert.Contains(backingB.Items, i => i.Id == chat.Id);
                Assert.True(sessions.Find(chat.Id)?.IsRunning);

                store.Flush();
                Assert.Null(store.LastError);
                using var disk = new JsonBotStore(AppHost.Get<IAppPaths>());
                Assert.DoesNotContain(disk.Current.Bots.Single(b => b.Id == botA.Id).Chats, c => c.Id == chat.Id);
                var movedPersisted = disk.Current.Bots.Single(b => b.Id == botB.Id).Chats.Single(c => c.Id == chat.Id);
                Assert.True(movedPersisted.IsOpen);
            }
            finally
            {
                store.Changed -= OnStoreChanged;
                next?.Dispose();
                await workspace.CloseAllAsync();
                await tabs.ReleaseAllAsync();
            }
        }
        finally
        {
            AppHost.ConfigureOverrides = originalOverrides;
        }
    }
}
