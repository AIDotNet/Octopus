using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.App.Composition;
using Octopus.App.Settings.Agents;
using Octopus.Core.Agents;
using Octopus.Storage;
using TKCode.Arc.Localization;
using Xunit;

namespace Octopus.App.Tests.Settings;

public class AgentFieldTests
{
    public AgentFieldTests() => SettingsTestSupport.Reset();

    [AvaloniaFact]
    public void Claude_gets_a_field_for_every_capability_it_declares()
    {
        var row = SettingsTestSupport.Row("claude");

        Assert.Equal(
            ["command", "model", "effort", "permission", "extraArgs", "env",
             "feature:appendSystemPrompt", "feature:mcpConfig", "feature:strictMcp", "feature:settings"],
            row.Keys());
        Assert.IsType<CommandFieldViewModel>(row.Fields[0]);
        Assert.IsType<ChoiceFieldViewModel>(row.Field<ChoiceFieldViewModel>("model"));
        Assert.IsType<ArgsFieldViewModel>(row.Fields.Single(f => f.Key == "extraArgs"));
        Assert.IsType<EnvFieldViewModel>(row.Fields.Single(f => f.Key == "env"));
        Assert.Equal(TextFieldKind.MultiLine, row.Field<TextFieldViewModel>("feature:appendSystemPrompt").Kind);
        Assert.IsType<ArgsFieldViewModel>(row.Fields.Single(f => f.Key == "feature:mcpConfig"));
        Assert.IsType<ToggleFieldViewModel>(row.Fields.Single(f => f.Key == "feature:strictMcp"));
        Assert.Equal(TextFieldKind.File, row.Field<TextFieldViewModel>("feature:settings").Kind);
    }

    [AvaloniaFact]
    public void Claude_effort_row_is_named_after_the_definition()
    {
        var row = SettingsTestSupport.Row("claude");

        Assert.Equal("Effort", row.Field<ChoiceFieldViewModel>("effort").Label);
        Assert.Equal(["", "low", "medium", "high", "xhigh", "max"], row.Field<ChoiceFieldViewModel>("effort").OptionIds);
    }

    [AvaloniaFact]
    public void OpenCode_has_no_effort_field_and_only_its_own_permission_levels()
    {
        var row = SettingsTestSupport.Row("opencode");

        Assert.DoesNotContain("effort", row.Keys());
        Assert.Contains("model", row.Keys());
        Assert.Contains("feature:agent", row.Keys());
        Assert.Equal(["", "default", "plan", "auto"], row.Field<ChoiceFieldViewModel>("permission").OptionIds);
        Assert.True(row.Field<ChoiceFieldViewModel>("permission").Options.Count == 4);
    }

    [AvaloniaFact]
    public void Aider_has_no_permission_field_but_a_yes_always_switch()
    {
        var row = SettingsTestSupport.Row("aider");

        Assert.DoesNotContain("permission", row.Keys());
        Assert.Contains("effort", row.Keys());
        Assert.IsType<ToggleFieldViewModel>(row.Fields.Single(f => f.Key == "feature:yesAlways"));
    }

    [AvaloniaFact]
    public void Fields_follow_the_definition_even_when_it_has_no_blocks()
    {
        var definition = new AgentDefinition { Id = "bare", Name = "Bare", Capabilities = new AgentCapabilities { ExtraArgs = false } };
        var row = new AgentRowViewModel(definition, null, SettingsTestSupport.Services());

        Assert.Equal(["command"], row.Keys());
    }

    [AvaloniaFact]
    public void Changing_the_effort_updates_the_preview_and_the_saved_profile()
    {
        var row = SettingsTestSupport.Row("claude");
        var settings = AppHost.Get<ISettingsStore>();

        row.Field<ChoiceFieldViewModel>("effort").SelectedId = "xhigh";

        Assert.Contains("--effort xhigh", row.CommandPreview);
        Assert.Equal("xhigh", settings.Current.Agents.Profiles["claude"].Effort);
    }

