using Octopus.App.Services;
using Octopus.Core.Projects;

namespace Octopus.App.Collab;

/// <summary>
/// A thread-safe, read-only view of the projects for the collaboration bus. <see cref="IProjectService"/> belongs to the
/// UI thread (its lists are mutated there), while MCP tool calls, wake-ups and remote updates run on other threads. The
/// index copies the lists into an immutable snapshot whenever the projects change (on the thread that changed them), so
/// readers never enumerate a list that is being modified. Item and project objects are shared, not copied: their scalar
/// properties are read live.
/// </summary>
public sealed class CollabProjectIndex : IDisposable
{
    private readonly IProjectService _projects;
    private Snapshot _snapshot;

    private sealed record Snapshot(
        IReadOnlyList<Project> Projects,
        IReadOnlyList<ProjectGroup> Groups,
        Project? Current,
        IReadOnlyDictionary<string, (Project Project, ProjectItem Item)> Items,
        IReadOnlyDictionary<string, IReadOnlyList<ProjectItem>> ItemsByProject,
        IReadOnlyDictionary<string, IReadOnlyList<ProjectWorkspace>> WorkspacesByProject);

    public CollabProjectIndex(IProjectService projects)
    {
        _projects = projects;
        _snapshot = Build();
        _projects.Changed += OnProjectsChanged;
    }

    public IReadOnlyList<Project> Projects => Volatile.Read(ref _snapshot).Projects;
    public IReadOnlyList<ProjectGroup> Groups => Volatile.Read(ref _snapshot).Groups;
    public Project? Current => Volatile.Read(ref _snapshot).Current;

    public ProjectItem? FindItem(string? itemId) =>
        itemId is not null && Volatile.Read(ref _snapshot).Items.TryGetValue(itemId, out var found) ? found.Item : null;

    public Project? FindProjectOf(string? itemId) =>
        itemId is not null && Volatile.Read(ref _snapshot).Items.TryGetValue(itemId, out var found) ? found.Project : null;

    /// <summary>The items of <paramref name="project"/> as of the last change (a copy, safe to enumerate anywhere).</summary>
    public IReadOnlyList<ProjectItem> ItemsOf(Project project) =>
        Volatile.Read(ref _snapshot).ItemsByProject.GetValueOrDefault(project.Id) ?? [];

    public ProjectWorkspace? FindWorkspace(Project project, string? workspaceId) =>
        workspaceId is null
            ? null
            : Volatile.Read(ref _snapshot).WorkspacesByProject.GetValueOrDefault(project.Id)?.FirstOrDefault(w => w.Id == workspaceId);

    /// <summary>Takes a fresh snapshot now (for changes made without a <see cref="IProjectService.Changed"/> event).</summary>
    public void Refresh() => Volatile.Write(ref _snapshot, Build());

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e) => Refresh();

    private Snapshot Build()
    {
        var projects = _projects.Projects.ToArray();
        var items = new Dictionary<string, (Project, ProjectItem)>(StringComparer.Ordinal);
        var byProject = new Dictionary<string, IReadOnlyList<ProjectItem>>(StringComparer.Ordinal);
        var workspaces = new Dictionary<string, IReadOnlyList<ProjectWorkspace>>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var projectItems = project.Items.ToArray();
            byProject[project.Id] = projectItems;
            workspaces[project.Id] = project.Workspaces.ToArray();
            foreach (var item in projectItems)
            {
                items[item.Id] = (project, item);
            }
        }
        return new Snapshot(projects, _projects.Groups.ToArray(), _projects.Current, items, byProject, workspaces);
    }

    public void Dispose() => _projects.Changed -= OnProjectsChanged;
}
