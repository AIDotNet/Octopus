using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Sidebar;
using Octopus.Core.Projects;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>Groups on the rail: folder squares that open one at a time into their projects.</summary>
public class ProjectGroupTests
{
    private static Project Add(string name) =>
        AppHost.Get<IProjectService>().AddProject(Directory.CreateTempSubdirectory("octopus-group-" + name + "-").FullName, name);

    private static ProjectGroupViewModel GroupEntry(ProjectRailViewModel rail, string id) =>
        rail.Entries.OfType<ProjectGroupViewModel>().Single(g => g.Id == id);

    [AvaloniaFact, FreshApp]
    public void Projects_in_no_group_come_first_then_the_folders()
    {
        var projects = AppHost.Get<IProjectService>();
        var rail = AppHost.Get<ProjectRailViewModel>();
        var a = Add("a");
        var b = Add("b");
        var c = Add("c");
        var work = projects.AddGroup("Work", [a.Id, c.Id]);
        projects.SetExpandedGroup(null);

        Assert.Equal(3, rail.Chips.Count);
        Assert.Collection(rail.Entries,
            e => Assert.Equal(b.Id, Assert.IsType<ProjectChipViewModel>(e).Id),
            e => Assert.Equal(work.Id, Assert.IsType<ProjectGroupViewModel>(e).Id));
        var folder = GroupEntry(rail, work.Id);
        Assert.True(folder.IsCollapsed);
        Assert.Equal(new[] { a.Id, c.Id }, folder.Chips.Select(x => x.Id));
        // Four places: the two projects' colors, then two empty ones.
        Assert.Equal(4, folder.Previews.Count);
        Assert.Equal(Color.Parse(a.Color!), ((ISolidColorBrush)folder.Previews[0].Background).Color);
        Assert.Equal(Color.Parse(c.Color!), ((ISolidColorBrush)folder.Previews[1].Background).Color);
        Assert.Same(folder.Previews[2].Background, folder.Previews[3].Background);
        Assert.Contains("Work", folder.Tip);
    }

    [AvaloniaFact, FreshApp]
    public void A_closed_folder_holding_the_current_project_takes_its_ring()
    {
        var projects = AppHost.Get<IProjectService>();
        var rail = AppHost.Get<ProjectRailViewModel>();
        var a = Add("a");
        Add("b");
        var work = projects.AddGroup("Work", [a.Id]);
        projects.SetCurrent(a.Id);

        Assert.False(GroupEntry(rail, work.Id).HasCurrent);
        projects.SetExpandedGroup(null);
        Assert.True(GroupEntry(rail, work.Id).HasCurrent);
        Assert.Equal(1, GroupEntry(rail, work.Id).FolderOpacity);
    }

    [AvaloniaFact, FreshApp]
    public void Opening_a_folder_closes_the_open_one()
    {
        var projects = AppHost.Get<IProjectService>();
        var rail = AppHost.Get<ProjectRailViewModel>();
        var work = projects.AddGroup("Work", [Add("a").Id]);
        var oss = projects.AddGroup("OSS", [Add("b").Id]);
        Assert.True(GroupEntry(rail, oss.Id).IsExpanded);

        GroupEntry(rail, work.Id).ToggleCommand.Execute(null);
        Assert.True(GroupEntry(rail, work.Id).IsExpanded);
        Assert.False(GroupEntry(rail, oss.Id).IsExpanded);

        GroupEntry(rail, work.Id).ToggleCommand.Execute(null);
        Assert.All(rail.Entries.OfType<ProjectGroupViewModel>(), g => Assert.True(g.IsCollapsed));
        Assert.Null(projects.ExpandedGroupId);
    }

