using CommunityToolkit.Mvvm.Input;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.App.Settings.Agents;

/// <summary>A catalog agent that was not found on this machine: its name, its first install command and its homepage.</summary>
public sealed partial class UninstalledAgentViewModel(AgentDefinition definition, IFileOpener opener)
{
    public AgentDefinition Definition { get; } = definition;

    public string Id => Definition.Id;

    public string Name => Definition.Name;

    public string InstallCommand => Definition.Detect.InstallHints.FirstOrDefault()?.Command ?? "";

    public bool HasInstallCommand => InstallCommand.Length > 0;

    public bool HasHomepage => !string.IsNullOrWhiteSpace(Definition.Homepage);

    [RelayCommand]
    private void OpenHomepage()
    {
        if (HasHomepage)
        {
            opener.Open(Definition.Homepage!);
        }
    }
}
