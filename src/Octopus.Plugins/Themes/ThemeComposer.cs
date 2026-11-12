using Octopus.Core.Plugins;

namespace Octopus.Plugins.Themes;

/// <summary>What the user set on the appearance page; null means "follow the theme" (design/plugins/07-主题.md §5).</summary>
public sealed record UserAppearance(string? Accent = null, ThemeDensity? Density = null, double? FontScale = null)
{
    public static readonly UserAppearance FollowTheme = new();
}

/// <summary>A pair of colors that reads badly. Blocking problems stop the theme from being applied (design 07 §7).</summary>
public sealed record ContrastProblem(string Foreground, string Background, double Ratio, double Required, bool Blocking);

/// <summary>
/// Everything layers 2–4 of design 07 §4.1 decide, merged: the chosen theme, then the user's own choices, then
/// runtime overrides from plugins. Layer 1 (Arc's defaults) is only consulted for contrast; layer 5 (Octopus's
/// structural tokens) is applied by the app after this.
/// </summary>
public sealed record ComposedTheme(
    ThemeAppearance Appearance,
    string? Accent,
    double Corners,
    ThemeDensity Density,
    double FontScale,
    IReadOnlyDictionary<string, string> Colors,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Controls,
    TerminalColors? Terminal,
    IReadOnlyDictionary<string, string> Syntax,
    IReadOnlyDictionary<string, string> App,
    string? UiFont,
    string? MonoFont,
    IReadOnlyList<ContrastProblem> Problems)
{
    /// <summary>False when the text would be unreadable: the app keeps the previous theme and shows <see cref="Problems"/>.</summary>
    public bool IsAcceptable => Problems.All(p => !p.Blocking);
}

public static class ThemeComposer
{
    /// <summary>Octopus's own corner scale when nothing else is said (ThemeService uses <c>ArcCornerStyle.Scale(0.4)</c>).</summary>
    public const double DefaultCorners = 0.4;
    public const double BlockingContrast = 3.0;
    public const double RecommendedContrast = 4.5;

    /// <param name="appearance">The appearance being shown (from the theme mode and, for "system", the OS).</param>
    /// <param name="theme">The color theme chosen for that appearance; null is the built-in one.</param>
    /// <param name="overrides">Runtime layers from plugins, oldest first.</param>
    /// <param name="baseColors">Arc's own token colors for <paramref name="appearance"/>, so contrast is checked on what will really show.</param>
    public static ComposedTheme Compose(
        ThemeAppearance appearance,
        ThemeDefinition? theme,
        UserAppearance user,
        IReadOnlyList<ThemeDefinition>? overrides = null,
        IReadOnlyDictionary<string, string>? baseColors = null)
    {
        var layers = new List<ThemeDefinition> { theme ?? ThemeDefinition.Empty };
        layers.Add(new ThemeDefinition { Accent = user.Accent, Density = user.Density, FontScale = user.FontScale });
        if (overrides is not null) layers.AddRange(overrides);

        string? accent = null, uiFont = null, monoFont = null;
        double? corners = null, fontScale = null;
        ThemeDensity? density = null;
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        var controls = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var syntax = new Dictionary<string, string>(StringComparer.Ordinal);
        var app = new Dictionary<string, string>(StringComparer.Ordinal);
        TerminalColors? terminal = null;

        foreach (var layer in layers)
        {
            accent = layer.Accent ?? accent;
            corners = layer.Corners ?? corners;
            density = layer.Density ?? density;
            fontScale = layer.FontScale ?? fontScale;
            uiFont = layer.UiFont ?? uiFont;
            monoFont = layer.MonoFont ?? monoFont;
            Merge(colors, layer.Colors);
            Merge(syntax, layer.Syntax);
            Merge(app, layer.App);
            foreach (var (control, tokens) in layer.Controls)
            {
                if (!controls.TryGetValue(control, out var existing)) controls[control] = existing = new Dictionary<string, string>(StringComparer.Ordinal);
                Merge(existing, tokens);
            }
            if (layer.Terminal is { } t)
            {
                terminal = terminal is null
                    ? t
                    : new TerminalColors(t.Foreground ?? terminal.Foreground, t.Background ?? terminal.Background,
                        t.Cursor ?? terminal.Cursor, t.Selection ?? terminal.Selection, t.Ansi ?? terminal.Ansi);
            }
        }

        var problems = CheckContrast(colors, terminal, baseColors);
        return new ComposedTheme(
            appearance, accent, corners ?? DefaultCorners, density ?? ThemeDensity.Standard,
            Math.Clamp(fontScale ?? 1.0, ThemeReader.MinFontScale, ThemeReader.MaxFontScale),
            colors, controls.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value, StringComparer.Ordinal),
            terminal, syntax, app, uiFont, monoFont, problems);
    }

    private static void Merge(Dictionary<string, string> target, IReadOnlyDictionary<string, string> source)
    {
        foreach (var (key, value) in source) target[key] = value;
    }

    /// <summary>
    /// The pairs of design 07 §7: body text on the window and on surfaces, and terminal text on the terminal must reach
    /// 3:1 or the theme is refused; secondary text and text on the accent should reach 4.5:1.
    /// </summary>
    private static List<ContrastProblem> CheckContrast(Dictionary<string, string> colors, TerminalColors? terminal, IReadOnlyDictionary<string, string>? baseColors)
    {
        string? Token(string name) => colors.GetValueOrDefault(name) ?? baseColors?.GetValueOrDefault(name);
        var problems = new List<ContrastProblem>();
        void Check(string fgName, string? fg, string bgName, string? bg, double required, bool blocking)
        {
            if (!ThemeColor.TryParse(fg, out var f) || !ThemeColor.TryParse(bg, out var b)) return;
            var ratio = ThemeColor.Contrast(f, b);
            if (ratio < required) problems.Add(new ContrastProblem(fgName, bgName, Math.Round(ratio, 2), required, blocking));
        }

        Check("Foreground", Token("Foreground"), "Background", Token("Background"), BlockingContrast, true);
        Check("Foreground", Token("Foreground"), "Surface", Token("Surface"), BlockingContrast, true);
        if (terminal is not null)
        {
            // An unset terminal background is the surface around it (design 07 §3.2).
            Check("terminal.foreground", terminal.Foreground, "terminal.background", terminal.Background ?? Token("Surface"), BlockingContrast, true);
        }
        Check("TextSecondary", Token("TextSecondary"), "Surface", Token("Surface"), RecommendedContrast, false);
        Check("AccentForeground", Token("AccentForeground"), "Accent", Token("Accent"), RecommendedContrast, false);
        return problems;
    }
}

/// <summary>
/// Octopus's structural sizes per density (design 07 §3.6). The theme only names the density; these numbers are
/// Octopus's own and are applied last, after every theme layer.
/// </summary>
public sealed record DensityMetrics(double TopRowHeight, double ProjectRailWidth, double ScaleFactor)
{
    public static DensityMetrics For(ThemeDensity density) => density switch
    {
        ThemeDensity.Compact => new DensityMetrics(34, 46, 0.85),
        ThemeDensity.Comfortable => new DensityMetrics(46, 62, 1.15),
        _ => new DensityMetrics(40, 54, 1.0),
    };
}
