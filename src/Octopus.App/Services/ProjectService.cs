using Octopus.App.Common;
using Octopus.Core.Projects;
using Octopus.Storage;

namespace Octopus.App.Services;

public sealed class ProjectService(IProjectStore store, PathComparer paths, ISettingsStore settings) : IProjectService
{
    /// <summary>Project colors are data: new projects take them in turn.</summary>
    public static readonly string[] Palette =
        ["#6366F1", "#0EA5E9", "#10B981", "#F59E0B", "#EF4444", "#EC4899", "#8B5CF6", "#14B8A6"];

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly object _saveGate = new();
    private ProjectsDocument _document = new();
    private readonly HashSet<string> _restored = [];
    private CancellationTokenSource? _pending;
    private Task _lastSave = Task.CompletedTask;

    public event EventHandler<ProjectChangedEventArgs>? Changed;

    public IReadOnlyList<Project> Projects => _document.Projects;

    public Project? Current =>
        _document.Projects.FirstOrDefault(p => p.Id == _document.ActiveProjectId) ?? _document.Projects.FirstOrDefault();

    public async Task LoadAsync()
    {
        _document = await store.LoadAsync();
        _restored.Clear();
        var dropped = !settings.Current.Terminal.RestoreItemsOnStartup && DropRunnableItems();
        foreach (var project in _document.Projects)
        {
            foreach (var item in project.Items)
            {
                _restored.Add(item.Id);
            }
            Normalize(project);
        }
        if (_document.ActiveProjectId is null || _document.Projects.All(p => p.Id != _document.ActiveProjectId))
        {
            _document.ActiveProjectId = _document.Projects.FirstOrDefault()?.Id;
        }
        Raise(ProjectChangeKind.Projects, save: dropped);
    }

    /// <summary>
    /// "Restore on startup" is off: the terminals and agents of the last run are not brought back (their processes
    /// ended with the app anyway). Files stay. A layout that named the removed tabs is dropped with them.
    /// </summary>
    private bool DropRunnableItems()
    {
        var any = false;
        foreach (var project in _document.Projects)
        {
            if (project.Items.RemoveAll(i => i.Kind is ProjectItemKind.Terminal or ProjectItemKind.Agent) > 0)
            {
                project.DockLayout = null;
                any = true;
            }
        }
        return any;
    }

    public Task FlushAsync()
    {
        Task previous;
        lock (_saveGate)
        {
            _pending?.Cancel();
            _pending = null;
            previous = _lastSave;
        }
        return FlushCoreAsync(previous);
    }

    private async Task FlushCoreAsync(Task previous)
    {
        try { await previous; } catch { /* a failed earlier save is retried below */ }
        await store.SaveAsync(_document);
    }

    // ---- projects ----

    public Project AddProject(string path, string? name = null, string? sshHost = null)
    {
        var host = string.IsNullOrWhiteSpace(sshHost) ? null : sshHost.Trim();
        // A folder on another machine is that machine's path: it is kept as typed, never made absolute here.
        var full = host is null ? Path.GetFullPath(path) : RemotePath(path);
        var existing = _document.Projects.FirstOrDefault(p => p.SshHost == host && (host is null ? paths.Equals(p.Path, full) : p.Path == full));
        if (existing is not null)
        {
            SetCurrent(existing.Id);
            return existing;
        }
        var project = new Project
        {
            Path = full,
            SshHost = host,
            Name = string.IsNullOrWhiteSpace(name) ? PathText.NameOf(full) : name.Trim(),
            Color = Palette[_document.Projects.Count % Palette.Length],
        };
        _document.Projects.Add(project);
        _document.ActiveProjectId = project.Id;
        Raise(ProjectChangeKind.Projects, project);
        Raise(ProjectChangeKind.CurrentProject, project, save: false);
        return project;
    }

    /// <summary>The folder as typed, without a trailing slash (the root stays "/").</summary>
    private static string RemotePath(string path)
    {
        var trimmed = path.Trim();
        var result = trimmed.TrimEnd('/');
        return result.Length == 0 && trimmed.Length > 0 ? "/" : result;
    }

