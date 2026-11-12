using Octopus.Terminal.Avalonia;
using Xunit;

namespace Octopus.Terminal.Tests;

public class LinkTests
{
    /// <summary>Rows of plain text, one character per cell. A row ending in '\\' soft-wraps into the next.</summary>
    private static (int Count, Func<int, LinkRow> Get) Screen(params string[] rows)
    {
        var parsed = rows.Select((r, i) =>
        {
            var wraps = r.EndsWith('\\');
            var text = wraps ? r[..^1] : r;
            return new LinkRow([.. text.Select(c => (string?)c.ToString())], wraps, i > 0 && rows[i - 1].EndsWith('\\'));
        }).ToArray();
        return (parsed.Length, i => parsed[i]);
    }

    private static string? UrlAt(string[] rows, int row, int column)
    {
        var (count, get) = Screen(rows);
        return TerminalLinks.Find(count, get, row, column)?.Url;
    }

    [Fact]
    public void Finds_a_link_under_any_of_its_cells()
    {
        string[] rows = ["see https://example.com/a?b=1 now"];
        Assert.Equal("https://example.com/a?b=1", UrlAt(rows, 0, 4));
        Assert.Equal("https://example.com/a?b=1", UrlAt(rows, 0, 20));
        Assert.Null(UrlAt(rows, 0, 3));
        Assert.Null(UrlAt(rows, 0, 29));
    }

    [Theory]
    [InlineData("go to https://example.com.", "https://example.com")]
    [InlineData("(see https://example.com/x)", "https://example.com/x")]
    [InlineData("https://en.wikipedia.org/wiki/Foo_(bar)", "https://en.wikipedia.org/wiki/Foo_(bar)")]
    [InlineData("\"https://example.com/q\",", "https://example.com/q")]
    [InlineData("[https://example.com/q]", "https://example.com/q")]
    [InlineData("http://localhost:3000/path!", "http://localhost:3000/path")]
    [InlineData("│ https://example.com/x │", "https://example.com/x")]
    public void Trims_punctuation_at_the_end(string line, string expected)
    {
        var start = line.IndexOf("http", StringComparison.Ordinal);
        Assert.Equal(expected, UrlAt([line], 0, start + 2));
    }

    [Fact]
    public void Does_not_include_the_trimmed_punctuation_in_the_hit_area()
    {
        var line = "go https://example.com.";
        Assert.Null(UrlAt([line], 0, line.Length - 1));
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:a@b.c")]
    [InlineData("https://")]
    public void Only_http_and_https_with_a_host(string text) => Assert.Null(UrlAt([text], 0, 3));

    [Fact]
    public void Follows_a_link_that_wraps_over_rows()
    {
        string[] rows = ["open https://example.com/a/very/\\", "long/path?x=1 please"];
        var (count, get) = Screen(rows);
        foreach (var (row, column) in new[] { (0, 10), (1, 2) })
        {
            var hit = TerminalLinks.Find(count, get, row, column);
            Assert.Equal("https://example.com/a/very/long/path?x=1", hit?.Url);
            Assert.Equal(2, hit!.Spans.Count);
            Assert.Equal((0, 5), (hit.Spans[0].Row, hit.Spans[0].StartColumn));
            Assert.Equal((1, 0, 12), (hit.Spans[1].Row, hit.Spans[1].StartColumn, hit.Spans[1].EndColumn));
        }
    }

    [Fact]
    public void Rows_that_are_not_soft_wrapped_are_not_joined()
    {
        string[] rows = ["https://example.com/a", "b/c"];
        Assert.Equal("https://example.com/a", UrlAt(rows, 0, 3));
        Assert.Null(UrlAt(rows, 1, 1));
    }

    [Fact]
    public void Skips_the_second_half_of_wide_characters()
    {
        var row = new LinkRow(["中", "", " ", "h", "t", "t", "p", "s", ":", "/", "/", "a", ".", "b"], false, false);
        var hit = TerminalLinks.Find(1, _ => row, 0, 5);
        Assert.Equal("https://a.b", hit?.Url);
    }

    [Fact]
    public void Two_links_on_a_row_are_told_apart()
    {
        string[] rows = ["https://a.example/x https://b.example/y"];
        Assert.Equal("https://a.example/x", UrlAt(rows, 0, 5));
        Assert.Equal("https://b.example/y", UrlAt(rows, 0, 30));
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("HTTP://example.com", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("not a url", false)]
    public void Checks_the_scheme(string url, bool ok) => Assert.Equal(ok, TerminalLinks.IsWebUrl(url));
}
