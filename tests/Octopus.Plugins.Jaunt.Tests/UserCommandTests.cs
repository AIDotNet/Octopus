using System.Text.Json.Nodes;
using Octopus.Platform;
using Octopus.Plugins.Runtime;
using Xunit;

namespace Octopus.Plugins.Jaunt.Tests;

/// <summary>
/// <c>command</c> settings: only the user writes them, the plugin cannot read them, and it can only have the host run
/// what the user wrote (never a default, never its own text), not more often than every ten seconds.
/// </summary>
public sealed class UserCommandTests : IAsyncDisposable
{
    private readonly Harness _harness = new() { Commands = Runner() };

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    internal static UserCommandRunner Runner() => new(new ProcessRunner(), new PosixShell(), new ProcessEnvironment());

    private const string Manifest = """
        "contributes": {
          "commands": [ { "id": "test.cmd.set", "title": "Set" }, { "id": "test.cmd.get", "title": "Get" }, { "id": "test.cmd.run", "title": "Run" } ],
          "configuration": { "properties": {
            "test.cmd.source": { "type": "command", "label": "Source" } } } }
        """;

    private const string Main = """
        import { commands, config } from 'octopus';
        const attempt = async fn => { try { return { ok: await fn() }; } catch (e) { return { code: e.code }; } };
        export function activate(ctx) {
          ctx.subscriptions.push(commands.register('test.cmd.set', () => attempt(() => config.set('test.cmd.source', 'echo pwned'))));
          ctx.subscriptions.push(commands.register('test.cmd.get', () => config.get('test.cmd.source')));
          ctx.subscriptions.push(commands.register('test.cmd.run', () => attempt(() => config.runCommand('test.cmd.source'))));
        }
        """;

    private async Task<PluginHost> StartAsync()
    {
        _harness.Plugin("test.cmd", Main, Manifest);
        var manager = await _harness.StartAsync();
        return (await manager.EnsureActiveAsync("test.cmd"))!;
    }

    private static async Task<JsonNode?> Call(PluginHost plugin, string command) =>
        JsonNode.Parse(await plugin.InvokeAsync($"command:{command}", [], TimeSpan.FromSeconds(40)));

    [Fact]
    public async Task ThePluginCannotWriteOrRead_TheCommand_AndRunsOnlyWhatTheUserSet()
    {
        var plugin = await StartAsync();

        Assert.Equal("PermissionDenied", (await Call(plugin, "test.cmd.set"))!["code"]!.GetValue<string>());
        Assert.False((await Call(plugin, "test.cmd.get"))!.GetValue<bool>());
        Assert.Equal("NotFound", (await Call(plugin, "test.cmd.run"))!["code"]!.GetValue<string>());

        _harness.Config.Set(plugin.Manifest, "test.cmd.source", JsonValue.Create("printf '{\"ok\":%s}' 1"), byUser: true);
        // The plugin learns that a command is set, not what it says.
        Assert.True((await Call(plugin, "test.cmd.get"))!.GetValue<bool>());
        var run = (await Call(plugin, "test.cmd.run"))!["ok"]!;
        Assert.Equal(0, run["exitCode"]!.GetValue<int>());
        Assert.Equal("""{"ok":1}""", run["stdout"]!.GetValue<string>());

        // Ten seconds between runs.
        Assert.Equal("Busy", (await Call(plugin, "test.cmd.run"))!["code"]!.GetValue<string>());
    }

    [Fact]
    public void A_command_setting_cannot_have_a_default()
    {
        var result = Octopus.Plugins.Manifests.PluginManifestReader.Parse("""
            { "schema": 1, "id": "test.cmd", "name": "x", "version": "1.0.0", "engines": { "api": 0 },
              "contributes": { "configuration": { "properties": { "test.cmd.source": { "type": "command", "label": "S", "default": "rm -rf ~" } } } } }
            """, null, Octopus.Core.Plugins.PluginSource.File);
        Assert.Contains(result.Errors, i => i.ToString().Contains("command cannot have a default", StringComparison.Ordinal));
    }

    internal sealed class PosixShell : IShellResolver
    {
        private static readonly ShellInfo Sh = new("/bin/sh", "sh", ShellKind.Posix);
        public ShellInfo GetDefaultShell() => Sh;
        public ShellInfo? Describe(string path) => Sh;
        public IReadOnlyList<string> GetInteractiveArguments(ShellInfo shell) => [];
        public IReadOnlyList<string> GetCommandArguments(ShellInfo shell, string commandLine) => ["-c", commandLine];
    }

    internal sealed class ProcessEnvironment : ILoginEnvironment
    {
        public Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? ""));
    }
}
