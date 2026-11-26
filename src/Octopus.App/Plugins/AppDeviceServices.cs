using Octopus.App.Remote;
using Octopus.App.Services;
using Octopus.Core.Projects;
using Octopus.Core.SystemMetrics;
using Octopus.Platform;
using Octopus.Platform.SystemMetrics;
using Octopus.Plugins.Runtime;
using Octopus.Remote.Client;
using Octopus.Remote.Protocol;
using Octopus.Ssh;
using Octopus.Storage;

namespace Octopus.App.Plugins;

/// <summary>Exposes the active device and all known devices (local, paired remote Octopus hosts, SSH aliases) to plugins.</summary>
public sealed class AppDeviceContext : IPluginDeviceContext
{
    private readonly IProjectService _projects;
    private readonly RemoteClientRuntime? _remote;
    private readonly ISshHostCatalog? _ssh;
    private readonly ISettingsStore? _settings;
    private volatile Snapshot _snapshot;

    private sealed record Snapshot(PluginDeviceInfo Current, IReadOnlyList<PluginDeviceInfo> All);

    public AppDeviceContext(
        IProjectService projects,
        RemoteClientRuntime? remote = null,
        ISshHostCatalog? ssh = null,
        ISettingsStore? settings = null)
    {
        _projects = projects;
        _remote = remote;
        _ssh = ssh;
        _settings = settings;
        _snapshot = BuildSnapshot();

        projects.Changed += (_, e) =>
        {
            if (e.Kind is ProjectChangeKind.Projects or ProjectChangeKind.CurrentProject or ProjectChangeKind.Device) Refresh();
        };
        if (remote is not null)
        {
            remote.StateChanged += _ => Refresh();
            remote.Hosts.Changed += Refresh;
        }
        ssh?.Changed += (_, _) => Refresh();
        settings?.Changed += (_, _) => Refresh();
    }

    public PluginDeviceInfo Current => _snapshot.Current;
    public IReadOnlyList<PluginDeviceInfo> All => _snapshot.All;
    public event EventHandler? CurrentChanged;
    public event EventHandler? ListChanged;

    private void Refresh()
    {
        var previous = _snapshot;
        var next = BuildSnapshot();
        _snapshot = next;
        if (previous.Current != next.Current) CurrentChanged?.Invoke(this, EventArgs.Empty);
        if (!previous.All.SequenceEqual(next.All)) ListChanged?.Invoke(this, EventArgs.Empty);
    }

    private Snapshot BuildSnapshot()
    {
        var activeKey = DeviceKeys.Normalize(_projects.ActiveDeviceKey);
        var list = new List<PluginDeviceInfo>();

        // 1. Local device
        var localName = string.IsNullOrWhiteSpace(_settings?.Current.Remote.DeviceName)
            ? Environment.MachineName
            : _settings!.Current.Remote.DeviceName!.Trim();
        var local = new PluginDeviceInfo(
            Key: DeviceKeys.Local,
            Kind: "local",
            Name: localName,
            Target: null,
            Status: "local",
            Capabilities: ["system.metrics", "system.signal", "process.run"]);
        list.Add(local);

        // 2. Paired remote Octopus hosts
        var remoteIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_remote is not null)
        {
            foreach (var peer in _remote.Hosts.Peers.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                remoteIds[peer.Id] = peer.Name;
            }
        }
        foreach (var project in _projects.Projects)
        {
            if (project.RemoteHost is { Length: > 0 } id && !remoteIds.ContainsKey(id))
            {
                remoteIds[id] = _remote?.HostName(id) ?? id;
            }
        }
        foreach (var (hostId, name) in remoteIds.OrderBy(kv => kv.Value, StringComparer.CurrentCultureIgnoreCase))
        {
            var state = _remote?.StateOf(hostId) ?? RemoteHostState.Disconnected;
            var status = state switch
            {
                RemoteHostState.Connected => "online",
                RemoteHostState.Connecting => "connecting",
                _ => "offline",
            };
            var caps = new List<string>();
            if (_remote?.ClientOf(hostId) is { } client)
            {
                if (client.SupportsSystemMetrics) caps.Add("system.metrics");
                if (client.SupportsSystemSignal) caps.Add("system.signal");
                if (client.SupportsProcessRun) caps.Add("process.run");
            }
            list.Add(new PluginDeviceInfo(DeviceKeys.Remote(hostId), "remote", name, hostId, status, caps));
        }

