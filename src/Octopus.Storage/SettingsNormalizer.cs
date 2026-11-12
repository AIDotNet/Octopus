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
        if (!Octopus.Core.Updates.UpdateSettingValues.IsKnownMode(settings.Updates.Mode))
            settings.Updates.Mode = Octopus.Core.Updates.UpdateSettingValues.Auto;
        if (!Octopus.Core.Updates.UpdateSettingValues.IsKnownChannel(settings.Updates.Channel))
            settings.Updates.Channel = null;
        settings.Agents.Profiles ??= [];
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
