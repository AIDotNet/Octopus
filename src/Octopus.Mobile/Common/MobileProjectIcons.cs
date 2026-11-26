using Avalonia.Media;
using Octopus.App.Common;
using Octopus.Mobile.Workspace;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.Mobile.Common;

/// <summary>
/// Resolves a remote project's real desktop appearance (custom picture thumbnail, built-in pixel icon, Lucide vector
/// icon, or colored initial letter) for mobile badges.
/// </summary>
public static class MobileProjectIcons
{
    private static readonly string[] FallbackPalette =
    [
        "#5B8DEF", "#C2783E", "#3FA796", "#B5609E", "#7A8B45", "#D97757", "#6E7BD9", "#2F9E7E",
    ];

    /// <summary>
    /// Every Lucide icon offered by the desktop project appearance picker. Referenced explicitly so the
    /// <c>TKCode.Arc.Icons.Lucide</c> source generator compiles their vector geometry into the mobile build.
    /// </summary>
    public static IReadOnlyList<LucideIconKind> Offered { get; } =
    [
        LucideIconKind.Code, LucideIconKind.Terminal, LucideIconKind.Globe, LucideIconKind.Server,
        LucideIconKind.Database, LucideIconKind.Cloud, LucideIconKind.Smartphone, LucideIconKind.Monitor,
        LucideIconKind.Laptop, LucideIconKind.Cpu, LucideIconKind.Bot, LucideIconKind.Brain, LucideIconKind.Sparkles,
        LucideIconKind.Rocket, LucideIconKind.Zap, LucideIconKind.Flame, LucideIconKind.Star,
        LucideIconKind.Heart, LucideIconKind.Gamepad2, LucideIconKind.Music, LucideIconKind.Camera,
        LucideIconKind.BookOpen, LucideIconKind.FileText, LucideIconKind.Package, LucideIconKind.Puzzle,
        LucideIconKind.Wrench, LucideIconKind.FlaskConical, LucideIconKind.Shield, LucideIconKind.Leaf,
    ];

    /// <summary>Parses a desktop Lucide icon name (e.g. "Rocket"); returns null for pixel icons, empty or unknown names.</summary>
    public static LucideIconKind? ParseLucide(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && char.IsAsciiLetter(name[0])
        && Enum.TryParse<LucideIconKind>(name, ignoreCase: false, out var kind)
        && Enum.IsDefined(kind)
            ? kind
            : null;

    /// <summary>Parses a built-in desktop pixel icon reference ("pixel:t-rex"); returns null for anything else.</summary>
    public static PixelIcon? ParsePixel(string? name) => PixelIcons.Parse(name);

    /// <summary>First visible character (surrogate-pair safe) for the fallback letter badge.</summary>
    public static string InitialOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "P";
        var trimmed = name.Trim();
        return char.ConvertFromUtf32(char.ConvertToUtf32(trimmed, 0)).ToUpperInvariant();
    }

    /// <summary>The badge fill: transparent when showing a custom picture, otherwise the project's color or stable fallback.</summary>
    public static IBrush BrushOf(RemoteProject project)
    {
        if (project.ImageBitmap is not null) return Brushes.Transparent;
        if (!string.IsNullOrWhiteSpace(project.Color) && Color.TryParse(project.Color, out var parsed))
        {
            return new SolidColorBrush(parsed);
        }
        var key = !string.IsNullOrWhiteSpace(project.Id) ? project.Id : project.Name;
        var hash = 17;
        foreach (var ch in key) hash = unchecked(hash * 31 + ch);
        var hex = FallbackPalette[(int)((uint)hash % (uint)FallbackPalette.Length)];
        return new SolidColorBrush(Color.Parse(hex));
    }
}
