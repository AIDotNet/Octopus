using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.Agents.Signals;
using Octopus.App.Activity;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.Core.Projects;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

public class SessionTitleTests
{
    [Theory]
    [InlineData("✳ Fix the login test", "Fix the login test", false)]
    [InlineData("⠂ Fix the login test", "Fix the login test", true)]
    [InlineData("⠐ Fix the login test", "Fix the login test", true)]
    [InlineData("◐ Reviewing", "Reviewing", true)]
    [InlineData("✳ Claude Code", "Claude Code", false)]
    [InlineData("token@mac:~/code", "token@mac:~/code", false)]
    [InlineData("* star.txt", "star.txt", false)]
    [InlineData("*.txt in vim", "*.txt in vim", false)]
    [InlineData("⠂", "", true)]
    [InlineData("  ", "", false)]
    [InlineData(null, "", false)]
    [InlineData("pwsh.exe", "", false)]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", "", false)]
    [InlineData("Windows PowerShell", "Windows PowerShell", false)]
    [InlineData("notes for foo.exe", "notes for foo.exe", false)]
    public void The_status_mark_is_split_from_the_name(string? raw, string text, bool working)
    {
        Assert.Equal(new SessionTitle(text, working), SessionTitle.Parse(raw));
    }
}

public class SessionNameTests
{
    [Theory]
    [InlineData("您好", "您好")]
    [InlineData("  fix   the\tlogin test \nsecond line", "fix the login test")]
    [InlineData("\n\n  second  ", "second")]
    [InlineData("/model", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("0123456789012345678901234567890123456789XYZ", "0123456789012345678901234567890123456789…")]
    public void A_prompt_becomes_a_short_title(string? prompt, string title)
    {
        Assert.Equal(title, SessionName.FromPrompt(prompt));
    }

    [Fact]
    public void The_payload_gives_the_prompt_and_the_transcript()
    {
        Assert.Equal(("hi", "/t.jsonl"), SessionName.ReadPayload("""{"prompt":"hi","transcript_path":"/t.jsonl","session_id":"x"}"""));
        Assert.Equal((null, null), SessionName.ReadPayload("not json"));
        Assert.Equal((null, null), SessionName.ReadPayload("""{"prompt":3}"""));
    }

    [Fact]
    public void The_latest_ai_title_of_the_transcript_wins()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path,
            [
                """{"type":"user","message":{"content":"\"ai-title\" in a prompt"}}""",
                """{"type":"ai-title","aiTitle":"First topic","sessionId":"s"}""",
                """{"type":"last-prompt","lastPrompt":"x"}""",
                """{"type":"ai-title","aiTitle":"Second topic","sessionId":"s"}""",
                """{"type":"ai-title","aiTitle":"broken""",
            ]);
            Assert.Equal("Second topic", SessionName.ReadTranscriptTitle(path));
            File.WriteAllText(path, """{"type":"last-prompt","lastPrompt":"您好"}""");
            Assert.Null(SessionName.ReadTranscriptTitle(path));
            Assert.Null(SessionName.ReadTranscriptTitle(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>The session title of a terminal becomes the item's title, and its spinner the item's "working" state.</summary>
public class ItemActivityTests
{
    [AvaloniaFact, FreshApp]
    public void The_title_and_the_spinner_follow_the_terminal()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-activity-").FullName;
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(dir);
            var agent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude Code" };
            projects.AddItem(project, agent);
            Frames.Pump();

            var activity = AppHost.Get<IItemActivityService>();
            var panel = AppHost.Get<ShellViewModel>().Panel;
            var pane = window.GetVisualDescendants().OfType<DockLayout>().Single().Workspace.FindPane(agent.Id)!;
            var restIcon = pane.Icon;

            // Working: the name without the mark, a spinner in the row and a turning ring on the tab.
            activity.ReportTitle(agent.Id, "⠂ Fix the login test");
            Frames.Pump();
            Assert.Equal("Fix the login test", agent.Title);
            Assert.Equal("Fix the login test", pane.Title);
            Assert.True(panel.Items.Single().IsWorking);
            Assert.Equal(ItemActivity.Working, activity.Of(agent.Id));
            Assert.NotSame(restIcon, pane.Icon);

            // The next spinner frame is no news; the rest mark ends the work.
            var changes = 0;
            activity.Changed += (_, _) => changes++;
            activity.ReportTitle(agent.Id, "⠐ Fix the login test");
            Assert.Equal(0, changes);
            activity.ReportTitle(agent.Id, "✳ Fix the login test");
            Frames.Pump();
            Assert.Equal(1, changes);
            Assert.False(panel.Items.Single().IsWorking);
            Assert.Same(restIcon, pane.Icon);

            // A name the user gave stays; the session's name is kept underneath.
            projects.RenameItem(agent.Id, "Reviewer");
            activity.ReportTitle(agent.Id, "⠂ Another task");
            Frames.Pump();
            Assert.Equal("Reviewer", pane.Title);
            Assert.Equal("Another task", agent.Title);

            // Exit ends the work.
            activity.ReportExit(agent.Id);
            Assert.Equal(ItemActivity.Idle, activity.Of(agent.Id));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact, FreshApp]
    public void A_session_the_agent_never_names_is_titled_after_its_prompt_then_its_transcript()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-activity-").FullName;
        var transcript = Path.Combine(dir, "session.jsonl");
        File.WriteAllText(transcript, """{"type":"last-prompt","lastPrompt":"您好"}""" + "\n");
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var agent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude Code" };
        projects.AddItem(project, agent);
        var activity = AppHost.Get<IItemActivityService>();
        var hub = AppHost.Get<ISignalHub>();
        AgentSignal Signal(string channel, string payload) => new("claude", channel, agent.Id, "", 0, 0, payload);

        // The first prompt stands in for the name; Claude repeating its own name does not undo it.
        hub.Publish(Signal("prompt-submit", $$"""{"prompt":"您好","transcript_path":{{System.Text.Json.JsonSerializer.Serialize(transcript)}}}"""));
        Frames.Pump();
        Assert.Equal("您好", agent.Title);
        activity.ReportTitle(agent.Id, "✳ Claude Code");
        Assert.Equal("您好", agent.Title);
        hub.Publish(Signal("prompt-submit", """{"prompt":"second prompt"}"""));
        Frames.Pump();
        Assert.Equal("您好", agent.Title);

        // Once the transcript has a title, the end of a turn picks it up.
        File.AppendAllText(transcript, """{"type":"ai-title","aiTitle":"登录测试修复"}""" + "\n");
        hub.Publish(Signal("stop", $$"""{"transcript_path":{{System.Text.Json.JsonSerializer.Serialize(transcript)}}}"""));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (agent.Title != "登录测试修复" && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Equal("登录测试修复", agent.Title);
        activity.ReportTitle(agent.Id, "✳ Claude Code");
        Assert.Equal("登录测试修复", agent.Title);

        // A real terminal title still wins.
        activity.ReportTitle(agent.Id, "✳ Another topic");
        Assert.Equal("Another topic", agent.Title);
    }
}
