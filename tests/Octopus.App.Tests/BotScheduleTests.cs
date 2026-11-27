using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.Acp.Protocol;
using Octopus.App.AgentGui;
using Octopus.App.Bots;
using Octopus.App.Composition;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Bots;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

public sealed class BotScheduleTests
{
    // ---- Cron ----

    [Theory]
    [InlineData("3 9 * * *", "2026-11-26 08:00", "2026-11-26 09:03")]
    [InlineData("3 9 * * *", "2026-11-26 09:03", "2026-11-27 09:03")]
    [InlineData("*/15 * * * *", "2026-11-26 10:07", "2026-11-26 10:15")]
    [InlineData("57 8 * * 1-5", "2026-11-27 09:00", "2026-11-30 08:57")] // Fri after 8:57 -> Mon
    [InlineData("0 12 1 * MON", "2026-11-26 13:00", "2026-11-30 12:00")]  // dom OR dow: Monday 30th comes before Dec 1st
    [InlineData("30 14 28 2 *", "2026-11-26 00:00", "2027-02-28 14:30")]
    [InlineData("0 0 * * 7", "2026-11-26 00:00", "2026-11-29 00:00")]     // 7 = Sunday
    [InlineData("0 */4 * * *", "2026-11-26 13:10", "2026-11-26 16:00")]
    public void CronExpression_FindsNextMatch(string cron, string after, string expected)
    {
        Assert.True(CronExpression.TryParse(cron, out var expr));
        Assert.Equal(DateTime.Parse(expected), expr!.Next(DateTime.Parse(after)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("5-1 * * * *")]
    public void CronExpression_RejectsInvalid(string cron)
    {
        Assert.False(CronExpression.TryParse(cron, out _));
    }

    // ---- Scheduler ----

    [AvaloniaFact]
    public async Task RecurringSchedule_FiresIntoItsChat_AndAdvances()
    {
        var (bots, bot, clock, scheduler, sent) = Setup();
        var chat = bots.CreateChat(bot.Id, activate: false)!;

        var s = scheduler.Create(bot.Id, "Remind the owner to tidy up documents.", "3 9 * * *", null, "整理资料", chat.Id);
        Assert.Equal(At(9, 3), Saved(bots, bot.Id, s.Id).NextRunAt);

        await scheduler.TickAsync();
        Assert.Empty(sent);

        clock.Set(At(9, 3, 10));
        await scheduler.TickAsync();
        var (chatId, text) = Assert.Single(sent);
        Assert.Equal(chat.Id, chatId);
        Assert.StartsWith($"[Octopus scheduled run: schedule {s.Id} “整理资料”]", text);
        Assert.Contains("Remind the owner", text);

        var saved = Saved(bots, bot.Id, s.Id);
        Assert.Equal(BotScheduleRunStatus.Sent, saved.LastRunStatus);
        Assert.Equal(1, saved.RunCount);
        Assert.Equal(At(9, 3).AddDays(1), saved.NextRunAt);

        // Ticking again in the same minute does not send twice.
        await scheduler.TickAsync();
        Assert.Single(sent);
    }

    [AvaloniaFact]
    public async Task OneShot_IsRemovedAfterSending_AndFallsBackToHomeChat()
    {
        var (bots, bot, clock, scheduler, sent) = Setup();
        var s = scheduler.Create(bot.Id, "Check the deploy.", null, At(10, 0), chatId: "gone-chat");

        clock.Set(At(10, 0, 30));
        await scheduler.TickAsync();

        Assert.Equal(bot.HomeChatId, Assert.Single(sent).ChatId);
        Assert.DoesNotContain(Bot(bots, bot.Id).Schedules, x => x.Id == s.Id);
    }

    [AvaloniaFact]
    public async Task OwnScheduleDueLongAgo_IsSkipped_ButBuiltInsCatchUp()
    {
        var (bots, bot, clock, scheduler, sent) = Setup();
        var once = scheduler.Create(bot.Id, "Morning reminder.", null, At(9, 0));
        bots.UpdateBotSettings(bot.Id, b => b.TidyUpMemoryEnabled = false);

        // Octopus was closed for a day.
        clock.Set(At(9, 0).AddHours(25));
        await scheduler.TickAsync();

        var missed = Saved(bots, bot.Id, once.Id);
        Assert.Equal(BotScheduleRunStatus.Missed, missed.LastRunStatus);
        Assert.False(missed.Enabled);
        // The look-around was overdue too: it runs once (not once per missed slot) and the next one is in the future.
        var (_, text) = Assert.Single(sent);
        Assert.StartsWith("[Octopus scheduled run: look-around]", text);
        Assert.True(Saved(bots, bot.Id, BotScheduleKinds.LookAround).NextRunAt > clock.GetUtcNow());
    }

    [AvaloniaFact]
    public async Task PausedBot_HoldsRuns_UntilResumed()
    {
        var (bots, bot, clock, scheduler, sent) = Setup();
        scheduler.Create(bot.Id, "Ping.", "*/10 * * * *", null);
        bots.SetBotPaused(bot.Id, true);

        clock.Set(At(8, 30));
        await scheduler.TickAsync();
        Assert.Empty(sent);

        bots.SetBotPaused(bot.Id, false);
        await scheduler.TickAsync();
        Assert.Single(sent);
    }

    [AvaloniaFact]
    public async Task FailedDelivery_IsRecorded()
    {
        var (bots, bot, clock, scheduler, _) = Setup();
        scheduler.DeliverOverride = (_, _, _) => Task.FromResult(false);
        var s = scheduler.Create(bot.Id, "Ping.", "*/10 * * * *", null);

        clock.Set(At(8, 10));
        await scheduler.TickAsync();

        Assert.Equal(BotScheduleRunStatus.Failed, Saved(bots, bot.Id, s.Id).LastRunStatus);
    }

    [AvaloniaFact, FreshApp]
    public async Task ScheduledRun_WakesUnshownBotChat_WithoutDoubleConnectingWhenOpened()
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);
        var bots = AppHost.Get<IBotService>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();

        string? sent = null;
        fakeFactory.ConfigureNext = s =>
        {
            s.PromptHandler = (_, text, _, _) =>
            {
                sent = text;
                return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
            };
        };

        var clock = new ManualTime(At(8, 0));
        using var scheduler = new BotScheduler(bots, () => tabs, clock);
        var bot = bots.CreateBot(new NewBotRequest("小李", "claude", "#2f9e63", null, null));
        scheduler.SyncBuiltIns(bot.Id);
        var s = scheduler.Create(bot.Id, "Check morning inbox.", null, At(8, 5), "早间巡检");

        // Fire while the bot view has never been attached to any window (user has not clicked into the bot).
        clock.Set(At(8, 5, 5));
        await scheduler.TickAsync();

        var backing = bots.GetBackingProject(bots.Bots.First(b => b.Id == bot.Id));
        var item = Assert.Single(backing.Items, i => i.Id == bot.HomeChatId);
        var control = tabs.GetOrCreate(backing, item);
        var vm = Assert.IsType<AgentGuiTabViewModel>(control.DataContext);

        Assert.True(Frames.Until(() => sent is not null, milliseconds: 5000), $"Scheduled prompt was not sent (state={vm.State}, error={vm.ErrorBanner}).");
        Assert.StartsWith($"[Octopus scheduled run: schedule {s.Id} “早间巡检”]", sent);
        Assert.Contains("Check morning inbox.", sent);
        Assert.Single(fakeFactory.Created);
        Assert.True(vm.HasStarted);

        // Clicking into the bot later attaches the view and invokes EnsureStarted(); it must not reconnect.
        var window = new Window { Width = 800, Height = 600, Content = control };
        window.Show();
        try
        {
            Frames.Pump();
            Assert.Single(fakeFactory.Created);
        }
        finally
        {
            window.Content = null;
            window.Close();
            Frames.Pump();
            await tabs.ReleaseAllAsync();
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task ScheduledRun_ReconnectsDisconnectedBotChat_AndDeliversPrompt()
    {
        AppHost.Get<ISettingsStore>().Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);
        var bots = AppHost.Get<IBotService>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();

        var prompts = new List<string>();
        fakeFactory.ConfigureNext = s =>
        {
            s.PromptHandler = (_, text, _, _) =>
            {
                prompts.Add(text);
                return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
            };
        };

        var clock = new ManualTime(At(8, 0));
        using var scheduler = new BotScheduler(bots, () => tabs, clock);
        var bot = bots.CreateBot(new NewBotRequest("小李", "claude", "#2f9e63", null, null));
        scheduler.SyncBuiltIns(bot.Id);
        scheduler.Create(bot.Id, "Ping.", "*/10 * * * *", null);

        // First run establishes the session.
        clock.Set(At(8, 10));
        await scheduler.TickAsync();
        Assert.True(Frames.Until(() => prompts.Count == 1, milliseconds: 5000));
        var firstSession = Assert.Single(fakeFactory.Created);

        // Simulate the background CLI exiting while the bot is idle (State -> Disconnected, _session still held).
        firstSession.Emit(new AcpClosedEvent(new IOException("Exited")));
        Frames.Pump();

        // Next scheduled run must reconnect a fresh session and deliver the prompt.
        clock.Set(At(8, 20));
        await scheduler.TickAsync();
        Assert.True(Frames.Until(() => prompts.Count == 2, milliseconds: 5000));
        Assert.Equal(2, fakeFactory.Created.Count);

        await tabs.ReleaseAllAsync();
    }

    [AvaloniaFact]
    public void BuiltIns_FollowTheBotSettings()
    {
        var (bots, bot, _, _, _) = Setup();
        var look = Saved(bots, bot.Id, BotScheduleKinds.LookAround);
        var memory = Saved(bots, bot.Id, BotScheduleKinds.MemoryMaintenance);
        Assert.Equal(bot.LookAroundEnabled, look.Enabled);
        Assert.Equal(BotScheduler.LookAroundCron(bot.LookAroundIntervalHours), look.Cron);
        Assert.Equal(BotScheduler.MemoryMaintenanceCron, memory.Cron);

        bots.UpdateBotSettings(bot.Id, b =>
        {
            b.LookAroundIntervalHours = 6;
            b.TidyUpMemoryEnabled = false;
        });
        Assert.Equal("0 */6 * * *", Saved(bots, bot.Id, BotScheduleKinds.LookAround).Cron);
        Assert.False(Saved(bots, bot.Id, BotScheduleKinds.MemoryMaintenance).Enabled);
        Assert.Null(Saved(bots, bot.Id, BotScheduleKinds.MemoryMaintenance).NextRunAt);
    }

    [AvaloniaFact]
    public void Create_RejectsBadInput()
    {
        var (_, bot, _, scheduler, _) = Setup();
        Assert.Throws<ArgumentException>(() => scheduler.Create(bot.Id, "x", "not cron", null));
        Assert.Throws<ArgumentException>(() => scheduler.Create(bot.Id, "x", "* * * * *", At(9, 0)));
        Assert.Throws<ArgumentException>(() => scheduler.Create(bot.Id, "x", null, At(7, 0)));
        Assert.Throws<ArgumentException>(() => scheduler.Create(bot.Id, " ", "* * * * *", null));
    }

    // ---- octopus-bot MCP tools ----

    [AvaloniaFact]
    public async Task McpTools_WorkForBotChatsOnly()
    {
        var (bots, bot, _, scheduler, _) = Setup();
        var mcp = new BotScheduleMcpServer(bots, scheduler);

        var denied = await mcp.CallOnUiAsync("some-project-item", "schedule_list", null);
        Assert.True(denied["isError"]?.GetValue<bool>());
        Assert.Null(mcp.BuildAcpServerConfig("some-project-item"));

        var created = await mcp.CallOnUiAsync(bot.HomeChatId!, "schedule_create", new JsonObject
        {
            ["prompt"] = "Remind the owner to tidy up documents.",
            ["cron"] = "3 9 * * *",
            ["title"] = "整理资料",
        });
        Assert.Null(created["isError"]);
        var custom = Assert.Single(Bot(bots, bot.Id).Schedules, s => !s.IsBuiltIn);
        Assert.Equal(bot.HomeChatId, custom.ChatId);
        Assert.Contains(custom.Id, TextOf(created));

        var list = TextOf(await mcp.CallOnUiAsync(bot.HomeChatId!, "schedule_list", null));
        Assert.Contains(custom.Id, list);
        Assert.Contains("look_around [built-in", list);

        var builtIn = await mcp.CallOnUiAsync(bot.HomeChatId!, "schedule_delete", new JsonObject { ["id"] = BotScheduleKinds.LookAround });
        Assert.True(builtIn["isError"]?.GetValue<bool>());

        await mcp.CallOnUiAsync(bot.HomeChatId!, "schedule_delete", new JsonObject { ["id"] = custom.Id });
        Assert.DoesNotContain(Bot(bots, bot.Id).Schedules, s => !s.IsBuiltIn);

        var badTime = await mcp.CallOnUiAsync(bot.HomeChatId!, "schedule_create", new JsonObject { ["prompt"] = "x", ["run_at"] = "tomorrow-ish" });
        Assert.True(badTime["isError"]?.GetValue<bool>());

        var names = BotScheduleMcpServer.BuildToolSchemas().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["schedule_create", "schedule_list", "schedule_delete"], names);
    }

