using System.Text;

namespace Octopus.Mobile.Common;

/// <summary>The kind of a parsed Markdown block.</summary>
public enum MarkdownBlockKind
{
    Heading,
    Paragraph,
    ListItem,
    Quote,
    Code,
    Divider,
    Table,
    Image,
}

/// <summary>The kind of an inline span inside a Markdown block.</summary>
public enum MarkdownInlineKind
{
    Text,
    Bold,
    Italic,
    BoldItalic,
    Strikethrough,
    Code,
    Link,
    Image,
}

/// <summary>Horizontal alignment of a Markdown table column.</summary>
public enum MarkdownColumnAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>One styled span inside a paragraph, heading, list item, quote, or table cell.</summary>
public sealed record MarkdownInline(MarkdownInlineKind Kind, string Text, string? Url = null);

/// <summary>One parsed cell of a Markdown pipe table.</summary>
public sealed record MarkdownTableCell(IReadOnlyList<MarkdownInline> Inlines, string RawText);

/// <summary>A parsed GitHub-Flavored Markdown pipe table.</summary>
public sealed record MarkdownTable(
    IReadOnlyList<MarkdownTableCell> Headers,
    IReadOnlyList<MarkdownColumnAlignment> Alignments,
    IReadOnlyList<IReadOnlyList<MarkdownTableCell>> Rows);

/// <summary>
/// One block of a Markdown document for <see cref="MobileMarkdownView"/>.
/// <paramref name="Level"/> is 1..4 for headings and 0..2 for nested list items; <paramref name="Marker"/> is the
/// bullet/number/checkbox of a list item, the language tag of a fenced code block, or the alt text of an image block.
/// </summary>
public sealed record MarkdownBlock(
    MarkdownBlockKind Kind,
    IReadOnlyList<MarkdownInline> Inlines,
    int Level = 0,
    string Marker = "",
    string RawText = "",
    MarkdownTable? Table = null);

