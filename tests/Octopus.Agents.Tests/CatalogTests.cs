using System.Text.Json;
using Octopus.Core.Agents;

namespace Octopus.Agents.Tests;

public class CatalogTests
{
    private static readonly string[] ExpectedIds =
        ["claude", "codex", "grok", "opencode", "cursor", "muse", "gemini", "copilot",
         "amp", "droid", "aider", "qwen", "kiro", "goose", "crush", "kimi"];

    [Fact]
    public void Builtins_load_in_fixed_order_without_errors()
    {
        Assert.Empty(Builtins.All.LoadErrors);
        Assert.Equal(ExpectedIds, Builtins.All.Definitions.Select(d => d.Id));
        Assert.Equal(ExpectedIds.Length, Builtins.All.Definitions.Select(d => d.Id).Distinct().Count());
    }

    [Fact]
    public void First_six_are_verified_and_the_rest_are_not()
    {
        var defs = Builtins.All.Definitions;
        Assert.All(defs.Take(6), d => Assert.True(d.Verified, d.Id));
        Assert.All(defs.Skip(6), d => Assert.False(d.Verified, d.Id));
    }

    [Fact]
    public void Every_definition_has_executables_hints_and_homepage()
    {
        foreach (var d in Builtins.All.Definitions)
        {
            Assert.NotEmpty(d.Detect.Executables);
            Assert.False(string.IsNullOrEmpty(d.Homepage), d.Id);
            Assert.False(d.IsCustom);
        }
        // Hints are optional only for agents whose installer is unknown.
        Assert.All(Builtins.All.Definitions.Where(d => d.Id is not ("grok" or "muse")), d => Assert.NotEmpty(d.Detect.InstallHints));
    }

    [Fact]
    public void Permission_modes_are_known_and_default_exists()
    {
        foreach (var d in Builtins.All.Definitions)
        {
            var p = d.Capabilities.Permission;
            if (p is null) continue;
            Assert.All(p.Modes.Keys, k => Assert.Contains(k, PermissionModes.All));
            Assert.Contains(p.Default, p.Modes.Keys);
        }
    }

    [Fact]
    public void Templates_are_empty_or_contain_value()
    {
        foreach (var d in Builtins.All.Definitions)
        {
            var c = d.Capabilities;
            if (c.Model is not null) AssertTemplate(d.Id, c.Model.Args, c.Model.Options);
            if (c.Effort is not null) AssertTemplate(d.Id, c.Effort.Args, c.Effort.Options, c.Effort.ModelParam);
            if (c.Fast is { } f && f.Args.Count > 0) { /* fast args are value-less */ }
            foreach (var feat in c.Features)
            {
                if (feat.Args.Count > 0 && feat.Type != "bool")
                    Assert.Contains(feat.Args, a => a.Contains("{value}"));
                if (feat.Args.Count == 0)
                    Assert.False(string.IsNullOrEmpty(feat.Env), $"{d.Id}/{feat.Id}");
            }
            if (c.Prompt is { Mode: "flag" } p)
                Assert.Contains(p.Args!, a => a.Contains("{value}"));
        }
    }

    private static void AssertTemplate(string id, List<string> args, List<AgentOption> options, string? modelParam = null)
    {
        if (args.Count == 0 && modelParam is null) { return; }
        if (args.Count > 0 && options.All(o => o.Args is not null)) { return; }
        if (args.Count > 0) Assert.Contains(args, a => a.Contains("{value}"));
        if (modelParam is not null) Assert.Contains("{value}", modelParam);
    }

    [Fact]
    public void Round_trip_through_the_json_context()
    {
        var claude = Builtins.Get("claude");
        var json = JsonSerializer.Serialize(claude, AgentJsonContext.Default.AgentDefinition);
        var back = JsonSerializer.Deserialize(json, AgentJsonContext.Default.AgentDefinition)!;
        Assert.Equal(claude.Capabilities.Permission!.Modes.Count, back.Capabilities.Permission!.Modes.Count);
        Assert.True(back.Capabilities.Permission.Modes["full"].Danger);
    }

