using System.Text.Json.Nodes;
using Octopus.Plugins.Runtime;
using Xunit;

namespace Octopus.Plugins.Jaunt.Tests;

public sealed class RuntimeTests : IAsyncDisposable
{
    private readonly Harness _h = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private const string Commands = """ "contributes": { "commands": [ { "id": "acme.t.run", "title": "Run" }, { "id": "acme.t.other", "title": "Other" } ] } """;

    private static string Str(JsonNode? node) => node!.GetValue<string>();

    [Fact]
    public async Task A_command_runs_after_activation_and_logs_reach_the_host()
    {
        _h.Plugin("acme.t", """
            import { commands } from 'octopus';
            export function activate(ctx) {
              console.log('activated', ctx.id, { version: ctx.version });
              ctx.subscriptions.push(commands.register('acme.t.run', (a, b) => `sum=${a + b}`));
            }
            """, Commands);
        var manager = await _h.StartAsync();

        var result = await manager.ExecuteCommandAsync("acme.t.run", [2, 3]);

        Assert.Equal("sum=5", Str(result));
        var host = manager.Find("acme.t")!;
        Assert.Equal(PluginHostState.Running, host.State);
        Assert.Contains(host.Log, e => e.Text == "activated acme.t {\"version\":\"1.0.0\"}");
    }

    [Fact]
    public async Task Host_calls_resolve_in_later_turns_and_storage_persists()
    {
        _h.Plugin("acme.t", """
            import { commands } from 'octopus';
            export function activate(ctx) {
              commands.register('acme.t.run', async () => {
                await ctx.globalState.set('count', ((await ctx.globalState.get('count')) ?? 0) + 1);
                await ctx.projectState('p1').set('name', '项目');
                return [await ctx.globalState.get('count'), await ctx.projectState('p1').get('name'), await ctx.globalState.keys()];
              });
            }
            """, Commands);
        var manager = await _h.StartAsync();

        await manager.ExecuteCommandAsync("acme.t.run");
        var second = (JsonArray)(await manager.ExecuteCommandAsync("acme.t.run"))!;

        Assert.Equal(2, second[0]!.GetValue<double>());
        Assert.Equal("项目", Str(second[1]));
        Assert.True(File.Exists(Path.Combine(_h.Paths.DataDirectoryOf("acme.t"), "state.json")));
        Assert.True(File.Exists(Path.Combine(_h.Paths.DataDirectoryOf("acme.t"), "projects", "p1.json")));
    }

    [Fact]
    public async Task Timers_fire_and_can_be_cleared()
    {
        _h.Plugin("acme.t", """
            import { commands } from 'octopus';
            export function activate() {
              commands.register('acme.t.run', () => new Promise(resolve => {
                const fired = [];
                const cancelled = setTimeout(() => fired.push('never'), 30);
                clearTimeout(cancelled);
                let ticks = 0;
                const interval = setInterval(() => { if (++ticks === 3) { clearInterval(interval); fired.push('interval'); } }, 20);
                setTimeout(() => { fired.push('once'); setTimeout(() => resolve(fired), 120); }, 40);
              }));
            }
            """, Commands);
        var manager = await _h.StartAsync();

        var fired = (JsonArray)(await manager.ExecuteCommandAsync("acme.t.run"))!;

        Assert.Equal(["once", "interval"], fired.Select(Str).Order().Reverse());
    }

