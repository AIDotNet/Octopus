using Octopus.App.AgentGui;
using Xunit;

namespace Octopus.App.Tests;

public sealed class UserTurnCollapseTests
{
    [Fact]
    public void ShortPromptIsNotCollapsible()
    {
        var item = new UserTurnItem("hello\nworld");
        Assert.False(item.IsCollapsible);
        Assert.Equal("hello\nworld", item.DisplayText);
    }

    [Fact]
    public void LongPromptCollapsesByCharsAndExpands()
    {
        var text = new string('a', UserTurnItem.CollapsedMaxChars + 500);
        var item = new UserTurnItem(text);
        Assert.True(item.IsCollapsible);
        Assert.True(item.DisplayText.Length < text.Length);
        item.ToggleExpandedCommand.Execute(null);
        Assert.Equal(text, item.DisplayText);
        item.ToggleExpandedCommand.Execute(null);
        Assert.True(item.DisplayText.Length < text.Length);
    }

    [Fact]
    public void ManyLinesCollapseAndAppendRecomputes()
    {
        var item = new UserTurnItem("a");
        Assert.False(item.IsCollapsible);
        item.Append(string.Concat(Enumerable.Repeat("\nx", 20)));
        Assert.True(item.IsCollapsible);
        Assert.Equal(UserTurnItem.CollapsedMaxLines, item.DisplayText.Split('\n').Length);
    }
}
