namespace Octopus.App.Shell.Chrome;

/// <summary>The one place that chooses the window chrome by operating system.</summary>
public static class WindowChromeFactory
{
    public static IWindowChrome Create() =>
        OperatingSystem.IsMacOS() ? new WindowChrome(78, false) : new WindowChrome(0, true);
}
