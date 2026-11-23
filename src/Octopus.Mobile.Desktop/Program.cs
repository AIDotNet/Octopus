using System.Diagnostics;
using Avalonia;
using Octopus.Mobile.Remote;

namespace Octopus.Mobile.Desktop;

internal static class Program
{
    // Avalonia is not ready until Main runs: do not use any Avalonia or Arc API before BuildAvaloniaApp().
    [STAThread]
    public static void Main(string[] args)
    {
        // "--demo": made-up computers and sessions, no network (what the app showed before it could talk to a host).
        if (!args.Contains("--demo")) UseRealHosts();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the XAML previewer of your IDE. UseOctopusMobile() (MobileApp.cs) sets up the fonts and Arc.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<MobileApp>()
                  .UsePlatformDetect()
                  .UseOctopusMobile()
                  .LogToTrace();

    /// <summary>
    /// The preview as a phone of its own: its key, sign-in and paired hosts live in a folder of this computer
    /// (OCTOPUS_MOBILE_DATA_DIR, else under the user's application data), apart from the desktop app's.
    /// OCTOPUS_RELAY_URL is the official service "Sign in" goes to; it wins over the address the build was given
    /// (OctopusServiceUrl in Directory.Build.props). With neither, "Sign in" asks for an address.
    /// </summary>
    private static void UseRealHosts()
    {
        var directory = Environment.GetEnvironmentVariable("OCTOPUS_MOBILE_DATA_DIR") is { Length: > 0 } given
            ? given
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Octopus", "mobile-preview");
        var remote = new MobileRemote(new FileMobileStore(directory), $"{Environment.MachineName} (phone preview)", Environment.GetEnvironmentVariable("OCTOPUS_RELAY_URL") is { Length: > 0 } official ? official : null);
        remote.Load();
        MobileHost.Workspace = remote.Workspace;
        MobileHost.Account = remote.Account;
        MobileHost.OpenBrowser = Open;
    }

    // Only ever a web address (the sign-in page), handed to the system's browser.
    private static void Open(Uri address)
    {
        if (address.Scheme is not ("https" or "http")) return;
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start("open", [address.AbsoluteUri])?.Dispose();
            else if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            else Process.Start("xdg-open", [address.AbsoluteUri])?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
        }
    }
}
