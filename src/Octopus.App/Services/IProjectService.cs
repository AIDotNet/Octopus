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

    /// <summary>Adds a project for a directory; returns the existing project when the directory is already there.</summary>
    Project AddProject(string path, string? name = null);
    void RemoveProject(string projectId);
    void RenameProject(string projectId, string name);
    void SetProjectColor(string projectId, string? color);
    void SetCurrent(string projectId);

    Project? FindProjectOf(string itemId);
    ProjectItem? FindItem(string itemId);
    /// <summary>True for an item that was loaded from the saved file in this run (not created in it).</summary>
    bool WasRestored(string itemId);

    /// <summary>Adds the item and activates it.</summary>
    void AddItem(Project project, ProjectItem item);
    /// <summary>Removes the item; the dock layout closes its tab.</summary>
    void RemoveItem(string itemId);
    void RenameItem(string itemId, string? customTitle);
    void ActivateItem(string itemId);
    /// <summary>Stores the dock layout JSON of a project (saved after a pause).</summary>
    void SetDockLayout(Project project, string? layout);
}
