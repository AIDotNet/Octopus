using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.Agents.Signals;
using Octopus.App.Activity;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.App.Sidebar;
using Octopus.Core.Projects;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

public class SubagentListTests
{
    private static string ToolUse(string name, string input) =>
        $$$"""{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"{{{name}}}","input":{{{input}}}}]}}""" + "\n";

    [AvaloniaFact, FreshApp]
    public void Subagents_show_as_cards_under_their_session_and_fold_when_finished()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-subagents-").FullName;
        var transcript = Path.Combine(dir, "session.jsonl");
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var agent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Avalonia模版与Studio" };
        var terminal = new ProjectItem { Kind = ProjectItemKind.Terminal, Title = "zsh" };
        projects.AddItem(project, agent);
        projects.AddItem(project, terminal);
        var panel = AppHost.Get<ShellViewModel>().Panel;
        var hub = AppHost.Get<ISignalHub>();
        var service = (SubagentService)AppHost.Get<ISubagentService>();
        void Send(string channel, object payload) =>
            hub.Publish(new AgentSignal("claude", channel, agent.Id, "", 0, 0, JsonSerializer.Serialize(payload)));
        ItemRowViewModel Row() => panel.Items.Single(r => r.Id == agent.Id);

        // No subagent yet: nothing under the row.
        Send("prompt-submit", new { prompt = "build it" });
        Frames.Pump();
        Assert.Null(Row().Subagents);

        Send("subagent-launch", new { tool_name = "Agent", tool_use_id = "toolu_a", tool_input = new { description = "构建 Studio 预览窗口", subagent_type = "general-purpose" } });
        Send("subagent-start", new { agent_id = "a1", agent_type = "general-purpose", transcript_path = transcript });
        Send("subagent-launch", new { tool_name = "Agent", tool_use_id = "toolu_b", tool_input = new { description = "查找模板生成代码", subagent_type = "Explore" } });
        Send("subagent-start", new { agent_id = "a2", agent_type = "Explore", transcript_path = transcript });
        Frames.Pump();
        var list = Row().Subagents!;
        Assert.True(list.HasAny);
        // Collapsed by default: shown as compact icon pill on the parent row until clicked.
        Assert.False(list.ShowList);
        Assert.Equal("0|2", list.CountText);
        Assert.Equal("0", list.FinishedCountText);
        Assert.Equal("2", list.TotalCountText);
        Assert.Equal(2, list.PillIcons.Count);
        Assert.False(list.HasPillOverflow);
        Assert.Equal(["构建 Studio 预览窗口", "查找模板生成代码"], list.Active.Select(c => c.Title));
        Assert.All(list.Active, c => Assert.True(c.IsRunning));
        Assert.Null(panel.Items.Single(r => r.Id == terminal.Id).Subagents);

        // Clicking the [0|2 icon] pill expands the subagent list below the row.
        list.ToggleExpandedCommand.Execute(null);
        Assert.True(list.ShowList);

        // What it does comes from its own transcript.
        var subagents = Path.Combine(dir, "session", "subagents");
        Directory.CreateDirectory(subagents);
        File.WriteAllText(Path.Combine(subagents, "agent-a1.jsonl"), ToolUse("Edit", """{"file_path":"/w/StudioView.axaml"}"""));
        service.PollNow();
        Frames.Pump();
        Assert.Equal("→ Edit StudioView.axaml", list.Active[0].Detail);

        // A rebuild of the panel keeps the list (and what the user opened).
        panel.Filter = "Avalonia";
        panel.Filter = "";
        Assert.Same(list, Row().Subagents);
        Assert.True(list.ShowList);

        // One finishes: it goes behind the fold; user's expanded state stays intact.
        Send("subagent-stop", new { agent_id = "a2" });
        Frames.Pump();
        Assert.Equal("1|2", list.CountText);
        Assert.Equal("1", list.FinishedCountText);
        Assert.Equal("2", list.TotalCountText);
        Assert.Single(list.Active);
        Assert.Single(list.Finished);
        Assert.True(list.HasFinished);
        Assert.Null(list.VisibleFinished);
        list.ToggleFoldCommand.Execute(null);
        Assert.Same(list.Finished, list.VisibleFinished);

        // All done: count updates to 2|2; clicking the pill collapses and re-expands cleanly.
        Send("subagent-stop", new { agent_id = "a1" });
        Frames.Pump();
        Assert.Equal("2|2", list.CountText);
        list.ToggleExpandedCommand.Execute(null);
        Assert.False(list.ShowList);
        list.ToggleExpandedCommand.Execute(null);
        Assert.True(list.ShowList);

        // The next prompt clears the finished ones and resets expansion state.
        Send("prompt-submit", new { prompt = "next" });
        Frames.Pump();
        Assert.False(list.HasAny);
        Assert.False(list.ShowList);
        Assert.Empty(list.PillIcons);
        Assert.False(list.HasPillOverflow);
    }

    [AvaloniaFact, FreshApp]
    public void Subagent_pill_shows_up_to_four_icons_and_overflow_ellipsis_beyond_four()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-subagents-overflow-").FullName;
        var transcript = Path.Combine(dir, "session.jsonl");
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var agent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Overflow Test" };
        projects.AddItem(project, agent);
        var panel = AppHost.Get<ShellViewModel>().Panel;
        var hub = AppHost.Get<ISignalHub>();
        void Send(string channel, object payload) =>
            hub.Publish(new AgentSignal("claude", channel, agent.Id, "", 0, 0, JsonSerializer.Serialize(payload)));
        SubagentListViewModel List() => panel.Items.Single(r => r.Id == agent.Id).Subagents!;

        Send("prompt-submit", new { prompt = "many" });
        for (var i = 1; i <= 4; i++)
        {
            Send("subagent-start", new { agent_id = $"a{i}", agent_type = "Explore", transcript_path = transcript });
            Frames.Pump();
            Assert.Equal(i, List().PillIcons.Count);
            Assert.False(List().HasPillOverflow);
        }

        // Finish a1 and a2, then add a 5th running subagent: pill caps at 4 icons with overflow, keeping active ones first.
        Send("subagent-stop", new { agent_id = "a1" });
        Send("subagent-stop", new { agent_id = "a2" });
        Send("subagent-start", new { agent_id = "a5", agent_type = "Explore", transcript_path = transcript });
        Frames.Pump();

        var list = List();
        Assert.Equal(4, list.PillIcons.Count);
        Assert.True(list.HasPillOverflow);
        Assert.Equal(["a3", "a4", "a5", "a1"], list.PillIcons.Select(c => c.Run.Id));
        Assert.True(list.PillIcons[0].IsRunning);
        Assert.True(list.PillIcons[3].IsDone);
    }
}
