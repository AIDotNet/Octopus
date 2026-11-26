using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Octopus.Core.Projects;
using Octopus.Core.SystemMetrics;
using Octopus.Platform;
using Octopus.Platform.SystemMetrics;

namespace Octopus.Ssh;

/// <summary>
/// Collects CPU, memory, disk, network and process telemetry from an SSH host in a single non-blocking round-trip,
/// reusing Octopus's ControlMaster connection. Also supports signaling processes and controlled allowlisted command
/// execution on SSH hosts.
/// </summary>
public sealed class SshSystemMetricsCollector(IProcessRunner runner, Func<IReadOnlyList<string>> sharedOptions)
{
    private readonly ConcurrentDictionary<string, HostDeltaCache> _caches = new(StringComparer.Ordinal);

    private sealed class HostDeltaCache
    {
        public readonly Lock Gate = new();
        public (ulong User, ulong Sys, ulong Idle, ulong Nice)? LastCpu;
        public Dictionary<int, (ulong User, ulong Sys, ulong Idle, ulong Nice)> LastCores = [];
        public Dictionary<string, (long Rx, long Tx, long Ts)> LastNet = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Collects telemetry from <paramref name="sshHost"/> in one SSH invocation. Supports Linux (<c>/proc</c>) and
    /// macOS/Darwin hosts out of the box.
    /// </summary>
    public async Task<SystemMetricsSnapshot> CollectAsync(string sshHost, SystemMetricsRequest request, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var script = BuildProbeScript(request);
        var args = SshCommands.Arguments(sshHost, script, [.. SshCommands.Unattended, .. sharedOptions()]);
        var result = await runner.RunAsync("ssh", args, timeout: TimeSpan.FromSeconds(12), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var reason = string.IsNullOrWhiteSpace(result.StandardError)
                ? (result.TimedOut ? "SSH telemetry probe timed out." : $"SSH exited with code {result.ExitCode}.")
                : result.StandardError.Trim();
            throw new SshCommandException(reason);
        }

        var sections = SplitSections(result.StandardOutput);
        var cache = _caches.GetOrAdd(sshHost, _ => new HostDeltaCache());
        var snapshot = ParseSnapshot(sshHost, sections, request, cache, nowMs);
        snapshot.DurationMs = sw.ElapsedMilliseconds;
        return snapshot;
    }

    public async Task<bool> SignalProcessAsync(string sshHost, int pid, ProcessSignalKind signal, CancellationToken cancellationToken = default)
    {
        if (pid <= 1) throw new ArgumentOutOfRangeException(nameof(pid), "Signaling PID <= 1 is not permitted.");
        var sig = signal switch
        {
            ProcessSignalKind.Kill => "KILL",
            ProcessSignalKind.Int => "INT",
            ProcessSignalKind.Hup => "HUP",
            _ => "TERM",
        };
        var remoteCmd = $"kill -{sig} {pid}";
        var args = SshCommands.Arguments(sshHost, remoteCmd, [.. SshCommands.Unattended, .. sharedOptions()]);
        var result = await runner.RunAsync("ssh", args, timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Succeeded) return true;
        var err = result.StandardError.Trim();
        if (err.Contains("No such process", StringComparison.OrdinalIgnoreCase)) return false;
        throw new SshCommandException(string.IsNullOrEmpty(err) ? $"kill -{sig} {pid} failed (exit {result.ExitCode})." : err);
    }

    /// <summary>
    /// Runs an allowlisted executable with explicit argv quoting on <paramref name="sshHost"/> over the shared SSH
    /// connection. Never interpolates raw shell syntax.
    /// </summary>
    public async Task<ProcessResult> RunCommandAsync(
        string sshHost,
        string command,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string? stdin = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command) || command.IndexOfAny(['/', '\\', ' ', '\t', '\r', '\n', ';', '&', '|', '$', '`']) >= 0)
        {
            throw new ArgumentException("Command must be a plain executable name.", nameof(command));
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            sb.Append("cd ").Append(SshCommands.RemotePath(workingDirectory)).Append(" && ");
        }
        if (environment is { Count: > 0 })
        {
            sb.Append("env ");
            foreach (var (k, v) in environment)
            {
                if (k.Length == 0 || k.StartsWith("OCTOPUS_", StringComparison.Ordinal)) continue;
                if (!IsValidEnvName(k)) continue;
                sb.Append(k).Append('=').Append(SshCommands.Quote(v)).Append(' ');
            }
        }
        sb.Append(SshCommands.Quote(command));
        foreach (var arg in arguments)
        {
            sb.Append(' ').Append(SshCommands.Quote(arg));
        }

