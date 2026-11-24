using Android.Content;
using Android.Content.PM;
using AndroidX.Core.Content;
using Octopus.Mobile.Updates;

namespace Octopus.Mobile.Android;

/// <summary>
/// The updater's Android side: the installed versionName, a download folder in the app's cache, and the system installer
/// (design/10-自动更新.md §15). The APK is shown to the installer through the app's FileProvider; Android asks the
/// person before installing it, and only takes it when it is signed with the same key as the installed app.
/// </summary>
internal sealed class AndroidUpdatePlatform(Context context) : IMobileUpdatePlatform
{
    /// <summary>The FileProvider's authority: <c>AndroidManifest.xml</c> declares it with the same name.</summary>
    private string Authority => context.PackageName + ".updates";

    public string Target => "android";

    public string? InstalledVersion
    {
        get
        {
            try
            {
                var manager = context.PackageManager!;
                var info = OperatingSystem.IsAndroidVersionAtLeast(33)
                    ? manager.GetPackageInfo(context.PackageName!, PackageManager.PackageInfoFlags.Of(0L))
                    : manager.GetPackageInfo(context.PackageName!, 0);
                return info?.VersionName;
            }
            catch (PackageManager.NameNotFoundException)
            {
                return null;
            }
        }
    }

    /// <summary>The cache's <c>updates/</c>, the folder <c>Resources/xml/update_paths.xml</c> shares.</summary>
    public string DownloadDirectory => Path.Combine(context.CacheDir!.AbsolutePath, "updates");

    public bool Install(string package)
    {
        // Since Android 8 each app must be allowed to install apps; the first time, open that switch for this app.
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && !context.PackageManager!.CanRequestPackageInstalls())
        {
            var settings = new Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources,
                                      global::Android.Net.Uri.Parse("package:" + context.PackageName));
            settings.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(settings);
            return false;
        }
        var uri = FileProvider.GetUriForFile(context, Authority, new Java.IO.File(package));
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
        context.StartActivity(intent);
        return true;
    }
}
