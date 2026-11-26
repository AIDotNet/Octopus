using System.Text;
using System.Text.RegularExpressions;
using TKCode.Arc;

namespace Octopus.App.Files.Documents;

/// <summary>What the rich text editor cannot keep of a Markdown file. Each one is named in the warning before the first save.</summary>
public enum MarkdownIssue
{
    /// <summary>A YAML or TOML block at the top of the file.</summary>
    FrontMatter,
    /// <summary>HTML on its own lines (a div, a picture tag, a comment block).</summary>
    HtmlBlock,
    /// <summary>A line break inside a paragraph written as two trailing spaces or a backslash.</summary>
    HardLineBreak,
    /// <summary>A picture in the middle of a line of text.</summary>
    InlineImage,
    /// <summary>Code made of indented lines instead of a fence.</summary>
    IndentedCode,
    /// <summary>A code fence written with tildes.</summary>
    TildeFence,
    /// <summary>A heading underlined with = or - instead of #.</summary>
    SetextHeading,
    /// <summary>A numbered list that starts at a number other than 1.</summary>
    ListStartNumber,
    /// <summary>Paragraphs, code or quotes inside a list item.</summary>
    NestedBlocksInList,
    /// <summary>The rewrite differs from the file in a way none of the rules above explains.</summary>
    Unrecognized,
}

/// <summary>
/// The verdict on opening a Markdown file in the editor: what it would lose (<see cref="Lost"/>), whether it would only
/// be laid out differently (<see cref="Reformats"/>: wrapped lines joined, list and table markers rewritten, indentation
/// normalized), and the text the editor writes back for it (<see cref="Exported"/>).
/// </summary>
public sealed partial record MarkdownFidelity(IReadOnlyList<MarkdownIssue> Lost, bool Reformats, string Exported)
{
    /// <summary>The editor keeps everything and writes the file back as it was.</summary>
    public bool IsExact => Lost.Count == 0 && !Reformats;

    /// <summary>Saving would lose or change content, not only its layout.</summary>
    public bool HasLoss => Lost.Count > 0;

    /// <summary>The editor's own import of a Markdown text and back: what saving an unedited document would write.</summary>
    public static string RoundTrip(string markdown)
    {
        var editor = new RichTextEditor { Profile = RichTextEditorProfile.Docs };
        editor.SetMarkdown(markdown);
        return editor.GetMarkdown();
    }

    public static MarkdownFidelity Assess(string markdown) => Assess(markdown, RoundTrip);

    /// <summary>
    /// Compares the text with what <paramref name="roundTrip"/> makes of it. The same text means nothing is lost. A
    /// difference is then explained: by the editor's own import report (only the kinds that really change the text; a
    /// footnote, a reference link or a backslash escape is kept as written and so is not a loss), by a front matter
    /// block, by blocks nested in list items, and otherwise by comparing the two with layout stripped away. What
    /// stays unexplained counts as a loss, because the check must err towards not touching the file.
    /// </summary>
    public static MarkdownFidelity Assess(string markdown, Func<string, string> roundTrip)
    {
        var exported = roundTrip(markdown);
        if (SameDocument(markdown, exported)) return new MarkdownFidelity([], false, exported);

        var lost = new List<MarkdownIssue>();
        var frontMatterEnd = FrontMatterEndLine(markdown);
        if (frontMatterEnd > 0) lost.Add(MarkdownIssue.FrontMatter);
        foreach (var issue in RichTextEditorMarkdown.Analyze(markdown, RichTextEditorProfile.Docs).Issues)
        {
            if (issue.Severity != RichTextImportSeverity.Lossy) continue;
            if (Map(issue.Kind) is not { } mapped) continue;
            if (mapped == MarkdownIssue.SetextHeading && issue.Line <= frontMatterEnd) continue;
            if (!lost.Contains(mapped)) lost.Add(mapped);
        }
        if (HasBackslashBreak(markdown) && !lost.Contains(MarkdownIssue.HardLineBreak)) lost.Add(MarkdownIssue.HardLineBreak);
        if (HasBlocksNestedInList(markdown) && !lost.Contains(MarkdownIssue.NestedBlocksInList)) lost.Add(MarkdownIssue.NestedBlocksInList);
        if (lost.Count == 0 && !string.Equals(Canonical(markdown), Canonical(exported), StringComparison.Ordinal)) lost.Add(MarkdownIssue.Unrecognized);
        return new MarkdownFidelity(lost, true, exported);
    }

    private static MarkdownIssue? Map(RichTextImportIssueKind kind) => kind switch
    {
        RichTextImportIssueKind.HtmlBlock => MarkdownIssue.HtmlBlock,
        RichTextImportIssueKind.HardLineBreak => MarkdownIssue.HardLineBreak,
        RichTextImportIssueKind.Image => MarkdownIssue.InlineImage,
        RichTextImportIssueKind.IndentedCode => MarkdownIssue.IndentedCode,
        RichTextImportIssueKind.TildeFence => MarkdownIssue.TildeFence,
        RichTextImportIssueKind.SetextHeading => MarkdownIssue.SetextHeading,
        RichTextImportIssueKind.ListStartNumber => MarkdownIssue.ListStartNumber,
        _ => null,
    };

