using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Files;
using Octopus.App.Services;
using Octopus.Core.Projects;
using TKCode.Arc;

namespace Octopus.App.Workspace;

/// <summary>
/// The workspace: one dock layout per project, created when the project is first shown and kept alive after that, so
/// switching projects only changes which layout is on screen and no terminal is touched.
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject, IWorkspaceCommands
{
    private readonly IProjectService _projects;
    private readonly ITabContentRegistry _registry;
    private readonly IItemOpener _opener;
    private readonly CloseConfirmation _confirmation;
    private readonly Dictionary<string, ProjectDockController> _controllers = [];

    public WorkspaceViewModel(
        IProjectService projects,
        ITabContentRegistry registry,
        IItemOpener opener,
        CloseConfirmation confirmation,
        EmptyStateViewModel emptyState)
    {
        _projects = projects;
        _registry = registry;
        _opener = opener;
        _confirmation = confirmation;
        EmptyState = emptyState;
        _projects.Changed += OnProjectsChanged;
        Refresh();
    }

    public EmptyStateViewModel EmptyState { get; }

    /// <summary>The dock layout of the current project; null while there is no project.</summary>
    [ObservableProperty] public partial object? CurrentLayout { get; private set; }
    [ObservableProperty] public partial bool HasProject { get; private set; }

    private ProjectDockController? CurrentController =>
        _projects.Current is { } project && _controllers.TryGetValue(project.Id, out var controller) ? controller : null;

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (e.Kind is ProjectChangeKind.Projects or ProjectChangeKind.CurrentProject)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        foreach (var gone in _controllers.Keys.Where(id => _projects.Projects.All(p => p.Id != id)).ToList())
        {
            _controllers.Remove(gone, out var controller);
            controller!.Dispose();
        }
        var current = _projects.Current;
        HasProject = current is not null;
        if (current is null)
        {
            CurrentLayout = null;
            return;
        }
        if (!_controllers.TryGetValue(current.Id, out var existing))
        {
            existing = Create(current);
            _controllers[current.Id] = existing;
        }
        var changed = !ReferenceEquals(CurrentLayout, existing.Layout);
        CurrentLayout = existing.Layout;
        if (changed)
        {
            existing.ActivateCurrent();
        }
    }

    private ProjectDockController Create(Project project) =>
        new(project, _projects, _registry, _confirmation, () => _opener.OpenTerminal(), new EmptyProjectView { DataContext = EmptyState });

    /// <summary>Asks once when programs still run in some tab; true when the app may quit.</summary>
    public Task<bool> ConfirmQuitAsync() => _confirmation.ConfirmQuitAsync();

    /// <summary>Ends every process and waits until they are gone (the window is closing).</summary>
    public async Task CloseAllAsync()
    {
        _projects.Changed -= OnProjectsChanged;
        foreach (var controller in _controllers.Values)
        {
            controller.Dispose();
        }
        _controllers.Clear();
        await _registry.ReleaseAllAsync();
    }

    // ---- IWorkspaceCommands ----

    /// <summary>The group the next new tab opens in (a "+" of a tab strip was clicked); null for the active group.</summary>
    public void SetNewTabGroup(DockGroup? group)
    {
        if (CurrentController is { } controller)
        {
            controller.NewTabGroup = group;
        }
    }

    public void SplitRight() => CurrentController?.SplitRight();

    public void MoveToNewGroup(string itemId) => CurrentController?.MoveToNewGroup(itemId);

    public void CloseActiveTab() => CurrentController?.CloseActive();

    public void SelectTab(int number) => CurrentController?.SelectTab(number);

    public void CycleTab(int delta) => CurrentController?.CycleTab(delta);

    public void OpenDiff(DiffTabRequest request) => CurrentController?.OpenDiff(request);

    public void FocusActive() => CurrentController?.ActivateCurrent();

    public async Task CloseItemAsync(string itemId)
    {
        if (await _confirmation.ConfirmCloseTabAsync(itemId))
        {
            _projects.RemoveItem(itemId);
        }
    }

    public async Task RestartItemAsync(string itemId)
    {
        if (_registry.Find(itemId) is { } content)
        {
            await content.RestartAsync();
        }
    }
}
