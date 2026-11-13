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
        Assert.True(list.ShowList);
        Assert.Equal("0/2", list.CountText);
        Assert.Equal(["构建 Studio 预览窗口", "查找模板生成代码"], list.Active.Select(c => c.Title));
        Assert.All(list.Active, c => Assert.True(c.IsRunning));
        Assert.Null(panel.Items.Single(r => r.Id == terminal.Id).Subagents);

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

        // One finishes: it goes behind the fold; the other still runs, so the list stays open.
        Send("subagent-stop", new { agent_id = "a2" });
        Frames.Pump();
        Assert.Equal("1/2", list.CountText);
        Assert.Single(list.Active);
        Assert.Single(list.Finished);
        Assert.True(list.HasFinished);
        Assert.Null(list.VisibleFinished);
        list.ToggleFoldCommand.Execute(null);
        Assert.Same(list.Finished, list.VisibleFinished);

        // All done: the list closes by itself; the user can open it again.
        Send("subagent-stop", new { agent_id = "a1" });
        Frames.Pump();
        Assert.False(list.ShowList);
        Assert.Equal("2/2", list.CountText);
        list.ToggleExpandedCommand.Execute(null);
        Assert.True(list.ShowList);

        // The next prompt clears the finished ones.
        Send("prompt-submit", new { prompt = "next" });
        Frames.Pump();
        Assert.False(list.HasAny);
    }

    [AvaloniaFact]
    public void Render_subagent_cards()
    {
        var shots = Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(shots);
        foreach (var dark in new[] { false, true })
        {
            ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
            TestApp.Fresh();
            var window = new MainWindow();
            window.Show();
            Frames.Pump();
            try
            {
                var dir = Directory.CreateTempSubdirectory("octopus-subagents-").FullName;
                var transcript = Path.Combine(dir, "session.jsonl");
                var projects = AppHost.Get<IProjectService>();
                var project = projects.AddProject(dir);
                var agent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Avalonia模版与Studio" };
                projects.AddItem(project, agent);
                projects.AddItem(project, new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "tkcode.si 域名迁移与前端部署" });
                var hub = AppHost.Get<ISignalHub>();
                void Send(string channel, object payload) =>
                    hub.Publish(new AgentSignal("claude", channel, agent.Id, "", 0, 0, JsonSerializer.Serialize(payload)));
                Send("prompt-submit", new { prompt = "go" });
                var tasks = new[] { ("Explore", "查找 Avalonia 模板生成相关代码"), ("general-purpose", "构建 Studio 预览窗口"), ("Plan", "设计 6 种组合的生成方案") };
                for (var i = 0; i < tasks.Length; i++)
                {
                    Send("subagent-launch", new { tool_name = "Agent", tool_use_id = $"t{i}", tool_input = new { description = tasks[i].Item2, subagent_type = tasks[i].Item1 } });
                    Send("subagent-start", new { agent_id = $"a{i}", agent_type = tasks[i].Item1, transcript_path = transcript });
                }
                var subagents = Directory.CreateDirectory(Path.Combine(dir, "session", "subagents")).FullName;
                File.WriteAllText(Path.Combine(subagents, "agent-a1.jsonl"),
                    ToolUse("Read", """{"file_path":"a.cs"}""") + ToolUse("Edit", """{"file_path":"/w/StudioView.axaml"}"""));
                File.WriteAllText(Path.Combine(subagents, "agent-a2.jsonl"), ToolUse("Read", """{"file_path":"/w/TemplateEngine.cs"}"""));
                Send("subagent-stop", new { agent_id = "a0" });
                Frames.Pump();
                ((SubagentService)AppHost.Get<ISubagentService>()).PollNow();
                Frames.Pump();
                Frames.Save(window, Path.Combine(shots, $"subagents-{(dark ? "dark" : "light")}.png"));

                AppHost.Get<ShellViewModel>().Panel.Items.First().Subagents!.ToggleFoldCommand.Execute(null);
                Frames.Pump();
                Frames.Save(window, Path.Combine(shots, $"subagents-fold-{(dark ? "dark" : "light")}.png"));
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
