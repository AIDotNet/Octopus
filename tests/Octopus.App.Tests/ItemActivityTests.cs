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
        Assert.Equal(
            new SessionPayload("hi", "/t.jsonl", "x"),
            SessionName.ReadPayload("""{"prompt":"hi","transcript_path":"/t.jsonl","session_id":"x"}"""));
        Assert.Equal(
            new SessionPayload("反重力的标签没有拿到会话标题", "/a/brain/c1/.system_generated/logs/transcript.jsonl", "c1", "/a/brain/c1"),
            SessionName.ReadPayload("""{"lastUserInput":"反重力的标签没有拿到会话标题","transcriptPath":"/a/brain/c1/.system_generated/logs/transcript.jsonl","conversationId":"c1","artifactDirectoryPath":"/a/brain/c1"}"""));
        Assert.Equal(
            new SessionPayload(null, null, "codex-thread-1"),
            SessionName.ReadPayload("""{"thread-id":"codex-thread-1"}"""));
        Assert.Equal(default, SessionName.ReadPayload("not json"));
        Assert.Equal(default, SessionName.ReadPayload("""{"prompt":3}"""));
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
    public void An_agent_that_asks_shows_a_yellow_dot_on_its_row_and_its_project()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-activity-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var agent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude Code" };
        projects.AddItem(project, agent);
        var row = AppHost.Get<ShellViewModel>().Panel.Items.Single();
        var chip = AppHost.Get<Octopus.App.Sidebar.ProjectRailViewModel>().Chips.Single(c => c.Id == project.Id);
        var hub = AppHost.Get<ISignalHub>();
        AgentSignal Signal(string channel) => new("claude", channel, agent.Id, "", 0, 0, "{}");

        // A permission request or a question: waiting, yellow; the project's square says so too.
        hub.Publish(Signal("permission-request"));
        Frames.Pump();
        Assert.True(row.IsWaiting);
        Assert.False(row.HasError);
        Assert.True(chip.NeedsInput);

        // Answered: the next turn starts, and the dots go.
        hub.Publish(Signal("prompt-submit"));
        Frames.Pump();
        Assert.False(row.IsWaiting);
        Assert.False(chip.NeedsInput);
        Assert.True(chip.IsWorking);

        // A failed turn is red, and is not "waiting".
        hub.Publish(Signal("stop-failure"));
        Frames.Pump();
        Assert.True(row.HasError);
        Assert.False(row.IsWaiting);
        Assert.False(chip.NeedsInput);
    }

    [AvaloniaFact, FreshApp]
    public void A_turn_that_finished_out_of_sight_stays_marked_until_the_tab_is_viewed()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-activity-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var first = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude Code" };
        var second = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude Code" };
        projects.AddItem(project, first);
        projects.AddItem(project, second);
        projects.ActivateItem(second.Id);
        var activity = AppHost.Get<IItemActivityService>();
        var rows = AppHost.Get<ShellViewModel>().Panel.Items;
        var chip = AppHost.Get<Octopus.App.Sidebar.ProjectRailViewModel>().Chips.Single(c => c.Id == project.Id);
        var hub = AppHost.Get<ISignalHub>();
        AgentSignal Signal(ProjectItem item, string channel) => new("claude", channel, item.Id, "", 0, 0, "{}");

        // The tab in front finishes: the user saw it, nothing to mark.
        hub.Publish(Signal(second, "prompt-submit"));
        hub.Publish(Signal(second, "stop"));
        Frames.Pump();
        Assert.Equal(ItemActivity.Idle, activity.Of(second.Id));
        Assert.False(chip.IsDone);

        // Another tab finishes while the user looks at this one: green dot on its row and on the project, until it is viewed.
        hub.Publish(Signal(first, "prompt-submit"));
        Frames.Pump();
        Assert.True(chip.IsWorking);
        hub.Publish(Signal(first, "stop"));
        Frames.Pump();
        Assert.Equal(ItemActivity.Done, activity.Of(first.Id));
        Assert.True(rows.Single(r => r.Id == first.Id).IsDone);
        Assert.False(rows.Single(r => r.Id == first.Id).IsWorking);
        Assert.True(chip.IsDone);
        Assert.False(chip.IsWorking);

        // A repeated end of turn does not wipe the mark; the next turn replaces it; viewing the tab clears it.
        hub.Publish(Signal(first, "stop"));
        Assert.Equal(ItemActivity.Done, activity.Of(first.Id));
        projects.ActivateItem(first.Id);
        Frames.Pump();
        Assert.Equal(ItemActivity.Idle, activity.Of(first.Id));
        Assert.False(rows.Single(r => r.Id == first.Id).IsDone);
        Assert.False(chip.IsDone);
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

    [AvaloniaFact, FreshApp]
    public void An_antigravity_session_is_titled_from_lastUserInput_then_conversation_summaries_and_resets_on_session_switch()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-agy-activity-").FullName;
        var dbPath = Path.Combine(dir, "conversation_summaries.db");
        void UpsertSummary(string id, string title, string preview)
        {
            var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs);
            conn.Open();
            using var create = conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS conversation_summaries (
                    conversation_id text PRIMARY KEY,
                    title text NOT NULL DEFAULT "",
                    preview text NOT NULL DEFAULT ""
                );
                """;
            create.ExecuteNonQuery();
            using var upsert = conn.CreateCommand();
            upsert.CommandText = """
                INSERT INTO conversation_summaries (conversation_id, title, preview)
                VALUES ($id, $title, $preview)
                ON CONFLICT(conversation_id) DO UPDATE SET title = excluded.title, preview = excluded.preview;
                """;
            upsert.Parameters.AddWithValue("$id", id);
            upsert.Parameters.AddWithValue("$title", title);
            upsert.Parameters.AddWithValue("$preview", preview);
            upsert.ExecuteNonQuery();
        }

        string TranscriptOf(string id) => Path.Combine(dir, "brain", id, ".system_generated", "logs", "transcript.jsonl");
        string ArtifactsOf(string id) => Path.Combine(dir, "brain", id);

        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var agent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "agy", Title = "Antigravity CLI" };
        projects.AddItem(project, agent);
        _ = AppHost.Get<IItemActivityService>();
        var hub = AppHost.Get<ISignalHub>();
        AgentSignal Signal(string channel, object payload) =>
            new("agy", channel, agent.Id, "", 0, 0, System.Text.Json.JsonSerializer.Serialize(payload));

        void WaitTitle(string expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (agent.Title != expected && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Equal(expected, agent.Title);
        }

        // SessionStart on a fresh conversation leaves the default name until a prompt or stored title exists.
        hub.Publish(Signal("session-start", new { conversationId = "conv-1", transcriptPath = TranscriptOf("conv-1"), artifactDirectoryPath = ArtifactsOf("conv-1") }));
        Frames.Pump();
        Assert.Equal("Antigravity CLI", agent.Title);

        // First PreInvocation titles the tab immediately from lastUserInput.
        hub.Publish(Signal("invocation", new
        {
            conversationId = "conv-1",
            transcriptPath = TranscriptOf("conv-1"),
            artifactDirectoryPath = ArtifactsOf("conv-1"),
            lastUserInput = "反重力的标签没有拿到会话标题",
        }));
        Frames.Pump();
        Assert.Equal("反重力的标签没有拿到会话标题", agent.Title);

        // Once agy writes the generated title to conversation_summaries.db, Stop picks it up.
        UpsertSummary("conv-1", "Anti-Gravity Label Missing", "反重力的标签没有拿到会话标题");
        hub.Publish(Signal("stop", new
        {
            conversationId = "conv-1",
            transcriptPath = TranscriptOf("conv-1"),
            artifactDirectoryPath = ArtifactsOf("conv-1"),
            lastUserInput = "反重力的标签没有拿到会话标题",
        }));
        WaitTitle("Anti-Gravity Label Missing");

        // Follow-up prompts in the same conversation do not clobber the generated title.
        hub.Publish(Signal("invocation", new
        {
            conversationId = "conv-1",
            transcriptPath = TranscriptOf("conv-1"),
            artifactDirectoryPath = ArtifactsOf("conv-1"),
            lastUserInput = "再检查一下边界情况",
        }));
        Frames.Pump();
        Assert.Equal("Anti-Gravity Label Missing", agent.Title);

        // Switching to another conversation (/resume or /new) resets naming and picks up the new conversation's title.
        UpsertSummary("conv-2", "", "登陆的弹窗需要优化一下");
        hub.Publish(Signal("session-start", new
        {
            conversationId = "conv-2",
            transcriptPath = TranscriptOf("conv-2"),
            artifactDirectoryPath = ArtifactsOf("conv-2"),
        }));
        WaitTitle("登陆的弹窗需要优化一下");

        UpsertSummary("conv-2", "Login Dialog Polish", "登陆的弹窗需要优化一下");
        hub.Publish(Signal("stop", new
        {
            conversationId = "conv-2",
            transcriptPath = TranscriptOf("conv-2"),
            artifactDirectoryPath = ArtifactsOf("conv-2"),
        }));
        WaitTitle("Login Dialog Polish");
    }

    [AvaloniaFact, FreshApp]
    public void A_resumed_session_keeps_its_title_across_follow_up_prompts_and_generic_agent_titles()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-resumed-activity-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var agent = new ProjectItem
        {
            Kind = ProjectItemKind.Agent,
            AgentId = "agy",
            Title = "登陆的弹窗需要优化一下",
            Launch = new Octopus.Core.Agents.AgentLaunchOptions { SessionId = "conv-resumed" },
        };
        projects.AddItem(project, agent);
        var activity = AppHost.Get<IItemActivityService>();
        var hub = AppHost.Get<ISignalHub>();

        // Generic agent title or follow-up prompt in the same resumed session must not overwrite its title.
        activity.ReportTitle(agent.Id, "Antigravity CLI");
        hub.Publish(new AgentSignal("agy", "invocation", agent.Id, "", 0, 0,
            """{"conversationId":"conv-resumed","lastUserInput":"继续"}"""));
        Frames.Pump();
        Assert.Equal("登陆的弹窗需要优化一下", agent.Title);

        // Starting a different session in the same tab replaces the old title and updates the persisted session id.
        hub.Publish(new AgentSignal("agy", "invocation", agent.Id, "", 0, 0,
            """{"conversationId":"conv-new","lastUserInput":"修复标签标题"}"""));
        Frames.Pump();
        Assert.Equal("修复标签标题", agent.Title);
        Assert.Equal("conv-new", agent.Launch?.SessionId);
    }

    [AvaloniaFact, FreshApp]
    public void A_claude_session_stays_working_while_a_dynamic_workflow_is_running()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-workflow-activity-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var active = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Monaco参考设计与代码" };
        var other = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Other" };
        projects.AddItem(project, active);
        projects.AddItem(project, other);
        projects.ActivateItem(other.Id);

        var activity = AppHost.Get<IItemActivityService>();
        var rows = AppHost.Get<ShellViewModel>().Panel.Items;
        var chip = AppHost.Get<Octopus.App.Sidebar.ProjectRailViewModel>().Chips.Single(c => c.Id == project.Id);
        var hub = AppHost.Get<ISignalHub>();
        AgentSignal Signal(string channel, string payload) => new("claude", channel, active.Id, "", 0, 0, payload);

        // Turn starts and terminal title spins while Claude launches a dynamic workflow.
        hub.Publish(Signal("prompt-submit", """{"prompt":"continue workflow"}"""));
        activity.ReportTitle(active.Id, "◐ Monaco参考设计与代码");
        Frames.Pump();
        Assert.Equal(ItemActivity.Working, activity.Of(active.Id));
        Assert.True(rows.Single(r => r.Id == active.Id).IsWorking);
        Assert.True(chip.IsWorking);

        // Main turn pauses waiting for the workflow: Stop carries background_tasks with the running workflow.
        hub.Publish(Signal("stop",
            """{"hook_event_name":"Stop","background_tasks":[{"id":"w12345678","type":"workflow","status":"running","name":"code-editor-remaining-phases","description":"Run remaining phases"}]}"""));
        Frames.Pump();
        Assert.Equal(ItemActivity.Working, activity.Of(active.Id));
        Assert.True(rows.Single(r => r.Id == active.Id).IsWorking);
        Assert.False(rows.Single(r => r.Id == active.Id).IsDone);
        Assert.True(chip.IsWorking);
        Assert.False(chip.IsDone);

        // Once the workflow finishes and the follow-up turn stops with empty background_tasks, the hidden tab marks Done.
        hub.Publish(Signal("stop", """{"hook_event_name":"Stop","background_tasks":[]}"""));
        activity.ReportTitle(active.Id, "✳ Monaco参考设计与代码");
        Frames.Pump();
        Assert.Equal(ItemActivity.Done, activity.Of(active.Id));
        Assert.False(rows.Single(r => r.Id == active.Id).IsWorking);
        Assert.True(rows.Single(r => r.Id == active.Id).IsDone);
        Assert.False(chip.IsWorking);
        Assert.True(chip.IsDone);

        // If the user cancels a running workflow manually (no follow-up Stop), the resting title stops the spinner.
        projects.ActivateItem(active.Id);
        hub.Publish(Signal("prompt-submit", """{"prompt":"run workflow"}"""));
        activity.ReportTitle(active.Id, "◐ Monaco参考设计与代码");
        hub.Publish(Signal("stop",
            """{"hook_event_name":"Stop","background_tasks":[{"id":"w87654321","type":"workflow","status":"running","name":"code-editor-remaining-phases"}]}"""));
        Frames.Pump();
        Assert.Equal(ItemActivity.Working, activity.Of(active.Id));
        activity.ReportTitle(active.Id, "✳ Monaco参考设计与代码");
        Frames.Pump();
        Assert.Equal(ItemActivity.Idle, activity.Of(active.Id));
        Assert.False(rows.Single(r => r.Id == active.Id).IsWorking);

        // Cloud session keeps working while the title still spins, and stops when resting.
        hub.Publish(Signal("prompt-submit", """{"prompt":"cloud workflow"}"""));
        activity.ReportTitle(active.Id, "◐ Monaco参考设计与代码");
        hub.Publish(Signal("stop",
            """{"hook_event_name":"Stop","background_tasks":[{"id":"r12345678","type":"cloud session","status":"running","description":"Cloud dynamic workflow"}]}"""));
        Frames.Pump();
        Assert.Equal(ItemActivity.Working, activity.Of(active.Id));
        activity.ReportTitle(active.Id, "✳ Monaco参考设计与代码");
        hub.Publish(Signal("stop",
            """{"hook_event_name":"Stop","background_tasks":[{"id":"r12345678","type":"cloud session","status":"running","description":"Long running autofix-pr"}]}"""));
        Frames.Pump();
        Assert.Equal(ItemActivity.Idle, activity.Of(active.Id));
    }
}
