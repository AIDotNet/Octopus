using System.Text;
using TKCode.Terminal.Core;
using Xunit;

namespace Octopus.Terminal.Tests;

/// <summary>The search of the real terminal core: order, wrapping, scrollback, scrolling to the match.</summary>
public class SearchTests
{
    private static TerminalCore Filled()
    {
        var core = TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(40, 6, 0, 0)) { ScrollbackMaxLines = 1000 });
        var sb = new StringBuilder();
        for (var i = 1; i <= 30; i++) sb.Append(i % 10 == 0 ? $"row {i} needle\r\n" : $"row {i}\r\n");
        core.Write(Encoding.UTF8.GetBytes(sb.ToString()));
        return core;
    }

    private static (long Total, long? Index, FrameSnapshot Frame) Look(TerminalCore core)
    {
        core.ActiveSearch!.Update();
        var frame = core.CaptureFrame(force: true)!;
        return (frame.Search!.TotalMatches, frame.Search.SelectedIndex, frame);
    }

    private static string RowText(FrameSnapshot frame, int row) =>
        string.Concat(Enumerable.Range(0, frame.Columns).Select(c => frame.Rows[row].TextAt(c) ?? " ")).TrimEnd();

    [Fact]
    public void Finds_matches_in_the_scrollback_and_the_screen()
    {
        using var core = Filled();
        var search = core.CreateSearch();
        search.Needle = "needle";
        var (total, _, _) = Look(core);
        Assert.Equal(3, total);
    }

    [Fact]
    public void Walks_through_the_matches_and_scrolls_to_them()
    {
        using var core = Filled();
        var search = core.CreateSearch();
        search.Needle = "needle";
        search.Update();
        if (search.SelectedIndex is null) search.Next();
        var seen = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var (_, index, frame) = Look(core);
            var selected = frame.Search!.Selected;
            Assert.Single(selected);
            seen.Add($"{index}:{RowText(frame, selected[0].Row)}");
            search.Previous();
        }
        // Print order for the keymap decision below.
        Assert.Equal(["0:row 30 needle", "2:row 10 needle", "1:row 20 needle", "0:row 30 needle"], seen);
    }
}
