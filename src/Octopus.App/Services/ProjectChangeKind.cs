namespace Octopus.App.Services;

public enum ProjectChangeKind
{
    /// <summary>A project was added or removed, renamed or recolored.</summary>
    Projects,
    /// <summary>The current project changed.</summary>
    CurrentProject,
    /// <summary>Items were added or removed.</summary>
    Items,
    /// <summary>An item was renamed.</summary>
    ItemTitle,
    /// <summary>The active item changed.</summary>
    Active,
    /// <summary>Only the saved dock layout changed; nothing needs to be redrawn.</summary>
    Layout,
}
