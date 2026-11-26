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
/// <paramref name="IsHyperlink"/>: the program marked the cells as a link (OSC 8), so the target need not be the text
/// on the screen.
/// </summary>
public sealed record LinkHit(string Url, IReadOnlyList<CellSpan> Spans, bool IsPath = false, int? Line = null, int? Column = null,
    bool IsHyperlink = false);

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
        {
            var cell = row.Cells[i];
            if (TCell.Width(cell) is CellWidth.SpacerTail or CellWidth.SpacerHead)
            {
                cells[i] = "";
                continue;
            }
            if (TCell.HasGrapheme(cell))
            {
                cells[i] = row.TextAt(i);
                continue;
            }
            var cp = TCell.Codepoint(cell);
            cells[i] = cp == 0 ? null : cp < 128 ? AsciiChars[cp] : cp <= 0xFFFF ? ((char)cp).ToString() : char.ConvertFromUtf32((int)cp);
        }
        return new LinkRow(cells, row.Flags.HasFlag(RowFlags.SoftWrapped), row.Flags.HasFlag(RowFlags.WrapContinuation));
    }

    private static readonly string[] AsciiChars = CreateAsciiChars();

    private static string[] CreateAsciiChars()
    {
        var table = new string[128];
        for (var i = 0; i < 128; i++) table[i] = ((char)i).ToString();
        return table;
    }

    /// <summary>
    /// Extracts all distinct http/https URLs visible in <paramref name="frame"/>, preserving appearance order and
    /// rejoining URLs that soft-wrapped across consecutive rows. Also includes any OSC 8 web hyperlinks.
    /// </summary>
    public static IReadOnlyList<string> ExtractWebUrls(FrameSnapshot frame)
    {
        var rows = new LinkRow?[frame.Rows.Length];
        return ExtractWebUrls(frame.Rows.Length, i => rows[i] ??= RowOf(frame.Rows[i]), frame.Links);
    }

    /// <summary>
    /// Extracts all distinct http/https URLs across <paramref name="rowCount"/> screen rows, joining soft-wrapped
    /// lines before matching, followed by any valid web URLs in <paramref name="osc8Links"/>.
    /// </summary>
    public static IReadOnlyList<string> ExtractWebUrls(int rowCount, Func<int, LinkRow> getRow, IReadOnlyList<string>? osc8Links = null)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var line = new StringBuilder();
        var r = 0;
        while (r < rowCount)
        {
            var first = r;
            var last = r;
            while (last + 1 < rowCount && (getRow(last).WrapsToNext || getRow(last + 1).IsContinuation)) last++;

            line.Clear();
            for (var i = first; i <= last; i++)
            {
                var cells = getRow(i).Cells;
                for (var c = 0; c < cells.Length; c++)
                {
                    var cell = cells[c];
                    if (cell is { Length: 0 }) continue;
                    line.Append(cell ?? " ");
                }
            }

            foreach (Match match in UrlPattern().Matches(line.ToString()))
            {
                var url = TrimTrailing(match.Value);
                if (IsWebUrl(url) && seen.Add(url))
                {
                    urls.Add(url);
                }
            }

            r = last + 1;
        }

        if (osc8Links is not null)
        {
            foreach (var link in osc8Links)
            {
                if (IsWebUrl(link) && seen.Add(link))
                {
                    urls.Add(link);
                }
            }
        }

        return urls;
    }

    /// <summary>
    /// The link under a cell of <paramref name="frame"/>: the program's own hyperlink (OSC 8) when the cell has one that
    /// can be opened, otherwise a link or path found in the text.
    /// </summary>
    public static LinkHit? Find(FrameSnapshot frame, int row, int column, Func<string, bool>? pathExists = null)
    {
        if (FindHyperlink(frame, row, column, pathExists) is { } hyperlink) return hyperlink;
        // Each row is asked for several times while the wrapped line is found: its text is built once.
        var rows = new LinkRow?[frame.Rows.Length];
        return Find(frame.Rows.Length, i => rows[i] ??= RowOf(frame.Rows[i]), row, column, pathExists);
    }

    /// <summary>
    /// The OSC 8 hyperlink under a cell, covering every cell next to it (across soft wraps) that carries the same link.
    /// Only http and https addresses, and <c>file:</c> addresses on this computer that <paramref name="pathExists"/>
    /// accepts, are links; any other target is null, and the text is then searched as usual.
    /// </summary>
    public static LinkHit? FindHyperlink(FrameSnapshot frame, int row, int column, Func<string, bool>? pathExists = null)
    {
        var id = LinkIdAt(frame, row, column);
        if (id <= 0 || id > frame.Links.Count) return null;
        var url = frame.Links[id - 1];

        string target;
        var isPath = false;
        if (IsWebUrl(url))
        {
            target = url;
        }
        else if (pathExists is not null && LocalFilePath(url) is { } path && pathExists(path))
        {
            target = path;
            isPath = true;
        }
        else
        {
            return null;
        }

        // Walk back to the first cell of the link, then forward over all of it.
        var (r, c) = (row, column);
        while (Previous(frame, r, c) is { } p && LinkIdAt(frame, p.Row, p.Column) == id) (r, c) = p;
        var spans = new List<CellSpan>();
        for (var at = ((int Row, int Column)?)(r, c); at is { } cell && LinkIdAt(frame, cell.Row, cell.Column) == id; at = Next(frame, cell.Row, cell.Column))
        {
            if (spans.Count > 0 && spans[^1].Row == cell.Row)
                spans[^1] = spans[^1] with { EndColumn = cell.Column };
            else
                spans.Add(new CellSpan(cell.Row, cell.Column, cell.Column));
        }
        return new LinkHit(target, spans, IsPath: isPath, IsHyperlink: true);
    }

    /// <summary>The hyperlink id of a cell (0: none). The second half of a wide character belongs to its first half.</summary>
    private static int LinkIdAt(FrameSnapshot frame, int row, int column)
    {
        if (row < 0 || row >= frame.Rows.Length) return 0;
        var r = frame.Rows[row];
        if (column < 0 || column >= r.Cells.Length) return 0;
        if (TCell.Width(r.Cells[column]) == CellWidth.SpacerTail && column > 0) column--;
        var cell = r.Cells[column];
        return TCell.Hyperlink(cell) ? r.Styles[TCell.StyleIndex(cell)].HyperlinkId : 0;
    }

    private static (int Row, int Column)? Previous(FrameSnapshot frame, int row, int column)
    {
        if (column > 0) return (row, column - 1);
        return row > 0 && frame.Rows[row - 1].Flags.HasFlag(RowFlags.SoftWrapped) ? (row - 1, frame.Rows[row - 1].Cells.Length - 1) : null;
    }

    private static (int Row, int Column)? Next(FrameSnapshot frame, int row, int column)
    {
        if (column + 1 < frame.Rows[row].Cells.Length) return (row, column + 1);
        return row + 1 < frame.Rows.Length && frame.Rows[row].Flags.HasFlag(RowFlags.SoftWrapped) ? (row + 1, 0) : null;
    }

    /// <summary>
    /// The local path of a <c>file:</c> address whose host is empty, <c>localhost</c> or this computer (as <c>ls
    /// --hyperlink</c> and ripgrep write them); null for anything else, including a file on another host.
    /// </summary>
    public static string? LocalFilePath(string url)
    {
        // The scheme must be written out: on Unix a bare "/etc/hosts" also parses as a file address.
        if (!url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile || !IsThisHost(uri.Host))
        {
            return null;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        // Windows: "/C:/Users/x" → "C:\Users\x".
        return OperatingSystem.IsWindows() ? path.TrimStart('/').Replace('/', '\\') : path;
    }

    private static readonly Lazy<string> HostName = new(() =>
    {
        try { return System.Net.Dns.GetHostName(); }
        catch (System.Net.Sockets.SocketException) { return Environment.MachineName; }
    });

    private static bool IsThisHost(string host)
    {
        if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        static string Short(string name) => name.Split('.')[0];
        return host.Equals(HostName.Value, StringComparison.OrdinalIgnoreCase)
            || Short(host).Equals(Short(HostName.Value), StringComparison.OrdinalIgnoreCase)
            || Short(host).Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }
}
