using System.Text.Json.Nodes;
using Octopus.Plugins.Runtime;
using Xunit;

namespace Octopus.Plugins.Jaunt.Tests;

public sealed class ApiTests : IAsyncDisposable
{
    private readonly Harness _h = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private static string Str(JsonNode? node) => node!.GetValue<string>();

    [Fact]
    public async Task Views_render_to_trees_whose_functions_become_handler_ids()
    {
        _h.Plugin("acme.v", """
            import { views, ui } from 'octopus';
            export function activate() {
              views.register('acme.v.board', {
                open(view) {
                  const count = ui.state(view.state?.start ?? 0);
                  view.setTitle('Board');
                  return {
                    render: () => ui.column({ gap: 2 }, [
                      ui.text(`count ${count.get()}`, { key: 'label' }),
                      ui.button({ key: 'inc', label: '+1', onClick: () => count.set(count.get() + 1) }),
                    ]),
                    getState: () => ({ start: count.get() }),
                  };
                },
              });
            }
            """, """ "contributes": { "views": [ { "id": "acme.v.board", "title": "Board" } ] } """);
        var manager = await _h.StartAsync();
        var host = (await manager.EnsureActiveAsync("acme.v"))!;
        Assert.Contains(("view", "acme.v.board"), host.Providers);

        var opened = JsonNode.Parse(await host.InvokeAsync("view.open",
            ["view", "acme.v.board", new JsonObject { ["instanceId"] = "i1", ["state"] = new JsonObject { ["start"] = 5 } }]))!;
        var tree = opened["tree"]!;
        Assert.Equal("Column", Str(tree["t"]));
        Assert.Equal("count 5", Str(tree["c"]![0]!["p"]!["value"]));
        var handler = Str(tree["c"]![1]!["p"]!["onClick"]!["$h"]);

        await host.InvokeAsync("view.event", ["i1", handler, new JsonArray()]);
        await Harness.WaitUntil(() => _h.Captured.Notifications.Any(n => n.Method == "view.update"));

        var update = _h.Captured.Notifications.Last(n => n.Method == "view.update").Args!;
        Assert.Equal("i1", Str(update["instanceId"]));
        Assert.Equal("count 6", Str(update["tree"]!["c"]![0]!["p"]!["value"]));
        Assert.Contains(_h.Captured.Notifications, n => n.Method == "view.chrome" && Str(n.Args!["title"]) == "Board");
        Assert.Equal(6, JsonNode.Parse(await host.InvokeAsync("view.getState", ["i1"]))!["start"]!.GetValue<double>());
        Assert.Equal("null", await host.InvokeAsync("view.event", ["i1", "r/9:onClick", new JsonArray()]));
    }

    [Fact]
    public async Task A_hidden_view_renders_again_only_when_shown()
    {
        _h.Plugin("acme.v", """
            import { views, ui, commands } from 'octopus';
            let current;
            export function activate() {
              views.register('acme.v.board', { open(view) { current = view; let n = 0; return { render: () => ui.text(`n${++n}`) }; } });
              commands.register('acme.v.poke', () => { current.invalidate(); current.invalidate(); return true; });
            }
            """, """ "contributes": { "views": [ { "id": "acme.v.board", "title": "B" } ], "commands": [ { "id": "acme.v.poke", "title": "Poke" } ] } """);
        var manager = await _h.StartAsync();
        var host = (await manager.EnsureActiveAsync("acme.v"))!;
        await host.InvokeAsync("view.open", ["view", "acme.v.board", new JsonObject { ["instanceId"] = "i1" }]);

        await manager.ExecuteCommandAsync("acme.v.poke");
        await Harness.WaitUntil(() => _h.Captured.Notifications.Count(n => n.Method == "view.update") == 1);

        await host.InvokeAsync("view.visibility", ["i1", false]);
        await manager.ExecuteCommandAsync("acme.v.poke");
        await Task.Delay(100);
        Assert.Equal(1, _h.Captured.Notifications.Count(n => n.Method == "view.update"));

        await host.InvokeAsync("view.visibility", ["i1", true]);
        await Harness.WaitUntil(() => _h.Captured.Notifications.Count(n => n.Method == "view.update") == 2);
        Assert.Equal("n3", Str(_h.Captured.Notifications.Last(n => n.Method == "view.update").Args!["tree"]!["p"]!["value"]));
    }

