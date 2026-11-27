using System.Diagnostics;

namespace Octopus.FileSystem.Tests;

public class WorkspaceFileIndexTests
{
    [Fact]
    public async Task Deep_source_file_is_found_even_with_many_build_and_ignored_files()
    {
        using var t = new TempTree();
        t.File(".gitignore", "out/\noutput/\nlogs/\nmarkdown_img/\n");

        // Reproduces a real Electron + Python project layout where > 400 decoy files exist before py/.
        for (var i = 0; i < 250; i++) t.File($"dist_electron/chunk_{i:D3}.js");
        for (var i = 0; i < 250; i++) t.File($"out/bundle_{i:D3}.js");
        for (var i = 0; i < 250; i++) t.File($"logs/app_{i:D3}.log");
        for (var i = 0; i < 150; i++) t.File($"docs/doc_{i:D3}.md");
        t.File("node_modules/fast-glob/index.js");
        t.File("py/.pytest_cache/fast_cache.json");
        t.File("py/api/device_server/electric_meter/__pycache__/fast_frequency40.cpython-312.pyc");
        t.File("py/api/device_server/electric_meter/energy_meter.py");
        t.File("py/api/device_server/electric_meter/fast_frequency40.py");

        using var index = new WorkspaceFileIndex();
        await index.WarmAsync(t.Root);

        var matches = index.Search(t.Root, "fast", maxResults: 10);

        Assert.NotEmpty(matches);
        Assert.Equal("py/api/device_server/electric_meter/fast_frequency40.py", matches[0].RelativePath);
        Assert.DoesNotContain(matches, m => m.RelativePath.Contains("node_modules")
            || m.RelativePath.Contains("dist_electron")
            || m.RelativePath.Contains("__pycache__")
            || m.RelativePath.Contains(".pytest_cache"));
    }

    [Fact]
    public void Ranking_orders_exact_stem_prefix_boundary_substring_fuzzy_then_directory()
    {
        var entries = new IndexedFileEntry[]
        {
            new("a/b/c/fast_tools/unrelated.py"),          // directory substring only
            new("src/my_f_a_s_t_thing.py"),                // filename fuzzy
            new("src/breakfast_menu.py"),                  // filename mid-word substring
            new("src/run_fast_mode.py"),                   // filename word-boundary substring
            new("py/api/electric_meter/fast_frequency40.py"), // filename prefix
            new("src/fast.py"),                            // exact filename stem
            new("src/fast"),                               // exact filename
        };

        var matches = FuzzyPathMatcher.Search(entries, "/repo", "fast", maxResults: 10);
        var paths = matches.Select(m => m.RelativePath).ToArray();

        Assert.Equal(
            [
                "src/fast",
                "src/fast.py",
                "py/api/electric_meter/fast_frequency40.py",
                "src/run_fast_mode.py",
                "src/breakfast_menu.py",
                "src/my_f_a_s_t_thing.py",
                "a/b/c/fast_tools/unrelated.py",
            ],
            paths);
    }

    [Fact]
    public void Fuzzy_and_path_segment_queries_match_and_normalize_separators()
    {
        var entries = new IndexedFileEntry[]
        {
            new("py/fast_utils/helper.py"),
            new("py/api/device_server/electric_meter/energy_meter.py"),
            new("py/api/device_server/electric_meter/fast_frequency40.py"),
            new("docs/配置说明文档.md"),
        };

        // Acronym fuzzy match on filename.
        var acronym = FuzzyPathMatcher.Search(entries, "/repo", "ff40", maxResults: 5);
        Assert.Single(acronym);
        Assert.Equal("py/api/device_server/electric_meter/fast_frequency40.py", acronym[0].RelativePath);

        // Path-segment query prefers filename tail match over directory-only match, with both '/' and '\'.
        var slashQuery = FuzzyPathMatcher.Search(entries, "/repo", "py/fast", maxResults: 5);
        var backslashQuery = FuzzyPathMatcher.Search(entries, "/repo", @"py\fast", maxResults: 5);
        Assert.Equal("py/api/device_server/electric_meter/fast_frequency40.py", slashQuery[0].RelativePath);
        Assert.Equal(slashQuery.Select(m => m.RelativePath), backslashQuery.Select(m => m.RelativePath));

        // Cross-directory + filename fuzzy query.
        var crossSeg = FuzzyPathMatcher.Search(entries, "/repo", "em/ff40", maxResults: 5);
        Assert.Single(crossSeg);
        Assert.Equal("py/api/device_server/electric_meter/fast_frequency40.py", crossSeg[0].RelativePath);

        // Non-ASCII / CJK query works cleanly through bitmask prefilter.
        var cjkSub = FuzzyPathMatcher.Search(entries, "/repo", "配置", maxResults: 5);
        Assert.Single(cjkSub);
        Assert.Equal("docs/配置说明文档.md", cjkSub[0].RelativePath);

        var cjkFuzzy = FuzzyPathMatcher.Search(entries, "/repo", "配档", maxResults: 5);
        Assert.Single(cjkFuzzy);
        Assert.Equal("docs/配置说明文档.md", cjkFuzzy[0].RelativePath);
    }

