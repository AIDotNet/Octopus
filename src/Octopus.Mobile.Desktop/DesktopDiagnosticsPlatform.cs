using System.Diagnostics;
using System.Runtime.InteropServices;
using Octopus.Mobile.Diagnostics;

namespace Octopus.Mobile.Desktop;

/// <summary>Desktop preview diagnostics provider: stores logs beside mobile preview data and reveals exported zips in the OS file manager.</summary>
internal sealed class DesktopDiagnosticsPlatform(string dataDirectory) : IMobileDiagnosticsPlatform
{
    public string LogDirectory { get; } = Path.Combine(dataDirectory, "logs");
    public string ExportDirectory { get; } = Path.Combine(dataDirectory, "exports");
    public bool CanShareFiles => true;

    public static void Install(string dataDirectory) =>
        MobileDiagnostics.Initialize(new DesktopDiagnosticsPlatform(dataDirectory));

    public DeviceEnvironmentInfo GetEnvironment() => new(
        Platform: OperatingSystem.IsMacOS() ? "macOS (Mobile Preview)" : OperatingSystem.IsWindows() ? "Windows (Mobile Preview)" : "Linux (Mobile Preview)",
        OsVersion: RuntimeInformation.OSDescription,
        DeviceModel: Environment.MachineName,
        Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
        AppVersion: MobileDiagnostics.AppVersion,
        RuntimeDescription: RuntimeInformation.FrameworkDescription,
        IsNativeAot: !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,
        TotalMemoryMb: TryReadTotalMemoryMb());

    public Task<bool> ShareFileAsync(string filePath, string title, string mimeType)
    {
        try
        {
            if (!File.Exists(filePath)) return Task.FromResult(false);
            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", ["-R", filePath])?.Dispose();
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true })?.Dispose();
            }
            else
            {
                var dir = Path.GetDirectoryName(filePath) ?? filePath;
                Process.Start("xdg-open", [dir])?.Dispose();
            }
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            MobileDiagnostics.Error("DIAG", "Desktop reveal diagnostic file failed", ex);
            return Task.FromResult(false);
        }
    }

    private static long? TryReadTotalMemoryMb()
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
