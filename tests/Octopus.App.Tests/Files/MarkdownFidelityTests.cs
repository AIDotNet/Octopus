using Avalonia.Headless.XUnit;
using Octopus.App.Files.Documents;
using Xunit;

namespace Octopus.App.Tests.Files;

/// <summary>
/// The round trip of the editor (import, no edit, export) over the Markdown people really write. Each case says
/// whether the file comes back as it was ("exact"), comes back with the same content in other clothes ("reformat"), or
/// loses something ("lost" and the kinds named). This table is the answer to "what does opening a file in the editor risk".
/// </summary>
public class MarkdownFidelityTests
{
    // ---- written back exactly ------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData("# Title\n\nSome **bold**, _it_, ~~del~~ and `code` and [link](https://a.b/c).\n\n- one\n- two\n  - nested\n\n1. a\n2. b\n\n> quote\n\n---\n\n```\ncode\n```\n")]
    [InlineData("- [ ] todo\n- [x] done\n")]
    [InlineData("#### h4\n\n###### h6\n")]
    [InlineData("```csharp\nvar x = 1;\n```\n")]
    [InlineData("![alt](./img.png)\n")]
    [InlineData("[a](https://x.y \"title\")\n")]
    [InlineData("<https://example.com> and https://bare.com\n")]
    [InlineData("> [!NOTE]\n> hi\n")]
    [InlineData("> a\n>\n> > b\n")]
    [InlineData("- a\n  - b\n    - c\n- d\n")]
    [InlineData("# 标题\n\n中文**加粗**和`代码`。\n")]
    [InlineData("a &amp; b &lt; c and :smile: 😀 and $x^2$\n")]
    [InlineData("Text[^1].\n\n[^1]: note here\n")]
    [InlineData("See [the site][s] now.\n\n[s]: https://example.com\n")]
    [InlineData("1\\. not a list \\* star\n")]
    [InlineData("no newline at end")]
    [InlineData("")]
    public void These_come_back_exactly(string markdown)
    {
        var fidelity = MarkdownFidelity.Assess(markdown);
        Assert.True(fidelity.IsExact, $"expected exact, got lost=[{string.Join(",", fidelity.Lost)}] reformats={fidelity.Reformats}\n--- exported:\n{fidelity.Exported}");
    }

    // ---- same content, other layout ------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData("- a\n    - b\n        - c\n- d\n")]                 // four-space nesting becomes two
    [InlineData("| a | b | c |\n|:--|:-:|--:|\n| 1 | 2 | 3 |\n")]   // the delimiter row is rewritten, alignment is kept
    [InlineData("| a | b |\n|---|---|\n| 1 | 2 |\n")]
    [InlineData("| a | b |\n|---|---|\n| **x** | `y` |\n")]
    [InlineData("line one\nline two\n")]                             // a wrapped paragraph is joined
    [InlineData("* a\n* b\n")]
    [InlineData("1) a\n2) b\n")]
    [InlineData("__bold__ and *it*\n")]
    [InlineData("***\n")]
    [InlineData("a\n\n\n\nb\n")]                                     // blank runs collapse
    [InlineData("text\n- a\n- b\n")]                                 // a blank line goes before the list
    [InlineData("-\ta\n-\tb\n")]
    public void These_keep_their_content_and_only_change_layout(string markdown)
    {
        var fidelity = MarkdownFidelity.Assess(markdown);
        Assert.Empty(fidelity.Lost);
        Assert.True(fidelity.Reformats);
        Assert.False(fidelity.HasLoss);
    }

