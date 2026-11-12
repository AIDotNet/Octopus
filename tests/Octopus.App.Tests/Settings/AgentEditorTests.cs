using Avalonia.Headless.XUnit;
using Octopus.Agents;
using Octopus.App.Composition;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.App.Settings.Agents;
using Octopus.Core.Agents;
using Octopus.Platform;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests.Settings;

/// <summary>Agents are added, changed and removed from the settings page; the editor keeps what it has no field for.</summary>
public class AgentEditorTests
{
    private static IAgentDefinitionStore Store => AppHost.Get<IAgentDefinitionStore>();
    private static IAgentCatalog Catalog => AppHost.Get<IAgentCatalog>();

    private static AgentsSettingsViewModel Page(params string[] installed) => new(
        AppHost.Get<ISettingsStore>(),
        Catalog,
        new SettingsTestSupport.FakeScanner(installed),
        AppHost.Get<IAgentCommandCompiler>(),
        AppHost.Get<IModelDiscovery>(),
        AppHost.Get<IFileOpener>(),
        new StoragePathPicker(),
        Store,
        AppHost.Get<IDialogService>());

    [Theory]
    [InlineData("--model {value}", new[] { "--model", "{value}" })]
    [InlineData("  -m   x ", new[] { "-m", "x" })]
    [InlineData("--name \"two words\" 'single q'", new[] { "--name", "two words", "single q" })]
    [InlineData("--empty \"\"", new[] { "--empty", "" })]
    [InlineData("\"a \\\"quoted\\\" b\"", new[] { "a \"quoted\" b" })]
    [InlineData("", new string[0])]
    public void Arguments_split_and_join_back(string text, string[] args)
    {
        Assert.Equal(args, ArgText.Split(text));
        Assert.Equal(args, ArgText.Split(ArgText.Join(args)));
    }

