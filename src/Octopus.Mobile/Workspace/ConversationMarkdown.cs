using System.Text;

namespace Octopus.Mobile.Workspace;

/// <summary>What a block of an answer is.</summary>
public enum ConversationTextKind
{
    Paragraph,
    Heading,
    /// <summary>One item of a bulleted or numbered list; <see cref="ConversationTextBlock.Marker"/> is its bullet or number.</summary>
    ListItem,
    Quote,
    /// <summary>A fenced code block, shown as it is in the terminal's face.</summary>
    Code,
}

/// <summary>A block of an answer as the conversation page shows it.</summary>
public sealed record ConversationTextBlock(ConversationTextKind Kind, string Text, string Marker = "")
{
    public bool IsParagraph => Kind == ConversationTextKind.Paragraph;
    public bool IsHeading => Kind == ConversationTextKind.Heading;
    public bool IsListItem => Kind == ConversationTextKind.ListItem;
    public bool IsQuote => Kind == ConversationTextKind.Quote;
    public bool IsCode => Kind == ConversationTextKind.Code;
}

/// <summary>
/// Cuts an agent's Markdown into the few blocks the phone draws (paragraphs, headings, list items, quotes, fenced code).
/// The phone app does not carry a Markdown renderer: Arc's lives in the desktop control library, which the phone app
/// does not reference (and its trimming is untested on a phone). Inline marks stay as they are, except bold markers,
/// which read as noise; a code fence that is still streaming is shown open.
/// </summary>
public static class ConversationMarkdown
{
    public static IReadOnlyList<ConversationTextBlock> Parse(string? markdown)
    {
        var blocks = new List<ConversationTextBlock>();
        if (string.IsNullOrEmpty(markdown)) return blocks;
        var paragraph = new StringBuilder();
        var code = (StringBuilder?)null;
        var fence = "";

        void Flush()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new(ConversationTextKind.Paragraph, Inline(paragraph.ToString())));
            paragraph.Clear();
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (code is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal) && trimmed.Trim('`', '~').Length == 0)
                {
                    blocks.Add(new(ConversationTextKind.Code, code.ToString().TrimEnd('\n')));
                    code = null;
                }
                else
                {
                    code.Append(raw).Append('\n');
                }
                continue;
            }
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                Flush();
                fence = trimmed[..3];
                code = new StringBuilder();
                continue;
            }
            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }
            if (trimmed.StartsWith('#') && trimmed.TrimStart('#') is { } heading && heading.StartsWith(' '))
            {
                Flush();
                blocks.Add(new(ConversationTextKind.Heading, Inline(heading.Trim())));
                continue;
            }
            if (trimmed is ['-' or '*' or '+', ' ', ..])
            {
                Flush();
                blocks.Add(new(ConversationTextKind.ListItem, Inline(trimmed[2..].Trim()), "•"));
                continue;
            }
            if (NumberedItem(trimmed) is { } numbered)
            {
                Flush();
                blocks.Add(new(ConversationTextKind.ListItem, Inline(numbered.Text), numbered.Marker));
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                Flush();
                blocks.Add(new(ConversationTextKind.Quote, Inline(trimmed.TrimStart('>').Trim())));
                continue;
            }
            if (trimmed.Trim('-', '*', '_').Length == 0 && trimmed.Length >= 3)
            {
                // A rule: the gap between blocks says as much.
                Flush();
                continue;
            }
            // A line break inside a paragraph stays one: agents write short lines meant to be read as such.
            if (paragraph.Length > 0) paragraph.Append('\n');
            paragraph.Append(trimmed);
        }
        Flush();
        // Still streaming inside a fence: what came so far.
        if (code is not null) blocks.Add(new(ConversationTextKind.Code, code.ToString().TrimEnd('\n')));
        return blocks;
    }

    private static (string Marker, string Text)? NumberedItem(string line)
    {
        var digits = 0;
        while (digits < line.Length && digits < 4 && char.IsAsciiDigit(line[digits])) digits++;
        if (digits == 0 || digits + 1 >= line.Length || line[digits] is not ('.' or ')') || line[digits + 1] != ' ') return null;
        return (line[..(digits + 1)], line[(digits + 2)..].Trim());
    }

    private static string Inline(string text) => text.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal);
}
