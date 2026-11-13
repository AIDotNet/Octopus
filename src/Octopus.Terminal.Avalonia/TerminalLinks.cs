using System.Text;
using System.Text.RegularExpressions;
using TKCode.Terminal.Core;

namespace Octopus.Terminal.Avalonia;

/// <summary>One screen row as the link finder sees it.</summary>
/// <param name="Cells">Text per column. Null is an empty cell (read as a space); "" is the second half of a wide character (skipped).</param>
/// <param name="WrapsToNext">The row continues on the next one (soft wrap).</param>
/// <param name="IsContinuation">The row continues the previous one.</param>
public readonly record struct LinkRow(string?[] Cells, bool WrapsToNext, bool IsContinuation);

/// <summary>A web link on the screen and the cells it covers (one span per row).</summary>
public sealed record LinkHit(string Url, IReadOnlyList<CellSpan> Spans);

/// <summary>Finds http and https links in terminal output, including links that wrap over several rows.</summary>
public static partial class TerminalLinks
{
    // Stops at whitespace, quotes, angle brackets and the box-drawing bars TUIs use for borders.
    [GeneratedRegex("""https?://[^\s<>"'`│┃║|]+""", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    /// <summary>Only http and https are ever opened.</summary>
    public static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);

    /// <summary>The link under cell (<paramref name="row"/>, <paramref name="column"/>), or null.</summary>
    public static LinkHit? Find(int rowCount, Func<int, LinkRow> getRow, int row, int column)
    {
        if (row < 0 || row >= rowCount) return null;
        var first = row;
        while (first > 0 && (getRow(first).IsContinuation || getRow(first - 1).WrapsToNext)) first--;
        var last = row;
        while (last + 1 < rowCount && (getRow(last).WrapsToNext || getRow(last + 1).IsContinuation)) last++;

        var text = new StringBuilder();
        var rows = new List<int>();
        var columns = new List<int>();
        var hit = -1;
        for (var r = first; r <= last; r++)
        {
            var cells = getRow(r).Cells;
            for (var c = 0; c < cells.Length; c++)
            {
                var cell = cells[c];
                if (cell is { Length: 0 }) continue;
                if (r == row && c == column && hit < 0) hit = text.Length;
                foreach (var ch in cell ?? " ")
                {
                    text.Append(ch);
                    rows.Add(r);
                    columns.Add(c);
                }
            }
        }
        if (hit < 0) return null;

        foreach (Match match in UrlPattern().Matches(text.ToString()))
        {
            if (hit < match.Index || hit >= match.Index + match.Length) continue;
            var url = TrimTrailing(match.Value);
            if (hit >= match.Index + url.Length || !IsWebUrl(url)) return null;
            var spans = new List<CellSpan>();
            for (var i = match.Index; i < match.Index + url.Length; i++)
            {
                if (spans.Count > 0 && spans[^1].Row == rows[i])
                    spans[^1] = spans[^1] with { EndColumn = columns[i] };
                else
                    spans.Add(new CellSpan(rows[i], columns[i], columns[i]));
            }
            return new LinkHit(url, spans);
        }
        return null;
    }

    /// <summary>Sentence punctuation and unbalanced closing brackets at the end are not part of the link.</summary>
    public static string TrimTrailing(string url)
    {
        while (url.Length > 0)
        {
            var last = url[^1];
            if (".,;:!?".Contains(last) || last == '…') { url = url[..^1]; continue; }
            if (last is ')' or ']' or '}')
            {
                var open = last switch { ')' => '(', ']' => '[', _ => '{' };
                if (url.Count(c => c == last) > url.Count(c => c == open)) { url = url[..^1]; continue; }
            }
            break;
        }
        return url;
    }

    /// <summary>A frame row as a <see cref="LinkRow"/>.</summary>
    public static LinkRow RowOf(RowSnapshot row)
    {
        var cells = new string?[row.Cells.Length];
        for (var i = 0; i < cells.Length; i++)
            cells[i] = TCell.Width(row.Cells[i]) is CellWidth.SpacerTail or CellWidth.SpacerHead ? "" : row.TextAt(i);
        return new LinkRow(cells, row.Flags.HasFlag(RowFlags.SoftWrapped), row.Flags.HasFlag(RowFlags.WrapContinuation));
    }

    /// <summary>The link under a cell of <paramref name="frame"/>.</summary>
    public static LinkHit? Find(FrameSnapshot frame, int row, int column)
    {
        // Each row is asked for several times while the wrapped line is found: its text is built once.
        var rows = new LinkRow?[frame.Rows.Length];
        return Find(frame.Rows.Length, i => rows[i] ??= RowOf(frame.Rows[i]), row, column);
    }
}
