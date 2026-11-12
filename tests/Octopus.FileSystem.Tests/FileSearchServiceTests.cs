namespace Octopus.FileSystem.Tests;

public class FileSearchServiceTests
{
    private readonly FileSearchService _service = new();

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source) list.Add(item);
        return list;
    }

    [Fact]
    public async Task Names_rank_exact_then_prefix_then_rest()
    {
        using var t = new TempTree();
        t.File("deep/er/xfoo.txt");
        t.File("foobar.txt");
        t.File("foo");
        t.File("a/barfoo");
        var names = (await ToListAsync(_service.SearchNamesAsync(t.Root, "FOO"))).Select(e => e.Name).ToArray();
        Assert.Equal("foo", names[0]);
        Assert.Equal("foobar.txt", names[1]);
        Assert.Equal(["barfoo", "xfoo.txt"], names[2..]);
    }

    [Fact]
    public async Task Names_are_capped_at_500_and_skip_ignored_folders()
    {
        using var t = new TempTree();
        for (var i = 0; i < 600; i++) t.File($"m/item{i}.txt");
        t.File("node_modules/item.js");
        t.File(".git/item");
        t.File("bin/item.dll");
        var results = await ToListAsync(_service.SearchNamesAsync(t.Root, "item"));
        Assert.Equal(500, results.Count);
        Assert.DoesNotContain(results, e => e.FullPath.Contains("node_modules") || e.FullPath.Contains(".git") || e.FullPath.Contains("bin"));
    }

    [Fact]
    public async Task Content_search_finds_lines_case_insensitively()
    {
        using var t = new TempTree();
        t.File("a.txt", "one\nHello World\nthree hello");
        var matches = await ToListAsync(_service.SearchContentAsync(t.Root, "hello"));
        Assert.Equal([2, 3], matches.Select(m => m.Line).ToArray());
        Assert.Equal(0, matches[0].Column);
        Assert.Equal(6, matches[1].Column);
        Assert.Equal("Hello World", matches[0].LineText);
    }

    [Fact]
    public async Task Content_search_skips_binary_large_and_ignored()
    {
        using var t = new TempTree();
        File.WriteAllBytes(Path.Combine(t.Root, "bin.dat"), [0x68, 0x69, 0, 0x68, 0x69]);
        t.File("big.txt", new string('x', 1024 * 1024 + 10) + "needle");
        t.File("node_modules/n.js", "needle");
        t.File("obj/o.txt", "needle");
        t.File("ok.txt", "needle here");
        var matches = await ToListAsync(_service.SearchContentAsync(t.Root, "needle"));
        Assert.Single(matches);
        Assert.EndsWith("ok.txt", matches[0].FilePath);
    }

    [Fact]
    public async Task Content_search_limits_per_file_total_and_line_length()
    {
        using var t = new TempTree();
        t.File("many.txt", string.Join('\n', Enumerable.Repeat("hit", 100)));
        t.File("long.txt", "hit" + new string('y', 500));
        var matches = await ToListAsync(_service.SearchContentAsync(t.Root, "hit"));
        Assert.Equal(20, matches.Count(m => m.FilePath.EndsWith("many.txt")));
        Assert.Equal(300, matches.Single(m => m.FilePath.EndsWith("long.txt")).LineText.Length);

        for (var i = 0; i < 80; i++) t.File($"f/{i}.txt", string.Join('\n', Enumerable.Repeat("hit", 20)));
        matches = await ToListAsync(_service.SearchContentAsync(t.Root, "hit"));
        Assert.Equal(1000, matches.Count);
    }

    [Fact]
    public async Task Search_can_be_cancelled()
    {
        using var t = new TempTree();
        for (var i = 0; i < 50; i++) t.File($"d{i}/f.txt", "needle");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in _service.SearchContentAsync(t.Root, "needle", cts.Token)) { }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in _service.SearchNamesAsync(t.Root, "f", cts.Token)) { }
        });
    }
}
