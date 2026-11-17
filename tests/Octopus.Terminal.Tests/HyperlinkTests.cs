using System.Text;
using Octopus.Terminal.Avalonia;
using TKCode.Terminal.Core;
using Xunit;

namespace Octopus.Terminal.Tests;

/// <summary>OSC 8 hyperlinks written to the real terminal core, as programs print them.</summary>
public class HyperlinkTests
{
    private static string Link(string target, string text) => $"\e]8;;{target}\e\\{text}\e]8;;\e\\";

    private static FrameSnapshot Screen(string output, int columns = 60)
    {
        using var core = TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(columns, 6, 0, 0)));
        core.Write(Encoding.UTF8.GetBytes(output));
        return core.CaptureFrame(force: true)!;
    }

    [Fact]
    public void A_hyperlink_opens_its_target_from_any_of_its_cells()
    {
        var frame = Screen("see " + Link("https://example.com/docs", "the docs") + " now");
        foreach (var column in new[] { 4, 8, 11 })
        {
            var hit = TerminalLinks.Find(frame, 0, column);
            Assert.NotNull(hit);
            Assert.True(hit!.IsHyperlink);
            Assert.False(hit.IsPath);
            Assert.Equal("https://example.com/docs", hit.Url);
            Assert.Equal([new CellSpan(0, 4, 11)], hit.Spans);
        }
        Assert.Null(TerminalLinks.Find(frame, 0, 3));
        Assert.Null(TerminalLinks.Find(frame, 0, 12));
    }

    [Fact]
    public void The_target_wins_over_a_different_address_in_the_text()
    {
        var frame = Screen(Link("https://real.example/", "https://shown.example/"));
        var hit = TerminalLinks.Find(frame, 0, 5);
        Assert.Equal("https://real.example/", hit?.Url);
        Assert.True(hit!.IsHyperlink);
    }

    [Fact]
    public void A_target_that_cannot_be_opened_falls_back_to_the_text()
    {
        var frame = Screen(Link("javascript:alert(1)", "https://shown.example/") + " " + Link("mailto:a@b.c", "write"));
        var hit = TerminalLinks.Find(frame, 0, 5);
        Assert.Equal("https://shown.example/", hit?.Url);
        Assert.False(hit!.IsHyperlink);
        Assert.Null(TerminalLinks.Find(frame, 0, 25));
    }

    [Fact]
    public void A_hyperlink_that_wraps_covers_both_rows()
    {
        var frame = Screen("ab " + Link("https://example.com/", "0123456789abc"), columns: 10);
        foreach (var (row, column) in new[] { (0, 4), (1, 2) })
        {
            var hit = TerminalLinks.Find(frame, row, column);
            Assert.Equal("https://example.com/", hit?.Url);
            Assert.Equal([new CellSpan(0, 3, 9), new CellSpan(1, 0, 5)], hit!.Spans);
        }
    }

    [Fact]
    public void Neighbouring_hyperlinks_are_told_apart()
    {
        var frame = Screen(Link("https://a.example/", "aaa") + Link("https://b.example/", "bbb"));
        Assert.Equal([new CellSpan(0, 0, 2)], TerminalLinks.Find(frame, 0, 1)!.Spans);
        var b = TerminalLinks.Find(frame, 0, 4);
        Assert.Equal("https://b.example/", b?.Url);
        Assert.Equal([new CellSpan(0, 3, 5)], b!.Spans);
    }

    [Fact]
    public void The_second_half_of_a_wide_character_is_part_of_the_link()
    {
        var frame = Screen(Link("https://example.com/", "中文") + " x");
        var hit = TerminalLinks.Find(frame, 0, 3);
        Assert.Equal("https://example.com/", hit?.Url);
        Assert.Equal([new CellSpan(0, 0, 3)], hit!.Spans);
    }

    private static (string Url, string Path) LocalFile() => OperatingSystem.IsWindows()
        ? ("file:///C:/work/my%20notes.txt", @"C:\work\my notes.txt")
        : ("file:///tmp/work/my%20notes.txt", "/tmp/work/my notes.txt");

    [Fact]
    public void A_file_on_this_computer_is_a_path_when_it_exists()
    {
        var (url, path) = LocalFile();
        var frame = Screen(Link(url, "notes"));
        var hit = TerminalLinks.Find(frame, 0, 1, p => p == path);
        Assert.NotNull(hit);
        Assert.True(hit!.IsPath);
        Assert.True(hit.IsHyperlink);
        Assert.Equal(path, hit.Url);

        Assert.Null(TerminalLinks.Find(frame, 0, 1, _ => false));
        // Path links off (an SSH terminal): a file address is not a link.
        Assert.Null(TerminalLinks.Find(frame, 0, 1));
    }

    [Theory]
    [InlineData("file://some-other-host.invalid/etc/hosts", false)]
    [InlineData("file://localhost/etc/hosts", true)]
    [InlineData("/etc/hosts", false)]
    [InlineData("https://example.com/", false)]
    public void Only_file_addresses_on_this_computer_have_a_local_path(string url, bool local) =>
        Assert.Equal(local, TerminalLinks.LocalFilePath(url) is not null);

    [Fact]
    public void A_file_address_names_this_computer_by_its_host_name()
    {
        var url = $"file://{System.Net.Dns.GetHostName()}/{(OperatingSystem.IsWindows() ? "C:/x.txt" : "tmp/x.txt")}";
        Assert.Equal(OperatingSystem.IsWindows() ? @"C:\x.txt" : "/tmp/x.txt", TerminalLinks.LocalFilePath(url));
    }
}
