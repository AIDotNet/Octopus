using Avalonia;
using Avalonia.iOS;
using Octopus.Mobile.Remote;

namespace Octopus.Mobile.iOS;

/// <summary>The iOS app. The pages are the shared ones of src/Octopus.Mobile.</summary>
[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<MobileApp>
{
    private NSObject? _didEnterBackgroundObserver;
    private NSObject? _willEnterForegroundObserver;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        _didEnterBackgroundObserver ??= UIApplication.Notifications.ObserveDidEnterBackground((_, _) => MobileHost.NotifyBackgrounded());
        _willEnterForegroundObserver ??= UIApplication.Notifications.ObserveWillEnterForeground((_, _) => MobileHost.NotifyForegrounded());
        return base.CustomizeAppBuilder(builder).UseOctopusMobile();
    }
}

public static class Program
{
    private static void Main(string[] args)
    {
        UseRealHosts();
        UIApplication.Main(args, null, typeof(AppDelegate));
    }

    /// <summary>Before Avalonia starts: the real hosts and account, on this phone's keychain (<see cref="KeychainMobileStore"/>).</summary>
    private static void UseRealHosts()
    {
        // Library/Application Support: kept, backed up, never shown to the person in the Files app.
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Octopus");
        var remote = new MobileRemote(new KeychainMobileStore(directory), DeviceName());
        remote.Load();
        MobileHost.Workspace = remote.Workspace;
        MobileHost.Account = remote.Account;
        MobileHost.OpenBrowser = OpenBrowser;
    }

    /// <summary>What a computer shows for this phone. Since iOS 16 the name the person gave it needs an entitlement; without, it is "iPhone".</summary>
    private static string DeviceName() => UIDevice.CurrentDevice.Name is { Length: > 0 } name ? name : "iPhone";

    // Only ever a web address (the sign-in page), handed to the system's browser.
    private static void OpenBrowser(Uri address)
    {
        if (address.Scheme is not ("https" or "http")) return;
        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            UIApplication.SharedApplication.OpenUrl(new NSUrl(address.AbsoluteUri), new UIApplicationOpenUrlOptions(), null));
    }
}
