namespace Octopus.FileSystem.Tests;

public class FileTreeServiceTests
{
    private readonly FileTreeService _service = new();

    [Fact]
    public void Folders_come_first_and_names_sort_naturally()
    {
        using var t = new TempTree();
        t.File("file10.txt");
        t.File("file2.txt");
        t.File("File1.txt");
        t.File("a.txt");
        t.Dir("zeta");
        t.Dir("Alpha");
        var names = _service.List(t.Root).Select(e => e.Name).ToArray();
        Assert.Equal(["Alpha", "zeta", "a.txt", "File1.txt", "file2.txt", "file10.txt"], names);
    }

    [Fact]
    public void Git_folder_is_skipped_and_dot_files_are_hidden()
    {
        using var t = new TempTree();
        t.Dir(".git");
        t.File(".env");
        t.File("src/x.cs");
        var entries = _service.List(t.Root);
        Assert.DoesNotContain(entries, e => e.Name == ".git");
        Assert.True(entries.Single(e => e.Name == ".env").IsHidden);
        Assert.False(entries.Single(e => e.Name == "src").IsHidden);
        Assert.True(entries.Single(e => e.Name == "src").IsDirectory);
    }

    [Fact]
    public void Missing_directory_gives_an_empty_list()
    {
        Assert.Empty(_service.List(Path.Combine(Path.GetTempPath(), "octopus-nope-" + Guid.NewGuid())));
    }

    [Fact]
    public async Task Watch_reports_the_directory_of_a_new_file()
    {
        using var t = new TempTree();
        var sub = t.Dir("sub");
        t.Dir(".git");
        var reported = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var watch = _service.Watch(t.Root, reported.Add);
        await Task.Delay(300);
        File.WriteAllText(Path.Combine(sub, "new.txt"), "x");
        await WaitFor(() => reported.Any(d => SamePath(d, sub)));
        Assert.Contains(reported, d => SamePath(d, sub));
    }

    [Fact]
    public async Task Watch_ignores_changes_inside_git()
    {
        using var t = new TempTree();
        var git = t.Dir(".git");
        var reported = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var watch = _service.Watch(t.Root, reported.Add);
        await Task.Delay(300);
        File.WriteAllText(Path.Combine(git, "index.lock"), "x");
        await Task.Delay(1000);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task WatchChanges_reports_a_write_to_an_existing_file()
    {
        using var t = new TempTree();
        var file = t.File("a.txt", "one");
        var count = 0;
        using var watch = _service.WatchChanges(t.Root, () => Interlocked.Increment(ref count), TimeSpan.FromMilliseconds(100));
        await Task.Delay(300);
        File.WriteAllText(file, "two");
        await WaitFor(() => Volatile.Read(ref count) > 0);
        Assert.True(count > 0);
    }

    [Fact]
    public async Task WatchChanges_reports_a_burst_of_writes_once()
    {
        using var t = new TempTree();
        var file = t.File("a.txt");
        var count = 0;
        using var watch = _service.WatchChanges(t.Root, () => Interlocked.Increment(ref count), TimeSpan.FromMilliseconds(300));
        await Settle(() => count = 0);
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(file, i.ToString());
            await Task.Delay(20);
        }
        await Task.Delay(1200);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task WatchChanges_notices_git_index_and_head_but_not_other_git_files()
    {
        using var t = new TempTree();
        var git = t.Dir(".git");
        t.Dir(".git/objects/ab");
        var count = 0;
        using var watch = _service.WatchChanges(t.Root, () => Interlocked.Increment(ref count), TimeSpan.FromMilliseconds(100));
        await Settle(() => count = 0);

        File.WriteAllText(Path.Combine(git, "index.lock"), "x");
        File.WriteAllText(Path.Combine(git, "objects", "ab", "cdef"), "x");
        File.WriteAllText(Path.Combine(git, "COMMIT_EDITMSG"), "x");
        await Task.Delay(800);
        Assert.Equal(0, count);

        File.WriteAllText(Path.Combine(git, "index"), "x");
        await WaitFor(() => Volatile.Read(ref count) > 0);
        Assert.Equal(1, count);

        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main");
        await WaitFor(() => Volatile.Read(ref count) > 1);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task WatchChanges_stops_after_dispose()
    {
        using var t = new TempTree();
        var count = 0;
        var watch = _service.WatchChanges(t.Root, () => Interlocked.Increment(ref count), TimeSpan.FromMilliseconds(50));
        await Task.Delay(300);
        watch.Dispose();
        watch.Dispose();
        t.File("late.txt");
        await Task.Delay(500);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task WatchGitDirectory_reports_head_index_and_refs_wherever_the_directory_is()
    {
        using var t = new TempTree();
        var git = t.Dir("elsewhere/gitdir");
        t.Dir("elsewhere/gitdir/refs/heads");
        t.Dir("elsewhere/gitdir/objects/ab");
        var count = 0;
        using var watch = _service.WatchGitDirectory(git, git, () => Interlocked.Increment(ref count), TimeSpan.FromMilliseconds(100));
        await Settle(() => count = 0);

        File.WriteAllText(Path.Combine(git, "objects", "ab", "cdef"), "x");
        File.WriteAllText(Path.Combine(git, "COMMIT_EDITMSG"), "x");
        File.WriteAllText(Path.Combine(git, "index.lock"), "x");
        await Task.Delay(800);
        Assert.Equal(0, count);

        File.WriteAllText(Path.Combine(git, "index"), "x");
        await WaitFor(() => Volatile.Read(ref count) > 0);
        Assert.Equal(1, count);

        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main");
        await WaitFor(() => Volatile.Read(ref count) > 1);
        Assert.Equal(2, count);

        File.WriteAllText(Path.Combine(git, "refs", "heads", "main"), "abc");
        await WaitFor(() => Volatile.Read(ref count) > 2);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task WatchGitDirectory_also_watches_the_common_directory_of_a_worktree()
    {
        using var t = new TempTree();
        var own = t.Dir("wt-git");
        var common = t.Dir("main-git");
        t.Dir("main-git/refs/heads");
        var count = 0;
        using var watch = _service.WatchGitDirectory(own, common, () => Interlocked.Increment(ref count), TimeSpan.FromMilliseconds(100));
        await Settle(() => count = 0);

        File.WriteAllText(Path.Combine(common, "refs", "heads", "feature"), "abc");
        await WaitFor(() => Volatile.Read(ref count) > 0);
        Assert.True(count > 0);
    }

    [Fact]
    public async Task WatchFile_reports_only_its_own_file()
    {
        using var t = new TempTree();
        var file = t.File("a.txt", "one");
        var other = t.File("b.txt", "one");
        var count = 0;
        using var watch = _service.WatchFile(file, () => Interlocked.Increment(ref count), TimeSpan.FromMilliseconds(100));
        await Settle(() => count = 0);

        File.WriteAllText(other, "two");
        await Task.Delay(600);
        Assert.Equal(0, count);

        File.WriteAllText(file, "two");
        await WaitFor(() => Volatile.Read(ref count) > 0);
        Assert.True(count > 0);

        var before = count;
        File.Delete(file);
        await WaitFor(() => Volatile.Read(ref count) > before);
        Assert.True(count > before);
    }

    private static bool SamePath(string a, string b) =>
        Path.TrimEndingDirectorySeparator(a) == Path.TrimEndingDirectorySeparator(b);

    /// <summary>The platform may replay the events of what the test just created; wait them out, then forget them.</summary>
    private static async Task Settle(Action forget)
    {
        await Task.Delay(1000);
        forget();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
    }
}
