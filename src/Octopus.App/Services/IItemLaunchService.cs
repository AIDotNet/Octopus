using Octopus.Core.Projects;
using Octopus.Core.Terminals;

namespace Octopus.App.Services;

/// <summary>Turns a terminal or agent item into the process a pty starts.</summary>
public interface IItemLaunchService
{
    /// <param name="allowResume">False to start the agent fresh even where it would continue its last session.</param>
    LaunchSpec Plan(Project project, ProjectItem item, bool allowResume = true);

    /// <summary>True when <see cref="Plan"/> starts this item by continuing its last session (a restored agent, with the setting on).</summary>
    bool Resumes(ProjectItem item);
}
