using Xunit;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Octopus.Agents;
using Octopus.Core.Plugins;
using Octopus.FileSystem;
using Octopus.Platform;
using Octopus.Plugins.Catalog;
using Octopus.Plugins.Manifests;
using Octopus.Plugins.Runtime;
using Octopus.Plugins.Runtime.Modules;
using Octopus.Plugins.Security;

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
        Assert.Equal(7, manifest.Contributes.UsageProviders.Count);
        Assert.Contains(manifest.Contributes.UsageProviders, p => p.Agent == "agy");
        Assert.Contains(manifest.Contributes.UsageProviders, p => p.Agent == "copilot");
        Assert.Contains(manifest.Permissions.Requests, r => r.Key == "agents.probes.agy.usage");
        Assert.Contains(manifest.Permissions.Requests, r => r.Key == "agents.probes.copilot.quota");
        Assert.Contains(manifest.Permissions.FsAgentData, d => d.Agent == "agy" && d.Include.Contains("brain/*/.system_generated/logs/transcript.jsonl"));
        Assert.Contains(manifest.Permissions.FsAgentData, d => d.Agent == "copilot" && d.Include.Contains("session-state/**/events.jsonl"));
        Assert.Contains(manifest.Permissions.FsAgentData, d => d.Agent == "grok" && d.Include.Contains("sessions/**/usage.json"));
        // No process and no credential files of other tools (design 12 §9 is undecided). The network is optional and
        // only reaches the API hosts of the providers the user gives a key for (design 12 §9.3, the "paste a key" path).
        Assert.DoesNotContain(manifest.Permissions.Requests, r => r.Key is "process.run" or "fs.external");
        var net = Assert.Single(manifest.Permissions.Requests, r => r.Key == "net");
        Assert.True(net.Optional);
        Assert.Equal(
            ["api.deepseek.com", "api.moonshot.cn", "api.moonshot.ai", "openrouter.ai", "api.siliconflow.cn", "api.siliconflow.com", "open.bigmodel.cn", "api.z.ai"],
            net.Details);
        Assert.Contains(manifest.Permissions.Requests, r => r.Key == "secrets" && r.Optional);
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
    public async Task Claude_StatusLine_BecomesSnapshot_AndPopoverHasATabPerAgent()
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
        var tree = JsonNode.Parse(json)!["tree"]!;
        var tabs = Find(tree, "Tabs")!;
        var titles = tabs["p"]!["tabs"]!.AsArray().Select(tab => tab!["title"]!.GetValue<string>()).ToList();
        // One tab per installed agent, the tightest window in its title; Claude (92%) is open.
        Assert.Contains("Claude 92%", titles);
        Assert.Contains("Codex", titles[1]);
        Assert.Contains(titles, title => title.StartsWith("Antigravity"));
        Assert.Contains(titles, title => title.StartsWith("Copilot"));
        Assert.Contains(titles, title => title.StartsWith("Cursor"));
        Assert.Contains(titles, title => title.StartsWith("Grok"));
        Assert.Equal("claude", tabs["p"]!["selected"]!.GetValue<string>());
        var text = tree.ToJsonString();
        Assert.Contains("\"Progress\"", text);
        // Only usage: the session cost is not shown.
        Assert.DoesNotContain("1.84", text);
    }

    [Fact]
    public async Task SwitchingTabs_ShowsThatAgent_AndTheApiTabOffersToAddAKey()
    {
        var (harness, manager, _) = await StartAsync();
        await using var _ = harness;
        var plugin = manager.Find(Id)!;
        var clicked = JsonNode.Parse(await plugin.InvokeAsync("indicator.click:octopus.agent-usage.indicator", [], TimeSpan.FromSeconds(5)))!;
        var instance = clicked["instanceId"]!.GetValue<string>();
        var onSelect = Find(clicked["tree"], "Tabs")!["p"]!["onSelect"]!["$h"]!.GetValue<string>();

        async Task<string> Select(string tab)
        {
            await plugin.InvokeAsync("view.event", [instance, onSelect, new JsonArray(tab)], TimeSpan.FromSeconds(5));
            var tree = JsonNode.Parse(await plugin.InvokeAsync("view.render", [instance], TimeSpan.FromSeconds(5)))!;
            Assert.Equal(tab, Find(tree, "Tabs")!["p"]!["selected"]!.GetValue<string>());
            return tree.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }

        // Grok says why there is no quota instead of showing 0%.
        Assert.Contains("/usage", await Select("grok"));
        var api = await Select("api");
        Assert.True(api.Contains("Add an API key") || api.Contains("添加 API 密钥"), api);
    }

    [Fact]
    public async Task ThePopover_IsCompactByDefault_AndComfortableWhenChosen()
    {
        var (harness, manager, agents) = await StartAsync();
        await using var _ = harness;
        var resets = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        agents.Signal("claude", "statusline", StatusLine(40, resets), account: "a1b2c3d4e5f6");
        await Harness.WaitUntil(() => harness.Usage.Get("claude", "a1b2c3d4e5f6") is not null);
        await Harness.WaitUntil(() => harness.Usage.Get("claude", "a1b2c3d4e5f6")?.Snapshot["plan"] is not null, 10_000);

        async Task<JsonNode> Popover() =>
            JsonNode.Parse(await manager.Find(Id)!.InvokeAsync("indicator.click:octopus.agent-usage.indicator", [], TimeSpan.FromSeconds(5)))!["tree"]!;

        // Compact: short window labels ("5h"), the plan as plain text, no footnote line.
        var compact = await Popover();
        var compactText = compact.ToJsonString();
        Assert.Contains("\"value\":\"5h\"", compactText);
        Assert.Null(Find(compact, "Badge"));
        Assert.Equal(400, compact["p"]!["width"]!.GetValue<int>());

        var manifest = PluginManifestReader.ReadPackage(Path.Combine(harness.BuiltinRoot, Id), PluginSource.Builtin, requireDirectoryName: true).Manifest!;
        harness.Config.Set(manifest, "octopus.agent-usage.style", JsonValue.Create("comfortable"), byUser: true);
        await Task.Delay(200);
        var comfortable = await Popover();
        Assert.NotNull(Find(comfortable, "Badge"));
        Assert.DoesNotContain("\"value\":\"5h\"", comfortable.ToJsonString());
        Assert.Equal(440, comfortable["p"]!["width"]!.GetValue<int>());
    }

    /// <summary>The first node of a rendered tree with the type <paramref name="type"/>, depth first.</summary>
    private static JsonNode? Find(JsonNode? node, string type)
    {
        if (node is not JsonObject obj) return null;
        if (obj["t"]?.GetValue<string>() == type) return obj;
        foreach (var child in obj["c"]?.AsArray() ?? [])
        {
            if (Find(child, type) is { } found) return found;
        }
        return null;
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

    [Fact]
    public async Task AnIdleSessionsOlderReading_DoesNotLowerTheWindow_UntilANewCycle()
    {
        var (harness, _, agents) = await StartAsync();
        await using var _ = harness;
        double FiveHour() => harness.Usage.Get("claude", "a1b2c3d4e5f6")!.Snapshot["windows"]![0]!["usedPercent"]!.GetValue<double>();
        var resets = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();

        // Two sessions of one account: the busy one says 80, the idle one repeats the 79 of its last reply.
        agents.Signal("claude", "statusline", StatusLine(80, resets), account: "a1b2c3d4e5f6");
        await Harness.WaitUntil(() => harness.Usage.Get("claude", "a1b2c3d4e5f6") is not null);
        agents.Signal("claude", "statusline", StatusLine(79, resets + 30), account: "a1b2c3d4e5f6");
        await Task.Delay(300);
        Assert.Equal(80, FiveHour());
        await Harness.WaitUntil(() => agents.Indicator?["text"]?.GetValue<string>() == "80%");

        // A later reset time is the next cycle: its low reading is the truth.
        agents.Signal("claude", "statusline", StatusLine(3, resets + 5 * 3600), account: "a1b2c3d4e5f6");
        await Harness.WaitUntil(() => FiveHour() == 3);
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
            a.Accounts["copilot"] = [("c0p110700001", "work/copilot")];
            // Minutes old at most: the host's cache from the last run (usage-cache.json).
            h.Usage.Update("codex", Id, Cached("c0dex0000001", 77, "plus"));
            h.Usage.Update("claude", Id, Cached("a1b2c3d4e5f6", 0, "Max 20x", withWindow: false));
            h.Usage.Update("agy", Id, Cached(null, 24));
            h.Usage.Update("copilot", Id, Cached("c0p110700001", 20));
            h.Usage.Update("cursor", Id, new JsonObject { ["plan"] = "pro", ["windows"] = new JsonArray(), ["source"] = "official" });
        });
        await using var _ = harness;

        // Shown before any probe could have run.
        await Harness.WaitUntil(() => agents.Indicator?["text"]?.GetValue<string>() == "77%");
        Assert.Empty(agents.Probes);

        // The start-up queries (Codex at 3 s, Agy at 3.5 s, Claude's plan at 4 s, Copilot at 4.5 s, Cursor at 5 s) find fresh readings and stay quiet.
        await Task.Delay(6_000);
        Assert.Empty(agents.Probes);

        // The refresh button asks anyway.
        await manager.ExecuteCommandAsync("octopus.agent-usage.refresh");
        await Harness.WaitUntil(() =>
            agents.Probes.Contains("codex:c0dex0000001")
            && agents.Probes.Contains("agy:")
            && agents.Probes.Contains("copilot:c0p110700001")
            && agents.Probes.Contains("cursor:")
            && agents.Probes.Contains("claude:a1b2c3d4e5f6"));
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
    public async Task ClaudeAccounts_ThatHaveNotReportedYet_AreNamedOnOneLine()
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
        Assert.True(tree.Contains("No data yet: 47196/config") || tree.Contains("还没有数据：47196/config"), tree);
    }

    [Fact]
    public async Task Agy_And_Copilot_ProbeResults_BecomeQuotaSnapshots()
    {
        var (harness, _, _) = await StartAsync((_, a) =>
        {
            a.Accounts["copilot"] = [("c0p110700001", "work/copilot"), ("c0p110700002", "unlimited/copilot")];
        });
        await using var __ = harness;

        await Harness.WaitUntil(() =>
            harness.Usage.Get("agy") is not null
            && harness.Usage.Get("copilot", "c0p110700001") is not null
            && harness.Usage.Get("copilot", "c0p110700002") is not null, 10_000);

        // Antigravity CLI: four buckets across two groups, 5h sorted before weekly within each group.
        var agy = harness.Usage.Get("agy")!.Snapshot;
        var agyWindows = agy["windows"]!.AsArray();
        Assert.Equal(4, agyWindows.Count);
        Assert.Equal("gemini-5h", agyWindows[0]!["id"]!.GetValue<string>());
        Assert.Equal(24, Math.Round(agyWindows[0]!["usedPercent"]!.GetValue<double>()));
        Assert.Equal("Gemini", agyWindows[0]!["tag"]!.GetValue<string>());
        Assert.Equal("gemini-weekly", agyWindows[1]!["id"]!.GetValue<string>());
        Assert.Equal(8, Math.Round(agyWindows[1]!["usedPercent"]!.GetValue<double>()));
        Assert.Equal("3p-5h", agyWindows[2]!["id"]!.GetValue<string>());
        Assert.Equal("Claude/GPT", agyWindows[2]!["tag"]!.GetValue<string>());
        Assert.Equal(0, Math.Round(agyWindows[2]!["usedPercent"]!.GetValue<double>()));

        // Copilot CLI: premium_interactions remainingPercentage=80 -> 20% used; unlimited entitlement -> credits.unlimited.
        var copilot = harness.Usage.Get("copilot", "c0p110700001")!.Snapshot;
        var copilotWindows = copilot["windows"]!.AsArray();
        Assert.Single(copilotWindows);
        Assert.Equal("premium_interactions", copilotWindows[0]!["id"]!.GetValue<string>());
        Assert.Equal(20, Math.Round(copilotWindows[0]!["usedPercent"]!.GetValue<double>()));

        var unlimited = harness.Usage.Get("copilot", "c0p110700002")!.Snapshot;
        Assert.Empty(unlimited["windows"]!.AsArray());
        Assert.True(unlimited["credits"]!["unlimited"]!.GetValue<bool>());
    }

    [Fact]
    public async Task HistoryView_Scans_Agy_Copilot_And_Grok_SessionRecords_EndToEnd()
    {
        var home = Directory.CreateTempSubdirectory("octopus-usage-home-").FullName;
        try
        {
            var today = DateTimeOffset.UtcNow;
            var iso = today.ToString("O");

            // 1. Antigravity CLI: PLANNER_RESPONSE + CHECKPOINT (inherits model, non-cached input separate from cache_read).
            var agyLog = Path.Combine(home, ".gemini", "antigravity-cli", "brain", "conv-1", ".system_generated", "logs", "transcript.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(agyLog)!);
            File.WriteAllLines(agyLog, [
                $$"""{"step_index":1,"type":"USER_INPUT","created_at":"{{iso}}","content":"secret prompt"}""",
                $$"""{"step_index":2,"type":"PLANNER_RESPONSE","created_at":"{{iso}}","model":"gemini-4-neon-xhigh","status":"DONE","input_tokens":10000,"output_tokens":500,"cache_read_tokens":20000}""",
                $$"""{"step_index":3,"type":"CHECKPOINT","created_at":"{{iso}}","status":"DONE","input_tokens":2000,"output_tokens":100,"cache_read_tokens":40000}""",
            ]);

            // 2. Copilot CLI:
            //    sess-1: per-call session.usage_record + cumulative session.shutdown (must NOT double-count shutdown; inputTokens includes cacheRead+cacheWrite).
            //    sess-2: session.shutdown ONLY (must fall back to modelMetrics).
            //    sess-3: legacy assistant.usage per-call event.
            var copilot1 = Path.Combine(home, ".copilot", "session-state", "sess-1", "events.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(copilot1)!);
            File.WriteAllLines(copilot1, [
                $$"""{"type":"session.usage_record","timestamp":"{{iso}}","id":"e1","data":{"usage":{"model":"gpt-6-luna","inputTokens":15000,"outputTokens":400,"cacheReadTokens":10000,"cacheWriteTokens":2000,"serviceRequestId":"req-1"} } }""",
                $$"""{"type":"session.shutdown","timestamp":"{{iso}}","id":"e2","data":{"modelMetrics":{"gpt-6-luna":{"requests":{"count":1},"usage":{"inputTokens":15000,"outputTokens":400,"cacheReadTokens":10000,"cacheWriteTokens":2000} } } } }""",
            ]);
            var copilot2 = Path.Combine(home, ".copilot", "session-state", "sess-2", "events.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(copilot2)!);
            File.WriteAllLines(copilot2, [
                $$"""{"type":"session.shutdown","timestamp":"{{iso}}","id":"e3","data":{"modelMetrics":{"Astra/deepseek-flash":{"requests":{"count":2},"usage":{"inputTokens":5000,"outputTokens":200,"cacheReadTokens":1000,"cacheWriteTokens":0} } } } }""",
            ]);
            var copilot3 = Path.Combine(home, ".copilot", "session-state", "sess-3", "events.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(copilot3)!);
            File.WriteAllLines(copilot3, [
                $$"""{"type":"assistant.usage","timestamp":"{{iso}}","id":"e4","data":{"model":"gpt-6-luna","inputTokens":6000,"outputTokens":100,"cacheReadTokens":4000,"cacheWriteTokens":0} }""",
            ]);

            // 3. Grok CLI: sessions/<encoded-cwd>/<id>/usage.json (with exact costUsdTicks = 10^10 ticks/USD).
            var grokUsage = Path.Combine(home, ".grok", "sessions", "%2Ftmp%2Fdemo", "sess-g1", "usage.json");
            Directory.CreateDirectory(Path.GetDirectoryName(grokUsage)!);
            File.WriteAllText(grokUsage, $$"""
                {
                  "sessionId": "sess-g1",
                  "updatedAt": "{{iso}}",
                  "session": { "inputTokens": 20000, "outputTokens": 1000, "cachedReadTokens": 5000, "cacheCreationTokens": 0, "totalTokens": 21000, "modelCalls": 2, "costUsdTicks": 5000000000, "primaryModelId": "grok-4.7-build" },
                  "turns": [
                    {
                      "turnNumber": 1,
                      "endedAt": "{{iso}}",
                      "primaryModelId": "grok-4.7-build",
                      "modelUsage": {
                        "grok-4.7-build": { "inputTokens": 20000, "outputTokens": 1000, "cachedReadTokens": 5000, "cacheCreationTokens": 0, "totalTokens": 21000, "modelCalls": 2, "costUsdTicks": 5000000000 }
                      }
                    }
                  ]
                }
                """);

            const string priceJson = """
                {
                  "google": { "models": { "gemini-4-neon": { "cost": { "input": 3, "output": 15, "cache_read": 0.15 } } } },
                  "openai": { "models": { "gpt-6-luna": { "cost": { "input": 2, "output": 10, "cache_read": 0.2, "cache_write": 2.5 } } } },
                  "deepseek": { "models": { "deepseek-flash": { "cost": { "input": 1, "output": 2, "cache_read": 0.1 } } } },
                  "xai": { "models": { "grok-4-7": { "cost": { "input": 2, "output": 6, "cache_read": 0.5 } } } }
                }
                """;

            await using var harness = new Harness
            {
                PriceCatalog = _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(priceJson))),
            };
            CopyDirectory(PackageSource(), Path.Combine(harness.BuiltinRoot, Id));
            var pluginCatalog = new PluginCatalog(harness.Paths, harness.State, PluginHostInfo.Current(new SemanticVersion(1, 0, 0)),
                new PluginCatalogOptions { BuiltinRoots = [harness.BuiltinRoot] });
            var agentCatalog = new AgentCatalog(harness);
            harness.ExtraModules.Add(new FakeAgents(includeFsScopes: false));
            harness.ExtraModules.Add(new FsModule(
                new FileAccessService(), new PluginAccessPolicy(), new NoTrashService(), harness.Paths, harness,
                new EmptyProjectContext(), agentCatalog, pluginCatalog, home: new UserHome(home)));

            var manager = await harness.StartAsync();
            await manager.FireAsync("onAgentInstalled:agy");
            await Harness.WaitUntil(() => manager.Find(Id)?.State is PluginHostState.Running or PluginHostState.Failed);
            var plugin = manager.Find(Id)!;
            Assert.True(plugin.State == PluginHostState.Running, plugin.LastError);

            var context = new JsonObject { ["instanceId"] = "tab:history", ["visible"] = true };
            await plugin.InvokeAsync("view.open", ["view", "octopus.agent-usage.history", context], TimeSpan.FromSeconds(5));

            // Wait for the scan to populate the Table with all three agents' models.
            JsonNode? table = null;
            await Harness.WaitUntil(() =>
            {
                var rendered = plugin.InvokeAsync("view.render", ["tab:history"], TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                table = Find(JsonNode.Parse(rendered), "Table");
                return table is not null && table["p"]!["rows"]!.AsArray().Count >= 4;
            }, 10_000);

            var rows = table!["p"]!["rows"]!.AsArray().ToDictionary(
                r => r!["id"]!.GetValue<string>(),
                r => r!["cells"]!.AsArray());

            // agy|gemini-4-neon-xhigh: input=12000, output=600, cache=60000, total=72600.
            // Cost: (12000*3 + 600*15 + 60000*0.15) / 1e6 = (36000 + 9000 + 9000) / 1e6 = $0.054 -> 0.05.
            Assert.True(rows.ContainsKey("agy|gemini-4-neon-xhigh"), string.Join(", ", rows.Keys));
            var agyCells = rows["agy|gemini-4-neon-xhigh"];
            Assert.Equal("agy", agyCells[1]!.GetValue<string>());
            Assert.Equal(0.05, agyCells[2]!.GetValue<double>());
            Assert.Equal(12000, agyCells[4]!.GetValue<long>());
            Assert.Equal(600, agyCells[5]!.GetValue<long>());
            Assert.Equal(60000, agyCells[6]!.GetValue<long>());
            Assert.Equal(72600, agyCells[7]!.GetValue<long>());

            // copilot|gpt-6-luna: sess-1 (3000 in, 400 out, 12000 cache; shutdown skipped!) + sess-3 (2000 in, 100 out, 4000 cache)
            // => input=5000, output=500, cache=16000, total=21500.
            Assert.True(rows.ContainsKey("copilot|gpt-6-luna"), string.Join(", ", rows.Keys));
            var copilotCells = rows["copilot|gpt-6-luna"];
            Assert.Equal("copilot", copilotCells[1]!.GetValue<string>());
            Assert.Equal(5000, copilotCells[4]!.GetValue<long>());
            Assert.Equal(500, copilotCells[5]!.GetValue<long>());
            Assert.Equal(16000, copilotCells[6]!.GetValue<long>());
            Assert.Equal(21500, copilotCells[7]!.GetValue<long>());

            // copilot|Astra/deepseek-flash (shutdown-only fallback): input=4000, output=200, cache=1000, total=5200.
            Assert.True(rows.ContainsKey("copilot|Astra/deepseek-flash"), string.Join(", ", rows.Keys));
            var deepseekCells = rows["copilot|Astra/deepseek-flash"];
            Assert.Equal(4000, deepseekCells[4]!.GetValue<long>());
            Assert.Equal(200, deepseekCells[5]!.GetValue<long>());
            Assert.Equal(1000, deepseekCells[6]!.GetValue<long>());
            Assert.Equal(5200, deepseekCells[7]!.GetValue<long>());

            // grok|grok-4.7-build: input=15000, output=1000, cache=5000, total=21000, directCost=$0.50 (5e9 / 1e10).
            Assert.True(rows.ContainsKey("grok|grok-4.7-build"), string.Join(", ", rows.Keys));
            var grokCells = rows["grok|grok-4.7-build"];
            Assert.Equal("grok", grokCells[1]!.GetValue<string>());
            Assert.Equal(0.5, grokCells[2]!.GetValue<double>());
            Assert.Equal(15000, grokCells[4]!.GetValue<long>());
            Assert.Equal(1000, grokCells[5]!.GetValue<long>());
            Assert.Equal(5000, grokCells[6]!.GetValue<long>());
            Assert.Equal(21000, grokCells[7]!.GetValue<long>());

            // Append turn 2 to Grok usage.json and trigger Rescan: turn 1 must NOT be double-counted.
            File.WriteAllText(grokUsage, $$"""
                {
                  "sessionId": "sess-g1",
                  "updatedAt": "{{iso}}",
                  "turns": [
                    {
                      "turnNumber": 1,
                      "endedAt": "{{iso}}",
                      "primaryModelId": "grok-4.7-build",
                      "modelUsage": {
                        "grok-4.7-build": { "inputTokens": 20000, "outputTokens": 1000, "cachedReadTokens": 5000, "cacheCreationTokens": 0, "totalTokens": 21000, "modelCalls": 2, "costUsdTicks": 5000000000 }
                      }
                    },
                    {
                      "turnNumber": 2,
                      "endedAt": "{{iso}}",
                      "primaryModelId": "grok-4.7-build",
                      "modelUsage": {
                        "grok-4.7-build": { "inputTokens": 10000, "outputTokens": 500, "cachedReadTokens": 2000, "cacheCreationTokens": 0, "totalTokens": 10500, "modelCalls": 1, "costUsdTicks": 2500000000 }
                      }
                    }
                  ]
                }
                """);
            var renderedBeforeRescan = JsonNode.Parse(await plugin.InvokeAsync("view.render", ["tab:history"], TimeSpan.FromSeconds(5)))!;
            var rescanButton = FindButtonByIcon(renderedBeforeRescan, "lucide:refresh-cw")!;
            var onClick = rescanButton["p"]!["onClick"]!["$h"]!.GetValue<string>();
            await plugin.InvokeAsync("view.event", ["tab:history", onClick, new JsonArray()], TimeSpan.FromSeconds(5));

            await Harness.WaitUntil(() =>
            {
                var r = JsonNode.Parse(plugin.InvokeAsync("view.render", ["tab:history"], TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
                var t = Find(r, "Table");
                var grokRow = t?["p"]?["rows"]?.AsArray().FirstOrDefault(x => x?["id"]?.GetValue<string>() == "grok|grok-4.7-build");
                return grokRow?["cells"]?[7]?.GetValue<long>() == 31500;
            }, 10_000);

            var finalTree = JsonNode.Parse(await plugin.InvokeAsync("view.render", ["tab:history"], TimeSpan.FromSeconds(5)))!;
            var finalGrok = Find(finalTree, "Table")!["p"]!["rows"]!.AsArray().First(x => x!["id"]!.GetValue<string>() == "grok|grok-4.7-build")!["cells"]!.AsArray();
            Assert.Equal(0.75, finalGrok[2]!.GetValue<double>());
            Assert.Equal(23000, finalGrok[4]!.GetValue<long>());
            Assert.Equal(1500, finalGrok[5]!.GetValue<long>());
            Assert.Equal(7000, finalGrok[6]!.GetValue<long>());
            Assert.Equal(31500, finalGrok[7]!.GetValue<long>());

            await plugin.InvokeAsync("view.close", ["tab:history"], TimeSpan.FromSeconds(5));
        }
        finally
        {
            try { Directory.Delete(home, true); } catch (IOException) { }
        }
    }

    private static JsonNode? FindButtonByIcon(JsonNode? node, string icon)
    {
        if (node is not JsonObject obj) return null;
        if (obj["t"]?.GetValue<string>() == "Button" && obj["p"]?["icon"]?.GetValue<string>() == icon) return obj;
        foreach (var child in obj["c"]?.AsArray() ?? [])
        {
            if (FindButtonByIcon(child, icon) is { } found) return found;
        }
        return null;
    }

    private sealed class EmptyProjectContext : IPluginProjectContext
    {
        public PluginProjectInfo? Current => null;
        public IReadOnlyList<PluginProjectInfo> All => [];
        public event EventHandler? CurrentChanged { add { } remove { } }
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
        // The bill export (issue #5) starts from the history tab.
        Assert.Contains("lucide:receipt-text", tree);
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
    private sealed class FakeAgents(bool includeFsScopes = true) : IPluginApiModule
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
                foreach (var (id, name) in new[]
                {
                    ("claude", "Claude Code"), ("codex", "Codex CLI"), ("agy", "Antigravity CLI"),
                    ("copilot", "GitHub Copilot CLI"), ("grok", "Grok CLI"), ("cursor", "Cursor"), ("opencode", "OpenCode"),
                })
                {
                    list.Add((JsonNode)new JsonObject { ["id"] = id, ["name"] = name, ["installed"] = true });
                }
                return Task.FromResult(PluginReply.Of(list));
            });
            router.Request("agents.runProbe", (call, _) =>
            {
                Probes.Enqueue($"{call.String("agentId")}:{call.String("account")}");
                var futureIso = DateTimeOffset.UtcNow.AddHours(3).ToString("O");
                var futureWeekIso = DateTimeOffset.UtcNow.AddDays(5).ToString("O");
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
                    // As agy 1.5.15 answered for `agy --print /usage --output-format json`.
                    "agy" => new JsonObject
                    {
                        ["ok"] = true, ["account"] = "", ["completedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ["data"] = JsonNode.Parse($$"""
                            { "status": "SUCCESS", "command": { "data": { "groups": [
                              { "name": "Gemini Models", "buckets": [
                                { "id": "gemini-weekly", "name": "Weekly Limit Remaining", "window": "weekly", "remaining_fraction": 0.92, "reset_time": "{{futureWeekIso}}" },
                                { "id": "gemini-5h", "name": "Five Hour Limit Remaining", "window": "5h", "remaining_fraction": 0.76, "reset_time": "{{futureIso}}" }
                              ] },
                              { "name": "Claude and GPT models", "buckets": [
                                { "id": "3p-weekly", "name": "Weekly Limit Remaining", "window": "weekly", "remaining_fraction": 1, "reset_time": "{{futureWeekIso}}" },
                                { "id": "3p-5h", "name": "Five Hour Limit Remaining", "window": "5h", "remaining_fraction": 1, "reset_time": "{{futureIso}}" }
                              ] }
                            ] } } }
                            """),
                    },
                    // As Copilot CLI's headless JSON-RPC server answers for `account.getQuota`.
                    "copilot" => new JsonObject
                    {
                        ["ok"] = true, ["account"] = call.String("account") ?? "", ["completedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ["data"] = call.String("account") == "c0p110700002"
                            ? JsonNode.Parse("""{ "quotaSnapshots": { "premium_interactions": { "isUnlimitedEntitlement": true } } }""")
                            : JsonNode.Parse($$"""{ "quotaSnapshots": { "premium_interactions": { "entitlementRequests": 300, "usedRequests": 60, "remainingPercentage": 80, "resetDate": "{{futureWeekIso}}" } } }"""),
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
            if (includeFsScopes) router.Sync("fs.scopes", _ => "[]");

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
