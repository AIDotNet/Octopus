using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.Content;
using Octopus.Mobile.Diagnostics;

namespace Octopus.Mobile.Android;

/// <summary>
/// Android platform diagnostics provider: installs managed/JNI/Java uncaught exception hooks, collects Android 11+
/// <c>ApplicationExitInfo</c> records (ANR, LMK/OOM, native crashes), and shares exported <c>.zip</c> bundles
/// through the app's existing <see cref="FileProvider"/>.
/// </summary>
internal sealed class AndroidDiagnosticsPlatform(Context context) : IMobileDiagnosticsPlatform
{
    private const int MaxTraceChars = 16 * 1024;
    private static int s_hooksInstalled;

    private readonly Context _context = context.ApplicationContext ?? context;

    public string LogDirectory => Path.Combine(_context.FilesDir!.AbsolutePath, "octopus", "logs");
    public string ExportDirectory => Path.Combine(_context.CacheDir!.AbsolutePath, "diagnostics");
    public bool CanShareFiles => true;

    public static void Install(Context context)
    {
        var platform = new AndroidDiagnosticsPlatform(context);
        MobileDiagnostics.Initialize(platform);
        InstallPlatformHooks();
    }

    public DeviceEnvironmentInfo GetEnvironment()
    {
        var maker = Build.Manufacturer ?? "";
        var model = Build.Model ?? "Android";
        var deviceName = model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) || maker.Length == 0
            ? model
            : $"{maker} {model}";

