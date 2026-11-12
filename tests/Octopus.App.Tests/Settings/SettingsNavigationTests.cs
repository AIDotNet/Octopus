using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Settings;
using ArcSidebar = TKCode.Arc.Sidebar;
using Xunit;

namespace Octopus.App.Tests.Settings;

/// <summary>The settings menu is Arc's sidebar, built from the view model's groups.</summary>
public class SettingsNavigationTests
{
    public SettingsNavigationTests() => SettingsTestSupport.Reset();

    private static (Window Window, SettingsView View, ArcSidebar Nav, SettingsViewModel Model) Open(string? section = null)
    {
        var view = new SettingsView { InitialSection = section };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Frames.Pump();
        var nav = view.GetVisualDescendants().OfType<ArcSidebar>().Single();
        return (window, view, nav, (SettingsViewModel)view.DataContext!);
    }

    [AvaloniaFact]
    public void The_menu_has_one_section_per_group_and_one_page_per_section()
    {
        var (window, _, nav, model) = Open();

        Assert.Equal(model.Groups.Count, nav.Sections.Count);
        Assert.Equal(model.All.Select(s => s.Key), nav.Sections.SelectMany(s => s.Items).Select(i => i.Key));
        Assert.Equal(model.All.Select(s => s.Title), nav.Sections.SelectMany(s => s.Items).Select(i => i.Label));
        Assert.All(nav.Sections, s => Assert.False(string.IsNullOrEmpty(s.Header)));
        window.Close();
    }

    [AvaloniaFact]
    public void The_selection_follows_the_open_page_both_ways()
    {
        var (window, _, nav, model) = Open("appearance");
        Assert.Equal("appearance", nav.SelectedKey);

        nav.SelectedKey = "terminal";
        Frames.Pump();
        Assert.Equal("terminal", model.Selected.Key);

        model.Show("plugins");
        Frames.Pump();
        Assert.Equal("plugins", nav.SelectedKey);
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_a_page_in_the_menu_opens_it()
    {
        var (window, view, nav, model) = Open("agents");
        var label = model.All.Single(s => s.Key == "terminal").Title;
        var text = nav.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == label && t.IsEffectivelyVisible);
        var center = text.TranslatePoint(new Point(text.Bounds.Width / 2, text.Bounds.Height / 2), window)!.Value;

        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Frames.Pump();

        Assert.Equal("terminal", model.Selected.Key);
        Assert.IsType<Octopus.App.Settings.Terminal.TerminalSettingsViewModel>(model.Page);
        _ = view;
        window.Close();
    }
}
