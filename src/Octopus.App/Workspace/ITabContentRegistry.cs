using Avalonia.Controls;
using Octopus.App.Files;
using Octopus.Core.Projects;

namespace Octopus.App.Workspace;

/// <summary>
/// One content control per item, created once and kept until the item is closed: switching tabs, groups or projects only
/// changes what is shown, so a terminal's process is never touched.
/// </summary>
public interface ITabContentRegistry
{
    Control GetOrCreate(Project project, ProjectItem item);
    /// <summary>
    /// The control of a diff tab, created once per <see cref="DiffTabRequest.Key"/>. A diff tab is not a project item:
    /// it is never saved, and <see cref="ReleaseAsync"/> with the key ends it like any other tab.
    /// </summary>
    Control GetOrCreateDiff(DiffTabRequest request);
    ITabContent? Find(string itemId);
    /// <summary>
    /// Closes the content of the item (ends its process) and forgets it. A second call while the first one is still
    /// ending the process waits for the same end.
    /// </summary>
    Task ReleaseAsync(string itemId);
    /// <summary>Closes every content and waits until every process has ended, including those already being released.</summary>
    Task ReleaseAllAsync();
    /// <summary>True when a program runs in at least one tab (quitting needs a confirmation).</summary>
    Task<bool> AnyRunningChildAsync();
    /// <summary>True when a tab holds edits that are not saved (quitting needs a confirmation).</summary>
    Task<bool> AnyUnsavedChangesAsync();
}