    public void RemoveProject(string projectId)
    {
        var project = Find(projectId);
        if (project is null)
        {
            return;
        }
        var index = _document.Projects.IndexOf(project);
        _document.Projects.Remove(project);
        var wasCurrent = _document.ActiveProjectId == projectId;
        if (wasCurrent)
        {
            _document.ActiveProjectId = _document.Projects.Count == 0
                ? null
                : _document.Projects[Math.Min(index, _document.Projects.Count - 1)].Id;
        }
        foreach (var item in project.Items)
        {
            _restored.Remove(item.Id);
        }
        Raise(ProjectChangeKind.Projects, project);
        if (wasCurrent)
        {
            Raise(ProjectChangeKind.CurrentProject, Current, save: false);
        }
    }

    public void RenameProject(string projectId, string name)
    {
        var project = Find(projectId);
        if (project is null || string.IsNullOrWhiteSpace(name) || project.Name == name.Trim())
        {
            return;
        }
        project.Name = name.Trim();
        Raise(ProjectChangeKind.Projects, project);
    }

    public void SetProjectColor(string projectId, string? color)
    {
        var project = Find(projectId);
        if (project is null)
        {
            return;
        }
        project.Color = color;
        Raise(ProjectChangeKind.Projects, project);
    }

    public void SetProjectAppearance(string projectId, string? color, string? icon, string? image)
    {
        var project = Find(projectId);
        if (project is null)
        {
            return;
        }
        project.Color = string.IsNullOrWhiteSpace(color) ? project.Color : color;
        project.Icon = string.IsNullOrWhiteSpace(icon) ? null : icon;
        project.Image = string.IsNullOrWhiteSpace(image) ? null : image;
        Raise(ProjectChangeKind.Projects, project);
    }

    public void SetCurrent(string projectId)
    {
        var project = Find(projectId);
        if (project is null || _document.ActiveProjectId == projectId)
        {
            return;
        }
        _document.ActiveProjectId = projectId;
        Raise(ProjectChangeKind.CurrentProject, project);
    }

    // ---- groups ----

    public IReadOnlyList<ProjectGroup> Groups => _document.Groups;

    public string? ExpandedGroupId => _document.ExpandedGroupId;

    public void AddTunnel(Project project, TunnelSpec tunnel)
    {
        if (project.SshHost is null || project.Tunnels.Any(t => t.Id == tunnel.Id)) return;
        project.Tunnels.Add(tunnel);
        Raise(ProjectChangeKind.Tunnels, project);
    }

    public void UpdateTunnel(Project project, TunnelSpec tunnel)
    {
        var index = project.Tunnels.FindIndex(t => t.Id == tunnel.Id);
        if (index < 0) return;
        project.Tunnels[index] = tunnel;
        Raise(ProjectChangeKind.Tunnels, project);
    }

    public void RemoveTunnel(Project project, string tunnelId)
    {
        if (project.Tunnels.RemoveAll(t => t.Id == tunnelId) > 0)
        {
            Raise(ProjectChangeKind.Tunnels, project);
        }
    }

    public ProjectGroup AddGroup(string name, IEnumerable<string>? projectIds = null)
    {
        var group = new ProjectGroup
        {
            Name = name.Trim(),
            Color = Palette[_document.Groups.Count % Palette.Length],
        };
        _document.Groups.Add(group);
        foreach (var id in projectIds ?? [])
        {
            if (Find(id) is { } project)
            {
                PlaceLast(project, group.Id);
            }
        }
        // Open, so the projects that went into it are still in sight.
        _document.ExpandedGroupId = group.Id;
        Raise(ProjectChangeKind.Groups);
        return group;
    }

    public void RenameGroup(string groupId, string name)
    {
        var group = FindGroup(groupId);
        if (group is null || string.IsNullOrWhiteSpace(name) || group.Name == name.Trim())
        {
            return;
        }
        group.Name = name.Trim();
        Raise(ProjectChangeKind.Groups);
    }

