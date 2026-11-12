using Octopus.Core.Agents;
using Octopus.Core.Projects;

namespace Octopus.App.Services;

/// <summary>Creates items in the current project; the dock layout puts the new tab in its active group.</summary>
public interface IItemOpener
{
    ProjectItem? OpenTerminal();
    ProjectItem? OpenAgent(string agentId, AgentLaunchOptions? launch = null);
    ProjectItem? OpenFile(string path);
    /// <summary>A file of the project's SSH host: copied here read-only and opened (see <c>ItemOpener</c>).</summary>
    Task<ProjectItem?> OpenRemoteFileAsync(string remotePath);
    /// <summary>A plugin view tab (design/plugins/06-标签与渲染.md §3); <paramref name="state"/> is the JSON it starts from.</summary>
    ProjectItem? OpenView(string viewId, string title, string? state = null);
    /// <summary>Asks for a file (starting in the project directory) and opens it.</summary>
    Task<ProjectItem?> PickAndOpenFileAsync();
    /// <summary>Asks for a directory and adds it as a project (or switches to it).</summary>
    Task<Project?> PickAndAddProjectAsync();
    /// <summary>Starts the default agent of the settings, if it is installed.</summary>
    ProjectItem? OpenDefaultAgent();
}
