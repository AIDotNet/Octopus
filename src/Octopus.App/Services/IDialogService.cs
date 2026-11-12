using Octopus.Ssh;

namespace Octopus.App.Services;

/// <summary>Confirmations, anchored to the main window.</summary>
public interface IDialogService
{
    /// <summary>True when the confirm button was pressed.</summary>
    Task<bool> ConfirmAsync(string title, string description, string confirmText, bool isDanger = false);

    /// <summary>Asks for the folder and the name of a new project; null when the dialog was dismissed.</summary>
    Task<NewProjectChoice?> AskNewProjectAsync();

    /// <summary>Asks for a host to add to ~/.ssh/config; null when the dialog was dismissed.</summary>
    Task<SshHostDraft?> AskNewSshHostAsync();

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
    /// Shows the agent editor until what it holds is a valid definition (then returns it) or the person cancels (null).
    /// </summary>
    Task<Octopus.Core.Agents.AgentDefinition?> AskAgentDefinitionAsync(Octopus.App.Settings.Agents.AgentEditorModel model);
}

/// <summary>What the project icon dialog returns: the color, a Lucide icon name (null: the initial) and a thumbnail file (null: none).</summary>
public sealed record ProjectAppearanceChoice(string? Color, string? Icon, string? Image);

/// <summary>
/// What the new project dialog returns: a folder and the name to show (empty: the folder's name). With an SSH host the
/// folder is a path on that host and is not checked here; without one it is an existing local folder.
/// </summary>
public sealed record NewProjectChoice(string Path, string Name, string? SshHost = null);