/// <summary>
/// Lightweight, NativeAOT-safe Markdown parser for mobile views (agent conversations, file previews, release notes).
/// Supports ATX headings (H1–H4), GFM pipe tables (with column alignment, escaped pipes, &lt;br&gt; breaks, and rich
/// inline spans), standalone &amp; inline images, nested bullet/numbered/task lists, blockquotes, fenced code blocks,
/// horizontal rules, HTML comment stripping, inline bold/italic/strikethrough/code, web &amp; local file links, and
/// bare HTTP(S) URLs.
/// </summary>
public static class MobileMarkdownParser
{
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var rawLines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new StringBuilder();
        var quote = new StringBuilder();
        StringBuilder? code = null;
        var fence = "";
        var codeLang = "";
        var inHtmlComment = false;

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            var text = NormalizeInlineHtml(paragraph.ToString());
            paragraph.Clear();
            if (string.IsNullOrWhiteSpace(text)) return;
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, ParseInlines(text), RawText: text));
        }

        void FlushQuote()
        {
            if (quote.Length == 0) return;
            var text = NormalizeInlineHtml(quote.ToString());
            quote.Clear();
            if (string.IsNullOrWhiteSpace(text)) return;
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Quote, ParseInlines(text), RawText: text));
        }

        void FlushText()
        {
            FlushParagraph();
            FlushQuote();
        }

        for (var lineIndex = 0; lineIndex < rawLines.Length; lineIndex++)
        {
            var raw = rawLines[lineIndex];
            var line = raw.TrimEnd();
            var indent = CountIndent(line);
            var trimmed = line.TrimStart();

            if (code is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal) && trimmed.Trim('`', '~').Length == 0)
                {
                    blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, [], Marker: codeLang, RawText: code.ToString().TrimEnd('\n')));
                    code = null;
                    codeLang = "";
                }
                else
                {
                    code.Append(raw).Append('\n');
                }
                continue;
            }

            // Strip HTML comments (single-line and multi-line) outside fenced code blocks.
            var cleaned = StripHtmlComments(line, ref inHtmlComment);
            if (inHtmlComment && string.IsNullOrWhiteSpace(cleaned))
            {
                continue;
            }
            if (!ReferenceEquals(cleaned, line))
            {
                line = cleaned.TrimEnd();
                indent = CountIndent(line);
                trimmed = line.TrimStart();
                if (trimmed.Length == 0)
                {
                    continue;
                }
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushText();
                fence = trimmed[..3];
                codeLang = trimmed[3..].Trim();
                code = new StringBuilder();
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushText();
                continue;
            }

            if (TryParseHeading(trimmed) is { } heading)
            {
                FlushText();
                var text = NormalizeInlineHtml(heading.Text);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading, ParseInlines(text), Level: heading.Level, RawText: text));
                continue;
            }

            if (IsHorizontalRule(trimmed))
            {
                FlushText();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Divider, []));
                continue;
            }

            // GFM pipe table: header row followed by a valid delimiter row.
            if (lineIndex + 1 < rawLines.Length && TryParseTable(rawLines, lineIndex, out var table, out var nextLineIndex))
            {
                FlushText();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Table, [], RawText: "", Table: table));
                lineIndex = nextLineIndex - 1;
                continue;
            }

            // Standalone image line: ![alt](src)
            if (TryParseStandaloneImage(trimmed, out var imgAlt, out var imgSrc))
            {
                FlushText();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Image, [], Marker: imgAlt, RawText: imgSrc));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var content = trimmed.TrimStart('>').TrimStart();
                if (quote.Length > 0) quote.Append('\n');
                quote.Append(content);
                continue;
            }

            if (TryParseListItem(trimmed, indent) is { } item)
            {
                FlushText();
                var text = NormalizeInlineHtml(item.Text);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.ListItem, ParseInlines(text), Level: item.Level, Marker: item.Marker, RawText: text));
                continue;
            }

            FlushQuote();
            if (paragraph.Length > 0) paragraph.Append('\n');
            paragraph.Append(trimmed);
        }

        FlushText();
        if (code is not null)
        {
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, [], Marker: codeLang, RawText: code.ToString().TrimEnd('\n')));
        }
        return blocks;
    }

    public static IReadOnlyList<MarkdownInline> ParseInlines(string text)
    {
        var result = new List<MarkdownInline>();
        if (string.IsNullOrEmpty(text)) return result;

        var plain = new StringBuilder();
        var i = 0;

        void FlushPlain()
        {
            if (plain.Length == 0) return;
            result.Add(new MarkdownInline(MarkdownInlineKind.Text, plain.ToString()));
            plain.Clear();
        }

        while (i < text.Length)
        {
            var ch = text[i];

            // Backslash escape for Markdown punctuation
            if (ch == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            // Inline code: `code`
            if (ch == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i + 1)
                {
                    FlushPlain();
                    result.Add(new MarkdownInline(MarkdownInlineKind.Code, text[(i + 1)..close]));
                    i = close + 1;
                    continue;
                }
            }

            // Inline image: ![alt](src)
            if (ch == '!' && i + 1 < text.Length && text[i + 1] == '[' && TryParseLinkCore(text, i + 1, allowEmptyLabel: true, out var alt, out var src, out var imgNext))
            {
                FlushPlain();
                result.Add(new MarkdownInline(MarkdownInlineKind.Image, alt.Length > 0 ? alt : FallbackName(src), src));
                i = imgNext;
                continue;
            }

            // Markdown link: [label](url)
            if (ch == '[' && TryParseLink(text, i, out var label, out var url, out var nextIndex))
            {
                FlushPlain();
                result.Add(new MarkdownInline(MarkdownInlineKind.Link, label, url));
                i = nextIndex;
                continue;
            }

            // Bare HTTP(S) URL
            if (ch == 'h' && TryParseBareUrl(text, i, out var bareUrl, out var bareEnd))
            {
                FlushPlain();
                result.Add(new MarkdownInline(MarkdownInlineKind.Link, bareUrl, bareUrl));
                i = bareEnd;
                continue;
            }

            // Bold + Italic: ***text***
            if (ch == '*' && MatchesAt(text, i, "***") && FindClosing(text, i + 3, "***") is { } biEnd)
            {
                FlushPlain();
                result.Add(new MarkdownInline(MarkdownInlineKind.BoldItalic, text[(i + 3)..biEnd]));
                i = biEnd + 3;
                continue;
            }

            // Bold: **text** or __text__
            if (ch == '*' && MatchesAt(text, i, "**") && FindClosing(text, i + 2, "**") is { } bStarEnd)
            {
                FlushPlain();
                result.Add(new MarkdownInline(MarkdownInlineKind.Bold, text[(i + 2)..bStarEnd]));
                i = bStarEnd + 2;
                continue;
            }
            if (ch == '_' && MatchesAt(text, i, "__") && IsWordBoundaryStart(text, i) && FindClosing(text, i + 2, "__") is { } bUnderEnd)
            {
                FlushPlain();
                result.Add(new MarkdownInline(MarkdownInlineKind.Bold, text[(i + 2)..bUnderEnd]));
                i = bUnderEnd + 2;
                continue;
            }

            // Strikethrough: ~~text~~
            if (ch == '~' && MatchesAt(text, i, "~~") && FindClosing(text, i + 2, "~~") is { } sEnd)
            {
                FlushPlain();
                result.Add(new MarkdownInline(MarkdownInlineKind.Strikethrough, text[(i + 2)..sEnd]));
                i = sEnd + 2;
                continue;
            }

            // Italic: *text* or _text_ (respecting word boundaries for underscores so snake_case stays literal)
            if (ch == '*' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && text[i + 1] != '*' && FindSingleClosing(text, i + 1, '*') is { } iStarEnd)
            {
                FlushPlain();
                result.Add(new MarkdownInline(MarkdownInlineKind.Italic, text[(i + 1)..iStarEnd]));
                i = iStarEnd + 1;
                continue;
            }
            if (ch == '_' && IsWordBoundaryStart(text, i) && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && text[i + 1] != '_' && FindSingleClosing(text, i + 1, '_') is { } iUnderEnd && IsWordBoundaryEnd(text, iUnderEnd))
            {
                FlushPlain();
                result.Add(new MarkdownInline(MarkdownInlineKind.Italic, text[(i + 1)..iUnderEnd]));
                i = iUnderEnd + 1;
                continue;
            }

            plain.Append(ch);
            i++;
        }

        FlushPlain();
        return result;
    }

    // ---- GFM pipe tables ----

    private static bool TryParseTable(string[] lines, int startIndex, out MarkdownTable table, out int nextIndex)
    {
        table = null!;
        nextIndex = startIndex;

        var headerLine = lines[startIndex].Trim();
        var sepLine = lines[startIndex + 1].Trim();
        if (!headerLine.Contains('|') || !sepLine.Contains('|')) return false;

        var rawHeaders = SplitTableRow(headerLine);
        var rawSeps = SplitTableRow(sepLine);
        if (rawHeaders.Count == 0 || rawSeps.Count == 0) return false;
        if (!TryParseAlignments(rawSeps, out var alignments)) return false;

        // Require column count agreement (or at least 2 columns if outer pipes were omitted).
        if (rawHeaders.Count != alignments.Count) return false;

        var colCount = rawHeaders.Count;
        var headers = new List<MarkdownTableCell>(colCount);
        foreach (var rawHeader in rawHeaders)
        {
            var normalized = NormalizeInlineHtml(rawHeader);
            headers.Add(new MarkdownTableCell(ParseInlines(normalized), normalized));
        }

        var rows = new List<IReadOnlyList<MarkdownTableCell>>();
        var idx = startIndex + 2;
        while (idx < lines.Length)
        {
            var rowLine = lines[idx].Trim();
            if (rowLine.Length == 0 || !rowLine.Contains('|')) break;
            if (rowLine.StartsWith("```", StringComparison.Ordinal) || rowLine.StartsWith("~~~", StringComparison.Ordinal)) break;
            if (TryParseHeading(rowLine) is not null || IsHorizontalRule(rowLine)) break;

            var rawCells = SplitTableRow(rowLine);
            var cells = new List<MarkdownTableCell>(colCount);
            for (var c = 0; c < colCount; c++)
            {
                var rawCell = c < rawCells.Count ? rawCells[c] : "";
                var normalized = NormalizeInlineHtml(rawCell);
                cells.Add(new MarkdownTableCell(ParseInlines(normalized), normalized));
            }
            rows.Add(cells);
            idx++;
        }

        table = new MarkdownTable(headers, alignments, rows);
        nextIndex = idx;
        return true;
    }

    private static List<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        var start = trimmed.StartsWith('|') ? 1 : 0;
        var end = trimmed.Length;
        if (end > start && trimmed[end - 1] == '|' && !IsEscapedAt(trimmed, end - 1))
        {
            end--;
        }

        var cells = new List<string>();
        var current = new StringBuilder();
        var inCode = false;

        for (var i = start; i < end; i++)
        {
            var ch = trimmed[i];
            if (ch == '\\' && i + 1 < end && trimmed[i + 1] == '|')
            {
                current.Append('|');
                i++;
                continue;
            }
            if (ch == '`')
            {
                inCode = !inCode;
                current.Append(ch);
                continue;
            }
            if (ch == '|' && !inCode)
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            current.Append(ch);
        }
        cells.Add(current.ToString().Trim());
        return cells;
    }

    private static bool IsEscapedAt(string text, int index)
    {
        var backslashes = 0;
        for (var i = index - 1; i >= 0 && text[i] == '\\'; i--) backslashes++;
        return (backslashes & 1) == 1;
    }

    private static bool TryParseAlignments(List<string> rawSeps, out List<MarkdownColumnAlignment> alignments)
    {
        alignments = new List<MarkdownColumnAlignment>(rawSeps.Count);
        foreach (var raw in rawSeps)
        {
            var cell = raw.Trim();
            if (cell.Length == 0) return false;
            var left = cell[0] == ':';
            var right = cell.Length > 1 && cell[^1] == ':';
            var dashStart = left ? 1 : 0;
            var dashEnd = right ? cell.Length - 1 : cell.Length;
            if (dashEnd <= dashStart) return false;
            for (var i = dashStart; i < dashEnd; i++)
            {
                if (cell[i] != '-') return false;
            }
            alignments.Add((left, right) switch
            {
                (true, true) => MarkdownColumnAlignment.Center,
                (false, true) => MarkdownColumnAlignment.Right,
                _ => MarkdownColumnAlignment.Left,
            });
        }
        return alignments.Count > 0;
    }

    // ---- HTML comments and <br> normalization ----

    private static string StripHtmlComments(string line, ref bool inComment)
    {
        if (!inComment && !line.Contains("<!--", StringComparison.Ordinal)) return line;

        var sb = new StringBuilder(line.Length);
        var i = 0;
        var inCode = false;
        while (i < line.Length)
        {
            if (inComment)
            {
                var close = line.IndexOf("-->", i, StringComparison.Ordinal);
                if (close < 0) return sb.ToString();
                inComment = false;
                i = close + 3;
                continue;
            }
            if (line[i] == '`')
            {
                inCode = !inCode;
                sb.Append(line[i++]);
                continue;
            }
            if (!inCode && MatchesAt(line, i, "<!--"))
            {
                var close = line.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (close < 0)
                {
                    inComment = true;
                    break;
                }
                i = close + 3;
                continue;
            }
            sb.Append(line[i++]);
        }
        return sb.ToString();
    }

    /// <summary>Converts inline <c>&lt;br&gt;</c>, <c>&lt;br/&gt;</c>, and <c>&lt;br /&gt;</c> tags outside backtick code spans into newlines.</summary>
    internal static string NormalizeInlineHtml(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('<')) return text;

        var sb = new StringBuilder(text.Length);
        var inCode = false;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '`')
            {
                inCode = !inCode;
                sb.Append(ch);
                i++;
                continue;
            }
            if (!inCode && ch == '<' && TryMatchBrTag(text, i, out var nextIdx))
            {
                sb.Append('\n');
                i = nextIdx;
                continue;
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    private static bool TryMatchBrTag(string text, int start, out int nextIndex)
    {
        nextIndex = start;
        if (start + 3 >= text.Length) return false;
        if (text[start] != '<') return false;
        if (text[start + 1] is not ('b' or 'B')) return false;
        if (text[start + 2] is not ('r' or 'R')) return false;
        var j = start + 3;
        while (j < text.Length && text[j] == ' ') j++;
        if (j < text.Length && text[j] == '/') j++;
        while (j < text.Length && text[j] == ' ') j++;
        if (j < text.Length && text[j] == '>')
        {
            nextIndex = j + 1;
            return true;
        }
        return false;
    }

    // ---- Blocks & inline helpers ----

    private static int CountIndent(string line)
    {
        var spaces = 0;
        foreach (var c in line)
        {
            if (c == ' ') spaces++;
            else if (c == '\t') spaces += 2;
            else break;
        }
        return spaces;
    }

    private static (int Level, string Text)? TryParseHeading(string trimmed)
    {
        var hashes = 0;
        while (hashes < trimmed.Length && hashes < 6 && trimmed[hashes] == '#') hashes++;
        if (hashes == 0 || hashes >= trimmed.Length || trimmed[hashes] != ' ') return null;
        var text = trimmed[(hashes + 1)..].Trim().TrimEnd('#').TrimEnd();
        return text.Length == 0 ? null : (Math.Min(hashes, 4), text);
    }

    private static bool IsHorizontalRule(string trimmed)
    {
        if (trimmed.Length < 3) return false;
        var marker = trimmed[0];
        if (marker is not ('-' or '*' or '_')) return false;
        var count = 0;
        foreach (var c in trimmed)
        {
            if (c == marker) count++;
            else if (c != ' ') return false;
        }
        return count >= 3;
    }

    private static (int Level, string Marker, string Text)? TryParseListItem(string trimmed, int indent)
    {
        var level = Math.Min(indent / 2, 2);

        if (trimmed is ['-' or '*' or '+', ' ', ..])
        {
            var body = trimmed[2..].Trim();
            if (body.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase))
            {
                return (level, "☑", body[4..].Trim());
            }
            if (body.StartsWith("[ ] ", StringComparison.Ordinal))
            {
                return (level, "☐", body[4..].Trim());
            }
            return (level, level == 0 ? "•" : "◦", body);
        }

        var digits = 0;
        while (digits < trimmed.Length && digits < 4 && char.IsAsciiDigit(trimmed[digits])) digits++;
        if (digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] is '.' or ')' && trimmed[digits + 1] == ' ')
        {
            return (level, trimmed[..(digits + 1)], trimmed[(digits + 2)..].Trim());
        }

        return null;
    }

    private static bool TryParseStandaloneImage(string trimmed, out string alt, out string src)
    {
        alt = "";
        src = "";
        if (trimmed.Length < 5 || trimmed[0] != '!' || trimmed[1] != '[') return false;
        if (!TryParseLinkCore(trimmed, 1, allowEmptyLabel: true, out alt, out src, out var nextIndex)) return false;
        if (nextIndex != trimmed.Length) return false;
        if (alt.Length == 0) alt = FallbackName(src);
        return true;
    }

    private static bool TryParseLink(string text, int start, out string label, out string url, out int nextIndex) =>
        TryParseLinkCore(text, start, allowEmptyLabel: false, out label, out url, out nextIndex);

    private static bool TryParseLinkCore(string text, int start, bool allowEmptyLabel, out string label, out string url, out int nextIndex)
    {
        label = "";
        url = "";
        nextIndex = start;
        var closeBracket = text.IndexOf(']', start + 1);
        if (closeBracket < start + 1 || (!allowEmptyLabel && closeBracket == start + 1) || closeBracket + 1 >= text.Length || text[closeBracket + 1] != '(') return false;

        // Allow balanced parentheses inside URLs (e.g. Wikipedia / GitHub links or file paths with parens)
        var depth = 0;
        var closeParen = -1;
        var angleWrapped = closeBracket + 2 < text.Length && text[closeBracket + 2] == '<';
        for (var j = closeBracket + 2; j < text.Length; j++)
        {
            if (angleWrapped)
            {
                if (text[j] == '>' && j + 1 < text.Length && text[j + 1] == ')')
                {
                    closeParen = j + 1;
                    break;
                }
                continue;
            }
            if (text[j] == '(')
            {
                depth++;
            }
            else if (text[j] == ')')
            {
                if (depth == 0)
                {
                    closeParen = j;
                    break;
                }
                depth--;
            }
            else if (char.IsWhiteSpace(text[j]))
            {
                break;
            }
        }
        if (closeParen <= closeBracket + 2) return false;

        var rawLabel = text[(start + 1)..closeBracket].Trim();
        var rawUrl = text[(closeBracket + 2)..closeParen].Trim();
        if (rawUrl.Length >= 2 && rawUrl[0] == '<' && rawUrl[^1] == '>')
        {
            rawUrl = rawUrl[1..^1].Trim();
        }
        if ((!allowEmptyLabel && rawLabel.Length == 0) || !IsAllowedLinkUrl(rawUrl))
        {
            return false;
        }
        label = rawLabel;
        url = rawUrl;
        nextIndex = closeParen + 1;
        return true;
    }

    internal static bool IsAllowedLinkUrl(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) || rawUrl[0] == '#') return false;
        if (rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(rawUrl, UriKind.Absolute, out _);
        }
        if (rawUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(rawUrl, UriKind.Absolute, out var fileUri)
                && fileUri.IsFile
                && (fileUri.Host.Length == 0 || string.Equals(fileUri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
        }
        // Windows drive paths like C:\foo\bar or C:/foo/bar
        if (rawUrl.Length > 2 && char.IsAsciiLetter(rawUrl[0]) && rawUrl[1] == ':' && rawUrl[2] is '\\' or '/')
        {
            return true;
        }
        // Reject unknown/unsafe URI schemes (javascript:, data:, vbscript:, mailto:, etc.),
        // while allowing file paths that contain a colon for line numbers (e.g. src/Foo.cs:42).
        var colon = rawUrl.IndexOf(':');
        if (colon >= 0)
        {
            var slash = rawUrl.IndexOfAny(['/', '\\', '?', '#']);
            if (slash < 0 || colon < slash)
            {
                // Treat "file.ext:12" as a local path, not a scheme, when the prefix contains a dot and suffix starts with a digit.
                var before = rawUrl[..colon];
                var afterIsLine = colon + 1 < rawUrl.Length && char.IsAsciiDigit(rawUrl[colon + 1]);
                if (!afterIsLine || !before.Contains('.')) return false;
            }
        }
        return true;
    }

    private static string FallbackName(string src)
    {
        var clean = src.Split(['?', '#'], 2)[0].TrimEnd('/', '\\');
        var slash = clean.LastIndexOfAny(['/', '\\']);
        var name = slash >= 0 && slash < clean.Length - 1 ? clean[(slash + 1)..] : clean;
        return string.IsNullOrEmpty(name) ? "image" : name;
    }

    private static bool TryParseBareUrl(string text, int start, out string url, out int nextIndex)
    {
        url = "";
        nextIndex = start;
        if (!MatchesAt(text, start, "https://") && !MatchesAt(text, start, "http://")) return false;
        if (start > 0 && char.IsLetterOrDigit(text[start - 1])) return false;

        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('<' or '>' or '"' or '\''))
        {
            end++;
        }
        // Trim trailing punctuation commonly attached to a URL in prose
        while (end > start && text[end - 1] is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '。' or '，' or '）')
        {
            end--;
        }
        var candidate = text[start..end];
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("https" or "http")) return false;

        url = candidate;
        nextIndex = end;
        return true;
    }

    private static bool MatchesAt(string text, int index, string token) =>
        index + token.Length <= text.Length && string.Compare(text, index, token, 0, token.Length, StringComparison.Ordinal) == 0;

    private static int? FindClosing(string text, int searchFrom, string token)
    {
        if (searchFrom >= text.Length || char.IsWhiteSpace(text[searchFrom])) return null;
        var pos = text.IndexOf(token, searchFrom, StringComparison.Ordinal);
        if (pos <= searchFrom || char.IsWhiteSpace(text[pos - 1])) return null;
        return pos;
    }

    private static int? FindSingleClosing(string text, int searchFrom, char marker)
    {
        for (var j = searchFrom; j < text.Length; j++)
        {
            if (text[j] == '\\' && j + 1 < text.Length)
            {
                j++;
                continue;
            }
            if (text[j] == marker && (j + 1 >= text.Length || text[j + 1] != marker))
            {
                if (j > searchFrom && !char.IsWhiteSpace(text[j - 1])) return j;
                return null;
            }
        }
        return null;
    }

    private static bool IsWordBoundaryStart(string text, int index) =>
        index == 0 || !char.IsLetterOrDigit(text[index - 1]);

    private static bool IsWordBoundaryEnd(string text, int index) =>
        index + 1 >= text.Length || !char.IsLetterOrDigit(text[index + 1]);

    private static bool IsEscapable(char c) =>
        c is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '(' or ')' or '#' or '+' or '-' or '.' or '!' or '~' or '|';
}
