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
    /// The item's tab was closed but the item keeps running: lets go of what only draws it and keeps what runs it.
    /// <see cref="GetOrCreate"/> gives it a control again; <see cref="ReleaseAsync"/> still ends it.
    /// </summary>
    void Detach(string itemId);
    /// <summary>
    /// Captures and unregisters the current owner of <paramref name="itemId"/> so any later
    /// <see cref="GetOrCreate"/> starts a replacement owner while disposal of the captured owner may be deferred
    /// across a dispatcher or async boundary. Returns null when no active owner exists to capture.
    /// </summary>
    Func<Task>? CaptureForRelease(string itemId);
    /// <summary>
    /// Waits for any captured or in-flight release of <paramref name="itemId"/> to finish (starting any deferred
    /// captured releases for <paramref name="itemId"/>) without disturbing a currently active owner.
    /// </summary>
    Task WaitForReleaseAsync(string itemId);
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
