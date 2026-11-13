using System.Text;
using System.Text.RegularExpressions;
using TKCode.Terminal.Core;

namespace Octopus.Terminal.Avalonia;

/// <summary>One screen row as the link finder sees it.</summary>
/// <param name="Cells">Text per column. Null is an empty cell (read as a space); "" is the second half of a wide character (skipped).</param>
/// <param name="WrapsToNext">The row continues on the next one (soft wrap).</param>
/// <param name="IsContinuation">The row continues the previous one.</param>
public readonly record struct LinkRow(string?[] Cells, bool WrapsToNext, bool IsContinuation);

/// <summary>
/// A link on the screen and the cells it covers (one span per row). For a web link <paramref name="Url"/> is the address;
/// for a file path (<paramref name="IsPath"/>) it is the path as printed, without the <c>:line:column</c> after it.
/// </summary>
public sealed record LinkHit(string Url, IReadOnlyList<CellSpan> Spans, bool IsPath = false, int? Line = null, int? Column = null);

/// <summary>
/// Finds http and https links and file paths in terminal output, including those that wrap over several rows. A path is
/// only a link when the caller says it exists, so ordinary words with a dot or a slash are left alone.
/// </summary>
public static partial class TerminalLinks
{
    // Stops at whitespace, quotes, angle brackets and the box-drawing bars TUIs use for borders.
    [GeneratedRegex("""https?://[^\s<>"'`│┃║|]+""", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    // A word that may be a path: no whitespace, quotes, brackets or the box-drawing bars TUIs use for borders.
    [GeneratedRegex("""[^\s<>"'`│┃║|()\[\]{},;]+""")]
    private static partial Regex PathPattern();

    // ":12" or ":12:5" after a path, as compilers, grep and agents print locations.
    [GeneratedRegex(@":(\d+)(?::(\d+))?$")]
    private static partial Regex LocationSuffix();

    /// <summary>Only http and https are ever opened.</summary>
    public static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);

    /// <summary>The link under cell (<paramref name="row"/>, <paramref name="column"/>), or null.</summary>
    public static LinkHit? Find(int rowCount, Func<int, LinkRow> getRow, int row, int column, Func<string, bool>? pathExists = null)
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
        return pathExists is null ? null : FindPath(text.ToString(), hit, rows, columns, pathExists);
    }

    private static LinkHit? FindPath(string text, int hit, List<int> rows, List<int> columns, Func<string, bool> pathExists)
    {
        foreach (Match match in PathPattern().Matches(text))
        {
            if (hit < match.Index || hit >= match.Index + match.Length) continue;
            var word = TrimTrailing(match.Value);
            if (hit >= match.Index + word.Length || word.Contains("://", StringComparison.Ordinal)) return null;
            var path = word;
            int? line = null, column = null;
            if (LocationSuffix().Match(word) is { Success: true } location)
            {
                path = word[..location.Index];
                line = int.Parse(location.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (location.Groups[2].Success) column = int.Parse(location.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            if (!LooksLikePath(path) || !pathExists(path)) return null;
            var spans = new List<CellSpan>();
            for (var i = match.Index; i < match.Index + word.Length; i++)
            {
                if (spans.Count > 0 && spans[^1].Row == rows[i])
                    spans[^1] = spans[^1] with { EndColumn = columns[i] };
                else
                    spans.Add(new CellSpan(rows[i], columns[i], columns[i]));
            }
            return new LinkHit(path, spans, IsPath: true, Line: line, Column: column);
        }
        return null;
    }

    /// <summary>Has a directory separator or a file extension: <c>src/a.cs</c>, <c>C:\x</c>, <c>README.md</c>, not <c>hello</c>.</summary>
    private static bool LooksLikePath(string path)
    {
        if (path.Length < 2) return false;
        if (path.AsSpan().IndexOfAny('\\', '/') >= 0) return true;
        var dot = path.LastIndexOf('.');
        return dot > 0 && dot < path.Length - 1 && path[(dot + 1)..].All(char.IsAsciiLetterOrDigit);
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
    public static LinkHit? Find(FrameSnapshot frame, int row, int column, Func<string, bool>? pathExists = null)
    {
        // Each row is asked for several times while the wrapped line is found: its text is built once.
        var rows = new LinkRow?[frame.Rows.Length];
        return Find(frame.Rows.Length, i => rows[i] ??= RowOf(frame.Rows[i]), row, column, pathExists);
    }
}
