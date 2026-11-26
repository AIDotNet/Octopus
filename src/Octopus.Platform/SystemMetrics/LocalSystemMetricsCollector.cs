using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Octopus.Core.SystemMetrics;

namespace Octopus.Platform.SystemMetrics;

/// <summary>Collects normalized CPU, memory, disk, network and process telemetry from this computer.</summary>
public interface ILocalSystemMetricsCollector
{
    Task<SystemMetricsSnapshot> CollectAsync(SystemMetricsRequest request, CancellationToken cancellationToken = default);
    Task<bool> SignalProcessAsync(int pid, ProcessSignalKind signal, CancellationToken cancellationToken = default);
}

/// <summary>
/// Cross-platform local telemetry collector for macOS, Linux and Windows. Keeps lightweight per-interface and CPU tick
/// baselines in memory so every sample returns instantaneous CPU% and byte/s throughput without sleeping.
/// </summary>
public sealed class LocalSystemMetricsCollector(IProcessRunner runner) : ILocalSystemMetricsCollector
{
    private readonly Lock _lock = new();
    private CpuTickSample? _lastCpu;
    private Dictionary<int, CpuTickSample> _lastPerCore = [];
    private readonly ConcurrentDictionary<string, NetSample> _lastNet = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, ProcCpuSample> _lastWinProcs = new();
    private SystemHostInfo? _cachedHost;

    private sealed record CpuTickSample(ulong User, ulong System, ulong Idle, ulong Nice, long TimestampMs);
    private sealed record NetSample(long RxBytes, long TxBytes, long TimestampMs);
    private sealed record ProcCpuSample(TimeSpan TotalCpu, long TimestampMs);

    public async Task<SystemMetricsSnapshot> CollectAsync(SystemMetricsRequest request, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshot = new SystemMetricsSnapshot
        {
            DeviceKey = "local",
            Host = await ReadHostInfoAsync(cancellationToken).ConfigureAwait(false),
            SampledAtMs = nowMs,
        };

        if (OperatingSystem.IsLinux())
        {
            await CollectLinuxAsync(snapshot, request, nowMs, cancellationToken).ConfigureAwait(false);
        }
        else if (OperatingSystem.IsMacOS())
        {
            await CollectMacOsAsync(snapshot, request, nowMs, cancellationToken).ConfigureAwait(false);
        }
        else if (OperatingSystem.IsWindows())
        {
            CollectWindows(snapshot, request, nowMs);
        }

        snapshot.DurationMs = sw.ElapsedMilliseconds;
        return snapshot;
    }