    public void SetGroupColor(string groupId, string? color)
    {
        var group = FindGroup(groupId);
        var value = string.IsNullOrWhiteSpace(color) ? null : color;
        if (group is null || group.Color == value)
        {
            return;
        }
        group.Color = value;
        Raise(ProjectChangeKind.Groups);
    }

    public void RemoveGroup(string groupId)
    {
        var group = FindGroup(groupId);
        if (group is null)
        {
            return;
        }
        foreach (var project in _document.Projects.Where(p => p.GroupId == groupId))
        {
            project.GroupId = null;
        }
        _document.Groups.Remove(group);
        if (_document.ExpandedGroupId == groupId)
        {
            _document.ExpandedGroupId = null;
        }
        Raise(ProjectChangeKind.Groups);
    }

    public void SetExpandedGroup(string? groupId)
    {
        if ((groupId is not null && FindGroup(groupId) is null) || _document.ExpandedGroupId == groupId)
        {
            return;
        }
        _document.ExpandedGroupId = groupId;
        Raise(ProjectChangeKind.GroupExpanded);
    }

    public void MoveProjectToGroup(string projectId, string? groupId)
    {
        var project = Find(projectId);
        if (project is null || project.GroupId == groupId || (groupId is not null && FindGroup(groupId) is null))
        {
            return;
        }
        PlaceLast(project, groupId);
        Raise(ProjectChangeKind.Groups, project);
    }

    public void MoveProject(string projectId, string anchorProjectId, bool after)
    {
        var project = Find(projectId);
        var anchor = Find(anchorProjectId);
        if (project is null || anchor is null || ReferenceEquals(project, anchor))
        {
            return;
        }
        var before = _document.Projects.IndexOf(project);
        var group = project.GroupId;
        _document.Projects.Remove(project);
        var index = _document.Projects.IndexOf(anchor) + (after ? 1 : 0);
        _document.Projects.Insert(index, project);
        project.GroupId = anchor.GroupId;
        if (index != before || group != project.GroupId)
        {
            Raise(ProjectChangeKind.Groups, project);
        }
    }

    public void MoveGroup(string groupId, string anchorGroupId, bool after)
    {
        var group = FindGroup(groupId);
        var anchor = FindGroup(anchorGroupId);
        if (group is null || anchor is null || ReferenceEquals(group, anchor))
        {
            return;
        }
        var before = _document.Groups.IndexOf(group);
        _document.Groups.Remove(group);
        var index = _document.Groups.IndexOf(anchor) + (after ? 1 : 0);
        _document.Groups.Insert(index, group);
        if (index != before)
        {
            Raise(ProjectChangeKind.Groups);
        }
    }

    /// <summary>Moves the project into the group (null: none), after the projects already there.</summary>
    private void PlaceLast(Project project, string? groupId)
    {
        project.GroupId = groupId;
        _document.Projects.Remove(project);
        _document.Projects.Add(project);
    }

    private ProjectGroup? FindGroup(string id) => _document.Groups.FirstOrDefault(g => g.Id == id);

    // ---- lookups ----

    public Project? FindProjectOf(string itemId) => _document.Projects.FirstOrDefault(p => p.Items.Any(i => i.Id == itemId));

    public ProjectItem? FindItem(string itemId) =>
        _document.Projects.SelectMany(p => p.Items).FirstOrDefault(i => i.Id == itemId);

    public bool WasRestored(string itemId) => _restored.Contains(itemId);

    // ---- items ----

    public void AddItem(Project project, ProjectItem item)
    {
        project.Items.Add(item);
        project.ActiveItemId = item.Id;
        Raise(ProjectChangeKind.Items, project, item);
        Raise(ProjectChangeKind.Active, project, item, save: false);
    }

