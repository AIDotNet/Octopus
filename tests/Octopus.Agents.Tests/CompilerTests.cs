using Octopus.Core.Agents;

namespace Octopus.Agents.Tests;

public class CompilerTests
{
    private static readonly AgentCommandCompiler Compiler = new(new TestShellBuilder());

    private static AgentCommand Compile(string id, AgentProfile? profile = null, AgentLaunchOptions? launch = null,
        string global = PermissionModes.Default, string? installed = null) =>
        Compiler.Compile(new AgentCompileRequest(Builtins.Get(id), profile ?? new AgentProfile(), launch, global, installed));

    private static string Preview(string id, AgentProfile? profile = null, AgentLaunchOptions? launch = null,
        string global = PermissionModes.Default, string? installed = null) =>
        Compiler.Preview(new AgentCompileRequest(Builtins.Get(id), profile ?? new AgentProfile(), launch, global, installed));

    [Fact]
    public void All_defaults_give_only_the_executable()
    {
        Assert.Equal("claude", Preview("claude"));
        Assert.Equal("cursor-agent", Preview("cursor"));
        Assert.Equal("codex", Preview("codex"));
    }

    [Fact]
    public void Installed_path_contributes_its_file_name()
    {
        Assert.Equal("claude", Compile("claude", installed: "/home/x/.npm-global/bin/claude").Executable);
        Assert.Equal("claude.exe", Compile("claude", installed: @"C:\bin\claude.exe").Executable);
    }

    [Fact]
    public void Command_override_with_effort_and_full()
    {
        var profile = new AgentProfile { Command = "rec", Effort = "xhigh", Permission = PermissionModes.Full };
        Assert.Equal("rec --effort xhigh --dangerously-skip-permissions", Preview("claude", profile));
    }

    [Fact]
    public void Command_override_with_arguments_goes_first()
    {
        var profile = new AgentProfile { Command = "rec --profile work", Model = "opus" };
        var cmd = Compile("claude", profile);
        Assert.Equal("rec", cmd.Executable);
        Assert.Equal(["--profile", "work", "--model", "opus"], cmd.Arguments);
    }

    [Fact]
    public void Launch_overrides_profile()
    {
        var profile = new AgentProfile { Model = "opus", Effort = "low" };
        var launch = new AgentLaunchOptions { Model = "sonnet", Effort = "high" };
        Assert.Equal(["--model", "sonnet", "--effort", "high"], Compile("claude", profile, launch).Arguments);
    }

    [Fact]
    public void Empty_values_mean_agent_default()
    {
        var profile = new AgentProfile { Model = "opus", Effort = "" };
        var launch = new AgentLaunchOptions { Model = "  ", Effort = null };
        // A blank launch value does not override: "" means default, which is the merged value of launch ?? profile.
        Assert.Empty(Compile("claude", new AgentProfile { Model = "", Effort = " " }).Arguments);
        Assert.Empty(Compile("claude", profile, launch).Arguments);
    }

    [Fact]
    public void Global_permission_applies_and_profile_overrides_it()
    {
        Assert.Equal(["--permission-mode", "plan"], Compile("claude", global: PermissionModes.Plan).Arguments);
        var profile = new AgentProfile { Permission = PermissionModes.AcceptEdits };
        Assert.Equal(["--permission-mode", "acceptEdits"], Compile("claude", profile, global: PermissionModes.Plan).Arguments);
    }

    [Fact]
    public void Unsupported_permission_falls_back_to_the_agents_default()
    {
        // OpenCode has no acceptEdits and no full.
        Assert.Empty(Compile("opencode", global: PermissionModes.AcceptEdits).Arguments);
        Assert.Equal(PermissionModes.Default, Compiler.ResolvePermission(Builtins.Get("opencode"), new AgentProfile(), PermissionModes.Full));
        Assert.Equal(["--agent", "plan"], Compile("opencode", global: PermissionModes.Plan).Arguments);
        Assert.Equal(["--auto"], Compile("opencode", global: PermissionModes.Auto).Arguments);
    }

    [Fact]
    public void No_permission_block_resolves_to_null()
    {
        Assert.Null(Compiler.ResolvePermission(Builtins.Get("droid"), new AgentProfile(), PermissionModes.Full));
        Assert.Empty(Compile("droid", global: PermissionModes.Full).Arguments);
    }