    public Task<bool> SignalProcessAsync(int pid, ProcessSignalKind signal, CancellationToken cancellationToken = default)
    {
        if (pid <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pid), "Signaling PID <= 1 is not permitted.");
        }
        if (pid == Environment.ProcessId)
        {
            throw new InvalidOperationException("Cannot signal the Octopus host process itself.");
        }

        if (OperatingSystem.IsWindows())
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: signal == ProcessSignalKind.Kill);
            return Task.FromResult(true);
        }

        var sig = signal switch
        {
            ProcessSignalKind.Kill => 9,
            ProcessSignalKind.Int => 2,
            ProcessSignalKind.Hup => 1,
            _ => 15,
        };

        var rc = UnixKill(pid, sig);
        if (rc == 0) return Task.FromResult(true);
        var errno = Marshal.GetLastPInvokeError();
        if (errno == 3) return Task.FromResult(false); // ESRCH: already gone
        if (errno == 1) throw new UnauthorizedAccessException($"Permission denied signaling PID {pid}.");
        throw new IOException($"kill({pid}, {sig}) failed with errno {errno}.");
    }

    // ───────────────────────── Host info ─────────────────────────

    private async Task<SystemHostInfo> ReadHostInfoAsync(CancellationToken ct)
    {
        var host = _cachedHost;
        if (host is null)
        {
            host = new SystemHostInfo
            {
                Hostname = Environment.MachineName,
                Os = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unknown",
                OsVersion = Environment.OSVersion.VersionString,
                Arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
                Kernel = RuntimeInformation.OSDescription,
            };
            if (OperatingSystem.IsMacOS())
            {
                var ver = await RunTextAsync("sw_vers", ["-productVersion"], ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(ver)) host.OsVersion = $"macOS {ver.Trim()}";
            }
            _cachedHost = host;
        }

        long? uptime = null;
        if (OperatingSystem.IsLinux() && File.Exists("/proc/uptime"))
        {
            var text = await File.ReadAllTextAsync("/proc/uptime", ct).ConfigureAwait(false);
            var first = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (double.TryParse(first, CultureInfo.InvariantCulture, out var secs)) uptime = (long)secs;
        }
        else if (OperatingSystem.IsMacOS())
        {
            uptime = await ReadMacUptimeAsync(ct).ConfigureAwait(false);
        }
        else
        {
            uptime = Environment.TickCount64 / 1000;
        }

        return new SystemHostInfo
        {
            Hostname = host.Hostname,
            Os = host.Os,
            OsVersion = host.OsVersion,
            Arch = host.Arch,
            Kernel = host.Kernel,
            UptimeSeconds = uptime,
        };
    }

    // ───────────────────────── macOS ─────────────────────────

    private async Task CollectMacOsAsync(SystemMetricsSnapshot snapshot, SystemMetricsRequest request, long nowMs, CancellationToken ct)
    {
        var tasks = new List<Task>();

        if (request.IncludeCpu)
        {
            tasks.Add(Task.Run(async () =>
            {
                var cpu = new CpuMetrics { Cores = Environment.ProcessorCount };
                if (TryReadMacCpuLoad(nowMs, out var usage, out var userPct, out var sysPct, out var idlePct))
                {
                    cpu.UsagePercent = usage;
                    cpu.UserPercent = userPct;
                    cpu.SystemPercent = sysPct;
                    cpu.IdlePercent = idlePct;
                }
                if (TryReadMacPerCoreCpu(nowMs, out var perCore) && perCore.Count > 0)
                {
                    cpu.PerCore = perCore;
                    if (cpu.UsagePercent <= 0) cpu.UsagePercent = Math.Round(perCore.Average(), 1);
                }
                var loadText = await RunTextAsync("sysctl", ["-n", "vm.loadavg"], ct).ConfigureAwait(false);
                ParseLoadAvg(loadText, cpu);
                var model = await RunTextAsync("sysctl", ["-n", "machdep.cpu.brand_string"], ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(model)) cpu.Model = model.Trim();
                snapshot.Cpu = cpu;
            }, ct));
        }

        if (request.IncludeMemory)
        {
            tasks.Add(Task.Run(async () =>
            {
                snapshot.Memory = await ReadMacMemoryAsync(ct).ConfigureAwait(false);
            }, ct));
        }

        if (request.IncludeDisk)
        {
            tasks.Add(Task.Run(async () =>
            {
                snapshot.Disks = await ReadDfDisksAsync(ct).ConfigureAwait(false);
            }, ct));
        }

        if (request.IncludeNetwork)
        {
            tasks.Add(Task.Run(async () =>
            {
                snapshot.Network = await ReadMacNetworkAsync(nowMs, ct).ConfigureAwait(false);
            }, ct));
        }

        if (request.IncludeProcesses)
        {
            tasks.Add(Task.Run(async () =>
            {
                snapshot.Processes = await ReadUnixProcessesAsync(request, ct).ConfigureAwait(false);
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<long?> ReadMacUptimeAsync(CancellationToken ct)
    {
        var text = await RunTextAsync("sysctl", ["-n", "kern.boottime"], ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return Environment.TickCount64 / 1000;
        var idx = text.IndexOf("sec =", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var rest = text[(idx + 5)..].Trim();
            var comma = rest.IndexOf(',');
            var num = comma >= 0 ? rest[..comma].Trim() : rest.TrimEnd('}', ' ');
            if (long.TryParse(num, CultureInfo.InvariantCulture, out var bootSec))
            {
                return Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - bootSec);
            }
        }
        return Environment.TickCount64 / 1000;
    }

    private async Task<MemoryMetrics> ReadMacMemoryAsync(CancellationToken ct)
    {
        var totalStr = await RunTextAsync("sysctl", ["-n", "hw.memsize"], ct).ConfigureAwait(false);
        long.TryParse(totalStr?.Trim(), CultureInfo.InvariantCulture, out var totalBytes);
        var vmStat = await RunTextAsync("vm_stat", [], ct).ConfigureAwait(false) ?? "";
        long pageSize = 4096;
        var firstLine = vmStat.Split('\n').FirstOrDefault() ?? "";
        var psIdx = firstLine.IndexOf("page size of ", StringComparison.OrdinalIgnoreCase);
        if (psIdx >= 0)
        {
            var after = firstLine[(psIdx + 13)..];
            var end = after.IndexOf(' ');
            if (end > 0 && long.TryParse(after[..end], CultureInfo.InvariantCulture, out var ps) && ps > 0) pageSize = ps;
        }

        long active = 0, wired = 0, compressed = 0, inactive = 0, free = 0, speculative = 0, purgeable = 0;
        foreach (var raw in vmStat.Split('\n'))
        {
            var colon = raw.IndexOf(':');
            if (colon <= 0) continue;
            var key = raw[..colon].Trim();
            var valStr = raw[(colon + 1)..].Trim().TrimEnd('.');
            if (!long.TryParse(valStr, CultureInfo.InvariantCulture, out var pages)) continue;
            if (key.Equals("Pages active", StringComparison.OrdinalIgnoreCase)) active = pages;
            else if (key.Equals("Pages wired down", StringComparison.OrdinalIgnoreCase)) wired = pages;
            else if (key.Equals("Pages occupied by compressor", StringComparison.OrdinalIgnoreCase)) compressed = pages;
            else if (key.Equals("Pages inactive", StringComparison.OrdinalIgnoreCase)) inactive = pages;
            else if (key.Equals("Pages free", StringComparison.OrdinalIgnoreCase)) free = pages;
            else if (key.Equals("Pages speculative", StringComparison.OrdinalIgnoreCase)) speculative = pages;
            else if (key.Equals("Pages purgeable", StringComparison.OrdinalIgnoreCase)) purgeable = pages;
        }

        var usedBytes = Math.Clamp((active + wired + compressed) * pageSize, 0, totalBytes > 0 ? totalBytes : long.MaxValue);
        var cachedBytes = (inactive + purgeable) * pageSize;
        var availableBytes = totalBytes > 0 ? Math.Max(0, totalBytes - usedBytes) : (free + inactive + speculative) * pageSize;

        long swapTotal = 0, swapUsed = 0;
        var swapText = await RunTextAsync("sysctl", ["-n", "vm.swapusage"], ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(swapText))
        {
            swapTotal = ParseMacSizeToken(swapText, "total =");
            swapUsed = ParseMacSizeToken(swapText, "used =");
        }

        return new MemoryMetrics
        {
            TotalBytes = totalBytes,
            UsedBytes = usedBytes,
            AvailableBytes = availableBytes,
            CachedBytes = cachedBytes,
            UsedPercent = totalBytes > 0 ? Math.Round(usedBytes * 100.0 / totalBytes, 1) : 0,
            SwapTotalBytes = swapTotal,
            SwapUsedBytes = swapUsed,
            SwapPercent = swapTotal > 0 ? Math.Round(swapUsed * 100.0 / swapTotal, 1) : 0,
        };
    }

    private static long ParseMacSizeToken(string text, string label)
    {
        var idx = text.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        var rest = text[(idx + label.Length)..].Trim();
        var token = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(token)) return 0;
        var unit = char.ToUpperInvariant(token[^1]);
        var mult = unit switch { 'K' => 1024.0, 'M' => 1024.0 * 1024, 'G' => 1024.0 * 1024 * 1024, 'T' => 1024.0 * 1024 * 1024 * 1024, _ => 1.0 };
        var numPart = char.IsLetter(unit) ? token[..^1] : token;
        return double.TryParse(numPart, CultureInfo.InvariantCulture, out var val) ? (long)(val * mult) : 0;
    }

    private async Task<List<NetworkInterfaceMetrics>> ReadMacNetworkAsync(long nowMs, CancellationToken ct)
    {
        var output = await RunTextAsync("netstat", ["-ibn"], ct).ConfigureAwait(false) ?? "";
        var totals = new Dictionary<string, (long Rx, long Tx, string? Ip)>(StringComparer.Ordinal);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 10) continue;
            var name = parts[0].TrimEnd('*');
            if (name.StartsWith("lo", StringComparison.Ordinal) || name.StartsWith("utun", StringComparison.Ordinal) || name.StartsWith("awdl", StringComparison.Ordinal) || name.StartsWith("llw", StringComparison.Ordinal))
            {
                continue;
            }
            if (parts[2].StartsWith("<Link#", StringComparison.Ordinal))
            {
                // Name Mtu Network Address Ipkts Ierrs Ibytes Opkts Oerrs Obytes Coll
                // Sometimes Address is omitted (10 cols vs 11 cols). Parse from the right!
                if (long.TryParse(parts[^5], CultureInfo.InvariantCulture, out var ibytes) &&
                    long.TryParse(parts[^2], CultureInfo.InvariantCulture, out var obytes))
                {
                    totals.TryGetValue(name, out var prev);
                    totals[name] = (Math.Max(prev.Rx, ibytes), Math.Max(prev.Tx, obytes), prev.Ip);
                }
            }
            else if (parts[3].Count(c => c == '.') == 3 && !parts[3].StartsWith("127.", StringComparison.Ordinal))
            {
                totals.TryGetValue(name, out var prev);
                totals[name] = (prev.Rx, prev.Tx, parts[3]);
            }
        }
        return ComputeNetRates(totals, nowMs);
    }

    private bool TryReadMacCpuLoad(long nowMs, out double usage, out double userPct, out double sysPct, out double idlePct)
    {
        usage = userPct = sysPct = idlePct = 0;
        try
        {
            var info = new HostCpuLoadInfo();
            var count = 4;
            if (MacHostStatistics(MacMachHostSelf(), 3 /* HOST_CPU_LOAD_INFO */, ref info, ref count) != 0)
            {
                return false;
            }
            var sample = new CpuTickSample(info.User, info.System, info.Idle, info.Nice, nowMs);
            return ComputeCpuDelta(sample, out usage, out userPct, out sysPct, out idlePct);
        }
        catch
        {
            return false;
        }
    }

    private bool TryReadMacPerCoreCpu(long nowMs, out List<double> perCore)
    {
        perCore = [];
        try
        {
            var rc = MacHostProcessorInfo(MacMachHostSelf(), 2 /* PROCESSOR_CPU_LOAD_INFO */, out var cpuCount, out var infoPtr, out var infoCount);
            if (rc != 0 || infoPtr == IntPtr.Zero || cpuCount <= 0) return false;
            try
            {
                var next = new Dictionary<int, CpuTickSample>(cpuCount);
                lock (_lock)
                {
                    for (var i = 0; i < cpuCount; i++)
                    {
                        var offset = i * 4 * sizeof(uint);
                        var u = (ulong)(uint)Marshal.ReadInt32(infoPtr, offset);
                        var s = (ulong)(uint)Marshal.ReadInt32(infoPtr, offset + 4);
                        var id = (ulong)(uint)Marshal.ReadInt32(infoPtr, offset + 8);
                        var n = (ulong)(uint)Marshal.ReadInt32(infoPtr, offset + 12);
                        var cur = new CpuTickSample(u, s, id, n, nowMs);
                        next[i] = cur;
                        if (_lastPerCore.TryGetValue(i, out var prev))
                        {
                            var du = cur.User >= prev.User ? cur.User - prev.User : 0;
                            var ds = cur.System >= prev.System ? cur.System - prev.System : 0;
                            var di = cur.Idle >= prev.Idle ? cur.Idle - prev.Idle : 0;
                            var dn = cur.Nice >= prev.Nice ? cur.Nice - prev.Nice : 0;
                            var tot = du + ds + di + dn;
                            perCore.Add(tot > 0 ? Math.Round((du + ds + dn) * 100.0 / tot, 1) : 0);
                        }
                        else
                        {
                            var tot = u + s + id + n;
                            perCore.Add(tot > 0 ? Math.Round((u + s + n) * 100.0 / tot, 1) : 0);
                        }
                    }
                    _lastPerCore = next;
                }
                return true;
            }
            finally
            {
                _ = MacVmDeallocate(MacMachTaskSelf(), infoPtr, (nuint)(infoCount * sizeof(int)));
            }
        }
        catch
        {
            return false;
        }
    }

    // ───────────────────────── Linux ─────────────────────────

    private async Task CollectLinuxAsync(SystemMetricsSnapshot snapshot, SystemMetricsRequest request, long nowMs, CancellationToken ct)
    {
        if (request.IncludeCpu && File.Exists("/proc/stat"))
        {
            var statLines = await File.ReadAllLinesAsync("/proc/stat", ct).ConfigureAwait(false);
            var cpu = new CpuMetrics { Cores = Environment.ProcessorCount };
            var perCore = new List<double>();
            var nextCores = new Dictionary<int, CpuTickSample>();

            foreach (var line in statLines)
            {
                if (!line.StartsWith("cpu", StringComparison.Ordinal)) break;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                var user = ParseUlong(parts, 1);
                var nice = ParseUlong(parts, 2);
                var sys = ParseUlong(parts, 3) + ParseUlong(parts, 6) + ParseUlong(parts, 7);
                var idle = ParseUlong(parts, 4) + ParseUlong(parts, 5);
                var sample = new CpuTickSample(user, sys, idle, nice, nowMs);

                if (parts[0] == "cpu")
                {
                    if (ComputeCpuDelta(sample, out var usage, out var userPct, out var sysPct, out var idlePct))
                    {
                        cpu.UsagePercent = usage;
                        cpu.UserPercent = userPct;
                        cpu.SystemPercent = sysPct;
                        cpu.IdlePercent = idlePct;
                    }
                }
                else if (int.TryParse(parts[0][3..], CultureInfo.InvariantCulture, out var coreIdx))
                {
                    nextCores[coreIdx] = sample;
                    lock (_lock)
                    {
                        if (_lastPerCore.TryGetValue(coreIdx, out var prev))
                        {
                            var du = sample.User >= prev.User ? sample.User - prev.User : 0;
                            var ds = sample.System >= prev.System ? sample.System - prev.System : 0;
                            var di = sample.Idle >= prev.Idle ? sample.Idle - prev.Idle : 0;
                            var dn = sample.Nice >= prev.Nice ? sample.Nice - prev.Nice : 0;
                            var tot = du + ds + di + dn;
                            perCore.Add(tot > 0 ? Math.Round((du + ds + dn) * 100.0 / tot, 1) : 0);
                        }
                        else
                        {
                            var tot = user + sys + idle + nice;
                            perCore.Add(tot > 0 ? Math.Round((user + sys + nice) * 100.0 / tot, 1) : 0);
                        }
                    }
                }
            }
            lock (_lock) { _lastPerCore = nextCores; }
            if (perCore.Count > 0) cpu.PerCore = perCore;

            if (File.Exists("/proc/loadavg"))
            {
                ParseLoadAvg(await File.ReadAllTextAsync("/proc/loadavg", ct).ConfigureAwait(false), cpu);
            }
            snapshot.Cpu = cpu;
        }

        if (request.IncludeMemory && File.Exists("/proc/meminfo"))
        {
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in await File.ReadAllLinesAsync("/proc/meminfo", ct).ConfigureAwait(false))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line[..colon].Trim();
                var valPart = line[(colon + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (long.TryParse(valPart, CultureInfo.InvariantCulture, out var kb)) map[key] = kb * 1024;
            }
            var total = map.GetValueOrDefault("MemTotal");
            var avail = map.TryGetValue("MemAvailable", out var a) ? a : map.GetValueOrDefault("MemFree") + map.GetValueOrDefault("Buffers") + map.GetValueOrDefault("Cached");
            var used = Math.Max(0, total - avail);
            var cached = map.GetValueOrDefault("Cached") + map.GetValueOrDefault("Buffers");
            var swapTotal = map.GetValueOrDefault("SwapTotal");
            var swapFree = map.GetValueOrDefault("SwapFree");
            var swapUsed = Math.Max(0, swapTotal - swapFree);
            snapshot.Memory = new MemoryMetrics
            {
                TotalBytes = total,
                UsedBytes = used,
                AvailableBytes = avail,
                CachedBytes = cached,
                UsedPercent = total > 0 ? Math.Round(used * 100.0 / total, 1) : 0,
                SwapTotalBytes = swapTotal,
                SwapUsedBytes = swapUsed,
                SwapPercent = swapTotal > 0 ? Math.Round(swapUsed * 100.0 / swapTotal, 1) : 0,
            };
        }

        if (request.IncludeDisk)
        {
            snapshot.Disks = await ReadDfDisksAsync(ct).ConfigureAwait(false);
        }

        if (request.IncludeNetwork && File.Exists("/proc/net/dev"))
        {
            var totals = new Dictionary<string, (long Rx, long Tx, string? Ip)>(StringComparer.Ordinal);
            foreach (var line in (await File.ReadAllLinesAsync("/proc/net/dev", ct).ConfigureAwait(false)).Skip(2))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var name = line[..colon].Trim();
                if (name == "lo") continue;
                var parts = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 9 &&
                    long.TryParse(parts[0], CultureInfo.InvariantCulture, out var rx) &&
                    long.TryParse(parts[8], CultureInfo.InvariantCulture, out var tx))
                {
                    totals[name] = (rx, tx, null);
                }
            }
            snapshot.Network = ComputeNetRates(totals, nowMs);
        }

        if (request.IncludeProcesses)
        {
            snapshot.Processes = await ReadUnixProcessesAsync(request, ct).ConfigureAwait(false);
        }
    }

    // ───────────────────────── Windows ─────────────────────────

    private void CollectWindows(SystemMetricsSnapshot snapshot, SystemMetricsRequest request, long nowMs)
    {
        if (request.IncludeCpu)
        {
            var cpu = new CpuMetrics { Cores = Environment.ProcessorCount };
            if (WinGetSystemTimes(out var idle, out var kernel, out var user))
            {
                var sys = kernel >= idle ? kernel - idle : 0;
                if (ComputeCpuDelta(new CpuTickSample(user, sys, idle, 0, nowMs), out var usage, out var userPct, out var sysPct, out var idlePct))
                {
                    cpu.UsagePercent = usage;
                    cpu.UserPercent = userPct;
                    cpu.SystemPercent = sysPct;
                    cpu.IdlePercent = idlePct;
                }
            }
            snapshot.Cpu = cpu;
        }

        long totalMemBytes = 0;
        if (request.IncludeMemory || request.IncludeProcesses)
        {
            var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (WinGlobalMemoryStatusEx(ref mem))
            {
                totalMemBytes = (long)mem.TotalPhys;
                if (request.IncludeMemory)
                {
                    var used = (long)(mem.TotalPhys - mem.AvailPhys);
                    var swapTotal = Math.Max(0, (long)mem.TotalPageFile - (long)mem.TotalPhys);
                    var swapAvail = Math.Max(0, (long)mem.AvailPageFile - (long)mem.AvailPhys);
                    var swapUsed = Math.Max(0, swapTotal - swapAvail);
                    snapshot.Memory = new MemoryMetrics
                    {
                        TotalBytes = totalMemBytes,
                        UsedBytes = used,
                        AvailableBytes = (long)mem.AvailPhys,
                        UsedPercent = totalMemBytes > 0 ? Math.Round(used * 100.0 / totalMemBytes, 1) : 0,
                        SwapTotalBytes = swapTotal,
                        SwapUsedBytes = swapUsed,
                        SwapPercent = swapTotal > 0 ? Math.Round(swapUsed * 100.0 / swapTotal, 1) : 0,
                    };
                }
            }
        }

        if (request.IncludeDisk)
        {
            var disks = new List<DiskMetrics>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || d.TotalSize <= 0) continue;
                    var used = d.TotalSize - d.AvailableFreeSpace;
                    disks.Add(new DiskMetrics
                    {
                        Mount = d.Name,
                        Filesystem = d.DriveFormat,
                        TotalBytes = d.TotalSize,
                        UsedBytes = used,
                        AvailableBytes = d.AvailableFreeSpace,
                        UsedPercent = Math.Round(used * 100.0 / d.TotalSize, 1),
                    });
                }
                catch { }
            }
            snapshot.Disks = disks;
        }

        if (request.IncludeNetwork)
        {
            var totals = new Dictionary<string, (long Rx, long Tx, string? Ip)>(StringComparer.Ordinal);
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                try
                {
                    var stats = nic.GetIPStatistics();
                    var ip = nic.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
                    totals[nic.Name] = (stats.BytesReceived, stats.BytesSent, ip);
                }
                catch { }
            }
            snapshot.Network = ComputeNetRates(totals, nowMs);
        }

        if (request.IncludeProcesses)
        {
            var list = new List<ProcessMetrics>();
            var activePids = new HashSet<int>();
            var cores = Math.Max(1, Environment.ProcessorCount);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        var pid = p.Id;
                        if (pid <= 0) continue;
                        activePids.Add(pid);
                        double cpuPct = 0;
                        try
                        {
                            var cpuTime = p.TotalProcessorTime;
                            if (_lastWinProcs.TryGetValue(pid, out var prev) && nowMs > prev.TimestampMs)
                            {
                                var elapsedMs = nowMs - prev.TimestampMs;
                                var deltaMs = (cpuTime - prev.TotalCpu).TotalMilliseconds;
                                cpuPct = Math.Round(Math.Clamp(deltaMs / (elapsedMs * cores) * 100.0, 0, 100 * cores), 1);
                            }
                            _lastWinProcs[pid] = new ProcCpuSample(cpuTime, nowMs);
                        }
                        catch { }
                        var rss = p.WorkingSet64;
                        list.Add(new ProcessMetrics
                        {
                            Pid = pid,
                            Name = p.ProcessName,
                            CpuPercent = cpuPct,
                            MemPercent = totalMemBytes > 0 ? Math.Round(rss * 100.0 / totalMemBytes, 1) : 0,
                            RssBytes = rss,
                            Threads = p.Threads.Count,
                            Command = p.ProcessName,
                        });
                    }
                    catch { }
                }
            }
            foreach (var stale in _lastWinProcs.Keys.Where(k => !activePids.Contains(k)).ToList())
            {
                _lastWinProcs.TryRemove(stale, out _);
            }
            snapshot.Processes = FilterAndSortProcesses(list, request);
        }
    }

    // ───────────────────────── Shared Unix helpers ─────────────────────────

    private async Task<List<DiskMetrics>> ReadDfDisksAsync(CancellationToken ct)
    {
        var output = await RunTextAsync("df", ["-kP"], ct).ConfigureAwait(false) ?? "";
        return ParseDfOutput(output);
    }

    private async Task<List<ProcessMetrics>> ReadUnixProcessesAsync(SystemMetricsRequest request, CancellationToken ct)
    {
        var args = OperatingSystem.IsMacOS()
            ? new[] { "-axo", "pid=,ppid=,user=,state=,%cpu=,%mem=,rss=,comm=,args=" }
            : new[] { "-eo", "pid=,ppid=,user:20=,stat=,%cpu=,%mem=,rss=,comm=,args=" };
        var output = await RunTextAsync("ps", args, ct).ConfigureAwait(false) ?? "";
        var all = ParsePsOutput(output);
        return FilterAndSortProcesses(all, request);
    }

    public static List<DiskMetrics> ParseDfOutput(string output)
    {
        var disks = new List<DiskMetrics>();
        var seenMounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6) continue;
            var fs = parts[0];
            if (fs.StartsWith("devfs", StringComparison.Ordinal) || fs.StartsWith("map ", StringComparison.Ordinal)
                || fs is "tmpfs" or "devtmpfs" or "overlay" or "shm" or "efivarfs" or "squashfs" or "proc" or "sysfs" or "cgroup")
            {
                continue;
            }
            var mount = string.Join(' ', parts[5..]);
            if (mount.StartsWith("/System/Volumes/", StringComparison.Ordinal) && mount != "/System/Volumes/Data") continue;
            if (mount.StartsWith("/snap/", StringComparison.Ordinal) || mount.StartsWith("/run", StringComparison.Ordinal) || mount.StartsWith("/dev", StringComparison.Ordinal)) continue;
            if (!seenMounts.Add(mount)) continue;

            if (!long.TryParse(parts[1], CultureInfo.InvariantCulture, out var totalKb) || totalKb <= 0) continue;
            long.TryParse(parts[2], CultureInfo.InvariantCulture, out var usedKb);
            long.TryParse(parts[3], CultureInfo.InvariantCulture, out var availKb);
            var total = totalKb * 1024L;
            var used = usedKb * 1024L;
            var avail = availKb * 1024L;
            disks.Add(new DiskMetrics
            {
                Mount = mount,
                Filesystem = fs,
                TotalBytes = total,
                UsedBytes = used,
                AvailableBytes = avail,
                UsedPercent = total > 0 ? Math.Round(used * 100.0 / total, 1) : 0,
            });
        }
        return disks;
    }

    public static List<ProcessMetrics> ParsePsOutput(string output)
    {
        var list = new List<ProcessMetrics>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(' ', 9, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 8) continue;
            if (!int.TryParse(parts[0], CultureInfo.InvariantCulture, out var pid)) continue;
            int.TryParse(parts[1], CultureInfo.InvariantCulture, out var ppid);
            var user = parts[2];
            var state = parts[3];
            double.TryParse(parts[4], CultureInfo.InvariantCulture, out var cpu);
            double.TryParse(parts[5], CultureInfo.InvariantCulture, out var mem);
            long.TryParse(parts[6], CultureInfo.InvariantCulture, out var rssKb);
            var comm = Path.GetFileName(parts[7]);
            var cmd = parts.Length >= 9 ? parts[8].Trim() : parts[7];
            if (string.IsNullOrEmpty(comm)) comm = cmd.Split(' ', 2)[0];

            list.Add(new ProcessMetrics
            {
                Pid = pid,
                Ppid = ppid,
                Name = comm,
                User = user,
                State = state,
                CpuPercent = Math.Round(cpu, 1),
                MemPercent = Math.Round(mem, 1),
                RssBytes = rssKb * 1024L,
                Command = cmd.Length > 512 ? cmd[..512] : cmd,
            });
        }
        return list;
    }

    public static List<ProcessMetrics> FilterAndSortProcesses(List<ProcessMetrics> all, SystemMetricsRequest request)
    {
        IEnumerable<ProcessMetrics> query = all;
        if (!string.IsNullOrWhiteSpace(request.ProcessFilter))
        {
            var f = request.ProcessFilter.Trim();
            query = query.Where(p =>
                p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                || (p.Command?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                || (p.User?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                || p.Pid.ToString(CultureInfo.InvariantCulture).Contains(f, StringComparison.Ordinal));
        }

        query = (request.ProcessSort ?? "cpu").ToLowerInvariant() switch
        {
            "memory" or "mem" => query.OrderByDescending(p => p.RssBytes).ThenByDescending(p => p.CpuPercent),
            "pid" => query.OrderBy(p => p.Pid),
            "name" => query.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.RssBytes),
        };

        var limit = Math.Clamp(request.ProcessLimit, 1, 200);
        return [.. query.Take(limit)];
    }

    public static void ParseLoadAvg(string? text, CpuMetrics cpu)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var clean = text.Replace('{', ' ').Replace('}', ' ').Replace(',', ' ');
        var parts = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3
            && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var l1)
            && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var l5)
            && double.TryParse(parts[2], CultureInfo.InvariantCulture, out var l15))
        {
            cpu.LoadAvg1 = Math.Round(l1, 2);
            cpu.LoadAvg5 = Math.Round(l5, 2);
            cpu.LoadAvg15 = Math.Round(l15, 2);
        }
    }

    private bool ComputeCpuDelta(CpuTickSample current, out double usage, out double userPct, out double sysPct, out double idlePct)
    {
        lock (_lock)
        {
            var prev = _lastCpu;
            _lastCpu = current;
            ulong du, ds, di, dn;
            if (prev is not null)
            {
                du = current.User >= prev.User ? current.User - prev.User : 0;
                ds = current.System >= prev.System ? current.System - prev.System : 0;
                di = current.Idle >= prev.Idle ? current.Idle - prev.Idle : 0;
                dn = current.Nice >= prev.Nice ? current.Nice - prev.Nice : 0;
            }
            else
            {
                du = current.User;
                ds = current.System;
                di = current.Idle;
                dn = current.Nice;
            }
            var total = du + ds + di + dn;
            if (total == 0)
            {
                usage = userPct = sysPct = idlePct = 0;
                return false;
            }
            userPct = Math.Round((du + dn) * 100.0 / total, 1);
            sysPct = Math.Round(ds * 100.0 / total, 1);
            idlePct = Math.Round(di * 100.0 / total, 1);
            usage = Math.Round((du + ds + dn) * 100.0 / total, 1);
            return true;
        }
    }

    private List<NetworkInterfaceMetrics> ComputeNetRates(Dictionary<string, (long Rx, long Tx, string? Ip)> totals, long nowMs)
    {
        var result = new List<NetworkInterfaceMetrics>(totals.Count);
        foreach (var (name, (rx, tx, ip)) in totals)
        {
            double rxRate = 0, txRate = 0;
            if (_lastNet.TryGetValue(name, out var prev) && nowMs > prev.TimestampMs)
            {
                var dt = (nowMs - prev.TimestampMs) / 1000.0;
                if (dt > 0.05)
                {
                    if (rx >= prev.RxBytes) rxRate = Math.Round((rx - prev.RxBytes) / dt, 1);
                    if (tx >= prev.TxBytes) txRate = Math.Round((tx - prev.TxBytes) / dt, 1);
                }
            }
            _lastNet[name] = new NetSample(rx, tx, nowMs);
            result.Add(new NetworkInterfaceMetrics
            {
                Name = name,
                RxBytesPerSec = rxRate,
                TxBytesPerSec = txRate,
                RxTotalBytes = rx,
                TxTotalBytes = tx,
                Ipv4 = ip,
            });
        }
        return [.. result.OrderByDescending(n => n.RxBytesPerSec + n.TxBytesPerSec).ThenBy(n => n.Name, StringComparer.Ordinal)];
    }

    private static ulong ParseUlong(string[] parts, int index) =>
        index < parts.Length && ulong.TryParse(parts[index], CultureInfo.InvariantCulture, out var v) ? v : 0;

    private async Task<string?> RunTextAsync(string fileName, IEnumerable<string> args, CancellationToken ct)
    {
        var res = await runner.RunAsync(fileName, args, timeout: TimeSpan.FromSeconds(5), cancellationToken: ct).ConfigureAwait(false);
        return res.Succeeded ? res.StandardOutput : null;
    }

    // ───────────────────────── P/Invoke ─────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct HostCpuLoadInfo
    {
        public uint User;
        public uint System;
        public uint Idle;
        public uint Nice;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mach_host_self")]
    private static extern uint MacMachHostSelf();

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mach_task_self")]
    private static extern uint MacMachTaskSelf();

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "host_statistics")]
    private static extern int MacHostStatistics(uint hostPriv, int flavor, ref HostCpuLoadInfo info, ref int count);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "host_processor_info")]
    private static extern int MacHostProcessorInfo(uint host, int flavor, out int outProcessorCount, out IntPtr outProcessorInfo, out int outProcessorInfoCount);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "vm_deallocate")]
    private static extern int MacVmDeallocate(uint targetTask, IntPtr address, nuint size);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int UnixKill(int pid, int sig);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinGlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [DllImport("kernel32.dll", EntryPoint = "GetSystemTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinGetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);
}