    [AvaloniaFact]
    public void Choosing_the_default_effort_clears_it()
    {
        var row = SettingsTestSupport.Row("claude");
        var effort = row.Field<ChoiceFieldViewModel>("effort");
        effort.SelectedId = "high";

        effort.SelectedId = "";

        Assert.DoesNotContain("--effort", row.CommandPreview);
        Assert.Null(AppHost.Get<ISettingsStore>().Current.Agents.Profiles["claude"].Effort);
    }

    [AvaloniaFact]
    public void A_command_override_starts_the_preview()
    {
        var row = SettingsTestSupport.Row("claude");
        var command = row.Field<CommandFieldViewModel>("command");

        command.Text = "rec";
        command.Flush();

        Assert.StartsWith("rec ", row.CommandPreview + " ");
        Assert.Equal("rec", AppHost.Get<ISettingsStore>().Current.Agents.Profiles["claude"].Command);

        command.ResetCommand.Execute(null);
        Assert.StartsWith("claude", row.CommandPreview);
        Assert.Null(AppHost.Get<ISettingsStore>().Current.Agents.Profiles["claude"].Command);
    }

    [AvaloniaFact]
    public void The_command_placeholder_is_the_detected_file_name()
    {
        var row = SettingsTestSupport.Row("claude");

        Assert.Equal("claude", row.Field<CommandFieldViewModel>("command").Placeholder);
    }

    [AvaloniaFact]
    public void A_text_edit_is_written_after_typing_pauses_not_before()
    {
        var row = SettingsTestSupport.Row("claude");
        var settings = AppHost.Get<ISettingsStore>();
        var field = row.Field<TextFieldViewModel>("feature:appendSystemPrompt");

        field.Text = "Be brief.";

        Assert.False(settings.Current.Agents.Profiles.ContainsKey("claude"));
        field.Flush();
        Assert.Equal("Be brief.", settings.Current.Agents.Profiles["claude"].Features["appendSystemPrompt"]);
        Assert.Contains("--append-system-prompt", row.CommandPreview);
    }

    [AvaloniaFact]
    public void Extra_args_are_saved_one_per_tag()
    {
        var row = SettingsTestSupport.Row("claude");

        row.Field<ArgsFieldViewModel>("extraArgs").Value = ["--verbose", "--debug"];

        Assert.Equal(["--verbose", "--debug"], AppHost.Get<ISettingsStore>().Current.Agents.Profiles["claude"].ExtraArgs);
        Assert.EndsWith("--verbose --debug", row.CommandPreview);
    }

    [AvaloniaFact]
    public void Bool_features_store_true_and_are_removed_when_off()
    {
        var row = SettingsTestSupport.Row("claude");
        var toggle = row.Field<ToggleFieldViewModel>("feature:strictMcp");
        var settings = AppHost.Get<ISettingsStore>();

        toggle.IsOn = true;
        Assert.Equal("true", settings.Current.Agents.Profiles["claude"].Features["strictMcp"]);

        toggle.IsOn = false;
        Assert.False(settings.Current.Agents.Profiles["claude"].Features.ContainsKey("strictMcp"));
    }

    [AvaloniaFact]
    public void List_features_are_joined_with_newlines()
    {
        var row = SettingsTestSupport.Row("claude");

        row.Field<ArgsFieldViewModel>("feature:mcpConfig").Value = ["a.json", "b.json"];

        Assert.Equal("a.json\nb.json", AppHost.Get<ISettingsStore>().Current.Agents.Profiles["claude"].Features["mcpConfig"]);
    }

