using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Octopus.Mobile.Common;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Terminals;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Protocol;
using Octopus.Remote.Tests;
using TKCode.Arc.Mobile;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>Mobile branch workspace grouping, session distinction, and creating worktrees locally (Demo) and over the real remote protocol.</summary>
public class WorkspaceBranchTests
{
    [AvaloniaFact]
    public void Host_page_groups_sessions_by_workspace_and_terminal_location_shows_branch()
    {
        using var phone = new Phone();
        var mac = phone.Workspace.Hosts[0];
        var octopus = mac.Projects[0];
        phone.Shell.ShowHost(mac);
        Phone.Settle();

        var host = (HostViewModel)phone.Top<HostPage>().DataContext!;
        // Before any worktree exists, the section renders flat.
        Assert.False(host.Sections[0].HasWorkspaces);
        Assert.Equal(3, host.Sections[0].FlatSessions.Count);
        Assert.Empty(host.Sections[0].WorkspaceGroups);

        // Open NewSheet and create a brand-new branch workspace bound to a GitHub issue.
        host.OpenNewSessionCommand.Execute(null);
        Phone.Settle();
        var sheet = host.NewSession;
        Assert.True(sheet.ShowWorkspaces);
        Assert.True(sheet.Workspaces[0].IsDefault);
        Assert.True(sheet.Workspaces[0].IsSelected);

        // Select "+ 新建分支工作区"
        var createNewChoice = Assert.Single(sheet.Workspaces, w => w.IsCreateNew);
        createNewChoice.ChooseCommand.Execute(null);
        Assert.True(Phone.Until(() => sheet.IsCreatingWorkspace && sheet.BaseBranches.Count > 0 && sheet.GhAvailable));
        Assert.Equal("main", sheet.SelectedBaseBranch);

        // Empty branch name is rejected without closing the sheet.
        sheet.CreateCommand.Execute(null);
        Phone.Settle();
        Assert.True(sheet.IsOpen);
        Assert.True(sheet.HasError);

        // Bind a GitHub issue: branch name is suggested automatically and preview reflects ~/.octopus/workspaces/<project>/<branch>.
        sheet.SelectedTaskMode = 1; // Issue
        Assert.NotEmpty(sheet.CurrentTaskOptions);
        var issue = sheet.CurrentTaskOptions[0];
        sheet.SelectTaskCommand.Execute(issue);
        Assert.Equal(issue.SuggestedBranch, sheet.NewBranchName);
        Assert.Contains("~/.octopus/workspaces/Octopus/feat-142-support-git-worktree-branch-workspaces", sheet.WorkspacePathPreview);
        Assert.True(sheet.ShowInjectTaskPrompt);
        Assert.True(sheet.InjectTaskPrompt);

        // Start creates the worktree and launches the agent inside it.
        sheet.CreateCommand.Execute(null);
        Phone.Settle();
        Assert.False(sheet.IsOpen);

        var terminal = (TerminalViewModel)phone.Top<TerminalPage>().DataContext!;
        Assert.Equal(issue.SuggestedBranch, terminal.Session.WorkspaceBranch);
        Assert.Equal(issue.Key, terminal.Session.WorkspaceTaskKey);
        Assert.Equal($"Octopus ({issue.SuggestedBranch}) · MacBook Pro · 局域网", terminal.Location);

        // Back on HostPage: Octopus now uses workspace grouping (Default + new branch workspace) with no duplicate visual rows.
        terminal.BackCommand.Execute(null);
        Phone.Settle();
        var section = host.Sections[0];
        Assert.True(section.HasWorkspaces);
        Assert.Empty(section.FlatSessions);
        Assert.Equal(2, section.WorkspaceGroups.Count);
        Assert.True(section.WorkspaceGroups[0].IsDefault);
        Assert.Equal(3, section.WorkspaceGroups[0].Sessions.Count);
        Assert.True(section.WorkspaceGroups[1].IsWorktree);
        Assert.Equal(issue.SuggestedBranch, section.WorkspaceGroups[1].Title);
        Assert.Equal(issue.Key, section.WorkspaceGroups[1].TaskKey);
        Assert.Single(section.WorkspaceGroups[1].Sessions);

        // Total rendered SessionRowViewModel items across all 3 projects = 5 original + 1 new = 6 (no hidden duplicates).
        Assert.Equal(6, phone.Shell.Top!.GetVisualDescendants().OfType<ListItem>().Count(row => row.DataContext is SessionRowViewModel));
    }

    [AvaloniaFact]
    public async Task Real_protocol_syncs_workspaces_and_creates_sessions_and_new_branch_worktrees_on_host()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        var octopus = studio.Workspace.AddProject("Octopus", "~/code/Octopus", isGitRepository: true, defaultBranch: "main");
        var existingWs = studio.Workspace.AddWorkspace(octopus, "feat/existing-ui", "issue", "#88", "Sidebar grouping");
        studio.Workspace.SetWorkspaceOptions(octopus, new WorkspaceOptionsMessage
        {
            IsGitRepository = true,
            DefaultBranch = "main",
            WorkspacesDirectory = "~/worktrees/Octopus",
            GhAvailable = true,
            Branches =
            [
                new WorkspaceBranchMessage { Name = "main", IsCurrent = true, OccupiedBy = "Octopus" },
                new WorkspaceBranchMessage { Name = "feat/existing-ui", OccupiedBy = "feat/existing-ui" },
                new WorkspaceBranchMessage { Name = "hotfix/mobile-scroll" },
            ],
            Issues =
            [
                new WorkspaceTaskOptionMessage
                {
                    Kind = "issue",
                    Key = "#201",
                    Title = "Mobile branch switcher",
                    Url = "https://github.com/org/octopus/issues/201",
                    Body = "Allow creating worktrees from phone.",
                    SuggestedBranch = "feat/201-mobile-branch-switcher",
                },
            ],
        });
        studio.Workspace.AddItem(octopus, "默认会话", "claude", ItemActivities.Idle);
        studio.Workspace.AddItem(octopus, "分支会话", "claude", ItemActivities.Working, workspace: existingWs);

        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        phone.Pair(studio);
        var row = phone.Model<HostsViewModel>().Online.Single();
        Assert.True(Phone.Until(() => row.Host.Projects.Count == 1 && row.Host.Projects[0].Workspaces.Count == 1 && row.Host.Projects[0].Sessions.Count == 2));

