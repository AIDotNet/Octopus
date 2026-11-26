using Octopus.Core.Agents;
using Octopus.Core.Projects;

namespace Octopus.App.Services;

/// <summary>Creates items in the current project; the dock layout puts the new tab in its active group.</summary>
public interface IItemOpener
{
    /// <summary>Pass as <c>workspaceId</c> to force the project's default directory rather than following the active tab's workspace.</summary>
    const string DefaultWorkspaceId = "";

    ProjectItem? OpenTerminal(string? workspaceId = null, string? workingDirectory = null);
    ProjectItem? OpenAgent(string agentId, AgentLaunchOptions? launch = null, string? workspaceId = null, string? workingDirectory = null);
    ProjectItem? OpenFile(string path);
    /// <summary>A file of the project's SSH host: copied here read-only and opened (see <c>ItemOpener</c>).</summary>
    Task<ProjectItem?> OpenRemoteFileAsync(string remotePath);
    /// <summary>A plugin view tab (design/plugins/06-标签与渲染.md §3); <paramref name="state"/> is the JSON it starts from.</summary>
    ProjectItem? OpenView(string viewId, string title, string? state = null);
    /// <summary>Asks for a file (starting in the workspace or project directory) and opens it.</summary>
    Task<ProjectItem?> PickAndOpenFileAsync(string? workspaceId = null);
    /// <summary>Asks for a directory and adds it as a project (or switches to it).</summary>
    Task<Project?> PickAndAddProjectAsync();
    /// <summary>Starts the default agent of the settings, if it is installed.</summary>
    ProjectItem? OpenDefaultAgent(string? workspaceId = null, AgentLaunchOptions? launch = null);
}
