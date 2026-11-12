namespace Octopus.Plugins;

/// <summary>Fixed facts about the plugin API this build of Octopus offers.</summary>
public static class PluginApi
{
    /// <summary>
    /// Major version of the plugin API. 0 while the API is a preview (until the end of P3); manifests asking for a
    /// higher version need a newer Octopus (design/plugins/10-分期与风险.md §4).
    /// </summary>
    public const int CurrentVersion = 0;

    /// <summary>The oldest API major version still accepted.</summary>
    public const int MinimumVersion = 0;

    public const int ManifestSchema = 1;
    public const string ManifestFileName = "plugin.json";
    public const string PackageExtension = ".octoplugin";

    /// <summary>A manifest larger than this is not read.</summary>
    public const int MaxManifestBytes = 1024 * 1024;
}

/// <summary>The app a plugin is checked against: its plugin API version and its own version.</summary>
public sealed record PluginHostInfo(int ApiVersion, SemanticVersion AppVersion)
{
    public static PluginHostInfo Current(SemanticVersion appVersion) => new(PluginApi.CurrentVersion, appVersion);
}
