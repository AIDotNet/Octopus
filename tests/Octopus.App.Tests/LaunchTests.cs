using System.Text.Json;
using Avalonia.Headless.XUnit;
using Octopus.Agents;
using Octopus.App.Composition;
using Octopus.App.Launcher;
using Octopus.App.Services;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Storage;
using TKCode.Arc.Localization;
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
    public void Continuing_a_chosen_session_passes_its_id_and_account_env()
    {
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(Dir);
        var claude = new ProjectItem
        {
            Kind = ProjectItemKind.Agent,
            AgentId = "claude",
            Title = "Claude Code",
            Launch = new AgentLaunchOptions
            {
                SessionId = "sess-123",
                Env = new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = "/tmp/claude-work" },
            },
        };
        projects.AddItem(project, claude);
        var spec = AppHost.Get<IItemLaunchService>().Plan(project, claude);
        var line = string.Join(' ', spec.Arguments);
        Assert.Contains("--resume", line);
        Assert.Contains("sess-123", line);
        Assert.Equal("/tmp/claude-work", spec.Environment["CLAUDE_CONFIG_DIR"]);

        var agy = new ProjectItem
        {
            Kind = ProjectItemKind.Agent,
            AgentId = "agy",
            Title = "Antigravity CLI",
            Launch = new AgentLaunchOptions { SessionId = "conv-456" },
        };
        projects.AddItem(project, agy);
        var agyLine = string.Join(' ', AppHost.Get<IItemLaunchService>().Plan(project, agy).Arguments);
        Assert.Contains("--conversation", agyLine);
        Assert.Contains("conv-456", agyLine);

        // Fallback (allowResume: false) drops the session id but keeps the account environment.
        var fallback = AppHost.Get<IItemLaunchService>().Plan(project, claude, allowResume: false);
        Assert.DoesNotContain("--resume", string.Join(' ', fallback.Arguments));
        Assert.Equal("/tmp/claude-work", fallback.Environment["CLAUDE_CONFIG_DIR"]);
    }

    [AvaloniaFact]
    public void A_terminal_item_runs_the_shell_in_the_project_directory()
    {
        var project = new Project { Path = Dir };
        var item = new ProjectItem { Kind = ProjectItemKind.Terminal, Title = "zsh", WorkingDirectory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) };
        var spec = AppHost.Get<IItemLaunchService>().Plan(project, item);
        Assert.Equal(item.WorkingDirectory, spec.WorkingDirectory);
        Assert.False(string.IsNullOrEmpty(spec.FileName));
    }

    [AvaloniaFact]
    public void An_agent_item_with_whitespace_working_directory_falls_back_to_project_path()
    {
        var project = new Project { Path = Dir };
        var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude Code", WorkingDirectory = "   " };
        var spec = AppHost.Get<IItemLaunchService>().Plan(project, item);
        Assert.Equal(Dir, spec.WorkingDirectory);
    }

    [AvaloniaFact, FreshApp]
    public void A_group_with_merged_workspaces_passes_sibling_local_folders_to_supported_agents()
    {
        var projects = AppHost.Get<IProjectService>();
        var dirA = Directory.CreateTempSubdirectory("octopus-merge-a-").FullName;
        var dirB = Directory.CreateTempSubdirectory("octopus-merge-b-").FullName;
        var dirMissing = Path.Combine(Path.GetTempPath(), "octopus-merge-missing-" + Guid.NewGuid().ToString("N"));
        var a = projects.AddProject(dirA, "A");
        var b = projects.AddProject(dirB, "B");
        var missing = projects.AddProject(dirMissing, "Missing");
        var ssh = projects.AddProject("/srv/remote", "RemoteSsh", sshHost: "prod");
        var group = projects.AddGroup("Stack", [a.Id, b.Id, missing.Id, ssh.Id]);

        var claude = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude Code" };
        var codex = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Codex CLI" };
        var opencode = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "OpenCode" };
        projects.AddItem(a, claude);
        projects.AddItem(a, codex);
        projects.AddItem(a, opencode);

        var launcher = AppHost.Get<IItemLaunchService>();

        // Disabled by default: no --add-dir.
        var offLine = string.Join(' ', launcher.Plan(a, claude).Arguments);
        Assert.DoesNotContain("--add-dir", offLine);

        // Enabled: sibling local existing folder is added; own cwd, SSH and missing folders are excluded.
        projects.SetGroupMergeWorkspaces(group.Id, true);
        var claudeSpec = launcher.Plan(a, claude);
        var claudeLine = string.Join(' ', claudeSpec.Arguments);
        var addDirSegment = claudeLine[claudeLine.IndexOf("--add-dir", StringComparison.Ordinal)..];
        Assert.Contains("--add-dir", claudeLine);
        Assert.Contains(dirB, addDirSegment);
        Assert.DoesNotContain(dirA, addDirSegment);
        Assert.DoesNotContain(dirMissing, addDirSegment);
        Assert.DoesNotContain("/srv/remote", addDirSegment);
        Assert.False(claudeSpec.Environment.ContainsKey("CLAUDE_CODE_ADDITIONAL_DIRECTORIES_CLAUDE_MD"));

        // Enabling the setting passes CLAUDE_CODE_ADDITIONAL_DIRECTORIES_CLAUDE_MD=1.
        AppHost.Get<ISettingsStore>().Update(s =>
            s.Agents.Profiles["claude"] = new AgentProfile { Features = { ["additionalDirsClaudeMd"] = "true" } });
        Assert.Equal("1", launcher.Plan(a, claude).Environment["CLAUDE_CODE_ADDITIONAL_DIRECTORIES_CLAUDE_MD"]);

        var codexLine = string.Join(' ', launcher.Plan(a, codex).Arguments);
        Assert.Contains("--add-dir", codexLine);
        Assert.Contains(dirB, codexLine);

        // Agents without multi-dir support launch cleanly without extra flags.
        var opencodeLine = string.Join(' ', launcher.Plan(a, opencode).Arguments);
        Assert.DoesNotContain("--add-dir", opencodeLine);
        Assert.DoesNotContain(dirB, opencodeLine);
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
        AppHost.Reset();
        var definition = new AgentDefinition
        {
            Id = "fast-test",
            Name = "Fast Test",
            Capabilities = new AgentCapabilities
            {
                Model = new ModelCapability { Args = ["--model", "{value}"] },
                Fast = new FastCapability { ModelParam = "fast=true" },
            },
        };
        var panel = new AgentLaunchViewModel(
            definition, new AgentProfile { Fast = true }, "default",
            AppHost.Get<IAgentCommandCompiler>(), AppHost.Get<IModelDiscovery>(), null, _ => { });
        Assert.True(panel.HasFast);
        Assert.True(panel.Fast);
        panel.Fast = false;
        Assert.False(panel.BuildOptions().Fast);
    }

    [AvaloniaFact]
    public void Cursor_panel_hides_effort_and_fast_because_models_embed_them()
    {
        var cursor = Panel("cursor");
        Assert.True(cursor.HasModel);
        Assert.False(cursor.HasEffort);
        Assert.False(cursor.HasFast);
        Assert.Empty(cursor.Efforts);
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
        var json = JsonSerializer.Serialize(new ProjectsDocument { Projects = [new Project { Path = Dir, Items = [item] }] }, OctopusJsonContext.FileOptions);
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

    [AvaloniaFact]
    public void Codex_launcher_panel_displays_discovered_third_party_models_ahead_of_static_defaults()
    {
        var discovery = AppHost.Get<FakeModelDiscovery>();
        discovery.ModelsByAgent["codex"] =
        [
            new AgentOption { Id = "deepseek-v4-pro", Label = "DeepSeek-V4-Pro · DeepSeek" },
            new AgentOption { Id = "gemini-4-argon", Label = "Gemini 4 Argon · 4sapi" },
        ];
        var profile = new AgentProfile
        {
            Env = { ["OPENAI_BASE_URL"] = "http://127.0.0.1:3425/backend-api/codex" },
        };
        var panel = new AgentLaunchViewModel(
            AppHost.Get<IAgentCatalog>().Find("codex")!,
            profile,
            PermissionModes.Default,
            AppHost.Get<IAgentCommandCompiler>(),
            discovery,
            "/usr/local/bin/codex",
            _ => { });
        try
        {
            Assert.True(Frames.Until(() => panel.Models.Any(m => m.Id == "deepseek-v4-pro")));
            // Models[0] is AgentDefault (null); discovered third-party models come immediately after it!
            Assert.Null(panel.Models[0].Id);
            Assert.Equal("deepseek-v4-pro", panel.Models[1].Id);
            Assert.Equal("DeepSeek-V4-Pro · DeepSeek", panel.Models[1].Label);
            Assert.Equal("gemini-4-argon", panel.Models[2].Id);
            Assert.Contains(discovery.Calls, c => c.AgentId == "codex"
                && c.Env is not null
                && c.Env.GetValueOrDefault("OPENAI_BASE_URL") == "http://127.0.0.1:3425/backend-api/codex");
        }
        finally
        {
            panel.Dispose();
        }
    }

    private static string AppLangResourceText(Octopus.App.Localization.AppLangResourceKind kind) =>
        Octopus.App.Localization.AppLangResources.Get(kind);
}

