using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Octopus.Agents;
using Octopus.App.Composition;
using Octopus.App.Launcher;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Storage;
using Octopus.Terminal;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>
/// Renders the main window with Skia on the headless platform and saves PNGs to <c>bin/shots</c> (the files are looked at
/// by a person: there is nothing to assert but that rendering works).
/// </summary>
public class ShellScreenshots
{
    private static readonly string s_dir = Path.Combine(AppContext.BaseDirectory, "shots");

    [AvaloniaFact]
    public void Render_the_main_window()
    {
        Directory.CreateDirectory(s_dir);
        foreach (var dark in new[] { false, true })
        {
            ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
            RenderAll(dark ? "dark" : "light");
        }
    }

    private static void RenderAll(string suffix)
    {
        // Empty: no project.
        TestApp.Fresh();
        var window = Open();
        Save(window, "empty-no-project", suffix);
        window.Close();

        // A project without items.
        var root = Directory.CreateTempSubdirectory("octopus-shot-").FullName;
        var octopus = Directory.CreateDirectory(Path.Combine(root, "Octopus")).FullName;
        var atlas = Directory.CreateDirectory(Path.Combine(root, "Atlas")).FullName;
        File.WriteAllText(Path.Combine(octopus, "README.md"), "# Octopus\n\nA terminal and project manager for coding agents.\n\n    dotnet run --project src/Octopus.App\n");
        TestApp.Fresh();
        window = Open();
        var projects = AppHost.Get<IProjectService>();
        projects.AddProject(atlas);
        Save(window, "empty-project", suffix);

        // The regular state: two projects, items.
        var project = projects.AddProject(octopus);
        var opener = AppHost.Get<IItemOpener>();
        opener.OpenAgent("claude", new AgentLaunchOptions { Model = "fable", Effort = "xhigh" });
        opener.OpenAgent("codex", new AgentLaunchOptions { Model = "gpt-5" });
        opener.OpenTerminal();
        opener.OpenTerminal();
        opener.OpenFile(Path.Combine(octopus, "README.md"));
        projects.ActivateItem(project.Items[0].Id);
        Frames.Pump();
        Save(window, "main", suffix);

        // An agent at work: the session's name in the tab and the row, the loading ring in both.
        var activity = AppHost.Get<Octopus.App.Activity.IItemActivityService>();
        activity.ReportTitle(project.Items[0].Id, "⠂ Fix the login test");
        activity.ReportTitle(project.Items[1].Id, "⠐ Review the parser");
        Save(window, "main-working", suffix);
        activity.ReportTitle(project.Items[0].Id, "✳ Claude Code");
        activity.ReportTitle(project.Items[1].Id, "Codex CLI");
        Frames.Pump();

        // The new-tab menu with the Claude panel.
        var button = window.GetVisualDescendants().OfType<Octopus.App.Workspace.StripAddButton>().First();
        LauncherRequest.Raise(button);
        Frames.Pump();
        var launcher = AppHost.Get<LauncherViewModel>();
        launcher.Expand(launcher.Agents.First(a => a.Id == "claude"));
        Frames.Pump();
        Save(window, "launcher-claude", suffix);
        launcher.Expand(launcher.Agents.First(a => a.Id == "opencode"));
        Frames.Pump();
        Save(window, "launcher-opencode", suffix);
        launcher.Expand(null);
        Frames.Pump();
        Save(window, "launcher", suffix);
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Escape, Avalonia.Input.RawInputModifiers.None);
        // The menu fades out: wait for the animation, or the next frame shows it half gone.
        Thread.Sleep(500);
        Frames.Pump();

        // Split into two groups.
        AppHost.Get<Octopus.App.Workspace.IWorkspaceCommands>().SplitRight();
        Frames.Pump();
        Save(window, "split", suffix);

