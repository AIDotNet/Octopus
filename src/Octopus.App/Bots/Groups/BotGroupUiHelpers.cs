using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Octopus.App.Services;
using Octopus.Core.Bots;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Bots.Groups;

/// <summary>One bot avatar tile used inside a 1–4 member Bot Group avatar cluster.</summary>
public sealed record BotGroupAvatarTile(
    string BotId,
    string Name,
    string Initial,
    IBrush AvatarBrush,
    Bitmap? AvatarImage,
    LucideIconKind AvatarIcon,
    bool HasAvatarIcon)
{
    public bool HasAvatarImage => AvatarImage is not null;
    public bool ShowInitial => !HasAvatarImage && !HasAvatarIcon;
}

internal static class BotGroupUiHelpers
{
    public static IBrush ParseBrush(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var color))
        {
            return new SolidColorBrush(color);
        }
        return SolidColorBrush.Parse("#5b5bd6");
    }

    public static string InitialOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "B";
        }
        var trimmed = name.Trim();
        return trimmed.Length > 0 ? trimmed[..1].ToUpperInvariant() : "B";
    }

    public static IReadOnlyList<BotGroupAvatarTile> BuildAvatarCluster(
        BotGroupDefinition group,
        IBotService bots,
        IProjectIconStore? icons,
        int maxCount = 4)
    {
        var result = new List<BotGroupAvatarTile>(Math.Min(group.Members.Count, maxCount));
        foreach (var member in group.Members)
        {
            if (result.Count >= maxCount)
            {
                break;
            }
            var bot = bots.Bots.FirstOrDefault(b => string.Equals(b.Id, member.BotId, StringComparison.Ordinal));
            var name = bot?.Name ?? member.BotId;
            var image = bot is not null ? icons?.Load(bot.Image) : null;
            var hasIcon = image is null && !string.IsNullOrWhiteSpace(bot?.Icon);
            var iconKind = NewBotDialogViewModel.ParseIcon(bot?.Icon, LucideIconKind.Bot);
            result.Add(new BotGroupAvatarTile(
                member.BotId,
                name,
                InitialOf(name),
                ParseBrush(bot?.Color),
                image,
                iconKind,
                hasIcon));
        }
        return result;
    }

    public static BotGroupAvatarTile BuildBotAvatar(
        string botId,
        string botName,
        string? colorHex,
        string? iconName,
        string? imageName,
        IProjectIconStore? icons)
    {
        var image = icons?.Load(imageName);
        var hasIcon = image is null && !string.IsNullOrWhiteSpace(iconName);
        var iconKind = NewBotDialogViewModel.ParseIcon(iconName, LucideIconKind.Bot);
        return new BotGroupAvatarTile(
            botId,
            botName,
            InitialOf(botName),
            ParseBrush(colorHex),
            image,
            iconKind,
            hasIcon);
    }
}

/// <summary>
/// Attached property that renders <see cref="BotGroupMentionSegment"/> runs inside a <see cref="TextBlock"/>,
/// highlighting <c>@mentions</c> with bold accent styling while keeping natural line wrapping.
/// </summary>
public static class BotGroupMentionText
{
    public static readonly AttachedProperty<IReadOnlyList<BotGroupMentionSegment>?> SegmentsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<BotGroupMentionSegment>?>(
            "Segments",
            typeof(BotGroupMentionText));

    static BotGroupMentionText()
    {
        SegmentsProperty.Changed.AddClassHandler<TextBlock>(OnSegmentsChanged);
    }

    public static IReadOnlyList<BotGroupMentionSegment>? GetSegments(TextBlock target) =>
        target.GetValue(SegmentsProperty);

    public static void SetSegments(TextBlock target, IReadOnlyList<BotGroupMentionSegment>? value) =>
        target.SetValue(SegmentsProperty, value);

    private static void OnSegmentsChanged(TextBlock target, AvaloniaPropertyChangedEventArgs args)
    {
        target.Inlines ??= new InlineCollection();
        target.Inlines.Clear();
        if (args.NewValue is not IReadOnlyList<BotGroupMentionSegment> segments || segments.Count == 0)
        {
            return;
        }

        foreach (var seg in segments)
        {
            var run = new Run(seg.Text);
            if (seg.IsMention)
            {
                run.FontWeight = FontWeight.SemiBold;
                ArcTokens.Bind(run, TextElement.ForegroundProperty, ArcDesignTokenKind.AccentStrong);
            }
            target.Inlines.Add(run);
        }
    }
}
