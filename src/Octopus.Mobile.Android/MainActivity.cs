using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Octopus.Mobile.Remote;

namespace Octopus.Mobile.Android;

/// <summary>
/// The Android process. Before Avalonia starts, the app is given the real hosts and account, on this phone's keystore
/// (<see cref="KeystoreMobileStore"/>); the pages are the shared ones of src/Octopus.Mobile.
/// </summary>
[Application]
public sealed class AndroidApp : AvaloniaAndroidApplication<MobileApp>
{
    public AndroidApp(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    public override void OnCreate()
    {
        UseRealHosts(this);
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).UseOctopusMobile();

    private static void UseRealHosts(Context context)
    {
        // The app's own files folder: no other app can read it, and it goes with the app when it is removed.
        var directory = Path.Combine(context.FilesDir!.AbsolutePath, "octopus");
        var remote = new MobileRemote(new KeystoreMobileStore(directory), DeviceName());
        remote.Load();
        MobileHost.Workspace = remote.Workspace;
        MobileHost.Account = remote.Account;
        MobileHost.OpenBrowser = address => OpenBrowser(context, address);
    }

    /// <summary>What a computer shows for this phone: "Google Pixel 9", the maker and the model.</summary>
    private static string DeviceName()
    {
        var maker = Build.Manufacturer ?? "";
        var model = Build.Model ?? "Android";
        return model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) || maker.Length == 0
            ? model
            : $"{char.ToUpperInvariant(maker[0])}{maker[1..]} {model}";
    }

    // Only ever a web address (the sign-in page), handed to the system's browser.
    private static void OpenBrowser(Context context, Uri address)
    {
        if (address.Scheme is not ("https" or "http")) return;
        var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(address.AbsoluteUri));
        intent.AddFlags(ActivityFlags.NewTask);
        try { context.StartActivity(intent); }
        catch (ActivityNotFoundException) { }
    }
}

[Activity(Label = "Octopus", MainLauncher = true, Icon = "@mipmap/octopus", Theme = "@style/Theme.AppCompat.DayNight.NoActionBar",
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize,
    WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = "octopus")]
public sealed class MainActivity : AvaloniaMainActivity
{
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent?.DataString is { Length: > 0 } raw && Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            MobileHost.HandleDeepLink(uri);
        }
    }
}
