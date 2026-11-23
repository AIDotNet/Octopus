using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Octopus.App.Files;
using Octopus.App.Plugins;
using Octopus.App.Services;
using Octopus.App.Terminals;
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
            _ => new TerminalTabView { DataContext = new TerminalTabViewModel(project, item, launcher, factory, settings, sessions) },
        };
        _controls[item.Id] = control;
        return control;
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
