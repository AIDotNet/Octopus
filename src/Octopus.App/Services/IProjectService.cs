using Octopus.Core.Projects;

namespace Octopus.App.Services;

/// <summary>The in-memory projects of the app: every change goes through here and is saved after a short pause.</summary>
public interface IProjectService
{
    IReadOnlyList<Project> Projects { get; }
    Project? Current { get; }
    event EventHandler<ProjectChangedEventArgs>? Changed;

    Task LoadAsync();
    /// <summary>Writes pending changes now (at exit).</summary>
    Task FlushAsync();

    /// <summary>
    /// Adds a project for a directory, or for a folder on the SSH host <paramref name="sshHost"/>; returns the existing
    /// project when that folder is already one.
    /// </summary>
    Project AddProject(string path, string? name = null, string? sshHost = null);
    void RemoveProject(string projectId);
    void RenameProject(string projectId, string name);
    void SetProjectColor(string projectId, string? color);
    /// <summary>Sets what the project's square shows: a color, a Lucide icon (null: the initial) and a thumbnail file (null: none).</summary>
    void SetProjectAppearance(string projectId, string? color, string? icon, string? image);
    void SetCurrent(string projectId);

    /// <summary>The groups of the rail, in order.</summary>
    IReadOnlyList<ProjectGroup> Groups { get; }
    /// <summary>The one open group; null when all are closed.</summary>
    string? ExpandedGroupId { get; }
    /// <summary>Adds a group holding the given projects and opens it (closing the open one).</summary>
    ProjectGroup AddGroup(string name, IEnumerable<string>? projectIds = null);
    void RenameGroup(string groupId, string name);
    void SetGroupColor(string groupId, string? color);
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

    /// <summary>Adds the item and activates it.</summary>
    void AddItem(Project project, ProjectItem item);
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
