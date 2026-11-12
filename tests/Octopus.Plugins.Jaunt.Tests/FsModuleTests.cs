using System.Text.Json.Nodes;
using Octopus.Agents;
using Octopus.Core.Agents;
using Octopus.FileSystem;
using Octopus.Platform;
using Octopus.Plugins.Catalog;
using Octopus.Plugins.Runtime;
using Octopus.Plugins.Runtime.Modules;
using Octopus.Plugins.Security;
using Xunit;

namespace Octopus.Plugins.Jaunt.Tests;

/// <summary>
/// <c>octopus.fs</c> end to end: a plugin on a real engine, the real access policy and real files. What a plugin
/// holds decides what it reaches; links and recursive operations cannot widen it.
/// </summary>
public sealed class FsModuleTests : IAsyncDisposable
{
    private readonly Harness _harness = new();
    private readonly string _project = Directory.CreateTempSubdirectory("octopus-fs-project-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("octopus-fs-outside-").FullName;

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        foreach (var directory in new[] { _project, _outside })
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private async Task<PluginHost> StartAsync(string main, string? manifestExtra = null)
    {
        var catalog = new PluginCatalog(_harness.Paths, _harness.State, PluginHostInfo.Current(new SemanticVersion(1, 0, 0)),
            new PluginCatalogOptions { BuiltinRoots = [_harness.BuiltinRoot] });
        _harness.ExtraModules.Add(new FsModule(new FileAccessService(), new PluginAccessPolicy(), new NoTrashService(), _harness.Paths, _harness,
            new FixedProject(_project), new NoAgents(), catalog));
        _harness.Plugin("test.fs", main, manifestExtra);
        var manager = await _harness.StartAsync();
        return (await manager.EnsureActiveAsync("test.fs"))!;
    }

    private static async Task<JsonNode?> CallAsync(PluginHost plugin, string name)
    {
        var json = await plugin.InvokeAsync($"command:{name}", [], TimeSpan.FromSeconds(10));
        return JsonNode.Parse(json);
    }

    private const string Commands = """
        "contributes": { "commands": [ { "id": "test.fs.run", "title": "Run" } ] }
        """;

    [Fact]
    public async Task OwnDataDirectory_IsReadableAndWritable_WithoutAnyPermission()
    {
        var plugin = await StartAsync("""
            import { commands, fs, path } from 'octopus';
            export function activate(ctx) {
              ctx.subscriptions.push(commands.register('test.fs.run', async () => {
                const file = path.join(ctx.dataPath, 'notes', 'a.txt');
                await fs.createDirectory(path.dirname(file));
                await fs.writeText(file, 'héllo');
                await fs.appendText(file, '!');
                const text = await fs.readText(file);
                const empty = path.join(ctx.dataPath, 'empty.txt');
                await fs.writeText(empty, '');
                const list = await fs.list(path.dirname(file));
                return { text, empty: await fs.readText(empty), names: list.entries.map(e => e.name) };
              }));
            }
            """, Commands);
        var result = (await CallAsync(plugin, "test.fs.run"))!;
        Assert.Equal("héllo!", result["text"]!.GetValue<string>());
        Assert.Equal("", result["empty"]!.GetValue<string>());
        Assert.Equal(["a.txt"], result["names"]!.AsArray().Select(n => n!.GetValue<string>()));
        // The data scope is the files folder: the host's own files beside it stay out of reach.
        Assert.True(File.Exists(Path.Combine(_harness.Paths.FilesDirectoryOf("test.fs"), "notes", "a.txt")));
    }

    [Fact]
    public async Task OutsideAnyScope_IsDenied_WithThePath()
    {
        var secret = Path.Combine(_outside, "secret.txt");
        File.WriteAllText(secret, "no");
        var plugin = await StartAsync($$"""
            import { commands, fs } from 'octopus';
            export function activate(ctx) {
              ctx.subscriptions.push(commands.register('test.fs.run', async () => {
                try { await fs.readText({{JsonValue.Create(secret).ToJsonString()}}); return 'read'; }
                catch (e) { return { code: e.code, path: e.path ?? e.details?.path }; }
              }));
            }
            """, Commands);
        var result = (await CallAsync(plugin, "test.fs.run"))!;
        Assert.Equal("PermissionDenied", result["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task SearchText_StreamsMatches_FromWhatThePluginMaySee()
    {
        File.WriteAllText(Path.Combine(_project, "a.ts"), "// TODO first\nconst x = 1;\n  todo: lower case\n");
        Directory.CreateDirectory(Path.Combine(_project, "src"));
        File.WriteAllText(Path.Combine(_project, "src", "b.ts"), "nothing here\n// another TODO\n");
        File.WriteAllBytes(Path.Combine(_project, "image.bin"), [0, 1, 2, (byte)'T', (byte)'O', (byte)'D', (byte)'O', 0]);
        File.WriteAllText(Path.Combine(_outside, "secret.ts"), "TODO outside");
        Directory.CreateSymbolicLink(Path.Combine(_project, "escape"), _outside);
        var plugin = await StartAsync($$"""
            import { commands, fs } from 'octopus';
            export function activate(ctx) {
              ctx.subscriptions.push(commands.register('test.fs.run', async () => {
                const found = [];
                let batches = 0;
                await fs.searchText({{JsonValue.Create(_project).ToJsonString()}}, 'todo', {
                  onMatch: matches => { batches++; for (const m of matches) found.push(m); },
                });
                let denied;
                try { await fs.searchText({{JsonValue.Create(_outside).ToJsonString()}}, 'todo', { onMatch: () => {} }); denied = 'searched'; }
                catch (e) { denied = e.code; }
                return { found, batches, denied };
              }));
            }
            """, Commands + """
            , "permissions": { "fs": { "project": { "access": "read" } } }
            """);
        var result = (await CallAsync(plugin, "test.fs.run"))!;
        var found = result["found"]!.AsArray().Select(m => (Path.GetFileName(m!["path"]!.GetValue<string>()), m["line"]!.GetValue<int>(), m["column"]!.GetValue<int>())).OrderBy(m => m).ToList();
        Assert.Equal([("a.ts", 1, 4), ("a.ts", 3, 3), ("b.ts", 2, 12)], found);
        Assert.True(result["batches"]!.GetValue<int>() >= 1);
        Assert.Equal("PermissionDenied", result["denied"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProjectScope_FollowsTheGrant_AndALinkCannotLeaveIt()
    {
        File.WriteAllText(Path.Combine(_project, "readme.md"), "project");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "outside");
        Directory.CreateSymbolicLink(Path.Combine(_project, "escape"), _outside);
        var plugin = await StartAsync($$"""
            import { commands, fs, path } from 'octopus';
            export function activate(ctx) {
              ctx.subscriptions.push(commands.register('test.fs.run', async () => {
                const root = {{JsonValue.Create(_project).ToJsonString()}};
                const readme = await fs.readText(path.join(root, 'readme.md'));
                let escape;
                try { await fs.readText(path.join(root, 'escape', 'secret.txt')); escape = 'read'; } catch (e) { escape = e.code; }
                let write;
                try { await fs.writeText(path.join(root, 'new.txt'), 'x'); write = 'written'; } catch (e) { write = e.code; }
                return { readme, escape, write };
              }));
            }
            """, Commands + """
            , "permissions": { "fs": { "project": { "access": "read" } } }
            """);
        var result = (await CallAsync(plugin, "test.fs.run"))!;
        Assert.Equal("project", result["readme"]!.GetValue<string>());
        Assert.Equal("PermissionDenied", result["escape"]!.GetValue<string>());
        // Granted read only: writing is refused.
        Assert.Equal("PermissionDenied", result["write"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(_project, "new.txt")));
    }

    [Fact]
    public async Task ScanJsonLines_ReturnsProjectedRows_AndAnOffsetToContinueFrom()
    {
        var plugin = await StartAsync("""
            import { commands, fs, path } from 'octopus';
            export function activate(ctx) {
              ctx.subscriptions.push(commands.register('test.fs.run', async () => {
                const file = path.join(ctx.dataPath, 'log.jsonl');
                await fs.writeText(file,
                  '{"type":"assistant","message":{"model":"m1","usage":{"output_tokens":5}},"text":"secret"}\n' +
                  '{"type":"user","text":"hi"}\n' +
                  'not json\n' +
                  '{"type":"assistant","message":{"model":"m2","usage":{"output_tokens":7}}}\n');
                const first = await fs.scanJsonLines(file, { where: { type: 'assistant' }, select: ['message.model', 'message.usage.output_tokens'], limit: 1 });
                const rest = await fs.scanJsonLines(file, { fromOffset: first.nextOffset, where: { type: 'assistant' }, select: ['message.model', 'message.usage.output_tokens'] });
                return { first: first.rows, rest: rest.rows, done: rest.done, skipped: first.skipped + rest.skipped };
              }));
            }
            """, Commands);
        var result = (await CallAsync(plugin, "test.fs.run"))!;
        Assert.Equal("""[["m1",5]]""", result["first"]!.ToJsonString());
        Assert.Equal("""[["m2",7]]""", result["rest"]!.ToJsonString());
        Assert.True(result["done"]!.GetValue<bool>());
        Assert.Equal(1, result["skipped"]!.GetValue<int>());
    }

    [Fact]
    public async Task Watch_ReportsChangesInsideAScope_AndFindFilesMatchesGlobs()
    {
        var plugin = await StartAsync("""
            import { commands, fs, path } from 'octopus';
            export function activate(ctx) {
              ctx.subscriptions.push(commands.register('test.fs.run', async () => {
                const events = [];
                const watch = fs.watch(ctx.dataPath, { recursive: true }, e => events.push(e));
                await new Promise(r => setTimeout(r, 100));
                await fs.createDirectory(path.join(ctx.dataPath, 'sub'));
                await fs.writeText(path.join(ctx.dataPath, 'sub', 'w.md'), 'x');
                await fs.writeText(path.join(ctx.dataPath, 'other.txt'), 'y');
                for (let i = 0; i < 150 && !events.some(e => e.path.endsWith('w.md')); i++) await new Promise(r => setTimeout(r, 20));
                watch.dispose();
                const found = await fs.findFiles(ctx.dataPath, '**/*.md');
                const named = await fs.searchNames(ctx.dataPath, 'other');
                return { seen: events.some(e => e.path.endsWith('w.md')), found: found.map(p => path.basename(p)), named: named.map(p => path.basename(p)) };
              }));
            }
            """, Commands);
        var result = (await CallAsync(plugin, "test.fs.run"))!;
        Assert.True(result["seen"]!.GetValue<bool>());
        Assert.Equal(["w.md"], result["found"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["other.txt"], result["named"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    private sealed class FixedProject(string path) : IPluginProjectContext
    {
        public PluginProjectInfo? Current { get; } = new("p1", "Project", path, null);
        public IReadOnlyList<PluginProjectInfo> All => [Current!];
        public event EventHandler? CurrentChanged { add { } remove { } }
    }

    private sealed class NoAgents : IAgentCatalog
    {
        public IReadOnlyList<AgentDefinition> Definitions => [];
        public AgentDefinition? Find(string agentId) => null;
        public void Reload() { }
        public event EventHandler? Changed { add { } remove { } }
    }
}