    [AvaloniaFact, FreshApp]
    public async Task The_square_menu_moves_a_project_and_makes_new_groups()
    {
        var projects = AppHost.Get<IProjectService>();
        var dialogs = AppHost.Get<FakeDialogService>();
        var rail = AppHost.Get<ProjectRailViewModel>();
        var a = Add("a");
        var b = Add("b");
        ProjectChipViewModel Chip(Project p) => rail.Chips.Single(x => x.Id == p.Id);

        // Dismissed: nothing.
        dialogs.NameAnswer = null;
        await Chip(a).NewGroupCommand.ExecuteAsync(null);
        Assert.Empty(projects.Groups);

        dialogs.NameAnswer = "Work";
        await Chip(a).NewGroupCommand.ExecuteAsync(null);
        var work = Assert.Single(projects.Groups);
        Assert.Equal("Work", work.Name);
        Assert.Equal(work.Id, a.GroupId);

        // The menu lists "No group", the groups (the project's own one checked) and "New group…".
        var menu = ProjectChipView.GroupMenuOf(Chip(a));
        Assert.Equal(3, menu.Count);
        Assert.Equal(new bool?[] { false, true, null }, menu.Select(e => e.IsChecked));

        Chip(b).MoveToGroupCommand.Execute(work.Id);
        Assert.Equal(work.Id, b.GroupId);
        Chip(a).MoveToGroupCommand.Execute(null);
        Assert.Null(a.GroupId);

        // An empty name gives the default one.
        dialogs.NameAnswer = "";
        await Chip(a).NewGroupCommand.ExecuteAsync(null);
        Assert.Equal(2, projects.Groups.Count);
        Assert.False(string.IsNullOrWhiteSpace(projects.Groups[1].Name));
    }

    [AvaloniaFact, FreshApp]
    public async Task The_folder_menu_renames_recolors_and_ungroups()
    {
        var projects = AppHost.Get<IProjectService>();
        var dialogs = AppHost.Get<FakeDialogService>();
        var rail = AppHost.Get<ProjectRailViewModel>();
        var a = Add("a");
        var work = projects.AddGroup("Work", [a.Id]);

        dialogs.NameAnswer = "Clients";
        await GroupEntry(rail, work.Id).RenameCommand.ExecuteAsync(null);
        Assert.Equal("Clients", work.Name);
        dialogs.NameAnswer = null;
        await GroupEntry(rail, work.Id).RenameCommand.ExecuteAsync(null);
        Assert.Equal("Clients", work.Name);

        GroupEntry(rail, work.Id).SetColorCommand.Execute(ProjectRailViewModel.GroupColors[7].Value);
        Assert.Equal(ProjectService.Palette[7], work.Color);
        Assert.Equal(ProjectService.Palette[7], GroupEntry(rail, work.Id).Color);

        GroupEntry(rail, work.Id).RemoveCommand.Execute(null);
        Assert.Empty(projects.Groups);
        Assert.Null(a.GroupId);
        Assert.Single(rail.Entries);
    }

    [AvaloniaFact, FreshApp]
    public async Task Dropping_a_square_moves_it()
    {
        var projects = AppHost.Get<IProjectService>();
        var dialogs = AppHost.Get<FakeDialogService>();
        var rail = AppHost.Get<ProjectRailViewModel>();
        var a = Add("a");
        var b = Add("b");
        var c = Add("c");

        // On another square in no group: the two make a group, named in the dialog.
        dialogs.NameAnswer = "Pair";
        await rail.DropOnProjectAsync(a.Id, b.Id);
        var pair = Assert.Single(projects.Groups);
        Assert.Equal("Pair", pair.Name);
        Assert.Equal(pair.Id, a.GroupId);
        Assert.Equal(pair.Id, b.GroupId);
        Assert.Equal(new[] { b.Id, a.Id }, GroupEntry(rail, pair.Id).Chips.Select(x => x.Id));

        // On a square in a group: into that group. On itself: nothing.
        await rail.DropOnProjectAsync(c.Id, a.Id);
        Assert.Equal(pair.Id, c.GroupId);
        await rail.DropOnProjectAsync(c.Id, c.Id);
        Assert.Single(projects.Groups);

        // On the rail outside the groups: out of its group. On a folder: into it.
        rail.DropOnRail(c.Id);
        Assert.Null(c.GroupId);
        rail.DropOnGroup(c.Id, pair.Id);
        Assert.Equal(pair.Id, c.GroupId);
    }

