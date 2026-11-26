namespace Octopus.Core.SystemMetrics;

/// <summary>Signals a plugin or UI may send to a process when granted <c>system.process.signal</c>.</summary>
public enum ProcessSignalKind
{
    Term,
    Kill,
    Int,
    Hup,
}

/// <summary>Which telemetry sections to collect and how to filter/sort processes.</summary>
public sealed class SystemMetricsRequest
{
    public bool IncludeCpu { get; init; } = true;
    public bool IncludeMemory { get; init; } = true;
    public bool IncludeDisk { get; init; } = true;
    public bool IncludeNetwork { get; init; } = true;
    public bool IncludeProcesses { get; init; } = true;
    public int ProcessLimit { get; init; } = 25;
    /// <summary><c>cpu</c>, <c>memory</c>, <c>pid</c> or <c>name</c>.</summary>
    public string ProcessSort { get; init; } = "cpu";
    public string? ProcessFilter { get; init; }
}

public sealed class SystemHostInfo
{
    public string Hostname { get; set; } = "";
    /// <summary><c>macos</c>, <c>linux</c>, <c>windows</c> or <c>unknown</c>.</summary>
    public string Os { get; set; } = "";
    public string? OsVersion { get; set; }
    public string? Arch { get; set; }
    public string? Kernel { get; set; }
    public long? UptimeSeconds { get; set; }
}

public sealed class CpuMetrics
{
    public double UsagePercent { get; set; }
    public double? UserPercent { get; set; }
    public double? SystemPercent { get; set; }
    public double? IdlePercent { get; set; }
    public int Cores { get; set; }
    public string? Model { get; set; }
    public double? LoadAvg1 { get; set; }
    public double? LoadAvg5 { get; set; }
    public double? LoadAvg15 { get; set; }
    public List<double>? PerCore { get; set; }
}

public sealed class MemoryMetrics
{
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long AvailableBytes { get; set; }
    public long? CachedBytes { get; set; }
    public double UsedPercent { get; set; }
    public long SwapTotalBytes { get; set; }
    public long SwapUsedBytes { get; set; }
    public double SwapPercent { get; set; }
}

public sealed class DiskMetrics
{
    public string Mount { get; set; } = "";
    public string? Filesystem { get; set; }
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long AvailableBytes { get; set; }
    public double UsedPercent { get; set; }
    public double? ReadBytesPerSec { get; set; }
    public double? WriteBytesPerSec { get; set; }
}

public sealed class NetworkInterfaceMetrics
{
    public string Name { get; set; } = "";
    public double RxBytesPerSec { get; set; }
    public double TxBytesPerSec { get; set; }
    public long RxTotalBytes { get; set; }
    public long TxTotalBytes { get; set; }
    public string? Ipv4 { get; set; }
}

public sealed class ProcessMetrics
{
    public int Pid { get; set; }
    public int? Ppid { get; set; }
    public string Name { get; set; } = "";
    public string? User { get; set; }
    public string? State { get; set; }
    public double CpuPercent { get; set; }
    public double MemPercent { get; set; }
    public long RssBytes { get; set; }
    public int? Threads { get; set; }
    public string? Command { get; set; }
}

/// <summary>Normalized system telemetry snapshot shared by local, SSH and remote Octopus devices.</summary>
public sealed class SystemMetricsSnapshot
{
    public string DeviceKey { get; set; } = "local";
    public SystemHostInfo Host { get; set; } = new();
    public CpuMetrics? Cpu { get; set; }
    public MemoryMetrics? Memory { get; set; }
    public List<DiskMetrics>? Disks { get; set; }
    public List<NetworkInterfaceMetrics>? Network { get; set; }
    public List<ProcessMetrics>? Processes { get; set; }
    public long SampledAtMs { get; set; }
    public long DurationMs { get; set; }
}