    [AvaloniaFact]
    public void Environment_edits_are_saved_and_secrets_are_masked()
    {
        var row = SettingsTestSupport.Row("claude");
        var env = row.Field<EnvFieldViewModel>("env");
        var settings = AppHost.Get<ISettingsStore>();

        Assert.True(env.IsEmpty);
        Assert.Contains(env.AvailableKnown, k => k.Variable.Name == "ANTHROPIC_API_KEY");

        env.AvailableKnown.Single(k => k.Variable.Name == "ANTHROPIC_API_KEY").AddCommand.Execute(null);
        var entry = Assert.Single(env.Entries);
        Assert.True(entry.IsSecret);
        Assert.DoesNotContain(env.AvailableKnown, k => k.Variable.Name == "ANTHROPIC_API_KEY");

        entry.Value = "sk-test";
        env.Flush();
        Assert.Equal("sk-test", settings.Current.Agents.Profiles["claude"].Env["ANTHROPIC_API_KEY"]);

        env.AddCustomCommand.Execute(null);
        var custom = env.Entries[1];
        Assert.False(custom.IsSecret);
        custom.Name = "FOO";
        custom.Value = "bar";
        env.Flush();
        Assert.Equal("bar", settings.Current.Agents.Profiles["claude"].Env["FOO"]);

        entry.RemoveCommand.Execute(null);
        Assert.False(settings.Current.Agents.Profiles["claude"].Env.ContainsKey("ANTHROPIC_API_KEY"));
    }

    [AvaloniaFact]
    public void A_variable_without_a_value_is_not_saved()
    {
        var row = SettingsTestSupport.Row("claude");
        var env = row.Field<EnvFieldViewModel>("env");

        env.AddCustomCommand.Execute(null);
        env.Entries[0].Name = "EMPTY";
        env.Flush();

        Assert.Empty(AppHost.Get<ISettingsStore>().Current.Agents.Profiles["claude"].Env);
    }

