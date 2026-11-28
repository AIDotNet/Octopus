using System.Globalization;
using System.Runtime.InteropServices;
using MetricKit;
using ObjCRuntime;
using Octopus.Mobile.Diagnostics;

namespace Octopus.Mobile.iOS;

/// <summary>
/// iOS platform diagnostics provider: installs managed/Objective-C bridge exception hooks, subscribes to Apple's
/// <see cref="MXMetricManager"/> (<c>MetricKit</c>) for native crash and hang payloads, excludes log files from
/// iCloud backup, and shares exported <c>.zip</c> bundles via <see cref="UIActivityViewController"/>.
/// </summary>
internal sealed class IosDiagnosticsPlatform : IMobileDiagnosticsPlatform
{
    private static int s_hooksInstalled;
    private static MetricKitSubscriber? s_metricSubscriber;

    public string LogDirectory { get; }
    public string ExportDirectory { get; }
    public bool CanShareFiles => true;

    public IosDiagnosticsPlatform()
    {
        LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Octopus", "logs");
        var cacheRoot = NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.CachesDirectory, NSSearchPathDomain.User).FirstOrDefault()?.Path
            ?? Path.GetTempPath();
        ExportDirectory = Path.Combine(cacheRoot, "diagnostics");

        try
        {
            if (Directory.Exists(LogDirectory))
            {
                NSFileManager.SetSkipBackupAttribute(LogDirectory, true);
            }
        }
        catch
        {
        }

        MobileDiagnostics.CollectionEnabledChanged += enabled =>
        {
            if (!enabled) return;
            try
            {
                if (Directory.Exists(LogDirectory))
                {
                    NSFileManager.SetSkipBackupAttribute(LogDirectory, true);
                }
            }
            catch
            {
            }
        };
    }

    public static void Install()
    {
        var platform = new IosDiagnosticsPlatform();
        MobileDiagnostics.Initialize(platform);
        InstallPlatformHooks();
    }

    public DeviceEnvironmentInfo GetEnvironment()
    {
        var device = UIDevice.CurrentDevice;
        var shortVersion = NSBundle.MainBundle.ObjectForInfoDictionary("CFBundleShortVersionString")?.ToString();
        var buildNumber = NSBundle.MainBundle.ObjectForInfoDictionary("CFBundleVersion")?.ToString();
        var appVersion = !string.IsNullOrWhiteSpace(shortVersion)
            ? string.IsNullOrWhiteSpace(buildNumber) ? shortVersion : $"{shortVersion} ({buildNumber})"
            : MobileDiagnostics.AppVersion;

        long? totalMb = null;
        try
        {
            var mem = NSProcessInfo.ProcessInfo.PhysicalMemory;
            if (mem > 0) totalMb = (long)(mem / (1024UL * 1024UL));
        }
        catch
        {
        }

        return new DeviceEnvironmentInfo(
            Platform: "iOS",
            OsVersion: $"{device.SystemName} {device.SystemVersion}",
            DeviceModel: device.Model,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            AppVersion: appVersion,
            RuntimeDescription: RuntimeInformation.FrameworkDescription,
            IsNativeAot: !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,
            TotalMemoryMb: totalMb);
    }

    public Task<bool> ShareFileAsync(string filePath, string title, string mimeType)
    {
        if (!File.Exists(filePath)) return Task.FromResult(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var presenter = FindTopViewController();
                if (presenter is null)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                var fileUrl = NSUrl.FromFilename(filePath);
                var controller = new UIActivityViewController([fileUrl], null);
                if (controller.PopoverPresentationController is { } popover && presenter.View is { } view)
                {
                    popover.SourceView = view;
                    popover.SourceRect = new CGRect(view.Bounds.GetMidX(), view.Bounds.GetMidY(), 1, 1);
                    popover.PermittedArrowDirections = 0;
                }

                presenter.PresentViewController(controller, animated: true, () => tcs.TrySetResult(true));
            }
            catch (Exception ex)
            {
                MobileDiagnostics.Error("DIAG", "iOS ShareFileAsync failed", ex);
                tcs.TrySetResult(false);
            }
        });
        return tcs.Task;
    }

    private static void InstallPlatformHooks()
    {
        if (Interlocked.Exchange(ref s_hooksInstalled, 1) != 0) return;

        Runtime.MarshalManagedException += (_, args) =>
        {
            MobileDiagnostics.RecordCrash("iOS.MarshalManagedException", args.Exception, isTerminating: true);
        };

        Runtime.MarshalObjectiveCException += (_, args) =>
        {
            var nsEx = args.Exception;
            var stack = nsEx?.CallStackSymbols is { Length: > 0 } symbols
                ? string.Join(Environment.NewLine, symbols)
                : nsEx?.Description ?? "";
            MobileDiagnostics.RecordPlatformCrash(
                "iOS.MarshalObjectiveCException",
                nsEx?.Name ?? "NSException",
                nsEx?.Reason ?? "",
                stack,
                isTerminating: true);
        };

        try
        {
            s_metricSubscriber = new MetricKitSubscriber();
            MXMetricManager.SharedManager.Add(s_metricSubscriber);
        }
        catch
        {
        }
    }

    private static UIViewController? FindTopViewController()
    {
        UIWindow? window = null;
        foreach (var scene in UIApplication.SharedApplication.ConnectedScenes)
        {
            if (scene is UIWindowScene windowScene && windowScene.ActivationState == UISceneActivationState.ForegroundActive)
            {
                window = windowScene.Windows.FirstOrDefault(w => w.IsKeyWindow) ?? windowScene.Windows.FirstOrDefault();
                if (window is not null) break;
            }
        }
        var current = window?.RootViewController;
        while (current?.PresentedViewController is { } presented)
        {
            current = presented;
        }
        return current;
    }

    private sealed class MetricKitSubscriber : NSObject, IMXMetricManagerSubscriber
    {
        [Export("didReceiveDiagnosticPayloads:")]
        public void DidReceiveDiagnosticPayloads(MXDiagnosticPayload[] payloads)
        {
            foreach (var payload in payloads)
            {
                try
                {
                    var ts = payload.TimeStampEnd is { } end
                        ? DateTimeOffset.FromUnixTimeSeconds((long)end.SecondsSince1970)
                        : DateTimeOffset.Now;
                    var json = payload.JsonRepresentation?.ToString(NSStringEncoding.UTF8) ?? "";
                    if (json.Length > 32_000) json = json[..32_000] + "\n… (truncated)";

                    var crashCount = payload.CrashDiagnostics?.Length ?? 0;
                    var hangCount = payload.HangDiagnostics?.Length ?? 0;
                    var reason = crashCount > 0 ? "iOS.MetricKit.Crash" : hangCount > 0 ? "iOS.MetricKit.Hang" : "iOS.MetricKit.Diagnostic";
                    var description = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Apple MetricKit payload (crashes={crashCount}, hangs={hangCount})");
                    var id = string.Create(CultureInfo.InvariantCulture, $"ios-mx-{ts.ToUnixTimeSeconds()}-{crashCount}-{hangCount}");

                    MobileDiagnostics.RecordNativeExit(
                        new NativeExitRecord(id, ts, reason, description, json),
                        raiseNotice: crashCount > 0 || hangCount > 0);
                }
                catch
                {
                }
            }
        }
    }
}
