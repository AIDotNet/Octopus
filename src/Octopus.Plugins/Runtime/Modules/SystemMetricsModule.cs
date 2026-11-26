using System.Text.Json.Nodes;
using Octopus.Core.Projects;
using Octopus.Core.SystemMetrics;

namespace Octopus.Plugins.Runtime.Modules;

/// <summary>
/// <c>octopus.system</c>: normalized system metrics (<c>system.metrics</c>) and process signaling
/// (<c>system.process.signal</c>) across local, SSH and remote Octopus devices.
/// </summary>
public sealed class SystemMetricsModule(IPluginSystemService system, IPluginDeviceContext devices) : IPluginApiModule
{
    public void Register(PluginApiRouter router)
    {
        router.Request("system.snapshot", SnapshotAsync);
        router.Request("system.signalProcess", SignalProcessAsync);
    }

    private async Task<PluginReply> SnapshotAsync(PluginCall call, CancellationToken ct)
    {
        call.Plugin.Grants.Require("system.metrics");
        var deviceKey = DeviceKeys.Normalize(call.String("device") ?? devices.Current.Key);
        var include = call.Strings("include");
        var hasInclude = include.Count > 0;
        bool Want(string name) => !hasInclude || include.Contains(name, StringComparer.OrdinalIgnoreCase);

        var request = new SystemMetricsRequest
        {
            IncludeCpu = Want("cpu"),
            IncludeMemory = Want("memory"),
            IncludeDisk = Want("disk"),
            IncludeNetwork = Want("network"),
            IncludeProcesses = Want("processes"),
            ProcessLimit = (int)Math.Clamp(call.Long("processLimit") ?? 25, 1, 200),
            ProcessSort = call.String("processSort") ?? "cpu",
            ProcessFilter = call.String("processFilter"),
        };

        try
        {
            var snapshot = await system.GetSnapshotAsync(deviceKey, request, ct).ConfigureAwait(false);
            return PluginReply.Of(ToJson(snapshot));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new PluginApiException("Cancelled", "The snapshot request was cancelled.");
        }
        catch (NotSupportedException e)
        {
            throw PluginApiException.Unsupported(e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new PluginApiException("PermissionDenied", e.Message);
        }
        catch (Exception e) when (e is not PluginApiException)
        {
            throw new PluginApiException("Io", e.Message);
        }
    }

    private async Task<PluginReply> SignalProcessAsync(PluginCall call, CancellationToken ct)
    {
        call.Plugin.Grants.Require("system.process.signal");
        var pidLong = call.Long("pid") ?? throw new PluginApiException("InvalidArgument", "pid is required.");
        if (pidLong <= 1 || pidLong > int.MaxValue)
        {
            throw new PluginApiException("InvalidArgument", "Signaling PID <= 1 is not permitted.");
        }
        var sigText = (call.String("signal") ?? "TERM").ToUpperInvariant();
        var signal = sigText switch
        {
            "TERM" => ProcessSignalKind.Term,
            "KILL" => ProcessSignalKind.Kill,
            "INT" => ProcessSignalKind.Int,
            "HUP" => ProcessSignalKind.Hup,
            _ => throw new PluginApiException("InvalidArgument", $"Unsupported signal \"{sigText}\". Use TERM, KILL, INT or HUP."),
        };
        var deviceKey = DeviceKeys.Normalize(call.String("device") ?? devices.Current.Key);

        try
        {
            var ok = await system.SignalProcessAsync(deviceKey, (int)pidLong, signal, ct).ConfigureAwait(false);
            call.Plugin.Write(PluginLogLevel.Info, $"system.signalProcess: device={deviceKey} pid={pidLong} signal={sigText} ok={ok}");
            return PluginReply.Of(new JsonObject { ["ok"] = ok });
        }
        catch (NotSupportedException e)
        {
            throw PluginApiException.Unsupported(e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new PluginApiException("PermissionDenied", e.Message);
        }
        catch (Exception e) when (e is not PluginApiException)
        {
            throw new PluginApiException("Io", e.Message);
        }
    }

    private static JsonObject ToJson(SystemMetricsSnapshot s)
    {
        var root = new JsonObject
        {
            ["deviceKey"] = s.DeviceKey,
            ["sampledAt"] = s.SampledAtMs,
            ["durationMs"] = s.DurationMs,
            ["host"] = new JsonObject
            {
                ["hostname"] = s.Host.Hostname,
                ["os"] = s.Host.Os,
                ["osVersion"] = s.Host.OsVersion,
                ["arch"] = s.Host.Arch,
                ["kernel"] = s.Host.Kernel,
                ["uptimeSeconds"] = s.Host.UptimeSeconds,
            },
        };

        if (s.Cpu is { } c)
        {
            var cpuObj = new JsonObject
            {
                ["usagePercent"] = c.UsagePercent,
                ["userPercent"] = c.UserPercent,
                ["systemPercent"] = c.SystemPercent,
                ["idlePercent"] = c.IdlePercent,
                ["cores"] = c.Cores,
                ["model"] = c.Model,
            };
            if (c.LoadAvg1 is not null && c.LoadAvg5 is not null && c.LoadAvg15 is not null)
            {
                cpuObj["loadAvg"] = new JsonArray(JsonValue.Create(c.LoadAvg1.Value), JsonValue.Create(c.LoadAvg5.Value), JsonValue.Create(c.LoadAvg15.Value));
            }
            if (c.PerCore is { Count: > 0 })
            {
                var arr = new JsonArray();
                foreach (var v in c.PerCore) arr.Add((JsonNode?)JsonValue.Create(v));
                cpuObj["perCore"] = arr;
            }
            root["cpu"] = cpuObj;
        }

        if (s.Memory is { } m)
        {
            root["memory"] = new JsonObject
            {
                ["totalBytes"] = m.TotalBytes,
                ["usedBytes"] = m.UsedBytes,
                ["availableBytes"] = m.AvailableBytes,
                ["cachedBytes"] = m.CachedBytes,
                ["usedPercent"] = m.UsedPercent,
                ["swapTotalBytes"] = m.SwapTotalBytes,
                ["swapUsedBytes"] = m.SwapUsedBytes,
                ["swapPercent"] = m.SwapPercent,
            };
        }

        if (s.Disks is not null)
        {
            var arr = new JsonArray();
            foreach (var d in s.Disks)
            {
                arr.Add((JsonNode)new JsonObject
                {
                    ["mount"] = d.Mount,
                    ["filesystem"] = d.Filesystem,
                    ["totalBytes"] = d.TotalBytes,
                    ["usedBytes"] = d.UsedBytes,
                    ["availableBytes"] = d.AvailableBytes,
                    ["usedPercent"] = d.UsedPercent,
                    ["readBytesPerSec"] = d.ReadBytesPerSec,
                    ["writeBytesPerSec"] = d.WriteBytesPerSec,
                });
            }
            root["disks"] = arr;
        }

        if (s.Network is not null)
        {
            var arr = new JsonArray();
            foreach (var n in s.Network)
            {
                arr.Add((JsonNode)new JsonObject
                {
                    ["name"] = n.Name,
                    ["rxBytesPerSec"] = n.RxBytesPerSec,
                    ["txBytesPerSec"] = n.TxBytesPerSec,
                    ["rxTotalBytes"] = n.RxTotalBytes,
                    ["txTotalBytes"] = n.TxTotalBytes,
                    ["ipv4"] = n.Ipv4,
                });
            }
            root["network"] = arr;
        }

        if (s.Processes is not null)
        {
            var arr = new JsonArray();
            foreach (var p in s.Processes)
            {
                arr.Add((JsonNode)new JsonObject
                {
                    ["pid"] = p.Pid,
                    ["ppid"] = p.Ppid,
                    ["name"] = p.Name,
                    ["user"] = p.User,
                    ["state"] = p.State,
                    ["cpuPercent"] = p.CpuPercent,
                    ["memPercent"] = p.MemPercent,
                    ["rssBytes"] = p.RssBytes,
                    ["threads"] = p.Threads,
                    ["command"] = p.Command,
                });
            }
            root["processes"] = arr;
        }

        return root;
    }
}
