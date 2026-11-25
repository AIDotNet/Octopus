using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Octopus.Mobile.Files;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Protocol;
using Octopus.Remote.Tests;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>Scheme A smart accordion project list on <see cref="HostPage"/>: folding, filtering, search, inline workspace tabs, and quick actions.</summary>
public class ProjectListTests
{
    [AvaloniaFact]
    public void Active_projects_start_expanded_while_idle_and_empty_projects_start_collapsed_and_toggle_all_works()
    {
        using var phone = new Phone();
        var mac = phone.Workspace.Hosts[0];
        // Add an idle project and an empty project alongside the 3 active demo projects.
        var idleProject = new RemoteProject(mac, "mac/docs", "octopus-docs", "~/code/octopus-docs")
        {
            Color = "#E07A3B",
            IsGitRepository = true,
            DefaultBranch = "main",
        };
        idleProject.Sessions.Add(new RemoteSession(idleProject, "idle-1", SessionKind.Terminal, "preview server")
        {
            Detail = "zsh",
            Activity = SessionActivity.Idle,
        });
        var emptyProject = new RemoteProject(mac, "mac/scripts", "dev-scripts", "~/scripts")
        {
            IsGitRepository = false,
        };
        mac.Projects.Add(idleProject);
        mac.Projects.Add(emptyProject);

        phone.Shell.ShowHost(mac);
        Phone.Settle();

        var host = (HostViewModel)phone.Top<HostPage>().DataContext!;
        Assert.Equal(5, host.VisibleSections.Count);

        // First 3 demo projects have waiting/working/error sessions -> auto-expanded.
        Assert.True(host.Sections[0].IsExpanded);
        Assert.True(host.Sections[1].IsExpanded);
        Assert.True(host.Sections[2].IsExpanded);
        // Idle & empty projects -> auto-collapsed into compact summary cards.
        Assert.False(host.Sections[3].IsExpanded);
        Assert.Equal("1 空闲", host.Sections[3].SummaryPillText);
        Assert.False(host.Sections[4].IsExpanded);
        Assert.Equal("0 会话", host.Sections[4].SummaryPillText);
        Assert.False(host.Sections[4].CanViewChanges);
        Assert.True(host.Sections[4].CanBrowseFiles);

        // Live host summary reflects 1 waiting, 2 working, 1 error.
        Assert.Equal((1, 2, 1), (host.TotalWaitingCount, host.TotalWorkingCount, host.TotalErrorCount));
        Assert.Equal("全部收起", host.ToggleAllExpandedText);

        // Toggle all collapses every visible card, then expands every visible card.
        host.ToggleAllExpandedCommand.Execute(null);
        Assert.All(host.VisibleSections, section => Assert.False(section.IsExpanded));
        Assert.Equal("全部展开", host.ToggleAllExpandedText);

        host.ToggleAllExpandedCommand.Execute(null);
        Assert.All(host.VisibleSections, section => Assert.True(section.IsExpanded));
        Assert.Equal("全部收起", host.ToggleAllExpandedText);

        // Explicit user collapse choice persists across a host project-list rebuild.
        host.Sections[0].ToggleExpandedCommand.Execute(null);
        Assert.False(host.Sections[0].IsExpanded);
        mac.Projects.Add(new RemoteProject(mac, "mac/new", "new-repo", "~/code/new-repo"));
        Phone.Pump();
        Assert.False(host.Sections[0].IsExpanded);
        Assert.False(host.Sections[^1].IsExpanded);
    }

    [AvaloniaFact]
    public void Search_and_status_filters_narrow_projects_and_temporarily_expand_session_hits()
    {
        using var phone = new Phone();
        var mac = phone.Workspace.Hosts[0];
        var idleProject = new RemoteProject(mac, "mac/docs", "octopus-docs", "~/code/octopus-docs")
        {
            IsGitRepository = true,
            DefaultBranch = "docs-v2",
        };
        idleProject.Sessions.Add(new RemoteSession(idleProject, "idle-doc", SessionKind.Agent, "梳理部署指南")
        {
            AgentId = "codex",
            Detail = "GPT-6 · 中",
            Activity = SessionActivity.Idle,
        });
        mac.Projects.Add(idleProject);

        phone.Shell.ShowHost(mac);
        Phone.Settle();
        var host = (HostViewModel)phone.Top<HostPage>().DataContext!;
        Assert.False(host.Sections[3].IsExpanded);

        // Status filter chips narrow the list accurately.
        host.SelectFilterAttentionCommand.Execute(null);
        Assert.Equal(["Octopus", "TKCode.Arc"], host.VisibleSections.Select(s => s.Name));

        host.SelectFilterWorkingCommand.Execute(null);
        Assert.Equal(["Octopus", "OctopusServer"], host.VisibleSections.Select(s => s.Name));

        host.SelectFilterAllCommand.Execute(null);
        Assert.Equal(4, host.VisibleSections.Count);

        // Searching by a session title inside a collapsed project temporarily expands it.
        host.SearchQuery = "部署指南";
        var hit = Assert.Single(host.VisibleSections);
        Assert.Equal("octopus-docs", hit.Name);
        Assert.True(hit.IsExpanded);

        // Clearing search restores its default collapsed state.
        host.ClearSearchCommand.Execute(null);
        Assert.Equal(4, host.VisibleSections.Count);
        Assert.False(host.Sections[3].IsExpanded);

        // Unmatched query displays the filtered empty state and ResetFilters recovers.
        host.SearchQuery = "non-existent-keyword-xyz";
        Assert.Empty(host.VisibleSections);
        Assert.True(host.IsFilteredEmpty);
        host.ResetFiltersCommand.Execute(null);
        Assert.False(host.IsFilteredEmpty);
        Assert.Equal(4, host.VisibleSections.Count);
    }