    [Fact]
    public async Task Usage_providers_report_through_the_registry()
    {
        _h.Plugin("octopus.agent-usage", """
            import { agents, commands } from 'octopus';
            export function activate() {
              const claude = agents.registerUsageProvider('claude');
              commands.register('octopus.agent-usage.refresh', () => {
                claude.update({ account: 'a1', plan: 'Max', source: 'official',
                  windows: [{ id: 'm300', label: '5h', usedPercent: 42, resetsAt: Date.now() + 3600000 },
                            { id: 'm10080', label: 'weekly', usedPercent: 10, resetsAt: Date.now() - 1000 }] });
                try { agents.registerUsageProvider('codex').update({ windows: [] }); } catch (e) {}
                return true;
              });
            }
            """, """ "contributes": { "usageProviders": [ { "id": "octopus.agent-usage.claude", "agent": "claude" } ], "commands": [ { "id": "octopus.agent-usage.refresh", "title": "Refresh" } ] } """);
        var manager = await _h.StartAsync();

        await manager.ExecuteCommandAsync("octopus.agent-usage.refresh");
        await Harness.WaitUntil(() => _h.Usage.Get("claude") is not null);

        var state = _h.Usage.Get("claude", "a1")!;
        var windows = (JsonArray)state.Snapshot["windows"]!;
        Assert.Equal(42, windows[0]!["usedPercent"]!.GetValue<double>());
        Assert.Null(windows[1]!["usedPercent"]);
        Assert.True(windows[1]!["reset"]!.GetValue<bool>());
        Assert.Null(_h.Usage.Get("codex"));
        Assert.Contains(manager.Find("octopus.agent-usage")!.Log, e => e.Text.Contains("does not declare a usage provider for codex"));
    }

    [Fact]
    public async Task Config_reads_defaults_and_reports_changes()
    {
        _h.Plugin("acme.c", """
            import { config, commands } from 'octopus';
            const seen = [];
            export function activate() {
              config.onDidChange(key => seen.push(key));
              commands.register('acme.c.run', async () => {
                const before = config.get('acme.c.delimiter');
                await config.set('acme.c.delimiter', ';');
                let error;
                try { await config.set('acme.c.delimiter', 'tab'); } catch (e) { error = e.code; }
                await new Promise(r => setTimeout(r, 50));
                return { before, after: config.get('acme.c.delimiter'), error, seen };
              });
            }
            """, """ "contributes": { "commands": [ { "id": "acme.c.run", "title": "Run" } ], "configuration": { "properties": { "acme.c.delimiter": { "type": "enum", "options": ["auto", ",", ";"], "default": "auto", "label": "Delimiter" } } } } """);
        var manager = await _h.StartAsync();

        var result = (JsonObject)(await manager.ExecuteCommandAsync("acme.c.run"))!;

        Assert.Equal("auto", Str(result["before"]));
        Assert.Equal(";", Str(result["after"]));
        Assert.Equal("InvalidArgument", Str(result["error"]));
        Assert.Equal(["acme.c.delimiter"], ((JsonArray)result["seen"]!).Select(Str));
    }

    [Fact]
    public async Task Large_text_crosses_the_bridge_outside_json_and_web_globals_work()
    {
        _h.Plugin("acme.t", """
            import { commands, env, path, l10n } from 'octopus';
            export function activate() {
              commands.register('acme.t.run', async () => {
                const bytes = new TextEncoder().encode('héllo 世界');
                const text = new TextDecoder().decode(bytes);
                const controller = new AbortController();
                controller.abort();
                return {
                  roundTrip: text, length: bytes.length, aborted: controller.signal.aborted,
                  b64: btoa('ab'), clone: structuredClone({ a: [1] }).a[0],
                  platform: env.platform, joined: path.join('a', 'b', '..', 'c'), base: path.basename('/x/y/z.txt', '.txt'),
                  greeting: l10n.t('hello', { name: 'Octo' }), now: typeof performance.now(),
                };
              });
            }
            """, """ "contributes": { "commands": [ { "id": "acme.t.run", "title": "Run" } ] } """, ("l10n/zh-CN.json", """{ "hello": "你好，{name}" }"""));
        var manager = await _h.StartAsync();

        var r = (JsonObject)(await manager.ExecuteCommandAsync("acme.t.run"))!;

        Assert.Equal("héllo 世界", Str(r["roundTrip"]));
        Assert.Equal(13, r["length"]!.GetValue<double>());
        Assert.True(r["aborted"]!.GetValue<bool>());
        Assert.Equal("YWI=", Str(r["b64"]));
        Assert.Equal(1, r["clone"]!.GetValue<double>());
        Assert.Equal(EnvModulePlatform(), Str(r["platform"]));
        Assert.Equal(OperatingSystem.IsWindows() ? "a\\c" : "a/c", Str(r["joined"]));
        Assert.Equal("z", Str(r["base"]));
        Assert.Equal("你好，Octo", Str(r["greeting"]));
        Assert.Equal("number", Str(r["now"]));
    }

    private static string EnvModulePlatform() => Octopus.Plugins.Runtime.Modules.EnvModule.Platform;
}
