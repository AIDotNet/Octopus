using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Octopus.Agents.Probes;
using Octopus.Agents.Signals;
using Octopus.App.Composition;
using Octopus.App.Plugins;
using Octopus.App.Plugins.Rendering;
using Octopus.App.Plugins.Settings;
using Octopus.App.Services;
using Octopus.Plugins.Runtime;
using Octopus.Plugins.Usage;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests.Plugins;

/// <summary>
/// Runs the built-in agent usage plugin as shipped inside the headless app (a fake Codex probe, a Claude status line
/// published to the signal hub) and saves its popover, its history tab and Settings → Plugins to <c>bin/shots</c>.
/// </summary>
public sealed class PluginScreenshots
{
    private static readonly string s_dir = Path.Combine(AppContext.BaseDirectory, "shots");

    [AvaloniaFact]
    public async Task Render_the_usage_plugin()
    {
        Directory.CreateDirectory(s_dir);
        var previous = AppHost.ConfigureOverrides;
        AppHost.ConfigureOverrides = services =>
        {
            previous?.Invoke(services);
            services.Replace(ServiceDescriptor.Singleton<IProbeRunner, FakeProbes>());
        };
        try
        {
            foreach (var dark in new[] { false, true })
            {
                TestApp.Fresh();
                ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
                await RenderAsync(dark ? "dark" : "light");
            }
        }
        finally
        {
            AppHost.ConfigureOverrides = previous;
            TestApp.Fresh();
        }
    }

