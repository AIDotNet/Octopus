namespace Octopus.Git.Tests;

public class PorcelainV2ParserTests
{
    private const string Root = "/repo";
    private const string Oid = "0123456789abcdef0123456789abcdef01234567";

    private static string Z(params string[] records) => string.Join('\0', records) + "\0";

    [Fact]
    public void Branch_headers_are_read()
    {
        var s = PorcelainV2Parser.Parse(Z($"# branch.oid {Oid}", "# branch.head main", "# branch.upstream origin/main", "# branch.ab +3 -2"), Root);
        Assert.Equal("main", s.Branch);
        Assert.Equal("0123456", s.Head);
        Assert.Equal("origin/main", s.Upstream);
        Assert.Equal(3, s.Ahead);
        Assert.Equal(2, s.Behind);
        Assert.Empty(s.Changes);
    }

    [Fact]
    public void Detached_head_has_no_branch()
    {
        var s = PorcelainV2Parser.Parse(Z($"# branch.oid {Oid}", "# branch.head (detached)"), Root);
        Assert.Null(s.Branch);
        Assert.Equal("0123456", s.Head);
        Assert.Null(s.Upstream);
        Assert.Equal(0, s.Ahead);
    }

    [Fact]
    public void Repository_without_commits_has_no_head()
    {
        var s = PorcelainV2Parser.Parse(Z("# branch.oid (initial)", "# branch.head master"), Root);
        Assert.Equal("master", s.Branch);
        Assert.Null(s.Head);
    }

    [Theory]
    [InlineData(".M", false, GitChangeKind.Modified)]
    [InlineData("M.", true, GitChangeKind.Modified)]
    [InlineData("A.", true, GitChangeKind.Added)]
    [InlineData(".D", false, GitChangeKind.Deleted)]
    [InlineData("D.", true, GitChangeKind.Deleted)]
    [InlineData("T.", true, GitChangeKind.Modified)]
    public void Single_side_changes(string xy, bool staged, GitChangeKind kind)
    {
        var s = PorcelainV2Parser.Parse(Z($"1 {xy} N... 100644 100644 100644 aaaa bbbb src/a.cs"), Root);
        var change = Assert.Single(s.Changes);
        Assert.Equal("src/a.cs", change.Path);
        Assert.Equal(kind, change.Kind);
        Assert.Equal(staged, change.IsStaged);
        Assert.Equal(Path.Combine(Root, "src", "a.cs"), change.FullPath);
    }

    [Fact]
    public void Both_sides_give_two_changes()
    {
        var s = PorcelainV2Parser.Parse(Z("1 MM N... 100644 100644 100644 aaaa bbbb a.txt", "1 AM N... 000000 100644 100644 0000 bbbb b.txt"), Root);
        Assert.Equal(4, s.Changes.Count);
        Assert.Equal([true, false, true, false], s.Changes.Select(c => c.IsStaged).ToArray());
        Assert.Equal([GitChangeKind.Modified, GitChangeKind.Modified, GitChangeKind.Added, GitChangeKind.Modified], s.Changes.Select(c => c.Kind).ToArray());
    }

    [Fact]
    public void Rename_takes_the_original_path_from_the_next_field()
    {
        var s = PorcelainV2Parser.Parse(Z("2 R. N... 100644 100644 100644 aaaa bbbb R100 new name.txt", "old name.txt", "? after.txt"), Root);
        Assert.Equal(2, s.Changes.Count);
        var rename = s.Changes[0];
        Assert.Equal(GitChangeKind.Renamed, rename.Kind);
        Assert.True(rename.IsStaged);
        Assert.Equal("new name.txt", rename.Path);
        Assert.Equal("old name.txt", rename.OriginalPath);
        Assert.Equal("after.txt", s.Changes[1].Path);
    }

    [Fact]
    public void Conflicts_are_one_unstaged_change()
    {
        var s = PorcelainV2Parser.Parse(Z("u UU N... 100644 100644 100644 100644 aaaa bbbb cccc conflict file.txt"), Root);
        var change = Assert.Single(s.Changes);
        Assert.Equal(GitChangeKind.Conflicted, change.Kind);
        Assert.False(change.IsStaged);
        Assert.Equal("conflict file.txt", change.Path);
    }

    [Fact]
    public void Untracked_and_ignored_records()
    {
        var s = PorcelainV2Parser.Parse(Z("? 中文 目录/文件 a.md", "! ignored.log"), Root);
        var change = Assert.Single(s.Changes);
        Assert.Equal(GitChangeKind.Untracked, change.Kind);
        Assert.False(change.IsStaged);
        Assert.Equal("中文 目录/文件 a.md", change.Path);
    }

    [Fact]
    public void Empty_output_is_an_empty_status()
    {
        var s = PorcelainV2Parser.Parse("", Root);
        Assert.Empty(s.Changes);
        Assert.Null(s.Branch);
    }

    [Fact]
    public void An_untracked_folder_listed_whole_is_one_directory_change_without_the_slash()
    {
        var s = PorcelainV2Parser.Parse(Z("? bulk/", "? notes.txt"), Root);
        var folder = s.Changes[0];
        Assert.True(folder.IsDirectory);
        Assert.Equal("bulk", folder.Path);
        Assert.False(s.Changes[1].IsDirectory);
    }
}