    [AvaloniaFact]
    public void Global_full_access_reaches_agents_without_their_own_permission()
    {
        var row = SettingsTestSupport.Row("claude");
        Assert.DoesNotContain("--dangerously-skip-permissions", row.CommandPreview);

        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultPermission = "full");
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("--dangerously-skip-permissions", row.CommandPreview);
        var permission = row.Field<ChoiceFieldViewModel>("permission");
        Assert.True(permission.IsDangerSelected);
        Assert.Contains("完全访问", permission.Options[0].Label + AppLabel("full"));
    }

    [AvaloniaFact]
    public void An_own_permission_wins_over_the_global_one()
    {
        var row = SettingsTestSupport.Row("claude");
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultPermission = "full");

        row.Field<ChoiceFieldViewModel>("permission").SelectedId = "plan";

        Assert.Contains("--permission-mode plan", row.CommandPreview);
        Assert.DoesNotContain("--dangerously-skip-permissions", row.CommandPreview);
        Assert.False(row.Field<ChoiceFieldViewModel>("permission").IsDangerSelected);
    }

    [AvaloniaFact]
    public void The_follow_global_option_names_the_level_the_agent_really_uses()
    {
        // OpenCode has no "accept edits": the global level falls back to the agent's default.
        var row = SettingsTestSupport.Row("opencode");
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultPermission = "acceptEdits");
        Dispatcher.UIThread.RunJobs();

        var first = row.Field<ChoiceFieldViewModel>("permission").Options[0].Label;

        Assert.Contains(AppLabel("default"), first);
    }

    [AvaloniaFact]
    public void A_model_that_supports_fewer_efforts_narrows_the_list_and_resets_a_dropped_level()
    {
        var row = SettingsTestSupport.Row("claude");
        var model = row.Field<ChoiceFieldViewModel>("model");
        var effort = row.Field<ChoiceFieldViewModel>("effort");
        effort.SelectedId = "max";
        Assert.Equal("max", effort.SelectedId);

        model.SelectedId = "haiku";

        Assert.Equal(["", "low", "medium", "high"], effort.OptionIds);
        Assert.Equal("", effort.SelectedId);
        Assert.Null(AppHost.Get<ISettingsStore>().Current.Agents.Profiles["claude"].Effort);

        model.SelectedId = "opus";
        Assert.Equal(["", "low", "medium", "high", "xhigh", "max"], effort.OptionIds);
    }

    [AvaloniaFact]
    public void A_custom_model_id_can_be_typed_and_shows_in_the_preview()
    {
        var row = SettingsTestSupport.Row("claude");
        var model = row.Field<ChoiceFieldViewModel>("model");

        model.SelectedId = ChoiceFieldViewModel.CustomId;
        Assert.True(model.IsCustom);
        model.CustomText = "my-model";
        model.Flush();

        Assert.Equal("my-model", AppHost.Get<ISettingsStore>().Current.Agents.Profiles["claude"].Model);
        Assert.Contains("--model my-model", row.CommandPreview);

        // A saved id that is not in the list opens as custom.
        var again = SettingsTestSupport.Row("claude");
        Assert.True(again.Field<ChoiceFieldViewModel>("model").IsCustom);
        Assert.Equal("my-model", again.Field<ChoiceFieldViewModel>("model").CustomText);
    }

    [AvaloniaFact]
    public void Enabling_and_the_default_agent_are_saved()
    {
        var row = SettingsTestSupport.Row("claude");
        var settings = AppHost.Get<ISettingsStore>();

        row.EnabledKey = "off";
        Assert.False(settings.Current.Agents.Profiles["claude"].Enabled);

        row.ToggleDefaultCommand.Execute(null);
        Assert.Equal("claude", settings.Current.Agents.DefaultAgentId);
        Assert.True(row.IsDefault);

        row.ToggleDefaultCommand.Execute(null);
        Assert.Null(settings.Current.Agents.DefaultAgentId);
        Assert.False(row.IsDefault);
    }

    [AvaloniaFact]
    public void Unverified_definitions_are_flagged()
    {
        Assert.False(SettingsTestSupport.Row("claude").IsUnverified);
        Assert.True(SettingsTestSupport.Row("aider").IsUnverified);
    }

    // The unified permission names, in the language the tests run in.
    [AvaloniaFact]
    public void Definition_text_follows_the_current_language()
    {
        var previous = ArcLanguage.Current;
        try
        {
            ArcLanguage.Set(LanguageTags.ZhCN);
            var zh = SettingsTestSupport.Row("claude");
            Assert.Equal("追加系统提示词", zh.Fields.Single(f => f.Key == "feature:appendSystemPrompt").Label);
            Assert.Equal("MCP 配置文件", zh.Fields.Single(f => f.Key == "feature:mcpConfig").Label);
            Assert.Contains(zh.Field<EnvFieldViewModel>("env").AvailableKnown, k => k.Title == "API 地址");
            Assert.Equal("Codex 主目录", SettingsTestSupport.Row("codex").Fields.Single(f => f.Key == "feature:codexHome").Label);

            ArcLanguage.Set(LanguageTags.EnUS);
            var en = SettingsTestSupport.Row("claude");
            Assert.Equal("Append system prompt", en.Fields.Single(f => f.Key == "feature:appendSystemPrompt").Label);
            Assert.Contains(en.Field<EnvFieldViewModel>("env").AvailableKnown, k => k.Title == "API base URL");
        }
        finally
        {
            ArcLanguage.Set(previous);
        }
    }

    [AvaloniaFact]
    public void An_overridden_command_is_flagged_so_the_original_name_can_be_struck_through()
    {
        var row = SettingsTestSupport.Row("claude");
        Assert.False(row.IsCommandOverridden);

        row.Field<CommandFieldViewModel>("command").Text = "rec";
        row.FlushPending();

        Assert.True(row.IsCommandOverridden);
        Assert.Equal("claude", row.DefaultCommandName);
        Assert.StartsWith("rec", row.CommandPreview);

        row.Field<CommandFieldViewModel>("command").Text = "";
        row.FlushPending();
        Assert.False(row.IsCommandOverridden);
    }

    private static string AppLabel(string id) => Octopus.App.Settings.PermissionNames.Of(id);
}
