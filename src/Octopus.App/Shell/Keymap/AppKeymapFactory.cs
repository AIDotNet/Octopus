namespace Octopus.App.Shell.Keymap;

/// <summary>The one place that chooses a keymap by operating system.</summary>
public static class AppKeymapFactory
{
    public static IAppKeymap Create() => OperatingSystem.IsMacOS() ? new MacAppKeymap() : new DefaultAppKeymap();
}
