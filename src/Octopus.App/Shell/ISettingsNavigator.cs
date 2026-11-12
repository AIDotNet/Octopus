namespace Octopus.App.Shell;

/// <summary>Opens the settings page over the main window.</summary>
public interface ISettingsNavigator
{
    /// <param name="section">Key of a settings section (<c>agents</c>, <c>general</c>, ...), or null for the first.</param>
    void OpenSettings(string? section = null);
}
