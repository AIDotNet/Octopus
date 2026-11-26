using Octopus.Agents;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Storage;
using Octopus.Terminal;

namespace Octopus.App.Settings;

/// <summary>What happened when the user clicked an install button.</summary>
/// <param name="Cancelled">The user declined the confirmation or closed the terminal: say nothing.</param>
/// <param name="Succeeded">The command reported success (or the user closed the terminal with Done): rescan to be sure.</param>
/// <param name="NeedsAdmin">It failed for lack of rights: offer "Install as administrator".</param>
/// <param name="Error">One line for the row when it failed; null otherwise.</param>
internal sealed record InstallOutcome(bool Cancelled, bool Succeeded, bool NeedsAdmin, string? Error);

/// <summary>
/// The install button of the Agents and Language server pages. Asks first (showing the exact command), then runs it:
/// in the background when no rights are needed; through UAC on Windows when they are; in an embedded terminal on
/// macOS/Linux for <c>sudo</c> (so the user types the password there, never into Octopus) and for install scripts
/// (which may call sudo or ask questions themselves).
/// </summary>
internal sealed class InstallFlow(
    IAgentInstaller installer,
    IDialogService? dialogs,
    ITerminalLaunchPlanner? launchPlanner,
    ISettingsStore settings)
{
    private bool CanUseTerminal => dialogs is not null && launchPlanner is not null;

    /// <param name="name">Shown in the dialogs ("Install Codex?").</param>
    /// <param name="asAdmin">The user clicked "Install as administrator" after a permission failure.</param>
    public async Task<InstallOutcome> RunAsync(string name, AgentInstallPlan plan, bool asAdmin)
    {
        var elevate = asAdmin || plan.Elevation == InstallElevation.Required;
        var inTerminal = !installer.CanElevateInBackground && (elevate || plan.IsScript) && CanUseTerminal;

        if (elevate && !installer.CanElevateInBackground && !CanUseTerminal)
        {
            // No way to ask for the password here: tell the user what to run themselves.
            return new InstallOutcome(false, false, true,
                AppLangResources.Format(AppLangResourceKind.SettingsInstallRunWithSudo, installer.BuildTerminalCommand(plan, elevated: true).Arguments[^1]));
        }

        var shownCommand = inTerminal ? installer.BuildTerminalCommand(plan, elevate).Arguments[^1] : plan.CommandLine;
        if (dialogs is not null)
        {
            var description = (elevate, inTerminal, plan.IsScript) switch
            {
                (true, true, _) => AppLangResources.Format(AppLangResourceKind.SettingsInstallSudoDescription, shownCommand),
                (true, false, _) => AppLangResources.Format(AppLangResourceKind.SettingsInstallUacDescription, shownCommand),
                (false, true, true) => AppLangResources.Format(AppLangResourceKind.SettingsInstallScriptTerminalDescription, shownCommand),
                (false, false, true) => AppLangResources.Format(AppLangResourceKind.SettingsInstallAgentScriptDescription, shownCommand),
                _ when plan.Elevation == InstallElevation.Possible =>
                    AppLangResources.Format(AppLangResourceKind.SettingsInstallMayPromptDescription, shownCommand),
                _ => AppLangResources.Format(AppLangResourceKind.SettingsInstallAgentDescription, shownCommand),
            };
            var confirmed = await dialogs.ConfirmAsync(
                AppLangResources.Format(AppLangResourceKind.SettingsInstallAgentTitle, name),
                description,
                AppLangResources.Get(elevate ? AppLangResourceKind.SettingsInstallAsAdmin : AppLangResourceKind.SettingsInstallAgent));
            if (!confirmed) return new InstallOutcome(true, false, false, null);
        }

        if (inTerminal)
        {
            var command = installer.BuildTerminalCommand(plan, elevate);
            var spec = launchPlanner!.PlanAgent(command, null, settings.Current.Terminal.Shell);
            var done = await dialogs!.RunAccountLoginAsync(
                AppLangResources.Format(AppLangResourceKind.SettingsInstallTerminalTitle, name),
                AppLangResources.Get(elevate
                    ? AppLangResourceKind.SettingsInstallTerminalSudoHint
                    : AppLangResourceKind.SettingsInstallTerminalHint),
                spec,
                autoCloseOnSuccess: true);
            return done ? new InstallOutcome(false, true, false, null) : new InstallOutcome(true, false, false, null);
        }

        var result = elevate ? await installer.InstallElevatedAsync(plan) : await installer.InstallAsync(plan);
        if (result.Succeeded) return new InstallOutcome(false, true, false, null);
        var needsAdmin = result.NeedsAdmin && !elevate;
        var error = needsAdmin
            ? AppLangResources.Get(AppLangResourceKind.SettingsInstallNeedsAdmin)
            : AppLangResources.Format(AppLangResourceKind.SettingsInstallAgentFailed, LastLine(result.Output));
        return new InstallOutcome(false, false, needsAdmin, error);
    }

    internal static string LastLine(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }
}