    [AvaloniaFact, FreshApp]
    public void Dragging_reorders_squares_and_folders()
    {
        var projects = AppHost.Get<IProjectService>();
        var rail = AppHost.Get<ProjectRailViewModel>();
        var a = Add("a");
        var b = Add("b");
        var c = Add("c");
        var d = Add("d");
        var work = projects.AddGroup("Work", [c.Id, d.Id]);
        var oss = projects.AddGroup("OSS");
        var clients = projects.AddGroup("Clients");

        // Squares: before another in no group; after one in a group (it joins the group there).
        rail.DropProjectNextTo(b.Id, a.Id, after: false);
        Assert.Equal(new[] { b.Id, a.Id }, rail.Entries.OfType<ProjectChipViewModel>().Select(x => x.Id));
        rail.DropProjectNextTo(a.Id, c.Id, after: true);
        Assert.Equal(new[] { c.Id, a.Id, d.Id }, GroupEntry(rail, work.Id).Chips.Select(x => x.Id));

        // Folders: before and after another, and last.
        rail.DropGroupNextTo(clients.Id, work.Id, after: false);
        Assert.Equal(new[] { clients.Id, work.Id, oss.Id }, rail.Entries.OfType<ProjectGroupViewModel>().Select(g => g.Id));
        rail.DropGroupNextTo(clients.Id, work.Id, after: true);
        Assert.Equal(new[] { work.Id, clients.Id, oss.Id }, rail.Entries.OfType<ProjectGroupViewModel>().Select(g => g.Id));
        rail.DropGroupLast(work.Id);
        Assert.Equal(new[] { clients.Id, oss.Id, work.Id }, rail.Entries.OfType<ProjectGroupViewModel>().Select(g => g.Id));
        // Squares in no group stay first.
        Assert.IsType<ProjectChipViewModel>(rail.Entries[0]);
    }

    [AvaloniaFact, FreshApp]
    public void Opening_a_folder_changes_it_in_place_and_a_rebuild_marks_what_to_play()
    {
        var projects = AppHost.Get<IProjectService>();
        var rail = AppHost.Get<ProjectRailViewModel>();
        var work = projects.AddGroup("Work", [Add("a").Id]);
        var oss = projects.AddGroup("OSS", [Add("b").Id]);

        // A new group opens as it is made, and the one that was open closes: both are played.
        Assert.True(GroupEntry(rail, oss.Id).AnimateOnAttach);
        Assert.True(GroupEntry(rail, work.Id).AnimateOnAttach);

        // Opening another one changes the same view models: the views stay and animate.
        var folder = GroupEntry(rail, work.Id);
        var changed = new List<string?>();
        folder.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        projects.SetExpandedGroup(work.Id);
        Assert.Same(folder, GroupEntry(rail, work.Id));
        Assert.True(folder.IsExpanded);
        Assert.False(GroupEntry(rail, oss.Id).IsExpanded);
        Assert.Contains(nameof(ProjectGroupViewModel.IsExpanded), changed);
        Assert.Contains(nameof(ProjectGroupViewModel.Tip), changed);

        // A rebuild with no change in which group is open plays nothing.
        projects.RenameGroup(oss.Id, "Open source");
        Assert.All(rail.Entries.OfType<ProjectGroupViewModel>(), g => Assert.False(g.AnimateOnAttach));
    }

    [AvaloniaFact, FreshApp]
    public void A_folder_springs_open_and_closed()
    {
        var projects = AppHost.Get<IProjectService>();
        var work = projects.AddGroup("Work", [Add("a").Id, Add("b").Id, Add("c").Id]);
        projects.SetExpandedGroup(null);
        var window = new Octopus.App.Shell.MainWindow();
        window.Show();
        Frames.Pump();
        var view = window.GetVisualDescendants().OfType<ProjectGroupView>().Single();
        var frame = view.FindControl<Border>("Frame")!;
        var folder = view.FindControl<Grid>("Folder")!;
        var capsule = view.FindControl<Panel>("Capsule")!;
        Assert.True(folder.IsVisible);
        Assert.False(capsule.IsVisible);
        Assert.Equal(42, view.Bounds.Height);

        var heights = new List<double>();
        projects.SetExpandedGroup(work.Id);
        Assert.True(Frames.Until(() => !folder.IsVisible, () => heights.Add(view.Bounds.Height)));
        Frames.Pump();
        Assert.True(capsule.IsVisible);
        Assert.True(double.IsNaN(frame.Height));
        var open = view.Bounds.Height;
        // The header and three squares.
        Assert.True(open > 42 * 3 + 26, $"open height {open}");
        Assert.Contains(heights, h => h > 42 && h < open);

        projects.SetExpandedGroup(null);
        Assert.True(Frames.Until(() => !capsule.IsVisible));
        Frames.Pump();
        Assert.True(folder.IsVisible);
        Assert.Equal(1, folder.Opacity);
        Assert.Equal(42, view.Bounds.Height);
        window.Close();
    }

