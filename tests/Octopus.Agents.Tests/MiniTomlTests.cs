using Octopus.Agents.Signals;

namespace Octopus.Agents.Tests;

public class MiniTomlTests
{
    [Fact]
    public void Reads_tables_dotted_keys_and_inline_tables_into_one_flat_map()
    {
        var values = MiniToml.Parse("""
            title = "top"   # comment
            notify = ["a", 'b\c']

            [ui]
            simple_mode = true
            status_line.type = "command"

            [ui.status_line]
            command = "~/.grok/statusline.sh"
            refresh_interval = 300

            [model_providers."my.provider"]
            base_url = "https://x"
            inline = { a = "1", b = { c = "2" } }
            """);

        Assert.Equal("top", values["title"]);
        Assert.Equal(["a", @"b\c"], (List<object?>)values["notify"]!);
        Assert.Equal(new MiniToml.Raw("true"), values["ui.simple_mode"]);
        Assert.Equal("command", values["ui.status_line.type"]);
        Assert.Equal("~/.grok/statusline.sh", values["ui.status_line.command"]);
        Assert.Equal(new MiniToml.Raw("300"), values["ui.status_line.refresh_interval"]);
        Assert.Equal("https://x", values["model_providers.my.provider.base_url"]);
        Assert.Equal("1", values["model_providers.my.provider.inline.a"]);
        Assert.Equal("2", values["model_providers.my.provider.inline.b.c"]);
    }

    [Fact]
    public void Reads_every_string_form_and_escape()
    {
        var values = MiniToml.Parse(""""
            basic = "tab\there \"q\" \u00e9 \U0001F600 back\\slash"
            literal = 'C:\Users\x'
            multi = """
            line one
            line \
                two"""
            multiLiteral = '''
            raw \n text'''
            """");

        Assert.Equal("tab\there \"q\" é 😀 back\\slash", values["basic"]);
        Assert.Equal(@"C:\Users\x", values["literal"]);
        Assert.Equal("line one\nline two", values["multi"]);
        Assert.Equal("raw \\n text", values["multiLiteral"]);
    }

    [Fact]
    public void Arrays_may_span_lines_with_comments_and_trailing_commas()
    {
        var values = MiniToml.Parse("""
            notify = [
              "python3", # interpreter
              "/x/notify.py",
            ]
            after = "still read"
            """);
        Assert.Equal(["python3", "/x/notify.py"], (List<object?>)values["notify"]!);
        Assert.Equal("still read", values["after"]);
    }

    [Fact]
    public void Array_tables_do_not_merge_into_each_other_or_into_top_level_keys()
    {
        var values = MiniToml.Parse("""
            [[hooks.Stop]]
            command = "first"
            [[hooks.Stop]]
            command = "second"
            """);
        Assert.Equal("first", values["hooks.Stop[0].command"]);
        Assert.Equal("second", values["hooks.Stop[1].command"]);
        Assert.False(values.ContainsKey("notify"));
    }

    [Fact]
    public void A_broken_line_is_skipped_and_the_rest_is_still_read()
    {
        var values = MiniToml.Parse("""
            broken = "unterminated
            = no key
            [unclosed
            good = "yes"
            weird = "x" trailing
            notify = ["ok"]
            """);
        Assert.Equal("yes", values["good"]);
        Assert.Equal(["ok"], (List<object?>)values["notify"]!);
        Assert.False(values.ContainsKey("broken"));
    }

    [Fact]
    public void Empty_and_comment_only_text_gives_nothing() =>
        Assert.Empty(MiniToml.Parse("\n# only a comment\r\n\n"));
}
