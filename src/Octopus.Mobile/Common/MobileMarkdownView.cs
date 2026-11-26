using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Octopus.Mobile.Common;

/// <summary>
/// Renders Markdown as native Avalonia controls with styled headings, paragraphs, GFM pipe tables (horizontally
/// scrollable with column alignment and rich inline formatting), clickable image cards, inline bold/italic/strikethrough/code,
/// tappable web &amp; local file links, nested bullet/numbered/task lists, blockquotes, dividers and scrollable fenced code blocks.
/// Uses pure <see cref="Run"/> inlines (with <see cref="TextBlock.TextLayout"/> hit-testing for links) and live
/// <see cref="ArcTokens"/> bindings so inline code and links stay baseline-aligned, wrap cleanly, and follow theme changes.
/// Zero reflection and zero desktop-only package dependencies so it stays safe under NativeAOT and Android trimming.
/// </summary>
public sealed class MobileMarkdownView : ContentControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MobileMarkdownView, string?>(nameof(Markdown));

    /// <summary>Opens an HTTP(S) <see cref="Uri"/> (e.g. release notes links).</summary>
    public static readonly StyledProperty<ICommand?> LinkCommandProperty =
        AvaloniaProperty.Register<MobileMarkdownView, ICommand?>(nameof(LinkCommand));

    /// <summary>Opens any raw link or image target <see cref="string"/> (web URL, <c>file://</c>, absolute or relative project path).</summary>
    public static readonly StyledProperty<ICommand?> LinkTargetCommandProperty =
        AvaloniaProperty.Register<MobileMarkdownView, ICommand?>(nameof(LinkTargetCommand));

    private readonly StackPanel _root = new() { Spacing = 8 };
    private string? _lastMarkdown;

    static MobileMarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MobileMarkdownView>((view, _) => view.RebuildIfChanged());
        LinkCommandProperty.Changed.AddClassHandler<MobileMarkdownView>((view, _) => view.Rebuild());
        LinkTargetCommandProperty.Changed.AddClassHandler<MobileMarkdownView>((view, _) => view.Rebuild());
    }

    public MobileMarkdownView()
    {
        Content = _root;
    }

    protected override Type StyleKeyOverride => typeof(ContentControl);

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public ICommand? LinkCommand
    {
        get => GetValue(LinkCommandProperty);
        set => SetValue(LinkCommandProperty, value);
    }

    public ICommand? LinkTargetCommand
    {
        get => GetValue(LinkTargetCommandProperty);
        set => SetValue(LinkTargetCommandProperty, value);
    }

    /// <summary>The parsed blocks currently displayed, exposed for tests.</summary>
    public IReadOnlyList<MarkdownBlock> Blocks { get; private set; } = [];

    private void RebuildIfChanged()
    {
        if (string.Equals(_lastMarkdown, Markdown, StringComparison.Ordinal)) return;
        Rebuild();
    }

    private void Rebuild()
    {
        _lastMarkdown = Markdown;
        _root.Children.Clear();
        var blocks = MobileMarkdownParser.Parse(Markdown);
        Blocks = blocks;

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var control = BuildBlock(block, isFirst: i == 0);
            if (control is not null) _root.Children.Add(control);
        }
    }

    private Control? BuildBlock(MarkdownBlock block, bool isFirst) => block.Kind switch
    {
        MarkdownBlockKind.Heading => BuildHeading(block, isFirst),
        MarkdownBlockKind.Paragraph => BuildParagraph(block),
        MarkdownBlockKind.ListItem => BuildListItem(block),
        MarkdownBlockKind.Quote => BuildQuote(block),
        MarkdownBlockKind.Code => BuildCodeBlock(block),
        MarkdownBlockKind.Divider => new Border { Classes = { "mdDivider" } },
        MarkdownBlockKind.Table when block.Table is not null => BuildTable(block.Table),
        MarkdownBlockKind.Image => BuildImageBlock(block),
        _ => null,
    };

    private Control BuildHeading(MarkdownBlock block, bool isFirst)
    {
        var defaultWeight = block.Level == 1 ? FontWeight.Bold : FontWeight.SemiBold;
        var tb = CreateRichTextBlock(block.Inlines, defaultWeight);
        tb.Classes.Add(block.Level switch
        {
            1 => "mdH1",
            2 => "mdH2",
            3 => "mdH3",
            _ => "mdH4",
        });

        if (block.Level <= 2)
        {
            var container = new StackPanel
            {
                Spacing = 6,
                Margin = isFirst ? new Thickness(0) : new Thickness(0, 8, 0, 0),
                Children = { tb },
            };
            if (block.Level == 2)
            {
                container.Children.Add(new Border { Classes = { "mdHeadingRule" } });
            }
            return container;
        }

        if (!isFirst) tb.Margin = new Thickness(0, 6, 0, 0);
        return tb;
    }

    private Control BuildParagraph(MarkdownBlock block)
    {
        var tb = CreateRichTextBlock(block.Inlines, FontWeight.Normal);
        tb.Classes.Add("mdBody");
        return tb;
    }

    private Control BuildListItem(MarkdownBlock block)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 8,
            Margin = new Thickness(block.Level * 14, 0, 0, 0),
        };

        var marker = new TextBlock
        {
            Text = block.Marker,
            Classes = { "mdMarker" },
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(marker, 0);

        var body = CreateRichTextBlock(block.Inlines, FontWeight.Normal);
        body.Classes.Add("mdBody");
        Grid.SetColumn(body, 1);

        grid.Children.Add(marker);
        grid.Children.Add(body);
        return grid;
    }

    private Control BuildQuote(MarkdownBlock block)
    {
        var tb = CreateRichTextBlock(block.Inlines, FontWeight.Normal);
        tb.Classes.Add("mdQuoteText");
        return new Border
        {
            Classes = { "mdQuote" },
            Child = tb,
        };
    }

    private static Control BuildCodeBlock(MarkdownBlock block)
    {
        var content = new StackPanel { Spacing = 4 };
        if (!string.IsNullOrWhiteSpace(block.Marker))
        {
            content.Children.Add(new TextBlock
            {
                Text = block.Marker,
                Classes = { "mdCodeLang" },
            });
        }

        content.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = block.RawText,
                Classes = { "mdCodeText" },
            },
        });

        return new Border
        {
            Classes = { "mdCodeBlock" },
            Child = content,
        };
    }

    private Control BuildTable(MarkdownTable table)
    {
        var colCount = table.Headers.Count;
        var rowCount = 1 + table.Rows.Count;
        var grid = new Grid();
        for (var c = 0; c < colCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }
        for (var r = 0; r < rowCount; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        // Header row
        for (var c = 0; c < colCount; c++)
        {
            var cell = table.Headers[c];
            var tb = CreateRichTextBlock(cell.Inlines, FontWeight.SemiBold);
            tb.Classes.Add("mdTableHeaderText");
            tb.MaxWidth = 240;
            tb.TextAlignment = AlignmentOf(c < table.Alignments.Count ? table.Alignments[c] : MarkdownColumnAlignment.Left);

            var border = new Border
            {
                Classes = { "mdTableHeaderCell" },
                BorderThickness = new Thickness(0, 0, c < colCount - 1 ? 1 : 0, 1),
                Child = tb,
            };
            Grid.SetRow(border, 0);
            Grid.SetColumn(border, c);
            grid.Children.Add(border);
        }

        // Data rows
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var isLastRow = r == table.Rows.Count - 1;
            var isAlt = (r & 1) == 1;
            for (var c = 0; c < colCount; c++)
            {
                var cell = c < row.Count ? row[c] : new MarkdownTableCell([], "");
                var tb = CreateRichTextBlock(cell.Inlines, FontWeight.Normal);
                tb.Classes.Add("mdTableCellText");
                tb.MaxWidth = 240;
                tb.TextAlignment = AlignmentOf(c < table.Alignments.Count ? table.Alignments[c] : MarkdownColumnAlignment.Left);

                var border = new Border
                {
                    Classes = { "mdTableCell" },
                    BorderThickness = new Thickness(0, 0, c < colCount - 1 ? 1 : 0, isLastRow ? 0 : 1),
                    Child = tb,
                };
                if (isAlt) border.Classes.Add("alt");
                Grid.SetRow(border, r + 1);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = grid,
        };

        return new Border
        {
            Classes = { "mdTableCard" },
            Child = scroller,
        };
    }

    private Control BuildImageBlock(MarkdownBlock block)
    {
        var label = string.IsNullOrWhiteSpace(block.Marker) ? block.RawText : block.Marker;
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 8,
        };

        var icon = new ArcGlyph
        {
            Data = ArcIcons.Image,
            Size = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ArcTokens.Bind(icon, ArcGlyph.ForegroundProperty, ArcDesignTokenKind.Accent);
        Grid.SetColumn(icon, 0);

        var textStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.Equals(label, block.RawText, StringComparison.Ordinal))
        {
            textStack.Children.Add(new TextBlock
            {
                Text = block.RawText,
                Classes = { "muted" },
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        Grid.SetColumn(textStack, 1);

        var chevron = new ArcGlyph
        {
            Data = ArcIcons.ChevronRight,
            Size = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ArcTokens.Bind(chevron, ArcGlyph.ForegroundProperty, ArcDesignTokenKind.TextMuted);
        Grid.SetColumn(chevron, 2);

        row.Children.Add(icon);
        row.Children.Add(textStack);
        row.Children.Add(chevron);

        var button = new Button
        {
            Classes = { "mdImageCard" },
            Content = row,
        };
        var target = block.RawText;
        button.Click += (_, _) => ExecuteLink(target);
        return button;
    }

    private TextBlock CreateRichTextBlock(IReadOnlyList<MarkdownInline> inlines, FontWeight defaultWeight)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var collection = tb.Inlines ??= [];
        List<(int Start, int End, string Target)>? linkRanges = null;
        var offset = 0;

        foreach (var span in inlines)
        {
            switch (span.Kind)
            {
                case MarkdownInlineKind.Text:
                    collection.Add(new Run(span.Text));
                    offset += span.Text.Length;
                    break;
                case MarkdownInlineKind.Bold:
                    var boldSpan = new Bold();
                    boldSpan.Inlines.Add(new Run(span.Text));
                    collection.Add(boldSpan);
                    offset += span.Text.Length;
                    break;
                case MarkdownInlineKind.Italic:
                    var italicSpan = new Italic();
                    italicSpan.Inlines.Add(new Run(span.Text));
                    collection.Add(italicSpan);
                    offset += span.Text.Length;
                    break;
                case MarkdownInlineKind.BoldItalic:
                    var biSpan = new Bold { FontStyle = FontStyle.Italic };
                    biSpan.Inlines.Add(new Run(span.Text));
                    collection.Add(biSpan);
                    offset += span.Text.Length;
                    break;
                case MarkdownInlineKind.Strikethrough:
                    collection.Add(new Run(span.Text) { FontWeight = defaultWeight, TextDecorations = TextDecorations.Strikethrough });
                    offset += span.Text.Length;
                    break;
                case MarkdownInlineKind.Code:
                    var padded = $" {span.Text} ";
                    var codeRun = new Run(padded)
                    {
                        FontSize = 12,
                        FontWeight = FontWeight.Normal,
                        FontStyle = FontStyle.Normal,
                    };
                    ArcTokens.Bind(codeRun, TextElement.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
                    ArcTokens.Bind(codeRun, TextElement.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
                    ArcTokens.Bind(codeRun, TextElement.ForegroundProperty, ArcDesignTokenKind.Foreground);
                    collection.Add(codeRun);
                    offset += padded.Length;
                    break;
                case MarkdownInlineKind.Link when !string.IsNullOrWhiteSpace(span.Url):
                    var linkRun = new Run(span.Text)
                    {
                        FontWeight = defaultWeight,
                        FontStyle = FontStyle.Normal,
                        TextDecorations = TextDecorations.Underline,
                    };
                    ArcTokens.Bind(linkRun, TextElement.ForegroundProperty, ArcDesignTokenKind.Accent);
                    collection.Add(linkRun);
                    linkRanges ??= [];
                    linkRanges.Add((offset, offset + span.Text.Length, span.Url!));
                    offset += span.Text.Length;
                    break;
                case MarkdownInlineKind.Image when !string.IsNullOrWhiteSpace(span.Url):
                    var imgLabel = $"[{span.Text}]";
                    var imgRun = new Run(imgLabel)
                    {
                        FontWeight = FontWeight.Medium,
                        FontStyle = FontStyle.Normal,
                        TextDecorations = TextDecorations.Underline,
                    };
                    ArcTokens.Bind(imgRun, TextElement.ForegroundProperty, ArcDesignTokenKind.Accent);
                    collection.Add(imgRun);
                    linkRanges ??= [];
                    linkRanges.Add((offset, offset + imgLabel.Length, span.Url!));
                    offset += imgLabel.Length;
                    break;
                default:
                    collection.Add(new Run(span.Text) { FontWeight = defaultWeight });
                    offset += span.Text.Length;
                    break;
            }
        }

        if (linkRanges is not null)
        {
            var ranges = linkRanges;
            tb.Cursor = new Cursor(StandardCursorType.Hand);
            tb.PointerPressed += (_, e) =>
            {
                if (LinkTargetCommand is null && LinkCommand is null) return;
                var pos = e.GetPosition(tb);
                var hit = tb.TextLayout.HitTestPoint(pos);
                var idx = hit.TextPosition;
                foreach (var (start, end, target) in ranges)
                {
                    if (idx >= start && idx <= end)
                    {
                        if (ExecuteLink(target))
                        {
                            e.Handled = true;
                        }
                        break;
                    }
                }
            };
        }

        return tb;
    }

    private bool ExecuteLink(string target)
    {
        if (LinkTargetCommand is { } targetCmd && targetCmd.CanExecute(target))
        {
            targetCmd.Execute(target);
            return true;
        }
        if (LinkCommand is { } uriCmd && Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uriCmd.CanExecute(uri))
        {
            uriCmd.Execute(uri);
            return true;
        }
        return false;
    }

    private static TextAlignment AlignmentOf(MarkdownColumnAlignment alignment) => alignment switch
    {
        MarkdownColumnAlignment.Center => TextAlignment.Center,
        MarkdownColumnAlignment.Right => TextAlignment.Right,
        _ => TextAlignment.Left,
    };
}
