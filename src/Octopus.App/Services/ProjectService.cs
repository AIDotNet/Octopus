using Octopus.App.Common;
using Octopus.Core.Projects;
using Octopus.Storage;

namespace Octopus.App.Services;

public sealed class ProjectService(IProjectStore store, PathComparer paths) : IProjectService
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
        Raise(ProjectChangeKind.Projects, save: false);
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

    public Project AddProject(string path, string? name = null)
    {
        var full = Path.GetFullPath(path);
        var existing = _document.Projects.FirstOrDefault(p => paths.Equals(p.Path, full));
        if (existing is not null)
        {
            SetCurrent(existing.Id);
            return existing;
        }
        var project = new Project
        {
            Path = full,
            Name = string.IsNullOrWhiteSpace(name) ? PathText.NameOf(full) : name.Trim(),
            Color = Palette[_document.Projects.Count % Palette.Length],
        };
        _document.Projects.Add(project);
        _document.ActiveProjectId = project.Id;
        Raise(ProjectChangeKind.Projects, project);
        Raise(ProjectChangeKind.CurrentProject, project, save: false);
        return project;
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
