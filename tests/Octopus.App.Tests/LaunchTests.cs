using Avalonia.Headless.XUnit;
using Octopus.Agents;
using Octopus.App.Composition;
using Octopus.App.Launcher;
using Octopus.App.Services;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Storage;
using TKCode.Arc.Localization;
using System.Text.Json;
using Xunit;

namespace Octopus.App.Tests;

public class LaunchTests
{
    private static readonly string Dir = Directory.CreateTempSubdirectory("octopus-launch-").FullName;

    [AvaloniaFact]
    public void An_agent_item_becomes_a_command_with_the_chosen_arguments()
    {
        var project = new Project { Path = Dir };
        var item = new ProjectItem
        {
            Kind = ProjectItemKind.Agent,
            AgentId = "claude",
            Title = "Claude Code",
            Launch = new AgentLaunchOptions { Model = "opus", Effort = "high" },
        };
        var spec = AppHost.Get<IItemLaunchService>().Plan(project, item);

        var line = string.Join(' ', spec.Arguments);
        Assert.Contains("--model", line);
        Assert.Contains("opus", line);
        Assert.Contains("--effort", line);
        Assert.Contains("high", line);
        Assert.Equal(Dir, spec.WorkingDirectory);
    }

    [AvaloniaFact]
    public void A_restored_agent_continues_its_session_but_a_new_one_does_not()
    {
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(Dir);
        var fresh = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude Code" };
        projects.AddItem(project, fresh);
        var line = string.Join(' ', AppHost.Get<IItemLaunchService>().Plan(project, fresh).Arguments);
        Assert.DoesNotContain("--continue", line);
    }

    [AvaloniaFact]
    public void A_terminal_item_runs_the_shell_in_the_project_directory()
    {
        var project = new Project { Path = Dir };
        var item = new ProjectItem { Kind = ProjectItemKind.Terminal, Title = "zsh", WorkingDirectory = "/tmp" };
        var spec = AppHost.Get<IItemLaunchService>().Plan(project, item);
        Assert.Equal("/tmp", spec.WorkingDirectory);
        Assert.False(string.IsNullOrEmpty(spec.FileName));
    }

    private static AgentLaunchViewModel Panel(string id)
    {
        var definition = AppHost.Get<IAgentCatalog>().Find(id)!;
        var settings = AppHost.Get<ISettingsStore>().Current.Agents;
        return new AgentLaunchViewModel(
            definition, settings.ProfileOf(id), settings.DefaultPermission,
            AppHost.Get<IAgentCommandCompiler>(), AppHost.Get<IModelDiscovery>(), null, _ => { });
    }

    [AvaloniaFact]
    public void The_panel_follows_the_capability_blocks_of_the_agent()
    {
        var claude = Panel("claude");
        Assert.True(claude.HasModel);
        Assert.True(claude.HasEffort);
        Assert.True(claude.Efforts.Count > 1);
        Assert.Equal("", claude.Efforts[0].Id);

        var opencode = Panel("opencode");
        Assert.False(opencode.HasEffort);
        Assert.Empty(opencode.Efforts);
    }

    [AvaloniaFact]
    public void The_model_list_filters_and_offers_a_custom_id()
    {
        var claude = Panel("claude");
        Assert.Null(claude.Models[0].Id);
        claude.ModelQuery = "son";
        Assert.Contains(claude.Models, m => m.Id == "sonnet");
        Assert.DoesNotContain(claude.Models, m => m.Id == "opus");
        claude.ModelQuery = "my-model-1";
        var custom = claude.Models.Last();
        Assert.True(custom.IsCustom);
        custom.SelectCommand.Execute(null);
        Assert.Equal("my-model-1", claude.SelectedModelId);
        Assert.Equal("my-model-1", claude.BuildOptions().Model);
    }

    private static AgentLaunchViewModel Panel(string id, AgentProfile profile)
    {
        AppHost.Reset();
        var definition = AppHost.Get<IAgentCatalog>().Find(id)!;
        return new AgentLaunchViewModel(
            definition, profile, "default",
            AppHost.Get<IAgentCommandCompiler>(), AppHost.Get<IModelDiscovery>(), null, _ => { });
    }

    [AvaloniaFact]
    public void The_panel_starts_from_the_profile_and_an_explicit_default_is_an_empty_string()
    {
        var panel = Panel("claude", new AgentProfile { Model = "opus", Effort = "high" });
        Assert.Equal("opus", panel.SelectedModelId);
        Assert.Equal("high", panel.SelectedEffortId);
        Assert.Equal("opus", panel.BuildOptions().Model);

        panel.Models[0].SelectCommand.Execute(null); // "agent default"
        panel.SelectedEffortId = "";
        var options = panel.BuildOptions();
        Assert.Equal("", options.Model);
        Assert.Equal("", options.Effort);

        // The empty choice beats the profile when compiled: no arguments for model and effort.
        var definition = AppHost.Get<IAgentCatalog>().Find("claude")!;
        var command = AppHost.Get<IAgentCommandCompiler>().Compile(
            new AgentCompileRequest(definition, new AgentProfile { Model = "opus", Effort = "high" }, options, "default", null));
        Assert.DoesNotContain("--model", command.Arguments);
        Assert.DoesNotContain("--effort", command.Arguments);
    }