    public void RemoveItem(string itemId)
    {
        var project = FindProjectOf(itemId);
        var item = project?.Items.FirstOrDefault(i => i.Id == itemId);
        if (project is null || item is null)
        {
            return;
        }
        var position = project.Items.IndexOf(item);
        project.Items.Remove(item);
        _restored.Remove(itemId);
        if (project.ActiveItemId == itemId)
        {
            project.ActiveItemId = project.Items.Count == 0 ? null : project.Items[Math.Min(position, project.Items.Count - 1)].Id;
        }
        Raise(ProjectChangeKind.Items, project, item);
    }

    public void MoveItem(string itemId, string anchorItemId, bool after)
    {
        var project = FindProjectOf(itemId);
        var item = project?.Items.FirstOrDefault(i => i.Id == itemId);
        if (project is null || item is null || itemId == anchorItemId || project.Items.All(i => i.Id != anchorItemId))
        {
            return;
        }
        var before = project.Items.IndexOf(item);
        project.Items.Remove(item);
        var index = project.Items.FindIndex(i => i.Id == anchorItemId) + (after ? 1 : 0);
        project.Items.Insert(index, item);
        if (index != before)
        {
            Raise(ProjectChangeKind.Order, project, item);
        }
    }

    public void RenameItem(string itemId, string? customTitle)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return;
        }
        var value = string.IsNullOrWhiteSpace(customTitle) || customTitle.Trim() == item.Title ? null : customTitle.Trim();
        if (item.CustomTitle == value)
        {
            return;
        }
        item.CustomTitle = value;
        Raise(ProjectChangeKind.ItemTitle, FindProjectOf(itemId), item);
    }

    public void SetItemTitle(string itemId, string title)
    {
        var item = FindItem(itemId);
        if (item is null || string.IsNullOrWhiteSpace(title) || item.Title == title.Trim())
        {
            return;
        }
        item.Title = title.Trim();
        Raise(ProjectChangeKind.ItemTitle, FindProjectOf(itemId), item);
    }

    public void SetItemViewState(string itemId, string? state)
    {
        var item = FindItem(itemId);
        if (item is null || item.ViewState == state)
        {
            return;
        }
        item.ViewState = state;
        Raise(ProjectChangeKind.Layout, FindProjectOf(itemId));
    }

    public void ActivateItem(string itemId)
    {
        var project = FindProjectOf(itemId);
        var item = project?.Items.FirstOrDefault(i => i.Id == itemId);
        if (project is null || item is null)
        {
            return;
        }
        if (_document.ActiveProjectId != project.Id)
        {
            _document.ActiveProjectId = project.Id;
            Raise(ProjectChangeKind.CurrentProject, project, save: false);
        }
        if (project.ActiveItemId == itemId)
        {
            return;
        }
        project.ActiveItemId = itemId;
        Raise(ProjectChangeKind.Active, project, item);
    }

    public void SetDockLayout(Project project, string? layout)
    {
        if (project.DockLayout == layout)
        {
            return;
        }
        project.DockLayout = layout;
        Raise(ProjectChangeKind.Layout, project);
    }

    // ---- internals ----

    private Project? Find(string id) => _document.Projects.FirstOrDefault(p => p.Id == id);

    private static void Normalize(Project project)
    {
        if (project.ActiveItemId is null || project.Items.All(i => i.Id != project.ActiveItemId))
        {
            project.ActiveItemId = project.Items.FirstOrDefault()?.Id;
        }
    }

    private void Raise(ProjectChangeKind kind, Project? project = null, ProjectItem? item = null, bool save = true)
    {
        Changed?.Invoke(this, new ProjectChangedEventArgs(kind, project, item));
        if (save)
        {
            ScheduleSave();
        }
    }

    private void ScheduleSave()
    {
        CancellationTokenSource source;
        lock (_saveGate)
        {
            _pending?.Cancel();
            source = _pending = new CancellationTokenSource();
            _lastSave = SaveLaterAsync(source.Token);
        }
    }

    private async Task SaveLaterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDelay, token);
            await store.SaveAsync(_document, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // A failed background save is retried by the next change and by FlushAsync.
        }
    }
}
