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

    [Fact]
    public async Task Declining_optional_write_at_runtime_keeps_required_read_and_remembers_denial()
    {
        var dirA = Path.Combine(_outside, "shared-target");
        Directory.CreateDirectory(dirA);
        var fileA = Path.Combine(dirA, "note.txt");
        File.WriteAllText(fileA, "hello-read");

        var consent = new FixedConsent(answer: false);
        _harness.Consent = consent;
        var catalog = new PluginCatalog(_harness.Paths, _harness.State, PluginHostInfo.Current(new SemanticVersion(1, 0, 0)),
            new PluginCatalogOptions { BuiltinRoots = [_harness.BuiltinRoot] });
        _harness.ExtraModules.Add(new FsModule(new FileAccessService(), new PluginAccessPolicy(), new NoTrashService(), _harness.Paths, _harness,
            new FixedProject(_project), new NoAgents(), catalog));

        var pattern = dirA.Replace("\\", "/") + "/**";
        var manifestExtra = $$"""
            "permissions": {
              "fs": {
                "external": [
                  { "path": {{JsonValue.Create(pattern).ToJsonString()}}, "access": "read", "reason": "Read notes" },
                  { "path": {{JsonValue.Create(pattern).ToJsonString()}}, "access": "readwrite", "reason": "Edit notes", "optional": true }
                ]
              }
            },
            {{Commands}}
            """;

        _harness.InstalledPlugin("test.fs", $$"""
            import { commands, fs, permissions } from 'octopus';
            export function activate(ctx) {
              ctx.subscriptions.push(commands.register('test.fs.run', async () => {
                const file = {{JsonValue.Create(fileA).ToJsonString()}};
                const firstReq = await permissions.request('fs.external', 'need write');
                const secondReq = await permissions.request('fs.external', 'ask again');
                const readAfterDecline = await fs.readText(file);
                let writeCode = 'written';
                try { await fs.writeText(file, 'changed'); } catch (e) { writeCode = e.code; }
                return { firstReq, secondReq, readAfterDecline, writeCode };
              }));
            }
            """, manifestExtra, new Dictionary<string, string>());

        // Seed exact post-install state: required read granted, optional readwrite not granted.
        _harness.State.Update(d =>
        {
            var rec = d.Plugins["test.fs"];
            rec.ScopedGrants.Add(new Octopus.Core.Plugins.PluginPermissionGrant("fs.external", "read", [pattern]));
            rec.Grants["fs.external"] = pattern;
        });

        var manager = await _harness.StartAsync();
        var plugin = (await manager.EnsureActiveAsync("test.fs", TestContext.Current.CancellationToken))!;
        var result = (await CallAsync(plugin, "test.fs.run"))!;

        Assert.False(result["firstReq"]!.GetValue<bool>());
        Assert.False(result["secondReq"]!.GetValue<bool>());
        Assert.Equal("hello-read", result["readAfterDecline"]!.GetValue<string>());
        Assert.Equal("PermissionDenied", result["writeCode"]!.GetValue<string>());
        // Must remember the declined optional write and not prompt a second time.
        Assert.Single(consent.Asked);
    }

    [Fact]
    public async Task Startup_migrates_old_format_multiple_required_external_paths_for_identified_installed_manifest()
    {
        var dirA = Path.Combine(_outside, "legacy-first");
        var dirB = Path.Combine(_outside, "legacy-second");
        var dirOpt = Path.Combine(_outside, "legacy-optional");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        Directory.CreateDirectory(dirOpt);
        var fileA = Path.Combine(dirA, "a.txt");
        var fileB = Path.Combine(dirB, "b.txt");
        var fileOpt = Path.Combine(dirOpt, "opt.txt");
        File.WriteAllText(fileA, "from-a");
        File.WriteAllText(fileB, "from-b");
        File.WriteAllText(fileOpt, "from-opt");

        var catalog = new PluginCatalog(_harness.Paths, _harness.State, PluginHostInfo.Current(new SemanticVersion(1, 0, 0)),
            new PluginCatalogOptions { BuiltinRoots = [_harness.BuiltinRoot] });
        _harness.ExtraModules.Add(new FsModule(new FileAccessService(), new PluginAccessPolicy(), new NoTrashService(), _harness.Paths, _harness,
            new FixedProject(_project), new NoAgents(), catalog));

        var patA = dirA.Replace("\\", "/") + "/**";
        var patB = dirB.Replace("\\", "/") + "/**";
        var patOpt = dirOpt.Replace("\\", "/") + "/**";
        var manifestExtra = $$"""
            "permissions": {
              "fs": {
                "external": [
                  { "path": {{JsonValue.Create(patA).ToJsonString()}}, "access": "read", "reason": "Read A" },
                  { "path": {{JsonValue.Create(patB).ToJsonString()}}, "access": "readwrite", "reason": "Write B" },
                  { "path": {{JsonValue.Create(patOpt).ToJsonString()}}, "access": "read", "reason": "Optional C", "optional": true }
                ]
              }
            },
            {{Commands}}
            """;

        // Base installer wrote ONLY the last required path (patB) under the shared "fs.external" key with empty ScopedGrants.
        _harness.InstalledPlugin("test.fs", $$"""
            import { commands, fs } from 'octopus';
            export function activate(ctx) {
              ctx.subscriptions.push(commands.register('test.fs.run', async () => {
                const a = await fs.readText({{JsonValue.Create(fileA).ToJsonString()}});
                await fs.writeText({{JsonValue.Create(fileB).ToJsonString()}}, 'updated-b');
                const b = await fs.readText({{JsonValue.Create(fileB).ToJsonString()}});
                let optCode = 'read';
                try { await fs.readText({{JsonValue.Create(fileOpt).ToJsonString()}}); } catch (e) { optCode = e.code; }
                return { a, b, optCode };
              }));
            }
            """, manifestExtra, new Dictionary<string, string> { ["fs.external"] = patB });

        var manager = await _harness.StartAsync();
        var plugin = (await manager.EnsureActiveAsync("test.fs", TestContext.Current.CancellationToken))!;
        var result = (await CallAsync(plugin, "test.fs.run"))!;

        Assert.Equal("from-a", result["a"]!.GetValue<string>());
        Assert.Equal("updated-b", result["b"]!.GetValue<string>());
        Assert.Equal("PermissionDenied", result["optCode"]!.GetValue<string>());
        var migrated = _harness.State.Current.Find("test.fs")!;
        Assert.Equal(2, migrated.ScopedGrants.Count(g => g.Key == "fs.external"));
    }

    [Fact]
    public async Task Startup_with_uncertain_provenance_old_format_record_requires_confirmation()
    {
        var dirA = Path.Combine(_outside, "uncertain-first");
        var dirB = Path.Combine(_outside, "uncertain-second");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        var fileA = Path.Combine(dirA, "a.txt");
        var fileB = Path.Combine(dirB, "b.txt");
        File.WriteAllText(fileA, "from-a");
        File.WriteAllText(fileB, "from-b");

        var consent = new FixedConsent(answer: true);
        _harness.Consent = consent;
        var catalog = new PluginCatalog(_harness.Paths, _harness.State, PluginHostInfo.Current(new SemanticVersion(1, 0, 0)),
            new PluginCatalogOptions { BuiltinRoots = [_harness.BuiltinRoot] });
        _harness.ExtraModules.Add(new FsModule(new FileAccessService(), new PluginAccessPolicy(), new NoTrashService(), _harness.Paths, _harness,
            new FixedProject(_project), new NoAgents(), catalog));

        var patA = dirA.Replace("\\", "/") + "/**";
        var patB = dirB.Replace("\\", "/") + "/**";
        var manifestExtra = $$"""
            "permissions": {
              "fs": {
                "external": [
                  { "path": {{JsonValue.Create(patA).ToJsonString()}}, "access": "read", "reason": "Read A" },
                  { "path": {{JsonValue.Create(patB).ToJsonString()}}, "access": "readwrite", "reason": "Write B" }
                ]
              }
            },
            {{Commands}}
            """;

        _harness.InstalledPlugin("test.fs", $$"""
            import { commands, fs, permissions } from 'octopus';
            export function activate(ctx) {
              ctx.subscriptions.push(commands.register('test.fs.run', async () => {
                let beforeA = 'read';
                try { await fs.readText({{JsonValue.Create(fileA).ToJsonString()}}); } catch (e) { beforeA = e.code; }
                let beforeB = 'read';
                try { await fs.readText({{JsonValue.Create(fileB).ToJsonString()}}); } catch (e) { beforeB = e.code; }
                const confirmed = await permissions.request('fs.external', 'confirm paths');
                const afterA = await fs.readText({{JsonValue.Create(fileA).ToJsonString()}});
                const afterB = await fs.readText({{JsonValue.Create(fileB).ToJsonString()}});
                return { beforeA, beforeB, confirmed, afterA, afterB };
              }));
            }
            """, manifestExtra, new Dictionary<string, string> { ["fs.external"] = patB });

        // Make provenance uncertain: state record version does not match installed manifest version (0.9.0 vs 1.0.0).
        _harness.State.Update(d => d.Plugins["test.fs"].Version = "0.9.0");

        var manager = await _harness.StartAsync();
        var plugin = (await manager.EnsureActiveAsync("test.fs", TestContext.Current.CancellationToken))!;
        var result = (await CallAsync(plugin, "test.fs.run"))!;

        Assert.Equal("PermissionDenied", result["beforeA"]!.GetValue<string>());
        Assert.Equal("PermissionDenied", result["beforeB"]!.GetValue<string>());
        Assert.True(result["confirmed"]!.GetValue<bool>());
        Assert.Equal(2, consent.Asked.Count);
        Assert.Equal("from-a", result["afterA"]!.GetValue<string>());
        Assert.Equal("from-b", result["afterB"]!.GetValue<string>());
    }

    [Fact]
    public async Task Startup_with_dev_source_old_format_record_requires_confirmation()
    {
        var dirA = Path.Combine(_outside, "dev-first");
        var dirB = Path.Combine(_outside, "dev-second");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        var fileA = Path.Combine(dirA, "a.txt");
        var fileB = Path.Combine(dirB, "b.txt");
        File.WriteAllText(fileA, "dev-a");
        File.WriteAllText(fileB, "dev-b");

        var consent = new FixedConsent(answer: false);
        _harness.Consent = consent;
        var catalog = new PluginCatalog(_harness.Paths, _harness.State, PluginHostInfo.Current(new SemanticVersion(1, 0, 0)),
            new PluginCatalogOptions { BuiltinRoots = [_harness.BuiltinRoot] });
        _harness.ExtraModules.Add(new FsModule(new FileAccessService(), new PluginAccessPolicy(), new NoTrashService(), _harness.Paths, _harness,
            new FixedProject(_project), new NoAgents(), catalog));

        var patA = dirA.Replace("\\", "/") + "/**";
        var patB = dirB.Replace("\\", "/") + "/**";
        var manifestExtra = $$"""
            "permissions": {
              "fs": {
                "external": [
                  { "path": {{JsonValue.Create(patA).ToJsonString()}}, "access": "read", "reason": "Read A" },
                  { "path": {{JsonValue.Create(patB).ToJsonString()}}, "access": "readwrite", "reason": "Write B" }
                ]
              }
            },
            {{Commands}}
            """;

        _harness.InstalledPlugin("test.fs", $$"""
            import { commands, fs, permissions } from 'octopus';
            export function activate(ctx) {
              ctx.subscriptions.push(commands.register('test.fs.run', async () => {
                let beforeA = 'read';
                try { await fs.readText({{JsonValue.Create(fileA).ToJsonString()}}); } catch (e) { beforeA = e.code; }
                let beforeB = 'read';
                try { await fs.readText({{JsonValue.Create(fileB).ToJsonString()}}); } catch (e) { beforeB = e.code; }
                const requested = await permissions.request('fs.external', 'confirm dev paths');
                return { beforeA, beforeB, requested };
              }));
            }
            """, manifestExtra, new Dictionary<string, string> { ["fs.external"] = patB });

        // Dev-source record with same version must NOT auto-migrate old-format external paths.
        _harness.State.Update(d => d.Plugins["test.fs"].Source = Octopus.Core.Plugins.PluginSource.Dev);

        var manager = await _harness.StartAsync();
        var plugin = (await manager.EnsureActiveAsync("test.fs", TestContext.Current.CancellationToken))!;
        var result = (await CallAsync(plugin, "test.fs.run"))!;

        Assert.Equal("PermissionDenied", result["beforeA"]!.GetValue<string>());
        Assert.Equal("PermissionDenied", result["beforeB"]!.GetValue<string>());
        Assert.False(result["requested"]!.GetValue<bool>());
        Assert.Single(consent.Asked);
        Assert.Empty(_harness.State.Current.Find("test.fs")!.ScopedGrants);
    }

    [Fact]
    public async Task Reload_keeps_new_optional_siblings_requestable_after_declining_sole_prior_sibling()
    {
        var consent = new SelectiveReasonConsent(deniedReason: "A");
        _harness.Consent = consent;
        var package = _harness.InstalledPlugin("test.fs", """
            import { commands, permissions } from 'octopus';
            export function activate(ctx) {
              ctx.subscriptions.push(commands.register('test.fs.run', () => permissions.request('fs.external', 'fixture')));
            }
            """, $$"""
            "permissions": {
              "fs": {
                "external": [
                  { "path": "~/fixtures/a/**", "access": "read", "reason": "A", "optional": true }
                ]
              }
            },
            {{Commands}}
            """, new Dictionary<string, string>());

        var manager = await _harness.StartAsync();
        var first = await manager.ExecuteCommandAsync("test.fs.run", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(first!.GetValue<bool>());
        Assert.Equal(["A"], consent.Asked);

        var manifestFile = Path.Combine(package, "plugin.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(manifestFile, TestContext.Current.CancellationToken))!;
        var external = json["permissions"]!["fs"]!["external"]!.AsArray();
        foreach (var name in new[] { "B", "C" })
        {
            external.Add(new JsonObject
            {
                ["path"] = $"~/fixtures/{name.ToLowerInvariant()}/**",
                ["access"] = "read",
                ["reason"] = name,
                ["optional"] = true,
            });
        }
        await File.WriteAllTextAsync(manifestFile, json.ToJsonString(), TestContext.Current.CancellationToken);
        await manager.ReloadAsync("test.fs");

        var second = await manager.ExecuteCommandAsync("test.fs.run", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(second!.GetValue<bool>());
        Assert.Equal(["A", "B", "C"], consent.Asked);

        var grants = manager.Find("test.fs")!.Grants;
        Assert.True(grants.IsDenied(grants.Manifest.Permissions.Requests[0]));
        Assert.True(grants.IsGranted(grants.Manifest.Permissions.Requests[1]));
        Assert.True(grants.IsGranted(grants.Manifest.Permissions.Requests[2]));
    }

    private sealed class SelectiveReasonConsent(string deniedReason) : IPluginConsent
    {
        public List<string> Asked { get; } = [];

        public Task<bool> RequestPermissionAsync(PluginHost plugin, Octopus.Core.Plugins.PermissionRequest request, string? reason, CancellationToken cancellationToken)
        {
            Asked.Add(request.Reason ?? request.Key);
            return Task.FromResult(!string.Equals(request.Reason, deniedReason, StringComparison.Ordinal));
        }
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
