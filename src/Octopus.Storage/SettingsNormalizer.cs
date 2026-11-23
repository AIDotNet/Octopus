using Octopus.Core.Settings;

namespace Octopus.Storage;

internal static class SettingsNormalizer
{
    public static AppSettingsModel Normalize(AppSettingsModel? settings)
    {
        settings ??= new AppSettingsModel();
        settings.Appearance ??= new();
        settings.Sidebar ??= new();
        settings.Terminal ??= new();
        settings.Agents ??= new();
        settings.Updates ??= new();
        settings.Tray ??= new();
        settings.Remote ??= new();
        settings.Workspaces ??= new();
        settings.Workspaces.RootDirectory = string.IsNullOrWhiteSpace(settings.Workspaces.RootDirectory)
            ? null
            : settings.Workspaces.RootDirectory.Trim();
        settings.WorkspaceCleanup ??= new();
        settings.WorkspaceCleanup.InactiveDays = settings.WorkspaceCleanup.InactiveDays <= 0
            ? WorkspaceCleanupSettings.DefaultInactiveDays
            : Math.Clamp(settings.WorkspaceCleanup.InactiveDays, WorkspaceCleanupSettings.MinInactiveDays, WorkspaceCleanupSettings.MaxInactiveDays);
        settings.WorkspaceCleanup.CheckIntervalMinutes = settings.WorkspaceCleanup.CheckIntervalMinutes <= 0
            ? WorkspaceCleanupSettings.DefaultCheckIntervalMinutes
            : Math.Clamp(settings.WorkspaceCleanup.CheckIntervalMinutes, WorkspaceCleanupSettings.MinCheckIntervalMinutes, WorkspaceCleanupSettings.MaxCheckIntervalMinutes);
        settings.WorkspaceCleanup.UnchangedGraceHours = settings.WorkspaceCleanup.UnchangedGraceHours < 0
            ? WorkspaceCleanupSettings.DefaultUnchangedGraceHours
            : Math.Clamp(settings.WorkspaceCleanup.UnchangedGraceHours, 0, WorkspaceCleanupSettings.MaxUnchangedGraceHours);
        if (!UiDensityValues.IsKnown(settings.Appearance.Density))
            settings.Appearance.Density = UiDensityValues.Compact;
        // 0 is allowed: the system then picks a free port (tests, and a second instance on one machine).
        if (settings.Remote.LanPort is < 0 or > 65535) settings.Remote.LanPort = Octopus.Core.Settings.RemoteSettings.DefaultLanPort;
        if (!Octopus.Core.Updates.UpdateSettingValues.IsKnownMode(settings.Updates.Mode))
            settings.Updates.Mode = Octopus.Core.Updates.UpdateSettingValues.Auto;
        if (!Octopus.Core.Updates.UpdateSettingValues.IsKnownChannel(settings.Updates.Channel))
            settings.Updates.Channel = null;
        settings.Agents.Profiles ??= [];
        settings.Agents.ExpansionStates ??= [];
        foreach (var key in settings.Agents.Profiles.Where(p => p.Value is null).Select(p => p.Key).ToList())
            settings.Agents.Profiles[key] = new();
        foreach (var profile in settings.Agents.Profiles.Values)
        {
            profile.ExtraArgs ??= [];
            profile.Env ??= [];
            profile.Features ??= [];
        }
        settings.Sidebar.Width = Clamp(settings.Sidebar.Width, 200, 480);
        settings.Terminal.FontSize = Clamp(settings.Terminal.FontSize, 8, 32);
        settings.Terminal.ScrollbackLines = (int)Clamp(settings.Terminal.ScrollbackLines, 100, 100_000);
        return settings;
    }

    private static double Clamp(double value, double min, double max) =>
        double.IsNaN(value) ? min : Math.Clamp(value, min, max);
}
