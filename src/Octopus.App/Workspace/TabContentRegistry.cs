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

    public Control GetOrCreate(Project project, ProjectItem item)
    {
        if (_controls.TryGetValue(item.Id, out var existing))
        {
            return existing;
        }
        Control control = item.Kind switch
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
            () => services.GetService<IWorkspaceCommands>());
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

    public ITabContent? Find(string itemId) => _controls.GetValueOrDefault(itemId) as ITabContent;

    public Task ReleaseAsync(string itemId)
    {
        if (_releasing.TryGetValue(itemId, out var inProgress))
        {
            return inProgress;
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
        foreach (var id in _controls.Keys.ToList())
        {
            _ = ReleaseAsync(id);
        }
        await Task.WhenAll(_releasing.Values.ToList());
        // Sessions that never had a tab.
        if (sessions is not null) await sessions.CloseAllAsync();
    }

    public async Task<bool> AnyRunningChildAsync()
    {
        var checks = _controls.Values.OfType<ITabContent>().Select(c => c.HasRunningChildAsync()).ToList();
        if ((await Task.WhenAll(checks)).Any(running => running)) return true;
        // Sessions that never had a tab count too: quitting ends their programs just the same.
        return sessions is not null && await sessions.AnyRunningChildAsync();
    }

    public async Task<bool> AnyUnsavedChangesAsync()
    {
        var checks = _controls.Values.OfType<ITabContent>().Select(c => c.HasUnsavedChangesAsync()).ToList();
        return (await Task.WhenAll(checks)).Any(unsaved => unsaved);
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
