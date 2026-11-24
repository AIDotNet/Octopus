using Octopus.Ssh;

namespace Octopus.App.Services;

/// <summary>Confirmations, anchored to the main window.</summary>
public interface IDialogService
{
    /// <summary>True when the confirm button was pressed.</summary>
    Task<bool> ConfirmAsync(string title, string description, string confirmText, bool isDanger = false);

    /// <summary>Asks for the folder and the name of a new project; null when the dialog was dismissed.</summary>
    Task<NewProjectChoice?> AskNewProjectAsync(NewProjectChoice? initial = null);

    /// <summary>Asks for a host to add to ~/.ssh/config; null when the dialog was dismissed.</summary>
    Task<SshHostDraft?> AskNewSshHostAsync();

    /// <summary>Asks for the changes to a host of ~/.ssh/config (its alias stays); null when the dialog was dismissed.</summary>
    Task<SshHostDraft?> AskEditSshHostAsync(SshHost host);

    /// <summary>
    /// Asks for a tunnel of a project on <paramref name="host"/> (design/11-SSH隧道.md): a new one, or the changes to
    /// <paramref name="initial"/>, which keeps its id. The form comes back until what is typed is valid; null when dismissed.
    /// </summary>
    Task<Octopus.Core.Projects.TunnelSpec?> AskTunnelAsync(Octopus.Core.Projects.TunnelSpec? initial, string host);

    /// <summary>Asks for one name (a new file, a rename); the trimmed name, or null when the dialog was dismissed.</summary>
    Task<string?> AskNameAsync(string title, string label, string? initial, string confirmText);

    /// <summary>
    /// Lets the person choose what the project's square shows; null when dismissed. A picture chosen in the dialog is
    /// already a thumbnail in the icon store; ones made and then not kept are deleted by the dialog.
    /// </summary>
    Task<ProjectAppearanceChoice?> AskProjectAppearanceAsync(Octopus.Core.Projects.Project project);

    /// <summary>
    /// Lets the person choose what a device's badge shows (the same color, icon or picture choices as projects); null when dismissed.
    /// </summary>
    Task<ProjectAppearanceChoice?> AskDeviceAppearanceAsync(
        string deviceKey,
        string deviceName,
        string subtitle,
        Octopus.Core.Projects.DeviceAppearance? current,
        string defaultColor,
        string? defaultIcon);

    /// <summary>
    /// Shows the agent editor until what it holds is a valid definition (then returns it) or the person cancels (null).
    /// </summary>
    Task<Octopus.Core.Agents.AgentDefinition?> AskAgentDefinitionAsync(Octopus.App.Settings.Agents.AgentEditorModel model);

    /// <summary>
    /// Runs an interactive OAuth login command inside an embedded terminal dialog. Returns true when the command exited
    /// cleanly or the user clicked Done; false when cancelled.
    /// </summary>
    Task<bool> RunAccountLoginAsync(string title, string description, Octopus.Core.Terminals.LaunchSpec spec, bool autoCloseOnSuccess = true);

    /// <summary>
    /// Shows the "New branch workspace" dialog (branch selection, optional GitHub Issue/PR or note binding, path preview).
    /// Returns the validated request, or null when dismissed.
    /// </summary>
    Task<Octopus.App.Services.Workspaces.CreateWorkspaceRequest?> AskCreateWorkspaceAsync(
        Octopus.Core.Projects.Project project,
        string repositoryRoot,
        IReadOnlyList<Octopus.Git.GitBranch> branches,
        IReadOnlyList<Octopus.Git.GitWorktree> worktrees,
        Octopus.App.Services.Workspaces.IGitHubTaskProvider taskProvider,
        Func<string, string> computePath,
        string homeDirectory,
        string? defaultAgentName,
        Octopus.App.Services.Workspaces.CreateWorkspaceRequest? initial = null,
        string? initialError = null);

    /// <summary>
    /// Shows the manual "Inspect workspaces" cleanup dialog listing all branch workspaces across local projects,
    /// allowing the user to review statuses/protections and remove selected ones. Returns the cleanup summary or null if closed.
    /// </summary>
    Task<Octopus.App.Services.Workspaces.WorkspaceCleanupRunSummary?> ShowInspectWorkspacesAsync(
        Octopus.App.Services.Workspaces.IWorkspaceCleanupService cleanup,
        IProjectService projects,
        Octopus.Platform.IFileOpener? fileOpener = null);
}

/// <summary>What the project icon dialog returns: the color, a Lucide icon name (null: the initial) and a thumbnail file (null: none).</summary>
public sealed record ProjectAppearanceChoice(string? Color, string? Icon, string? Image);

/// <summary>
/// What the new project dialog returns: a folder and the name to show (empty: the folder's name). With an SSH host the
/// folder is a path on that host and is not checked here; without one it is an existing local folder.
/// </summary>
/// <param name="RemoteHost">The device id of a paired Octopus host when the folder is there; null otherwise.</param>
public sealed record NewProjectChoice(string Path, string Name, string? SshHost = null, string? RemoteHost = null);
