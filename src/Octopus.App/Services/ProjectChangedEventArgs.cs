using Octopus.Core.Projects;

namespace Octopus.App.Services;

public sealed class ProjectChangedEventArgs(ProjectChangeKind kind, Project? project = null, ProjectItem? item = null) : EventArgs
{
    public ProjectChangeKind Kind { get; } = kind;
    public Project? Project { get; } = project;
    public ProjectItem? Item { get; } = item;
}