    [Fact]
    public async Task A_runaway_handler_crashes_the_plugin_which_restarts_and_stops_after_too_many_crashes()
    {
        _h.Plugin("acme.t", """
            import { commands } from 'octopus';
            export function activate() {
              commands.register('acme.t.run', () => { for (;;) {} });
              commands.register('acme.t.other', () => 'alive');
            }
            """, Commands);
        var manager = await _h.StartAsync();
        var host = manager.Find("acme.t")!;
        // The call fails before the host has handled the fault, so "Running" right after it can still be the old run:
        // wait for the crash to be recorded as well.
        Task RestartedAfter(int crashes) => Harness.WaitUntil(() =>
            (_h.State.Current.Find("acme.t")?.Crashes.Count ?? crashes) >= crashes
            && host.State == PluginHostState.Running && host.Commands.Contains("acme.t.other"));

        await Assert.ThrowsAnyAsync<Exception>(() => manager.ExecuteCommandAsync("acme.t.run"));
        await RestartedAfter(1);
        Assert.Equal("alive", Str(await manager.ExecuteCommandAsync("acme.t.other")));
        // Whichever budget a fast machine hits first: work units or time.
        Assert.Contains(host.Log, e => e.Text.Contains("exceeded its"));

        await Assert.ThrowsAnyAsync<Exception>(() => manager.ExecuteCommandAsync("acme.t.run"));
        await RestartedAfter(2);
        await Assert.ThrowsAnyAsync<Exception>(() => manager.ExecuteCommandAsync("acme.t.run"));
        await Harness.WaitUntil(() => host.State == PluginHostState.Stopped);
        Assert.Contains("3 crashes", host.LastError);
        Assert.Equal(3, _h.State.Current.Find("acme.t")?.Crashes.Count ?? 3);

        await manager.ReloadAsync("acme.t");
        Assert.Equal("alive", Str(await manager.ExecuteCommandAsync("acme.t.other")));
    }

    [Fact]
    public async Task Load_errors_leave_the_plugin_failed_with_a_readable_reason()
    {
        _h.Plugin("acme.tla", "await Promise.resolve(); export function activate() {}");
        _h.Plugin("acme.syntax", "export function activate( {");
        _h.Plugin("acme.bare", "import _ from 'lodash'; export function activate() {}");
        _h.Plugin("acme.noactivate", "export const x = 1;");
        _h.Plugin("acme.throws", "export function activate() { throw new Error('boom'); }");
        var manager = await _h.StartAsync();

        foreach (var id in new[] { "acme.tla", "acme.syntax", "acme.bare", "acme.noactivate", "acme.throws" })
        {
            await manager.EnsureActiveAsync(id);
            Assert.Equal(PluginHostState.Failed, manager.Find(id)!.State);
        }
        Assert.Contains("top-level await", manager.Find("acme.tla")!.LastError);
        Assert.Contains("main.js:1", manager.Find("acme.syntax")!.LastError);
        Assert.Contains("lodash", manager.Find("acme.bare")!.LastError);
        Assert.Contains("must export an activate function", manager.Find("acme.noactivate")!.LastError);
        Assert.Contains("boom", manager.Find("acme.throws")!.LastError);
    }

    [Fact]
    public async Task Relative_and_json_imports_resolve_inside_the_package_only()
    {
        _h.Plugin("acme.t", """
            import { commands } from 'octopus';
            import { add } from './lib/math.js';
            import data from './data.json' with { type: 'json' };
            export function activate() {
              commands.register('acme.t.run', async () => {
                let escaped;
                try { await import('../../escape.js'); escaped = true; } catch { escaped = false; }
                return [add(data.a, data.b), escaped];
              });
            }
            """, Commands, ("lib/math.js", "export const add = (a, b) => a + b;"), ("data.json", """{ "a": 40, "b": 2 }"""));
        File.WriteAllText(Path.Combine(_h.BuiltinRoot, "escape.js"), "export const x = 1;");
        var manager = await _h.StartAsync();

        var result = (JsonArray)(await manager.ExecuteCommandAsync("acme.t.run"))!;

        Assert.Equal(42, result[0]!.GetValue<double>());
        Assert.False(result[1]!.GetValue<bool>());
    }

    [Fact]
    public async Task Eval_is_blocked_unless_the_manifest_asks_for_it()
    {
        const string main = """
            import { commands } from 'octopus';
            export function activate() {
              commands.register('acme.t.run', () => { try { return eval('6 * 7'); } catch (e) { return e.name; } });
            }
            """;
        _h.Plugin("acme.t", main, Commands);
        _h.Plugin("acme.u", main.Replace("acme.t.run", "acme.u.run"), """ "unsafeEval": true, "contributes": { "commands": [ { "id": "acme.u.run", "title": "Run" } ] } """);
        var manager = await _h.StartAsync();

        Assert.Equal("EvalError", Str(await manager.ExecuteCommandAsync("acme.t.run")));
        Assert.Equal(42, (await manager.ExecuteCommandAsync("acme.u.run"))!.GetValue<double>());
    }

