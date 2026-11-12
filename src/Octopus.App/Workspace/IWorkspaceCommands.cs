using Octopus.App.Files;

namespace Octopus.App.Workspace;

/// <summary>What the rest of the app can ask of the workspace (the dock layout of the current project).</summary>
public interface IWorkspaceCommands
{
    /// <summary>Puts the active tab in a new group on its right; with a single tab in the group, starts a terminal there.</summary>
    void SplitRight();
    /// <summary>Moves the item's tab into a new group on the right of its own.</summary>
    void MoveToNewGroup(string itemId);
    void CloseActiveTab();
    /// <summary>Activates the n-th tab (1-based) of the active group.</summary>
    void SelectTab(int number);
    void CycleTab(int delta);
    /// <summary>Closes the item, asking first when a program still runs in it.</summary>
    Task CloseItemAsync(string itemId);
    Task RestartItemAsync(string itemId);
    /// <summary>
    /// Opens the read-only diff of a changed file in the current project's workspace, or activates and refreshes the tab
    /// that already shows it. A diff tab is temporary: it is not one of the project's items and is not restored.
    /// </summary>
    void OpenDiff(DiffTabRequest request);
    /// <summary>Puts the keyboard focus into the active tab.</summary>
    void FocusActive();
}