        // The right panel open; a frame from the middle of the slide on the way.
        AppHost.Get<ShellViewModel>().IsRightPanelOpen = true;
        var main = window.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "Main");
        var half = AppHost.Get<ShellViewModel>().RightPanelWidth / 2;
        Frames.Until(() => main.ColumnDefinitions[4].Width.Value > half);
        Save(window, "right-panel-mid", suffix);
        Frames.Until(() => main.ColumnDefinitions[4].Width.Value >= AppHost.Get<ShellViewModel>().RightPanelWidth - 0.01);
        Frames.Pump();
        Save(window, "right-panel", suffix);
        AppHost.Get<ShellViewModel>().IsRightPanelOpen = false;
        Frames.Until(() => main.ColumnDefinitions[4].Width.Value < 0.01);
        Frames.Pump();

        // The sidebar collapsed (and a frame from the middle of its slide).
        AppHost.Get<ShellViewModel>().IsSidebarCollapsed = true;
        var full = main.ColumnDefinitions[0].Width.Value;
        Frames.Until(() => main.ColumnDefinitions[0].Width.Value < full / 2);
        Save(window, "collapsed-mid", suffix);
        Frames.Until(() => main.ColumnDefinitions[0].Width.Value < 0.01);
        Frames.Pump();
        Save(window, "collapsed", suffix);

        // Many tabs: the strip overflows.
        for (var i = 0; i < 25; i++)
        {
            opener.OpenTerminal();
        }
        Frames.Pump();
        Save(window, "overflow", suffix);
        window.Close();
        TestApp.Fresh();
    }

    [AvaloniaFact]
    public void Render_the_rail_with_groups()
    {
        Directory.CreateDirectory(s_dir);
        foreach (var dark in new[] { false, true })
        {
            var suffix = dark ? "dark" : "light";
            ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
            TestApp.Fresh();
            var window = Open();
            var projects = AppHost.Get<IProjectService>();
            var root = Directory.CreateTempSubdirectory("octopus-shot-groups-").FullName;
            Project Add(string name) => projects.AddProject(Directory.CreateDirectory(Path.Combine(root, name)).FullName);
            var octopus = Add("Octopus");
            var oneAi = Add("OneAI");
            var stream = Add("StreamMark");
            var gateway = Add("Gateway");
            var arc = Add("Arc");
            var jaunt = Add("Jaunt");
            var lucide = Add("Lucide");
            var portal = Add("Portal");
            var work = projects.AddGroup("Work", [oneAi.Id, stream.Id, gateway.Id]);
            var oss = projects.AddGroup("Open source", [arc.Id, jaunt.Id, lucide.Id]);
            projects.AddGroup("Clients", [portal.Id]);
            // Agents at work: in a project in no group, in the open group and in a closed one (its folder shows it).
            // Opened the way the app opens them: in the current project (an item added to another project pulls the
            // workspace over to it).
            var activity = AppHost.Get<Octopus.App.Activity.IItemActivityService>();
            var opener = AppHost.Get<IItemOpener>();
            foreach (var busy in new[] { octopus, stream, portal })
            {
                projects.SetCurrent(busy.Id);
                opener.OpenAgent("claude", new AgentLaunchOptions { Model = "fable" });
                Frames.Pump();
                activity.ReportTitle(busy.Items[^1].Id, "⠂ Fix the login test");
            }
            projects.SetCurrent(oneAi.Id);
            // Pixel icons on some squares.
            foreach (var (project, icon) in new[] { (octopus, "alien"), (oneAi, "robot-face-happy"), (stream, "t-rex"), (arc, "castle"), (jaunt, "snail"), (lucide, "potion") })
            {
                projects.SetProjectAppearance(project.Id, project.Color, "pixel:" + icon, null);
            }

            // "Work" open, the others closed.
            projects.SetExpandedGroup(work.Id);
            Frames.Pump();
            Save(window, "groups-open", suffix);

            // All closed: the folder of the current project takes the ring.
            projects.SetExpandedGroup(null);
            Frames.Until(() => window.GetVisualDescendants().OfType<Octopus.App.Sidebar.ProjectGroupView>()
                .All(v => v.FindControl<Panel>("Capsule")?.IsVisible == false));
            Frames.Pump();
            Save(window, "groups-closed", suffix);

            // A frame from the middle of opening "Open source": the folder fades into the capsule as it grows.
            var view = window.GetVisualDescendants().OfType<Octopus.App.Sidebar.ProjectGroupView>()
                .First(v => (v.DataContext as Octopus.App.Sidebar.ProjectGroupViewModel)?.Id == oss.Id);
            var folder = view.FindControl<Grid>("Folder")!;
            projects.SetExpandedGroup(oss.Id);
            Frames.Until(() => folder.Opacity < 0.6);
            Save(window, "groups-opening", suffix);
            window.Close();
        }
        TestApp.Fresh();
    }

    [AvaloniaFact]
    public void Render_the_icon_dialog()
    {
        Directory.CreateDirectory(s_dir);
        foreach (var dark in new[] { false, true })
        {
            ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
            TestApp.Fresh();
            var project = new Project { Name = "OneAI", Path = "/Users/me/code/OneAI", Color = "#10B981", Icon = "pixel:t-rex" };
            var form = new Octopus.App.Dialogs.ProjectAppearanceView(project, null, null);
            var window = new Window { Width = 400, Height = 640, Content = new Border { Padding = new Thickness(22), Child = form } };
            window.Show();
            Frames.Pump();
            Save(window, "icon-dialog-fun", dark ? "dark" : "light");
            form.ShowCategory("brands");
            Frames.Pump();
            Save(window, "icon-dialog-brands", dark ? "dark" : "light");
            window.Close();
        }
        TestApp.Fresh();
    }

    private static Window Open()
    {
        var window = new MainWindow();
        window.Show();
        Frames.Pump();
        return window;
    }

    private static void Save(Window window, string name, string theme)
    {
        Frames.Save(window, Path.Combine(s_dir, $"{name}-{theme}.png"));
    }
}
