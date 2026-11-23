using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using Octopus.App.Appearance;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.App.Sidebar;
using Octopus.Core.Settings;
using Xunit;

namespace Octopus.App.Tests;

public class ProjectNameEditTests
{
    [AvaloniaFact]
    public void Enter_commits_trimmed_and_collapsed_project_name()
    {
        TestApp.Fresh();
        var root = Directory.CreateTempSubdirectory("octopus-rename-").FullName;
        var dir = Directory.CreateDirectory(Path.Combine(root, "NextCoWork")).FullName;
        var window = Open();
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(dir);
            Frames.Pump();

            var panel = window.GetVisualDescendants().OfType<ProjectPanelView>().First();
            var display = panel.FindControl<Avalonia.Controls.Button>("NameDisplayButton")!;
            var pencil = panel.FindControl<Control>("NameEditIcon")!;
            var editorFrame = panel.FindControl<Border>("NameEditFrame")!;
            var input = panel.FindControl<TextBox>("NameInput")!;

            Assert.Equal(0, pencil.Opacity, precision: 2);
            var restBounds = display.TranslatePoint(default, panel)!.Value;
            var restSize = display.Bounds.Size;

            panel.BeginNameEdit();
            Frames.Pump();

            Assert.True(panel.IsEditingProjectName);
            Assert.False(display.IsVisible);
            Assert.True(editorFrame.IsVisible);
            Assert.Equal("NextCoWork", input.Text);

            // Exact same position and height in edit mode: zero layout shift, zero left clipping.
            var editOrigin = editorFrame.TranslatePoint(default, panel)!.Value;
            Assert.True(editOrigin.X >= 6, $"Editor frame should stay inside panel bounds, got X={editOrigin.X}");
            Assert.Equal(restBounds.X, editOrigin.X, precision: 1);
            Assert.Equal(restBounds.Y, editOrigin.Y, precision: 1);
            Assert.Equal(restSize.Height, editorFrame.Bounds.Height, precision: 1);

            input.Text = "  Next   CoWork\nStudio  ";
            Frames.Pump();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Frames.Pump();

            Assert.False(panel.IsEditingProjectName);
            Assert.True(display.IsVisible);
            Assert.False(editorFrame.IsVisible);
            Assert.Equal("Next CoWork Studio", project.Name);
            Assert.Equal("Next CoWork Studio", AppHost.Get<ProjectPanelViewModel>().ProjectName);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Escape_cancels_edit_without_changing_name()
    {
        TestApp.Fresh();
        var root = Directory.CreateTempSubdirectory("octopus-rename-").FullName;
        var dir = Directory.CreateDirectory(Path.Combine(root, "Octopus")).FullName;
        var window = Open();
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(dir);
            Frames.Pump();

            var panel = window.GetVisualDescendants().OfType<ProjectPanelView>().First();
            var input = panel.FindControl<TextBox>("NameInput")!;

            panel.BeginNameEdit();
            Frames.Pump();
            input.Text = "Temporary Name";
            Frames.Pump();

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Frames.Pump();

            Assert.False(panel.IsEditingProjectName);
            Assert.Equal("Octopus", project.Name);
            Assert.Equal("Octopus", AppHost.Get<ProjectPanelViewModel>().ProjectName);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Blank_name_disables_save_button_and_reverts_on_commit()
    {
        TestApp.Fresh();
        var root = Directory.CreateTempSubdirectory("octopus-rename-").FullName;
        var dir = Directory.CreateDirectory(Path.Combine(root, "Octopus")).FullName;
        var window = Open();
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(dir);
            Frames.Pump();

            var panel = window.GetVisualDescendants().OfType<ProjectPanelView>().First();
            var input = panel.FindControl<TextBox>("NameInput")!;
            var save = panel.FindControl<Avalonia.Controls.Button>("NameSaveButton")!;

            panel.BeginNameEdit();
            Frames.Pump();
            Assert.True(save.IsEnabled);

            input.Text = "   ";
            Frames.Pump();
            Assert.False(save.IsEnabled);

            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Frames.Pump();

            Assert.False(panel.IsEditingProjectName);
            Assert.Equal("Octopus", project.Name);
            Assert.Equal("Octopus", AppHost.Get<ProjectPanelViewModel>().ProjectName);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Switching_projects_while_editing_renames_original_project_not_new_one()
    {
        TestApp.Fresh();
        var root = Directory.CreateTempSubdirectory("octopus-rename-").FullName;
        var dirA = Directory.CreateDirectory(Path.Combine(root, "Alpha")).FullName;
        var dirB = Directory.CreateDirectory(Path.Combine(root, "Beta")).FullName;
        var window = Open();
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var a = projects.AddProject(dirA);
            var b = projects.AddProject(dirB);
            projects.SetCurrent(a.Id);
            Frames.Pump();

            var panel = window.GetVisualDescendants().OfType<ProjectPanelView>().First();
            var input = panel.FindControl<TextBox>("NameInput")!;

            panel.BeginNameEdit();
            Frames.Pump();
            input.Text = "Alpha Renamed";
            Frames.Pump();

            projects.SetCurrent(b.Id);
            Frames.Pump();

            Assert.False(panel.IsEditingProjectName);
            Assert.Equal("Alpha Renamed", a.Name);
            Assert.Equal("Beta", b.Name);
            Assert.Equal("Beta", AppHost.Get<ProjectPanelViewModel>().ProjectName);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RenameProjectRequested_opens_inline_editor()
    {
        TestApp.Fresh();
        var root = Directory.CreateTempSubdirectory("octopus-rename-").FullName;
        var dir = Directory.CreateDirectory(Path.Combine(root, "NextCoWork")).FullName;
        var window = Open();
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(dir);
            Frames.Pump();

            var panel = window.GetVisualDescendants().OfType<ProjectPanelView>().First();
            WeakReferenceMessenger.Default.Send(new RenameProjectRequested(project.Id));
            Frames.Pump();

            Assert.True(panel.IsEditingProjectName);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Text_aligns_with_project_path_in_both_densities()
    {
        foreach (var density in new[] { UiDensityValues.Compact, UiDensityValues.Comfortable })
        {
            TestApp.Fresh();
            AppHost.Get<ThemeService>().SetDensity(density);
            var root = Directory.CreateTempSubdirectory("octopus-rename-").FullName;
            var dir = Directory.CreateDirectory(Path.Combine(root, "NextCoWork")).FullName;
            var window = Open();
            try
            {
                var projects = AppHost.Get<IProjectService>();
                projects.AddProject(dir);
                Frames.Pump();

                var panel = window.GetVisualDescendants().OfType<ProjectPanelView>().First();
                var displayText = panel.FindControl<TextBlock>("NameDisplayText")!;
                var editorFrame = panel.FindControl<Border>("NameEditFrame")!;
                var input = panel.FindControl<TextBox>("NameInput")!;
                var pathText = panel.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.Text == dir);

                var pathX = pathText.TranslatePoint(default, panel)!.Value.X;
                var displayTextX = displayText.TranslatePoint(default, panel)!.Value.X;
                Assert.Equal(pathX, displayTextX, precision: 0);

                panel.BeginNameEdit();
                Frames.Pump();

                var inputX = input.TranslatePoint(default, panel)!.Value.X;
                var frameBottom = editorFrame.TranslatePoint(new Point(0, editorFrame.Bounds.Height), panel)!.Value.Y;
                var pathTop = pathText.TranslatePoint(default, panel)!.Value.Y;
                Assert.Equal(pathX, inputX, precision: 0);
                Assert.True(frameBottom < pathTop, $"Editor bottom ({frameBottom}) must not touch path top ({pathTop}) in {density}");
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void Save_and_cancel_buttons_commit_or_discard_without_stealing_focus_prematurely()
    {
        TestApp.Fresh();
        var root = Directory.CreateTempSubdirectory("octopus-rename-").FullName;
        var dir = Directory.CreateDirectory(Path.Combine(root, "NextCoWork")).FullName;
        var window = Open();
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(dir);
            Frames.Pump();

            var panel = window.GetVisualDescendants().OfType<ProjectPanelView>().First();
            var input = panel.FindControl<TextBox>("NameInput")!;
            var save = panel.FindControl<Avalonia.Controls.Button>("NameSaveButton")!;
            var cancel = panel.FindControl<Avalonia.Controls.Button>("NameCancelButton")!;

            Assert.False(save.Focusable);
            Assert.False(cancel.Focusable);

            panel.BeginNameEdit();
            Frames.Pump();
            input.Text = "Canceled Name";
            Frames.Pump();
            cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Frames.Pump();

            Assert.False(panel.IsEditingProjectName);
            Assert.Equal("NextCoWork", project.Name);

            panel.BeginNameEdit();
            Frames.Pump();
            input.Text = "Saved Name";
            Frames.Pump();
            save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Frames.Pump();

            Assert.False(panel.IsEditingProjectName);
            Assert.Equal("Saved Name", project.Name);
        }
        finally
        {
            window.Close();
        }
    }

    private static Window Open()
    {
        var window = new MainWindow();
        window.Show();
        Frames.Pump();
        return window;
    }
}
