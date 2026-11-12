using Octopus.Core.Updates;
using static Octopus.Updates.Tests.Versions;

namespace Octopus.Updates.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("1.2.1112", 1, 2, 1112, null)]
    [InlineData("v1.2.1112", 1, 2, 1112, null)]
    [InlineData("1.2.1112-preview.3", 1, 2, 1112, 3)]
    [InlineData("0.1.0-preview.0", 0, 1, 0, 0)]
    [InlineData("1.2.1112+4f2a9c0d1e", 1, 2, 1112, null)]
    [InlineData("1.2.1112-preview.3+4f2a9c0d1e.dirty", 1, 2, 1112, 3)]
    [InlineData(" 1.2.3 ", 1, 2, 3, null)]
    public void Parses(string text, int major, int minor, int build, int? preview)
    {
        Assert.True(AppVersion.TryParse(text, out var version));
        Assert.Equal((major, minor, build, preview), (version.Major, version.Minor, version.Build, version.Preview));
        Assert.Equal(preview is not null, version.IsPreview);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("0.0.0-dev")]
    [InlineData("1.2.3-beta.1")]
    [InlineData("1.2.3-preview")]
    [InlineData("1.2.3-preview.x")]
    [InlineData("１.2.3")]
    [InlineData("1.2.99999999999")]
    public void RejectsOtherShapes(string? text) => Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public void OrdersLikeSemVer()
    {
        string[] ordered =
        [
            "1.2.1112-preview.1", "1.2.1112-preview.2", "1.2.1112-preview.10", "1.2.1112",
            "1.2.1113-preview.1", "1.2.1113", "1.3.0", "2.0.0-preview.1", "10.0.0",
        ];
        for (var i = 0; i < ordered.Length - 1; i++)
        {
            Assert.True(V(ordered[i]) < V(ordered[i + 1]), $"{ordered[i]} < {ordered[i + 1]}");
        }
        var shuffled = ordered.Reverse().Select(V).Order().Select(v => v.ToString());
        Assert.Equal(ordered, shuffled);
    }

    [Fact]
    public void EqualityIgnoresTheTagPrefixAndMetadata()
    {
        Assert.Equal(V("1.2.3-preview.4"), V("v1.2.3-preview.4+abc"));
        Assert.Equal(V("1.2.3").GetHashCode(), V("v1.2.3+abc").GetHashCode());
        Assert.Equal("1.2.3-preview.4", V("v1.2.3-preview.4+abc").ToString());
    }

    [Theory]
    [InlineData(null, "1.2.3", UpdateChannel.Stable)]
    [InlineData(null, "1.2.3-preview.1", UpdateChannel.Preview)]
    [InlineData("stable", "1.2.3-preview.1", UpdateChannel.Stable)]
    [InlineData("preview", "1.2.3", UpdateChannel.Preview)]
    [InlineData("nonsense", "1.2.3-preview.1", UpdateChannel.Preview)]
    public void TheChannelFollowsTheRunningVersionUnlessOneIsSaved(string? saved, string current, UpdateChannel expected) =>
        Assert.Equal(expected, UpdateSettingValues.ResolveChannel(saved, V(current)));

    [Theory]
    [InlineData("auto", UpdateMode.Auto)]
    [InlineData("notify", UpdateMode.Notify)]
    [InlineData("off", UpdateMode.Off)]
    [InlineData(null, UpdateMode.Auto)]
    [InlineData("weird", UpdateMode.Auto)]
    public void ModesParse(string? value, UpdateMode expected)
    {
        Assert.Equal(expected, UpdateSettingValues.ParseMode(value));
        Assert.Equal(expected, UpdateSettingValues.ParseMode(UpdateSettingValues.ToValue(expected)));
    }
}