    [Fact]
    public async Task Undeclared_or_ungranted_permissions_are_refused_and_runtime_requests_ask_the_user()
    {
        var consent = new FixedConsent(answer: false);
        _h.Consent = consent;
        _h.InstalledPlugin("acme.t", """
            import { commands, permissions, OctopusError } from 'octopus';
            export function activate() {
              commands.register('acme.t.run', async () => {
                const before = permissions.has('git');
                let error;
                try { await globalThis.__probe; } catch {}
                const asked = await permissions.request('git', 'show branches');
                const undeclared = await permissions.request('net', 'nope');
                return { before, asked, undeclared };
              });
            }
            """, """ "permissions": { "git": "read" }, "contributes": { "commands": [ { "id": "acme.t.run", "title": "Run" } ] } """,
            new Dictionary<string, string>());
        var manager = await _h.StartAsync();

        var result = (JsonObject)(await manager.ExecuteCommandAsync("acme.t.run"))!;

        Assert.False(result["before"]!.GetValue<bool>());
        Assert.False(result["asked"]!.GetValue<bool>());
        Assert.False(result["undeclared"]!.GetValue<bool>());
        Assert.Equal(["git:show branches"], consent.Asked);
        Assert.Contains("git", _h.State.Current.Find("acme.t")!.Denied);
    }

    [Fact]
    public async Task Calls_to_unknown_host_methods_reject_with_unsupported()
    {
        _h.Plugin("acme.t", """
            import { commands, git, workers } from 'octopus';
            export function activate() {
              commands.register('acme.t.run', async () => {
                const codes = [];
                try { await git.status(); } catch (e) { codes.push(e.code); }
                try { workers.spawn('./w.js'); } catch (e) { codes.push(e.code); }
                return codes;
              });
            }
            """, Commands);
        var manager = await _h.StartAsync();

        var codes = (JsonArray)(await manager.ExecuteCommandAsync("acme.t.run"))!;

        Assert.Equal(["Unsupported", "Unsupported"], codes.Select(Str));
    }

    [Fact]
    public async Task Commands_must_be_declared_to_be_registered()
    {
        _h.Plugin("acme.t", """
            import { commands } from 'octopus';
            export function activate() {
              commands.register('acme.t.run', () => 1);
              commands.register('someone.else', () => 2);
            }
            """, Commands);
        var manager = await _h.StartAsync();
        await manager.EnsureActiveAsync("acme.t");

        var host = manager.Find("acme.t")!;
        Assert.Equal(["acme.t.run"], host.Commands);
        Assert.Contains(host.Log, e => e.Text.Contains("someone.else"));
        var error = await Assert.ThrowsAsync<PluginApiException>(() => manager.ExecuteCommandAsync("acme.t.other"));
        Assert.Contains("did not register", error.Message);
    }

    [Fact]
    public async Task Startup_plugins_activate_at_once_and_others_on_their_event()
    {
        _h.Plugin("acme.boot", "export function activate() { console.log('boot'); }", """ "activation": ["onStartup"] """);
        _h.Plugin("acme.codex", "export function activate() {}", """ "activation": ["onAgentInstalled:codex"] """);
        _h.Plugin("acme.lazy", "export function activate() {}");
        var manager = await _h.StartAsync();

        Assert.Equal(PluginHostState.Running, manager.Find("acme.boot")!.State);
        Assert.Equal(PluginHostState.Inactive, manager.Find("acme.codex")!.State);
        await manager.FireAsync("onAgentInstalled:codex");
        Assert.Equal(PluginHostState.Running, manager.Find("acme.codex")!.State);
        Assert.Equal(PluginHostState.Inactive, manager.Find("acme.lazy")!.State);
    }
}