/// <summary>Agents and open files are remembered across restarts (when enabled); terminals stay transient.</summary>
public class StartupItemsTests
{
    [AvaloniaFact, FreshApp]
    public async Task Startup_restores_saved_agents_and_files_and_resumes_sessions()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-restore-").FullName;
        var claudeWithId = new ProjectItem
        {
            Kind = ProjectItemKind.Agent,
            AgentId = "claude",
            Title = "Fix auth bug",
            Launch = new AgentLaunchOptions { SessionId = "sess-claude-1" },
        };
        var opencode = new ProjectItem
        {
            Kind = ProjectItemKind.Agent,
            AgentId = "opencode",
            Title = "OpenCode",
        };
        var terminal = new ProjectItem { Kind = ProjectItemKind.Terminal, Title = "zsh" };
        var file = new ProjectItem { Kind = ProjectItemKind.File, Title = "a.txt", FilePath = Path.Combine(dir, "a.txt") };
        var project = new Project
        {
            Path = dir,
            Name = "p",
            ActiveItemId = claudeWithId.Id,
            DockLayout = "{}",
            Items = { claudeWithId, opencode, terminal, file },
        };
        var store = AppHost.Get<Octopus.Storage.IProjectStore>();
        await store.SaveAsync(new ProjectsDocument { Projects = { project } });

