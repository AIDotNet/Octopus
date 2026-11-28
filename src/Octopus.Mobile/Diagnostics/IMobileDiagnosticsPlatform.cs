using System.Runtime.InteropServices;

namespace Octopus.Mobile.Diagnostics;

/// <summary>Snapshot of the current device and build environment attached to crash and diagnostic reports.</summary>
public sealed record DeviceEnvironmentInfo(
    string Platform,
    string OsVersion,
    string DeviceModel,
    string Architecture,
    string AppVersion,
    string RuntimeDescription,
    bool IsNativeAot,
    long? TotalMemoryMb = null);

/// <summary>
/// Summary of a previous process termination reported by the OS (Android <c>ApplicationExitInfo</c>, iOS MetricKit,
/// or the foreground dirty-exit sentinel when the process vanished without running managed handlers).
/// </summary>
public sealed record NativeExitRecord(
    string Id,
    DateTimeOffset Timestamp,
    string Reason,
    string Description,
    string? Details = null);

/// <summary>
/// Platform seam for mobile diagnostics: where logs live, how device metadata and OS exit reasons are read,
/// and how a diagnostic bundle (<c>.zip</c>) is handed to the system share sheet.
/// </summary>
public interface IMobileDiagnosticsPlatform
{
    /// <summary>Persistent private directory for crash logs and rotating runtime logs (kept apart from secrets).</summary>
    string LogDirectory { get; }

    /// <summary>Cache directory where temporary export <c>.zip</c> bundles are written before sharing.</summary>
    string ExportDirectory { get; }

    /// <summary>True when <see cref="ShareFileAsync"/> opens a native share sheet or file manager.</summary>
    bool CanShareFiles { get; }

    /// <summary>Returns current OS, device model, architecture, app version and runtime info.</summary>
    DeviceEnvironmentInfo GetEnvironment();

    /// <summary>Hands an exported diagnostic file to the OS share sheet (Android/iOS) or file manager (Desktop).</summary>
    Task<bool> ShareFileAsync(string filePath, string title, string mimeType);

    /// <summary>
    /// Collects recent OS-level exit or diagnostic records (e.g. Android API 30+ <c>ApplicationExitInfo</c>
    /// for ANR/LMK/native crashes). Called once during startup; never throws.
    /// </summary>
    IReadOnlyList<NativeExitRecord> CollectNativeExitRecords() => [];
}

/// <summary>
/// Fallback diagnostics platform used in headless tests or before a platform head registers itself.
/// </summary>
internal sealed class DefaultDiagnosticsPlatform(string? logDirectory = null, string? exportDirectory = null) : IMobileDiagnosticsPlatform
{
    private readonly string _root = logDirectory
        ?? Path.Combine(Path.GetTempPath(), "octopus-mobile-diagnostics", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public string LogDirectory => _root;
    public string ExportDirectory => exportDirectory ?? Path.Combine(_root, "exports");
    public bool CanShareFiles => false;

    public DeviceEnvironmentInfo GetEnvironment() => new(
        Platform: OperatingSystem.IsAndroid() ? "Android" : OperatingSystem.IsIOS() ? "iOS" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : "Linux",
        OsVersion: RuntimeInformation.OSDescription,
        DeviceModel: "Unknown",
        Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
        AppVersion: MobileDiagnostics.AppVersion,
        RuntimeDescription: RuntimeInformation.FrameworkDescription,
        IsNativeAot: !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,
        TotalMemoryMb: TryReadTotalMemoryMb());

    public Task<bool> ShareFileAsync(string filePath, string title, string mimeType) => Task.FromResult(false);

    internal static long? TryReadTotalMemoryMb()
    {
        try
        {
            var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return bytes > 0 ? bytes / (1024 * 1024) : null;
        }
        catch
        {
            return null;
        }
    }
}
