using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Octopus.App.Explorer.Search;

/// <summary>A line of text with one stretch in bold on the accent tint: the part of a line that matched the search.</summary>
public sealed class HighlightedTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> SourceTextProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, string?>(nameof(SourceText));

    public static readonly StyledProperty<int> HighlightStartProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, int>(nameof(HighlightStart));

    public static readonly StyledProperty<int> HighlightLengthProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, int>(nameof(HighlightLength));

    static HighlightedTextBlock()
    {
        SourceTextProperty.Changed.AddClassHandler<HighlightedTextBlock>((b, _) => b.Rebuild());
        HighlightStartProperty.Changed.AddClassHandler<HighlightedTextBlock>((b, _) => b.Rebuild());
        HighlightLengthProperty.Changed.AddClassHandler<HighlightedTextBlock>((b, _) => b.Rebuild());
    }

    public string? SourceText
    {
        get => GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public int HighlightStart
    {
        get => GetValue(HighlightStartProperty);
        set => SetValue(HighlightStartProperty, value);
    }

    public int HighlightLength
    {
        get => GetValue(HighlightLengthProperty);
        set => SetValue(HighlightLengthProperty, value);
    }

    private void Rebuild()
    {
        var text = SourceText ?? "";
        Inlines?.Clear();
        var start = Math.Clamp(HighlightStart, 0, text.Length);
        var length = Math.Clamp(HighlightLength, 0, text.Length - start);
        if (length == 0)
        {
            Text = text;
            return;
        }
        Text = null;
        Inlines ??= new InlineCollection();
        if (start > 0) Inlines.Add(new Run(text[..start]));
        var match = new Run(text.Substring(start, length)) { FontWeight = FontWeight.SemiBold };
        ArcTokens.Bind(match, TextElement.ForegroundProperty, ArcDesignTokenKind.Foreground);
        ArcTokens.Bind(match, TextElement.BackgroundProperty, ArcDesignTokenKind.AccentSubtle);
        Inlines.Add(match);
        if (start + length < text.Length) Inlines.Add(new Run(text[(start + length)..]));
    }
}