        var projects = AppHost.Get<IProjectService>();
        await projects.LoadAsync();
        var loadedProject = projects.Projects[0];
        Assert.Equal([claudeWithId.Id, opencode.Id, file.Id], loadedProject.Items.Select(i => i.Id));

        var launcher = AppHost.Get<IItemLaunchService>();
        var restoredClaude = loadedProject.Items[0];
        var restoredOpenCode = loadedProject.Items[1];

        // Exact session id uses byId; agent without an id uses continueLatest.
        Assert.True(launcher.Resumes(restoredClaude));
        var claudeArgs = string.Join(' ', launcher.Plan(loadedProject, restoredClaude).Arguments);
        Assert.Contains("--resume", claudeArgs);
        Assert.Contains("sess-claude-1", claudeArgs);
        Assert.DoesNotContain("--continue", claudeArgs);

        Assert.True(launcher.Resumes(restoredOpenCode));
        var openCodeArgs = string.Join(' ', launcher.Plan(loadedProject, restoredOpenCode).Arguments);
        Assert.Contains("--continue", openCodeArgs);

        // Turning ResumeAgentsOnRestart off suppresses both byId and continueLatest for restored tabs.
        AppHost.Get<ISettingsStore>().Update(s => s.Terminal.ResumeAgentsOnRestart = false);
        Assert.False(launcher.Resumes(restoredClaude));
        Assert.False(launcher.Resumes(restoredOpenCode));
        var freshClaudeArgs = string.Join(' ', launcher.Plan(loadedProject, restoredClaude).Arguments);
        Assert.DoesNotContain("--resume", freshClaudeArgs);
        Assert.DoesNotContain("--continue", freshClaudeArgs);
        var freshOpenCodeArgs = string.Join(' ', launcher.Plan(loadedProject, restoredOpenCode).Arguments);
        Assert.DoesNotContain("--continue", freshOpenCodeArgs);

        // Flushing persists newly added agents (without terminals).
        var addedAgent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "agy", Title = "Antigravity CLI" };
        projects.AddItem(loadedProject, addedAgent);
        projects.AddItem(loadedProject, new ProjectItem { Kind = ProjectItemKind.Terminal, Title = "zsh" });
        await projects.FlushAsync();

        var reloaded = await store.LoadAsync();
        Assert.Equal([claudeWithId.Id, opencode.Id, file.Id, addedAgent.Id], reloaded.Projects[0].Items.Select(i => i.Id));
    }

    [AvaloniaFact, FreshApp]
    public async Task Duplicate_restored_tabs_without_session_id_only_continue_latest_on_one_tab()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-dup-continue-").FullName;
        var first = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "OpenCode 1" };
        var second = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "OpenCode 2" };
        var project = new Project { Path = dir, Name = "p", ActiveItemId = first.Id, Items = { first, second } };
        var store = AppHost.Get<Octopus.Storage.IProjectStore>();
        await store.SaveAsync(new ProjectsDocument { Projects = { project } });

        var projects = AppHost.Get<IProjectService>();
        await projects.LoadAsync();
        var loaded = projects.Projects[0];
        var launcher = AppHost.Get<IItemLaunchService>();

        // Active tab owns --continue; the second tab opens fresh instead of colliding on the same session.
        Assert.True(launcher.Resumes(loaded.Items[0]));
        Assert.Contains("--continue", string.Join(' ', launcher.Plan(loaded, loaded.Items[0]).Arguments));
        Assert.False(launcher.Resumes(loaded.Items[1]));
        Assert.DoesNotContain("--continue", string.Join(' ', launcher.Plan(loaded, loaded.Items[1]).Arguments));
    }
}
