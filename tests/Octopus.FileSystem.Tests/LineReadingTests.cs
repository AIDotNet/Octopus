using System.Text;
using System.Text.Json;

namespace Octopus.FileSystem.Tests;

public sealed class LineReadingTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FileAccessService _fs = new();

    public void Dispose() => _tree.Dispose();

    private string Write(string content, string name = "lines.txt")
    {
        var path = Path.Combine(_tree.Root, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    private static FileErrorCode Code(Action action) => Assert.Throws<FileAccessException>(action).Code;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    // ---------------------------------------------------------------- readLines

    [Fact]
    public void ReadLines_pages_through_a_file_on_line_boundaries()
    {
        var path = Write("one\r\ntwo\nthree\n");
        var first = _fs.ReadLines(path, new LineReadOptions { Limit = 2 });
        Assert.Equal(["one", "two"], first.Lines);
        Assert.Equal(9, first.NextOffset);
        Assert.False(first.Done);

        var second = _fs.ReadLines(path, new LineReadOptions { FromOffset = first.NextOffset });
        Assert.Equal(["three"], second.Lines);
        Assert.Equal(15, second.NextOffset);
        Assert.True(second.Done);
    }

    [Fact]
    public void ReadLines_leaves_an_unterminated_last_line_unless_asked()
    {
        var path = Write("done\nhalf");
        var batch = _fs.ReadLines(path);
        Assert.Equal(["done"], batch.Lines);
        Assert.Equal(5, batch.NextOffset);
        Assert.True(batch.Done);

        File.AppendAllText(path, " written\n");
        var later = _fs.ReadLines(path, new LineReadOptions { FromOffset = batch.NextOffset });
        Assert.Equal(["half written"], later.Lines);

        var all = _fs.ReadLines(Write("a\nb", "b.txt"), new LineReadOptions { IncludeUnterminatedLine = true });
        Assert.Equal(["a", "b"], all.Lines);
        Assert.Equal(3, all.NextOffset);
    }

    [Fact]
    public void ReadLines_skips_and_counts_overlong_lines_even_across_chunks()
    {
        var huge = new string('x', 200_000); // spans several 64 KB reads
        var path = Write($"short\n{huge}\nafter\n{new string('y', 70_000)}\n");
        var batch = _fs.ReadLines(path, new LineReadOptions { MaxLineLength = 100_000 });
        Assert.Equal(["short", "after", new string('y', 70_000)], batch.Lines);
        Assert.Equal(1, batch.Skipped);
        Assert.True(batch.Done);
        Assert.Equal(new FileInfo(path).Length, batch.NextOffset);
    }

    [Fact]
    public void ReadLines_drops_the_byte_order_mark_and_checks_the_offset()
    {
        var path = Path.Combine(_tree.Root, "bom.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\n']);
        Assert.Equal(["a"], _fs.ReadLines(path).Lines);
        Assert.Equal(FileErrorCode.InvalidArgument, Code(() => _fs.ReadLines(path, new LineReadOptions { FromOffset = 100 })));
        Assert.Equal(FileErrorCode.InvalidArgument, Code(() => _fs.ReadLines(path, new LineReadOptions { Limit = 0 })));
    }

    // ---------------------------------------------------------------- tail

    [Fact]
    public void Tail_returns_the_last_lines()
    {
        var path = Write("1\n2\r\n3\n4\n5\n");
        Assert.Equal(["4", "5"], _fs.Tail(path, 2));
        Assert.Equal(["1", "2", "3", "4", "5"], _fs.Tail(path, 50));
        Assert.Equal(["c"], _fs.Tail(Write("a\nb\nc", "unterminated.txt"), 1));
        Assert.Empty(_fs.Tail(Write("", "empty.txt"), 3));
    }

    [Fact]
    public void Tail_reads_backwards_across_chunks_and_stops_at_the_limit()
    {
        var lines = Enumerable.Range(0, 30_000).Select(i => $"line {i:D6}").ToList();
        var path = Write(string.Join('\n', lines) + "\n");
        Assert.Equal(lines[^20_000..], _fs.Tail(path, 20_000));

        // A 100-byte window only holds whole lines from the window, never half of one.
        var small = new FileAccessService(new FileAccessLimits { MaxReadBytes = 100 });
        var tail = small.Tail(path, 1000);
        Assert.InRange(tail.Count, 1, 10);
        Assert.All(tail, line => Assert.Matches(@"^line \d{6}$", line));
        Assert.Equal(lines[^1], tail[^1]);
    }

    // ---------------------------------------------------------------- scanJsonLines

    private const string Session = """
        {"type":"user","timestamp":"t1","message":{"role":"user","content":"a very long text nobody wants"}}
        {"type":"assistant","timestamp":"t2","message":{"model":"opus","usage":{"input_tokens":10,"output_tokens":3}}}
        not json at all
        {"type":"assistant","timestamp":"t3","message":{"model":"sonnet","usage":{"input_tokens":5}}}

        """;

    [Fact]
    public void ScanJsonLines_filters_and_projects()
    {
        var path = Write(Session);
        var batch = _fs.ScanJsonLines(path, new JsonLinesQuery
        {
            Where = new Dictionary<string, JsonElement> { ["type"] = Json("\"assistant\"") },
            Select = ["timestamp", "message.model", "message.usage", "message.missing"],
        });
        Assert.Equal(2, batch.RowCount);
        Assert.Equal(1, batch.Skipped);
        Assert.True(batch.Done);
        Assert.Equal(new FileInfo(path).Length, batch.NextOffset);
        Assert.Equal(
            """[["t2","opus",{"input_tokens":10,"output_tokens":3},null],["t3","sonnet",{"input_tokens":5},null]]""",
            batch.RowsJson);
        var rows = batch.ParseRows();
        Assert.Equal(10, rows[0][2].GetProperty("input_tokens").GetInt32());
        Assert.Equal(JsonValueKind.Null, rows[1][3].ValueKind);
    }

    [Fact]
    public void ScanJsonLines_resumes_from_the_offset_and_waits_for_half_written_lines()
    {
        var path = Write("{\"n\":1}\n{\"n\":2}\n{\"n\":");
        var first = _fs.ScanJsonLines(path, new JsonLinesQuery { Select = ["n"] });
        Assert.Equal("[[1],[2]]", first.RowsJson);
        Assert.Equal(0, first.Skipped); // the half line is not counted…
        Assert.Equal(16, first.NextOffset); // …and not consumed
        Assert.True(first.Done);

        File.AppendAllText(path, "3}");
        // A complete last line is taken even without a line break.
        var second = _fs.ScanJsonLines(path, new JsonLinesQuery { FromOffset = first.NextOffset, Select = ["n"] });
        Assert.Equal("[[3]]", second.RowsJson);
        Assert.Equal(new FileInfo(path).Length, second.NextOffset);

        File.AppendAllText(path, "\n{\"n\":4}\n");
        var third = _fs.ScanJsonLines(path, new JsonLinesQuery { FromOffset = second.NextOffset, Select = ["n"] });
        Assert.Equal("[[4]]", third.RowsJson);
    }

    [Fact]
    public void ScanJsonLines_pages_by_limit_and_by_result_size()
    {
        var path = Write(string.Concat(Enumerable.Range(0, 10).Select(i => $"{{\"i\":{i},\"pad\":\"{new string('p', 100)}\"}}\n")));
        var limited = _fs.ScanJsonLines(path, new JsonLinesQuery { Select = ["i"], Limit = 3 });
        Assert.Equal("[[0],[1],[2]]", limited.RowsJson);
        Assert.False(limited.Done);
        var rest = _fs.ScanJsonLines(path, new JsonLinesQuery { FromOffset = limited.NextOffset, Select = ["i"] });
        Assert.Equal(7, rest.RowCount);
        Assert.Equal(3, rest.ParseRows()[0][0].GetInt32());

        var sized = _fs.ScanJsonLines(path, new JsonLinesQuery { Select = ["pad"], MaxResultBytes = 350 });
        Assert.InRange(sized.RowCount, 1, 3);
        Assert.False(sized.Done);
        Assert.True(sized.RowsJson.Length <= 350);
    }

    [Fact]
    public void ScanJsonLines_where_uses_json_equality()
    {
        var path = Write("""
            {"v":1.0,"flag":true,"o":{"a":[1,2]},"s":"café"}
            {"v":2,"flag":false,"o":{"a":[1]},"s":"cafe"}

            """);
        JsonLinesBatch Scan(string key, string value) => _fs.ScanJsonLines(path, new JsonLinesQuery
        {
            Where = new Dictionary<string, JsonElement> { [key] = Json(value) },
            Select = ["v"],
        });
        Assert.Equal("[[1.0]]", Scan("v", "1").RowsJson);
        Assert.Equal("[[2]]", Scan("flag", "false").RowsJson);
        Assert.Equal("[[1.0]]", Scan("o", """{"a":[1,2]}""").RowsJson);
        Assert.Equal("[[1.0]]", Scan("s", "\"café\"").RowsJson); // escaped in the file, plain in the filter
        Assert.Equal("[]", Scan("missing", "null").RowsJson);
    }

    [Fact]
    public void ScanJsonLines_paths_reach_into_arrays_and_escaped_names()
    {
        var path = Write("""
            {"content":[{"type":"text"},{"type":"tool_use"}],"type":"x","dup":1,"dup":2}

            """);
        var batch = _fs.ScanJsonLines(path, new JsonLinesQuery { Select = ["content.1.type", "type", "dup", "content.5"] });
        Assert.Equal("""[["tool_use","x",2,null]]""", batch.RowsJson);
    }

    [Fact]
    public void ScanJsonLines_skips_overlong_and_trailing_garbage_lines()
    {
        var path = Write($"{{\"a\":1}}\n{{\"a\":\"{new string('x', 5000)}\"}}\n{{\"a\":3}} extra\n{{\"a\":4}}\n");
        var batch = _fs.ScanJsonLines(path, new JsonLinesQuery { Select = ["a"], MaxLineLength = 1000 });
        Assert.Equal("[[1],[4]]", batch.RowsJson);
        Assert.Equal(2, batch.Skipped);
    }

    [Fact]
    public void ScanJsonLines_keeps_non_ascii_text_unescaped()
    {
        var path = Write("{\"s\":\"日本語\"}\n");
        Assert.Equal("[[\"日本語\"]]", _fs.ScanJsonLines(path, new JsonLinesQuery { Select = ["s"] }).RowsJson);
    }

    [Fact]
    public void ScanJsonLines_refuses_an_offset_past_the_end()
    {
        var path = Write("{}\n");
        Assert.Equal(FileErrorCode.InvalidArgument, Code(() => _fs.ScanJsonLines(path, new JsonLinesQuery { FromOffset = 10, Select = ["a"] })));
    }
}
