using Octopus.Core.SystemMetrics;
using Octopus.Platform;

namespace Octopus.Plugins.Runtime;

/// <summary>One device (local computer, paired Octopus remote host, or SSH alias) as plugins see it.</summary>
public sealed record PluginDeviceInfo(
    string Key,
    string Kind,
    string Name,
    string? Target,
    string Status,
    IReadOnlyList<string> Capabilities);

/// <summary>Snapshot of the active device and all known devices; implemented by the app.</summary>
public interface IPluginDeviceContext
{
    PluginDeviceInfo Current { get; }
    IReadOnlyList<PluginDeviceInfo> All { get; }
    event EventHandler? CurrentChanged;
    event EventHandler? ListChanged;
}

/// <summary>Default local-only device context for headless tests.</summary>
public sealed class LocalOnlyDeviceContext : IPluginDeviceContext
{
    private static readonly PluginDeviceInfo Local = new("local", "local", Environment.MachineName, null, "local", ["system.metrics", "system.signal", "process.run"]);

    public PluginDeviceInfo Current => Local;
    public IReadOnlyList<PluginDeviceInfo> All => [Local];
    public event EventHandler? CurrentChanged { add { } remove { } }
    public event EventHandler? ListChanged { add { } remove { } }
}

/// <summary>Routes system telemetry, process signals and multi-device controlled execution to local, SSH or remote Octopus devices.</summary>
public interface IPluginSystemService
{
    Task<SystemMetricsSnapshot> GetSnapshotAsync(string deviceKey, SystemMetricsRequest request, CancellationToken cancellationToken = default);
    Task<bool> SignalProcessAsync(string deviceKey, int pid, ProcessSignalKind signal, CancellationToken cancellationToken = default);
    Task<ProcessResult> RunOnDeviceAsync(
        string deviceKey,
        string command,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        string? stdin,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
