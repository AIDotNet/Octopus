using Octopus.Core.Projects;
using Octopus.Core.Terminals;

namespace Octopus.App.Services;

/// <summary>Turns a terminal or agent item into the process a pty starts.</summary>
public interface IItemLaunchService
{
    LaunchSpec Plan(Project project, ProjectItem item);
}
