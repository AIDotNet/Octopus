namespace Octopus.Core.Updates;

/// <summary>Which releases an install receives (design/10-自动更新.md §4).</summary>
public enum UpdateChannel
{
    /// <summary>Releases only.</summary>
    Stable,
    /// <summary>Releases and previews, whichever is newest.</summary>
    Preview,
}

/// <summary>What the updater does on its own (design/10-自动更新.md §9).</summary>
public enum UpdateMode
{
    /// <summary>Checks, downloads, and installs when the app quits.</summary>
    Auto,
    /// <summary>Checks and tells; downloads only when asked.</summary>
    Notify,
    /// <summary>Checks only when asked.</summary>
    Off,
}

/// <summary>The values of <c>updates.mode</c> and <c>updates.channel</c> in settings.json.</summary>
public static class UpdateSettingValues
{
    public const string Auto = "auto";
    public const string Notify = "notify";
    public const string Off = "off";
    public const string Stable = "stable";
    public const string Preview = "preview";

    public static UpdateMode ParseMode(string? value) => value switch
    {
        Notify => UpdateMode.Notify,
        Off => UpdateMode.Off,
        _ => UpdateMode.Auto,
    };

    public static string ToValue(UpdateMode mode) => mode switch
    {
        UpdateMode.Notify => Notify,
        UpdateMode.Off => Off,
        _ => Auto,
    };

    /// <summary>The channel in use: the saved one, or, when none is saved, the one of the running version.</summary>
    public static UpdateChannel ResolveChannel(string? saved, AppVersion? current) => saved switch
    {
        Stable => UpdateChannel.Stable,
        Preview => UpdateChannel.Preview,
        _ => current?.IsPreview == true ? UpdateChannel.Preview : UpdateChannel.Stable,
    };

    public static bool IsKnownMode(string? value) => value is Auto or Notify or Off;
    public static bool IsKnownChannel(string? value) => value is null or Stable or Preview;
}
