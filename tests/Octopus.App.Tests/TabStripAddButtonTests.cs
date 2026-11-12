using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Composition;
using Octopus.App.Launcher;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.App.Workspace;
using Octopus.Core.Projects;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>The "+" at the end of each tab group's strip: where it sits, which group it belongs to.</summary>
public class TabStripAddButtonTests
{
    private const double Tolerance = 6;

    private static MainWindow Open()
    {
        var window = new MainWindow();
        window.Show();
        Frames.Pump();
        return window;
    }

    private static Project ProjectWithTabs(int tabs)
    {
        var project = AppHost.Get<IProjectService>().AddProject(Directory.CreateTempSubdirectory("octopus-strip-").FullName);
        for (var i = 0; i < tabs; i++)
        {
            AppHost.Get<IItemOpener>().OpenTerminal();
        }
        return project;
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Frames.Pump();
            window.UpdateLayout();
        }
    }

    private static List<Control> GroupViews(Window window) =>
        window.GetVisualDescendants().OfType<Control>().Where(c => c.GetType().Name == "DockGroupView").ToList();

    private static List<Control> TabsOf(Control group) =>
        group.GetVisualDescendants().OfType<Control>().Where(c => c.GetType().Name == "DockTab").ToList();

    private static StripAddButton AddOf(Control group) => group.GetVisualDescendants().OfType<StripAddButton>().Single();

    private static Rect In(Window window, Control control) =>
        new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

    private static void AssertFollowsLastTab(Window window, Control group)
    {
        var last = TabsOf(group).Select(t => In(window, t)).MaxBy(r => r.Right);
        var plus = In(window, AddOf(group));
        Assert.True(Math.Abs(plus.Left - last.Right) <= Tolerance, $"the + starts at {plus.Left}, the last tab ends at {last.Right}");
        // Same row, centred on the tab strip's height.
        Assert.True(Math.Abs((plus.Top + plus.Height / 2) - (last.Top + last.Height / 2)) <= 2, $"+ centre {plus.Center.Y}, tab centre {last.Center.Y}");
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(3)]
    public void The_plus_follows_the_last_tab(int tabs)
    {
        var window = Open();
        ProjectWithTabs(tabs);
        Settle(window);

        var group = Assert.Single(GroupViews(window));
        Assert.Equal(tabs, TabsOf(group).Count);
        AssertFollowsLastTab(window, group);
    }

    [AvaloniaFact]
    public void Closing_a_tab_moves_the_plus_left()
    {
        var window = Open();
        var project = ProjectWithTabs(3);
        Settle(window);
        var before = In(window, AddOf(GroupViews(window).Single())).Left;

        AppHost.Get<IProjectService>().RemoveItem(project.Items[^1].Id);
        Settle(window);
        // Arc's tabs glide when they change: wait for them to arrive.
        Assert.True(Frames.Until(() => In(window, AddOf(GroupViews(window).Single())).Left < before - 20));
        Settle(window);

        var group = Assert.Single(GroupViews(window));
        Assert.Equal(2, TabsOf(group).Count);
        AssertFollowsLastTab(window, group);
    }

    [AvaloniaFact]
    public void Each_group_has_its_own_plus_and_a_click_adds_the_tab_to_that_group()
    {
        var window = Open();
        var project = ProjectWithTabs(2);
        // Split only once the first layout has run (with the split before it, the two terminals keep taking the focus from each other).
        Settle(window);
        AppHost.Get<IWorkspaceCommands>().SplitRight();
        Settle(window);

        var groups = GroupViews(window).OrderBy(g => In(window, g).Left).ToList();
        Assert.Equal(2, groups.Count);
        foreach (var group in groups)
        {
            AssertFollowsLastTab(window, group);
        }
        Assert.True(In(window, AddOf(groups[0])).Left < In(window, groups[1]).Left, "the first + stays in the first group");

        // After the split the new group (on the right) is the active one; the click is on the first group's +.
        var layout = (DockLayout)AppHost.Get<WorkspaceViewModel>().CurrentLayout!;
        var second = layout.Workspace.FindPane(project.Items[^1].Id)!.Group!;
        var first = layout.Workspace.FindPane(project.Items[0].Id)!.Group!;
        Assert.NotSame(first, second);
        Assert.Same(second, layout.ActivePane!.Group);
        var inner = AddOf(groups[0]).FindControl<Avalonia.Controls.Button>("Button")!;
        inner.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Frames.Pump();

        AppHost.Get<LauncherViewModel>().NewTerminalCommand.Execute(null);
        Settle(window);
        var created = project.Items[^1];
        Assert.Same(first, layout.Workspace.FindPane(created.Id)!.Group);
        Assert.Equal(2, first.Panes.Count);
        Assert.Single(second.Panes);
    }

    [AvaloniaFact]
    public void The_plus_of_the_first_group_does_not_overlap_the_collapsed_lead()
    {
        var window = Open();
        ProjectWithTabs(2);
        var shell = AppHost.Get<ShellViewModel>();
        shell.IsSidebarCollapsed = true;
        Assert.True(Frames.Until(() => window.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "Main").ColumnDefinitions[0].Width.Value < 0.01));
        Settle(window);

        var lead = In(window, window.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "CollapsedLead"));
        var group = Assert.Single(GroupViews(window));
        var first = TabsOf(group).Select(t => In(window, t)).MinBy(r => r.Left);
        var plus = In(window, AddOf(group));
        Assert.True(first.Left >= lead.Right - 0.5, $"first tab {first.Left}, lead ends {lead.Right}");
        Assert.True(plus.Left >= lead.Right);
        AssertFollowsLastTab(window, group);
    }

    [AvaloniaFact]
    public void Many_tabs_keep_the_plus_inside_the_strip_clear_of_the_overflow_and_more_buttons()
    {
        var window = Open();
        ProjectWithTabs(30);
        Settle(window);

        var group = Assert.Single(GroupViews(window));
        var plus = In(window, AddOf(group));
        var more = group.GetVisualDescendants().OfType<Control>().First(c => c.Name == "PART_More");
        var overflow = group.GetVisualDescendants().OfType<Control>().First(c => c.Name == "PART_Overflow");
        Assert.True(overflow.IsVisible, "the overflow chevron shows");
        var moreRect = In(window, more);
        var overflowRect = In(window, overflow);
        Assert.True(plus.Right <= overflowRect.Left + 0.5, $"+ ends at {plus.Right}, the chevron starts at {overflowRect.Left}");
        Assert.True(overflowRect.Right <= moreRect.Left + 0.5);
        Assert.True(plus.Right <= In(window, group).Right);
        // No tab shows under the plus: the strip ends where the plus begins.
        var strip = group.GetVisualDescendants().OfType<Control>().First(c => c.Name == "PART_Strip");
        Assert.True(In(window, strip).Right <= plus.Left + 0.5, $"strip ends {In(window, strip).Right}, + starts {plus.Left}");
    }

    [AvaloniaFact]
    public void A_project_without_tabs_has_no_groups_and_no_plus_but_the_empty_state_offers_a_new_tab()
    {
        var window = Open();
        AppHost.Get<IProjectService>().AddProject(Directory.CreateTempSubdirectory("octopus-strip-").FullName);
        Settle(window);
        Assert.Empty(window.GetVisualDescendants().OfType<StripAddButton>());
        Assert.NotEmpty(window.GetVisualDescendants().OfType<EmptyProjectView>());
    }
}
