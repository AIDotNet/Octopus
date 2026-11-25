using Octopus.App.AgentGui;
using StreamMark.Avalonia.Extensions;
using Xunit;

namespace Octopus.App.Tests;

public class AgentMarkdownLinksTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "octopus-links"));

    [Fact]
    public void FileUrlWithPercentEncodedNameResolvesToTheDecodedLocalPath()
    {
        var file = Path.Combine(Root, "design", "移动端会话界面 方案.html");
        var url = new Uri(file).AbsoluteUri;
        Assert.Contains("%E7%A7%BB", url);

        Assert.Equal(file, AgentMarkdownLinks.ResolvePath(url, null));
        Assert.Equal(LinkDecision.Allow, AgentMarkdownLinks.Services.Links.Evaluate(url, "移动端会话界面 方案.html"));
    }

    [Fact]
    public void RelativeAndAbsolutePathsResolveAgainstTheWorkingDirectory()
    {
        Assert.Equal(Path.Combine(Root, "src", "a b.cs"), AgentMarkdownLinks.ResolvePath("src/a%20b.cs#L3", Root));
        Assert.Equal(Path.Combine(Root, "x.md"), AgentMarkdownLinks.ResolvePath(Path.Combine(Root, "x.md"), "/elsewhere"));
        Assert.Null(AgentMarkdownLinks.ResolvePath("notes.md", null));
    }

    [Theory]
    [InlineData("https://example.com/a.html")]
    [InlineData("mailto:me@example.com")]
    [InlineData("#heading")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file://server/share/a.txt")]
    public void WebLinksAnchorsAndRemoteFilesAreNotLocalPaths(string url) =>
        Assert.Null(AgentMarkdownLinks.ResolvePath(url, Root));

    [Fact]
    public void PolicyStillBlocksOtherSchemesAndRemoteFileHosts()
    {
        var links = AgentMarkdownLinks.Services.Links;
        Assert.Equal(LinkDecision.Block, links.Evaluate("javascript:alert(1)"));
        Assert.Equal(LinkDecision.Block, links.Evaluate("file://server/share/a.txt"));
        Assert.Equal(LinkDecision.Allow, links.Evaluate("https://example.com"));
    }
}
