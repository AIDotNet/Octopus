using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.App.Common;
using Xunit;

namespace Octopus.App.Tests;

public class AgentIconTests
{
    [AvaloniaTheory]
    [InlineData("agy")]
    [InlineData("gemini")]
    [InlineData("claude")]
    public void An_agent_with_a_logo_file_shows_the_logo_not_the_letter_tile(string id)
    {
        var icon = new AgentIcon { AgentId = id, AgentName = id };

        Assert.IsType<Viewbox>(icon.Child);
        Assert.Null(icon.Background);
    }

    [AvaloniaFact]
    public void An_agent_without_a_logo_file_falls_back_to_the_letter_tile()
    {
        var icon = new AgentIcon { AgentId = "no-such-agent", AgentName = "Nope" };

        Assert.IsType<TextBlock>(icon.Child);
        Assert.NotNull(icon.Background);
    }
}
