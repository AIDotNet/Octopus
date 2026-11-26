using System.Text.Json;
using Avalonia.Headless.XUnit;
using Octopus.Agents;
using Octopus.Agents.Sessions;
using Octopus.Agents.Signals;
using Octopus.App.Composition;
using Octopus.App.Explorer.Sessions;
using Octopus.App.Services;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests.Explorer;

/// <summary>The sessions tab (design/ui/会话管理方案.html, layout A): agents, then one agent's sessions by account.</summary>
public class SessionsPanelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);
    private const string Team = FakeAgentSessionService.Team;
    private const string Pro = FakeAgentSessionService.Pro;

    private static AgentSessionInfo Session(string id, string title, string account, int minutesAgo, string agent = "claude") =>
        new(agent, account, id, title, Now.AddHours(-3), Now.AddMinutes(-minutesAgo), 12, "claude-opus-5-5", "main", $"{account}/{id}.jsonl");

    private sealed class FakeMigrationDialog : ISessionMigrationDialog
    {
        public Func<SessionMigrationFormViewModel, SessionMigrationChoice?> Answer { get; set; } = _ => null;
        public SessionMigrationFormViewModel? Last { get; private set; }

        public Task<SessionMigrationChoice?> AskAsync(SessionMigrationFormViewModel form)
        {
            Last = form;
            return Task.FromResult(Answer(form));
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (SessionsPanelViewModel Panel, FakeAgentSessionService Sessions, FakeMigrationDialog Dialog, Project Project) Create()
    {
        var catalog = AppHost.Get<IAgentCatalog>();
        var sessions = new FakeAgentSessionService();
        var claude = catalog.Find("claude")!;
        sessions.Groups["claude"] = new AgentSessionGroup(claude, true,
        [
            new AgentSessionAccount(Team, "Team", true, [Session("aaa", "SSH 隧道断线重连", Team, 5), Session("bbb", "设置页拆分", Team, 90)]),
            new AgentSessionAccount(Pro, "Pro", false, [Session("ccc", "自动更新签名", Pro, 30)]),
        ]);
        var dialog = new FakeMigrationDialog();
        var projects = AppHost.Get<IProjectService>();
        var folder = Directory.CreateTempSubdirectory("octopus-sessions-").FullName;
        var project = projects.AddProject(folder);
        var panel = new SessionsPanelViewModel(sessions, catalog, AppHost.Get<IAgentScanner>(), AppHost.Get<ISettingsStore>(), projects,
            AppHost.Get<IItemOpener>(), dialog, AppHost.Get<IDialogService>(), AppHost.Get<IAppPaths>(), AppHost.Get<ISignalHub>(), time: new FixedTime());
        panel.SetProject(folder);
        return (panel, sessions, dialog, project);
    }

    [AvaloniaFact, FreshApp]
    public async Task The_first_page_lists_agents_with_sessions_and_folds_the_others()
    {
        var (panel, _, _, _) = Create();
        panel.IsActive = true;
        await panel.LoadAsync();

        Assert.True(panel.IsAgentsPage);
        var claude = Assert.Single(panel.Agents);
        Assert.Equal("claude", claude.AgentId);
        Assert.Equal(["Team", "Pro"], claude.AccountLabels);
        // Installed without sessions: codex (readable, none here) before opencode (not readable yet).
        Assert.Equal(["codex", "opencode"], panel.QuietAgents.Select(a => a.AgentId));
        Assert.False(panel.QuietAgents[0].HasSessions);
        Assert.Equal(3, panel.InstalledCount);
        Assert.Equal(AppHost.Get<IAgentCatalog>().Definitions.Count - 3, panel.NotInstalledCount);
    }

    [AvaloniaFact, FreshApp]
    public async Task An_agent_shows_its_sessions_by_account_and_the_search_narrows_them()
    {
        var (panel, _, _, _) = Create();
        panel.IsActive = true;
        await panel.LoadAsync();
        panel.Agents[0].OpenCommand.Execute(null);

        Assert.True(panel.IsSessionsPage);
        Assert.Equal(["Team", "Pro"], panel.Groups.Select(g => g.Label));
        Assert.Equal(["SSH 隧道断线重连", "设置页拆分"], panel.Groups[0].Rows.Select(r => r.Title));
        Assert.Contains("Opus 5.5", panel.Groups[0].Rows[0].MetaText);

        panel.Filter = "签名";
        Assert.Equal("自动更新签名", Assert.Single(Assert.Single(panel.Groups).Rows).Title);
        panel.Filter = "nothing like it";
        Assert.True(panel.IsListEmpty);

        // A refresh keeps the agent open; Back returns to the agents.
        panel.Filter = "";
        await panel.LoadAsync();
        Assert.True(panel.IsSessionsPage);
        panel.BackCommand.Execute(null);
        Assert.True(panel.IsAgentsPage);
    }

    [AvaloniaFact, FreshApp]
    public async Task Continuing_opens_a_tab_in_the_sessions_account_and_then_switches_to_it()
    {
        var (panel, _, _, project) = Create();
        panel.IsActive = true;
        await panel.LoadAsync();
        panel.Agents[0].OpenCommand.Execute(null);

        var row = panel.Groups[1].Rows[0];
        await row.ContinueCommand.ExecuteAsync(null);
        var item = Assert.Single(project.Items, i => i.Kind == ProjectItemKind.Agent);
        Assert.Equal("claude", item.AgentId);
        Assert.Equal("自动更新签名", item.Title);
        Assert.Equal("ccc", item.Launch!.SessionId);
        Assert.Equal(Pro, item.Launch.Env!["CLAUDE_CONFIG_DIR"]);
        Assert.True(row.IsLive);

        // Again: the open tab is shown, no second one.
        await row.ContinueCommand.ExecuteAsync(null);
        Assert.Single(project.Items, i => i.Kind == ProjectItemKind.Agent);
        Assert.Equal(item.Id, AppHost.Get<IProjectService>().Current!.ActiveItemId);
    }

    [AvaloniaFact, FreshApp]
    public async Task A_tab_whose_agent_reports_the_session_marks_it_live()
    {
        var (panel, _, _, project) = Create();
        var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude Code" };
        AppHost.Get<IProjectService>().AddItem(project, item);
        AppHost.Get<ISignalHub>().Publish(new AgentSignal("claude", "statusline", item.Id, "", 0, 1,
            JsonSerializer.Serialize(new { session_id = "bbb", model = new { id = "opus" } })));
        panel.IsActive = true;
        await panel.LoadAsync();
        panel.Agents[0].OpenCommand.Execute(null);

        var rows = panel.Groups[0].Rows;
        Assert.False(rows[0].IsLive);
        Assert.Equal(item.Id, rows[1].LiveItemId);
        Assert.Equal("conv-9", SessionsPanelViewModel.SessionIdOf("""{"conversation_id":"conv-9"}"""));
        Assert.Equal("conv-10", SessionsPanelViewModel.SessionIdOf("""{"conversationId":"conv-10"}"""));
    }

    [AvaloniaFact, FreshApp]
    public async Task Moving_a_session_offers_the_ways_each_agent_takes_it_and_opens_the_result()
    {
        var (panel, sessions, dialog, project) = Create();
        panel.IsActive = true;
        await panel.LoadAsync();
        panel.Agents[0].OpenCommand.Execute(null);
        var row = panel.Groups[0].Rows[0];

        // Cancelled: nothing happens.
        await row.MigrateCommand.ExecuteAsync(null);
        var form = dialog.Last!;
        // Claude itself only into its other account (natively); codex by handoff; opencode cannot take it.
        var claude = form.Targets.Single(t => t.AgentId == "claude");
        Assert.Equal([Pro], claude.Target.Accounts.Select(a => a.Directory));
        Assert.Equal([SessionMigrationKind.Native], claude.Target.Kinds);
        Assert.False(form.Targets.Single(t => t.AgentId == "opencode").IsAvailable);
        Assert.Same(claude, form.Selected);
        Assert.True(form.IsNative);
        Assert.DoesNotContain(project.Items, i => i.Kind == ProjectItemKind.Agent);

        // A handoff to codex starts codex with the note as its first prompt.
        dialog.Answer = f =>
        {
            f.Targets.Single(t => t.AgentId == "codex").ChooseCommand.Execute(null);
            Assert.False(f.CanNative);
            Assert.True(f.IsHandoff);
            f.RecentOnly = true;
            return f.Choice;
        };
        await row.MigrateCommand.ExecuteAsync(null);
        var codex = Assert.Single(project.Items, i => i.AgentId == "codex");
        Assert.Equal("# Handoff", codex.Launch!.Prompt);
        Assert.Equal(SessionsPanelViewModel.RecentTurns, sessions.LastRequest!.RecentTurns);
        Assert.Equal(project.Path, sessions.LastRequest.ProjectPath);

        // A native copy into the other Claude account resumes the copy there.
        sessions.NextResult = new SessionMigrationResult(SessionMigrationKind.Native, ResumeSessionId: "aaa");
        dialog.Answer = f => f.Choice;
        await row.MigrateCommand.ExecuteAsync(null);
        var copy = Assert.Single(project.Items, i => i.AgentId == "claude");
        Assert.Equal("aaa", copy.Launch!.SessionId);
        Assert.Equal(Pro, copy.Launch.Env!["CLAUDE_CONFIG_DIR"]);
    }
}
