namespace Octopus.FileSystem.Tests;

public class FolderBrowserTests
{
    [Fact]
    public async Task Local_browser_lists_only_directories_sorted_naturally_and_marks_existing_projects()
    {
        using var t = new TempTree();
        t.File("README.md");
        t.Dir("item10");
        var item2 = t.Dir("item2");
        t.Dir("Alpha");
        t.Dir(".hidden");

        var browser = new LocalFolderBrowser("Local", () => [new FolderBrowseProject("Item 2", item2)]);
        var page = await browser.BrowseAsync(t.Root, cancellationToken: TestContext.Current.CancellationToken);

        var expected = new[] { "Alpha", "item2", "item10", ".hidden" }.OrderBy(x => x, NaturalStringComparer.Instance).ToArray();
        Assert.Equal(expected, page.Entries.Select(e => e.Name).ToArray());
        Assert.DoesNotContain(page.Entries, e => e.Name == "README.md");
        Assert.True(page.Entries.Single(e => e.Name == ".hidden").IsHidden);
        Assert.True(page.Entries.Single(e => e.Name == "item2").IsExistingProject);
        Assert.False(page.Entries.Single(e => e.Name == "Alpha").IsExistingProject);
        Assert.NotNull(page.ParentPath);
        Assert.NotEmpty(page.Roots);
    }

    [Fact]
    public async Task Local_browser_falls_back_to_nearest_existing_ancestor_when_requested()
    {
        using var t = new TempTree();
        var existing = t.Dir("code");
        var missing = Path.Combine(existing, "not-yet", "deeper");

        var browser = new LocalFolderBrowser("Local");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => browser.BrowseAsync(missing, fallbackToParent: false, cancellationToken: TestContext.Current.CancellationToken));

        var page = await browser.BrowseAsync(missing, fallbackToParent: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(existing)), page.CurrentPath);
    }

    [Fact]
    public async Task Local_browser_creates_folders_and_rejects_invalid_or_duplicate_names()
    {
        using var t = new TempTree();
        var browser = new LocalFolderBrowser("Local");

        var created = await browser.CreateFolderAsync(t.Root, "my-project", TestContext.Current.CancellationToken);
        Assert.True(Directory.Exists(created));
        Assert.Equal("my-project", Path.GetFileName(created));

        await Assert.ThrowsAsync<IOException>(() => browser.CreateFolderAsync(t.Root, "my-project", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => browser.CreateFolderAsync(t.Root, "../escape", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => browser.CreateFolderAsync(t.Root, "a/b", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => browser.CreateFolderAsync(t.Root, "   ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Breadcrumb_splits_unix_windows_and_unc_paths()
    {
        var unix = FolderBrowserPaths.SplitBreadcrumb("/Users/token/code", "/");
        Assert.Equal([("/", "/"), ("Users", "/Users"), ("token", "/Users/token"), ("code", "/Users/token/code")],
            unix.Select(s => (s.Label, s.FullPath)).ToArray());

        var win = FolderBrowserPaths.SplitBreadcrumb(@"C:\Users\token\code", @"\");
        Assert.Equal([("C:", @"C:\"), ("Users", @"C:\Users"), ("token", @"C:\Users\token"), ("code", @"C:\Users\token\code")],
            win.Select(s => (s.Label, s.FullPath)).ToArray());

        var winRoot = FolderBrowserPaths.SplitBreadcrumb(@"D:\", @"\");
        Assert.Equal([("D:", @"D:\")], winRoot.Select(s => (s.Label, s.FullPath)).ToArray());

        var unc = FolderBrowserPaths.SplitBreadcrumb(@"\\nas\share\projects", @"\");
        Assert.Equal([(@"\\nas\share", @"\\nas\share"), ("projects", @"\\nas\share\projects")],
            unc.Select(s => (s.Label, s.FullPath)).ToArray());

        Assert.Empty(FolderBrowserPaths.SplitBreadcrumb("", @"\"));
    }
}
