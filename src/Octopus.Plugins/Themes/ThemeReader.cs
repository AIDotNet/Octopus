using System.Text.Json;
using Octopus.Core.Plugins;
using Octopus.Plugins.Manifests;

namespace Octopus.Plugins.Themes;

/// <summary>
/// Reads a color theme file (design/plugins/07-主题.md §3–4.3). Only invalid JSON or an unsupported schema make the whole
/// theme unusable; a bad value only drops that value with a warning, so a theme written for a newer Octopus still works.
/// The same reader checks runtime overrides (<c>theme.setOverrides</c>), which use the same shape.
/// </summary>
public static class ThemeReader
{
    public const int Schema = 1;
    public const double MinFontScale = 0.85;
    public const double MaxFontScale = 1.3;

    /// <summary>Arc design tokens a theme may color (design 07 §3.1). Sizes, spacing, type scale and motion are not here on purpose.</summary>
    public static readonly IReadOnlySet<string> ColorTokens = new HashSet<string>(StringComparer.Ordinal)
    {
        "Background", "Surface", "SurfaceMuted", "SurfaceRaised", "Scrim",
        "Foreground", "TextSecondary", "TextMuted",
        "Border", "BorderStrong", "BorderSubtle",
        "Accent", "AccentStrong", "AccentSubtle", "AccentForeground",
        "ControlOn", "ControlOnSubtle", "ControlThumb", "ControlThumbOn", "ControlTrack", "ControlTrackHover", "ControlGlyph", "ControlFill",
        "Success", "Warning", "Danger", "SuccessSubtle", "WarningSubtle", "DangerSubtle",
        "Series1", "Series2", "Series3", "Series4",
        "Neutral0", "Neutral1", "Neutral2", "Neutral3", "Neutral4", "Neutral5", "Neutral6", "Neutral7", "Neutral8", "Neutral9", "Neutral10", "Neutral11",
        "FocusRing",
    };

    /// <summary>The nine syntax roles of Octopus's code view (<c>SyntaxRole</c>).</summary>
    public static readonly IReadOnlySet<string> SyntaxRoles = new HashSet<string>(StringComparer.Ordinal)
    {
        "comment", "string", "number", "keyword", "type", "function", "property", "tag", "punctuation",
    };