    [Fact]
    public void Codex_effort_is_a_config_override()
    {
        var cmd = Compile("codex", new AgentProfile { Model = "gpt-5.6-terra", Effort = "max", Permission = PermissionModes.DontAsk });
        Assert.Equal(["-m", "gpt-5.6-terra", "-c", "model_reasoning_effort=\"max\"", "-a", "never", "-s", "workspace-write"], cmd.Arguments);
    }

    [Fact]
    public void Per_model_effort_narrowing()
    {
        var def = new AgentDefinition
        {
            Id = "t",
            Capabilities = new AgentCapabilities
            {
                Model = new ModelCapability { Args = ["--model", "{value}"] },
                Effort = new EffortCapability
                {
                    Args = ["--effort", "{value}"],
                    Options = [new() { Id = "low" }, new() { Id = "high" }, new() { Id = "max" }],
                    PerModel = new() { ["haiku"] = ["low", "high"], ["*"] = ["low", "high", "max"] }
                }
            }
        };
        Assert.Equal(["low", "high"], Compiler.EffortOptionsFor(def, "haiku").Select(o => o.Id));
        Assert.Equal(["low", "high", "max"], Compiler.EffortOptionsFor(def, "other").Select(o => o.Id));
        Assert.Equal(3, Compiler.EffortOptionsFor(def, null).Count);

        AgentCommand Run(string model, string effort) => Compiler.Compile(new AgentCompileRequest(
            def, new AgentProfile { Model = model, Effort = effort }, null, "default", null));
        Assert.Equal(["--model", "haiku"], Run("haiku", "max").Arguments);
        Assert.Equal(["--model", "haiku", "--effort", "high"], Run("haiku", "high").Arguments);
        Assert.Equal(["--model", "x", "--effort", "max"], Run("x", "max").Arguments);

        def.Capabilities.Effort!.PerModel = new() { ["haiku"] = ["low"] };
        Assert.Equal(["--model", "sonnet", "--effort", "max"], Run("sonnet", "max").Arguments);
    }

    [Fact]
    public void Effort_not_in_options_is_dropped()
    {
        Assert.Equal(["--model", "opus"], Compile("claude", new AgentProfile { Model = "opus", Effort = "ultra" }).Arguments);
        Assert.Empty(Compile("opencode", new AgentProfile { Effort = "high" }).Arguments);
    }

    [Fact]
    public void Cursor_encodes_effort_and_fast_in_the_model()
    {
        var profile = new AgentProfile { Model = "claude-opus-5-5", Effort = "high", Fast = true };
        Assert.Equal(["--model", "claude-opus-5-5[effort=high,fast=true]"], Compile("cursor", profile).Arguments);
        Assert.Equal(["--model", "claude-opus-5-5[effort=high]"],
            Compile("cursor", new AgentProfile { Model = "claude-opus-5-5", Effort = "high", Fast = false }).Arguments);
        Assert.Equal(["--model", "auto"], Compile("cursor", new AgentProfile { Model = "auto" }).Arguments);
        // No model: model params are ignored and nothing is produced.
        Assert.Empty(Compile("cursor", new AgentProfile { Effort = "high", Fast = true }).Arguments);
    }

    [Fact]
    public void Cursor_permissions()
    {
        Assert.Equal(["--yolo"], Compile("cursor", global: PermissionModes.Full).Arguments);
        Assert.Equal(["--plan"], Compile("cursor", global: PermissionModes.Plan).Arguments);
        Assert.Empty(Compile("cursor", global: PermissionModes.DontAsk).Arguments);
    }

    [Fact]
    public void Opencode_prompt_is_a_flag()
    {
        var cmd = Compile("opencode", launch: new AgentLaunchOptions { Prompt = "fix it" });
        Assert.Equal(["--prompt", "fix it"], cmd.Arguments);
    }

    [Theory]
    [InlineData("say \"hi\" and 'bye'")]
    [InlineData("line one\nline two")]
    [InlineData("cost is $HOME and `whoami` $(id)")]
    [InlineData("-- starts with dashes")]
    public void Prompt_stays_one_argument(string prompt)
    {
        var cmd = Compile("claude", new AgentProfile { Model = "opus" }, new AgentLaunchOptions { Prompt = prompt });
        Assert.Equal(["--model", "opus", prompt], cmd.Arguments);
        var cmd2 = Compile("opencode", launch: new AgentLaunchOptions { Prompt = prompt });
        Assert.Equal(["--prompt", prompt], cmd2.Arguments);
    }

