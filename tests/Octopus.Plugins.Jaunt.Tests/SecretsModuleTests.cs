using System.Text.Json.Nodes;
using Octopus.Platform;
using Octopus.Plugins.Runtime;
using Octopus.Plugins.Runtime.Modules;
using Xunit;

namespace Octopus.Plugins.Jaunt.Tests;

/// <summary><c>octopus.secrets</c> on a real engine: a plugin keeps its own secrets, under its own service, and only with the permission.</summary>
public sealed class SecretsModuleTests : IAsyncDisposable
{
    private readonly Harness _harness = new();
    private readonly MemorySecretStore _store = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private const string Main = """
        import { commands, secrets } from 'octopus';
        export function activate(ctx) {
          ctx.subscriptions.push(commands.register('test.secrets.run', async () => {
            const before = await secrets.get('token');
            await secrets.set('token', 's3cret with spaces');
            const after = await secrets.get('token');
            await secrets.delete('token');
            const gone = await secrets.get('token');
            let error = null;
            try { await secrets.set('', 'x'); } catch (e) { error = e.code; }
            return { before: before ?? null, after, gone: gone ?? null, error };
          }));
        }
        """;

    private async Task<JsonNode?> RunAsync(string permissions)
    {
        _harness.ExtraModules.Add(new SecretsModule(_store));
        _harness.Plugin("test.secrets", Main, $$"""
            "contributes": { "commands": [ { "id": "test.secrets.run", "title": "Run" } ] },
            "permissions": {{permissions}}
            """);
        var manager = await _harness.StartAsync();
        var plugin = (await manager.EnsureActiveAsync("test.secrets"))!;
        var json = await plugin.InvokeAsync("command:test.secrets.run", [], TimeSpan.FromSeconds(10));
        return JsonNode.Parse(json);
    }

    [Fact]
    public async Task A_plugin_keeps_its_own_secrets_in_the_keystore()
    {
        var result = await RunAsync("""{ "secrets": true }""");
        Assert.Null(result!["before"]);
        Assert.Equal("s3cret with spaces", (string?)result["after"]);
        Assert.Null(result["gone"]);
        Assert.Equal("InvalidArgument", (string?)result["error"]);
        // Its service is its own.
        await _store.SetAsync("Octopus/test.secrets", "probe", "x", TestContext.Current.CancellationToken);
        Assert.Equal("x", await _store.GetAsync("Octopus/test.secrets", "probe", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Without_the_permission_a_secret_is_refused()
    {
        _harness.ExtraModules.Add(new SecretsModule(_store));
        _harness.Plugin("test.secrets", """
            import { commands, secrets } from 'octopus';
            export function activate(ctx) {
              ctx.subscriptions.push(commands.register('test.secrets.run', async () => {
                try { await secrets.get('token'); return 'read'; } catch (e) { return e.code; }
              }));
            }
            """, """
            "contributes": { "commands": [ { "id": "test.secrets.run", "title": "Run" } ] }
            """);
        var manager = await _harness.StartAsync();
        var plugin = (await manager.EnsureActiveAsync("test.secrets"))!;
        var json = await plugin.InvokeAsync("command:test.secrets.run", [], TimeSpan.FromSeconds(10));
        Assert.Equal("PermissionDenied", (string?)JsonNode.Parse(json));
    }
}
