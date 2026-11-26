using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Octopus.App.Activity;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.App.StarPrompt;
using Octopus.App.Updates;
using Octopus.Core.Projects;
using Octopus.Core.Settings;
using Octopus.Platform;
using Octopus.Storage;
using Octopus.Updates;
using Xunit;

namespace Octopus.App.Tests;

public class StarPromptTests
{
    private sealed class FakeClock(DateTimeOffset startUtc)
    {
        public DateTimeOffset Now { get; set; } = startUtc;
        public DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);
    }

    private sealed class RecordingFileOpener : IFileOpener
    {
        public List<string> OpenedUrls { get; } = [];
        public void Open(string path) { }
        public void Reveal(string path) { }
        public bool OpenUrl(string url) { OpenedUrls.Add(url); return true; }
    }

    private static (StarPromptViewModel Vm, FakeClock Clock, FakeUpdateService Updates, RecordingFileOpener Opener) BuildHost(
        DateTimeOffset? startUtc = null,
        Action<StarPromptSettings>? seedSettings = null,
        TimeSpan? completionDelay = null,
        TimeSpan? thanksDuration = null)
    {
        var clock = new FakeClock(startUtc ?? new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        var fakeUpdates = new FakeUpdateService();
        var opener = new RecordingFileOpener();
        var options = new StarPromptOptions
        {
            AutoStart = false,
            CompletionPauseDelay = completionDelay ?? TimeSpan.Zero,
            ThanksDisplayDuration = thanksDuration ?? TimeSpan.FromMinutes(1),
            UtcNow = () => clock.Now,
            LocalToday = () => clock.Today,
        };

        var original = AppHost.ConfigureOverrides;
        AppHost.ConfigureOverrides = services =>
        {
            original?.Invoke(services);
            services.Replace(ServiceDescriptor.Singleton<IUpdateService>(fakeUpdates));
            services.Replace(ServiceDescriptor.Singleton<IFileOpener>(opener));
            services.Replace(ServiceDescriptor.Singleton(options));
        };
        AppHost.Reset();
        AppHost.Get<Octopus.App.Appearance.ThemeService>().Track();
        AppHost.ConfigureOverrides = original;

        if (seedSettings is not null)
        {
            AppHost.Get<ISettingsStore>().Update(s => seedSettings(s.StarPrompt));
        }

        return (AppHost.Get<StarPromptViewModel>(), clock, fakeUpdates, opener);
    }

    private static (Project Project, ProjectItem Agent) AddProjectWithAgent()
    {
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject("/tmp/octopus-star-test", "Octopus");
        var agent = new ProjectItem
        {
            Id = "agent-1",
            Kind = ProjectItemKind.Agent,
            AgentId = "claude",
            Title = "Claude Code",
        };
        projects.AddItem(project, agent);
        return (project, agent);
    }

    [AvaloniaFact]
    public void NewUsersAreNotPromptedBeforeThresholds()
    {
        var (vm, _, _, _) = BuildHost();
        vm.Start();
        AddProjectWithAgent();

        Assert.False(vm.CanShowNow());
        Assert.False(vm.TryTriggerNow());
        Assert.False(vm.IsOpen);

        var saved = AppHost.Get<ISettingsStore>().Current.StarPrompt;
        Assert.NotNull(saved.FirstSeenUtc);
        Assert.Equal(1, saved.ActiveDaysCount);
    }

    [AvaloniaFact]
    public void ShowsAfterEligibleAgentTurnCompletesWithoutError()
    {
        var now = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var (vm, _, _, _) = BuildHost(now, s =>
        {
            s.FirstSeenUtc = now.AddDays(-4);
            s.ActiveDaysCount = 3;
            s.LastActiveDate = "2026-11-25";
            s.CompletedAgentTurns = 4; // One more completion crosses the threshold of 5.
        });
        vm.Start();
        var (_, agent) = AddProjectWithAgent();
        var activity = AppHost.Get<IItemActivityService>();

        activity.ReportStatus(agent.Id, ItemActivity.Working);
        Assert.False(vm.IsOpen);

        activity.ReportStatus(agent.Id, ItemActivity.Idle);
        Assert.True(vm.IsOpen);
        Assert.True(vm.IsPromptVisible);
        Assert.False(vm.IsThanksVisible);
        Assert.False(vm.IsCloseMenuOpen);
        Assert.Equal(5, AppHost.Get<ISettingsStore>().Current.StarPrompt.CompletedAgentTurns);
    }

    [AvaloniaFact]
    public void DoesNotShowDuringErrorWaitingSettingsOrNarrowWindow()
    {
        var now = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var (vm, _, _, _) = BuildHost(now, s =>
        {
            s.FirstSeenUtc = now.AddDays(-5);
            s.ActiveDaysCount = 4;
            s.LastActiveDate = "2026-11-25";
            s.CompletedAgentTurns = 10;
        });
        vm.Start();
        var (_, agent) = AddProjectWithAgent();
        var activity = AppHost.Get<IItemActivityService>();

        activity.ReportStatus(agent.Id, ItemActivity.Error);
        Assert.False(vm.CanShowNow());

        activity.ReportStatus(agent.Id, ItemActivity.Waiting);
        Assert.False(vm.CanShowNow());

        activity.ReportStatus(agent.Id, ItemActivity.Idle);
        Assert.True(vm.CanShowNow());

        vm.SetSettingsOpen(true);
        Assert.False(vm.CanShowNow());
        vm.SetSettingsOpen(false);
        Assert.True(vm.CanShowNow());

        vm.SetWindowState(isActive: true, width: 640);
        Assert.False(vm.CanShowNow());
        vm.SetWindowState(isActive: false, width: 1200);
        Assert.False(vm.CanShowNow());

        vm.SetWindowState(isActive: true, width: 1200);
        Assert.True(vm.TryTriggerNow());
        Assert.True(vm.IsOpen);
    }

    [AvaloniaFact]
    public void YieldsToUpdateCardWithoutBurningPromptCount()
    {
        var now = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var (vm, _, updates, _) = BuildHost(now, s =>
        {
            s.FirstSeenUtc = now.AddDays(-5);
            s.ActiveDaysCount = 4;
            s.LastActiveDate = "2026-11-25";
            s.CompletedAgentTurns = 10;
        });
        vm.Start();
        AddProjectWithAgent();

        Assert.True(vm.TryTriggerNow());
        Assert.True(vm.IsOpen);

        // Update card appears -> star pill yields silently without incrementing PromptCount.
        updates.Set(s => s with { Status = UpdateStatus.Available, Offer = FakeUpdateService.Offer("1.3.1201") });
        Assert.True(AppHost.Get<UpdateCardViewModel>().IsOpen);
        Assert.False(vm.IsOpen);
        Assert.Equal(0, AppHost.Get<ISettingsStore>().Current.StarPrompt.PromptCount);

        // Once update card is closed, the star pill is allowed to show again later.
        AppHost.Get<UpdateCardViewModel>().LaterCommand.Execute(null);
        Assert.False(AppHost.Get<UpdateCardViewModel>().IsOpen);
        Assert.True(vm.TryTriggerNow());
        Assert.True(vm.IsOpen);
    }

    [AvaloniaFact]
    public void TieredSnoozeCooldownsAndPermanentCapAfterThreeDismissals()
    {
        var start = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var (vm, clock, _, _) = BuildHost(start, s =>
        {
            s.FirstSeenUtc = start.AddDays(-5);
            s.ActiveDaysCount = 4;
            s.LastActiveDate = "2026-11-25";
            s.CompletedAgentTurns = 10;
        });
        vm.Start();
        AddProjectWithAgent();

        // 1st prompt: click × reveals close menu; click Snooze -> 14-day cooldown.
        Assert.True(vm.TryTriggerNow());
        Assert.True(vm.CanSnooze);
        Assert.Contains("14", vm.SnoozeLabel);
        vm.ToggleCloseMenuCommand.Execute(null);
        Assert.True(vm.IsCloseMenuOpen);

        vm.SnoozeCommand.Execute(null);
        Assert.False(vm.IsOpen);
        var s1 = AppHost.Get<ISettingsStore>().Current.StarPrompt;
        Assert.Equal(1, s1.PromptCount);
        Assert.False(s1.DismissedForever);
        Assert.Equal(start.AddDays(14), s1.SnoozeUntilUtc);

        // Simulate a fresh app launch during the 14-day cooldown: still blocked.
        clock.Now = start.AddDays(10);
        var (vm2, clock2, _, _) = BuildHost(clock.Now, s =>
        {
            s.FirstSeenUtc = s1.FirstSeenUtc;
            s.ActiveDaysCount = s1.ActiveDaysCount;
            s.LastActiveDate = s1.LastActiveDate;
            s.CompletedAgentTurns = s1.CompletedAgentTurns;
            s.PromptCount = s1.PromptCount;
            s.SnoozeUntilUtc = s1.SnoozeUntilUtc;
        });
        vm2.Start();
        AddProjectWithAgent();
        Assert.False(vm2.TryTriggerNow());

        // Advance past 14 days -> 2nd prompt shows; snooze -> 30-day cooldown.
        clock2.Now = start.AddDays(15);
        Assert.True(vm2.TryTriggerNow());
        Assert.True(vm2.CanSnooze);
        Assert.Contains("30", vm2.SnoozeLabel);
        vm2.SnoozeCommand.Execute(null);
        var s2 = AppHost.Get<ISettingsStore>().Current.StarPrompt;
        Assert.Equal(2, s2.PromptCount);
        Assert.Equal(clock2.Now.AddDays(30), s2.SnoozeUntilUtc);

        // Advance past 30 days on a fresh launch -> 3rd (final) prompt: CanSnooze is false, × permanently silences.
        var after30 = clock2.Now.AddDays(31);
        var (vm3, _, _, _) = BuildHost(after30, s =>
        {
            s.FirstSeenUtc = s2.FirstSeenUtc;
            s.ActiveDaysCount = s2.ActiveDaysCount;
            s.LastActiveDate = s2.LastActiveDate;
            s.CompletedAgentTurns = s2.CompletedAgentTurns;
            s.PromptCount = s2.PromptCount;
            s.SnoozeUntilUtc = s2.SnoozeUntilUtc;
        });
        vm3.Start();
        AddProjectWithAgent();
        Assert.True(vm3.TryTriggerNow());
        Assert.False(vm3.CanSnooze);

        vm3.ToggleCloseMenuCommand.Execute(null);
        Assert.False(vm3.IsOpen);
        var s3 = AppHost.Get<ISettingsStore>().Current.StarPrompt;
        Assert.Equal(3, s3.PromptCount);
        Assert.True(s3.DismissedForever);
        Assert.False(vm3.CanShowNow());
    }

    [AvaloniaFact]
    public void ClickingStarOpensRepositoryShowsThanksAndSilencesForever()
    {
        var now = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var (vm, _, _, opener) = BuildHost(now, s =>
        {
            s.FirstSeenUtc = now.AddDays(-5);
            s.ActiveDaysCount = 4;
            s.LastActiveDate = "2026-11-25";
            s.CompletedAgentTurns = 10;
        });
        vm.Start();
        AddProjectWithAgent();

        Assert.True(vm.TryTriggerNow());
        vm.StarCommand.Execute(null);

        Assert.Equal([UpdateEndpoints.RepositoryUrl], opener.OpenedUrls);
        Assert.True(vm.IsOpen);
        Assert.False(vm.IsPromptVisible);
        Assert.True(vm.IsThanksVisible);
        Assert.True(AppHost.Get<ISettingsStore>().Current.StarPrompt.Starred);
        Assert.False(vm.CanShowNow());
    }

    [AvaloniaFact]
    public void FallbackIdleTriggersForTerminalOnlyUsers()
    {
        var start = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var (vm, clock, _, _) = BuildHost(start, s =>
        {
            s.FirstSeenUtc = start.AddDays(-6);
            s.ActiveDaysCount = 4;
            s.LastActiveDate = "2026-11-25";
            s.CompletedAgentTurns = 0;
            s.SessionStartCount = 8;
        });
        vm.Start();
        AddProjectWithAgent();

        // Not enough active session time yet.
        clock.Now = start.AddMinutes(5);
        Assert.False(vm.TryEvaluateFallbackIdle());

        // Active 11 minutes, but user just typed 3 seconds ago -> wait.
        clock.Now = start.AddMinutes(11);
        vm.NotifyUserInput();
        clock.Now = clock.Now.AddSeconds(3);
        Assert.False(vm.TryEvaluateFallbackIdle());

        // Quiet for 20 seconds -> triggers.
        clock.Now = clock.Now.AddSeconds(17);
        Assert.True(vm.TryEvaluateFallbackIdle());
        Assert.True(vm.IsOpen);
    }

    [AvaloniaFact]
    public void SchemeCPillRendersAndSwitchesStatesInShellView()
    {
        var now = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var (vm, _, _, _) = BuildHost(now, s =>
        {
            s.FirstSeenUtc = now.AddDays(-5);
            s.ActiveDaysCount = 4;
            s.LastActiveDate = "2026-11-25";
            s.CompletedAgentTurns = 10;
        });
        vm.Start();
        AddProjectWithAgent();

        var shell = new ShellView { DataContext = AppHost.Get<ShellViewModel>() };
        var window = new Window { Width = 1200, Height = 760, Content = shell };
        window.Show();
        try
        {
            var view = shell.GetVisualDescendants().OfType<StarPromptView>().Single();
            Assert.False(view.IsVisible);

            Assert.True(vm.TryTriggerNow());
            Assert.True(view.IsVisible);
            Assert.True(view.FindControl<Border>("PromptPill")!.IsVisible);
            Assert.False(view.FindControl<Border>("CloseMenuCard")!.IsVisible);
            Assert.False(view.FindControl<Border>("ThanksPill")!.IsVisible);

            vm.ToggleCloseMenuCommand.Execute(null);
            Assert.True(view.FindControl<Border>("CloseMenuCard")!.IsVisible);

            vm.StarCommand.Execute(null);
            Assert.False(view.FindControl<Border>("PromptPill")!.IsVisible);
            Assert.True(view.FindControl<Border>("ThanksPill")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