    [AvaloniaFact]
    public void Inline_workspace_tabs_filter_sessions_and_target_quick_actions_to_the_selected_worktree()
    {
        using var phone = new Phone();
        var mac = phone.Workspace.Hosts[0];
        var octopus = mac.Projects[0];
        var wt = new RemoteBranchWorkspace(
            "ws-142",
            "feat/mobile-list",
            "feat/mobile-list",
            "~/.octopus/workspaces/Octopus/feat-mobile-list",
            "issue",
            "#142",
            "Optimize mobile project list");
        octopus.Workspaces.Add(wt);
        octopus.Sessions.Add(new RemoteSession(octopus, "s-wt", SessionKind.Agent, "优化项目卡片")
        {
            AgentId = "claude",
            Detail = "Sonnet 5.5 · 高",
            WorkspaceId = wt.Id,
            WorkspaceBranch = wt.DisplayName,
            WorkspaceTaskKey = wt.TaskKey,
            Activity = SessionActivity.Working,
        });

        phone.Shell.ShowHost(mac);
        Phone.Settle();
        var host = (HostViewModel)phone.Top<HostPage>().DataContext!;
        var section = host.Sections[0];

        Assert.True(section.HasWorkspaces);
        Assert.Equal(3, section.WorkspaceTabs.Count); // All, Default, #142
        Assert.Equal(4, section.DisplayedSessions.Count);

        // Switch to the Default folder tab -> shows the 3 root sessions.
        section.WorkspaceTabs[1].SelectCommand.Execute(null);
        Assert.Equal(3, section.DisplayedSessions.Count);
        Assert.DoesNotContain(section.DisplayedSessions, row => row.Title == "优化项目卡片");

        // Switch to the #142 worktree tab -> shows only the worktree session.
        section.WorkspaceTabs[2].SelectCommand.Execute(null);
        var only = Assert.Single(section.DisplayedSessions);
        Assert.Equal("优化项目卡片", only.Title);
        Assert.Equal("#142", only.WorkspaceBadgeText);

        // Quick "+" on the card pre-selects this project AND the currently selected worktree.
        section.AddSessionCommand.Execute(null);
        Phone.Settle();
        Assert.True(host.NewSession.IsOpen);
        Assert.Same(octopus, host.NewSession.Projects.Single(p => p.IsSelected).Project);
        Assert.Equal(wt.Id, host.NewSession.SelectedWorkspaceChoice?.Workspace?.Id);
        host.NewSession.IsOpen = false;

        // Footer "Files" opens FilesPage at the selected worktree folder.
        section.BrowseFilesCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is FilesPage));
        var filesVm = (FilesViewModel)phone.Top<FilesPage>().DataContext!;
        Assert.Equal(wt.Path, filesVm.Crumbs[0].Path);
        phone.Shell.Back();
        Phone.Settle();

        // Footer "Changes" opens ChangesPage for the selected worktree.
        section.ViewChangesCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is ChangesPage));
        phone.Shell.Back();
        Phone.Settle();

        // Live activity updates refresh the card's corner badge and host summary counters.
        var waiting = octopus.Sessions[0];
        Assert.True(section.HasAttention);
        Assert.Equal("1", section.CornerBadgeText);
        waiting.Activity = SessionActivity.Idle;
        Phone.Pump();
        Assert.False(section.HasAttention);
        Assert.Equal(0, host.TotalWaitingCount);
    }

    [AvaloniaFact]
    public async Task Remote_protocol_syncs_project_color_and_renders_colored_project_badge()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        var p = studio.Workspace.AddProject("Octopus", "~/code/Octopus", isGitRepository: true, defaultBranch: "main");
        p.Color = "#1F9D74";

        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        phone.Pair(studio);
        var row = phone.Model<HostsViewModel>().Online.Single();
        Assert.True(Phone.Until(() => row.Host.Projects.Count == 1));
        Assert.Equal("#1F9D74", row.Host.Projects[0].Color);

        row.OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostPage));
        var section = Assert.Single(phone.Model<HostViewModel>().Sections);
        var brush = Assert.IsType<SolidColorBrush>(section.ProjectBrush);
        Assert.Equal(Color.Parse("#1F9D74"), brush.Color);
    }
}
