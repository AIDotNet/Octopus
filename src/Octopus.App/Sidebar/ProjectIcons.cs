using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Sidebar;

/// <summary>The icons offered for a project's square. A project stores the name, so any Lucide icon name still works.</summary>
public static class ProjectIcons
{
    public static IReadOnlyList<LucideIconKind> Offered { get; } =
    [
        LucideIconKind.Code, LucideIconKind.Terminal, LucideIconKind.Globe, LucideIconKind.Server,
        LucideIconKind.Database, LucideIconKind.Cloud, LucideIconKind.Smartphone, LucideIconKind.Monitor,
        LucideIconKind.Cpu, LucideIconKind.Bot, LucideIconKind.Brain, LucideIconKind.Sparkles,
        LucideIconKind.Rocket, LucideIconKind.Zap, LucideIconKind.Flame, LucideIconKind.Star,
        LucideIconKind.Heart, LucideIconKind.Gamepad2, LucideIconKind.Music, LucideIconKind.Camera,
        LucideIconKind.BookOpen, LucideIconKind.FileText, LucideIconKind.Package, LucideIconKind.Puzzle,
        LucideIconKind.Wrench, LucideIconKind.FlaskConical, LucideIconKind.Shield, LucideIconKind.Leaf,
    ];

    /// <summary>The icon a project names; null for none or an unknown name.</summary>
    public static LucideIconKind? Parse(string? name) =>
        // A name, not a number: Enum.TryParse would also take "12".
        !string.IsNullOrWhiteSpace(name) && char.IsAsciiLetter(name[0]) && Enum.TryParse<LucideIconKind>(name, ignoreCase: false, out var kind) && Enum.IsDefined(kind)
            ? kind
            : null;
}
