using Xunit;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Octopus.Core.Plugins;
using Octopus.Plugins.Manifests;
using Octopus.Plugins.Runtime;

namespace Octopus.Plugins.Jaunt.Tests;

/// <summary>
/// The built-in agent usage plugin (src/Octopus.App/BuiltinPlugins/octopus.agent-usage), run as shipped on a real
/// engine against fake agent data: its manifest is valid, it turns the official payloads into usage snapshots, and
/// its indicator, popover and history tab render.
/// </summary>
public sealed class AgentUsagePluginTests
{
    private const string Id = "octopus.agent-usage";

    private static string PackageSource([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Octopus.App", "BuiltinPlugins", Id));

    [Fact]
    public void Manifest_IsValid_WithoutWarnings()
    {
        var result = PluginManifestReader.ReadPackage(PackageSource(), PluginSource.Builtin, requireDirectoryName: true);
        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Empty(result.Issues);
        var manifest = result.Manifest!;
        Assert.Equal(5, manifest.Contributes.UsageProviders.Count);
        // Official sources only: no network, no process, no credential files (design 12 §9 is undecided).
        Assert.DoesNotContain(manifest.Permissions.Requests, r => r.Key is "net" or "process.run" or "fs.external");
    }

    private static async Task<(Harness Harness, PluginManager Manager, FakeAgents Agents)> StartAsync(Action<Harness, FakeAgents>? arrange = null)
    {
        var harness = new Harness();
        CopyDirectory(PackageSource(), Path.Combine(harness.BuiltinRoot, Id));
        var agents = new FakeAgents();
        harness.ExtraModules.Add(agents);
        arrange?.Invoke(harness, agents);
        var manager = await harness.StartAsync();
        await manager.FireAsync("onAgentInstalled:codex");
        await Harness.WaitUntil(() => manager.Find(Id)?.State is PluginHostState.Running or PluginHostState.Failed);
        Assert.True(manager.Find(Id)!.State == PluginHostState.Running, manager.Find(Id)!.LastError + "\n" + string.Join("\n", manager.Find(Id)!.Log.Select(l => l.Text)));
        return (harness, manager, agents);
    }

    [Fact]
    public async Task Codex_ProbeResult_BecomesSnapshotAndIndicator()
    {
        var (harness, _, agents) = await StartAsync();
        await using var __ = harness;
        // The probe runs about 3 s after activation.
        await Harness.WaitUntil(() => harness.Usage.Get("codex") is not null, 10_000);
        var snapshot = harness.Usage.Get("codex")!.Snapshot;
        var windows = snapshot["windows"]!.AsArray();
        Assert.Equal(2, windows.Count);
        Assert.Equal(43, windows[0]!["usedPercent"]!.GetValue<int>());
        // The 30-day window of this machine keeps its real length (design 12 §4.2).
        Assert.Contains(windows, w => w!["id"]!.GetValue<string>() == "m43200");
        Assert.Equal("plus", snapshot["plan"]!.GetValue<string>());
        await Harness.WaitUntil(() => agents.Indicator?["text"]?.GetValue<string>() == "61%");
        Assert.Equal("default", agents.Indicator!["level"]!.GetValue<string>());
    }

    [Fact]
    public async Task Claude_StatusLine_BecomesSnapshot_AndPopoverRendersEveryAgent()
    {
        var (harness, manager, agents) = await StartAsync();
        await using var _ = harness;
        var resets = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        agents.Signal("claude", "statusline", new JsonObject
        {
            ["rate_limits"] = new JsonObject
            {
                ["five_hour"] = new JsonObject { ["used_percentage"] = 92, ["resets_at"] = resets },
                ["seven_day"] = new JsonObject { ["used_percentage"] = 23, ["resets_at"] = resets + 86400 },
            },
            ["cost"] = new JsonObject { ["total_cost_usd"] = 1.84 },
        }, account: "a1b2c3d4e5f6");
        await Harness.WaitUntil(() => harness.Usage.Get("claude", "a1b2c3d4e5f6") is not null);
        var claude = harness.Usage.Get("claude", "a1b2c3d4e5f6")!.Snapshot;
        Assert.Equal(92, claude["windows"]![0]!["usedPercent"]!.GetValue<int>());
        Assert.Equal(1.84, claude["session"]!["costUsd"]!.GetValue<double>());
        // The plan comes from `claude auth status`.
        await Harness.WaitUntil(() => harness.Usage.Get("claude", "a1b2c3d4e5f6")?.Snapshot["plan"]?.GetValue<string>() == "Max 20x", 10_000);
        // 92% is the tightest window anywhere: the indicator shows it in the danger color.
        await Harness.WaitUntil(() => agents.Indicator?["text"]?.GetValue<string>() == "92%");
        Assert.Equal("danger", agents.Indicator!["level"]!.GetValue<string>());

        var json = await manager.Find(Id)!.InvokeAsync("indicator.click:octopus.agent-usage.indicator", [], TimeSpan.FromSeconds(5));
        var tree = JsonNode.Parse(json)!["tree"]!.ToJsonString();
        Assert.Contains("Claude", tree);
        Assert.Contains("Cursor", tree);
        // Grok and Cursor say why there is no quota instead of showing 0%.
        Assert.Contains("/usage", tree);
        Assert.Contains("\"Progress\"", tree);
    }

    [Fact]
    public async Task Claude_CustomCommand_ReplacesTheStatusLine_AsTheSource()
    {
        if (OperatingSystem.IsWindows()) return; // the command runs through PosixShell and uses printf
        await using var harness = new Harness { Commands = UserCommandTests.Runner() };
        CopyDirectory(PackageSource(), Path.Combine(harness.BuiltinRoot, Id));
        var manifest = PluginManifestReader.ReadPackage(Path.Combine(harness.BuiltinRoot, Id), PluginSource.Builtin, requireDirectoryName: true).Manifest!;
        harness.Config.Set(manifest, "octopus.agent-usage.claude.source", JsonValue.Create("command"), byUser: true);
        // What a user's script might print: Octopus's own shape, with the reset time in Unix seconds.
        var resets = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();
        harness.Config.Set(manifest, "octopus.agent-usage.claude.command",
            JsonValue.Create($$"""printf '%s' '{"plan":"Max","windows":[{"label":"5h","usedPercent":71.5,"resetsAt":{{resets}}},{"label":"week","usedPercent":12}]}'"""), byUser: true);
        var agents = new FakeAgents();
        harness.ExtraModules.Add(agents);
        var manager = await harness.StartAsync();
        await manager.FireAsync("onAgentInstalled:claude");

        await Harness.WaitUntil(() => harness.Usage.Get("claude") is not null, 10_000);
        var snapshot = harness.Usage.Get("claude")!.Snapshot;
        Assert.Equal("custom", snapshot["source"]!.GetValue<string>());
        Assert.Equal("Max", snapshot["plan"]!.GetValue<string>());
        Assert.Equal(71.5, snapshot["windows"]![0]!["usedPercent"]!.GetValue<double>());
        Assert.Equal(resets * 1000, snapshot["windows"]![0]!["resetsAt"]!.GetValue<long>());
        // In command mode a status line no longer decides the windows.
        agents.Signal("claude", "statusline", new JsonObject { ["rate_limits"] = new JsonObject { ["five_hour"] = new JsonObject { ["used_percentage"] = 5 } } });
        await Task.Delay(200);
        Assert.Equal(71.5, harness.Usage.Get("claude")!.Snapshot["windows"]![0]!["usedPercent"]!.GetValue<double>());
    }

    private static JsonObject StatusLine(int fiveHour, long resets) => new()
    {
        ["rate_limits"] = new JsonObject { ["five_hour"] = new JsonObject { ["used_percentage"] = fiveHour, ["resets_at"] = resets } },
    };

    [Fact]
    public async Task ThresholdNotes_AreOncePerCycle_AndKeepAPauseBetweenThem()
    {
        var (harness, _, agents) = await StartAsync();
        await using var _ = harness;
        int Toasts() => harness.Captured.Notifications.Count(n => n.Method == "ui.toast");
        var resets = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();

        agents.Signal("claude", "statusline", StatusLine(50, resets), account: "a1b2c3d4e5f6");   // baseline
        await Harness.WaitUntil(() => harness.Usage.Get("claude", "a1b2c3d4e5f6") is not null);
        agents.Signal("claude", "statusline", StatusLine(82, resets), account: "a1b2c3d4e5f6");   // crosses 80
        await Harness.WaitUntil(() => Toasts() == 1);

        // Dipping under the threshold and climbing back says nothing new, and neither does the same reading again.
        agents.Signal("claude", "statusline", StatusLine(70, resets), account: "a1b2c3d4e5f6");
        agents.Signal("claude", "statusline", StatusLine(83, resets), account: "a1b2c3d4e5f6");
        agents.Signal("claude", "statusline", StatusLine(83, resets), account: "a1b2c3d4e5f6");
        // 95 is a new level, but the last note on this window was moments ago.
        agents.Signal("claude", "statusline", StatusLine(96, resets), account: "a1b2c3d4e5f6");
        await Task.Delay(500);
        Assert.Equal(1, Toasts());

        // Without the pause the new level is announced, once.
        var manifest = PluginManifestReader.ReadPackage(Path.Combine(harness.BuiltinRoot, Id), PluginSource.Builtin, requireDirectoryName: true).Manifest!;
        harness.Config.Set(manifest, "octopus.agent-usage.notifyInterval", JsonValue.Create(0.0), byUser: true);
        await Task.Delay(300);
        agents.Signal("claude", "statusline", StatusLine(96, resets), account: "a1b2c3d4e5f6");
        await Harness.WaitUntil(() => Toasts() == 2);
        agents.Signal("claude", "statusline", StatusLine(97, resets), account: "a1b2c3d4e5f6");
        await Task.Delay(500);
        Assert.Equal(2, Toasts());
    }

    /// <summary>A snapshot as the plugin reported it last run: one weekly window.</summary>
    private static JsonObject Cached(string? account, double percent, string? plan = null, bool withWindow = true) => new()
    {
        ["account"] = account,
        ["plan"] = plan,
        ["source"] = "official",
        ["windows"] = withWindow
            ? new JsonArray((JsonNode)new JsonObject { ["id"] = "m10080", ["label"] = "Weekly", ["usedPercent"] = percent, ["resetsAt"] = DateTimeOffset.UtcNow.AddHours(5).ToUnixTimeMilliseconds() })
            : new JsonArray(),
    };

    [Fact]
    public async Task CachedReadings_ShowAtOnce_AndFreshOnesAreNotAskedAgain()
    {
        var (harness, manager, agents) = await StartAsync((h, a) =>
        {
            a.Accounts["codex"] = [("c0dex0000001", "work/codex")];
            a.Accounts["claude"] = [("a1b2c3d4e5f6", "55232/config")];
            // Minutes old at most: the host's cache from the last run (usage-cache.json).
            h.Usage.Update("codex", Id, Cached("c0dex0000001", 77, "plus"));
            h.Usage.Update("claude", Id, Cached("a1b2c3d4e5f6", 0, "Max 20x", withWindow: false));
            h.Usage.Update("cursor", Id, new JsonObject { ["plan"] = "pro", ["windows"] = new JsonArray(), ["source"] = "official" });
        });
        await using var _ = harness;

        // Shown before any probe could have run.
        await Harness.WaitUntil(() => agents.Indicator?["text"]?.GetValue<string>() == "77%");
        Assert.Empty(agents.Probes);

        // The start-up queries (Codex at 3 s, Claude's plan at 4 s, Cursor at 5 s) find fresh readings and stay quiet.
        await Task.Delay(6_000);
        Assert.Empty(agents.Probes);

        // The refresh button asks anyway.
        await manager.ExecuteCommandAsync("octopus.agent-usage.refresh");
        await Harness.WaitUntil(() => agents.Probes.Contains("codex:c0dex0000001") && agents.Probes.Contains("cursor:") && agents.Probes.Contains("claude:a1b2c3d4e5f6"));
    }

    [Fact]
    public async Task AStaleCachedReading_ShowsFirst_ThenIsAskedAgain()
    {
        var (harness, _, agents) = await StartAsync((h, _) =>
        {
            // Written the way the host does, an hour old, before the registry is first read.
            Directory.CreateDirectory(h.DataDirectory);
            var old = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
            File.WriteAllText(Path.Combine(h.DataDirectory, "usage-cache.json"),
                new JsonArray((JsonNode)new JsonObject { ["agent"] = "codex", ["account"] = "", ["provider"] = Id, ["updatedAt"] = old, ["snapshot"] = Cached(null, 10) }).ToJsonString());
        });
        await using var _ = harness;

        await Harness.WaitUntil(() => agents.Indicator?["text"]?.GetValue<string>() == "10%");
        await Harness.WaitUntil(() => agents.Indicator?["text"]?.GetValue<string>() == "61%", 10_000);
        Assert.Contains("codex:", agents.Probes);
    }

    [Fact]
    public async Task EveryAccountTheHostFinds_IsQueried()
    {
        var (harness, _, agents) = await StartAsync((_, a) =>
        {
            a.Accounts["codex"] = [("c0dex0000001", "work/codex"), ("c0dex0000002", "home/codex")];
        });
        await using var _ = harness;

        await Harness.WaitUntil(() => harness.Usage.Get("codex", "c0dex0000001") is not null && harness.Usage.Get("codex", "c0dex0000002") is not null, 10_000);
        Assert.Contains("codex:c0dex0000001", agents.Probes);
        Assert.Contains("codex:c0dex0000002", agents.Probes);
    }

    [Fact]
    public async Task ClaudeAccounts_ThatHaveNotReportedYet_AreListedWithWhatToDo()
    {
        var (harness, manager, _) = await StartAsync((h, a) =>
        {
            a.Accounts["claude"] = [("a1b2c3d4e5f6", "55232/config"), ("b1b2c3d4e5f6", "47196/config")];
            h.Usage.Update("claude", Id, Cached("a1b2c3d4e5f6", 41, "Max 20x"));
        });
        await using var _ = harness;

        var json = await manager.Find(Id)!.InvokeAsync("indicator.click:octopus.agent-usage.indicator", [], TimeSpan.FromSeconds(5));
        // Not escaped: the hint is Chinese or English, whichever the test machine's language is.
        var tree = JsonNode.Parse(json)!["tree"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.Contains("55232/config", tree);
        Assert.Contains("47196/config", tree);
        Assert.True(tree.Contains("No usage from this configuration") || tree.Contains("还没有这个配置的用量"), tree);
    }

    [Fact]
    public async Task HistoryView_OpensAndRenders_WithoutSessionRecords()
    {
        var (harness, manager, _) = await StartAsync();
        await using var __ = harness;
        var context = new JsonObject { ["instanceId"] = "tab:1", ["visible"] = true };
        var json = await manager.Find(Id)!.InvokeAsync("view.open", ["view", "octopus.agent-usage.history", context], TimeSpan.FromSeconds(5));
        var tree = JsonNode.Parse(json)!["tree"]!.ToJsonString();
        Assert.Contains("\"Segmented\"", tree);
        await manager.Find(Id)!.InvokeAsync("view.close", ["tab:1"], TimeSpan.FromSeconds(5));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>The app's agent, workspace and indicator modules, answering with fixed data.</summary>
    private sealed class FakeAgents : IPluginApiModule
    {
        private readonly ConcurrentDictionary<string, (PluginHost Plugin, PluginSubscription Subscription)> _signals = new();

        public JsonObject? Indicator { get; private set; }

        /// <summary>Every probe the plugin asked for: "agent:account" (the account empty for the default).</summary>
        public ConcurrentQueue<string> Probes { get; } = new();

        /// <summary>The accounts the host "finds" for an agent: (key, label).</summary>
        public ConcurrentDictionary<string, (string Key, string Label)[]> Accounts { get; } = new();

        private JsonArray AccountsOf(string agent)
        {
            var list = new JsonArray();
            foreach (var (key, label) in Accounts.GetValueOrDefault(agent) ?? [])
            {
                list.Add((JsonNode)new JsonObject { ["key"] = key, ["label"] = label });
            }
            return list;
        }

        public void Register(PluginApiRouter router)
        {
            router.Request("agents.list", (_, _) =>
            {
                var list = new JsonArray();
                foreach (var (id, name) in new[] { ("claude", "Claude"), ("codex", "Codex"), ("grok", "Grok"), ("cursor", "Cursor"), ("opencode", "OpenCode") })
                {
                    list.Add((JsonNode)new JsonObject { ["id"] = id, ["name"] = name, ["installed"] = true });
                }
                return Task.FromResult(PluginReply.Of(list));
            });
            router.Request("agents.runProbe", (call, _) =>
            {
                Probes.Enqueue($"{call.String("agentId")}:{call.String("account")}");
                return Task.FromResult(PluginReply.Of(call.String("agentId") switch
            {
                // As this machine's codex app-server answered in P0 (design 13 §3), with a weekly window added.
                "codex" => new JsonObject
                {
                    ["ok"] = true, ["account"] = call.String("account") ?? "", ["completedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["data"] = JsonNode.Parse("""
                        { "rateLimits": { "planType": "plus",
                          "primary": { "usedPercent": 43, "windowDurationMins": 43200, "resetsAt": 4102444800 },
                          "secondary": { "usedPercent": 61, "windowDurationMins": 10080, "resetsAt": 4102444800 } } }
                        """),
                },
                "cursor" => new JsonObject { ["ok"] = true, ["account"] = "", ["data"] = new JsonObject { ["subscriptionTier"] = "pro" } },
                // claude auth status --json, as this machine answered (the e-mail is not selected by the probe).
                "claude" => new JsonObject { ["ok"] = true, ["account"] = call.String("account") ?? "", ["data"] = JsonNode.Parse("""{ "loggedIn": true, "authMethod": "claude.ai", "apiProvider": "firstParty", "subscriptionType": "max", "planTier": "max_20x" }""") },
                _ => new JsonObject { ["ok"] = false, ["error"] = "NotDeclared" },
                }));
            });
            router.Sync("agents.accounts", call => AccountsOf(call.RequiredString("agentId")).ToJsonString());
            router.Request("agents.discoverAccounts", (call, _) => Task.FromResult(PluginReply.Of(AccountsOf(call.RequiredString("agentId")))));
            router.Sync("agents.lastSignal", _ => "null");
            router.Sync("workspace.activeItem", _ => "null");
            router.Sync("app.state", _ => """{ "focused": true, "visible": true, "idleSeconds": 0 }""");
            router.Sync("fs.scopes", _ => "[]");
            router.Notify("indicators.create", _ => { });
            router.Notify("indicators.update", call => Indicator = call["state"]?.DeepClone() as JsonObject);
            router.Notify("indicators.remove", _ => { });
        }

        public void Subscribed(PluginHost plugin, PluginSubscription subscription)
        {
            if (subscription.Event != "agents.signal") return;
            var key = $"{subscription.Args?["agentId"]}:{subscription.Args?["channel"]}";
            _signals[key] = (plugin, subscription);
        }

        public void Signal(string agent, string channel, JsonObject payload, string account = "")
        {
            if (!_signals.TryGetValue($"{agent}:{channel}", out var target)) throw new InvalidOperationException($"The plugin does not listen to {agent}/{channel}.");
            target.Plugin.EmitTo(target.Subscription.Key, new JsonObject
            {
                ["agentId"] = agent, ["channel"] = channel, ["account"] = account,
                ["receivedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["payload"] = payload,
            });
        }
    }
}
