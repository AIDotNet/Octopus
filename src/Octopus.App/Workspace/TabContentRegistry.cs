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
    private readonly Dictionary<string, Task> _releasing = [];
    /// <summary>
    /// Agent conversations whose tab was closed but which keep running: the view model (and its session) is kept, the
    /// view is let go. <see cref="GetOrCreate"/> puts a new view over it when the tab opens again.
    /// </summary>
    private readonly Dictionary<string, AgentGuiTabViewModel> _detached = [];

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
            services.GetService<IWorkspaceFileIndex>());
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

    public Task ReleaseAsync(string itemId)
    {
        if (_releasing.TryGetValue(itemId, out var inProgress))
        {
            return inProgress;
        }
        if (_detached.Remove(itemId, out var model))
        {
            var ending = ReleaseDetachedAsync(itemId, model);
            if (!ending.IsCompleted)
            {
                _releasing[itemId] = ending;
            }
            return ending;
        }
        if (!_controls.Remove(itemId, out var control))
        {
            // No tab was ever built for it, but its session may run (started for a view on another device).
            return sessions?.CloseAsync(itemId) ?? Task.CompletedTask;
        }
        var release = ReleaseCoreAsync(itemId, control);
        if (!release.IsCompleted)
        {
            _releasing[itemId] = release;
        }
        return release;
    }

    public async Task ReleaseAllAsync()
    {
        foreach (var id in _controls.Keys.Concat(_detached.Keys).ToList())
        {
            _ = ReleaseAsync(id);
        }
        await Task.WhenAll(_releasing.Values.ToList());
        // Sessions that never had a tab.
        if (sessions is not null) await sessions.CloseAllAsync();
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

    private async Task ReleaseDetachedAsync(string itemId, AgentGuiTabViewModel model)
    {
        try
        {
            await model.CloseAsync();
            model.Dispose();
        }
        finally
        {
            _releasing.Remove(itemId);
        }
    }

    /// <summary>What a closed agent tab answers for while it has no view: it runs, ends and restarts like the open one.</summary>
    private sealed class DetachedAgent(AgentGuiTabViewModel model) : ITabContent
    {
        public Task<bool> HasRunningChildAsync() => model.HasRunningChildAsync();
        public Task CloseAsync() => model.CloseAsync();
        public Task RestartAsync() => model.RestartAsync();
        public void FocusContent() { }
    }

    private async Task ReleaseCoreAsync(string itemId, Control control)
    {
        try
        {
            if (control is ITabContent content)
            {
                await content.CloseAsync();
            }
            var dataContext = control.DataContext;
            control.DataContext = null;
            (dataContext as IDisposable)?.Dispose();
            // A terminal tab closed its session through the registry already; this covers a tab that never started it.
            if (sessions is not null) await sessions.CloseAsync(itemId);
        }
        finally
        {
            _releasing.Remove(itemId);
        }
    }
}