    [Fact]
    public void Positional_prompt_separator_and_blank_prompt()
    {
        var def = new AgentDefinition { Id = "t", Capabilities = new AgentCapabilities { Prompt = new PromptCapability { Separator = true } } };
        AgentCommand Run(string? prompt) => Compiler.Compile(new AgentCompileRequest(def, new AgentProfile(), new AgentLaunchOptions { Prompt = prompt }, "default", null));
        Assert.Equal(["--", "hello"], Run("hello").Arguments);
        Assert.Empty(Run("  \n ").Arguments);
        Assert.Empty(Run(null).Arguments);
    }

    [Fact]
    public void Resume_appends_continue_and_drops_the_prompt()
    {
        var launch = new AgentLaunchOptions { Resume = true, Prompt = "ignored" };
        Assert.Equal(["--model", "opus", "--continue"], Compile("claude", new AgentProfile { Model = "opus" }, launch).Arguments);
        // Codex: resume is a subcommand and `codex -m x resume --last` is valid (resume accepts all options too).
        Assert.Equal(["-m", "x", "resume", "--last"], Compile("codex", new AgentProfile { Model = "x" }, launch).Arguments);
        // No resume block: nothing appended and the prompt is kept out as well only when resume works.
        Assert.Equal(["hello"], Compile("droid", launch: new AgentLaunchOptions { Resume = true, Prompt = "hello" }).Arguments);
    }

