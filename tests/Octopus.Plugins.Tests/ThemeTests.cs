using Octopus.Core.Plugins;
using Octopus.Plugins.Manifests;
using Octopus.Plugins.Themes;
using Xunit;

namespace Octopus.Plugins.Tests;

public class ThemeReaderTests
{
    private const string TokyoNight = """
        {
          "schema": 1,
          "name": "Tokyo Night",
          "appearance": "dark",
          "accent": "#7aa2f7",
          "corners": 0.4,
          "density": "compact",
          "fontScale": 1.1,
          "colors": { "Background": "#1A1B26", "Surface": "#1F2335", "Foreground": "#C0CAF5", "TextSecondary": "#A9B1D6" },
          "controls": { "DockGroupView": { "StripBackground": "#16161E" } },
          "terminal": {
            "foreground": "#C0CAF5", "background": "#1A1B26", "cursor": "#C0CAF5", "selection": "#33467C",
            "ansi": ["#15161E","#F7768E","#9ECE6A","#E0AF68","#7AA2F7","#BB9AF7","#7DCFFF","#A9B1D6",
                     "#414868","#F7768E","#9ECE6A","#E0AF68","#7AA2F7","#BB9AF7","#7DCFFF","#C0CAF5"]
          },
          "syntax": { "comment": "#565F89", "keyword": "#BB9AF7" },
          "app": { "git.added": "#9ECE6A", "diff.addedBackground": "#9ECE6A22", "project.blue": "#7AA2F7" },
          "fonts": { "ui": "Inter", "mono": "JetBrains Mono" }
        }
        """;

    [Fact]
    public void Reads_every_section_and_normalizes_colors()
    {
        var result = ThemeReader.Parse(TokyoNight);

        Assert.True(result.IsUsable);
        Assert.Empty(result.Issues);
        var t = result.Theme!;
        Assert.Equal(ThemeAppearance.Dark, t.Appearance);
        Assert.Equal("#7AA2F7", t.Accent);
        Assert.Equal(ThemeDensity.Compact, t.Density);
        Assert.Equal(1.1, t.FontScale);
        Assert.Equal("#1A1B26", t.Colors["Background"]);
        Assert.Equal("#16161E", t.Controls["DockGroupView"]["StripBackground"]);
        Assert.Equal(16, t.Terminal!.Ansi!.Count);
        Assert.Equal("#9ECE6A22", t.App["diff.addedBackground"]);
        Assert.Equal("#7AA2F7", t.App["project.blue"]);
        Assert.Equal("JetBrains Mono", t.MonoFont);
    }

    [Fact]
    public void Bad_values_are_dropped_with_warnings_never_the_whole_theme()
    {
        var result = ThemeReader.Parse("""
            {
              "appearance": "sepia",
              "accent": "blue",
              "fontScale": 3,
              "corners": "round",
              "density": "huge",
              "colors": { "Background": "#000", "ControlSizeMD": "#FFFFFF", "Foreground": "#EEEEEE" },
              "controls": { "Button": { "Padding": "4,8" } },
              "terminal": { "ansi": ["#000000"] },
              "syntax": { "operator": "#FFFFFF" },
              "app": { "sidebar.background": "#FFFFFF" },
              "sounds": {}
            }
            """);

        Assert.True(result.IsUsable);
        Assert.All(result.Issues, i => Assert.Equal(IssueSeverity.Warning, i.Severity));
        var t = result.Theme!;
        Assert.Null(t.Appearance);
        Assert.Null(t.Accent);
        Assert.Equal(ThemeReader.MaxFontScale, t.FontScale);
        Assert.Null(t.Corners);
        Assert.Null(t.Density);
        Assert.Equal(["Foreground"], t.Colors.Keys);
        Assert.Empty(t.Controls);
        Assert.Null(t.Terminal);
        Assert.Empty(t.Syntax);
        Assert.Empty(t.App);
        foreach (var path in new[] { "$.appearance", "$.accent", "$.fontScale", "$.corners", "$.density", "$.colors.Background", "$.colors.ControlSizeMD",
                     "$.controls.Button.Padding", "$.terminal.ansi", "$.syntax.operator", "$.app.sidebar.background", "$.sounds" })
        {
            Assert.Contains(result.Issues, i => i.Path == path);
        }
    }

    [Fact]
    public void Only_broken_json_or_another_schema_make_a_theme_unusable()
    {
        Assert.False(ThemeReader.Parse("{ \"colors\": ").IsUsable);
        Assert.False(ThemeReader.Parse("[]").IsUsable);
        Assert.False(ThemeReader.Parse("""{ "schema": 2 }""").IsUsable);
        Assert.True(ThemeReader.Parse("{}").IsUsable);
    }

    [Fact]
    public void A_mislabeled_appearance_is_pointed_out()
    {
        var result = ThemeReader.Parse("""{ "appearance": "dark", "colors": { "Background": "#FAFAFA", "Foreground": "#111111" } }""");
        Assert.Contains(result.Issues, i => i.Path == "$.appearance" && i.Message.Contains("lighter"));
    }
}

