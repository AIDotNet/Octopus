using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Composition;
using Octopus.App.Explorer;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.Core.Projects;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>How the right panel is tied to the window: open and close, width, opening files, following the project.</summary>
public class RightPanelTests
{
    private static (MainWindow Window, ExplorerPanelView Panel) Open()
    {
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, window.GetVisualDescendants().OfType<ExplorerPanelView>().Single());
    }

    private static void OpenPanel(Window window, ExplorerPanelView panel)
    {
        AppHost.Get<ShellViewModel>().ToggleRightPanelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.True(panel.IsVisible);
    }

    private static string Folder(params string[] files)
    {
        var dir = Directory.CreateTempSubdirectory("octopus-panel-").FullName;
        foreach (var file in files) File.WriteAllText(Path.Combine(dir, file), "x");
        return dir;
    }

    private static Grid MainGrid(Window window) => window.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "Main");

    private static double RightWidth(Window window) => MainGrid(window).ColumnDefinitions[4].Width.Value;

    /// <summary>Renders until the right column has the width (the slide has arrived).</summary>
    private static void WaitForRight(Window window, double width)
    {
        Assert.True(Frames.Until(() => Math.Abs(RightWidth(window) - width) < 0.01), $"the right column is {RightWidth(window)}, not {width}");
        Frames.Pump();
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public void The_panel_opens_and_closes_with_the_switch_and_the_choice_is_saved()
    {
        var (window, panel) = Open();
        var shell = AppHost.Get<ShellViewModel>();
        var settings = AppHost.Get<ISettingsStore>();
        Assert.False(panel.IsVisible);
        Assert.Equal(0, MainGrid(window).ColumnDefinitions[4].Width.Value);

        shell.ToggleRightPanelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.True(panel.IsVisible);
        WaitForRight(window, shell.RightPanelWidth);
        Assert.Equal(shell.RightPanelWidth, MainGrid(window).ColumnDefinitions[4].Width.Value);
        Assert.True(panel.Bounds.Width > 200);
        Assert.True(settings.Current.RightPanel.IsOpen);

        shell.ToggleRightPanelCommand.Execute(null);
        WaitForRight(window, 0);
        Assert.False(panel.IsVisible);
        Assert.Equal(0, MainGrid(window).ColumnDefinitions[4].Width.Value);
        Assert.False(settings.Current.RightPanel.IsOpen);
    }

    [AvaloniaFact]
    public void The_dragged_width_is_kept_within_its_limits_and_saved()
    {
        var (window, _) = Open();
        var shell = AppHost.Get<ShellViewModel>();
        shell.IsRightPanelOpen = true;
        WaitForRight(window, shell.RightPanelWidth);

        shell.CommitRightPanelWidth(900);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal(ShellViewModel.MaxRightPanelWidth, shell.RightPanelWidth);
        Assert.Equal(ShellViewModel.MaxRightPanelWidth, AppHost.Get<ISettingsStore>().Current.RightPanel.Width);
        Assert.Equal(ShellViewModel.MaxRightPanelWidth, MainGrid(window).ColumnDefinitions[4].Width.Value);

        shell.CommitRightPanelWidth(10);
        Assert.Equal(ShellViewModel.MinRightPanelWidth, shell.RightPanelWidth);
    }

    [AvaloniaFact]
    public void The_panel_follows_the_current_project()
    {
        var (window, panel) = Open();
        OpenPanel(window, panel);
        var projects = AppHost.Get<IProjectService>();
        Assert.True(panel.Model.HasNoProject);

        var a = projects.AddProject(Folder("a.txt"));
        Assert.Equal(a.Path, panel.Model.ProjectPath);
        Assert.Equal(a.Name, panel.Model.ProjectName);
        Assert.Equal(["a.txt"], panel.Model.Files.Roots.Select(n => n.Name).ToArray());

        var b = projects.AddProject(Folder("b.txt"));
        Assert.Equal(b.Path, panel.Model.ProjectPath);
        Assert.Equal(["b.txt"], panel.Model.Files.Roots.Select(n => n.Name).ToArray());

        projects.SetCurrent(a.Id);
        Assert.Equal(a.Path, panel.Model.ProjectPath);
        Assert.Equal(["a.txt"], panel.Model.Files.Roots.Select(n => n.Name).ToArray());

        projects.RenameProject(a.Id, "Renamed");
        Assert.Equal("Renamed", panel.Model.ProjectName);

        projects.RemoveProject(a.Id);
        projects.RemoveProject(b.Id);
        Assert.True(panel.Model.HasNoProject);
        Assert.Empty(panel.Model.Files.Roots);
    }

    [AvaloniaFact]
    public void A_closed_panel_reads_nothing_until_it_opens()
    {
        var (window, panel) = Open();
        Assert.False(panel.IsVisible);
        var projects = AppHost.Get<IProjectService>();

        var a = projects.AddProject(Folder("a.txt"));
        Assert.Null(panel.Model.ProjectPath);
        Assert.Empty(panel.Model.Files.Roots);

        OpenPanel(window, panel);
        Assert.Equal(a.Path, panel.Model.ProjectPath);
        Assert.Equal(["a.txt"], panel.Model.Files.Roots.Select(n => n.Name).ToArray());

        // Closed again: a file added meanwhile shows when it opens, and what was read stays.
        AppHost.Get<ShellViewModel>().ToggleRightPanelCommand.Execute(null);
        WaitForRight(window, 0);
        Assert.False(panel.IsVisible);
        File.WriteAllText(Path.Combine(a.Path, "b.txt"), "x");
        OpenPanel(window, panel);
        Assert.Equal(["a.txt", "b.txt"], panel.Model.Files.Roots.Select(n => n.Name).ToArray());
    }

    [AvaloniaFact]
    public void A_file_chosen_in_the_panel_opens_as_a_tab_and_an_open_one_is_activated()
    {
        var (window, panel) = Open();
        OpenPanel(window, panel);
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(Folder("a.txt", "b.txt"));
        var a = panel.Model.Files.Roots.Single(n => n.Name == "a.txt");
        var b = panel.Model.Files.Roots.Single(n => n.Name == "b.txt");

        panel.Model.Files.RequestOpen(a);
        var tab = Assert.Single(project.Items);
        Assert.Equal(ProjectItemKind.File, tab.Kind);
        Assert.Equal(a.FullPath, tab.FilePath);
        Assert.Equal(tab.Id, project.ActiveItemId);

        panel.Model.Files.RequestOpen(b);
        Assert.Equal(2, project.Items.Count);
        Assert.NotEqual(tab.Id, project.ActiveItemId);

        // The first file again: no second tab, the first one comes to the front.
        panel.Model.Files.RequestOpen(a);
        Assert.Equal(2, project.Items.Count);
        Assert.Equal(tab.Id, project.ActiveItemId);
    }

    [AvaloniaFact]
    public void The_top_row_lines_up_with_the_window_and_leaves_room_for_the_corner_switch()
    {
        var (window, panel) = Open();
        AppHost.Get<ShellViewModel>().IsRightPanelOpen = true;
        WaitForRight(window, AppHost.Get<ShellViewModel>().RightPanelWidth);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var topBar = panel.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "TopBar");
        var corner = window.GetVisualDescendants().OfType<StackPanel>().First(g => g.Name == "TopRight");
        Assert.Equal(40, topBar.Bounds.Height);
        // Windows' 1px window frame may push the content down by one pixel.
        Assert.InRange(topBar.TranslatePoint(default, window)!.Value.Y, 0, 1);
        // Same height as the corner switch's row, and the switch lies over the reserved end of the top row, not over its tabs.
        Assert.Equal(40, corner.Bounds.Height);
        var tabs = topBar.Children[0];
        var tabsRight = tabs.TranslatePoint(new Point(tabs.Bounds.Width, 0), window)!.Value.X;
        var cornerLeft = corner.TranslatePoint(default, window)!.Value.X;
        Assert.True(cornerLeft >= tabsRight, $"the corner switch ({cornerLeft}) overlaps the panel's tabs (end at {tabsRight})");
        Assert.True(panel.TrailingInset >= corner.Bounds.Width);
    }

    [AvaloniaFact]
    public void The_panel_slides_open_in_steps_and_the_workspace_gives_way_with_it()
    {
        var (window, panel) = Open();
        var shell = AppHost.Get<ShellViewModel>();
        var workspace = window.GetVisualDescendants().OfType<Octopus.App.Workspace.WorkspaceView>().Single();
        var closedWorkspaceWidth = workspace.Bounds.Width;

        shell.ToggleRightPanelCommand.Execute(null);
        var widths = new List<double>();
        Assert.True(Frames.Until(() => Math.Abs(RightWidth(window) - shell.RightPanelWidth) < 0.01, () => widths.Add(RightWidth(window))));

        Assert.True(widths.Count >= 5, $"only {widths.Count} frames: {string.Join(", ", widths)}");
        Assert.Equal(widths.Order().ToList(), widths);
        Assert.Contains(widths, w => w > 0 && w < shell.RightPanelWidth - 1);
        Assert.Equal(shell.RightPanelWidth, widths[^1]);
        Frames.Pump();
        window.UpdateLayout();
        // Arrived: the content is no longer pinned and has the width of the column.
        Assert.True(double.IsNaN(panel.Width));
        Assert.True(double.IsNaN(workspace.Width));
        Assert.True(workspace.Bounds.Width < closedWorkspaceWidth - shell.RightPanelWidth + 1);
    }

    [AvaloniaFact]
    public void While_the_panel_slides_the_panel_and_the_workspace_keep_their_layout_width()
    {
        var (window, panel) = Open();
        var shell = AppHost.Get<ShellViewModel>();
        var workspace = window.GetVisualDescendants().OfType<Octopus.App.Workspace.WorkspaceView>().Single();
        var closedWorkspaceWidth = workspace.Bounds.Width;

        shell.ToggleRightPanelCommand.Execute(null);
        var seen = new List<(double Panel, double Workspace)>();
        Frames.Until(() => Math.Abs(RightWidth(window) - shell.RightPanelWidth) < 0.01,
            () => { if (RightWidth(window) < shell.RightPanelWidth - 1) seen.Add((panel.Bounds.Width, workspace.Bounds.Width)); });

        Assert.NotEmpty(seen);
        // The panel is laid out once at its open width, the workspace at its closed width: nothing is squeezed on the way.
        Assert.All(seen, s => Assert.Equal(shell.RightPanelWidth, s.Panel, 0.5));
        Assert.All(seen, s => Assert.Equal(closedWorkspaceWidth, s.Workspace, 0.5));
    }

    [AvaloniaFact]
    public void Terminals_are_not_resized_on_every_frame_of_the_slide()
    {
        var (window, _) = Open();
        var projects = AppHost.Get<IProjectService>();
        projects.AddProject(Folder("a.txt"));
        AppHost.Get<IItemOpener>().OpenTerminal();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Frames.Pump();
        var connection = AppHost.Get<FakeTerminalConnectionFactory>().Connections.Single();
        var before = connection.Resizes;

        var shell = AppHost.Get<ShellViewModel>();
        shell.ToggleRightPanelCommand.Execute(null);
        var frames = 0;
        Assert.True(Frames.Until(() => Math.Abs(RightWidth(window) - shell.RightPanelWidth) < 0.01, () => frames++));
        Frames.Pump();
        window.UpdateLayout();
        Frames.Pump();
        Assert.True(frames >= 5);
        // Once, when the slide has arrived (the grid changes only when the final width differs by a whole cell).
        Assert.InRange(connection.Resizes - before, 0, 1);

        before = connection.Resizes;
        shell.ToggleRightPanelCommand.Execute(null);
        Assert.True(Frames.Until(() => RightWidth(window) < 0.01));
        Frames.Pump();
        window.UpdateLayout();
        Frames.Pump();
        Assert.InRange(connection.Resizes - before, 0, 2);
    }

    [AvaloniaFact]
    public void The_saved_state_is_shown_at_once_at_start()
    {
        AppHost.Get<ISettingsStore>().Update(s => { s.RightPanel.IsOpen = true; s.RightPanel.Width = 300; });
        var (window, panel) = Open();
        window.UpdateLayout();
        // No frames, no waiting: the panel is there as the window shows.
        Assert.Equal(300, RightWidth(window));
        Assert.True(panel.IsVisible);
        Assert.True(double.IsNaN(panel.Width));
    }

    [AvaloniaFact]
    public void A_dragged_width_is_not_animated()
    {
        var (window, _) = Open();
        var shell = AppHost.Get<ShellViewModel>();
        shell.IsRightPanelOpen = true;
        WaitForRight(window, shell.RightPanelWidth);

        shell.CommitRightPanelWidth(380);
        Assert.Equal(380, RightWidth(window));
        shell.CommitRightPanelWidth(260);
        Assert.Equal(260, RightWidth(window));
    }

    [AvaloniaFact]
    public void A_second_toggle_in_mid_slide_turns_the_panel_around_without_a_jump()
    {
        var (window, panel) = Open();
        var shell = AppHost.Get<ShellViewModel>();
        shell.ToggleRightPanelCommand.Execute(null);
        var widths = new List<double>();
        Assert.True(Frames.Until(() => RightWidth(window) > shell.RightPanelWidth * 0.4, () => widths.Add(RightWidth(window))));
        var turned = RightWidth(window);

        shell.ToggleRightPanelCommand.Execute(null);
        Assert.False(AppHost.Get<ShellViewModel>().IsRightPanelOpen);
        // Turning around starts where the slide was: no jump to open or to closed. (Frame-to-frame steps are not
        // compared: the motion runs on the clock, and a loaded machine makes a late frame a long step.)
        Assert.Equal(turned, RightWidth(window), 1.0);
        var closing = new List<double> { RightWidth(window) };
        Assert.True(Frames.Until(() => RightWidth(window) < 0.01, () => closing.Add(RightWidth(window))));
        Assert.True(closing.Count >= 3, "the panel closed at once");
        // The spring keeps its speed, so the panel coasts a little further before it turns; from that peak it only closes.
        var text = $"widths: {string.Join(", ", closing.Select(w => w.ToString("0.0")))}";
        var peak = closing.IndexOf(closing.Max());
        Assert.True(closing.Max() < shell.RightPanelWidth * 0.9, text);
        Assert.True(peak <= closing.Count / 3, text);
        var afterPeak = closing.Skip(peak).ToList();
        Assert.True(afterPeak.Zip(afterPeak.Skip(1), (a, b) => b <= a + 0.5).All(ok => ok), text);
        Frames.Pump();
        Assert.False(panel.IsVisible);
        Assert.Equal(0, RightWidth(window));
    }

    [AvaloniaFact]
    public void The_sidebar_slides_away_and_back_in_steps()
    {
        var (window, _) = Open();
        AppHost.Get<IProjectService>().AddProject(Folder("a.txt"));
        var shell = AppHost.Get<ShellViewModel>();
        var main = MainGrid(window);
        var full = main.ColumnDefinitions[0].Width.Value;
        Assert.True(full > 200);

        shell.ToggleSidebarCommand.Execute(null);
        var widths = new List<double>();
        Assert.True(Frames.Until(() => main.ColumnDefinitions[0].Width.Value < 0.01, () => widths.Add(main.ColumnDefinitions[0].Width.Value)));
        Assert.True(widths.Count >= 5);
        Assert.Equal(widths.OrderDescending().ToList(), widths);
        Frames.Pump();
        window.UpdateLayout();
        var lead = window.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "CollapsedLead");
        Assert.Equal(1, lead.Opacity);

        shell.ToggleSidebarCommand.Execute(null);
        Assert.Equal(0, lead.Opacity);
        widths.Clear();
        Assert.True(Frames.Until(() => Math.Abs(main.ColumnDefinitions[0].Width.Value - full) < 0.01, () => widths.Add(main.ColumnDefinitions[0].Width.Value)));
        Assert.True(widths.Count >= 5);
        Assert.Equal(widths.Order().ToList(), widths);
        Frames.Pump();
        window.UpdateLayout();
        Assert.Equal(0, lead.Opacity);
        Assert.True(window.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "LeftArea").Bounds.Width > 200);
    }
}
