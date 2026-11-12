namespace Octopus.App.Workspace;

/// <summary>What the dock needs from the control behind a tab.</summary>
public interface ITabContent
{
    /// <summary>True while a program runs in the tab (closing it needs a confirmation).</summary>
    Task<bool> HasRunningChildAsync();
    /// <summary>True while the tab holds edits that are not saved (closing it needs a confirmation).</summary>
    Task<bool> HasUnsavedChangesAsync() => Task.FromResult(false);
    /// <summary>Ends the process and lets go of what the tab holds.</summary>
    Task CloseAsync();
    Task RestartAsync();
    /// <summary>Moves the keyboard focus into the content.</summary>
    void FocusContent();
}
