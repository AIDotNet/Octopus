using Octopus.Platform;
using Octopus.Platform.Linux;
using Octopus.Platform.MacOS;
using Octopus.Platform.Windows;
using Xunit;

namespace Octopus.Platform.Tests;

public class WebUrlTests
{
    [Theory]
    [InlineData("https://example.com/a?b=1#c", true)]
    [InlineData("http://localhost:3000", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ssh://host", false)]
    [InlineData("https://", false)]
    [InlineData("example.com", false)]
    [InlineData(null, false)]
    public void Accepts_only_http_and_https(string? url, bool expected) => Assert.Equal(expected, WebUrl.IsHttp(url));

    // Refused addresses return before any process is started, so this is safe to run everywhere.
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    public void Openers_refuse_other_schemes(string url)
    {
        Assert.False(new MacFileOpener().OpenUrl(url));
        Assert.False(new LinuxFileOpener().OpenUrl(url));
        Assert.False(new WindowsFileOpener().OpenUrl(url));
    }
}