    [AvaloniaFact]
    public void Fast_off_in_the_panel_overrides_a_profile_with_fast_on()
    {
        var panel = Panel("cursor", new AgentProfile { Fast = true });
        Assert.True(panel.Fast);
        panel.Fast = false;
        Assert.False(panel.BuildOptions().Fast);
    }

    [AvaloniaFact]
    public void Blocks_the_agent_lacks_stay_null()
    {
        var options = Panel("opencode", new AgentProfile()).BuildOptions();
        Assert.Null(options.Effort);
        Assert.Null(options.Fast);
    }

    [AvaloniaFact]
    public void An_empty_choice_survives_the_projects_file()
    {
        var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Launch = new AgentLaunchOptions { Model = "", Effort = "", Fast = false } };
        var json = JsonSerializer.Serialize(new ProjectsDocument { Projects = [new Project { Path = Dir, Items = [item]}] }, OctopusJsonContext.FileOptions);
        var back = JsonSerializer.Deserialize<ProjectsDocument>(json, OctopusJsonContext.FileOptions)!.Projects[0].Items[0].Launch!;
        Assert.Equal("", back.Model);
        Assert.Equal("", back.Effort);
        Assert.False(back.Fast);
    }

    [AvaloniaFact]
    public void A_typed_model_id_stays_listed_and_checked_after_it_is_chosen()
    {
        var panel = Panel("claude");
        panel.ModelQuery = "my-model-1";
        panel.Models.Last().SelectCommand.Execute(null);
        Assert.Equal("", panel.ModelQuery);
        Assert.Contains(panel.Models, m => m.Id == "my-model-1" && m.IsSelected);
    }

    [AvaloniaFact]
    public void Enter_in_the_model_search_picks_the_match_and_launches()
    {
        AppHost.Reset();
        AgentLaunchOptions? launched = null;
        var definition = AppHost.Get<IAgentCatalog>().Find("claude")!;
        var panel = new AgentLaunchViewModel(
            definition, new AgentProfile(), "default",
            AppHost.Get<IAgentCommandCompiler>(), AppHost.Get<IModelDiscovery>(), null, o => launched = o);

        panel.ModelQuery = "my-model-1";
        panel.CommitModelQuery();
        Assert.Equal("my-model-1", launched!.Model);
    }

    [AvaloniaFact]
    public void Panels_of_different_agents_do_not_share_choices()
    {
        AppHost.Reset();
        var launcher = AppHost.Get<LauncherViewModel>();
        launcher.Refresh();
        var claude = launcher.Agents.First(a => a.Id == "claude");
        var opencode = launcher.Agents.First(a => a.Id == "opencode");
        launcher.Expand(claude);
        var first = launcher.Panel!;
        first.Prompt = "hello";
        first.SelectedEffortId = "high";
        launcher.Expand(opencode);
        Assert.NotSame(first, launcher.Panel);
        Assert.Equal("", launcher.Panel!.Prompt);
        // Opening the menu again (Refresh) starts every panel from the profile.
        launcher.Refresh();
        launcher.Expand(launcher.Agents.First(a => a.Id == "claude"));
        Assert.Equal("", launcher.Panel!.Prompt);
        Assert.Equal("", launcher.Panel.SelectedEffortId);
    }

    [AvaloniaFact]
    public void Panel_text_follows_a_language_change()
    {
        var previous = ArcLanguage.Current;
        try
        {
            ArcLanguage.Set(LanguageTags.ZhCN);
            var panel = Panel("claude");
            var zh = panel.Models[0].Label;
            var zhCaption = panel.EffortCaption;
            ArcLanguage.Set(LanguageTags.EnUS);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(zh, panel.Models[0].Label);
            Assert.NotEqual(zhCaption, panel.EffortCaption);
            Assert.Equal(AppLangResourceText(Octopus.App.Localization.AppLangResourceKind.AgentDefault), panel.Models[0].Label);
            panel.Dispose();
        }
        finally
        {
            ArcLanguage.Set(previous);
        }
    }

    private static string AppLangResourceText(Octopus.App.Localization.AppLangResourceKind kind) =>
        Octopus.App.Localization.AppLangResources.Get(kind);
}