public class ThemeComposerTests
{
    private static ThemeDefinition Theme(string json) => ThemeReader.Parse(json).Theme!;

    [Fact]
    public void Contrast_ratios_match_wcag()
    {
        ThemeColor.TryParse("#000000", out var black);
        ThemeColor.TryParse("#FFFFFF", out var white);
        ThemeColor.TryParse("#777777", out var grey);
        Assert.Equal(21.0, ThemeColor.Contrast(white, black), 1);
        Assert.Equal(1.0, ThemeColor.Contrast(grey, grey), 2);
        Assert.Equal(4.48, ThemeColor.Contrast(grey, white), 2);
    }

    [Fact]
    public void Layers_apply_in_order_theme_then_user_then_overrides()
    {
        var theme = Theme("""{ "accent": "#112233", "density": "compact", "fontScale": 0.9, "colors": { "Background": "#101010", "Foreground": "#F0F0F0" }, "terminal": { "foreground": "#EEEEEE", "cursor": "#FF0000" } }""");
        var user = new UserAppearance(Accent: "#ABCDEF", FontScale: 1.15);
        var plugin = Theme("""{ "colors": { "Surface": "#202020" }, "terminal": { "cursor": "#00FF00" } }""");

        var composed = ThemeComposer.Compose(ThemeAppearance.Dark, theme, user, [plugin]);

        Assert.Equal("#ABCDEF", composed.Accent);
        Assert.Equal(ThemeDensity.Compact, composed.Density);
        Assert.Equal(1.15, composed.FontScale);
        Assert.Equal("#101010", composed.Colors["Background"]);
        Assert.Equal("#202020", composed.Colors["Surface"]);
        Assert.Equal("#EEEEEE", composed.Terminal!.Foreground);
        Assert.Equal("#00FF00", composed.Terminal.Cursor);
        Assert.Equal(ThemeComposer.DefaultCorners, composed.Corners);
        Assert.True(composed.IsAcceptable);
    }

    [Fact]
    public void Defaults_without_any_theme()
    {
        var composed = ThemeComposer.Compose(ThemeAppearance.Light, null, UserAppearance.FollowTheme);
        Assert.Equal(ThemeDensity.Standard, composed.Density);
        Assert.Equal(1.0, composed.FontScale);
        Assert.Empty(composed.Colors);
        Assert.True(composed.IsAcceptable);
    }

    [Fact]
    public void Unreadable_text_refuses_the_theme()
    {
        var theme = Theme("""{ "colors": { "Background": "#1A1A1A", "Foreground": "#2A2A2A" } }""");
        var composed = ThemeComposer.Compose(ThemeAppearance.Dark, theme, UserAppearance.FollowTheme);
        Assert.False(composed.IsAcceptable);
        var problem = Assert.Single(composed.Problems);
        Assert.Equal(("Foreground", "Background"), (problem.Foreground, problem.Background));
        Assert.True(problem.Blocking);
    }

    [Fact]
    public void Contrast_uses_arcs_own_colors_for_what_the_theme_leaves_out()
    {
        var theme = Theme("""{ "colors": { "Foreground": "#F5F5F5" } }""");
        var arcLight = new Dictionary<string, string> { ["Background"] = "#FFFFFF", ["Surface"] = "#FAFAFA" };

        var composed = ThemeComposer.Compose(ThemeAppearance.Light, theme, UserAppearance.FollowTheme, baseColors: arcLight);

        Assert.False(composed.IsAcceptable);
        Assert.Equal(2, composed.Problems.Count(p => p.Blocking));
    }

    [Fact]
    public void Terminal_text_is_checked_against_the_surface_when_the_terminal_has_no_background()
    {
        var theme = Theme("""{ "colors": { "Surface": "#202020", "Background": "#101010", "Foreground": "#E0E0E0" }, "terminal": { "foreground": "#252525" } }""");
        var composed = ThemeComposer.Compose(ThemeAppearance.Dark, theme, UserAppearance.FollowTheme);
        Assert.Contains(composed.Problems, p => p.Foreground == "terminal.foreground" && p.Background == "terminal.background" && p.Blocking);
    }

    [Fact]
    public void Weak_secondary_text_is_only_a_warning()
    {
        var theme = Theme("""{ "colors": { "Background": "#FFFFFF", "Surface": "#FFFFFF", "Foreground": "#111111", "TextSecondary": "#999999" } }""");
        var composed = ThemeComposer.Compose(ThemeAppearance.Light, theme, UserAppearance.FollowTheme);
        Assert.True(composed.IsAcceptable);
        Assert.Contains(composed.Problems, p => p.Foreground == "TextSecondary" && !p.Blocking);
    }

    [Theory]
    [InlineData(ThemeDensity.Compact, 34, 46)]
    [InlineData(ThemeDensity.Standard, 40, 54)]
    [InlineData(ThemeDensity.Comfortable, 46, 62)]
    public void Density_maps_to_octopus_structure(ThemeDensity density, double top, double rail)
    {
        var metrics = DensityMetrics.For(density);
        Assert.Equal(top, metrics.TopRowHeight);
        Assert.Equal(rail, metrics.ProjectRailWidth);
    }
}