        var sshArgs = SshCommands.Arguments(sshHost, sb.ToString(), [.. SshCommands.Unattended, .. sharedOptions()]);
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        if (!string.IsNullOrEmpty(stdin))
        {
            return await runner.RunWithInputAsync("ssh", sshArgs, Encoding.UTF8.GetBytes(stdin), limit, cancellationToken).ConfigureAwait(false);
        }
        return await runner.RunAsync("ssh", sshArgs, timeout: limit, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static bool IsValidEnvName(string name)
    {
        if (!(char.IsAsciiLetter(name[0]) || name[0] == '_')) return false;
        for (var i = 1; i < name.Length; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(name[i]) || name[i] == '_')) return false;
        }
        return true;
    }

    private static string BuildProbeScript(SystemMetricsRequest req)
    {
        var sb = new StringBuilder();
        sb.Append("OS=$(uname -s 2>/dev/null || echo unknown); ");
        sb.Append("echo '===HOST==='; echo \"os=$OS\"; echo \"host=$(hostname 2>/dev/null)\"; echo \"arch=$(uname -m 2>/dev/null)\"; echo \"kernel=$(uname -r 2>/dev/null)\"; ");
        sb.Append("if [ \"$OS\" = Linux ]; then ");
        sb.Append("  echo \"uptime=$(cut -d' ' -f1 /proc/uptime 2>/dev/null)\"; ");
        sb.Append("  echo \"cores=$(nproc 2>/dev/null || grep -c ^processor /proc/cpuinfo 2>/dev/null)\"; ");
        if (req.IncludeCpu)
        {
            sb.Append("  echo '===CPU==='; grep '^cpu' /proc/stat 2>/dev/null; ");
            sb.Append("  echo '===LOAD==='; cat /proc/loadavg 2>/dev/null; ");
        }
        if (req.IncludeMemory)
        {
            sb.Append("  echo '===MEM==='; cat /proc/meminfo 2>/dev/null; ");
        }
        if (req.IncludeDisk)
        {
            sb.Append("  echo '===DISK==='; df -kP 2>/dev/null; ");
        }
        if (req.IncludeNetwork)
        {
            sb.Append("  echo '===NET==='; cat /proc/net/dev 2>/dev/null; ");
        }
        if (req.IncludeProcesses)
        {
            sb.Append("  echo '===PROC==='; ps -eo pid=,ppid=,user:20=,stat=,%cpu=,%mem=,rss=,comm=,args= 2>/dev/null; ");
        }
        sb.Append("elif [ \"$OS\" = Darwin ]; then ");
        sb.Append("  echo \"cores=$(sysctl -n hw.ncpu 2>/dev/null)\"; ");
        sb.Append("  echo \"boottime=$(sysctl -n kern.boottime 2>/dev/null)\"; ");
        if (req.IncludeCpu)
        {
            sb.Append("  echo '===LOAD==='; sysctl -n vm.loadavg 2>/dev/null; ");
            sb.Append("  echo '===MAC_CPU==='; top -l 1 -n 0 2>/dev/null | grep -i 'CPU usage' | head -n 1; ");
        }
        if (req.IncludeMemory)
        {
            sb.Append("  echo '===MAC_MEM==='; echo \"memsize=$(sysctl -n hw.memsize 2>/dev/null)\"; echo \"swap=$(sysctl -n vm.swapusage 2>/dev/null)\"; vm_stat 2>/dev/null; ");
        }
        if (req.IncludeDisk)
        {
            sb.Append("  echo '===DISK==='; df -kP 2>/dev/null; ");
        }
        if (req.IncludeNetwork)
        {
            sb.Append("  echo '===MAC_NET==='; netstat -ibn 2>/dev/null; ");
        }
        if (req.IncludeProcesses)
        {
            sb.Append("  echo '===PROC==='; ps -axo pid=,ppid=,user=,state=,%cpu=,%mem=,rss=,comm=,args= 2>/dev/null; ");
        }
        sb.Append("fi");
        return sb.ToString();
    }

    private static Dictionary<string, string> SplitSections(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var sb = new StringBuilder();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("===", StringComparison.Ordinal) && line.EndsWith("===", StringComparison.Ordinal) && line.Length > 6)
            {
                if (current is not null) result[current] = sb.ToString();
                current = line[3..^3];
                sb.Clear();
            }
            else if (current is not null)
            {
                sb.AppendLine(line);
            }
        }
        if (current is not null) result[current] = sb.ToString();
        return result;
    }

    private static SystemMetricsSnapshot ParseSnapshot(string sshHost, Dictionary<string, string> sections, SystemMetricsRequest req, HostDeltaCache cache, long nowMs)
    {
        var hostMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (sections.TryGetValue("HOST", out var hostText))
        {
            foreach (var line in hostText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = line.IndexOf('=');
                if (eq > 0) hostMap[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        var rawOs = hostMap.GetValueOrDefault("os", "unknown");
        var os = rawOs.Equals("Linux", StringComparison.OrdinalIgnoreCase) ? "linux"
            : rawOs.Equals("Darwin", StringComparison.OrdinalIgnoreCase) ? "macos"
            : rawOs.ToLowerInvariant();
        int.TryParse(hostMap.GetValueOrDefault("cores"), CultureInfo.InvariantCulture, out var cores);
        long? uptime = null;
        if (double.TryParse(hostMap.GetValueOrDefault("uptime"), CultureInfo.InvariantCulture, out var upSec))
        {
            uptime = (long)upSec;
        }
        else if (hostMap.TryGetValue("boottime", out var bt))
        {
            var idx = bt.IndexOf("sec =", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var rest = bt[(idx + 5)..].Trim();
                var comma = rest.IndexOf(',');
                var num = comma >= 0 ? rest[..comma].Trim() : rest.TrimEnd('}', ' ');
                if (long.TryParse(num, CultureInfo.InvariantCulture, out var bootSec))
                {
                    uptime = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - bootSec);
                }
            }
        }

        var snapshot = new SystemMetricsSnapshot
        {
            DeviceKey = DeviceKeys.Ssh(sshHost),
            SampledAtMs = nowMs,
            Host = new SystemHostInfo
            {
                Hostname = hostMap.GetValueOrDefault("host", sshHost),
                Os = os,
                Arch = hostMap.GetValueOrDefault("arch"),
                Kernel = hostMap.GetValueOrDefault("kernel"),
                UptimeSeconds = uptime,
            },
        };

        if (req.IncludeCpu)
        {
            var cpu = new CpuMetrics { Cores = Math.Max(1, cores) };
            if (sections.TryGetValue("LOAD", out var loadText))
            {
                LocalSystemMetricsCollector.ParseLoadAvg(loadText, cpu);
            }
            if (sections.TryGetValue("CPU", out var cpuText))
            {
                ParseLinuxCpu(cpuText, cpu, cache);
            }
            else if (sections.TryGetValue("MAC_CPU", out var macCpuText))
            {
                ParseMacTopCpu(macCpuText, cpu);
            }
            snapshot.Cpu = cpu;
        }

        if (req.IncludeMemory)
        {
            if (sections.TryGetValue("MEM", out var memText))
            {
                snapshot.Memory = ParseLinuxMeminfo(memText);
            }
            else if (sections.TryGetValue("MAC_MEM", out var macMemText))
            {
                snapshot.Memory = ParseMacMemSection(macMemText);
            }
        }

        if (req.IncludeDisk && sections.TryGetValue("DISK", out var diskText))
        {
            snapshot.Disks = LocalSystemMetricsCollector.ParseDfOutput(diskText);
        }

        if (req.IncludeNetwork)
        {
            if (sections.TryGetValue("NET", out var netText))
            {
                snapshot.Network = ParseLinuxNet(netText, cache, nowMs);
            }
            else if (sections.TryGetValue("MAC_NET", out var macNetText))
            {
                snapshot.Network = ParseMacNet(macNetText, cache, nowMs);
            }
        }

        if (req.IncludeProcesses && sections.TryGetValue("PROC", out var procText))
        {
            var all = LocalSystemMetricsCollector.ParsePsOutput(procText);
            snapshot.Processes = LocalSystemMetricsCollector.FilterAndSortProcesses(all, req);
        }

        return snapshot;
    }

    private static void ParseLinuxCpu(string cpuText, CpuMetrics cpu, HostDeltaCache cache)
    {
        var perCore = new List<double>();
        var nextCores = new Dictionary<int, (ulong, ulong, ulong, ulong)>();
        lock (cache.Gate)
        {
            foreach (var line in cpuText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                ulong Parse(int idx) => idx < parts.Length && ulong.TryParse(parts[idx], CultureInfo.InvariantCulture, out var v) ? v : 0;
                var u = Parse(1);
                var n = Parse(2);
                var s = Parse(3) + Parse(6) + Parse(7);
                var i = Parse(4) + Parse(5);

                if (parts[0] == "cpu")
                {
                    var prev = cache.LastCpu;
                    cache.LastCpu = (u, s, i, n);
                    var du = prev is { } p && u >= p.User ? u - p.User : u;
                    var ds = prev is { } p2 && s >= p2.Sys ? s - p2.Sys : s;
                    var di = prev is { } p3 && i >= p3.Idle ? i - p3.Idle : i;
                    var dn = prev is { } p4 && n >= p4.Nice ? n - p4.Nice : n;
                    var tot = du + ds + di + dn;
                    if (tot > 0)
                    {
                        cpu.UserPercent = Math.Round((du + dn) * 100.0 / tot, 1);
                        cpu.SystemPercent = Math.Round(ds * 100.0 / tot, 1);
                        cpu.IdlePercent = Math.Round(di * 100.0 / tot, 1);
                        cpu.UsagePercent = Math.Round((du + ds + dn) * 100.0 / tot, 1);
                    }
                }
                else if (int.TryParse(parts[0][3..], CultureInfo.InvariantCulture, out var idx))
                {
                    nextCores[idx] = (u, s, i, n);
                    if (cache.LastCores.TryGetValue(idx, out var p))
                    {
                        var du = u >= p.User ? u - p.User : 0;
                        var ds = s >= p.Sys ? s - p.Sys : 0;
                        var di = i >= p.Idle ? i - p.Idle : 0;
                        var dn = n >= p.Nice ? n - p.Nice : 0;
                        var tot = du + ds + di + dn;
                        perCore.Add(tot > 0 ? Math.Round((du + ds + dn) * 100.0 / tot, 1) : 0);
                    }
                    else
                    {
                        var tot = u + s + i + n;
                        perCore.Add(tot > 0 ? Math.Round((u + s + n) * 100.0 / tot, 1) : 0);
                    }
                }
            }
            cache.LastCores = nextCores;
        }
        if (perCore.Count > 0)
        {
            cpu.PerCore = perCore;
            if (cpu.Cores <= 1) cpu.Cores = perCore.Count;
        }
    }

    private static void ParseMacTopCpu(string text, CpuMetrics cpu)
    {
        // "CPU usage: 12.34% user, 5.67% sys, 81.99% idle"
        var colon = text.IndexOf(':');
        if (colon < 0) return;
        foreach (var part in text[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;
            if (!double.TryParse(tokens[0].TrimEnd('%'), CultureInfo.InvariantCulture, out var val)) continue;
            if (tokens[1].StartsWith("user", StringComparison.OrdinalIgnoreCase)) cpu.UserPercent = Math.Round(val, 1);
            else if (tokens[1].StartsWith("sys", StringComparison.OrdinalIgnoreCase)) cpu.SystemPercent = Math.Round(val, 1);
            else if (tokens[1].StartsWith("idle", StringComparison.OrdinalIgnoreCase)) cpu.IdlePercent = Math.Round(val, 1);
        }
        if (cpu.IdlePercent is { } idle) cpu.UsagePercent = Math.Round(Math.Clamp(100.0 - idle, 0, 100), 1);
    }

    private static MemoryMetrics ParseLinuxMeminfo(string text)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
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
        return new MemoryMetrics
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

    private static MemoryMetrics ParseMacMemSection(string text)
    {
        long total = 0, pageSize = 4096, active = 0, wired = 0, compressed = 0, inactive = 0, free = 0, purgeable = 0;
        long swapTotal = 0, swapUsed = 0;
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith("memsize=", StringComparison.Ordinal))
            {
                long.TryParse(raw[8..].Trim(), CultureInfo.InvariantCulture, out total);
                continue;
            }
            if (raw.StartsWith("swap=", StringComparison.Ordinal))
            {
                var swapText = raw[5..];
                swapTotal = ParseSizeToken(swapText, "total =");
                swapUsed = ParseSizeToken(swapText, "used =");
                continue;
            }
            var psIdx = raw.IndexOf("page size of ", StringComparison.OrdinalIgnoreCase);
            if (psIdx >= 0)
            {
                var after = raw[(psIdx + 13)..];
                var end = after.IndexOf(' ');
                if (end > 0 && long.TryParse(after[..end], CultureInfo.InvariantCulture, out var ps) && ps > 0) pageSize = ps;
                continue;
            }
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
            else if (key.Equals("Pages purgeable", StringComparison.OrdinalIgnoreCase)) purgeable = pages;
        }
        var used = Math.Clamp((active + wired + compressed) * pageSize, 0, total > 0 ? total : long.MaxValue);
        var avail = total > 0 ? Math.Max(0, total - used) : (free + inactive) * pageSize;
        return new MemoryMetrics
        {
            TotalBytes = total,
            UsedBytes = used,
            AvailableBytes = avail,
            CachedBytes = (inactive + purgeable) * pageSize,
            UsedPercent = total > 0 ? Math.Round(used * 100.0 / total, 1) : 0,
            SwapTotalBytes = swapTotal,
            SwapUsedBytes = swapUsed,
            SwapPercent = swapTotal > 0 ? Math.Round(swapUsed * 100.0 / swapTotal, 1) : 0,
        };
    }

    private static long ParseSizeToken(string text, string label)
    {
        var idx = text.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        var token = text[(idx + label.Length)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(token)) return 0;
        var unit = char.ToUpperInvariant(token[^1]);
        var mult = unit switch { 'K' => 1024.0, 'M' => 1024.0 * 1024, 'G' => 1024.0 * 1024 * 1024, 'T' => 1024.0 * 1024 * 1024 * 1024, _ => 1.0 };
        var numPart = char.IsLetter(unit) ? token[..^1] : token;
        return double.TryParse(numPart, CultureInfo.InvariantCulture, out var val) ? (long)(val * mult) : 0;
    }

    private static List<NetworkInterfaceMetrics> ParseLinuxNet(string text, HostDeltaCache cache, long nowMs)
    {
        var totals = new Dictionary<string, (long Rx, long Tx, string? Ip)>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(2))
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
        return ComputeRates(totals, cache, nowMs);
    }

    private static List<NetworkInterfaceMetrics> ParseMacNet(string text, HostDeltaCache cache, long nowMs)
    {
        var totals = new Dictionary<string, (long Rx, long Tx, string? Ip)>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 10) continue;
            var name = parts[0].TrimEnd('*');
            if (name.StartsWith("lo", StringComparison.Ordinal) || name.StartsWith("utun", StringComparison.Ordinal) || name.StartsWith("awdl", StringComparison.Ordinal)) continue;
            if (parts[2].StartsWith("<Link#", StringComparison.Ordinal)
                && long.TryParse(parts[^5], CultureInfo.InvariantCulture, out var rx)
                && long.TryParse(parts[^2], CultureInfo.InvariantCulture, out var tx))
            {
                totals.TryGetValue(name, out var prev);
                totals[name] = (Math.Max(prev.Rx, rx), Math.Max(prev.Tx, tx), prev.Ip);
            }
            else if (parts[3].Count(c => c == '.') == 3 && !parts[3].StartsWith("127.", StringComparison.Ordinal))
            {
                totals.TryGetValue(name, out var prev);
                totals[name] = (prev.Rx, prev.Tx, parts[3]);
            }
        }
        return ComputeRates(totals, cache, nowMs);
    }

    private static List<NetworkInterfaceMetrics> ComputeRates(Dictionary<string, (long Rx, long Tx, string? Ip)> totals, HostDeltaCache cache, long nowMs)
    {
        var list = new List<NetworkInterfaceMetrics>(totals.Count);
        lock (cache.Gate)
        {
            foreach (var (name, (rx, tx, ip)) in totals)
            {
                double rxRate = 0, txRate = 0;
                if (cache.LastNet.TryGetValue(name, out var prev) && nowMs > prev.Ts)
                {
                    var dt = (nowMs - prev.Ts) / 1000.0;
                    if (dt > 0.05)
                    {
                        if (rx >= prev.Rx) rxRate = Math.Round((rx - prev.Rx) / dt, 1);
                        if (tx >= prev.Tx) txRate = Math.Round((tx - prev.Tx) / dt, 1);
                    }
                }
                cache.LastNet[name] = (rx, tx, nowMs);
                list.Add(new NetworkInterfaceMetrics
                {
                    Name = name,
                    RxBytesPerSec = rxRate,
                    TxBytesPerSec = txRate,
                    RxTotalBytes = rx,
                    TxTotalBytes = tx,
                    Ipv4 = ip,
                });
            }
        }
        return [.. list.OrderByDescending(n => n.RxBytesPerSec + n.TxBytesPerSec).ThenBy(n => n.Name, StringComparer.Ordinal)];
    }
}