    [AvaloniaFact, FreshApp]
    public void A_working_agent_puts_a_loading_ring_on_its_square_and_closed_folder()
    {
        var projects = AppHost.Get<IProjectService>();
        var activity = AppHost.Get<Octopus.App.Activity.IItemActivityService>();
        var rail = AppHost.Get<ProjectRailViewModel>();
        var a = Add("a");
        var b = Add("b");
        var agent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude" };
        projects.AddItem(a, agent);
        var work = projects.AddGroup("Work", [a.Id]);
        projects.SetExpandedGroup(null);
        ProjectChipViewModel Chip(Project p) => rail.Chips.Single(x => x.Id == p.Id);
        Assert.False(Chip(a).IsWorking);
        Assert.False(GroupEntry(rail, work.Id).IsWorking);

        // The agent's title carries a spinner: it is working.
        activity.ReportTitle(agent.Id, "⠂ Fix the login test");
        Assert.True(Chip(a).IsWorking);
        Assert.True(GroupEntry(rail, work.Id).IsWorking);
        Assert.False(Chip(b).IsWorking);

        // Choosing another project keeps it.
        projects.SetCurrent(b.Id);
        Assert.True(Chip(a).IsWorking);
        Assert.True(GroupEntry(rail, work.Id).IsWorking);

        // The turn ends.
        activity.ReportTitle(agent.Id, "✳ Claude Code");
        Assert.False(Chip(a).IsWorking);
        Assert.False(GroupEntry(rail, work.Id).IsWorking);

        // A working agent whose tab is closed no longer counts.
        activity.ReportTitle(agent.Id, "⠐ Review the parser");
        Assert.True(Chip(a).IsWorking);
        projects.RemoveItem(agent.Id);
        Assert.False(Chip(a).IsWorking);
        Assert.False(GroupEntry(rail, work.Id).IsWorking);
    }

    [AvaloniaFact, FreshApp]
    public void The_panel_names_the_group_of_the_current_project()
    {
        var projects = AppHost.Get<IProjectService>();
        var panel = AppHost.Get<ProjectPanelViewModel>();
        var a = Add("a");
        Assert.False(panel.HasGroup);

        var work = projects.AddGroup("Work", [a.Id]);
        Assert.Equal("Work", panel.GroupName);
        Assert.True(panel.HasGroup);

        projects.RenameGroup(work.Id, "Clients");
        Assert.Equal("Clients", panel.GroupName);
        projects.MoveProjectToGroup(a.Id, null);
        Assert.False(panel.HasGroup);
    }

    [AvaloniaFact, FreshApp]
    public void Choosing_another_project_moves_the_ring_without_rebuilding_the_rail()
    {
        var projects = AppHost.Get<IProjectService>();
        var rail = AppHost.Get<ProjectRailViewModel>();
        var a = Add("a");
        var b = Add("b");
        var work = projects.AddGroup("Work", [a.Id]);
        projects.SetExpandedGroup(null);
        var chips = rail.Chips.ToList();
        var entries = rail.Entries.ToList();
        ProjectChipViewModel Chip(Project p) => rail.Chips.Single(x => x.Id == p.Id);
        Assert.True(Chip(b).IsCurrent);
        Assert.False(GroupEntry(rail, work.Id).HasCurrent);

        var changed = new List<string?>();
        Chip(a).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        projects.SetCurrent(a.Id);

        // The same squares and folders, with the ring moved.
        Assert.Equal(chips, rail.Chips);
        Assert.Equal(entries, rail.Entries);
        Assert.True(Chip(a).IsCurrent);
        Assert.False(Chip(b).IsCurrent);
        Assert.Equal(1, Chip(a).ChipOpacity);
        Assert.Contains(nameof(ProjectChipViewModel.ChipOpacity), changed);
        Assert.True(GroupEntry(rail, work.Id).HasCurrent);
        Assert.Equal(1, GroupEntry(rail, work.Id).FolderOpacity);
    }

    [AvaloniaFact, FreshApp]
    public void Activating_another_item_moves_the_highlight_without_rebuilding_the_panel()
    {
        var projects = AppHost.Get<IProjectService>();
        var panel = AppHost.Get<ProjectPanelViewModel>();
        var a = Add("a");
        var first = new ProjectItem { Kind = ProjectItemKind.Terminal, Title = "zsh" };
        var second = new ProjectItem { Kind = ProjectItemKind.Terminal, Title = "bash" };
        projects.AddItem(a, first);
        projects.AddItem(a, second);
        var rows = panel.Items.ToList();
        Assert.Equal([false, true], rows.Select(r => r.IsActive).ToArray());

        projects.ActivateItem(first.Id);

        Assert.Equal(rows, panel.Items);
        Assert.Equal([true, false], panel.Items.Select(r => r.IsActive).ToArray());
    }
}