    /// <summary>The same text, but for the final newline and the line endings (the editor writes neither a final newline nor a CR).</summary>
    private static bool SameDocument(string a, string b)
    {
        var sa = a.AsSpan().TrimEnd("\r\n");
        var sb = b.AsSpan().TrimEnd("\r\n");
        if (!sa.Contains('\r') && !sb.Contains('\r')) return sa.SequenceEqual(sb);
        int ia = 0, ib = 0;
        while (ia < sa.Length && ib < sb.Length)
        {
            if (sa[ia] == '\r' && ia + 1 < sa.Length && sa[ia + 1] == '\n') ia++;
            if (sb[ib] == '\r' && ib + 1 < sb.Length && sb[ib + 1] == '\n') ib++;
            if (sa[ia] != sb[ib]) return false;
            ia++;
            ib++;
        }
        return ia == sa.Length && ib == sb.Length;
    }

    /// <summary>The line (1-based) where a front matter block ends, or 0 when the text has none.</summary>
    private static int FrontMatterEndLine(string markdown)
    {
        var span = markdown.AsSpan();
        var firstEnd = span.IndexOf('\n');
        if (firstEnd < 0) return 0;
        var open = span[..firstEnd].TrimEnd();
        if (open is not ("---" or "+++")) return 0;
        var pos = firstEnd + 1;
        for (var i = 1; pos < span.Length && i < 200; i++)
        {
            var nl = span[pos..].IndexOf('\n');
            var raw = nl < 0 ? span[pos..] : span.Slice(pos, nl);
            var line = raw.TrimEnd();
            if (line.SequenceEqual(open) || (open is "---" && line is "...")) return i + 1;
            if (nl < 0) break;
            pos += nl + 1;
        }
        return 0;
    }

    /// <summary>A line that ends in a backslash and continues on the next line: a hard line break the editor writes back as a stray backslash.</summary>
    private static bool HasBackslashBreak(string markdown)
    {
        var span = markdown.AsSpan();
        var inFence = false;
        var pos = 0;
        while (pos < span.Length)
        {
            var nl = span[pos..].IndexOf('\n');
            if (nl < 0) break;
            var line = span.Slice(pos, nl).TrimEnd('\r');
            pos += nl + 1;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)) inFence = !inFence;
            if (inFence || line.Length == 0 || line[^1] != '\\') continue;
            var nextNl = span[pos..].IndexOf('\n');
            var nextLine = nextNl < 0 ? span[pos..] : span.Slice(pos, nextNl);
            if (nextLine.Trim().Length == 0) continue;
            var slashes = line.Length - line.TrimEnd('\\').Length;
            if ((slashes & 1) == 1) return true;
        }
        return false;
    }

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s+")]
    private static partial Regex ListMarker();

    /// <summary>
    /// A paragraph, code fence or quote that continues a list item (indented under it after a blank line). The editor
    /// keeps list items to one text block, so these are pulled out of the list.
    /// </summary>
    private static bool HasBlocksNestedInList(string markdown)
    {
        var inList = false;
        var inFence = false;
        var previousBlank = false;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Replace("\t", "    ");
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) { previousBlank = true; continue; }
            var indent = line.Length - trimmed.Length;
            var isFence = trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);
            if (inFence)
            {
                if (isFence) inFence = false;
                previousBlank = false;
                continue;
            }
            var isMarker = ListMarker().IsMatch(line);
            if (isMarker) inList = true;
            else if (inList && indent >= 2 && (previousBlank || isFence)) return true;
            else if (indent == 0 && previousBlank) inList = false;
            if (isFence) inFence = true;
            previousBlank = false;
        }
        return false;
    }

    [GeneratedRegex(@"^([-*_])(\s*\1){2,}$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)+\|?$")]
    private static partial Regex TableDelimiter();

    [GeneratedRegex(@"\s*\|\s*")]
    private static partial Regex TableBars();

    [GeneratedRegex(@"(?<!\*)\*(?!\*)")]
    private static partial Regex SingleStar();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>
    /// The text with layout taken out: wrapped lines joined, blank lines, indentation, list markers, table delimiter rows,
    /// rule and emphasis spellings all made alike. Fenced code keeps its own lines. Two texts with the same canonical
    /// form say the same thing in different clothes.
    /// </summary>
    private static string Canonical(string markdown)
    {
        var result = new StringBuilder();
        var inFence = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            var isFence = line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal);
            if (isFence)
            {
                inFence = !inFence;
                result.Append('\n').Append(line);
                continue;
            }
            if (inFence)
            {
                result.Append('\n').Append(raw.TrimEnd());
                continue;
            }
            if (line.Length == 0) continue;
            if (Rule().IsMatch(line))
            {
                line = "---";
            }
            else if (line.Contains('|') && TableDelimiter().IsMatch(line))
            {
                continue;
            }
            else
            {
                line = ListMarker().Replace(line, "");
                if (line.Contains('|')) line = TableBars().Replace(line, "|");
                line = SingleStar().Replace(line.Replace("__", "**"), "_");
                line = Spaces().Replace(line, " ");
            }
            result.Append(' ').Append(line);
        }
        return result.ToString().Trim();
    }
}
