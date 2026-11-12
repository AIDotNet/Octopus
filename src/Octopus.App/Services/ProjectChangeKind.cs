namespace Octopus.App.Services;

public enum ProjectChangeKind
{
    /// <summary>A project was added or removed, renamed or recolored.</summary>
    Projects,
    /// <summary>The current project changed.</summary>
    CurrentProject,
    /// <summary>Items were added or removed.</summary>
    Items,
    /// <summary>An item moved to another place in the project's list; tabs keep their own order.</summary>
    Order,
    /// <summary>An item was renamed.</summary>
    ItemTitle,
    /// <summary>The active item changed.</summary>
    Active,
    /// <summary>Only the saved dock layout changed; nothing needs to be redrawn.</summary>
    Layout,
    /// <summary>
    /// The rail's grouping or order changed: a group was added, renamed, recolored, moved or removed, or a project moved
    /// into, out of or within a group. The workspace is not affected.
    /// </summary>
    Groups,
    /// <summary>Only which group is open on the rail changed: the rail opens and closes folders in place (animated).</summary>
    GroupExpanded,
    /// <summary>A tunnel of a project on an SSH host was added, edited or removed. Nothing but the tunnels is affected.</summary>
    Tunnels,
}
