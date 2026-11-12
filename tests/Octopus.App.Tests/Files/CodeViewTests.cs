using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Octopus.App.Files;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests.Files;

public class CodeViewTests
{
    private static (Window Window, ScrollArea Scroller, CodeView View) Host(string text, string? language, double width = 800, double height = 500)
    {
        var view = new CodeView
        {
            Text = text,
            Language = language,
            FontSize = 13,
            Background = Brushes.White,
            Foreground = Brushes.Black,
            GutterForeground = Brushes.Gray,
            SelectionBrush = Brushes.LightSkyBlue,
            HoverBrush = Brushes.WhiteSmoke,
        };
        var scroller = new ScrollArea { Orientation = ScrollAreaOrientation.Both, FadeLength = 0, Content = view };
        var window = new Window { Width = width, Height = height, Content = scroller };
        window.Show();
        Frames.Pump();
        return (window, scroller, view);
    }

    private static async Task Highlighted(CodeView view)
    {
        for (var i = 0; i < 1200 && !view.WhenHighlighted.IsCompleted; i++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(view.WhenHighlighted.IsCompleted, "highlighting did not finish");
        Dispatcher.UIThread.RunJobs();
    }

    private static string Lines(int count) => string.Concat(Enumerable.Range(1, count).Select(i => $"    var value{i} = Compute({i}); // line {i}\n"));

    [AvaloniaFact]
    public void A_file_of_twenty_thousand_lines_only_lays_out_the_rows_in_view()
    {
        var (window, scroller, view) = Host(Lines(20_000), null);
        Assert.Equal(20_000, view.LineCount);
        Assert.Equal(20_000, view.TotalRows);
        Assert.InRange(view.RenderedRows, 1, 80);
        Assert.InRange(view.LayoutsCreated, 1, 120);
        Assert.True(scroller.Extent.Height >= 20_000 * view.RowHeight);

        // Scrolling far down lays out another screenful, not the file.
        scroller.Offset = new Vector(0, 15_000 * view.RowHeight);
        Frames.Pump();
        Assert.InRange(view.RenderedRows, 1, 80);
        Assert.InRange(view.LayoutsCreated, 1, 300);
        window.Close();
    }

    [AvaloniaFact]
    public void A_selection_across_lines_copies_the_text_between_its_ends()
    {
        var (window, _, view) = Host("alpha beta\r\ngamma delta\r\n\tepsilon zeta\r\neta\r\n", null);
        Assert.Equal(4, view.LineCount);
        view.Select(0, 6, 2, 8);
        Assert.Equal("beta\ngamma delta\n\tepsilon", view.SelectedText);
        view.Select(2, 8, 0, 6); // backwards gives the same text
        Assert.Equal("beta\ngamma delta\n\tepsilon", view.SelectedText);
        view.SelectAll();
        Assert.Equal("alpha beta\ngamma delta\n\tepsilon zeta\neta", view.SelectedText);
        view.ClearSelection();
        Assert.Equal("", view.SelectedText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_mouse_selects_and_the_platform_copy_shortcut_puts_it_on_the_clipboard()
    {
        var (window, _, view) = Host("first line\nsecond line\nthird line\n", null);
        var origin = new Point(view.GutterWidth + view.CharWidth * 0.5 + 1, view.RowHeight * 0.5);
        window.MouseDown(origin, MouseButton.Left);
        window.MouseMove(origin + new Point(view.CharWidth * 4, view.RowHeight));
        window.MouseUp(origin + new Point(view.CharWidth * 4, view.RowHeight), MouseButton.Left);
        Frames.Pump();
        Assert.True(view.HasSelection);
        var selected = view.SelectedText;
        Assert.StartsWith("first line\nsec", selected);   // where exactly it ends depends on the font the host picked
        Assert.True(selected.Length <= "first line\nsecond".Length);

        // The shortcut is whatever the platform says copy is: the test asks for it the way the view does.
        var copy = Application.Current!.PlatformSettings!.HotkeyConfiguration.Copy[0];
        var clipboard = window.Clipboard!;
        await clipboard.ClearAsync();
        window.KeyPress(copy.Key, (RawInputModifiers)(int)copy.KeyModifiers, PhysicalKey.None, null);
        Frames.Pump();
        Assert.Equal(selected, await clipboard.TryGetTextAsync());

        var selectAll = Application.Current.PlatformSettings!.HotkeyConfiguration.SelectAll[0];
        window.KeyPress(selectAll.Key, (RawInputModifiers)(int)selectAll.KeyModifiers, PhysicalKey.None, null);
        Assert.Equal("first line\nsecond line\nthird line", view.SelectedText);
        window.Close();
    }

    [AvaloniaFact]
    public void Wrapping_adds_rows_for_long_lines_and_removes_the_sideways_scroll()
    {
        var text = "short\n" + string.Join(' ', Enumerable.Repeat("word", 120)) + "\nend\n";
        var (window, scroller, view) = Host(text, null, 600, 400);
        Assert.Equal(3, view.TotalRows);
        Assert.True(scroller.Extent.Width > scroller.Viewport.Width);

        view.WrapLines = true;
        Frames.Pump();
        Assert.Equal(3, view.LineCount);
        Assert.True(view.TotalRows > 5, $"rows: {view.TotalRows}");
        Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 1);

        view.WrapLines = false;
        Frames.Pump();
        Assert.Equal(3, view.TotalRows);
        window.Close();
    }

    [AvaloniaFact]
    public void The_line_under_the_pointer_is_tracked()
    {
        var (window, _, view) = Host("a\nb\nc\n", null);
        window.MouseMove(new Point(view.GutterWidth + 20, view.RowHeight * 1.5));
        Frames.Pump();
        Assert.Equal(1, view.HoverLine);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Json_is_colored_in_several_roles_from_arcs_palette()
    {
        var (window, _, view) = Host("{\n  \"name\": \"octopus\",\n  \"count\": 42,\n  \"ok\": true,\n  \"tags\": [\"a\", \"b\"]\n}\n", "json");
        await Highlighted(view);
        var roles = view.SyntaxRolesInUse;
        Assert.True(roles.Count >= 3, "roles: " + string.Join(",", roles));
        Assert.Contains(SyntaxRole.Property, roles);
        Assert.Contains(SyntaxRole.String, roles);
        Assert.Contains(SyntaxRole.Number, roles);
        var colors = roles.Select(r => (view.BrushOf(r) as ISolidColorBrush)?.Color).Distinct().ToList();
        Assert.True(colors.Count >= 3, "distinct colors: " + colors.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Csharp_is_colored_in_several_roles()
    {
        var (window, _, view) = Host("using System;\n\n// a comment\npublic class Cache<T>\n{\n    private readonly int _size = 42;\n    public string Name => \"cache\";\n    public void Run() { Console.WriteLine(Name); }\n}\n", "cs");
        await Highlighted(view);
        var roles = view.SyntaxRolesInUse;
        Assert.True(roles.Count >= 5, "roles: " + string.Join(",", roles));
        Assert.Contains(SyntaxRole.Keyword, roles);
        Assert.Contains(SyntaxRole.Comment, roles);
        Assert.Contains(SyntaxRole.String, roles);
        Assert.Contains(SyntaxRole.Number, roles);
        var colors = roles.Select(r => (view.BrushOf(r) as ISolidColorBrush)?.Color).Distinct().ToList();
        Assert.True(colors.Count >= 4, "distinct colors: " + colors.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_large_file_is_colored_in_the_background_without_blocking_the_first_frame()
    {
        var (window, _, view) = Host(Lines(20_000), "cs");
        // The first frame is already on screen (plain) before the colors are in.
        Assert.InRange(view.RenderedRows, 1, 80);
        await Highlighted(view);
        Assert.Contains(SyntaxRole.Keyword, view.SyntaxRolesInUse);
        window.Close();
    }

    [AvaloniaFact]
    public void The_syntax_colors_follow_the_theme()
    {
        var (window, _, view) = Host("class A { }\n", "cs");
        try
        {
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Light));
            Frames.Pump();
            var light = (view.BrushOf(SyntaxRole.Keyword) as ISolidColorBrush)?.Color;
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
            Frames.Pump();
            Frames.Until(() => !Equals((view.BrushOf(SyntaxRole.Keyword) as ISolidColorBrush)?.Color, light), null, 1500);
            var dark = (view.BrushOf(SyntaxRole.Keyword) as ISolidColorBrush)?.Color;
            Assert.NotNull(light);
            Assert.NotNull(dark);
            Assert.NotEqual(light, dark);
        }
        finally
        {
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Light));
            window.Close();
        }
    }
}
