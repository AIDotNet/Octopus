namespace Octopus.Platform;

/// <summary>
/// The icon the operating system shows for the running app outside of any window: the Dock on macOS. Windows and Linux
/// take it from the window (Avalonia's <c>Window.Icon</c>) and the exe, so they do nothing here.
/// </summary>
public interface IAppIcon
{
    /// <summary>Shows <paramref name="png"/> (PNG image data) as the app's icon. Never throws.</summary>
    void Set(byte[] png);
}

/// <summary>For platforms whose app icon is the window's: Windows and Linux. Also the stand-in for tests.</summary>
public sealed class NoAppIcon : IAppIcon
{
    public void Set(byte[] png)
    {
    }
}
