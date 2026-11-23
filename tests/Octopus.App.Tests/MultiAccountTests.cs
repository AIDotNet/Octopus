using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.Agents;
using Octopus.Agents.Accounts;
using Octopus.App.Composition;
using Octopus.App.Launcher;
using Octopus.App.Services;
using Octopus.App.Settings.Agents;
using Octopus.App.Sidebar;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Storage;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using Xunit;

namespace Octopus.App.Tests;

public class MultiAccountTests
{
    [AvaloniaFact]
    public void Settings_agent_row_adds_renames_sets_default_and_removes_isolated_accounts()
    {
        var dialogs = AppHost.Get<FakeDialogService>();
        var accounts = AppHost.Get<IAgentAccountService>();
        var settings = AppHost.Get<ISettingsStore>();
        var claudeDef = AppHost.Get<IAgentCatalog>().Find("claude")!;

        var services = new AgentServices(
            settings,
            AppHost.Get<IAgentCommandCompiler>(),
            AppHost.Get<IModelDiscovery>(),
            AppHost.Get<IFileOpener>(),
            new FixedPathPicker(),
            accounts,
            dialogs,
            AppHost.Get<ITerminalLaunchPlanner>(),
            AppHost.Get<IProjectService>(),
            AppHost.Get<TerminalSessionRegistry>());

        using var row = new AgentRowViewModel(
            claudeDef,
            new AgentInstallation { AgentId = "claude", Path = "/usr/local/bin/claude", Version = "2.1.90" },
            services);

        Assert.True(row.HasAccountsSection);
        var section = row.AccountsSection!;
        Assert.True(section.IsEnvDir);
        Assert.Single(section.Items); // Default only

        // Add a new managed account -> runs interactive login with isolated CLAUDE_CONFIG_DIR
        dialogs.NameAnswer = "Work Max";
        section.AddManagedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, section.Items.Count);
        var added = section.Items[1];
        Assert.Equal("Work Max", added.DisplayLabel);
        Assert.Equal(1, dialogs.LoginsRun);
        Assert.Equal(added.Record.Directory, dialogs.LastLoginSpec!.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.False(dialogs.LastLoginSpec.Environment.ContainsKey("ANTHROPIC_API_KEY"));

        // Set as global default account
        added.SetDefaultCommand.Execute(null);
        Assert.Equal(added.Id, settings.Current.Agents.ProfileOf("claude").DefaultAccountId);
        Assert.Single(section.Items, i => i.IsDefault && i.Id == added.Id);

        // Rename account
        dialogs.NameAnswer = "Enterprise Max";
        added.RenameCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Enterprise Max", section.Items[1].DisplayLabel);

        // Remove account -> clears profile DefaultAccountId and removes managed directory
        var managedDir = added.Record.Directory!;
        Assert.True(Directory.Exists(managedDir));
        dialogs.Answer = true;
        section.Items[1].RemoveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(section.Items);
        Assert.Null(settings.Current.Agents.ProfileOf("claude").DefaultAccountId);
        Assert.False(Directory.Exists(managedDir));
    }

