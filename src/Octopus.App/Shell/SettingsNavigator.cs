namespace Octopus.App.Shell;

/// <summary>Lets any view model ask for the settings page; the shell shows it.</summary>
public sealed class SettingsNavigator : ISettingsNavigator
{
    public event EventHandler<string?>? Requested;

    public void OpenSettings(string? section = null) => Requested?.Invoke(this, section);
}