    [Fact]
    public void A_chosen_session_is_resumed_by_id_with_its_account()
    {
        // Also on a restored tab (Resume): the chosen session, not the latest one.
        var launch = new AgentLaunchOptions
        {
            SessionId = "0c3e55f2",
            Resume = true,
            Prompt = "ignored",
            Env = new() { ["CLAUDE_CONFIG_DIR"] = "/home/u/.work-claude" },
        };
        var profile = new AgentProfile { Env = { ["CLAUDE_CONFIG_DIR"] = "/home/u/.claude", ["KEEP"] = "1" } };
        var claude = Compile("claude", profile, launch);
        Assert.Equal(["--resume", "0c3e55f2"], claude.Arguments);
        Assert.Equal("/home/u/.work-claude", claude.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.Equal("1", claude.Environment["KEEP"]);
        Assert.Equal(["resume", "0c3e55f2"], Compile("codex", launch: launch).Arguments);
        // An agent that cannot resume by id starts as it would have without one.
        Assert.Equal(["--continue"], Compile("auggie", launch: launch).Arguments);
    }

    [Fact]
    public void Features_bool_list_string_and_env()
    {
        var profile = new AgentProfile
        {
            Features = new()
            {
                ["appendSystemPrompt"] = "be brief",
                ["mcpConfig"] = "a.json\n\n b.json \n",
                ["strictMcp"] = "true",
                ["settings"] = "  "
            }
        };
        Assert.Equal(
            ["--append-system-prompt", "be brief", "--mcp-config", "a.json", "--mcp-config", "b.json", "--strict-mcp-config"],
            Compile("claude", profile).Arguments);

        Assert.Empty(Compile("claude", new AgentProfile { Features = new() { ["strictMcp"] = "false" } }).Arguments);

        var codex = Compile("codex", new AgentProfile
        {
            Features = new() { ["codexHome"] = "/work/.codex", ["search"] = "true", ["profile"] = "work" }
        });
        Assert.Equal(["--search", "--profile", "work"], codex.Arguments);
        Assert.Equal("/work/.codex", codex.Environment["CODEX_HOME"]);
    }

    [Fact]
    public void Base_args_extra_args_and_order()
    {
        var profile = new AgentProfile
        {
            Model = "m",
            ExtraArgs = ["--foo", "bar baz"],
            Features = new() { ["yesAlways"] = "true" }
        };
        var cmd = Compile("aider", profile);
        Assert.Equal(["--model", "m", "--yes-always", "--foo", "bar baz"], cmd.Arguments);
        Assert.Equal(["chat", "--model", "x"], Compile("kiro", new AgentProfile { Model = "x" }).Arguments);
    }

    [Fact]
    public void Environment_merge_order()
    {
        var def = new AgentDefinition
        {
            Id = "t",
            Capabilities = new AgentCapabilities
            {
                BaseEnv = new() { ["A"] = "base", ["B"] = "base", ["C"] = "base", ["D"] = "base" },
                Permission = new PermissionCapability
                {
                    Modes = new()
                    {
                        ["default"] = new PermissionModeSpec { Env = new() { ["B"] = "perm", ["C"] = "perm", ["D"] = "perm" } }
                    }
                },
                Features = [new AgentFeature { Id = "f", Type = "string", Env = "C" }, new AgentFeature { Id = "g", Type = "string", Env = "D" }]
            }
        };
        var profile = new AgentProfile
        {
            Features = new() { ["f"] = "feat", ["g"] = "feat" },
            Env = new() { ["D"] = "profile" }
        };
        var env = Compiler.Compile(new AgentCompileRequest(def, profile, null, "default", null)).Environment;
        Assert.Equal("base", env["A"]);
        Assert.Equal("perm", env["B"]);
        Assert.Equal("feat", env["C"]);
        Assert.Equal("profile", env["D"]);
    }

    [Fact]
    public void Muse_sets_no_auto_update_and_goose_full_uses_env()
    {
        Assert.Equal("1", Compile("muse").Environment["MUSE_NO_AUTO_UPDATE"]);
        var goose = Compile("goose", global: PermissionModes.Full);
        Assert.Equal(["session"], goose.Arguments);
        Assert.Equal("auto", goose.Environment["GOOSE_MODE"]);
    }

    [Fact]
    public void Option_args_replace_the_template_and_template_can_wrap_the_value()
    {
        var def = new AgentDefinition
        {
            Id = "t",
            Capabilities = new AgentCapabilities
            {
                Model = new ModelCapability
                {
                    Args = ["--model=pre-{value}-post"],
                    Options = [new() { Id = "special", Args = ["--special-model"] }]
                }
            }
        };
        AgentCommand Run(string model) => Compiler.Compile(new AgentCompileRequest(def, new AgentProfile { Model = model }, null, "default", null));
        Assert.Equal(["--special-model"], Run("special").Arguments);
        Assert.Equal(["--model=pre-x-post"], Run("x").Arguments);
    }

    [Fact]
    public void Extra_args_ignored_when_definition_disallows_them()
    {
        var def = new AgentDefinition { Id = "t", Capabilities = new AgentCapabilities { ExtraArgs = false } };
        var cmd = Compiler.Compile(new AgentCompileRequest(def, new AgentProfile { ExtraArgs = ["--x"] }, null, "default", null));
        Assert.Empty(cmd.Arguments);
        Assert.Equal("t", cmd.Executable);
    }

    [Fact]
    public void Preview_quotes_for_reading()
    {
        var p = Preview("claude", new AgentProfile { Features = new() { ["appendSystemPrompt"] = "be brief" } });
        Assert.Equal("claude --append-system-prompt 'be brief'", p);
    }

    [Fact]
    public void An_empty_launch_choice_overrides_the_profile_and_produces_no_argument()
    {
        var profile = new AgentProfile { Model = "opus", Effort = "high" };
        Assert.Equal(["--model", "opus", "--effort", "high"], Compile("claude", profile).Arguments);
        Assert.Empty(Compile("claude", profile, new AgentLaunchOptions { Model = "", Effort = "" }).Arguments);
        // null means "no choice": the profile stays.
        Assert.Equal(["--model", "opus", "--effort", "high"], Compile("claude", profile, new AgentLaunchOptions()).Arguments);
        Assert.Equal(["--effort", "high"], Compile("claude", profile, new AgentLaunchOptions { Model = "" }).Arguments);
    }

    [Fact]
    public void Fast_false_overrides_a_profile_that_turned_it_on()
    {
        var profile = new AgentProfile { Model = "auto", Fast = true };
        Assert.Contains("fast=true", string.Join(' ', Compile("cursor", profile).Arguments));
        var off = Compile("cursor", profile, new AgentLaunchOptions { Fast = false }).Arguments;
        Assert.Equal(["--model", "auto"], off.Take(2));
        Assert.DoesNotContain("fast", string.Join(' ', off));
    }
}
