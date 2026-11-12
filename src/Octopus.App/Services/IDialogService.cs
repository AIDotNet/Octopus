namespace Octopus.App.Services;

/// <summary>Confirmations, anchored to the main window.</summary>
public interface IDialogService
{
    /// <summary>True when the confirm button was pressed.</summary>
    Task<bool> ConfirmAsync(string title, string description, string confirmText, bool isDanger = false);

    /// <summary>Asks for the folder and the name of a new project; null when the dialog was dismissed.</summary>
    Task<NewProjectChoice?> AskNewProjectAsync();
}

/// <summary>What the new project dialog returns: an existing folder and the name to show (empty: the folder's name).</summary>
public sealed record NewProjectChoice(string Path, string Name);