    // ---- Bot home ----

    [AvaloniaFact]
    public void BotHome_PointsAgentsAtOctopusSchedules_AndDeniesSessionCron()
    {
        var bots = AppHost.Get<IBotService>();
        var bot = bots.CreateBot(new NewBotRequest("小李", "claude", "#2f9e63", null, null));

        Assert.Contains("schedule_create", File.ReadAllText(bots.Home.GetAgentsPath(bot)));

        var settingsPath = bots.Home.GetClaudeSettingsPath(bot);
        File.WriteAllText(settingsPath, """{ "model": "haiku", "permissions": { "deny": ["WebFetch"] } }""");
        bots.Home.SyncManagedFiles(bot);

        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!;
        Assert.Equal("haiku", settings["model"]!.GetValue<string>());
        var deny = settings["permissions"]!["deny"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["WebFetch", "CronCreate", "CronDelete", "CronList"], deny);

        // A file that is not JSON is the owner's business: left untouched.
        File.WriteAllText(settingsPath, "{ not json");
        bots.Home.SyncManagedFiles(bot);
        Assert.Equal("{ not json", File.ReadAllText(settingsPath));
    }

    [Fact]
    public void Formatter_DescribesCommonShapes()
    {
        Assert.True(CronExpression.TryParse("3 9 * * *", out var daily));
        Assert.Contains("09:03", BotScheduleFormatter.DescribeCron(daily));
        Assert.True(CronExpression.TryParse("57 8 * * 1-5", out var weekdays));
        Assert.Contains("08:57", BotScheduleFormatter.DescribeCron(weekdays));
        Assert.True(CronExpression.TryParse("1-5/2 9 * * *", out var odd));
        Assert.Null(BotScheduleFormatter.DescribeCron(odd));
    }

