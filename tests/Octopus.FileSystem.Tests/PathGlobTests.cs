namespace Octopus.FileSystem.Tests;

public class PathGlobTests
{
    [Theory]
    [InlineData("**/*.md", "a.md", true)]
    [InlineData("**/*.md", "docs/deep/a.md", true)]
    [InlineData("**/*.md", "docs/a.txt", false)]
    [InlineData("*.md", "a.md", true)]
    [InlineData("*.md", "docs/a.md", true)]
    [InlineData("/*.md", "docs/a.md", false)]
    [InlineData("docs/*.md", "a/docs/x.md", false)]
    [InlineData("node_modules", "a/b/node_modules", true)]
    [InlineData("docs/**", "docs", true)]
    [InlineData("docs/**", "docs/a/b/c.txt", true)]
    [InlineData("docs/**", "documents/a.txt", false)]
    [InlineData("projects/**/*.jsonl", "projects/x/session.jsonl", true)]
    [InlineData("projects/**/*.jsonl", "projects/session.jsonl", true)]
    [InlineData("projects/**/*.jsonl", "other/x/session.jsonl", false)]
    [InlineData("**/node_modules/**", "node_modules", true)]
    [InlineData("**/node_modules/**", "a/node_modules", true)]
    [InlineData("**/node_modules/**", "a/node_modules/b/c.js", true)]
    [InlineData("**/node_modules/**", "a/node_modulesx/c.js", false)]
    [InlineData("a/?.txt", "a/b.txt", true)]
    [InlineData("a/?.txt", "a/bc.txt", false)]
    [InlineData("a/*", "a/b/c", false)]
    [InlineData("a/*b*c", "a/xxbyyc", true)]
    [InlineData("**", "anything/at/all", true)]
    [InlineData("**/a/**/b", "x/a/y/z/b", true)]
    [InlineData("**/a/**/b", "x/a/y/z/c", false)]
    public void Matches_like_a_path_glob(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, new PathGlob(pattern, ignoreCase: false).IsMatch(path));
    }

    [Fact]
    public void Case_follows_the_option()
    {
        Assert.True(new PathGlob("**/*.MD", ignoreCase: true).IsMatch("Docs/readme.md"));
        Assert.False(new PathGlob("**/*.MD", ignoreCase: false).IsMatch("Docs/readme.md"));
    }

    [Fact]
    public void Backslashes_separate_segments_too()
    {
        Assert.True(new PathGlob("docs/**/*.md", ignoreCase: false).IsMatch(@"docs\a\b.md"));
        Assert.True(new PathGlob(@"docs\*.md", ignoreCase: false).IsMatch("docs/b.md"));
    }

    [Theory]
    [InlineData("src/**/*.cs", "", true)]
    [InlineData("src/**/*.cs", "src", true)]
    [InlineData("src/**/*.cs", "src/deep/er", true)]
    [InlineData("src/**/*.cs", "docs", false)]
    [InlineData("src/*.cs", "src", true)]
    [InlineData("src/*.cs", "src/deep", false)]
    [InlineData("*.md", "docs", true)]
    [InlineData("/*.md", "docs", false)]
    [InlineData("**/*.md", "docs/a/b", true)]
    [InlineData("docs", "docs", true)]   // "docs" is "**/docs": another docs could be inside
    [InlineData("/docs", "docs", false)]
    public void Knows_when_a_directory_cannot_contain_matches(string pattern, string directory, bool expected)
    {
        Assert.Equal(expected, new PathGlob(pattern, ignoreCase: false).CouldMatchInside(directory));
    }
}