    private static async Task RenderAsync(string suffix)
    {
        var manager = AppHost.Get<PluginManager>();
        // Claude's usage from a command the user typed (Settings → Plugins → Agent Usage).
        var catalog = AppHost.Get<Octopus.Plugins.Catalog.IPluginCatalog>();
        catalog.Refresh();
        var usageManifest = catalog.Find("octopus.agent-usage")!.Manifest!;
        var config = AppHost.Get<Octopus.Plugins.Runtime.Modules.PluginConfigStore>();
        config.Set(usageManifest, "octopus.agent-usage.claude.source", JsonValue.Create("command"), byUser: true);
        config.Set(usageManifest, "octopus.agent-usage.claude.command",
            JsonValue.Create("printf '%s' '{\"plan\":\"Max\",\"windows\":[{\"label\":\"5 小时\",\"usedPercent\":64},{\"label\":\"7 天\",\"usedPercent\":23}]}'"), byUser: true);
        await manager.StartAsync();
        await manager.FireAsync("onAgentInstalled:codex");
        var plugin = manager.Find("octopus.agent-usage")!;
        Assert.True(Frames.Until(() => plugin.State == PluginHostState.Running), plugin.LastError);

        var now = DateTimeOffset.UtcNow;
        AppHost.Get<ISignalHub>().Publish(new AgentSignal("claude", "statusline", "item-1", "a1b2c3d4e5f6",
            now.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds(), $$"""
            { "rate_limits": { "five_hour": { "used_percentage": 64, "resets_at": {{now.AddMinutes(112).ToUnixTimeSeconds()}} },
                               "seven_day": { "used_percentage": 23, "resets_at": {{now.AddDays(3).ToUnixTimeSeconds()}} } },
              "cost": { "total_cost_usd": 1.84 }, "context_window": { "used_percentage": 41 } }
            """));
        var usage = AppHost.Get<UsageRegistry>();
        Assert.True(Frames.Until(() => usage.Get("claude") is not null && usage.Get("codex") is not null, milliseconds: 10_000), "No usage arrived.");

        // The main window: the indicator sits in the top bar, left of the split button.
        var main = new Octopus.App.Shell.MainWindow { Width = 1280, Height = 800 };
        AppHost.Get<IProjectService>().AddProject(Directory.CreateTempSubdirectory("octopus-shot-main-").FullName);
        main.Show();
        Assert.True(Frames.Until(() => AppHost.Get<PluginIndicators>().Items.Any(i => i.Text is not null)), "The indicator did not show.");
        Frames.Until(() => false, milliseconds: 300);
        Frames.Save(main, Path.Combine(s_dir, $"plugin-indicator-{suffix}.png"));
        main.Close();

        // The popover, as the indicator shows it.
        var json = await plugin.InvokeAsync("indicator.click:octopus.agent-usage.indicator", [], TimeSpan.FromSeconds(5));
        var tree = JsonNode.Parse(json)!["tree"];
        var renderer = new UiRenderer(AppHost.Get<PluginWorkbench>().SinkFor(plugin, "indicator:shot"));
        var popover = new Window
        {
            Width = 420, Height = 640,
            Content = new Border { Padding = new Thickness(12), Child = renderer.Render(tree, null) },
        };
        popover.Show();
        Frames.Until(() => false, milliseconds: 300);
        Frames.Save(popover, Path.Combine(s_dir, $"plugin-usage-popover-{suffix}.png"));
        popover.Close();

        // The history tab, over made-up session records in the test's own home folder.
        SeedSessions(AppHost.Get<Octopus.Plugins.Security.UserHome>().Path);
        var project = AppHost.Get<IProjectService>().AddProject(Directory.CreateTempSubdirectory("octopus-shot-plugin-").FullName);
        var workbench = AppHost.Get<PluginWorkbench>();
        var (host, view) = workbench.FindView("octopus.agent-usage.history")!.Value;
        var item = workbench.OpenView(host, view)!;
        var tab = new PluginViewTab(project, item, workbench, AppHost.Get<IProjectService>());
        var window = new Window { Width = 1000, Height = 900, Content = tab };
        window.Show();
        // The scan finishes, then the chart's headline rolls to its number: wait for both.
        Frames.Until(() => false, milliseconds: 4000);
        Frames.Save(window, Path.Combine(s_dir, $"plugin-usage-history-{suffix}.png"));
        window.Close();

        // Settings → Plugins in the settings window: an installed plugin beside the built-in one, then expanded.
        InstallExample();
        var settingsView = new Octopus.App.Settings.SettingsView { InitialSection = "plugins" };
        var settings = new Window { Width = 1100, Height = 820, Content = settingsView };
        settings.Show();
        var settingsModel = (Octopus.App.Settings.SettingsViewModel)settingsView.DataContext!;
        Frames.Until(() => false, milliseconds: 300);
        Frames.Save(settings, Path.Combine(s_dir, $"plugin-settings-{suffix}.png"));
        var page = (PluginsSettingsViewModel)settingsModel.Page;
        // Two Claude configurations in the test's home: one connected (with a report), one not.
        var home = AppHost.Get<Octopus.Plugins.Security.UserHome>().Path;
        var work = Directory.CreateDirectory(Path.Combine(home, ".rec", "claude", "accounts", "1001", "config")).FullName;
        File.WriteAllText(Path.Combine(work, ".claude.json"), """{ "oauthAccount": { "emailAddress": "dev@example.com", "displayName": "Dev" } }""");
        var personal = Directory.CreateDirectory(Path.Combine(home, ".claude")).FullName;
        File.WriteAllText(Path.Combine(home, ".claude.json"), """{ "oauthAccount": { "emailAddress": "me@example.com" } }""");
        var bridge = AppHost.Get<Octopus.Agents.Signals.ClaudeStatusLineBridge>();
        bridge.Install(work);
        File.WriteAllText(bridge.OutputPath(work), """{"rate_limits":{}}""");
        _ = personal;
        page.Builtin[0].IsExpanded = true;
        page.Builtin[0].IsLogVisible = true;
        settings.Height = 2100;
        Frames.Until(() => false, milliseconds: 300);
        Frames.Save(settings, Path.Combine(s_dir, $"plugin-settings-expanded-{suffix}.png"));
        settings.Close();
    }