    [Fact]
    public async Task Gitignored_directories_are_deferred_behind_source_files_and_remain_searchable_within_budget()
    {
        using var t = new TempTree();
        t.File(".gitignore", "logs/\n*.tmp\n!logs/important.log\n");

        for (var i = 0; i < 120; i++) t.File($"logs/run_{i:D3}.log");
        t.File("output/report.json");
        t.File("a/b/c/d/e/target_service.cs");

        // Tight cap: 120 log files would starve target_service.cs if ignored dirs weren't deferred.
        using var index = new WorkspaceFileIndex(maxIndexedFiles: 40, maxDeferredIgnoredFiles: 10);
        await index.WarmAsync(t.Root);

        var sourceMatch = index.Search(t.Root, "target_service", maxResults: 5);
        Assert.Single(sourceMatch);
        Assert.Equal("a/b/c/d/e/target_service.cs", sourceMatch[0].RelativePath);

        // Still indexes a bounded slice of ignored files when budget remains.
        var logMatches = index.Search(t.Root, "run_", maxResults: 20);
        Assert.Equal(10, logMatches.Count);
    }

    [Fact]
    public void GitIgnoreMatcher_handles_comments_globs_directories_and_negations()
    {
        var matcher = GitIgnoreMatcher.Parse(
            """
            # comment
            logs/
            *.pyc
            /root_only.txt
            !keep.pyc
            """);

        Assert.True(matcher.IsIgnored("logs", "logs", isDirectory: true));
        Assert.False(matcher.IsIgnored("logs", "logs", isDirectory: false));
        Assert.True(matcher.IsIgnored("a/b/foo.pyc", "foo.pyc", isDirectory: false));
        Assert.False(matcher.IsIgnored("a/b/keep.pyc", "keep.pyc", isDirectory: false));
        Assert.True(matcher.IsIgnored("root_only.txt", "root_only.txt", isDirectory: false));
        Assert.False(matcher.IsIgnored("sub/root_only.txt", "root_only.txt", isDirectory: false));
    }

    [Fact]
    public async Task Cold_search_triggers_refresh_callback_and_invalidation_picks_up_new_files()
    {
        using var t = new TempTree();
        t.File("src/alpha.cs");

        using var index = new WorkspaceFileIndex();
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial = index.Search(t.Root, "alpha", maxResults: 10, onRefreshed: () => refreshed.TrySetResult());
        Assert.Empty(initial);

        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var afterWarm = index.Search(t.Root, "alpha", maxResults: 10);
        Assert.Single(afterWarm);
        Assert.Equal("src/alpha.cs", afterWarm[0].RelativePath);

        // Create a new file and invalidate -> returns existing snapshot immediately, then refreshes asynchronously.
        t.File("src/fast_new.cs");
        index.Invalidate(t.Root);

        var secondRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = index.Search(t.Root, "fast", maxResults: 10, onRefreshed: () => secondRefresh.TrySetResult());
        await secondRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var afterNew = index.Search(t.Root, "fast", maxResults: 10);
        Assert.Single(afterNew);
        Assert.Equal("src/fast_new.cs", afterNew[0].RelativePath);
    }

    [Fact]
    public void Large_index_fuzzy_search_stays_fast_and_accurate()
    {
        const int total = 20_000;
        var entries = new IndexedFileEntry[total];
        for (var i = 0; i < total - 2; i++)
        {
            entries[i] = new IndexedFileEntry($"pkg{i % 40}/module{i % 200}/component_{i:D5}.ts");
        }
        entries[total - 2] = new IndexedFileEntry("py/api/device_server/electric_meter/fast_frequency40.py");
        entries[total - 1] = new IndexedFileEntry("py/api/device_server/electric_meter/energy_meter.py");

        // Warm JIT once, then measure.
        _ = FuzzyPathMatcher.Search(entries, "/repo", "fast", maxResults: 20);

        var sw = Stopwatch.StartNew();
        var matches = FuzzyPathMatcher.Search(entries, "/repo", "fast", maxResults: 20);
        sw.Stop();

        Assert.NotEmpty(matches);
        Assert.Equal("py/api/device_server/electric_meter/fast_frequency40.py", matches[0].RelativePath);
        Assert.True(sw.ElapsedMilliseconds < 250, $"Expected fast search over {total} files, took {sw.ElapsedMilliseconds} ms.");
    }
}