    // ---- helpers ----

    private static DateTimeOffset At(int hour, int minute, int second = 0)
    {
        var local = new DateTime(2026, 11, 26, hour, minute, second);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static (IBotService Bots, BotDefinition Bot, ManualTime Clock, BotScheduler Scheduler, List<(string ChatId, string Text)> Sent) Setup()
    {
        var bots = AppHost.Get<IBotService>();
        var clock = new ManualTime(At(8, 0));
        var scheduler = new BotScheduler(bots, () => null, clock);
        var sent = new List<(string, string)>();
        scheduler.DeliverOverride = (_, chatId, text) =>
        {
            sent.Add((chatId, text));
            return Task.FromResult(true);
        };
        var bot = bots.CreateBot(new NewBotRequest("小李", "claude", "#2f9e63", null, null));
        // No Start(): its timer would outlive the test's container. Ticks are driven by hand.
        scheduler.SyncBuiltIns(bot.Id);
        return (bots, Bot(bots, bot.Id), clock, scheduler, sent);
    }

    private static BotDefinition Bot(IBotService bots, string id) => bots.Bots.First(b => b.Id == id);

    private static BotSchedule Saved(IBotService bots, string botId, string scheduleId) =>
        Bot(bots, botId).Schedules.First(s => s.Id == scheduleId);

    private static string TextOf(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
        public void Set(DateTimeOffset at) => _now = at;
    }
}