    /// <summary>A small installed plugin with a theme, enabled with its required permissions.</summary>
    private static void InstallExample()
    {
        var directory = AppHost.Get<Octopus.Plugins.PluginPaths>().PackageDirectoryOf("example.ocean");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), """
            { "schema": 1, "id": "example.ocean", "name": "Ocean", "version": "0.3.1", "engines": { "api": 0 },
              "description": "A calm blue theme for light and dark, and a scratchpad tab.", "icon": "lucide:waves",
              "main": "main.js",
              "permissions": { "clipboard": ["write"], "fs": { "project": { "access": "read" } } },
              "contributes": {
                "themes": [ { "id": "example.ocean.light", "label": "Ocean Light", "appearance": "light", "path": "light.json" },
                            { "id": "example.ocean.dark", "label": "Ocean Dark", "appearance": "dark", "path": "dark.json" } ],
                "views": [ { "id": "example.ocean.scratch", "title": "Scratchpad" } ] } }
            """);
        File.WriteAllText(Path.Combine(directory, "main.js"), "export function activate() {}");
        File.WriteAllText(Path.Combine(directory, "light.json"), """{ "schema": 1, "colors": { "Accent": "#1f6feb" } }""");
        File.WriteAllText(Path.Combine(directory, "dark.json"), """{ "schema": 1, "colors": { "Accent": "#58a6ff" } }""");
        AppHost.Get<Octopus.Storage.IPluginStateStore>().Update(d => d.Plugins["example.ocean"] = new Octopus.Core.Plugins.PluginRecord
        {
            Enabled = true, Source = Octopus.Core.Plugins.PluginSource.File, Version = "0.3.1",
            Grants = new() { ["clipboard.write"] = "granted" },
        });
        AppHost.Get<Octopus.Plugins.Catalog.IPluginCatalog>().Refresh();
    }

    /// <summary>Claude and Codex records of the last ten days, in their real formats, with made-up numbers.</summary>
    private static void SeedSessions(string home)
    {
        var claude = Directory.CreateDirectory(Path.Combine(home, ".claude", "projects", "demo")).FullName;
        var codex = Directory.CreateDirectory(Path.Combine(home, ".codex", "sessions", "2026", "11", "12")).FullName;
        var random = new Random(7);
        var claudeLines = new List<string>();
        var codexLines = new List<string> { """{"timestamp":"2026-11-01T00:00:00Z","type":"turn_context","payload":{"model":"gpt-5.2-codex"}}""" };
        long total = 0, input = 0, output = 0, cached = 0;
        for (var day = 9; day >= 0; day--)
        {
            var at = DateTimeOffset.UtcNow.AddDays(-day);
            for (var i = 0; i < 6; i++)
            {
                var model = i % 3 == 0 ? "claude-opus-4-5" : "claude-sonnet-4-5";
                claudeLines.Add(new JsonObject
                {
                    ["type"] = "assistant", ["timestamp"] = at.AddMinutes(i).ToString("O"), ["requestId"] = $"req-{day}-{i}",
                    ["message"] = new JsonObject
                    {
                        ["model"] = model,
                        ["usage"] = new JsonObject
                        {
                            ["input_tokens"] = random.Next(2000, 9000), ["output_tokens"] = random.Next(500, 4000),
                            ["cache_creation_input_tokens"] = random.Next(0, 20000), ["cache_read_input_tokens"] = random.Next(10000, 90000),
                        },
                    },
                }.ToJsonString());
                var step = random.Next(20000, 120000);
                input += step; cached += step / 2; output += step / 10; total = input + output;
                codexLines.Add(new JsonObject
                {
                    ["timestamp"] = at.AddMinutes(i).ToString("O"), ["type"] = "event_msg",
                    ["payload"] = new JsonObject
                    {
                        ["type"] = "token_count",
                        ["info"] = new JsonObject
                        {
                            ["total_token_usage"] = new JsonObject { ["input_tokens"] = input, ["cached_input_tokens"] = cached, ["output_tokens"] = output, ["total_tokens"] = total },
                        },
                    },
                }.ToJsonString());
            }
        }
        File.WriteAllLines(Path.Combine(claude, "session.jsonl"), claudeLines);
        File.WriteAllLines(Path.Combine(codex, "rollout-demo.jsonl"), codexLines);
    }

    /// <summary>Answers the Codex rate limit probe as this machine's app-server did in P0, with a weekly window added.</summary>
    private sealed class FakeProbes : IProbeRunner
    {
        public Task<ProbeResult> RunAsync(ProbeRequest request, CancellationToken cancellationToken = default)
        {
            var reset = DateTimeOffset.UtcNow.AddDays(9).ToUnixTimeSeconds();
            var json = request.Definition.Id == "codex"
                ? $$"""{ "rateLimits": { "planType": "plus", "primary": { "usedPercent": 43, "windowDurationMins": 43200, "resetsAt": {{reset}} }, "secondary": { "usedPercent": 12, "windowDurationMins": 10080, "resetsAt": {{reset}} } } }"""
                : null;
            return Task.FromResult(new ProbeResult(request.Definition.Id, request.Probe, "", json is not null,
                json is null ? ProbeErrorKind.NotDeclared : ProbeErrorKind.None, null, TimeSpan.FromMilliseconds(5), json, DateTimeOffset.UtcNow));
        }
    }
}
