using Octopus.Terminal.Avalonia;
using Xunit;

namespace Octopus.Terminal.Tests;

public class PasteGuardTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("ls", 1)]
    [InlineData("ls\n", 1)]
    [InlineData("ls\r\n", 1)]
    [InlineData("a\nb", 2)]
    [InlineData("a\r\nb\r\n", 2)]
    [InlineData("a\rb\rc", 3)]
    [InlineData("a\n\nb", 3)]
    public void Counts_lines(string text, int lines) => Assert.Equal(lines, TerminalPasteGuard.CountLines(text));

    [Theory]
    [InlineData("a\nb", false, true, true)]
    [InlineData("single line", false, true, false)]
    [InlineData("single line\n", false, true, false)]
    [InlineData("a\nb", true, true, false)]   // bracketed paste: safe
    [InlineData("a\nb", false, false, false)] // switched off
    public void Decides_when_to_ask(string text, bool bracketed, bool setting, bool expected) =>
        Assert.Equal(expected, TerminalPasteGuard.NeedsConfirmation(text, bracketed, setting));

    [Fact]
    public void Preview_shows_the_first_lines_and_counts_the_rest()
    {
        var text = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}")) + "\n";
        var preview = TerminalPasteGuard.Preview(text, maxLines: 4);
        Assert.Equal(10, preview.LineCount);
        Assert.Equal(["line 1", "line 2", "line 3", "line 4"], preview.Lines);
        Assert.Equal(6, preview.MoreLines);
    }

    [Fact]
    public void Preview_cuts_long_lines()
    {
        var preview = TerminalPasteGuard.Preview(new string('x', 300) + "\nb", maxColumns: 20);
        Assert.Equal(21, preview.Lines[0].Length);
        Assert.EndsWith("…", preview.Lines[0]);
    }
}