        return new DeviceEnvironmentInfo(
            Platform: "Android",
            OsVersion: $"{Build.VERSION.Release ?? "?"} (API {(int)Build.VERSION.SdkInt})",
            DeviceModel: deviceName,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            AppVersion: ReadInstalledVersion() ?? MobileDiagnostics.AppVersion,
            RuntimeDescription: RuntimeInformation.FrameworkDescription,
            IsNativeAot: !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,
            TotalMemoryMb: ReadTotalMemoryMb());
    }

    public Task<bool> ShareFileAsync(string filePath, string title, string mimeType)
    {
        try
        {
            if (!File.Exists(filePath)) return Task.FromResult(false);
            var authority = _context.PackageName + ".updates";
            var uri = FileProvider.GetUriForFile(_context, authority, new Java.IO.File(filePath));
            var send = new Intent(Intent.ActionSend);
            send.SetType(string.IsNullOrWhiteSpace(mimeType) ? "application/zip" : mimeType);
            send.PutExtra(Intent.ExtraStream, uri);
            send.PutExtra(Intent.ExtraSubject, title);
            send.AddFlags(ActivityFlags.GrantReadUriPermission);

            var chooser = Intent.CreateChooser(send, title);
            if (chooser is null) return Task.FromResult(false);
            chooser.AddFlags(ActivityFlags.GrantReadUriPermission);

            if (MainActivity.Current is { } weak && weak.TryGetTarget(out var activity) && !activity.IsFinishing && !activity.IsDestroyed)
            {
                activity.StartActivity(chooser);
            }
            else
            {
                chooser.AddFlags(ActivityFlags.NewTask);
                _context.StartActivity(chooser);
            }
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            MobileDiagnostics.Error("DIAG", "Android ShareFileAsync failed", ex);
            return Task.FromResult(false);
        }
    }

    public IReadOnlyList<NativeExitRecord> CollectNativeExitRecords()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return [];

        var results = new List<NativeExitRecord>();
        try
        {
            if (_context.GetSystemService(Context.ActivityService) is not ActivityManager am) return results;
            var reasons = am.GetHistoricalProcessExitReasons(_context.PackageName, 0, 5);
            if (reasons is null) return results;

            foreach (var info in reasons)
            {
                var reasonCode = (ApplicationExitInfoReason)info.Reason;
                if (reasonCode is ApplicationExitInfoReason.ExitSelf
                    or ApplicationExitInfoReason.UserRequested
                    or ApplicationExitInfoReason.UserStopped
                    or ApplicationExitInfoReason.Other)
                {
                    continue;
                }

                var ts = DateTimeOffset.FromUnixTimeMilliseconds(info.Timestamp);
                var reasonName = reasonCode.ToString();
                var description = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{reasonName} (status={info.Status}, PSS={info.Pss / 1024}MB, RSS={info.Rss / 1024}MB): {info.Description ?? "no description"}");
                var details = BuildExitDetails(info, reasonCode);
                results.Add(new NativeExitRecord(
                    Id: $"android-exit-{info.Pid}-{info.Timestamp}-{reasonName}",
                    Timestamp: ts,
                    Reason: $"Android.ExitInfo.{reasonName}",
                    Description: description,
                    Details: details));
            }
        }
        catch
        {
            // Never block app startup if OEM ROM restricts exit info queries.
        }

        return results;
    }

    [SupportedOSPlatform("android30.0")]
    private static string BuildExitDetails(ApplicationExitInfo info, ApplicationExitInfoReason reasonCode)
    {
        var sb = new StringBuilder(512);
        sb.Append("Pid: ").AppendLine(info.Pid.ToString(CultureInfo.InvariantCulture));
        sb.Append("ProcessName: ").AppendLine(info.ProcessName ?? "?");
        sb.Append("Reason: ").AppendLine(reasonCode.ToString());
        sb.Append("Status: ").AppendLine(info.Status.ToString(CultureInfo.InvariantCulture));
        sb.Append("Importance: ").AppendLine(info.Importance.ToString());
        sb.Append("PSS: ").Append((info.Pss / 1024).ToString(CultureInfo.InvariantCulture)).AppendLine(" MB");
        sb.Append("RSS: ").Append((info.Rss / 1024).ToString(CultureInfo.InvariantCulture)).AppendLine(" MB");
        if (!string.IsNullOrWhiteSpace(info.Description))
        {
            sb.Append("Description: ").AppendLine(info.Description);
        }

        try
        {
            using var trace = info.TraceInputStream;
            if (trace is not null)
            {
                using var reader = new StreamReader(trace, Encoding.UTF8);
                var buffer = new char[MaxTraceChars];
                var read = reader.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("--- Trace Excerpt ---");
                    sb.Append(buffer, 0, read);
                }
            }
        }
        catch
        {
        }

        return sb.ToString();
    }

    private static void InstallPlatformHooks()
    {
        if (Interlocked.Exchange(ref s_hooksInstalled, 1) != 0) return;

        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
        {
            MobileDiagnostics.RecordCrash(
                "AndroidEnvironment.UnhandledExceptionRaiser",
                e.Exception,
                isTerminating: !e.Handled);
        };

        try
        {
            var previous = Java.Lang.Thread.DefaultUncaughtExceptionHandler;
            Java.Lang.Thread.DefaultUncaughtExceptionHandler = new JavaUncaughtExceptionBridge(previous);
        }
        catch
        {
        }
    }

    private string? ReadInstalledVersion()
    {
        try
        {
            var pm = _context.PackageManager;
            if (pm is null || _context.PackageName is null) return null;
            var info = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? pm.GetPackageInfo(_context.PackageName, PackageManager.PackageInfoFlags.Of(0L))
                : pm.GetPackageInfo(_context.PackageName, 0);
            if (info is null) return null;
            var code = OperatingSystem.IsAndroidVersionAtLeast(28) ? info.LongVersionCode : 0;
            return string.IsNullOrWhiteSpace(info.VersionName) ? null : $"{info.VersionName} ({code})";
        }
        catch
        {
            return null;
        }
    }

    private long? ReadTotalMemoryMb()
    {
        try
        {
            if (_context.GetSystemService(Context.ActivityService) is not ActivityManager am) return null;
            var memInfo = new ActivityManager.MemoryInfo();
            am.GetMemoryInfo(memInfo);
            return memInfo.TotalMem > 0 ? memInfo.TotalMem / (1024 * 1024) : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class JavaUncaughtExceptionBridge(Java.Lang.Thread.IUncaughtExceptionHandler? previous)
        : Java.Lang.Object, Java.Lang.Thread.IUncaughtExceptionHandler
    {
        public void UncaughtException(Java.Lang.Thread t, Java.Lang.Throwable e)
        {
            try
            {
                var sb = new StringBuilder(1024);
                sb.Append("JavaThread: ").AppendLine(t.Name ?? "?");
                sb.AppendLine(e.ToString());
                foreach (var frame in e.GetStackTrace() ?? [])
                {
                    sb.Append("  at ").AppendLine(frame.ToString());
                }
                MobileDiagnostics.RecordPlatformCrash(
                    "Android.Java.UncaughtException",
                    e.Class?.Name ?? "Java.Lang.Throwable",
                    e.Message ?? "",
                    sb.ToString(),
                    isTerminating: true);
            }
            catch
            {
            }
            finally
            {
                previous?.UncaughtException(t, e);
            }
        }
    }
}
