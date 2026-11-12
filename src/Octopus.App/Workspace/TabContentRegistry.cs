using Avalonia.Controls;
using Octopus.App.Files;
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
    IDialogService dialogs) : ITabContentRegistry
{
    private readonly Dictionary<string, Control> _controls = [];
    private readonly Dictionary<string, Task> _releasing = [];

    public Control GetOrCreate(Project project, ProjectItem item)
    {
        if (_controls.TryGetValue(item.Id, out var existing))
        {
            return existing;
        }
        Control control = item.Kind == ProjectItemKind.File
            ? new FileTabView { DataContext = new FileTabViewModel(item, files, settings, dialogs: dialogs) }
            : new TerminalTabView { DataContext = new TerminalTabViewModel(project, item, launcher, factory, settings) };
        _controls[item.Id] = control;
        return control;
    }

    public Control GetOrCreateDiff(DiffTabRequest request)
    {
        if (_controls.TryGetValue(request.Key, out var existing))
        {
            return existing;
        }
        var control = new DiffTabView { DataContext = new DiffTabViewModel(request, git, files, settings) };
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
            return Task.CompletedTask;
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
    }

    public async Task<bool> AnyRunningChildAsync()
    {
        var checks = _controls.Values.OfType<ITabContent>().Select(c => c.HasRunningChildAsync()).ToList();
        return (await Task.WhenAll(checks)).Any(running => running);
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
            (control.DataContext as IDisposable)?.Dispose();
        }
        finally
        {
            _releasing.Remove(itemId);
        }
    }
}
