using System.Text.Json.Nodes;
using Octopus.Core.Plugins;
using Octopus.Core.SystemMetrics;
using Octopus.Platform;
using Octopus.Platform.SystemMetrics;
using Octopus.Plugins.Manifests;
using Octopus.Plugins.Runtime;
using Octopus.Plugins.Runtime.Modules;
using Xunit;

namespace Octopus.Plugins.Jaunt.Tests;

/// <summary>
/// Verifies the extensible plugin capabilities added for right-panel contributions, multi-device context,
/// cross-device system telemetry/signaling, and Tier 2 multi-device controlled process execution.
/// </summary>
public sealed class ServerMonitorExtensionsTests
{
    [Fact]
    public void Manifest_ParsesRichPanels_Devices_System_And_MultiDeviceProcess()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-manifest-test-");
        var pkgDir = Path.Combine(tempDir.FullName, "acme.monitor");
        Directory.CreateDirectory(pkgDir);
        try
        {
            File.WriteAllText(Path.Combine(pkgDir, "main.js"), "export function activate() {}");
            File.WriteAllText(Path.Combine(pkgDir, "plugin.json"), """
            {
              "schema": 1,
              "id": "acme.monitor",
              "name": "Monitor",
              "version": "1.0.0",
              "engines": { "api": 0 },
              "main": "main.js",
              "permissions": {
                "devices": "read",
                "system": {
                  "metrics": true,
                  "processSignal": { "optional": true }
                },
                "process": {
                  "run": ["docker", "uptime"],
                  "devices": ["local", "ssh", "remote:*"]
                }
              },
              "contributes": {
                "panels": [
                  {
                    "id": "acme.monitor.panel",
                    "title": "Monitor",
                    "icon": "lucide:activity",
                    "scope": "device",
                    "when": "any",
                    "order": 42,
                    "localized": { "zh-CN": { "title": "监控" } }
                  }
                ]
              }
            }
            """);

            var parsed = PluginManifestReader.ReadPackage(pkgDir, PluginSource.File, requireDirectoryName: true);
            Assert.True(parsed.IsValid, string.Join("\n", parsed.Issues));
            Assert.Empty(parsed.Issues);

            var manifest = parsed.Manifest!;
            var panel = Assert.Single(manifest.Contributes.Panels);
            Assert.Equal("acme.monitor.panel", panel.Id);
            Assert.Equal(PanelScope.Device, panel.Scope);
            Assert.Equal("any", panel.When);
            Assert.Equal(42, panel.Order);

            var reqs = manifest.Permissions.Requests;
            Assert.Contains(reqs, r => r.Key == "devices" && r.Risk == PermissionRisk.Low);
            Assert.Contains(reqs, r => r.Key == "system.metrics" && r.Risk == PermissionRisk.Medium && !r.Optional);
            Assert.Contains(reqs, r => r.Key == "system.process.signal" && r.Risk == PermissionRisk.High && r.Optional);
            Assert.Contains(reqs, r => r.Key == "process.run" && r.Risk == PermissionRisk.High && r.Details.SequenceEqual(["docker", "uptime"]));
            Assert.Contains(reqs, r => r.Key == "process.run.devices" && r.Risk == PermissionRisk.High && r.Details.SequenceEqual(["local", "ssh", "remote:*"]));
        }
        finally
        {
            Directory.Delete(tempDir.FullName, true);
        }
    }

    [Fact]
    public async Task LocalCollector_CollectsCpuMemoryDisksAndProcesses()
    {
        var collector = new LocalSystemMetricsCollector(new ProcessRunner());
        var snap = await collector.CollectAsync(new SystemMetricsRequest
        {
            IncludeCpu = true,
            IncludeMemory = true,
            IncludeDisk = true,
            IncludeNetwork = true,
            IncludeProcesses = true,
            ProcessLimit = 10,
            ProcessSort = "cpu",
        }, TestContext.Current.CancellationToken);

        Assert.Equal("local", snap.DeviceKey);
        Assert.NotEmpty(snap.Host.Hostname);
        Assert.NotNull(snap.Cpu);
        Assert.True(snap.Cpu!.Cores >= 1);
        Assert.NotNull(snap.Memory);
        Assert.True(snap.Memory!.TotalBytes > 0);
        Assert.NotNull(snap.Processes);
        Assert.NotEmpty(snap.Processes!);
    }

    [Fact]
    public async Task Runtime_DevicesSystemAndMultiDeviceProcess_WorkAndEnforcePermissions()
    {
        var fakeDevices = new FakeDeviceContext();
        var fakeSystem = new FakeSystemService();
        await using var harness = new Harness();
        harness.ExtraModules.Add(new DevicesModule(fakeDevices));
        harness.ExtraModules.Add(new SystemMetricsModule(fakeSystem, fakeDevices));
        harness.ExtraModules.Add(new ProcessModule(new FakeLocator(), new FakeLogin(), fakeSystem));

        harness.InstalledPlugin(
            "acme.ops",
            """
            import { commands, devices, panels, process, system, ui } from 'octopus';

            export function activate(ctx) {
              ctx.subscriptions.push(
                panels.register('acme.ops.panel', {
                  open() {
                    return { render: () => ui.column({}, [ui.text('Monitor Ready')]) };
                  },
                }),
                commands.register('acme.ops.probe', async () => {
                  const cur = devices.current;
                  const all = await devices.list();
                  const snap = await system.snapshot({ device: 'ssh:prod-1', include: ['cpu', 'memory', 'processes'] });
                  return { currentKey: cur.key, deviceCount: all.length, cpu: snap.cpu.usagePercent, hostname: snap.host.hostname };
                }),
                commands.register('acme.ops.signalDenied', async () => {
                  try {
                    await system.signalProcess(999, 'TERM', { device: 'ssh:prod-1' });
                    return { denied: false };
                  } catch (e) {
                    return { denied: e.code === 'PermissionDenied', permission: e.permission };
                  }
                }),
                commands.register('acme.ops.runSsh', async () => {
                  return await process.run('uptime', ['-p'], { device: 'ssh:prod-1' });
                }),
                commands.register('acme.ops.runBlockedDevice', async () => {
                  try {
                    await process.run('uptime', [], { device: 'ssh:other-box' });
                    return { denied: false };
                  } catch (e) {
                    return { denied: e.code === 'PermissionDenied' };
                  }
                }),
              );
            }
            """,
            """
            "activation": ["onStartup"],
            "permissions": {
              "devices": "read",
              "system": { "metrics": true, "processSignal": { "optional": true } },
              "process": { "run": ["uptime"], "devices": ["ssh:prod-1"] }
            },
            "contributes": {
              "panels": [{ "id": "acme.ops.panel", "title": "Ops", "icon": "lucide:activity", "scope": "device" }],
              "commands": [
                { "id": "acme.ops.probe", "title": "Probe" },
                { "id": "acme.ops.signalDenied", "title": "Signal Denied" },
                { "id": "acme.ops.runSsh", "title": "Run SSH" },
                { "id": "acme.ops.runBlockedDevice", "title": "Run Blocked" }
              ]
            }
            """,
            new Dictionary<string, string>
            {
                ["devices"] = "read",
                ["system.metrics"] = "granted",
                ["process.run"] = "uptime",
                ["process.run.devices"] = "ssh:prod-1",
            });

        var manager = await harness.StartAsync();

        var ct = TestContext.Current.CancellationToken;
        var probe = (await manager.ExecuteCommandAsync("acme.ops.probe", [], ct))!.AsObject();
        Assert.Equal("local", probe["currentKey"]!.GetValue<string>());
        Assert.Equal(2, probe["deviceCount"]!.GetValue<int>());
        Assert.Equal(42.5, probe["cpu"]!.GetValue<double>());
        Assert.Equal("prod-1", probe["hostname"]!.GetValue<string>());

        var deniedSig = (await manager.ExecuteCommandAsync("acme.ops.signalDenied", [], ct))!.AsObject();
        Assert.True(deniedSig["denied"]!.GetValue<bool>());
        Assert.Equal("system.process.signal", deniedSig["permission"]!.GetValue<string>());

        var runSsh = (await manager.ExecuteCommandAsync("acme.ops.runSsh", [], ct))!.AsObject();
        Assert.Equal(0, runSsh["exitCode"]!.GetValue<int>());
        Assert.Equal("ssh:prod-1:uptime:-p", runSsh["stdout"]!.GetValue<string>());

        var blockedDev = (await manager.ExecuteCommandAsync("acme.ops.runBlockedDevice", [], ct))!.AsObject();
        Assert.True(blockedDev["denied"]!.GetValue<bool>());

        // Verify panel view opens and renders a valid UI tree
        var host = manager.Find("acme.ops")!;
        Assert.Contains(("panel", "acme.ops.panel"), host.Providers);
        var openJson = await host.InvokeAsync("view.open", new JsonArray("panel", "acme.ops.panel", new JsonObject { ["instanceId"] = "panel:acme.ops.panel", ["visible"] = true }), TimeSpan.FromSeconds(5), ct);
        var openObj = JsonNode.Parse(openJson)!.AsObject();
        Assert.Equal("Column", openObj["tree"]!["t"]!.GetValue<string>());
    }

    private sealed class FakeDeviceContext : IPluginDeviceContext
    {
        public PluginDeviceInfo Current { get; } = new("local", "local", "MyMac", null, "local", ["system.metrics", "system.signal", "process.run"]);
        public IReadOnlyList<PluginDeviceInfo> All =>
        [
            Current,
            new("ssh:prod-1", "ssh", "prod-1", "prod-1", "unknown", ["system.metrics", "system.signal", "process.run"]),
        ];
        public event EventHandler? CurrentChanged { add { } remove { } }
        public event EventHandler? ListChanged { add { } remove { } }
    }

    private sealed class FakeSystemService : IPluginSystemService
    {
        public Task<SystemMetricsSnapshot> GetSnapshotAsync(string deviceKey, SystemMetricsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemMetricsSnapshot
            {
                DeviceKey = deviceKey,
                SampledAtMs = 1795611893496,
                DurationMs = 12,
                Host = new SystemHostInfo { Hostname = "prod-1", Os = "linux", Arch = "x86_64", UptimeSeconds = 3600 },
                Cpu = new CpuMetrics { UsagePercent = 42.5, Cores = 8 },
                Memory = new MemoryMetrics { TotalBytes = 16_000_000_000, UsedBytes = 8_000_000_000, AvailableBytes = 8_000_000_000, UsedPercent = 50.0 },
                Processes = [new ProcessMetrics { Pid = 101, Name = "nginx", CpuPercent = 12.0, RssBytes = 50_000_000 }],
            });

        public Task<bool> SignalProcessAsync(string deviceKey, int pid, ProcessSignalKind signal, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<ProcessResult> RunOnDeviceAsync(string deviceKey, string command, IReadOnlyList<string> arguments, string? workingDirectory, IReadOnlyDictionary<string, string>? environment, string? stdin, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(0, $"{deviceKey}:{command}:{string.Join(",", arguments)}", "", false));
    }

    private sealed class FakeLocator : IExecutableLocator
    {
        public IReadOnlyList<string> FindAll(string name, string? pathVariable) => [];
    }

    private sealed class FakeLogin : ILoginEnvironment
    {
        public Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }
}
