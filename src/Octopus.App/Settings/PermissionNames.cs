using Octopus.App.Localization;
using Octopus.Core.Agents;

namespace Octopus.App.Settings;

/// <summary>The words for the unified permission levels and for effort levels.</summary>
public static class PermissionNames
{
    public static string Of(string id) => id switch
    {
        PermissionModes.Default => AppLangResources.Get(AppLangResourceKind.SettingsPermDefault),
        PermissionModes.AcceptEdits => AppLangResources.Get(AppLangResourceKind.SettingsPermAcceptEdits),
        PermissionModes.Plan => AppLangResources.Get(AppLangResourceKind.SettingsPermPlan),
        PermissionModes.Auto => AppLangResources.Get(AppLangResourceKind.SettingsPermAuto),
        PermissionModes.DontAsk => AppLangResources.Get(AppLangResourceKind.SettingsPermDontAsk),
        PermissionModes.Full => AppLangResources.Get(AppLangResourceKind.SettingsPermFull),
        _ => id
    };

    /// <summary>The name of an effort level; an id it does not know is shown as it is.</summary>
    public static string Effort(string id) => id switch
    {
        "low" => AppLangResources.Get(AppLangResourceKind.SettingsEffortLow),
        "medium" => AppLangResources.Get(AppLangResourceKind.SettingsEffortMedium),
        "high" => AppLangResources.Get(AppLangResourceKind.SettingsEffortHigh),
        "xhigh" => AppLangResources.Get(AppLangResourceKind.SettingsEffortXhigh),
        "max" => AppLangResources.Get(AppLangResourceKind.SettingsEffortMax),
        "none" => AppLangResources.Get(AppLangResourceKind.SettingsEffortNone),
        "minimal" => AppLangResources.Get(AppLangResourceKind.SettingsEffortMinimal),
        "ultra" => AppLangResources.Get(AppLangResourceKind.SettingsEffortUltra),
        _ => id
    };
}
