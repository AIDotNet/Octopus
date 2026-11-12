namespace Octopus.Core.Plugins;

/// <summary>The layout density a theme or the user picks (design/plugins/07-主题.md §3.6).</summary>
public enum ThemeDensity
{
    Compact,
    Standard,
    Comfortable,
}

/// <summary>
/// A color theme file after validation (design/plugins/07-主题.md §3), or one layer of a composition (runtime overrides
/// use the same shape). Every part is optional; colors are normalized to <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.
/// </summary>
public sealed class ThemeDefinition
{
    public static readonly ThemeDefinition Empty = new();

    public string? Name { get; init; }
    public ThemeAppearance? Appearance { get; init; }
    /// <summary>Accent seed; Arc derives the accent family from it.</summary>
    public string? Accent { get; init; }
    /// <summary>Corner scale, 0 (square) to 1 (Arc's default).</summary>
    public double? Corners { get; init; }
    public ThemeDensity? Density { get; init; }
    /// <summary>Interface font size factor, 0.85–1.3. Never applies to the terminal.</summary>
    public double? FontScale { get; init; }
    /// <summary>Arc design tokens (<c>Background</c>, <c>Foreground</c>…) → color.</summary>
    public IReadOnlyDictionary<string, string> Colors { get; init; } = new Dictionary<string, string>();
    /// <summary>Arc control identity → color-typed control token → color.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Controls { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, string>>();
    public TerminalColors? Terminal { get; init; }
    /// <summary>Syntax role (<c>comment</c>, <c>keyword</c>…) → color.</summary>
    public IReadOnlyDictionary<string, string> Syntax { get; init; } = new Dictionary<string, string>();
    /// <summary>Octopus's own semantic colors (<c>git.added</c>, <c>agent.waiting</c>…) → color.</summary>
    public IReadOnlyDictionary<string, string> App { get; init; } = new Dictionary<string, string>();
    /// <summary>Installed font family names; ignored when the system does not have them.</summary>
    public string? UiFont { get; init; }
    public string? MonoFont { get; init; }
}

/// <summary>Terminal colors. <see cref="Ansi"/> has exactly 16 entries when present; entries 16–255 stay xterm's cube and grey ramp.</summary>
public sealed record TerminalColors(string? Foreground, string? Background, string? Cursor, string? Selection, IReadOnlyList<string>? Ansi);