    [Fact]
    public void User_definitions_replace_builtins_and_bad_files_are_reported()
    {
        using var dir = new TempDir();
        var agents = Path.Combine(dir.Path, "data", "agents");
        Directory.CreateDirectory(agents);
        File.WriteAllText(Path.Combine(agents, "claude.json"),
            "// comment\n{ \"id\": \"claude\", \"name\": \"My Claude\", \"detect\": { \"executables\": [\"rec\",], }, }");
        File.WriteAllText(Path.Combine(agents, "mine.json"), "{ \"id\": \"mine\", \"name\": \"Mine\" }");
        File.WriteAllText(Path.Combine(agents, "broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(agents, "noid.json"), "{ \"name\": \"x\" }");

        var catalog = new AgentCatalog(new TestPaths(dir.Path));

        Assert.Equal(2, catalog.LoadErrors.Count);
        Assert.Equal("claude", catalog.Definitions[0].Id);
        Assert.Equal("My Claude", catalog.Definitions[0].Name);
        Assert.True(catalog.Definitions[0].IsCustom);
        Assert.Equal("mine", catalog.Definitions[^1].Id);
        Assert.Equal(17, catalog.Definitions.Count);
        Assert.NotNull(catalog.Find("codex"));
    }

    private sealed class FakeSource : IAgentDefinitionSource
    {
        public List<AgentDefinitionFile> List { get; } = [];
        public IReadOnlyList<AgentDefinitionFile> Files => List;
        public event EventHandler? Changed;
        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    [Fact]
    public void Plugins_add_agents_but_never_replace_one_and_the_user_folder_wins_over_them()
    {
        using var dir = new TempDir();
        var plugin = Path.Combine(dir.Path, "plugin");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "bot.json"), "{ \"id\": \"bot\", \"name\": \"Plugin Bot\", \"detect\": { \"executables\": [\"bot\"] } }");
        File.WriteAllText(Path.Combine(plugin, "claude.json"), "{ \"id\": \"claude\", \"name\": \"Hijacked\" }");
        var source = new FakeSource();
        var catalog = new AgentCatalog(new TestPaths(dir.Path), [source]);
        Assert.Null(catalog.Find("bot"));

        source.List.Add(new AgentDefinitionFile(Path.Combine(plugin, "bot.json"), "acme.bots", "Acme Bots"));
        source.List.Add(new AgentDefinitionFile(Path.Combine(plugin, "claude.json"), "acme.bots", "Acme Bots"));
        source.Raise();

        var bot = catalog.Find("bot")!;
        Assert.Equal("Plugin Bot", bot.Name);
        Assert.Equal("acme.bots", bot.Origin);
        Assert.Equal("Acme Bots", bot.OriginName);
        Assert.False(bot.IsCustom);
        Assert.Equal("Claude Code", catalog.Find("claude")!.Name);
        Assert.Contains(catalog.LoadErrors, e => e.Contains("\"claude\" already exists", StringComparison.Ordinal));

        // The user's own file of the same id wins over the plugin's.
        var agents = Path.Combine(dir.Path, "data", "agents");
        Directory.CreateDirectory(agents);
        File.WriteAllText(Path.Combine(agents, "bot.json"), "{ \"id\": \"bot\", \"name\": \"My Bot\" }");
        catalog.Reload();
        Assert.Equal("My Bot", catalog.Find("bot")!.Name);
        Assert.Null(catalog.Find("bot")!.Origin);

        // The plugin goes away: so does its agent.
        File.Delete(Path.Combine(agents, "bot.json"));
        source.List.Clear();
        source.Raise();
        Assert.Null(catalog.Find("bot"));
    }

    [Fact]
    public void Claude_narrows_haiku_efforts()
    {
        var effort = Builtins.Get("claude").Capabilities.Effort!;
        Assert.Equal(["low", "medium", "high"], effort.PerModel!["haiku"]);
        Assert.True(Builtins.Get("claude").Verified);
    }

    [Fact]
    public void Grok_effort_levels_are_marked_unconfirmed()
    {
        var options = Builtins.Get("grok").Capabilities.Effort!.Options;
        Assert.All(options, o => Assert.Equal("Unconfirmed", o.Note));
        Assert.All(options, o => Assert.Equal("未确认", o.NoteFor("zh-CN")));
    }

    [Fact]
    public void Every_feature_and_known_variable_has_chinese_text()
    {
        foreach (var d in Builtins.All.Definitions)
        {
            foreach (var f in d.Capabilities.Features)
            {
                Assert.False(string.IsNullOrEmpty(f.LabelFor("zh-CN")) || f.LabelFor("zh-CN") == f.Label, $"{d.Id}/{f.Id}");
                Assert.Equal(f.Label, f.LabelFor("en-US"));
            }
            foreach (var v in d.Capabilities.Env?.Known ?? [])
            {
                Assert.NotEqual(v.Label, v.TitleFor("zh-CN"));
                Assert.Equal(v.Label, v.TitleFor("en-US"));
            }
        }
        var claude = Builtins.Get("claude").Capabilities.Features.Single(f => f.Id == "appendSystemPrompt");
        Assert.Equal("追加系统提示词", claude.LabelFor("zh-CN"));
    }

    [Fact]
    public void Localized_text_resolves_exact_then_prefix_then_default()
    {
        var feature = new AgentFeature
        {
            Id = "x", Label = "Default", Description = "Desc",
            Localized = new()
            {
                ["zh-CN"] = new LocalizedText { Label = "简体" },
                ["fr"] = new LocalizedText { Label = "Français" },
            }
        };
        Assert.Equal("简体", feature.LabelFor("zh-CN"));
        Assert.Equal("简体", feature.LabelFor("zh-cn"));
        Assert.Equal("简体", feature.LabelFor("zh-TW"));      // same language, closest entry
        Assert.Equal("Français", feature.LabelFor("fr-CA"));  // prefix entry
        Assert.Equal("Default", feature.LabelFor("en-US"));
        Assert.Equal("Default", feature.LabelFor(null));
        Assert.Equal("Desc", feature.DescriptionFor("zh-CN")); // member not localized: default
    }

    [Fact]
    public void Localized_json_round_trips_through_the_source_generated_context()
    {
        var json = """
            { "id": "t", "capabilities": { "features": [
              { "id": "f", "label": "L", "localized": { "zh-CN": { "label": "标签", "placeholder": "占位" } } } ],
              "permission": { "modes": { "full": { "label": "Full", "localized": { "zh-CN": { "label": "全部" } } } } } } }
            """;
        var def = JsonSerializer.Deserialize(json, AgentJsonContext.Default.AgentDefinition)!;
        Assert.Equal("标签", def.Capabilities.Features[0].LabelFor("zh-CN"));
        Assert.Equal("占位", def.Capabilities.Features[0].PlaceholderFor("zh-CN"));
        Assert.Equal("全部", def.Capabilities.Permission!.Modes["full"].LabelFor("zh-CN"));
        var again = JsonSerializer.Deserialize(JsonSerializer.Serialize(def, AgentJsonContext.Default.AgentDefinition), AgentJsonContext.Default.AgentDefinition)!;
        Assert.Equal("标签", again.Capabilities.Features[0].LabelFor("zh-CN"));
    }
}
