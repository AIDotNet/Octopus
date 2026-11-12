using Octopus.Core.Plugins;
using Octopus.Plugins.Manifests;
using Xunit;

namespace Octopus.Plugins.Tests;

public sealed class ManifestReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("octopus-manifest-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private ManifestReadResult ReadPackage(string id, string manifest, params (string, string)[] files) =>
        PluginManifestReader.ReadPackage(Package.Write(Path.Combine(_root, id), manifest, files), PluginSource.File, requireDirectoryName: true);

    private static ManifestReadResult Parse(string json) => PluginManifestReader.Parse(json, null, PluginSource.File);

    [Fact]
    public void Icon_MayNameAnAppIcon_InsteadOfAPackageFile()
    {
        var result = ReadPackage("acme.sample", Package.Manifest(extra: "\"icon\": \"lucide:gauge\""));
        Assert.Empty(result.Issues);
        Assert.Equal("lucide:gauge", result.Manifest!.Icon);
    }

    private static void AssertIssue(ManifestReadResult result, IssueSeverity severity, string path) =>
        Assert.Contains(result.Issues, i => i.Severity == severity && i.Path == path);

    // ------------------------------------------------------------ the whole manifest

    private const string FullManifest = """
        {
          // comments and trailing commas are allowed
          "schema": 1,
          "id": "acme.csv",
          "name": "CSV Table",
          "version": "1.2.0-beta.1",
          "description": "Open CSV files as a table.",
          "publisher": "acme",
          "icon": "assets/icon.svg",
          "homepage": "https://example.com",
          "engines": { "api": 0, "octopus": ">=0.1.0" },
          "main": "main.js",
          "activation": ["onProject:contains:package.json", "onAgentInstalled:codex"],
          "permissions": {
            "fs": { "project": { "access": "read", "exclude": ["**/node_modules/**"] } },
            "git": "read",
            "terminal": ["read"],
            "agents": { "signals": { "claude": ["statusline", "prompt-submit"] }, "probes": { "codex": ["rateLimits"] } },
            "notifications": { "optional": true },
          },
          "contributes": {
            "themes": [{ "id": "acme.csv.night", "label": "Night", "appearance": "dark", "path": "themes/night.json" }],
            "agents": ["agents/mytool.json"],
            "prompts": [{ "id": "acme.csv.review", "title": "Review", "text": "Review {file}", "agents": ["claude"] }],
            "agentPresets": [{ "id": "acme.csv.preset", "title": "Fast review", "agent": "claude", "effort": "low", "prompt": "acme.csv.review" }],
            "terminalProfiles": [{ "id": "acme.csv.dev", "title": "Dev server", "command": "npm", "args": ["run", "dev"], "env": { "PORT": "3000" }, "cwd": "web" }],
            "terminalLinks": [{ "pattern": "([\\w./-]+):(\\d+)", "kind": "file", "template": "$1:$2" }],
            "commands": [{ "id": "acme.csv.export", "title": "Export as JSON", "localized": { "zh-CN": { "title": "导出为 JSON" } } }],
            "views": [{ "id": "acme.csv.board", "title": "Board", "launcher": true, "singleton": "project" }],
            "editors": [{ "id": "acme.csv.table", "title": "Table", "selector": ["*.csv", "*.tsv"], "access": "read", "maxSize": 52428800 }],
            "keybindings": [
              { "command": "acme.csv.export", "mac": "cmd+shift+e", "other": "ctrl+shift+e" },
              { "command": "octopus.terminal.new", "mac": "cmd+alt+n" },
              { "command": "acme.csv.board.open", "other": "ctrl+alt+b" }
            ],
            "menus": { "editor/title": [{ "command": "acme.csv.export", "when": "editor == acme.csv.table", "order": 2 }] },
            "configuration": { "properties": {
              "acme.csv.delimiter": { "type": "enum", "options": ["auto", ",", ";"], "default": "auto", "label": "Delimiter" },
              "acme.csv.preview": { "type": "bool", "default": true, "label": "Preview" }
            } },
            "usageProviders": [{ "id": "acme.csv.usage", "agent": "codex" }],
          },
          "localized": { "zh-CN": { "name": "CSV 表格" } }
        }
        """;

    [Fact]
    public void A_complete_manifest_reads_without_errors()
    {
        var result = ReadPackage("acme.csv", FullManifest,
            ("main.js", "export function activate() {}"), ("assets/icon.svg", "<svg/>"),
            ("themes/night.json", "{}"), ("agents/mytool.json", "{}"));

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Empty(result.Issues);
        var m = result.Manifest!;
        Assert.Equal("acme.csv", m.Id);
        Assert.Equal("CSV 表格", m.NameFor("zh-CN"));
        Assert.Equal("CSV 表格", m.NameFor("zh-TW"));
        Assert.Equal("CSV Table", m.NameFor("en-US"));
        Assert.Equal(new PluginEngines(0, ">=0.1.0"), m.Engines);
        Assert.Equal(["onProject:contains:package.json", "onAgentInstalled:codex"], m.Activation);

        var c = m.Contributes;
        Assert.Equal(ThemeAppearance.Dark, Assert.Single(c.Themes).Appearance);
        Assert.Equal(["agents/mytool.json"], c.Agents);
        Assert.Equal("导出为 JSON", Assert.Single(c.Commands).Localized.Pick("zh-CN", "title"));
        Assert.Equal(ViewSingleton.Project, Assert.Single(c.Views).Singleton);
        var editor = Assert.Single(c.Editors);
        Assert.Equal(["*.csv", "*.tsv"], editor.Selector);
        Assert.Equal(EditorPriority.Default, editor.Priority);
        Assert.Equal(52428800, editor.MaxSize);
        Assert.Equal(3, c.Keybindings.Count);
        Assert.Equal(2, Assert.Single(c.Menus["editor/title"]).Order);
        Assert.Equal("\"auto\"", c.Configuration.Single(p => p.Key == "acme.csv.delimiter").DefaultJson);
        Assert.Equal("3000", Assert.Single(c.TerminalProfiles).Env["PORT"]);
        Assert.Equal(TerminalLinkKind.File, Assert.Single(c.TerminalLinks).Kind);
        Assert.False(c.IsDeclarativeOnly);
    }

    [Fact]
    public void Permissions_are_flattened_with_their_risk()
    {
        var result = ReadPackage("acme.csv", FullManifest,
            ("main.js", ""), ("assets/icon.svg", ""), ("themes/night.json", "{}"), ("agents/mytool.json", "{}"));
        var requests = result.Manifest!.Permissions.Requests.ToDictionary(r => r.Key);

        Assert.Equal(PermissionRisk.Medium, requests["fs.project"].Risk);
        Assert.Equal(["read"], requests["fs.project"].Details);
        Assert.Equal(PermissionRisk.Low, requests["git"].Risk);
        Assert.Equal(PermissionRisk.High, requests["terminal.read"].Risk);
        Assert.Equal(PermissionRisk.Medium, requests["agents.signals.claude.statusline"].Risk);
        Assert.Equal(PermissionRisk.High, requests["agents.signals.claude.prompt-submit"].Risk);
        Assert.Equal(PermissionRisk.Medium, requests["agents.probes.codex.rateLimits"].Risk);
        Assert.True(requests["notifications"].Optional);
        Assert.Equal(["**/node_modules/**"], result.Manifest.Permissions.FsProject!.Exclude);
    }

    // ------------------------------------------------------------ required fields and formats

    [Fact]
    public void Missing_basics_mean_no_manifest()
    {
        var result = Parse("""{ "schema": 1 }""");
        Assert.Null(result.Manifest);
        AssertIssue(result, IssueSeverity.Error, "$.id");
        AssertIssue(result, IssueSeverity.Error, "$.name");
        AssertIssue(result, IssueSeverity.Error, "$.version");
        AssertIssue(result, IssueSeverity.Error, "$.engines");
    }

    [Theory]
    [InlineData("Acme.csv")]
    [InlineData("acme")]
    [InlineData("acme..csv")]
    [InlineData("-acme.csv")]
    [InlineData("acme.csv-")]
    [InlineData("acme_co.csv")]
    [InlineData("acme.csv/../x")]
    public void Ids_must_look_like_publisher_dot_name(string id)
    {
        var result = Parse(Package.Manifest(id));
        Assert.False(result.IsValid);
        AssertIssue(result, IssueSeverity.Error, "$.id");
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-")]
    public void Versions_must_be_semantic(string version)
    {
        var result = Parse($$"""{ "id": "acme.x", "name": "X", "version": "{{version}}", "engines": { "api": 0 } }""");
        AssertIssue(result, IssueSeverity.Error, "$.version");
    }

    [Fact]
    public void Installed_packages_live_in_a_directory_named_after_their_id()
    {
        var result = PluginManifestReader.ReadPackage(Package.Write(Path.Combine(_root, "wrong-name"), Package.Manifest("acme.sample")), PluginSource.File, requireDirectoryName: true);
        AssertIssue(result, IssueSeverity.Error, "$.id");

        var dev = PluginManifestReader.ReadPackage(Path.Combine(_root, "wrong-name"), PluginSource.Dev, requireDirectoryName: false);
        Assert.True(dev.IsValid);
    }

    [Fact]
    public void Invalid_json_reports_the_line()
    {
        var result = Parse("{\n  \"id\": \"acme.x\",\n  oops\n}");
        Assert.Null(result.Manifest);
        Assert.Contains("line 3", Assert.Single(result.Issues).Message);
    }

    [Fact]
    public void Engines_api_must_be_a_whole_number_and_a_bad_range_is_ignored()
    {
        AssertIssue(Parse("""{ "id": "acme.x", "name": "X", "version": "1.0.0", "engines": { "api": "1" } }"""), IssueSeverity.Error, "$.engines.api");
        AssertIssue(Parse("""{ "id": "acme.x", "name": "X", "version": "1.0.0", "engines": { "api": -1 } }"""), IssueSeverity.Error, "$.engines.api");

        var range = Parse("""{ "id": "acme.x", "name": "X", "version": "1.0.0", "engines": { "api": 0, "octopus": "^1.2" } }""");
        Assert.True(range.IsValid);
        AssertIssue(range, IssueSeverity.Warning, "$.engines.octopus");
        Assert.Null(range.Manifest!.Engines.Octopus);
    }

    // ------------------------------------------------------------ leniency

    [Fact]
    public void Unknown_fields_kinds_and_activations_are_warnings()
    {
        var result = Parse(Package.Manifest(extra: """
            "futureField": 1,
            "activation": ["onStartup", "onSomethingNew"],
            "contributes": { "hologram": [] }
            """));
        Assert.True(result.IsValid);
        AssertIssue(result, IssueSeverity.Warning, "$.futureField");
        AssertIssue(result, IssueSeverity.Warning, "$.contributes.hologram");
        AssertIssue(result, IssueSeverity.Warning, "$.activation");
        Assert.Equal(["onStartup"], result.Manifest!.Activation);
    }

    [Fact]
    public void Only_plugins_shipped_with_Octopus_can_be_builtin()
    {
        var file = Parse(Package.Manifest(extra: "\"builtin\": true"));
        Assert.False(file.Manifest!.Builtin);
        AssertIssue(file, IssueSeverity.Warning, "$.builtin");

        var builtin = PluginManifestReader.Parse(Package.Manifest(extra: "\"builtin\": true"), null, PluginSource.Builtin);
        Assert.True(builtin.Manifest!.Builtin);
    }

    // ------------------------------------------------------------ contributions

    [Fact]
    public void Contribution_ids_must_carry_the_plugin_id()
    {
        var result = Parse(Package.Manifest(extra: """
            "main": "main.js",
            "contributes": { "commands": [
              { "id": "other.export", "title": "Steal" },
              { "id": "acme.sample.export", "title": "Export" },
              { "id": "acme.sample.export", "title": "Duplicate" },
              { "id": "acme.sample", "title": "Plugin-level" }
            ] }
            """));
        Assert.False(result.IsValid);
        AssertIssue(result, IssueSeverity.Error, "$.contributes.commands[0].id");
        AssertIssue(result, IssueSeverity.Error, "$.contributes.commands[2].id");
        Assert.Equal(["acme.sample.export", "acme.sample"], result.Manifest!.Contributes.Commands.Select(c => c.Id));
    }

    [Fact]
    public void Contributions_that_need_code_need_an_entry_module()
    {
        var code = Parse(Package.Manifest(extra: """ "contributes": { "views": [{ "id": "acme.sample.v", "title": "V" }] } """));
        AssertIssue(code, IssueSeverity.Error, "$.main");

        var data = Parse(Package.Manifest(extra: """ "contributes": { "prompts": [{ "id": "acme.sample.p", "title": "P", "text": "Hi" }] } """));
        Assert.True(data.IsValid);
        Assert.True(data.Manifest!.Contributes.IsDeclarativeOnly);
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("themes/../../outside.json")]
    [InlineData("/etc/hosts")]
    [InlineData("themes\\night.json")]
    [InlineData("C:/themes/night.json")]
    [InlineData("themes/missing.json")]
    public void Referenced_files_must_exist_inside_the_package(string path)
    {
        File.WriteAllText(Path.Combine(_root, "outside.json"), "{}");
        var result = ReadPackage("acme.sample", Package.Manifest(extra: $$"""
            "contributes": { "themes": [{ "id": "acme.sample.t", "label": "T", "appearance": "dark", "path": {{System.Text.Json.JsonSerializer.Serialize(path)}} }] }
            """), ("themes/night.json", "{}"));
        Assert.False(result.IsValid);
        AssertIssue(result, IssueSeverity.Error, "$.contributes.themes[0].path");
        Assert.Empty(result.Manifest!.Contributes.Themes);
    }

    [Fact]
    public void A_link_inside_the_package_may_not_point_out_of_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "creating symbolic links needs privileges on Windows");
        var secret = Path.Combine(_root, "secret.txt");
        File.WriteAllText(secret, "token");
        var package = Package.Write(Path.Combine(_root, "acme.sample"), Package.Manifest(extra: "\"main\": \"main.js\""));
        File.CreateSymbolicLink(Path.Combine(package, "main.js"), secret);

        var result = PluginManifestReader.ReadPackage(package, PluginSource.File, requireDirectoryName: true);
        AssertIssue(result, IssueSeverity.Error, "$.main");
    }

    [Fact]
    public void A_link_that_stays_inside_the_package_is_fine_even_through_linked_parent_directories()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "creating symbolic links needs privileges on Windows");
        var package = Package.Write(Path.Combine(_root, "acme.sample"), Package.Manifest(extra: "\"main\": \"main.js\""), ("dist/main.js", "export function activate() {}"));
        // An absolute target written through the temp path as the system reports it (on macOS /var is a link to /private/var).
        File.CreateSymbolicLink(Path.Combine(package, "main.js"), Path.Combine(package, "dist", "main.js"));

        var result = PluginManifestReader.ReadPackage(package, PluginSource.File, requireDirectoryName: true);
        Assert.True(result.IsValid, string.Join("\n", result.Issues));
    }

    [Fact]
    public void Configuration_is_checked_against_its_type()
    {
        var result = Parse(Package.Manifest(extra: """
            "contributes": { "configuration": { "properties": {
              "other.key": { "type": "string", "label": "Foreign" },
              "acme.sample.mode": { "type": "enum", "label": "Mode" },
              "acme.sample.count": { "type": "number", "default": "3", "label": "Count" },
              "acme.sample.token": { "type": "secret", "default": "abc", "label": "Token" },
              "acme.sample.fine": { "type": "list", "default": ["a"], "label": "Fine" }
            } } }
            """));
        AssertIssue(result, IssueSeverity.Error, "$.contributes.configuration.properties.other.key");
        AssertIssue(result, IssueSeverity.Error, "$.contributes.configuration.properties.acme.sample.mode.options");
        AssertIssue(result, IssueSeverity.Error, "$.contributes.configuration.properties.acme.sample.count.default");
        AssertIssue(result, IssueSeverity.Error, "$.contributes.configuration.properties.acme.sample.token.default");
        Assert.Equal(["acme.sample.fine"], result.Manifest!.Contributes.Configuration.Select(p => p.Key));
    }

    [Fact]
    public void Keybindings_to_unknown_commands_are_warnings()
    {
        var result = Parse(Package.Manifest(extra: """
            "contributes": { "keybindings": [
              { "command": "someone.else.command", "mac": "cmd+k" },
              { "command": "octopus.tab.close", "mac": "cmd+alt+w" },
              { "command": "acme.sample.x" }
            ] }
            """));
        AssertIssue(result, IssueSeverity.Warning, "$.contributes.keybindings[0].command");
        Assert.DoesNotContain(result.Issues, i => i.Path == "$.contributes.keybindings[1].command");
        AssertIssue(result, IssueSeverity.Error, "$.contributes.keybindings[2]");
    }

    [Fact]
    public void Terminal_links_must_be_valid_expressions_and_profiles_stay_in_the_project()
    {
        var result = Parse(Package.Manifest(extra: """
            "contributes": {
              "terminalLinks": [{ "pattern": "([a-z", "kind": "url", "template": "$0" }],
              "terminalProfiles": [{ "id": "acme.sample.p", "title": "P", "command": "ls", "cwd": "/etc" }]
            }
            """));
        AssertIssue(result, IssueSeverity.Error, "$.contributes.terminalLinks[0].pattern");
        AssertIssue(result, IssueSeverity.Error, "$.contributes.terminalProfiles[0].cwd");
    }

    // ------------------------------------------------------------ permissions

    [Fact]
    public void Permission_shapes_and_their_errors()
    {
        var result = Parse(Package.Manifest(extra: """
            "unsafeEval": true,
            "permissions": {
              "git": "write",
              "net": { "hosts": [] },
              "process": { "run": ["git"] },
              "clipboard": ["read", "paste"],
              "fs": {
                "external": [{ "path": "~/.config/tool/**", "access": "read" }, { "path": "relative/x", "reason": "r" }],
                "agentData": [{ "agent": "claude", "include": ["projects/**/*.jsonl"], "access": "readwrite", "reason": "r" }]
              },
              "camera": true
            }
            """));
        AssertIssue(result, IssueSeverity.Error, "$.permissions.git");
        AssertIssue(result, IssueSeverity.Error, "$.permissions.net.hosts");
        AssertIssue(result, IssueSeverity.Error, "$.permissions.clipboard");
        AssertIssue(result, IssueSeverity.Error, "$.permissions.fs.external[0].reason");
        AssertIssue(result, IssueSeverity.Error, "$.permissions.fs.external[1].path");
        AssertIssue(result, IssueSeverity.Warning, "$.permissions.camera");

        var requests = result.Manifest!.Permissions.Requests.ToDictionary(r => r.Key);
        Assert.Equal(PermissionRisk.High, requests["process.run"].Risk);
        Assert.Equal(["git"], requests["process.run"].Details);
        Assert.Equal(PermissionRisk.High, requests["fs.agentData.claude"].Risk);
        Assert.Equal(PermissionRisk.High, requests["unsafeEval"].Risk);
        Assert.Equal(PermissionRisk.Medium, requests["clipboard.read"].Risk);
    }

    [Fact]
    public void Agents_permission_also_accepts_the_short_array_form()
    {
        var result = Parse(Package.Manifest(extra: """ "permissions": { "agents": ["launch"] } """));
        Assert.True(result.IsValid);
        Assert.Equal(PermissionRisk.High, Assert.Single(result.Manifest!.Permissions.Requests).Risk);
    }
}

