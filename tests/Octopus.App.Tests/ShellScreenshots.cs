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
