using Octopus.App.Common;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Storage;

namespace Octopus.App.Services;

public sealed class ProjectService(IProjectStore store, PathComparer paths, ISettingsStore? settings = null) : IProjectService
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

    public string ActiveDeviceKey => DeviceKeys.Normalize(_document.ActiveDeviceKey);

    public Project? Current =>
        _document.ActiveProjectId is { } activeId
            ? _document.Projects.FirstOrDefault(p => p.Id == activeId)
            : null;

    public async Task LoadAsync()
    {
        _document = await store.LoadAsync();
        _restored.Clear();
        var dropped = DropRunnableItems();
        foreach (var project in _document.Projects)
        {
            foreach (var item in project.Items)
            {
                _restored.Add(item.Id);
            }
            Normalize(project);
        }
        var current = _document.ActiveProjectId is { } savedActive
            ? _document.Projects.FirstOrDefault(p => p.Id == savedActive)
            : null;
        if (string.IsNullOrWhiteSpace(_document.ActiveDeviceKey))
        {
            _document.ActiveDeviceKey = current is not null ? DeviceKeys.Of(current) : DeviceKeys.Local;
        }
        else
        {
            _document.ActiveDeviceKey = DeviceKeys.Normalize(_document.ActiveDeviceKey);
        }
        var deviceKey = ActiveDeviceKey;
        if (current is null || !DeviceKeys.Matches(current, deviceKey))
        {
            var preferred = PickProjectForDevice(deviceKey);
            _document.ActiveProjectId = preferred?.Id;
        }
        if (Current is { } resolved)
        {
            _document.LastProjectByDevice[DeviceKeys.Of(resolved)] = resolved.Id;
        }
        Raise(ProjectChangeKind.Projects, save: dropped);
    }

    /// <summary>
    /// Open terminals are not remembered across restarts (their shell processes end with the app). Local agents stay
    /// when "Restore agents on startup" is enabled; remote host copies and disabled agents are dropped. A saved layout
    /// that named any dropped tab is cleared with them.
    /// </summary>
    private bool DropRunnableItems()
    {
        var any = false;
        foreach (var project in _document.Projects)
        {
            if (project.Items.RemoveAll(item => IsTransientItem(project, item)) > 0)
            {
                project.DockLayout = null;
                any = true;
            }
        }
        return any;
    }

    private bool RestoreAgentsEnabled => settings?.Current.Terminal.RestoreItemsOnStartup ?? true;

    private bool IsTransientItem(Project project, ProjectItem item) => item.Kind switch
    {
        ProjectItemKind.Terminal => true,
        ProjectItemKind.Agent => !RestoreAgentsEnabled || ProjectLocation.IsOnRemoteHost(project),
        _ => false,
    };

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
        await store.SaveAsync(SnapshotForSave());
    }

    // ---- projects ----

    public Project AddProject(string path, string? name = null, string? sshHost = null, bool activate = true)
    {
        var host = string.IsNullOrWhiteSpace(sshHost) ? null : sshHost.Trim();
        // A folder on another machine is that machine's path: it is kept as typed, never made absolute here.
        var full = host is null ? Path.GetFullPath(path) : RemotePath(path);
        var existing = _document.Projects.FirstOrDefault(p => p.RemoteHost is null && p.SshHost == host && (host is null ? paths.Equals(p.Path, full) : p.Path == full));
        if (existing is not null)
        {
            if (activate) SetCurrent(existing.Id);
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
        // Without a current project there is nothing on screen to keep: the new one becomes it either way.
        if (!activate && _document.ActiveProjectId is not null)
        {
            Raise(ProjectChangeKind.Projects, project);
            return project;
        }
        var deviceKey = DeviceKeys.Of(project);
        var deviceChanged = !string.Equals(ActiveDeviceKey, deviceKey, StringComparison.Ordinal);
        _document.ActiveDeviceKey = deviceKey;
        _document.ActiveProjectId = project.Id;
        _document.LastProjectByDevice[deviceKey] = project.Id;
        Raise(ProjectChangeKind.Projects, project);
        if (deviceChanged)
        {
            Raise(ProjectChangeKind.Device, project, save: false);
        }
        Raise(ProjectChangeKind.CurrentProject, project, save: false);
        return project;
    }

    public Project AddRemoteProject(string hostId, string remoteId, string path, string name)
    {
        var existing = _document.Projects.FirstOrDefault(p => p.RemoteHost == hostId && p.RemoteId == remoteId);
        if (existing is not null)
        {
            SetCurrent(existing.Id);
            return existing;
        }
        var project = new Project
        {
            Path = path,
            RemoteHost = hostId,
            RemoteId = remoteId,
            Name = string.IsNullOrWhiteSpace(name) ? PathText.NameOf(path) : name.Trim(),
            Color = Palette[_document.Projects.Count % Palette.Length],
        };
        _document.Projects.Add(project);
        var deviceKey = DeviceKeys.Of(project);
        var deviceChanged = !string.Equals(ActiveDeviceKey, deviceKey, StringComparison.Ordinal);
        _document.ActiveDeviceKey = deviceKey;
        _document.ActiveProjectId = project.Id;
        _document.LastProjectByDevice[deviceKey] = project.Id;
        Raise(ProjectChangeKind.Projects, project);
        if (deviceChanged)
        {
            Raise(ProjectChangeKind.Device, project, save: false);
        }
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
        var deviceKey = DeviceKeys.Of(project);
        var sameDevice = _document.Projects.Where(p => DeviceKeys.Matches(p, deviceKey)).ToList();
        var sameDeviceIndex = sameDevice.IndexOf(project);
        _document.Projects.Remove(project);
        foreach (var k in _document.LastProjectByDevice.Where(kv => kv.Value == projectId).Select(kv => kv.Key).ToList())
        {
            _document.LastProjectByDevice.Remove(k);
        }
        var wasCurrent = _document.ActiveProjectId == projectId;
        if (wasCurrent)
        {
            var remainingOnDevice = _document.Projects.Where(p => DeviceKeys.Matches(p, ActiveDeviceKey)).ToList();
            if (remainingOnDevice.Count > 0)
            {
                var next = remainingOnDevice[Math.Min(Math.Max(0, sameDeviceIndex), remainingOnDevice.Count - 1)];
                _document.ActiveProjectId = next.Id;
                _document.LastProjectByDevice[ActiveDeviceKey] = next.Id;
            }
            else
            {
                _document.ActiveProjectId = null;
            }
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

    public void SetProjectAgentAccount(string projectId, string agentId, string? accountId)
    {
        var project = Find(projectId);
        if (project is null || string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }
        var key = agentId.Trim();
        var value = string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim();
        project.AgentAccounts ??= [];
        if (value is null)
        {
            if (!project.AgentAccounts.Remove(key)) return;
        }
        else
        {
            if (project.AgentAccounts.TryGetValue(key, out var existing) && existing == value) return;
            project.AgentAccounts[key] = value;
        }
        Raise(ProjectChangeKind.ProjectAccounts, project);
    }

    public void SetCurrent(string projectId)
    {
        var project = Find(projectId);
        if (project is null)
        {
            return;
        }
        var deviceKey = DeviceKeys.Of(project);
        var deviceChanged = !string.Equals(ActiveDeviceKey, deviceKey, StringComparison.Ordinal);
        if (!deviceChanged && _document.ActiveProjectId == projectId)
        {
            return;
        }
        _document.ActiveDeviceKey = deviceKey;
        _document.ActiveProjectId = projectId;
        _document.LastProjectByDevice[deviceKey] = projectId;
        if (deviceChanged)
        {
            Raise(ProjectChangeKind.Device, project, save: false);
        }
        Raise(ProjectChangeKind.CurrentProject, project);
    }

    public void SetActiveDevice(string deviceKey)
    {
        var normalized = DeviceKeys.Normalize(deviceKey);
        if (string.Equals(ActiveDeviceKey, normalized, StringComparison.Ordinal))
        {
            return;
        }
        if (Current is { } current)
        {
            _document.LastProjectByDevice[DeviceKeys.Of(current)] = current.Id;
        }
        _document.ActiveDeviceKey = normalized;
        var next = PickProjectForDevice(normalized);
        var previousId = _document.ActiveProjectId;
        _document.ActiveProjectId = next?.Id;
        if (next is not null)
        {
            _document.LastProjectByDevice[normalized] = next.Id;
        }
        Raise(ProjectChangeKind.Device, next);
        if (!string.Equals(previousId, _document.ActiveProjectId, StringComparison.Ordinal))
        {
            Raise(ProjectChangeKind.CurrentProject, next, save: false);
        }
    }

    public DeviceAppearance? GetDeviceAppearance(string deviceKey) =>
        _document.DeviceAppearances.TryGetValue(DeviceKeys.Normalize(deviceKey), out var appearance)
            ? appearance
            : null;

    public void SetDeviceAppearance(string deviceKey, string? color, string? icon, string? image)
    {
        var normalized = DeviceKeys.Normalize(deviceKey);
        var normalizedColor = string.IsNullOrWhiteSpace(color) ? null : color.Trim();
        var normalizedIcon = string.IsNullOrWhiteSpace(icon) ? null : icon.Trim();
        var normalizedImage = string.IsNullOrWhiteSpace(image) ? null : image.Trim();
        if (normalizedColor is null && normalizedIcon is null && normalizedImage is null)
        {
            if (!_document.DeviceAppearances.Remove(normalized)) return;
        }
        else
        {
            _document.DeviceAppearances[normalized] = new DeviceAppearance
            {
                Color = normalizedColor,
                Icon = normalizedIcon,
                Image = normalizedImage,
            };
        }
        Raise(ProjectChangeKind.Device);
    }

    private Project? PickProjectForDevice(string deviceKey)
    {
        if (_document.LastProjectByDevice.TryGetValue(deviceKey, out var lastId)
            && Find(lastId) is { } remembered
            && DeviceKeys.Matches(remembered, deviceKey))
        {
            return remembered;
        }
        return _document.Projects.FirstOrDefault(p => DeviceKeys.Matches(p, deviceKey));
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

    public ProjectWorkspace? FindWorkspace(Project project, string? workspaceId) =>
        string.IsNullOrEmpty(workspaceId) ? null : project.Workspaces.FirstOrDefault(w => w.Id == workspaceId);

    public ProjectWorkspace AddWorkspace(Project project, ProjectWorkspace workspace)
    {
        var full = Path.GetFullPath(workspace.Path);
        var existing = project.Workspaces.FirstOrDefault(w => paths.Equals(w.Path, full));
        if (existing is not null)
        {
            var changed = false;
            if (!string.IsNullOrWhiteSpace(workspace.Branch) && existing.Branch != workspace.Branch)
            {
                existing.Branch = workspace.Branch;
                changed = true;
            }
            if (workspace.Task is not null)
            {
                existing.Task = workspace.Task;
                changed = true;
            }
            if (changed) Raise(ProjectChangeKind.Workspaces, project);
            return existing;
        }
        workspace.Path = full;
        if (string.IsNullOrWhiteSpace(workspace.Name)) workspace.Name = workspace.Branch;
        project.Workspaces.Add(workspace);
        Raise(ProjectChangeKind.Workspaces, project);
        return workspace;
    }

    public void UpdateWorkspace(Project project, ProjectWorkspace workspace)
    {
        var index = project.Workspaces.FindIndex(w => w.Id == workspace.Id);
        if (index < 0) return;
        project.Workspaces[index] = workspace;
        Raise(ProjectChangeKind.Workspaces, project);
    }

    public void RemoveWorkspace(Project project, string workspaceId)
    {
        if (project.Workspaces.RemoveAll(w => w.Id == workspaceId) == 0) return;
        foreach (var item in project.Items.Where(i => i.WorkspaceId == workspaceId))
        {
            item.WorkspaceId = null;
        }
        Raise(ProjectChangeKind.Workspaces, project);
    }

    public void SetWorkspaceCollapsed(Project project, string workspaceId, bool collapsed)
    {
        var workspace = FindWorkspace(project, workspaceId);
        if (workspace is null || workspace.IsCollapsed == collapsed) return;
        workspace.IsCollapsed = collapsed;
        Raise(ProjectChangeKind.Workspaces, project);
    }

    public void SetWorkspacePinned(Project project, string workspaceId, bool pinned)
    {
        var workspace = FindWorkspace(project, workspaceId);
        if (workspace is null || workspace.IsPinned == pinned) return;
        workspace.IsPinned = pinned;
        Raise(ProjectChangeKind.Workspaces, project);
    }

    public void TouchWorkspaceActivity(Project project, string workspaceId, DateTimeOffset timestamp)
    {
        var workspace = FindWorkspace(project, workspaceId);
        if (workspace is null) return;
        if (workspace.LastActivityAt is { } previous && timestamp - previous < TimeSpan.FromMinutes(5)) return;
        workspace.LastActivityAt = timestamp;
        ScheduleSave();
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

    public void SetGroupMergeWorkspaces(string groupId, bool merge)
    {
        var group = FindGroup(groupId);
        if (group is null || group.MergeWorkspaces == merge)
        {
            return;
        }
        group.MergeWorkspaces = merge;
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

    public void AddItem(Project project, ProjectItem item, bool activate = true)
    {
        project.Items.Add(item);
        if (!activate)
        {
            Raise(ProjectChangeKind.Items, project, item);
            return;
        }
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

    public void SetItemSessionId(string itemId, string? sessionId, bool isAcp = false)
    {
        var item = FindItem(itemId);
        if (item is null || item.Kind != ProjectItemKind.Agent)
        {
            return;
        }
        var normalized = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        if (isAcp)
        {
            var clearLaunch = normalized is null && item.Launch?.SessionId is not null;
            if (string.Equals(item.AcpSessionId, normalized, StringComparison.Ordinal) && !clearLaunch)
            {
                return;
            }
            item.AcpSessionId = normalized;
            if (normalized is null && item.Launch is not null)
            {
                item.Launch.SessionId = null;
            }
        }
        else
        {
            if (string.Equals(item.Launch?.SessionId, normalized, StringComparison.Ordinal))
            {
                return;
            }
            if (normalized is not null)
            {
                item.Launch ??= new AgentLaunchOptions();
                item.Launch.SessionId = normalized;
            }
            else
            {
                item.Launch?.SessionId = null;
            }
        }
        ScheduleSave();
    }

    public void ActivateItem(string itemId)
    {
        var project = FindProjectOf(itemId);
        var item = project?.Items.FirstOrDefault(i => i.Id == itemId);
        if (project is null || item is null)
        {
            return;
        }
        var deviceKey = DeviceKeys.Of(project);
        var deviceChanged = !string.Equals(ActiveDeviceKey, deviceKey, StringComparison.Ordinal);
        if (deviceChanged || _document.ActiveProjectId != project.Id)
        {
            _document.ActiveDeviceKey = deviceKey;
            _document.ActiveProjectId = project.Id;
            _document.LastProjectByDevice[deviceKey] = project.Id;
            if (deviceChanged)
            {
                Raise(ProjectChangeKind.Device, project, save: false);
            }
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
            await store.SaveAsync(SnapshotForSave(), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // A failed background save is retried by the next change and by FlushAsync.
        }
    }

    /// <summary>
    /// Builds the document written to disk: open terminals (and agents when restore is disabled or the project is on a
    /// remote host) are omitted, while projects, groups, workspaces, tunnels, open files/plugin tabs and restorable
    /// agents are preserved.
    /// </summary>
    private ProjectsDocument SnapshotForSave() => new()
    {
        Version = _document.Version,
        ActiveProjectId = _document.ActiveProjectId,
        ExpandedGroupId = _document.ExpandedGroupId,
        ActiveDeviceKey = ActiveDeviceKey,
        LastProjectByDevice = new Dictionary<string, string>(_document.LastProjectByDevice, StringComparer.Ordinal),
        DeviceAppearances = _document.DeviceAppearances.ToDictionary(
            kv => kv.Key,
            kv => new DeviceAppearance { Color = kv.Value.Color, Icon = kv.Value.Icon, Image = kv.Value.Image },
            StringComparer.Ordinal),
        Groups = [.. _document.Groups],
        Projects = [.. _document.Projects.Select(SnapshotProject)],
    };

    private Project SnapshotProject(Project project)
    {
        var hasTransient = project.Items.Any(i => IsTransientItem(project, i));
        var items = hasTransient ? project.Items.Where(i => !IsTransientItem(project, i)).ToList() : [.. project.Items];
        var activeItemId = project.ActiveItemId is { } active && items.Any(i => i.Id == active)
            ? active
            : items.FirstOrDefault()?.Id;
        return new Project
        {
            Id = project.Id,
            Name = project.Name,
            Path = project.Path,
            SshHost = project.SshHost,
            RemoteHost = project.RemoteHost,
            RemoteId = project.RemoteId,
            Color = project.Color,
            Icon = project.Icon,
            Image = project.Image,
            IsExpanded = project.IsExpanded,
            GroupId = project.GroupId,
            ActiveItemId = activeItemId,
            DockLayout = hasTransient ? null : project.DockLayout,
            Items = items,
            Workspaces = [.. project.Workspaces],
            Tunnels = [.. project.Tunnels],
            AgentAccounts = new Dictionary<string, string>(project.AgentAccounts, StringComparer.Ordinal),
            CreatedAt = project.CreatedAt,
        };
    }
}