public class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("0.0.0", 0, 0, 0, null)]
    [InlineData("1.0.0-beta.2+build.7", 1, 0, 0, "beta.2")]
    public void Parses(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(SemanticVersion.TryParse(text, out var v));
        Assert.Equal(new SemanticVersion(major, minor, patch, pre), v);
    }

    [Fact]
    public void Orders_releases_after_their_prereleases()
    {
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0"];
        var parsed = ordered.Select(s => { SemanticVersion.TryParse(s, out var v); return v; }).ToList();
        for (var i = 1; i < parsed.Count; i++) Assert.True(parsed[i - 1] < parsed[i], $"{ordered[i - 1]} < {ordered[i]}");
    }

    [Theory]
    [InlineData(">=0.4.0", "0.4.0", true)]
    [InlineData(">=0.4.0", "0.3.9", false)]
    [InlineData(">=0.4.0 <2.0.0", "1.9.9", true)]
    [InlineData(">=0.4.0 <2.0.0", "2.0.0", false)]
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("=1.2.3", "1.2.4", false)]
    public void Ranges(string range, string version, bool expected)
    {
        Assert.True(VersionRange.TryParse(range, out var r));
        SemanticVersion.TryParse(version, out var v);
        Assert.Equal(expected, r.Contains(v));
    }

    [Theory]
    [InlineData("^1.2.0")]
    [InlineData("~1.2")]
    [InlineData("1.x")]
    [InlineData("")]
    public void Unsupported_ranges_are_refused(string range) => Assert.False(VersionRange.TryParse(range, out _));
}