    /// <summary>Octopus's own semantic colors (design 07 §3.4). <c>project.&lt;name&gt;</c> is matched by prefix.</summary>
    public static readonly IReadOnlySet<string> AppColorKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "git.added", "git.modified", "git.deleted", "git.renamed", "git.untracked", "git.conflict",
        "diff.addedBackground", "diff.removedBackground", "diff.addedForeground", "diff.removedForeground",
        "agent.working", "agent.waiting", "agent.done", "agent.error",
        "search.match", "link",
    };

    private static readonly string[] KnownTopLevel =
    [
        "$schema", "schema", "name", "appearance", "accent", "corners", "density", "fontScale",
        "colors", "controls", "terminal", "syntax", "app", "fonts",
    ];

    public sealed record Result(ThemeDefinition? Theme, IReadOnlyList<ManifestIssue> Issues)
    {
        public bool IsUsable => Theme is not null;
    }

    /// <summary>Reads a theme file from disk (size cap 1 MB).</summary>
    public static Result ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Fail("the theme file does not exist");
            if (info.Length > PluginApi.MaxManifestBytes) return Fail("the theme file is larger than 1 MB");
            return Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail($"the theme file cannot be read: {e.Message}");
        }
    }

    public static Result Parse(string json)
    {
        var log = new IssueLog();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 32 });
        }
        catch (JsonException e)
        {
            return Fail($"is not valid JSON (line {e.LineNumber + 1}): {e.Message}");
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Fail("must be a JSON object");
            var root = new ManifestObject(document.RootElement, "$", log);
            root.WarnUnknown(KnownTopLevel);
            var schema = root.Integer("schema") ?? Schema;
            if (schema != Schema)
            {
                log.Error(root.At("schema"), $"schema {schema} is not supported; this version of Octopus reads schema {Schema}");
                return new Result(null, log.Issues);
            }
            var theme = Read(root);
            // Values are problems of the file, never reasons to drop the whole theme: report them as warnings.
            var issues = log.Issues.Select(i => i with { Severity = IssueSeverity.Warning }).ToList();
            AppearanceSanity(theme, issues);
            return new Result(theme, issues);
        }
    }

    private static Result Fail(string message) => new(null, [new ManifestIssue(IssueSeverity.Error, "$", message)]);

    private static ThemeDefinition Read(ManifestObject root)
    {
        ThemeAppearance? appearance = root.String("appearance") switch
        {
            null => null,
            "light" => ThemeAppearance.Light,
            "dark" => ThemeAppearance.Dark,
            var other => Ignore<ThemeAppearance?>(root, "appearance", $"\"{other}\" must be \"light\" or \"dark\"", null),
        };
        ThemeDensity? density = root.String("density") switch
        {
            null => null,
            "compact" => ThemeDensity.Compact,
            "standard" => ThemeDensity.Standard,
            "comfortable" => ThemeDensity.Comfortable,
            var other => Ignore<ThemeDensity?>(root, "density", $"\"{other}\" must be compact, standard or comfortable", null),
        };
        var fonts = root.Object("fonts");
        fonts?.WarnUnknown("ui", "mono");
        return new ThemeDefinition
        {
            Name = root.String("name"),
            Appearance = appearance,
            Accent = Color(root, "accent"),
            Corners = Number(root, "corners", 0, 1),
            Density = density,
            FontScale = Number(root, "fontScale", MinFontScale, MaxFontScale),
            Colors = ColorMap(root.Object("colors"), name => ColorTokens.Contains(name), "is not a design token a theme can change"),
            Controls = ReadControls(root.Object("controls")),
            Terminal = ReadTerminal(root.Object("terminal")),
            Syntax = ColorMap(root.Object("syntax"), name => SyntaxRoles.Contains(name), "is not a syntax role"),
            App = ColorMap(root.Object("app"), name => AppColorKeys.Contains(name) || (name.StartsWith("project.", StringComparison.Ordinal) && name.Length > 8),
                "is not an Octopus color"),
            UiFont = fonts?.String("ui"),
            MonoFont = fonts?.String("mono"),
        };
    }

    private static T Ignore<T>(ManifestObject obj, string name, string message, T value)
    {
        obj.Log.Warn(obj.At(name), $"{message}; ignored");
        return value;
    }

    private static string? Color(ManifestObject obj, string name)
    {
        var text = obj.String(name);
        if (text is null) return null;
        if (ThemeColor.TryParse(text, out var color)) return color.ToString();
        obj.Log.Warn(obj.At(name), $"\"{text}\" is not a color (#RRGGBB or #RRGGBBAA); ignored");
        return null;
    }

    /// <summary>A number clamped into its range (a value outside it is pulled in, with a warning).</summary>
    private static double? Number(ManifestObject obj, string name, double min, double max)
    {
        if (obj.Raw(name) is not { } value) return null;
        if (value.ValueKind != JsonValueKind.Number)
        {
            obj.Log.Warn(obj.At(name), "must be a number; ignored");
            return null;
        }
        var number = value.GetDouble();
        if (number < min || number > max)
        {
            obj.Log.Warn(obj.At(name), $"{number} is outside {min}–{max}; clamped");
            return Math.Clamp(number, min, max);
        }
        return number;
    }

    private static IReadOnlyDictionary<string, string> ColorMap(ManifestObject? obj, Func<string, bool> allowed, string unknownMessage)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (obj is not { } block) return map;
        foreach (var (name, _) in block.Properties())
        {
            if (!allowed(name))
            {
                block.Log.Warn(block.At(name), $"{unknownMessage}; ignored");
                continue;
            }
            if (Color(block, name) is { } color) map[name] = color;
        }
        return map;
    }

    /// <summary>Control tokens: only color values are accepted (sizes and spacing are not a theme's business).</summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ReadControls(ManifestObject? obj)
    {
        var controls = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (obj is not { } block) return controls;
        foreach (var (control, value) in block.Properties())
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                block.Log.Warn(block.At(control), "must be an object of token → color; ignored");
                continue;
            }
            var tokens = ColorMap(block.Object(control), _ => true, "");
            if (tokens.Count > 0) controls[control] = tokens;
        }
        return controls;
    }

    private static TerminalColors? ReadTerminal(ManifestObject? obj)
    {
        if (obj is not { } terminal) return null;
        terminal.WarnUnknown("foreground", "background", "cursor", "selection", "ansi");
        IReadOnlyList<string>? ansi = null;
        if (terminal.Has("ansi"))
        {
            var list = terminal.Strings("ansi");
            var parsed = list.Select(text => ThemeColor.TryParse(text, out var c) ? c.ToString() : null).ToList();
            if (list.Count != 16 || parsed.Any(c => c is null))
            {
                terminal.Log.Warn(terminal.At("ansi"), "must be exactly 16 colors; the terminal section is ignored");
                return null;
            }
            ansi = parsed!;
        }
        return new TerminalColors(Color(terminal, "foreground"), Color(terminal, "background"), Color(terminal, "cursor"), Color(terminal, "selection"), ansi);
    }

    /// <summary>A "dark" theme whose background is lighter than its text is almost certainly mislabeled.</summary>
    private static void AppearanceSanity(ThemeDefinition theme, List<ManifestIssue> issues)
    {
        if (theme.Appearance is not { } appearance
            || !theme.Colors.TryGetValue("Background", out var bgText) || !theme.Colors.TryGetValue("Foreground", out var fgText)
            || !ThemeColor.TryParse(bgText, out var bg) || !ThemeColor.TryParse(fgText, out var fg))
        {
            return;
        }
        var backgroundIsLighter = bg.Luminance > fg.Luminance;
        if (appearance == ThemeAppearance.Dark && backgroundIsLighter || appearance == ThemeAppearance.Light && !backgroundIsLighter)
        {
            issues.Add(new ManifestIssue(IssueSeverity.Warning, "$.appearance",
                $"the theme says \"{appearance.ToString().ToLowerInvariant()}\" but its background is {(backgroundIsLighter ? "lighter" : "darker")} than its text"));
        }
    }
}
