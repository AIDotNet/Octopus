using Octopus.Core.Projects;

namespace Octopus.App.Services;

/// <summary>The in-memory projects of the app: every change goes through here and is saved after a short pause.</summary>
public interface IProjectService
{
    IReadOnlyList<Project> Projects { get; }
    Project? Current { get; }
    /// <summary>Canonical key of the device currently shown on the sidebar rail (<see cref="DeviceKeys"/>).</summary>
    string ActiveDeviceKey { get; }
    event EventHandler<ProjectChangedEventArgs>? Changed;

    /// <summary>Switches the sidebar rail to show only projects on <paramref name="deviceKey"/> and restores its last active project.</summary>
    void SetActiveDevice(string deviceKey);
    /// <summary>Returns the saved custom appearance for <paramref name="deviceKey"/>, or null when uncustomized.</summary>
    DeviceAppearance? GetDeviceAppearance(string deviceKey);
    /// <summary>Sets the custom color, icon (Lucide or pixel) or picture thumbnail for a device badge.</summary>
    void SetDeviceAppearance(string deviceKey, string? color, string? icon, string? image);

    Task LoadAsync();
    /// <summary>Writes pending changes now (at exit).</summary>
    Task FlushAsync();

    /// <summary>
    /// Adds a project for a directory, or for a folder on the SSH host <paramref name="sshHost"/>; returns the existing
    /// project when that folder is already one.
    /// </summary>
    /// <param name="activate">False adds it without making it the current project (somebody on another device added it).</param>
    Project AddProject(string path, string? name = null, string? sshHost = null, bool activate = true);
    /// <summary>
    /// Adds a project that lives on another computer running Octopus (design/12-远程操作.md): <paramref name="path"/> is
    /// its folder there, <paramref name="remoteId"/> its id there. Returns the existing project when it is already one.
    /// </summary>
    Project AddRemoteProject(string hostId, string remoteId, string path, string name);
    void RemoveProject(string projectId);
    void RenameProject(string projectId, string name);
    void SetProjectColor(string projectId, string? color);
    /// <summary>Sets what the project's square shows: a color, a Lucide icon (null: the initial) and a thumbnail file (null: none).</summary>
    void SetProjectAppearance(string projectId, string? color, string? icon, string? image);
    /// <summary>Binds an agent's default subscription account for one project; null or empty clears the override so the project follows global defaults.</summary>
    void SetProjectAgentAccount(string projectId, string agentId, string? accountId);
    void SetCurrent(string projectId);

    /// <summary>Adds a port forward to a project on an SSH host (design/11-SSH隧道.md); saved after a pause.</summary>
    void AddTunnel(Project project, TunnelSpec tunnel);
    /// <summary>Replaces the tunnel that has the same id with this one (a new object, so that what runs can tell it was edited).</summary>
    void UpdateTunnel(Project project, TunnelSpec tunnel);
    void RemoveTunnel(Project project, string tunnelId);

    /// <summary>Finds one of a project's git worktree workspaces by id; null when not found or <paramref name="workspaceId"/> is null/empty.</summary>
    ProjectWorkspace? FindWorkspace(Project project, string? workspaceId);
    /// <summary>Adds a git worktree workspace to a project; returns existing when the same path is already tracked.</summary>
    ProjectWorkspace AddWorkspace(Project project, ProjectWorkspace workspace);
    /// <summary>Updates branch name, task link or path of a tracked workspace.</summary>
    void UpdateWorkspace(Project project, ProjectWorkspace workspace);
    /// <summary>Removes a workspace record from the project (does not touch disk).</summary>
    void RemoveWorkspace(Project project, string workspaceId);
    /// <summary>Persists whether a workspace group is collapsed in the sidebar tree.</summary>
    void SetWorkspaceCollapsed(Project project, string workspaceId, bool collapsed);
    /// <summary>Pins or unpins a workspace (pinned workspaces are always protected from automatic cleanup).</summary>
    void SetWorkspacePinned(Project project, string workspaceId, bool pinned);
    /// <summary>Records the latest user or agent activity timestamp for a workspace (throttled, persisted silently).</summary>
    void TouchWorkspaceActivity(Project project, string workspaceId, DateTimeOffset timestamp);

    /// <summary>The groups of the rail, in order.</summary>
    IReadOnlyList<ProjectGroup> Groups { get; }
    /// <summary>The one open group; null when all are closed.</summary>
    string? ExpandedGroupId { get; }
    /// <summary>Adds a group holding the given projects and opens it (closing the open one).</summary>
    ProjectGroup AddGroup(string name, IEnumerable<string>? projectIds = null);
    void RenameGroup(string groupId, string name);
    void SetGroupColor(string groupId, string? color);
    /// <summary>Enables or disables merging the group's local project directories when launching agents that support multiple directories.</summary>
    void SetGroupMergeWorkspaces(string groupId, bool merge);
    /// <summary>Removes the group only: its projects stay, in no group.</summary>
    void RemoveGroup(string groupId);
    /// <summary>Opens a group, closing the one that was open; null closes the open one.</summary>
    void SetExpandedGroup(string? groupId);
    /// <summary>Puts the project last in a group, or last among the projects in no group (null).</summary>
    void MoveProjectToGroup(string projectId, string? groupId);
    /// <summary>Moves the project right after or right before another one, into that one's group (or out of any).</summary>
    void MoveProject(string projectId, string anchorProjectId, bool after);
    /// <summary>Moves the group right after or right before another group.</summary>
    void MoveGroup(string groupId, string anchorGroupId, bool after);

    Project? FindProjectOf(string itemId);
    ProjectItem? FindItem(string itemId);
    /// <summary>True for an item that was loaded from the saved file in this run (not created in it).</summary>
    bool WasRestored(string itemId);

    /// <summary>
    /// Adds the item and activates it. With <paramref name="activate"/> false it is only added: its tab opens behind the
    /// one the person is looking at (an item that somebody on another device created must not take the screen).
    /// </summary>
    void AddItem(Project project, ProjectItem item, bool activate = true);
    /// <summary>Removes the item; the dock layout closes its tab.</summary>
    void RemoveItem(string itemId);
    /// <summary>Moves the item next to another item of the same project: right after it, or right before it.</summary>
    void MoveItem(string itemId, string anchorItemId, bool after);
    void RenameItem(string itemId, string? customTitle);
    /// <summary>Plugin tabs: the default title the plugin set (the user's own title still wins).</summary>
    void SetItemTitle(string itemId, string title);
    /// <summary>Plugin tabs: the state the plugin saved to come back to; saved after a pause, nothing is redrawn.</summary>
    void SetItemViewState(string itemId, string? state);
    void ActivateItem(string itemId);
    /// <summary>Stores the dock layout JSON of a project (saved after a pause).</summary>
    void SetDockLayout(Project project, string? layout);
}