        // 3. SSH hosts
        var sshAliases = new HashSet<string>(StringComparer.Ordinal);
        if (_ssh is not null)
        {
            foreach (var host in _ssh.Current.Hosts) sshAliases.Add(host.Alias);
        }
        foreach (var project in _projects.Projects)
        {
            if (project.SshHost is { Length: > 0 } alias) sshAliases.Add(alias);
        }
        foreach (var alias in sshAliases.OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(new PluginDeviceInfo(
                Key: DeviceKeys.Ssh(alias),
                Kind: "ssh",
                Name: alias,
                Target: alias,
                Status: "unknown",
                Capabilities: ["system.metrics", "system.signal", "process.run"]));
        }

        var current = list.FirstOrDefault(d => string.Equals(d.Key, activeKey, StringComparison.Ordinal)) ?? local;
        return new Snapshot(current, list);
    }
}

/// <summary>Routes plugin telemetry, process signaling and multi-device process runs across local, SSH and remote Octopus devices.</summary>
public sealed class AppPluginSystemService(
    ILocalSystemMetricsCollector local,
    SshSystemMetricsCollector ssh,
    RemoteClientRuntime? remote = null) : IPluginSystemService
{
    public async Task<SystemMetricsSnapshot> GetSnapshotAsync(string deviceKey, SystemMetricsRequest request, CancellationToken cancellationToken = default)
    {
        var (kind, target) = DeviceKeys.Parse(deviceKey);
        return kind switch
        {
            ProjectLocationKind.Local => await local.CollectAsync(request, cancellationToken).ConfigureAwait(false),
            ProjectLocationKind.Ssh when !string.IsNullOrWhiteSpace(target) => await ssh.CollectAsync(target, request, cancellationToken).ConfigureAwait(false),
            ProjectLocationKind.Remote when !string.IsNullOrWhiteSpace(target) => await GetRemoteSnapshotAsync(target, request, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Unsupported device \"{deviceKey}\"."),
        };
    }

    public async Task<bool> SignalProcessAsync(string deviceKey, int pid, ProcessSignalKind signal, CancellationToken cancellationToken = default)
    {
        var (kind, target) = DeviceKeys.Parse(deviceKey);
        return kind switch
        {
            ProjectLocationKind.Local => await local.SignalProcessAsync(pid, signal, cancellationToken).ConfigureAwait(false),
            ProjectLocationKind.Ssh when !string.IsNullOrWhiteSpace(target) => await ssh.SignalProcessAsync(target, pid, signal, cancellationToken).ConfigureAwait(false),
            ProjectLocationKind.Remote when !string.IsNullOrWhiteSpace(target) => await SignalRemoteProcessAsync(target, pid, signal, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Unsupported device \"{deviceKey}\"."),
        };
    }

    public async Task<ProcessResult> RunOnDeviceAsync(
        string deviceKey,
        string command,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        string? stdin,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var (kind, target) = DeviceKeys.Parse(deviceKey);
        return kind switch
        {
            ProjectLocationKind.Ssh when !string.IsNullOrWhiteSpace(target) =>
                await ssh.RunCommandAsync(target, command, arguments, workingDirectory, environment, stdin, timeout, cancellationToken).ConfigureAwait(false),
            ProjectLocationKind.Remote when !string.IsNullOrWhiteSpace(target) =>
                await RunRemoteProcessAsync(target, command, arguments, workingDirectory, environment, stdin, timeout, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Device \"{deviceKey}\" is not supported for remote process execution."),
        };
    }

    private async Task<RemoteHostClient> RequireRemoteClientAsync(string hostId)
    {
        if (remote is null) throw new NotSupportedException("Remote hosts are not available.");
        return remote.ClientOf(hostId) ?? await remote.ConnectAsync(hostId).ConfigureAwait(false);
    }

    private async Task<SystemMetricsSnapshot> GetRemoteSnapshotAsync(string hostId, SystemMetricsRequest request, CancellationToken ct)
    {
        var client = await RequireRemoteClientAsync(hostId).ConfigureAwait(false);
        if (!client.SupportsSystemMetrics)
        {
            throw new NotSupportedException("The remote Octopus host does not support system metrics yet. Please update Octopus on that device.");
        }
        var msg = await client.GetSystemMetricsAsync(new SystemMetricsRequestMessage
        {
            IncludeCpu = request.IncludeCpu,
            IncludeMemory = request.IncludeMemory,
            IncludeDisk = request.IncludeDisk,
            IncludeNetwork = request.IncludeNetwork,
            IncludeProcesses = request.IncludeProcesses,
            ProcessLimit = request.ProcessLimit,
            ProcessSort = request.ProcessSort,
            ProcessFilter = request.ProcessFilter,
        }, ct).ConfigureAwait(false);

        return new SystemMetricsSnapshot
        {
            DeviceKey = DeviceKeys.Remote(hostId),
            SampledAtMs = msg.SampledAtMs,
            DurationMs = msg.DurationMs,
            Host = new SystemHostInfo
            {
                Hostname = msg.Host.Hostname,
                Os = msg.Host.Os,
                OsVersion = msg.Host.OsVersion,
                Arch = msg.Host.Arch,
                Kernel = msg.Host.Kernel,
                UptimeSeconds = msg.Host.UptimeSeconds,
            },
            Cpu = msg.Cpu is null ? null : new CpuMetrics
            {
                UsagePercent = msg.Cpu.UsagePercent,
                UserPercent = msg.Cpu.UserPercent,
                SystemPercent = msg.Cpu.SystemPercent,
                IdlePercent = msg.Cpu.IdlePercent,
                Cores = msg.Cpu.Cores,
                Model = msg.Cpu.Model,
                LoadAvg1 = msg.Cpu.LoadAvg1,
                LoadAvg5 = msg.Cpu.LoadAvg5,
                LoadAvg15 = msg.Cpu.LoadAvg15,
                PerCore = msg.Cpu.PerCore,
            },
            Memory = msg.Memory is null ? null : new MemoryMetrics
            {
                TotalBytes = msg.Memory.TotalBytes,
                UsedBytes = msg.Memory.UsedBytes,
                AvailableBytes = msg.Memory.AvailableBytes,
                CachedBytes = msg.Memory.CachedBytes,
                UsedPercent = msg.Memory.UsedPercent,
                SwapTotalBytes = msg.Memory.SwapTotalBytes,
                SwapUsedBytes = msg.Memory.SwapUsedBytes,
                SwapPercent = msg.Memory.SwapPercent,
            },
            Disks = msg.Disks?.Select(d => new DiskMetrics
            {
                Mount = d.Mount,
                Filesystem = d.Filesystem,
                TotalBytes = d.TotalBytes,
                UsedBytes = d.UsedBytes,
                AvailableBytes = d.AvailableBytes,
                UsedPercent = d.UsedPercent,
                ReadBytesPerSec = d.ReadBytesPerSec,
                WriteBytesPerSec = d.WriteBytesPerSec,
            }).ToList(),
            Network = msg.Network?.Select(n => new NetworkInterfaceMetrics
            {
                Name = n.Name,
                RxBytesPerSec = n.RxBytesPerSec,
                TxBytesPerSec = n.TxBytesPerSec,
                RxTotalBytes = n.RxTotalBytes,
                TxTotalBytes = n.TxTotalBytes,
                Ipv4 = n.Ipv4,
            }).ToList(),
            Processes = msg.Processes?.Select(p => new ProcessMetrics
            {
                Pid = p.Pid,
                Ppid = p.Ppid,
                Name = p.Name,
                User = p.User,
                State = p.State,
                CpuPercent = p.CpuPercent,
                MemPercent = p.MemPercent,
                RssBytes = p.RssBytes,
                Threads = p.Threads,
                Command = p.Command,
            }).ToList(),
        };
    }

    private async Task<bool> SignalRemoteProcessAsync(string hostId, int pid, ProcessSignalKind signal, CancellationToken ct)
    {
        var client = await RequireRemoteClientAsync(hostId).ConfigureAwait(false);
        if (!client.SupportsSystemSignal)
        {
            throw new NotSupportedException("The remote Octopus host does not support process signaling yet. Please update Octopus on that device.");
        }
        var sigName = signal switch
        {
            ProcessSignalKind.Kill => "KILL",
            ProcessSignalKind.Int => "INT",
            ProcessSignalKind.Hup => "HUP",
            _ => "TERM",
        };
        return await client.SignalProcessAsync(pid, sigName, ct).ConfigureAwait(false);
    }

    private async Task<ProcessResult> RunRemoteProcessAsync(
        string hostId,
        string command,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        string? stdin,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var client = await RequireRemoteClientAsync(hostId).ConfigureAwait(false);
        if (!client.SupportsProcessRun)
        {
            throw new NotSupportedException("The remote Octopus host does not support remote process execution yet. Please update Octopus on that device.");
        }
        var res = await client.RunProcessAsync(new RemoteProcessRunMessage
        {
            Command = command,
            Args = [.. arguments],
            Cwd = workingDirectory,
            Env = environment is null ? null : new Dictionary<string, string>(environment, StringComparer.Ordinal),
            Stdin = stdin,
            TimeoutMs = (int)Math.Clamp(timeout.TotalMilliseconds, 100, 600_000),
        }, ct).ConfigureAwait(false);
        return new ProcessResult(res.ExitCode, res.StandardOutput, res.StandardError, res.TimedOut);
    }
}
