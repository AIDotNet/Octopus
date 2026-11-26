using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents;
using Octopus.App.Localization;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.App.Settings.Agents;

/// <summary>
/// A catalog agent that was not found on this machine: its name, its install command (one click when a tool for it is
/// installed), and its homepage.
/// </summary>
public sealed partial class UninstalledAgentViewModel(AgentDefinition definition, IFileOpener opener) : ObservableObject
{
    public AgentDefinition Definition { get; } = definition;

    public string Id => Definition.Id;

    public string Name => Definition.Name;

    public bool SupportsAcp => Definition.SupportsAcp;

    public string AcpBadgeText => Definition.Acp switch
    {
        null => AppLangResources.Get(AppLangResourceKind.SettingsAcpBadgeTerminalOnly),
        { Mode: "adapter" } => AppLangResources.Get(AppLangResourceKind.SettingsAcpBadgeAdapter),
        _ => AppLangResources.Get(AppLangResourceKind.SettingsAcpBadgeNative),
    };

    /// <summary>"Custom" or "Modified" when the user has a definition file of this agent; null for a built-in one as shipped.</summary>
    public string? OriginBadge { get; init; }

    public bool HasOriginBadge => OriginBadge is not null;

    /// <summary>The user's file can be removed: the agent is deleted, or the built-in one restored (<see cref="RemoveText"/> says which).</summary>
    public bool CanRemove { get; init; }

    public string RemoveText { get; init; } = "";

    public bool CanEdit { get; init; }

    /// <summary>The command shown and copied: the one Octopus would run, else the catalog's first hint.</summary>
    public string InstallCommand => InstallPlan?.CommandLine ?? Definition.Detect.InstallHints.FirstOrDefault()?.Command ?? "";

    public bool HasInstallCommand => InstallCommand.Length > 0;

    /// <summary>Runs the install; set by the page. Null when installing from the app is not available.</summary>
    public Func<UninstalledAgentViewModel, Task>? Installer { get; init; }

    /// <summary>The hint Octopus can run here, found after the row is shown; null while unknown or when none fits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallCommand), nameof(HasInstallCommand), nameof(CanInstall), nameof(ShowInstallButton), nameof(InstallButtonText))]
    public partial AgentInstallPlan? InstallPlan { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstallButton))]
    public partial bool IsInstalling { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallError))]
    public partial string? InstallError { get; set; }

    public bool HasInstallError => !string.IsNullOrEmpty(InstallError);

    /// <summary>The last install failed for lack of rights: the button installs as administrator (sudo / UAC).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    public partial bool NeedsAdmin { get; set; }

    public string InstallButtonText => AppLangResources.Get(
        NeedsAdmin || InstallPlan?.Elevation == InstallElevation.Required
            ? AppLangResourceKind.SettingsInstallAsAdmin
            : AppLangResourceKind.SettingsInstallAgent);

    public bool CanInstall => InstallPlan is not null && Installer is not null;

    public bool ShowInstallButton => CanInstall && !IsInstalling;

    [RelayCommand]
    private Task RunInstall() => CanInstall && !IsInstalling ? Installer!(this) : Task.CompletedTask;

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