    // ---- lost ----------------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData("---\ntitle: Hello\ntags: [a, b]\n---\n\n# Doc\n\ntext\n", MarkdownIssue.FrontMatter)]
    [InlineData("+++\ntitle = \"x\"\n+++\n\n# Doc\n", MarkdownIssue.FrontMatter)]
    [InlineData("# T\n\n<div align=\"center\">\n  <img src=\"a.png\">\n</div>\n\ntext\n", MarkdownIssue.HtmlBlock)]
    [InlineData("line one  \nline two\n", MarkdownIssue.HardLineBreak)]
    [InlineData("line one\\\nline two\n", MarkdownIssue.HardLineBreak)]
    [InlineData("~~~\ncode\n~~~\n", MarkdownIssue.TildeFence)]
    [InlineData("Title\n=====\n\ntext\n", MarkdownIssue.SetextHeading)]
    [InlineData("para\n\n    indented code\n\nafter\n", MarkdownIssue.IndentedCode)]
    [InlineData("3. three\n4. four\n", MarkdownIssue.ListStartNumber)]
    [InlineData("text ![alt](./img.png) more\n", MarkdownIssue.InlineImage)]
    [InlineData("- a\n\n  para in item\n\n- b\n", MarkdownIssue.NestedBlocksInList)]
    [InlineData("- a\n\n  ```\n  code\n  ```\n", MarkdownIssue.NestedBlocksInList)]
    public void These_lose_something_and_say_what(string markdown, MarkdownIssue issue)
    {
        var fidelity = MarkdownFidelity.Assess(markdown);
        Assert.True(fidelity.HasLoss, $"expected a loss\n--- exported:\n{fidelity.Exported}");
        Assert.Contains(issue, fidelity.Lost);
    }

    [AvaloniaFact]
    public void A_front_matter_block_is_not_also_reported_as_an_underlined_heading()
    {
        var fidelity = MarkdownFidelity.Assess("---\ntitle: Hello\n---\n\n# Doc\n");
        Assert.Equal([MarkdownIssue.FrontMatter], fidelity.Lost);
    }

    [AvaloniaFact]
    public void Constructs_the_editor_keeps_as_written_are_not_reported_even_though_its_import_report_lists_them()
    {
        // Analyze() calls a footnote, a reference link, an escape and a fence's language "lossy"; the round trip shows they survive.
        foreach (var markdown in new[] { "Text[^1].\n\n[^1]: n\n", "[a][b]\n\n[b]: https://x\n", "\\* x\n", "```cs\nx\n```\n" })
        {
            Assert.True(MarkdownFidelity.Assess(markdown).IsExact, markdown);
        }
    }

    [Fact]
    public void An_unexplained_difference_counts_as_a_loss()
    {
        // A round trip that eats a word: no rule explains it, so the file must not be edited by default.
        var fidelity = MarkdownFidelity.Assess("hello brave new world\n", s => s.Replace("brave ", ""));
        Assert.Equal([MarkdownIssue.Unrecognized], fidelity.Lost);
    }

    [Fact]
    public void Line_endings_and_the_final_newline_alone_are_not_differences()
    {
        Assert.True(MarkdownFidelity.Assess("a\r\n\r\nb\r\n", s => "a\n\nb").IsExact);
    }

    [AvaloniaFact]
    public void The_description_of_a_loss_names_each_kind()
    {
        var fidelity = MarkdownFidelity.Assess("---\na: b\n---\n\n<div>x</div>\n\nok\n");
        var text = MarkdownViewerModel.DescribeLoss(fidelity);
        Assert.Contains(MarkdownViewerModel.Describe(MarkdownIssue.FrontMatter), text);
        Assert.Contains(MarkdownViewerModel.Describe(MarkdownIssue.HtmlBlock), text);
    }

    [AvaloniaFact]
    public void A_large_document_round_trips_in_reasonable_time()
    {
        var paragraph = "## Section\n\nSome **bold** text with `code` and a [link](https://example.com) in a longer sentence of prose.\n\n- item one\n- item two\n  - nested\n\n```\ncode line\n```\n\n";
        var markdown = string.Concat(Enumerable.Repeat(paragraph, 1200)); // about 370 KB, several thousand blocks
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var fidelity = MarkdownFidelity.Assess(markdown);
        watch.Stop();
        Assert.True(fidelity.IsExact || !fidelity.HasLoss);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }
}
