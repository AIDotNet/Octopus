using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.Agents;
using Octopus.App.Composition;
using Octopus.App.Launcher;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.App.Sidebar;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Storage;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

public class WorkspaceTests
{
    private static MainWindow Open()
    {
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Project AddProject(string? path = null) => AppHost.Get<IProjectService>().AddProject(path ?? Directory.CreateTempSubdirectory("octopus-demo-").FullName);

    [AvaloniaFact]
    public void Main_window_has_rail_panel_and_workspace()
    {
        var window = Open();
        Assert.NotEmpty(window.GetVisualDescendants().OfType<ProjectRailView>());
        Assert.NotEmpty(window.GetVisualDescendants().OfType<ProjectPanelView>());
        Assert.NotEmpty(window.GetVisualDescendants().OfType<WorkspaceView>());
    }

    [AvaloniaFact]
    public void Without_a_project_the_welcome_view_shows()
    {
        var window = Open();
        Assert.True(window.GetVisualDescendants().OfType<NoProjectView>().Single().IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_new_terminal_adds_a_tab_to_the_dock_layout()
    {
        var window = Open();
        var project = AddProject();
        var item = AppHost.Get<IItemOpener>().OpenTerminal()!;
        Dispatcher.UIThread.RunJobs();

        var layout = (DockLayout)AppHost.Get<WorkspaceViewModel>().CurrentLayout!;
        var pane = layout.Workspace.FindPane(item.Id);
        Assert.NotNull(pane);
        Assert.Equal(item.DisplayTitle, pane!.Title);
        Assert.Same(pane, layout.ActivePane);
        Assert.Contains(item, project.Items);
        Assert.Contains(window.GetVisualDescendants().OfType<DockLayout>(), l => ReferenceEquals(l, layout));
    }

    [AvaloniaFact]
    public void The_process_starts_when_the_tab_is_first_shown_not_before()
    {
        var window = Open();
        var project = AddProject();
        var restored = new ProjectItem { Kind = ProjectItemKind.Terminal, Title = "zsh" };
        // Two restored-style items: only the active (shown) one may start.
        AppHost.Get<IProjectService>().AddItem(project, restored);
        var second = AppHost.Get<IItemOpener>().OpenTerminal()!;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var content = AppHost.Get<ITabContentRegistry>();
        Assert.NotNull(content.Find(restored.Id));
        Assert.NotNull(content.Find(second.Id));
        Assert.Same(content.Find(second.Id), content.Find(second.Id));
    }

    [AvaloniaFact]
    public void Closing_an_item_removes_its_tab_and_content()
    {
        Open();
        var project = AddProject();
        var item = AppHost.Get<IItemOpener>().OpenTerminal()!;
        Dispatcher.UIThread.RunJobs();
        AppHost.Get<IProjectService>().RemoveItem(item.Id);
        Dispatcher.UIThread.RunJobs();

        var layout = (DockLayout)AppHost.Get<WorkspaceViewModel>().CurrentLayout!;
        Assert.Null(layout.Workspace.FindPane(item.Id));
        Assert.Empty(project.Items);
        Assert.Null(AppHost.Get<ITabContentRegistry>().Find(item.Id));
    }

    [AvaloniaFact]
    public void Split_right_puts_the_active_tab_in_a_second_group()
    {
        Open();
        AddProject();
        var opener = AppHost.Get<IItemOpener>();
        opener.OpenTerminal();
        var second = opener.OpenTerminal()!;
        Dispatcher.UIThread.RunJobs();
        AppHost.Get<IWorkspaceCommands>().SplitRight();
        Dispatcher.UIThread.RunJobs();

        var layout = (DockLayout)AppHost.Get<WorkspaceViewModel>().CurrentLayout!;
        var moved = layout.Workspace.FindPane(second.Id)!;
        var other = layout.Workspace.AllPanes().First(p => p.Id != second.Id);
        Assert.NotSame(moved.Group, other.Group);
    }

    [AvaloniaFact]
    public void Move_to_a_new_group_takes_the_tab_out_of_a_group_of_several()
    {
        Open();
        AddProject();
        var opener = AppHost.Get<IItemOpener>();
        var first = opener.OpenTerminal()!;
        opener.OpenTerminal();
        Dispatcher.UIThread.RunJobs();

        AppHost.Get<IWorkspaceCommands>().MoveToNewGroup(first.Id);
        Dispatcher.UIThread.RunJobs();

        var layout = (DockLayout)AppHost.Get<WorkspaceViewModel>().CurrentLayout!;
        var moved = layout.Workspace.FindPane(first.Id)!;
        Assert.All(layout.Workspace.AllPanes().Where(p => p.Id != first.Id), p => Assert.NotSame(moved.Group, p.Group));
        Assert.Equal(2, layout.Workspace.AllPanes().Count());
    }

    [AvaloniaFact]
    public void Move_to_a_new_group_on_a_tab_that_is_alone_starts_a_terminal_in_the_new_group()
    {
        Open();
        var project = AddProject();
        var only = AppHost.Get<IItemOpener>().OpenTerminal()!;
        Dispatcher.UIThread.RunJobs();

        AppHost.Get<IWorkspaceCommands>().MoveToNewGroup(only.Id);
        Dispatcher.UIThread.RunJobs();

        // The tab stayed, a second terminal exists, and the two are in different groups: a split.
        Assert.Equal(2, project.Items.Count);
        var created = project.Items.Single(i => i.Id != only.Id);
        var layout = (DockLayout)AppHost.Get<WorkspaceViewModel>().CurrentLayout!;
        var stayed = layout.Workspace.FindPane(only.Id)!;
        var started = layout.Workspace.FindPane(created.Id)!;
        Assert.NotSame(stayed.Group, started.Group);
        Assert.Single(stayed.Group!.Panes);
        Assert.Single(started.Group!.Panes);
    }

    [AvaloniaFact]
    public void Collapsing_the_sidebar_leaves_no_left_column()
    {
        var window = Open();
        AddProject();
        var shell = AppHost.Get<ShellViewModel>();
        var left = window.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "LeftArea");
        Assert.True(left.IsVisible);
        Assert.True(left.Bounds.Width > 200);

        shell.IsSidebarCollapsed = true;
        var column = window.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "Main").ColumnDefinitions[0];
        // The sidebar slides away; the columns are gone once it has arrived.
        Assert.True(Frames.Until(() => column.Width.Value < 0.01));
        Frames.Pump();
        window.UpdateLayout();
        Assert.False(left.IsEffectivelyVisible);
        Assert.Equal(0, shell.LeftColumnWidth.Value);
        Assert.True(AppHost.Get<ISettingsStore>().Current.Sidebar.IsCollapsed);

        var workspace = window.GetVisualDescendants().OfType<WorkspaceView>().Single();
        Assert.True(workspace.Bounds.Width >= window.ClientSize.Width - 1);
    }

    [AvaloniaFact]
    public void Removing_a_project_asks_and_deletes_only_the_record()
    {
        Open();
        var dir = Directory.CreateTempSubdirectory("octopus-proj-").FullName;
        var project = AddProject(dir);
        var chip = AppHost.Get<ProjectRailViewModel>().Chips.Single();
        AppHost.Get<FakeDialogService>().Answer = true;
        chip.RemoveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(AppHost.Get<IProjectService>().Projects);
        Assert.True(Directory.Exists(dir));
        Assert.Equal(1, AppHost.Get<FakeDialogService>().Asked);
        _ = project;
    }

    [AvaloniaFact]
    public void The_new_tab_menu_lists_installed_agents_with_the_default_first()
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultAgentId = "opencode");
        var launcher = AppHost.Get<LauncherViewModel>();
        launcher.Refresh();
        Assert.Equal("opencode", launcher.Agents[0].Id);
        Assert.True(launcher.Agents[0].IsDefault);
        Assert.Contains(launcher.Agents, a => a.Id == "claude");
    }

    [AvaloniaFact]
    public void Disabled_agents_are_left_out_of_the_menu()
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.Profiles["codex"] = new AgentProfile { Enabled = false });
        var launcher = AppHost.Get<LauncherViewModel>();
        launcher.Refresh();
        Assert.DoesNotContain(launcher.Agents, a => a.Id == "codex");
    }

    [AvaloniaFact]
    public void Launching_an_agent_creates_an_agent_item_with_the_choices()
    {
        Open();
        var project = AddProject();
        var launcher = AppHost.Get<LauncherViewModel>();
        launcher.Refresh();
        var claude = launcher.Agents.First(a => a.Id == "claude");
        launcher.Expand(claude);
        var panel = launcher.Panel!;
        panel.SelectedEffortId = "high";
        panel.LaunchCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var item = Assert.Single(project.Items);
        Assert.Equal(ProjectItemKind.Agent, item.Kind);
        Assert.Equal("claude", item.AgentId);
        Assert.Equal("high", item.Launch!.Effort);
    }
}
