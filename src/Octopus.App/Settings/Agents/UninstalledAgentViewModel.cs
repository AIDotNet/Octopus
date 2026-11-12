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

    /// <summary>"Custom" or "Modified" when the user has a definition file of this agent; null for a built-in one as shipped.</summary>
    public string? OriginBadge { get; init; }

    public bool HasOriginBadge => OriginBadge is not null;

    /// <summary>The user's file can be removed: the agent is deleted, or the built-in one restored (<see cref="RemoveText"/> says which).</summary>
    public bool CanRemove { get; init; }

    public string RemoveText { get; init; } = "";

    public bool CanEdit { get; init; }

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