        row.OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostPage));
        var host = phone.Model<HostViewModel>();
        var section = Assert.Single(host.Sections);
        Assert.True(section.HasWorkspaces);
        Assert.Equal(2, section.WorkspaceGroups.Count);
        Assert.Equal(["默认会话"], section.WorkspaceGroups[0].Sessions.Select(s => s.Title));
        Assert.Equal(["分支会话"], section.WorkspaceGroups[1].Sessions.Select(s => s.Title));
        Assert.Equal("#88", section.WorkspaceGroups[1].TaskKey);

        // 1. Tap "+" on the existing worktree group -> pre-selects that worktree and starts a terminal in it.
        section.WorkspaceGroups[1].AddSessionCommand.Execute(null);
        Phone.Settle();
        var sheet = host.NewSession;
        Assert.True(sheet.IsOpen);
        Assert.Equal(existingWs.Id, sheet.SelectedWorkspaceChoice?.Workspace?.Id);
        sheet.SelectedKind = 1; // Terminal
        sheet.CreateCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is TerminalPage), "terminal opens: " + sheet.ErrorText);
        Assert.True(studio.Workspace.Created.TryDequeue(out var existingReq));
        Assert.Equal(existingWs.Id, existingReq.WorkspaceId);
        Assert.Equal("Octopus (feat/existing-ui) · Studio · 中转", phone.Model<TerminalViewModel>().Location);
        phone.Model<TerminalViewModel>().BackCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostPage));

        // 2. Tap "+ 新分支" on the project header -> opens directly in new branch creation mode, binds host gh issue, creates worktree + agent.
        section.NewBranchCommand.Execute(null);
        Assert.True(Phone.Until(() => sheet.IsOpen && sheet.IsCreatingWorkspace && sheet.GhAvailable && sheet.BaseBranches.Count == 3));
        // Only the unoccupied branch appears in AvailableExistingBranches.
        Assert.Equal(["hotfix/mobile-scroll"], sheet.AvailableExistingBranches.Select(b => b.Name));

        sheet.SelectedTaskMode = 1; // Issue
        var issue = Assert.Single(sheet.CurrentTaskOptions);
        sheet.SelectTaskCommand.Execute(issue);
        Assert.Equal("feat/201-mobile-branch-switcher", sheet.NewBranchName);
        Assert.Contains("~/worktrees/Octopus/feat-201-mobile-branch-switcher", sheet.WorkspacePathPreview);
        sheet.Prompt = "实现移动端工作区";
        sheet.CreateCommand.Execute(null);

        Assert.True(Phone.Until(() => phone.Shell.Top is TerminalPage), "new branch session opens: " + sheet.ErrorText);
        Assert.True(studio.Workspace.CreatedWorkspaces.TryDequeue(out var wsReq));
        Assert.Equal((octopus.Id, true, "feat/201-mobile-branch-switcher", "main", "issue", "#201"),
            (wsReq.ProjectId, wsReq.CreateBranch, wsReq.BranchName, wsReq.BaseRef, wsReq.TaskKind, wsReq.TaskKey));

        Assert.True(studio.Workspace.Created.TryDequeue(out var itemReq));
        Assert.False(string.IsNullOrEmpty(itemReq.WorkspaceId));
        Assert.True(itemReq.InjectTaskPrompt);
        Assert.Equal("实现移动端工作区", itemReq.Prompt);

        var terminal = phone.Model<TerminalViewModel>();
        Assert.Equal("Octopus (feat/201-mobile-branch-switcher) · Studio · 中转", terminal.Location);
    }

    [AvaloniaFact]
    public async Task Older_host_without_workspaces_capability_reports_a_clear_message_when_creating_workspace()
    {
        await using var relay = new FakeRelay();
        await using var older = await Computer.StartAsync(relay, "OldMac", advertiseWorkspaces: false);
        older.Workspace.AddProject("Octopus", "~/code/Octopus", isGitRepository: true, defaultBranch: "main");

        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        phone.Pair(older);
        var host = phone.Model<HostsViewModel>().Online.Single().Host;
        Assert.True(Phone.Until(() => host.Projects.Count == 1));
        var project = host.Projects[0];

        var options = await phone.Remote.Workspace.GetWorkspaceOptionsAsync(project);
        Assert.False(options.IsGitRepository);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            phone.Remote.Workspace.CreateWorkspaceAsync(project, new CreateBranchWorkspaceRequest(true, "feat/new", "main", false)));
        Assert.Contains("更新主机上的 Octopus", error.Message);
    }
}