    [AvaloniaFact]
    public void A_new_agent_gets_an_id_from_its_name_and_is_checked_before_saving()
    {
        var model = new AgentEditorModel(Store, null, ["claude"]);
        Assert.True(model.IsNew);
        model.Name = "My Agent";
        Assert.Equal("my-agent", model.Id);

        // Missing executable, then a model template without {value}.
        Assert.Null(model.Build());
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.AgentEditorNeedExecutable), model.Error);
        model.Executables = "my-agent ma";
        model.ModelArgs = "--model";
        Assert.Null(model.Build());
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.AgentEditorModelNeedsValue), model.Error);
        model.ModelArgs = "--model {value}";
        model.ModelOptions = "fast\nsmart\nfast";
        model.PromptMode = AgentEditorModel.PromptFlag;
        model.PromptArgs = "-p {value}";
        model.ResumeArgs = "--continue";
        model.Environment = "MY_TOKEN=abc\nEMPTY=\n=bad";

        var definition = model.Build();
        Assert.NotNull(definition);
        Assert.Null(model.Error);
        Assert.Equal(["my-agent", "ma"], definition!.Detect.Executables);
        Assert.Equal(["--model", "{value}"], definition.Capabilities.Model!.Args);
        Assert.Equal(["fast", "smart"], definition.Capabilities.Model.Options.Select(o => o.Id));
        Assert.Equal("flag", definition.Capabilities.Prompt!.Mode);
        Assert.Equal(["--continue"], definition.Capabilities.Resume!.ContinueLatest);
        Assert.Equal(new Dictionary<string, string> { ["MY_TOKEN"] = "abc", ["EMPTY"] = "" }, definition.Capabilities.BaseEnv);

        // A typed id stays, and one taken or malformed is refused.
        model.Id = "claude";
        Assert.Null(model.Build());
        model.Id = "bad id";
        Assert.Null(model.Build());
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.AgentEditorBadId), model.Error);
        model.Name = "Renamed";
        Assert.Equal("bad id", model.Id);
    }

    [AvaloniaFact]
    public void The_json_view_holds_everything_and_the_form_keeps_what_it_does_not_show()
    {
        var claude = Catalog.Find("claude")!;
        var model = new AgentEditorModel(Store, claude, []);
        Assert.True(model.IsBuiltIn);
        Assert.False(model.CanEditId);

        // The form changes the name; the permission modes it has no field for survive.
        model.Name = "Claude (mine)";
        model.Mode = AgentEditorModel.JsonMode;
        Assert.Contains("\"Claude (mine)\"", model.Json);
        Assert.Contains("\"permission\"", model.Json);

        // An edit in JSON comes back to the form.
        model.Json = model.Json.Replace("\"Claude (mine)\"", "\"Claude (json)\"");
        model.Mode = AgentEditorModel.FormMode;
        Assert.Equal("Claude (json)", model.Name);
        var built = model.Build()!;
        Assert.Equal(claude.Capabilities.Permission!.Modes.Keys.Order(), built.Capabilities.Permission!.Modes.Keys.Order());

        // JSON that does not parse keeps the JSON view open and says why; the id of an agent cannot change.
        model.Mode = AgentEditorModel.JsonMode;
        model.Json = "{ not json";
        model.Mode = AgentEditorModel.FormMode;
        Assert.True(model.IsJson);
        Assert.True(model.HasError);
        model.Json = Store.ToJson(built).Replace("\"id\": \"claude\"", "\"id\": \"other\"");
        Assert.Null(model.Build());
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.AgentEditorIdChanged), model.Error);
        // The catalog's own object was never touched.
        Assert.Equal("Claude Code", Catalog.Find("claude")!.Name);
    }

    [AvaloniaFact]
    public async Task Agents_are_added_edited_and_removed_from_the_settings_page()
    {
        var dialogs = AppHost.Get<FakeDialogService>();
        dialogs.Answer = true;
        using var page = Page("claude");
        Assert.True(page.CanEditAgents);

        // Add: written to the agents folder, listed (not installed here) as custom.
        dialogs.AgentEditor = m =>
        {
            m.Name = "Local Bot";
            m.Executables = "local-bot";
            return true;
        };
        await page.AddAgentCommand.ExecuteAsync(null);
        var file = Path.Combine(AppHost.Get<IAppPaths>().DataDirectory, "agents", "local-bot.json");
        Assert.True(File.Exists(file));
        Assert.Equal("Local Bot", Catalog.Find("local-bot")!.Name);
        var row = page.NotInstalled.Single(r => r.Id == "local-bot");
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.SettingsCustomAgent), row.OriginBadge);
        Assert.True(row.CanRemove);

        // Edit a built-in agent: the user's version replaces it and is marked modified.
        dialogs.AgentEditor = m =>
        {
            m.BaseArgs = "--verbose";
            return true;
        };
        await page.EditAgentCommand.ExecuteAsync(Catalog.Find("claude"));
        Assert.Equal(["--verbose"], Catalog.Find("claude")!.Capabilities.BaseArgs);
        var claude = page.Installed.Single(r => r.Id == "claude");
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.SettingsModifiedAgent), claude.OriginBadge);
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.SettingsRestoreAgent), claude.RemoveText);

        // Cancelled: nothing changes.
        dialogs.AgentEditor = _ => false;
        await page.EditAgentCommand.ExecuteAsync(Catalog.Find("claude"));
        Assert.Equal(["--verbose"], Catalog.Find("claude")!.Capabilities.BaseArgs);

        // Restore the built-in one, delete the custom one.
        await page.RemoveAgentCommand.ExecuteAsync(Catalog.Find("claude"));
        Assert.Empty(Catalog.Find("claude")!.Capabilities.BaseArgs);
        Assert.Null(page.Installed.Single(r => r.Id == "claude").OriginBadge);
        await page.RemoveAgentCommand.ExecuteAsync(Catalog.Find("local-bot"));
        Assert.Null(Catalog.Find("local-bot"));
        Assert.False(File.Exists(file));
        Assert.DoesNotContain(page.NotInstalled, r => r.Id == "local-bot");
    }

    /// <summary>Renders the editor (light and dark) to <c>bin/shots</c> for a person to look at.</summary>
    [AvaloniaFact]
    public void Render_the_agent_editor()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "shots"));
        Directory.CreateDirectory(dir);
        foreach (var dark in new[] { false, true })
        {
            TKCode.Arc.ArcTheme.Apply(t => t.Appearance(dark ? TKCode.Arc.ArcAppearance.Dark : TKCode.Arc.ArcAppearance.Light));
            var model = new AgentEditorModel(Store, Catalog.Find("codex"), []);
            var window = new Avalonia.Controls.Window
            {
                Width = 600, Height = 640,
                Content = new Avalonia.Controls.Border { Padding = new Avalonia.Thickness(20), Child = new AgentEditorView(model) },
            };
            window.Show();
            Frames.Pump();
            Frames.Save(window, Path.Combine(dir, $"agent-editor-{(dark ? "dark" : "light")}.png"));
            window.Close();
        }
    }
}