    [AvaloniaFact]
    public async Task Global_keyring_agent_switches_snapshot_automatically_when_opening_tab()
    {
        var accounts = AppHost.Get<IAgentAccountService>();
        var secrets = AppHost.Get<ISecretStore>();
        var agyDef = AppHost.Get<IAgentCatalog>().Find("agy")!;
        var slot = new ExternalKeyringSlot("gemini", "antigravity", "gemini:antigravity", "username");

        await secrets.SetExternalAsync(slot, "cred-personal");
        var personal = await accounts.CaptureLiveKeyringAsync(agyDef, "Personal");

        await secrets.SetExternalAsync(slot, "cred-work");
        var work = await accounts.CaptureLiveKeyringAsync(agyDef, "Work");
        Assert.Equal(work.Id, accounts.GetActiveGlobalAccountId("agy"));

        // Create a project bound to "Personal" account and launch agy -> ItemOpener swaps keyring to cred-personal!
        var dir = Directory.CreateTempSubdirectory("octopus-agy-proj-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir, "AgyRepo");
        projects.SetProjectAgentAccount(project.Id, "agy", personal.Id);

        var opener = AppHost.Get<IItemOpener>();
        opener.OpenAgent("agy");
        Assert.True(Frames.Until(() => accounts.GetActiveGlobalAccountId("agy") == personal.Id));
        Assert.Equal("cred-personal", await secrets.GetExternalAsync(slot));
    }

    [AvaloniaFact]
    public void Project_rail_launcher_and_panel_coordinate_project_and_per_tab_accounts()
    {
        var accounts = AppHost.Get<IAgentAccountService>();
        var settings = AppHost.Get<ISettingsStore>();
        var projects = AppHost.Get<IProjectService>();
        var claudeDef = AppHost.Get<IAgentCatalog>().Find("claude")!;

        var dirA = Directory.CreateTempSubdirectory("octopus-acc-a-").FullName;
        var dirB = Directory.CreateTempSubdirectory("octopus-acc-b-").FullName;
        var projA = projects.AddProject(dirA, "WorkRepo");
        var projB = projects.AddProject(dirB, "SideRepo");

        // With only 1 account (Default), launcher hides account picker and project chip hides AgentAccounts menu.
        var rail = AppHost.Get<ProjectRailViewModel>();
        Assert.All(rail.Chips, c => Assert.False(c.HasAgentAccounts));

        var launcher = AppHost.Get<LauncherViewModel>();
        launcher.Refresh();
        launcher.Expand(launcher.Agents.First(a => a.Id == "claude"));
        Assert.False(launcher.Panel!.HasAccounts);

        // Add two managed accounts: Work & Side
        var workAcc = accounts.CreateManaged(claudeDef, "Work");
        var sideAcc = accounts.CreateManaged(claudeDef, "Side");
        Dispatcher.UIThread.RunJobs();

        // Set global profile default to Work
        settings.Update(s => s.Agents.Profiles["claude"] = new AgentProfile { DefaultAccountId = workAcc.Id });
        Dispatcher.UIThread.RunJobs();

        // Project chip now exposes AgentAccounts menu; bind SideRepo -> Side account via chip command!
        var chipB = rail.Chips.Single(c => c.Id == projB.Id);
        Assert.True(chipB.HasAgentAccounts);
        var claudeMenu = chipB.AgentAccounts.Single(m => m.AgentId == "claude");
        Assert.Equal(4, claudeMenu.Options.Count); // Follow default + Default + Work + Side
        chipB.SetAgentAccountCommand.Execute(new ProjectAgentAccountSelection("claude", sideAcc.Id));
        Assert.Equal(sideAcc.Id, projects.Projects.Single(p => p.Id == projB.Id).AgentAccounts["claude"]);

        // Switch current project to SideRepo -> Launcher preselects Side account!
        projects.SetCurrent(projB.Id);
        Dispatcher.UIThread.RunJobs();
        launcher.Refresh();
        launcher.Expand(launcher.Agents.First(a => a.Id == "claude"));
        Assert.True(launcher.Panel!.HasAccounts);
        Assert.Equal(sideAcc.Id, launcher.Panel.SelectedAccountId);

        // Override in Launcher to launch one tab with Default account, and another tab using project's Side account
        launcher.Panel.SelectedAccountId = AgentAccountRecord.DefaultId;
        launcher.Panel.LaunchCommand.Execute(null);

        AppHost.Get<IItemOpener>().OpenAgent("claude");
        Dispatcher.UIThread.RunJobs();

        var itemsB = projects.Current!.Items.Where(i => i.Kind == ProjectItemKind.Agent).ToList();
        Assert.Equal(2, itemsB.Count);

        var launchService = AppHost.Get<IItemLaunchService>();
        var specTab1 = launchService.Plan(projects.Current, itemsB[0]);
        var specTab2 = launchService.Plan(projects.Current, itemsB[1]);

        // Tab 1 explicitly chose Default -> CLAUDE_CONFIG_DIR is NOT set in Environment, and is unset in login shell!
        Assert.False(specTab1.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
        var shellCommand = specTab1.Arguments[^1];
        Assert.True(
            shellCommand.Contains("unset", StringComparison.Ordinal) ||
            shellCommand.Contains("Remove-Item", StringComparison.Ordinal) ||
            shellCommand.Contains("set -e", StringComparison.Ordinal) ||
            shellCommand.Contains("set \"CLAUDE_CONFIG_DIR=\"", StringComparison.Ordinal),
            $"Expected shell unset wrapper in: {shellCommand}");
        Assert.Contains("CLAUDE_CONFIG_DIR", shellCommand);

        // Tab 2 inherited SideRepo's project account -> CLAUDE_CONFIG_DIR points to sideAcc.Directory!
        Assert.Equal(sideAcc.Directory, specTab2.Environment["CLAUDE_CONFIG_DIR"]);

        // ProjectPanelViewModel shows the account badge on each tab's ModelName subtitle
        var panelVm = AppHost.Get<ProjectPanelViewModel>();
        Assert.Equal(2, panelVm.Items.Count);
        Assert.Contains("Side", panelVm.Items[1].ModelName);
    }

    private sealed class FixedPathPicker(string? path = null) : IPathPicker
    {
        public Task<string?> PickAsync(bool folder, string title) => Task.FromResult(path);
    }
}
