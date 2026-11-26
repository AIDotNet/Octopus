namespace Octopus.Remote.Protocol;

/// <summary>Request for <see cref="RemoteMethods.SystemMetrics"/>.</summary>
public sealed class SystemMetricsRequestMessage
{
    public bool IncludeCpu { get; set; } = true;
    public bool IncludeMemory { get; set; } = true;
    public bool IncludeDisk { get; set; } = true;
    public bool IncludeNetwork { get; set; } = true;
    public bool IncludeProcesses { get; set; } = true;
    public int ProcessLimit { get; set; } = 25;
    public string ProcessSort { get; set; } = "cpu";
    public string? ProcessFilter { get; set; }
}

public sealed class SystemHostMessage
{
    public string Hostname { get; set; } = "";
    public string Os { get; set; } = "";
    public string? OsVersion { get; set; }
    public string? Arch { get; set; }
    public string? Kernel { get; set; }
    public long? UptimeSeconds { get; set; }
}

public sealed class CpuMetricsMessage
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

public sealed class MemoryMetricsMessage
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

public sealed class DiskMetricsMessage
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

public sealed class NetworkInterfaceMessage
{
    public string Name { get; set; } = "";
    public double RxBytesPerSec { get; set; }
    public double TxBytesPerSec { get; set; }
    public long RxTotalBytes { get; set; }
    public long TxTotalBytes { get; set; }
    public string? Ipv4 { get; set; }
}

public sealed class ProcessMetricsMessage
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

/// <summary>Response of <see cref="RemoteMethods.SystemMetrics"/>.</summary>
public sealed class SystemMetricsSnapshotMessage
{
    public SystemHostMessage Host { get; set; } = new();
    public CpuMetricsMessage? Cpu { get; set; }
    public MemoryMetricsMessage? Memory { get; set; }
    public List<DiskMetricsMessage>? Disks { get; set; }
    public List<NetworkInterfaceMessage>? Network { get; set; }
    public List<ProcessMetricsMessage>? Processes { get; set; }
    public long SampledAtMs { get; set; }
    public long DurationMs { get; set; }
}

/// <summary>Request of <see cref="RemoteMethods.SystemSignalProcess"/>.</summary>
public sealed class SignalProcessMessage
{
    public int Pid { get; set; }
    /// <summary><c>TERM</c>, <c>KILL</c>, <c>INT</c> or <c>HUP</c>.</summary>
    public string Signal { get; set; } = "TERM";
}

public sealed class SignalProcessResultMessage
{
    public bool Ok { get; set; }
}

/// <summary>Request of <see cref="RemoteMethods.ProcessRun"/> (Tier 2 controlled multi-device execution).</summary>
public sealed class RemoteProcessRunMessage
{
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
    public string? Cwd { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public string? Stdin { get; set; }
    public int TimeoutMs { get; set; } = 30_000;
}

public sealed class RemoteProcessRunResultMessage
{
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = "";
    public string StandardError { get; set; } = "";
    public bool TimedOut { get; set; }
}
