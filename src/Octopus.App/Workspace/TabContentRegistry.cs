using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Octopus.Acp.Dialects;
using Octopus.Acp.Process;
using Octopus.Agents;
using Octopus.Agents.Accounts;
using Octopus.App.Activity;
using Octopus.App.AgentGui;
using Octopus.App.Files;
using Octopus.App.Plugins;
using Octopus.App.Services;
using Octopus.App.Terminals;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.FileSystem;
using Octopus.Git;
using Octopus.Storage;
using Octopus.Terminal;

namespace Octopus.App.Workspace;

/// <summary>Creates the control behind each tab (a terminal or a file preview) once and ends it when the item is closed.</summary>
public sealed class TabContentRegistry(
    IItemLaunchService launcher,
    ITerminalConnectionFactory factory,
    ISettingsStore settings,
    IGitService git,
    IFileTreeService files,
    IDialogService dialogs,
    IServiceProvider services,
    Octopus.Terminal.Avalonia.TerminalSessionRegistry? sessions = null,
    Octopus.App.Remote.RemoteProjects? remote = null) : ITabContentRegistry
{
    private readonly Dictionary<string, Control> _controls = [];
    private readonly Dictionary<string, List<Task>> _releasing = [];
    private readonly HashSet<Task> _activeReleases = [];
    private readonly Dictionary<string, List<Func<Task>>> _pendingCapturedReleases = [];
    private readonly Dictionary<string, List<PendingConfirmation>> _pendingConfirmations = new(StringComparer.Ordinal);
    /// <summary>
    /// Agent conversations whose tab was closed but which keep running: the view model (and its session) is kept, the
    /// view is let go. <see cref="GetOrCreate"/> puts a new view over it when the tab opens again.
    /// </summary>
    private readonly Dictionary<string, AgentGuiTabViewModel> _detached = [];

    /// <summary>
    /// Bounded tracking handle for an in-flight close confirmation. Registered only while the confirmation awaits and
    /// removed on disposal or shutdown; invalidated immediately if the item's owner retires or its terminal session
    /// restarts in place.
    /// </summary>
    internal sealed class PendingConfirmation : IDisposable
    {
        private readonly TabContentRegistry _owner;
        private readonly string _itemId;
        private readonly Control? _originalControl;
        private readonly AgentGuiTabViewModel? _originalDetached;
        private readonly Octopus.Terminal.Avalonia.TerminalSession? _originalSession;
        private readonly long? _originalSessionGeneration;
        private bool _invalidated;
        private bool _disposed;

        internal PendingConfirmation(
            TabContentRegistry owner,
            string itemId,
            Control? originalControl,
            AgentGuiTabViewModel? originalDetached,
            Octopus.Terminal.Avalonia.TerminalSession? originalSession)
        {
            _owner = owner;
            _itemId = itemId;
            _originalControl = originalControl;
            _originalDetached = originalDetached;
            _originalSession = originalSession;
            _originalSessionGeneration = originalSession?.Generation;
        }

        internal void Invalidate() => _invalidated = true;

        internal bool IsStillCurrent()
        {
            if (_invalidated)
            {
                return false;
            }
            var currentControl = _owner._controls.GetValueOrDefault(_itemId);
            var currentDetached = _owner._detached.GetValueOrDefault(_itemId);
            var currentSession = _owner.FindSession(_itemId);
            if (_originalControl is not null && !ReferenceEquals(currentControl, _originalControl))
            {
                if (_originalControl is AgentGuiTabView { DataContext: AgentGuiTabViewModel vm } && ReferenceEquals(currentDetached, vm))
                {
                    return true;
                }
                return false;
            }
            if (_originalDetached is not null
                && !ReferenceEquals(currentDetached, _originalDetached)
                && !(currentControl is AgentGuiTabView { DataContext: AgentGuiTabViewModel reattachedVm } && ReferenceEquals(reattachedVm, _originalDetached)))
            {
                return false;
            }
            if (_originalSession is not null
                && (!ReferenceEquals(currentSession, _originalSession)
                    || currentSession.Generation != _originalSessionGeneration))
            {
                return false;
            }
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_owner._pendingConfirmations.TryGetValue(_itemId, out var list))
            {
                list.Remove(this);
                if (list.Count == 0)
                {
                    _owner._pendingConfirmations.Remove(_itemId);
                }
            }
        }
    }

    private Octopus.Terminal.Avalonia.TerminalSession? FindSession(string itemId) => sessions?.Find(itemId);

    internal readonly record struct OwnerSnapshot(
        Control? Control,
        AgentGuiTabViewModel? Detached,
        Octopus.Terminal.Avalonia.TerminalSession? Session,
        long? SessionGeneration)
    {
        internal bool HasOwner => Control is not null || Detached is not null || Session is not null;
    }

    internal OwnerSnapshot SnapshotOwner(string itemId)
    {
        var session = FindSession(itemId);
        return new(
            _controls.GetValueOrDefault(itemId),
            _detached.GetValueOrDefault(itemId),
            session,
            session?.Generation);
    }

    /// <summary>
    /// Captures and retires <paramref name="itemId"/> only when the currently registered owner and terminal process
    /// generation still match <paramref name="expected"/>, preventing an older close notification from retiring a
    /// restarted or newly attached successor.
    /// </summary>
    internal Func<Task>? CaptureForReleaseIfMatches(string itemId, OwnerSnapshot expected)
    {
        if (!expected.HasOwner)
        {
            return null;
        }
        var currentControl = _controls.GetValueOrDefault(itemId);
        var currentDetached = _detached.GetValueOrDefault(itemId);
        var currentSession = FindSession(itemId);

        if (expected.Detached is not null)
        {
            return ReferenceEquals(currentDetached, expected.Detached)
                ? CaptureForRelease(itemId)
                : null;
        }

        if (expected.Control is not null)
        {
            if (!ReferenceEquals(currentControl, expected.Control))
            {
                return null;
            }
            if (expected.Session is not null)
            {
                if (!ReferenceEquals(currentSession, expected.Session)
                    || currentSession.Generation != expected.SessionGeneration)
                {
                    return null;
                }
            }
            else if (currentSession is not null)
            {
                return null;
            }
            return CaptureForRelease(itemId);
        }

        if (currentControl is not null
            || currentDetached is not null
            || currentSession is null
            || !ReferenceEquals(currentSession, expected.Session)
            || currentSession.Generation != expected.SessionGeneration)
        {
            return null;
        }
        return CaptureForRelease(itemId);
    }

    internal PendingConfirmation BeginConfirmation(string itemId)
    {
        var confirmation = new PendingConfirmation(
            this,
            itemId,
            _controls.GetValueOrDefault(itemId),
            _detached.GetValueOrDefault(itemId),
            sessions?.Find(itemId));
        if (!_pendingConfirmations.TryGetValue(itemId, out var list))
        {
            list = [];
            _pendingConfirmations[itemId] = list;
        }
        list.Add(confirmation);
        return confirmation;
    }

    private void InvalidatePendingConfirmations(string itemId)
    {
        if (_pendingConfirmations.TryGetValue(itemId, out var list))
        {
            foreach (var confirmation in list)
            {
                confirmation.Invalidate();
            }
        }
    }

    public Control GetOrCreate(Project project, ProjectItem item)
    {
        if (_controls.TryGetValue(item.Id, out var existing))
        {
            return existing;
        }
        if (_detached.Remove(item.Id, out var detached))
        {
            var view = new AgentGuiTabView { DataContext = detached };
            _controls[item.Id] = view;
            return view;
        }
        var control = item.Kind switch
        {
            ProjectItemKind.File => new FileTabView { DataContext = new FileTabViewModel(item, files, settings, dialogs: dialogs) },
            // Resolved here, not injected: the plugin workbench needs the item opener, which needs this registry's peers.
            ProjectItemKind.Plugin => new PluginViewTab(project, item, services.GetRequiredService<PluginWorkbench>(), services.GetRequiredService<IProjectService>()),
            // A project on another computer: the tab mirrors the terminal that computer runs, and owns nothing here.
            _ when ProjectLocation.IsOnRemoteHost(project) && remote is not null =>
                new TerminalTabView { DataContext = new TerminalTabViewModel(project, item, launcher, remote.TerminalFactory(project, item), settings) { IsMirror = true } },
            // Local ACP-capable agent opened in GUI surface: structured Style A conversation view.
            ProjectItemKind.Agent when TryCreateAgentGui(project, item) is { } guiTab => guiTab,
            _ => new TerminalTabView
            {
                DataContext = new TerminalTabViewModel(
                    project,
                    item,
                    launcher,
                    factory,
                    settings,
                    sessions,
                    services.GetService<Octopus.Platform.IFileOpener>(),
                    services.GetService<Octopus.App.Services.Workspaces.IWorkspaceManager>()?.HomeDirectory),
            },
        };
        _controls[item.Id] = control;
        return control;
    }

    private Control? TryCreateAgentGui(Project project, ProjectItem item)
    {
        if (item.Surface != AgentSurface.Gui || string.IsNullOrWhiteSpace(item.AgentId))
        {
            return null;
        }
        var catalog = services.GetService<IAgentCatalog>();
        var definition = catalog?.Find(item.AgentId);
        // Hard constraint: any agent without ACP support always opens in a terminal.
        if (definition is null || !definition.SupportsAcp)
        {
            return null;
        }
        var launchResolver = services.GetService<AcpLaunchResolver>();
        var sessionFactory = services.GetService<IAcpSessionFactory>();
        var dialects = services.GetService<AcpDialectRegistry>();
        if (launchResolver is null || sessionFactory is null || dialects is null)
        {
            return null;
        }
        var vm = new AgentGuiTabViewModel(
            project,
            item,
            definition,
            settings,
            services.GetRequiredService<IAgentScanner>(),
            services.GetRequiredService<IAgentCommandCompiler>(),
            launchResolver,
            sessionFactory,
            dialects,
            services.GetRequiredService<IProjectService>(),
            services.GetRequiredService<IItemOpener>(),
            services.GetService<IPickerService>(),
            services.GetService<IItemActivityService>(),
            services.GetService<IAgentAccountService>(),
            () => services.GetService<IWorkspaceCommands>(),
            services.GetService<ISubagentService>(),
            services.GetService<IModelDiscovery>(),
            services.GetService<Octopus.Platform.IFileOpener>(),
            services.GetService<Octopus.App.Bots.IBotService>(),
            services.GetService<Octopus.App.Collab.ICollabCoordinator>(),
            services.GetService<Octopus.App.Bots.BotScheduleMcpServer>(),
            services.GetService<IWorkspaceFileIndex>(),
            services.GetService<Octopus.Agents.Providers.IAgentBackendResolver>());
        return new AgentGuiTabView { DataContext = vm };
    }

    public Control GetOrCreateDiff(DiffTabRequest request)
    {
        if (_controls.TryGetValue(request.Key, out var existing))
        {
            return existing;
        }
        // A change of a project on another computer: that computer's git and files answer for it.
        var current = services.GetRequiredService<IProjectService>().Current;
        var access = services.GetService<Octopus.App.Remote.RemoteProjectAccess>();
        var control = new DiffTabView
        {
            DataContext = new DiffTabViewModel(request, access?.GitOf(current) ?? git, access?.GitOf(current) is null ? files : access.TreeOf(current) ?? files, settings),
        };
        _controls[request.Key] = control;
        return control;
    }

    public ITabContent? Find(string itemId) =>
        _controls.GetValueOrDefault(itemId) as ITabContent
        ?? (_detached.TryGetValue(itemId, out var model) ? new DetachedAgent(model) : null);

    public void Detach(string itemId)
    {
        // Only an agent conversation can live without its view. A terminal's control is what reads its session
        // (title, exit, activity), so it is kept as it is: off screen, nothing of it is laid out or drawn.
        if (_controls.GetValueOrDefault(itemId) is not AgentGuiTabView { DataContext: AgentGuiTabViewModel model } view)
        {
            return;
        }
        _controls.Remove(itemId);
        view.DataContext = null;
        _detached[itemId] = model;
    }

    public Func<Task>? CaptureForRelease(string itemId)
    {
        if (_detached.Remove(itemId, out var model))
        {
            InvalidatePendingConfirmations(itemId);
            return CreateCapturedRelease(itemId, () => StartTrackedRelease(itemId, () => ReleaseDetachedAsync(model)));
        }
        if (_controls.Remove(itemId, out var control))
        {
            InvalidatePendingConfirmations(itemId);
            var isTerminalTab = control is TerminalTabView;
            var retiredSession = isTerminalTab ? sessions?.TakeDetached(itemId) : null;
            var handle = CreateCapturedRelease(itemId, () => StartTrackedRelease(itemId, () => ReleaseCoreAsync(itemId, control, retiredSession)));
            if (retiredSession is not null)
            {
                sessions!.NotifyChanged(itemId);
            }
            return handle;
        }
        var headlessSession = sessions?.TakeDetached(itemId);
        if (headlessSession is not null)
        {
            InvalidatePendingConfirmations(itemId);
            var handle = CreateCapturedRelease(itemId, () => StartTrackedRelease(itemId, () => ReleaseHeadlessSessionAsync(itemId, headlessSession)));
            sessions!.NotifyChanged(itemId);
            return handle;
        }
        return null;
    }

    public Task WaitForReleaseAsync(string itemId)
    {
        List<Task>? waiting = null;
        if (_pendingCapturedReleases.TryGetValue(itemId, out var pending))
        {
            foreach (var handle in pending.ToList())
            {
                var task = handle();
                if (!task.IsCompletedSuccessfully)
                {
                    waiting ??= [];
                    waiting.Add(task);
                }
            }
        }
        if (_releasing.TryGetValue(itemId, out var active))
        {
            foreach (var task in active)
            {
                if (waiting is null || !waiting.Contains(task))
                {
                    waiting ??= [];
                    waiting.Add(task);
                }
            }
        }
        return waiting switch
        {
            null or { Count: 0 } => Task.CompletedTask,
            { Count: 1 } => waiting[0],
            _ => Task.WhenAll(waiting),
        };
    }

    public Task ReleaseAsync(string itemId)
    {
        if (CaptureForRelease(itemId) is { } captured)
        {
            var current = captured();
            var all = WaitForReleaseAsync(itemId);
            if (ReferenceEquals(all, current) || all.IsCompletedSuccessfully)
            {
                return current;
            }
            return current.IsCompletedSuccessfully ? all : Task.WhenAll(current, all);
        }
        if (_pendingCapturedReleases.ContainsKey(itemId) || _releasing.ContainsKey(itemId))
        {
            return WaitForReleaseAsync(itemId);
        }
        // No tab or session was found, but a legacy default CLI lease may still need cleanup.
        return StartTrackedRelease(itemId, () => ReleaseHeadlessSessionAsync(itemId, retiredSession: null));
    }

    public async Task ReleaseAllAsync()
    {
        foreach (var list in _pendingConfirmations.Values)
        {
            foreach (var confirmation in list)
            {
                confirmation.Invalidate();
            }
        }
        _pendingConfirmations.Clear();
        foreach (var pending in _pendingCapturedReleases.Values.SelectMany(list => list).ToList())
        {
            _ = pending();
        }
        foreach (var id in _controls.Keys.Concat(_detached.Keys).ToList())
        {
            _ = ReleaseAsync(id);
        }
        await Task.WhenAll(_activeReleases.ToList());
        // Sessions that never had a tab.
        if (sessions is not null) await sessions.CloseAllAsync();
    }

    private Func<Task> CreateCapturedRelease(string itemId, Func<Task> run)
    {
        Task? started = null;
        Func<Task>? handle = null;
        handle = () =>
        {
            if (started is not null) return started;
            if (_pendingCapturedReleases.TryGetValue(itemId, out var pending))
            {
                pending.Remove(handle!);
                if (pending.Count == 0)
                {
                    _pendingCapturedReleases.Remove(itemId);
                }
            }
            started = run();
            return started;
        };
        if (!_pendingCapturedReleases.TryGetValue(itemId, out var list))
        {
            list = [];
            _pendingCapturedReleases[itemId] = list;
        }
        list.Add(handle);
        return handle;
    }

    public async Task<bool> AnyRunningChildAsync()
    {
        var checks = _controls.Values.OfType<ITabContent>().Select(c => c.HasRunningChildAsync())
            .Concat(_detached.Values.Select(m => m.HasRunningChildAsync())).ToList();
        if ((await Task.WhenAll(checks)).Any(running => running)) return true;
        // Sessions that never had a tab count too: quitting ends their programs just the same.
        return sessions is not null && await sessions.AnyRunningChildAsync();
    }

    public async Task<bool> AnyUnsavedChangesAsync()
    {
        var checks = _controls.Values.OfType<ITabContent>().Select(c => c.HasUnsavedChangesAsync()).ToList();
        return (await Task.WhenAll(checks)).Any(unsaved => unsaved);
    }

    private Task StartTrackedRelease(string itemId, Func<Task> body)
    {
        Task? tracked = null;
        async Task RunAsync()
        {
            try
            {
                await body();
            }
            finally
            {
                if (tracked is not null)
                {
                    _activeReleases.Remove(tracked);
                    if (_releasing.TryGetValue(itemId, out var list))
                    {
                        list.Remove(tracked);
                        if (list.Count == 0)
                        {
                            _releasing.Remove(itemId);
                        }
                    }
                }
            }
        }
        tracked = RunAsync();
        if (!tracked.IsCompleted)
        {
            if (!_releasing.TryGetValue(itemId, out var list))
            {
                list = [];
                _releasing[itemId] = list;
            }
            list.Add(tracked);
            _activeReleases.Add(tracked);
        }
        return tracked;
    }

    private static async Task ReleaseDetachedAsync(AgentGuiTabViewModel model)
    {
        await model.CloseAsync();
        model.Dispose();
    }

    /// <summary>What a closed agent tab answers for while it has no view: it runs, ends and restarts like the open one.</summary>
    private sealed class DetachedAgent(AgentGuiTabViewModel model) : ITabContent
    {
        public Task<bool> HasRunningChildAsync() => model.HasRunningChildAsync();
        public Task CloseAsync() => model.CloseAsync();
        public Task RestartAsync() => model.RestartAsync();
        public void FocusContent() { }
    }

    private async Task ReleaseCoreAsync(string itemId, Control control, Octopus.Terminal.Avalonia.TerminalSession? retiredSession)
    {
        var isTerminalTab = control is TerminalTabView;
        try
        {
            if (control is ITabContent content)
            {
                await content.CloseAsync();
            }
        }
        finally
        {
            // The session was already retired from the registry at capture time so any reopen starts a distinct
            // terminal immediately; dispose the captured instance directly without touching any replacement.
            if (retiredSession is not null)
            {
                await retiredSession.DisposeAsync();
            }
            var dataContext = control.DataContext;
            control.DataContext = null;
            (dataContext as IDisposable)?.Dispose();
            if (isTerminalTab && !_controls.ContainsKey(itemId) && sessions?.Find(itemId) is null)
            {
                services.GetService<Collab.CollabCliMcpInjector>()?.ReleaseDefaultLease(itemId);
                (services.GetService<Collab.ICollabCoordinator>() as Collab.CollabCoordinator)?.NotifyTerminalSessionClosed(itemId, retiredSession);
            }
            else if (isTerminalTab && retiredSession is not null && !ReferenceEquals(sessions?.Find(itemId), retiredSession))
            {
                (services.GetService<Collab.ICollabCoordinator>() as Collab.CollabCoordinator)?.NotifyTerminalSessionRetired(itemId, retiredSession);
            }
        }
    }

    private async Task ReleaseHeadlessSessionAsync(string itemId, Octopus.Terminal.Avalonia.TerminalSession? retiredSession)
    {
        if (retiredSession is not null)
        {
            await retiredSession.DisposeAsync();
        }
        if (!_controls.ContainsKey(itemId) && !_detached.ContainsKey(itemId) && sessions?.Find(itemId) is null)
        {
            services.GetService<Collab.CollabCliMcpInjector>()?.ReleaseDefaultLease(itemId);
            if (retiredSession is not null)
            {
                (services.GetService<Collab.ICollabCoordinator>() as Collab.CollabCoordinator)?.NotifyTerminalSessionClosed(itemId, retiredSession);
            }
        }
        else if (retiredSession is not null && !ReferenceEquals(sessions?.Find(itemId), retiredSession))
        {
            (services.GetService<Collab.ICollabCoordinator>() as Collab.CollabCoordinator)?.NotifyTerminalSessionRetired(itemId, retiredSession);
        }
    }
}
