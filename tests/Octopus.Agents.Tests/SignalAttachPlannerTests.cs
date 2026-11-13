using System.Text.Json;
using Octopus.Agents.Signals;
using Octopus.Core.Agents;

namespace Octopus.Agents.Tests;

public sealed class SignalAttachPlannerTests : IDisposable
{
    private const string Forwarder = "/Applications/Octopus.app/Contents/MacOS/octopus-signal";
    private static readonly SignalEndpoint Endpoint = new("/tmp/octo/hub.sock", "tok123");

    private readonly TempDir _dir = new();
    private readonly SignalAttachPlanner _planner;
    private readonly string _home;
    private readonly string _project;

    public SignalAttachPlannerTests()
    {
        _planner = new SignalAttachPlanner(new TestPaths(_dir.Path));
        _home = Path.Combine(_dir.Path, "home");
        _project = Path.Combine(_dir.Path, "project");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_project);
    }

    public void Dispose() => _dir.Dispose();

    private string AttachDirectory => Path.Combine(_dir.Path, "cache", SignalAttachPlanner.AttachDirectoryName);

    private SignalAttachment Plan(AgentDefinition definition, string? version, Dictionary<string, string>? env = null, bool enabled = true, SignalEndpoint? endpoint = null) =>
        _planner.Plan(new SignalAttachRequest(
            definition,
            "item-9",
            Forwarder,
            endpoint ?? Endpoint,
            version,
            new Dictionary<string, string>(env ?? []) { ["HOME"] = _home, ["USERPROFILE"] = _home },
            _project,
            enabled));

    private static JsonElement ReadJson(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement;

    // ---- When nothing is attached ----

    [Fact]
    public void Nothing_when_disabled_unsupported_too_old_unknown_version_or_without_a_hub()
    {
        var claude = Builtins.Get("claude");
        Assert.True(Plan(claude, "2.1.322", enabled: false).IsEmpty);
        Assert.True(Plan(Builtins.Get("aider"), "1.0.0").IsEmpty);
        Assert.True(Plan(claude, "2.1.79").IsEmpty);
        Assert.True(Plan(claude, null).IsEmpty);
        Assert.True(_planner.Plan(new SignalAttachRequest(claude, "i", Forwarder, null, "2.1.322")).IsEmpty);
        Assert.True(Plan(claude, "2.1.322", endpoint: new SignalEndpoint("", "t")).IsEmpty);
        // Grok's built-in attach is off (its overlay drops status line and hooks): no token in its environment.
        Assert.True(Plan(Builtins.Get("grok"), "1.0.62").IsEmpty);
        Assert.False(Directory.Exists(AttachDirectory));
    }

    // ---- Claude ----

    [Fact]
    public void Claude_gets_settings_and_a_plugin_directory_and_the_forwarders_environment()
    {
        var attachment = Plan(Builtins.Get("claude"), "2.1.322", new() { ["CLAUDE_CONFIG_DIR"] = Path.Combine(_home, "work") });

        Assert.Equal(4, attachment.Arguments.Count);
        Assert.Equal("--settings", attachment.Arguments[0]);
        Assert.Equal("--plugin-dir", attachment.Arguments[2]);
        Assert.StartsWith(AttachDirectory, attachment.Arguments[1]);
        Assert.StartsWith(AttachDirectory, attachment.Arguments[3]);

        var env = attachment.Environment;
        Assert.Equal(Endpoint.Address, env[SignalAttachPlanner.EndpointVariable]);
        Assert.Equal("tok123", env[SignalAttachPlanner.TokenVariable]);
        Assert.Equal("item-9", env[SignalAttachPlanner.ItemVariable]);
        Assert.Equal("CLAUDE_CONFIG_DIR", env[SignalAttachPlanner.AccountVariable]);
        Assert.False(env.ContainsKey(SignalAttachPlanner.ChainVariable)); // the user has no status line
    }

    [Fact]
    public void Claude_settings_hold_only_the_status_line_pointed_at_the_forwarder()
    {
        var attachment = Plan(Builtins.Get("claude"), "2.1.322");
        var settings = ReadJson(attachment.Arguments[1]);

        var property = Assert.Single(settings.EnumerateObject());
        Assert.Equal("statusLine", property.Name);
        var statusLine = property.Value;
        Assert.Equal("command", statusLine.GetProperty("type").GetString());
        Assert.Equal(
            $"{Forwarder} statusline --agent claude --channel statusline --min-interval 2000",
            statusLine.GetProperty("command").GetString());
        Assert.Equal(30, statusLine.GetProperty("refreshInterval").GetInt32());
        Assert.DoesNotContain("tok123", File.ReadAllText(attachment.Arguments[1]));
    }

    [Fact]
    public void Claude_plugin_directory_has_the_plugin_layout_and_one_hook_per_hook_channel()
    {
        var directory = Plan(Builtins.Get("claude"), "2.1.322").Arguments[3];

        var manifest = ReadJson(Path.Combine(directory, ".claude-plugin", "plugin.json"));
        Assert.Equal("octopus-signal", manifest.GetProperty("name").GetString());
        var hooks = ReadJson(Path.Combine(directory, "hooks", "hooks.json")).GetProperty("hooks");
        var events = hooks.EnumerateObject().Select(p => p.Name).Order().ToList();
        Assert.Equal(
            ["Notification", "PermissionRequest", "PostToolUseFailure", "PreToolUse", "SessionEnd", "SessionStart", "Stop", "StopFailure",
             "SubagentStart", "SubagentStop", "UserPromptSubmit"],
            events);

        // The Agent tool only: no forwarder process for every other tool call.
        var launch = hooks.GetProperty("PreToolUse").EnumerateArray().Single();
        Assert.Equal("Agent|Task", launch.GetProperty("matcher").GetString());
        Assert.Equal($"{Forwarder} hook --agent claude --channel subagent-launch", launch.GetProperty("hooks")[0].GetProperty("command").GetString());
        Assert.Equal("Agent|Task", hooks.GetProperty("PostToolUseFailure")[0].GetProperty("matcher").GetString());

        var stop = hooks.GetProperty("Stop")[0].GetProperty("hooks")[0];
        Assert.Equal("command", stop.GetProperty("type").GetString());
        Assert.Equal($"{Forwarder} hook --agent claude --channel stop", stop.GetProperty("command").GetString());
        Assert.Equal(SignalAttachPlanner.HookTimeoutSeconds, stop.GetProperty("timeout").GetInt32());
        Assert.False(hooks.GetProperty("Stop")[0].TryGetProperty("matcher", out _));
        Assert.DoesNotContain("tok123", File.ReadAllText(Path.Combine(directory, "hooks", "hooks.json")));
    }

    [Fact]
    public void Forwarder_paths_with_spaces_are_quoted_for_the_shell()
    {
        var attachment = _planner.Plan(new SignalAttachRequest(Builtins.Get("claude"), "i", "/Users/x/My Apps/octo's/octopus-signal", Endpoint, "2.1.322"));
        var command = ReadJson(attachment.Arguments[1]).GetProperty("statusLine").GetProperty("command").GetString();
        Assert.StartsWith("'/Users/x/My Apps/octo'\\''s/octopus-signal' statusline", command);
    }

    [Fact]
    public void Generated_files_are_shared_between_launches_and_live_in_a_private_directory()
    {
        var first = Plan(Builtins.Get("claude"), "2.1.322");
        var second = _planner.Plan(new SignalAttachRequest(Builtins.Get("claude"), "item-other", Forwarder, Endpoint, "2.1.322"));
        Assert.Equal(first.Arguments, second.Arguments);
        Assert.Equal("item-other", second.Environment[SignalAttachPlanner.ItemVariable]);

        var moved = _planner.Plan(new SignalAttachRequest(Builtins.Get("claude"), "i", "/opt/octopus/octopus-signal", Endpoint, "2.1.322"));
        Assert.NotEqual(first.Arguments[1], moved.Arguments[1]);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(AttachDirectory));
        }
    }

    [Fact]
    public void Claude_chains_the_highest_priority_status_line_and_keeps_its_look()
    {
        var account = Path.Combine(_home, ".claude");
        Directory.CreateDirectory(account);
        Directory.CreateDirectory(Path.Combine(_project, ".claude"));
        var userSettings = Path.Combine(account, "settings.json");
        File.WriteAllText(userSettings, """
            // comments and trailing commas are fine in Claude's settings
            { "env": { "ANTHROPIC_API_KEY": "not-read" }, "statusLine": { "type": "command", "command": "~/.claude/user.sh", "padding": 2, }, }
            """);
        var before = File.ReadAllText(userSettings);
        var userWritten = File.GetLastWriteTimeUtc(userSettings);

        var attachment = Plan(Builtins.Get("claude"), "2.1.322");
        Assert.Equal("{\"shell\":\"~/.claude/user.sh\"}", attachment.Environment[SignalAttachPlanner.ChainVariable]);
        var statusLine = ReadJson(attachment.Arguments[1]).GetProperty("statusLine");
        Assert.Equal(2, statusLine.GetProperty("padding").GetInt32());

        File.WriteAllText(Path.Combine(_project, ".claude", "settings.json"), """{ "statusLine": { "type": "command", "command": "project.sh", "refreshInterval": 5, "hideVimModeIndicator": true } }""");
        attachment = Plan(Builtins.Get("claude"), "2.1.322");
        Assert.Equal("{\"shell\":\"project.sh\"}", attachment.Environment[SignalAttachPlanner.ChainVariable]);
        statusLine = ReadJson(attachment.Arguments[1]).GetProperty("statusLine");
        Assert.Equal(5, statusLine.GetProperty("refreshInterval").GetInt32()); // the user's faster refresh wins
        Assert.True(statusLine.GetProperty("hideVimModeIndicator").GetBoolean());

        File.WriteAllText(Path.Combine(_project, ".claude", "settings.local.json"), """{ "statusLine": { "type": "command", "command": "echo 'local'" } }""");
        attachment = Plan(Builtins.Get("claude"), "2.1.322");
        Assert.Equal("{\"shell\":\"echo 'local'\"}", attachment.Environment[SignalAttachPlanner.ChainVariable]);

        // Read only: the user's file is exactly as before.
        Assert.Equal(before, File.ReadAllText(userSettings));
        Assert.Equal(userWritten, File.GetLastWriteTimeUtc(userSettings));
    }

    [Fact]
    public void Claude_reads_the_status_line_of_the_account_in_the_agents_environment()
    {
        var work = Path.Combine(_dir.Path, "accounts", "work");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "settings.json"), """{ "statusLine": { "type": "command", "command": "work.sh" } }""");
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        File.WriteAllText(Path.Combine(_home, ".claude", "settings.json"), """{ "statusLine": { "type": "command", "command": "default.sh" } }""");

        Assert.Equal("{\"shell\":\"default.sh\"}", Plan(Builtins.Get("claude"), "2.1.322").Environment[SignalAttachPlanner.ChainVariable]);
        Assert.Equal("{\"shell\":\"work.sh\"}",
            Plan(Builtins.Get("claude"), "2.1.322", new() { ["CLAUDE_CONFIG_DIR"] = work }).Environment[SignalAttachPlanner.ChainVariable]);
    }

    // ---- Codex ----

    [Fact]
    public void Codex_gets_a_notify_override_and_chains_the_users_notify_program()
    {
        var codexHome = Path.Combine(_dir.Path, "codex-home");
        Directory.CreateDirectory(codexHome);
        File.WriteAllText(Path.Combine(codexHome, "config.toml"), """
            model = "gpt-6-luna"
            notify = [
              "python3",   # the user's own program
              '/Users/x/.codex/notify.py',
            ]

            [model_providers.custom]
            base_url = "https://example.invalid"
            notify = ["not", "this", "one"]
            """);

        var attachment = Plan(Builtins.Get("codex"), "0.167.0", new() { ["CODEX_HOME"] = codexHome });

        Assert.Equal(["-c", $"notify=[\"{Forwarder}\",\"notify\",\"--agent\",\"codex\",\"--channel\",\"turn-complete\"]"], attachment.Arguments);
        Assert.Equal("{\"argv\":[\"python3\",\"/Users/x/.codex/notify.py\"]}", attachment.Environment[SignalAttachPlanner.ChainVariable]);
        Assert.Equal("CODEX_HOME", attachment.Environment[SignalAttachPlanner.AccountVariable]);
        Assert.False(Directory.Exists(AttachDirectory)); // notify needs no files
    }

    [Fact]
    public void Codex_without_a_notify_program_chains_nothing_and_windows_paths_are_escaped_in_toml()
    {
        var attachment = _planner.Plan(new SignalAttachRequest(Builtins.Get("codex"), "i", @"C:\Program Files\Octopus\octopus-signal.exe", Endpoint, "0.167.0",
            new Dictionary<string, string> { ["HOME"] = _home, ["USERPROFILE"] = _home }));
        Assert.Equal("notify=[\"C:\\\\Program Files\\\\Octopus\\\\octopus-signal.exe\",\"notify\",\"--agent\",\"codex\",\"--channel\",\"turn-complete\"]", attachment.Arguments[1]);
        Assert.False(attachment.Environment.ContainsKey(SignalAttachPlanner.ChainVariable));

        var parsed = MiniToml.Parse(attachment.Arguments[1]);
        Assert.Equal(@"C:\Program Files\Octopus\octopus-signal.exe", ((List<object?>)parsed["notify"]!)[0]);
    }

    // ---- Cursor ----

    [Fact]
    public void Cursor_gets_a_plugin_directory_in_its_own_hooks_format_with_a_harmless_reply()
    {
        var attachment = Plan(Builtins.Get("cursor"), "2026.08.11");
        Assert.Equal("--plugin-dir", attachment.Arguments[0]);
        var directory = attachment.Arguments[1];

        Assert.True(File.Exists(Path.Combine(directory, ".cursor-plugin", "plugin.json")));
        Assert.False(Directory.Exists(Path.Combine(directory, ".claude-plugin")));
        var root = ReadJson(Path.Combine(directory, "hooks", "hooks.json"));
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        var hooks = root.GetProperty("hooks");
        Assert.Equal(["beforeSubmitPrompt", "sessionEnd", "sessionStart", "stop"], hooks.EnumerateObject().Select(p => p.Name).Order());
        var expected = OperatingSystem.IsWindows()
            ? $"{Forwarder} hook --agent cursor --channel stop --reply \"{{}}\""
            : $"{Forwarder} hook --agent cursor --channel stop --reply '{{}}'";
        Assert.Equal(expected, hooks.GetProperty("stop")[0].GetProperty("command").GetString());
        Assert.False(attachment.Environment.ContainsKey(SignalAttachPlanner.AccountVariable));
    }

    // ---- OpenCode ----

    [Fact]
    public void OpenCode_gets_config_content_that_loads_a_generated_plugin()
    {
        var attachment = Plan(Builtins.Get("opencode"), "1.19.3");
        Assert.Empty(attachment.Arguments);

        var config = JsonDocument.Parse(attachment.Environment["OPENCODE_CONFIG_CONTENT"]).RootElement;
        var url = Assert.Single(config.GetProperty("plugin").EnumerateArray()).GetString()!;
        Assert.StartsWith("file:///", url);
        var script = File.ReadAllText(new Uri(url).LocalPath);
        Assert.Contains("const AGENT = \"opencode\";", script);
        Assert.Contains("\"session.usage.updated\":\"session-usage\"", script);
        Assert.Contains("\"session.status:busy\":\"session-busy\"", script);
        Assert.Contains("OCTOPUS_SIGNAL_ENDPOINT", script);
        Assert.DoesNotContain("tok123", script);
        Assert.Equal("tok123", attachment.Environment[SignalAttachPlanner.TokenVariable]);
    }

    [Fact]
    public void OpenCode_config_content_the_user_already_sets_is_merged_not_replaced()
    {
        var mine = """{"model":"anthropic/x","plugin":["my-plugin"]}""";
        var attachment = Plan(Builtins.Get("opencode"), "1.19.3", new() { ["OPENCODE_CONFIG_CONTENT"] = mine });

        var config = JsonDocument.Parse(attachment.Environment["OPENCODE_CONFIG_CONTENT"]).RootElement;
        Assert.Equal("anthropic/x", config.GetProperty("model").GetString());
        var plugins = config.GetProperty("plugin").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Equal("my-plugin", plugins[0]);
        Assert.StartsWith("file:///", plugins[1]);

        // A value that is not a JSON object is left alone, and nothing else would attach: no signals at all.
        Assert.True(Plan(Builtins.Get("opencode"), "1.19.3", new() { ["OPENCODE_CONFIG_CONTENT"] = "not json" }).IsEmpty);
    }

    // ---- Grok (generator only; the built-in definition keeps it off) ----

    private static AgentDefinition GrokWithOverlay()
    {
        var grok = JsonSerializer.Deserialize(JsonSerializer.Serialize(Builtins.Get("grok"), AgentJsonContext.Default.AgentDefinition), AgentJsonContext.Default.AgentDefinition)!;
        grok.Signals!.Attach.Add(new SignalAttachSpec { Env = new() { ["GROK_CONFIG"] = "{signalConfig}" } });
        return grok;
    }

    [Fact]
    public void Grok_overlay_is_inline_json_with_the_status_line_and_hooks()
    {
        var grokHome = Path.Combine(_dir.Path, "grok-home");
        Directory.CreateDirectory(grokHome);
        File.WriteAllText(Path.Combine(grokHome, "config.toml"), """
            [ui]
            simple_mode = true
            status_line = { type = "command", command = "~/.grok/statusline.sh", padding = 1 }
            """);

        var attachment = Plan(GrokWithOverlay(), "1.0.62", new() { ["GROK_HOME"] = grokHome });

        var overlay = JsonDocument.Parse(attachment.Environment["GROK_CONFIG"]).RootElement;
        var statusLine = overlay.GetProperty("ui").GetProperty("status_line");
        Assert.Equal("command", statusLine.GetProperty("type").GetString());
        Assert.Equal($"{Forwarder} statusline --agent grok --channel statusline --min-interval 2000", statusLine.GetProperty("command").GetString());
        Assert.Equal(30, statusLine.GetProperty("refresh_interval").GetInt32());
        Assert.Equal(1, statusLine.GetProperty("padding").GetInt32());
        var stop = overlay.GetProperty("hooks").GetProperty("Stop")[0].GetProperty("hooks")[0];
        Assert.Equal($"{Forwarder} hook --agent grok --channel stop", stop.GetProperty("command").GetString());
        Assert.Equal("{\"shell\":\"~/.grok/statusline.sh\"}", attachment.Environment[SignalAttachPlanner.ChainVariable]);
        Assert.Equal("GROK_HOME", attachment.Environment[SignalAttachPlanner.AccountVariable]);
    }

    [Fact]
    public void Grok_overlay_merges_with_a_users_own_overlay()
    {
        var attachment = Plan(GrokWithOverlay(), "1.0.62", new() { ["GROK_CONFIG"] = """{"models":{"default_reasoning_effort":"high"}}""" });
        var overlay = JsonDocument.Parse(attachment.Environment["GROK_CONFIG"]).RootElement;
        Assert.Equal("high", overlay.GetProperty("models").GetProperty("default_reasoning_effort").GetString());
        Assert.True(overlay.TryGetProperty("ui", out _));
        Assert.False(attachment.Environment.ContainsKey(SignalAttachPlanner.ChainVariable));
    }

    // ---- Dialects and partial attaches ----

    [Fact]
    public void A_wrapper_definition_uses_claudes_formats_through_its_dialect()
    {
        var wrapper = new AgentDefinition
        {
            Id = "rec",
            Signals = new AgentSignals
            {
                Dialect = "claude",
                Attach = [new SignalAttachSpec { Args = ["--settings", "{signalSettings}"] }],
                Channels = new() { ["statusline"] = new SignalChannelSpec { Via = SignalVia.StatusLine } },
            },
        };
        var attachment = Plan(wrapper, null);
        Assert.Equal("--settings", attachment.Arguments[0]);
        Assert.Contains("--agent rec --channel statusline", File.ReadAllText(attachment.Arguments[1]));
        Assert.False(attachment.Environment.ContainsKey(SignalAttachPlanner.AccountVariable));
    }

    [Fact]
    public void An_attach_entry_whose_placeholder_cannot_be_produced_is_skipped_alone()
    {
        var definition = new AgentDefinition
        {
            Id = "partial",
            Signals = new AgentSignals
            {
                Attach =
                [
                    new SignalAttachSpec { Args = ["--settings", "{signalSettings}"] }, // not the claude dialect
                    new SignalAttachSpec { Env = new() { ["FORWARDER"] = "{forwarder}" } },
                ],
                Channels = new() { ["stop"] = new SignalChannelSpec { Via = SignalVia.Hook, Event = "Stop" } },
            },
        };
        var attachment = Plan(definition, null);
        Assert.Empty(attachment.Arguments);
        Assert.Equal(Forwarder, attachment.Environment["FORWARDER"]);
    }

    // ---- Compiler ----

    [Fact]
    public void Compiler_puts_signal_arguments_after_base_arguments_and_signal_variables_last()
    {
        var compiler = new AgentCommandCompiler(new TestShellBuilder());
        var definition = Builtins.Get("opencode");
        var profile = new AgentProfile
        {
            Model = "anthropic/x",
            Env = new() { ["OPENCODE_CONFIG_CONTENT"] = """{"plugin":["mine"]}""", ["OTHER"] = "1" },
        };
        var launch = new AgentLaunchOptions { Prompt = "hello" };
        var request = new AgentCompileRequest(definition, profile, launch, PermissionModes.Default, null);

        var plain = compiler.Compile(request);
        var attachment = Plan(definition, "1.19.3", plain.Environment.ToDictionary());
        var command = compiler.Compile(request with { Signals = attachment });

        Assert.Equal(plain.Arguments, command.Arguments); // OpenCode attaches through the environment only
        Assert.Contains("mine", command.Environment["OPENCODE_CONFIG_CONTENT"]);
        Assert.Contains("file:///", command.Environment["OPENCODE_CONFIG_CONTENT"]);
        Assert.Equal("1", command.Environment["OTHER"]);

        var claude = new AgentCompileRequest(Builtins.Get("claude"), new AgentProfile { Model = "opus" }, launch, PermissionModes.Default, null,
            new SignalAttachment(["--settings", "/s.json"], new Dictionary<string, string> { ["OCTOPUS_ITEM_ID"] = "i" }));
        var compiled = compiler.Compile(claude);
        Assert.Equal(["--settings", "/s.json", "--model", "opus", "hello"], compiled.Arguments);
        Assert.Equal("i", compiled.Environment["OCTOPUS_ITEM_ID"]);
    }
}
