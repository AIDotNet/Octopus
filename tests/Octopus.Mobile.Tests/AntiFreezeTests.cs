using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Octopus.Mobile.Common;
using Octopus.Mobile.Conversations;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Settings;
using Octopus.Mobile.Terminals;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Protocol;
using Octopus.Remote.Tests;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>
/// Regression tests for the mobile background/foreground anti-freeze architecture and Me -> Settings Center navigation:
/// minimal-diff collection sync, streaming Markdown throttling + prefix reuse, lazy single-tab mounting,
/// background stream suspension + single-snapshot recovery, and Settings Center controls.
/// </summary>
public class AntiFreezeTests
{
    [Fact]
    public void Collection_diff_sync_avoids_clear_and_add_storms_for_unchanged_and_incremental_updates()
    {
        var a = new object();
        var b = new object();
        var c = new object();
        var d = new object();
        var collection = new ObservableCollection<object> { a, b, c };
        var events = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => events.Add(e.Action);

        // Identical list -> zero events!
        Assert.False(MobileCollectionSync.SyncByReference(collection, [a, b, c]));
        Assert.Empty(events);

        // Adding 1 item at end -> single Add, never Reset!
        Assert.True(MobileCollectionSync.SyncByReference(collection, [a, b, c, d]));
        Assert.Equal([NotifyCollectionChangedAction.Add], events);
        Assert.Equal([a, b, c, d], collection);

        // Removing 1 item -> single Remove, never Reset!
        events.Clear();
        Assert.True(MobileCollectionSync.SyncByReference(collection, [a, c, d]));
        Assert.Equal([NotifyCollectionChangedAction.Remove], events);
        Assert.Equal([a, c, d], collection);

        // Reordering -> Move, never Reset!
        events.Clear();
        Assert.True(MobileCollectionSync.SyncByReference(collection, [c, a, d]));
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, events);
        Assert.Equal([c, a, d], collection);
    }

    [AvaloniaFact]
    public void Markdown_view_throttles_streaming_bursts_reuses_unchanged_prefix_blocks_and_flushes_on_completion()
    {
        var view = new MobileMarkdownView { IsStreaming = true };

        // First non-empty render happens immediately so UI isn't blank.
        view.Markdown = "# Heading\n\nFirst paragraph.\n\nStreaming start";
        Assert.Equal(1, view.RebuildCount);
        Assert.Equal(3, view.Blocks.Count);

        // Burst of 25 rapid streaming deltas within <120ms: must be coalesced instead of 25 full rebuilds!
        for (var i = 1; i <= 25; i++)
        {
            view.Markdown = $"# Heading\n\nFirst paragraph.\n\nStreaming token {i}";
        }
        Assert.Equal(1, view.RebuildCount);

        // Ending streaming flushes the final text immediately and reuses the 2 unchanged prefix blocks (Heading + First paragraph).
        view.IsStreaming = false;
        Assert.Equal(2, view.RebuildCount);
        Assert.Equal(2, view.LastReusedBlockCount);
        Assert.Equal(3, view.Blocks.Count);
        Assert.Contains("Streaming token 25", view.Blocks[2].RawText);
    }

    [AvaloniaFact]
    public void Hosts_page_lazily_mounts_only_one_root_tab_at_a_time_and_opens_settings_from_me_tab()
    {
        MobilePreferences.Current.ResetToDefaults();
        try
        {
            using var phone = new Phone();
            var page = phone.Top<HostsPage>();
            var hosts = (HostsViewModel)page.DataContext!;

            // Tab 0 (Activity): only ActivityTabContent is mounted in the visual tree.
            Assert.Same(page.FindControl<Control>("ActivityTabContent"), page.ActiveTabView);
            Assert.NotNull(TopLevel.GetTopLevel(page.FindControl<Control>("ActivityTabContent")!));
            Assert.Null(TopLevel.GetTopLevel(page.FindControl<Control>("HostsTabContent")!));
            Assert.False(page.FindControl<Control>("HostsTabContent")!.IsEffectivelyVisible);
            Assert.Null(TopLevel.GetTopLevel(page.FindControl<Control>("AccountTabContent")!));
            Assert.False(page.FindControl<Control>("AccountTabContent")!.IsEffectivelyVisible);

            // Switch to Tab 3 (Me): only AccountTabContent is mounted, Settings button & row are visible.
            hosts.SelectedTab = HostsViewModel.TabAccount;
            Phone.Pump();
            Assert.Same(page.FindControl<Control>("AccountTabContent"), page.ActiveTabView);
            Assert.NotNull(TopLevel.GetTopLevel(page.FindControl<Control>("AccountTabContent")!));
            Assert.Null(TopLevel.GetTopLevel(page.FindControl<Control>("ActivityTabContent")!));
            Assert.False(page.FindControl<Control>("ActivityTabContent")!.IsEffectivelyVisible);
            Assert.True(page.FindControl<Control>("NavSettingsButton")!.IsEffectivelyVisible);
            Assert.True(page.FindControl<Control>("MeSettingsRow")!.IsEffectivelyVisible);

            // Clicking Settings from Me tab pushes SettingsPage.
            hosts.OpenSettingsCommand.Execute(null);
            Phone.Settle();
            var settingsPage = phone.Top<SettingsPage>();
            var settingsVm = (SettingsViewModel)settingsPage.DataContext!;

            // Theme, anti-freeze toggles, terminal font size, and live search all work.
            settingsVm.SelectedThemeIndex = (int)MobileThemeMode.Dark;
            Assert.Equal(MobileThemeMode.Dark, MobilePreferences.Current.ThemeMode);
            Assert.Equal(ThemeVariant.Dark, Avalonia.Application.Current?.RequestedThemeVariant);

            settingsVm.SuspendStreamInBackground = false;
            Assert.False(MobilePreferences.Current.SuspendStreamInBackground);
            settingsVm.SuspendStreamInBackground = true;
            Assert.True(MobilePreferences.Current.SuspendStreamInBackground);

            var beforeFont = settingsVm.TerminalFontSize;
            settingsVm.LargerFontCommand.Execute(null);
            Assert.Equal(beforeFont + 1, settingsVm.TerminalFontSize);

            settingsVm.SearchQuery = "后台";
            Assert.True(settingsVm.ShowPerformanceSection);
            Assert.False(settingsVm.ShowAppearanceSection);
            Assert.False(settingsVm.HasNoSearchResults);

            // Non-matching search shows clean empty state and clears back cleanly.
            settingsVm.SearchQuery = "不存在的设置项xyz";
            Assert.True(settingsVm.HasNoSearchResults);
            settingsVm.ClearSearchCommand.Execute(null);
            Assert.False(settingsVm.HasSearchQuery);
            Assert.False(settingsVm.HasNoSearchResults);

            // Live CompactDensity toggle applies/removes .compact CSS class on MobileShell root immediately.
            Assert.Contains("compact", phone.Shell.Classes);
            settingsVm.CompactDensity = false;
            Phone.Pump();
            Assert.DoesNotContain("compact", phone.Shell.Classes);
            settingsVm.ResetDefaultsCommand.Execute(null);
            Phone.Pump();
            Assert.True(MobilePreferences.Current.CompactDensity);
            Assert.Contains("compact", phone.Shell.Classes);
            Assert.Equal(12, settingsVm.TerminalFontSize);

            settingsVm.BackCommand.Execute(null);
            Phone.Settle();
            phone.Top<HostsPage>();
        }
        finally
        {
            MobilePreferences.Current.ResetToDefaults();
        }
    }

    [AvaloniaFact]
    public void Terminal_paste_confirm_preference_guards_multiline_sends_and_allows_single_line()
    {
        MobilePreferences.Current.ResetToDefaults();
        try
        {
            using var phone = new Phone();
            var session = phone.Workspace.Hosts[0].Projects[0].Sessions[1];
            phone.Shell.ShowHost(session.Project.Host);
            phone.Shell.ShowSession(session);
            Assert.True(Phone.Until(() => phone.Top<TerminalPage>().ReadScreenText().Trim().Length > 0));
            var terminal = (TerminalViewModel)phone.Top<TerminalPage>().DataContext!;
            terminal.FitCommand.Execute(null);
            Phone.Pump();

            // Multi-line composer text prompts for confirmation when TerminalPasteConfirm is enabled.
            Assert.True(MobilePreferences.Current.TerminalPasteConfirm);
            terminal.ComposeText = "echo first\necho second";
            terminal.SendCommand.Execute(null);
            Assert.True(terminal.IsPasteConfirmOpen);
            Assert.Equal("echo first\necho second", terminal.ComposeText);

            // Cancelling leaves terminal untouched.
            terminal.CancelPasteCommand.Execute(null);
            Assert.False(terminal.IsPasteConfirmOpen);
            Assert.Equal("", phone.Workspace.ConnectionOf(session)!.Received);

            // Re-sending and confirming transmits the payload and clears the composer.
            terminal.SendCommand.Execute(null);
            Assert.True(terminal.IsPasteConfirmOpen);
            terminal.ConfirmPasteCommand.Execute(null);
            Assert.False(terminal.IsPasteConfirmOpen);
            Assert.Equal("", terminal.ComposeText);
            Assert.True(Phone.Until(() => phone.Workspace.ConnectionOf(session)!.Received == "echo first\necho second\r"));

            // Disabling TerminalPasteConfirm sends multi-line text directly without confirmation.
            MobilePreferences.Current.TerminalPasteConfirm = false;
            terminal.ComposeText = "ls\npwd";
            terminal.SendCommand.Execute(null);
            Assert.False(terminal.IsPasteConfirmOpen);
            Assert.True(Phone.Until(() => phone.Workspace.ConnectionOf(session)!.Received.EndsWith("ls\npwd\r", StringComparison.Ordinal)));
        }
        finally
        {
            MobilePreferences.Current.ResetToDefaults();
        }
    }

    [AvaloniaFact]
    public async Task Background_dirty_host_reloads_cleanly_on_foreground_without_stale_projects()
    {
        MobilePreferences.Current.ResetToDefaults();
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        studio.Workspace.AddProject("Octopus", "/home/ada/code/Octopus");

        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        phone.Pair(studio);

        var host = phone.Remote.Workspace.Hosts.Single(h => h.Id == studio.Identity.Id);
        Assert.True(Phone.Until(() => host.Projects.Count == 1));

        try
        {
            // Background the phone and add a project on Desktop while backgrounded (workspace event suppressed while asleep).
            MobileHost.NotifyBackgrounded();
            phone.Remote.Workspace.OnAppBackgrounded();
            studio.Workspace.AddProject("Octopus-New", "/home/ada/code/Octopus-New");
            await Task.Delay(120);
            Assert.Single(host.Projects);

            // Returning to foreground automatically reloads dirty connected hosts so no stale project list persists.
            MobileHost.NotifyForegrounded();
            phone.Remote.Workspace.OnAppForegrounded();
            Assert.True(Phone.Until(() => host.Projects.Any(p => p.Name == "Octopus-New"), 8_000),
                "Dirty connected host did not reload new projects after returning from background.");
        }
        finally
        {
            MobileHost.NotifyForegrounded();
            MobilePreferences.Current.ResetToDefaults();
        }
    }

    [AvaloniaFact]
    public async Task Background_stream_suspension_drops_background_bursts_and_resyncs_with_single_snapshot_on_foreground()
    {
        MobilePreferences.Current.ResetToDefaults();
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        var project = studio.Workspace.AddProject("Octopus", "/home/ada/code/Octopus");
        var gui = studio.Workspace.AddItem(project, "流式防卡死验证", "claude", surface: ItemSurfaces.Gui);

        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        phone.Pair(studio);

        var host = phone.Remote.Workspace.Hosts.Single(h => h.Id == studio.Identity.Id);
        Assert.True(Phone.Until(() => host.Projects.Count == 1 && host.Projects[0].Sessions.Count == 1));
        phone.Shell.ShowSession(host.Projects[0].Sessions[0]);
        Assert.True(Phone.Until(() => phone.Shell.Top is ConversationPage));
        var convVm = phone.Model<ConversationViewModel>();
        Assert.True(Phone.Until(() => convVm.IsLoaded));

        try
        {
            MobileHost.NotifyBackgrounded();
            phone.Remote.Workspace.OnAppBackgrounded();

            // Simulate a burst of prompts/updates happening on the host while the phone is backgrounded.
            var fakeConv = studio.Workspace.ConversationOf(gui.Id);
            for (var i = 1; i <= 15; i++)
            {
                fakeConv.Prompt(new AcpPromptMessage { ItemId = gui.Id, Text = $"后台消息 {i}" });
            }

            // Return to foreground: recovers via single fresh snapshot without hanging and shows the latest state.
            MobileHost.NotifyForegrounded();
            phone.Remote.Workspace.OnAppForegrounded();

            Assert.True(Phone.Until(() =>
                convVm.IsLoaded
                && !convVm.HasError
                && convVm.Entries.OfType<ConversationUserEntry>().Any(e => e.Text == "后台消息 15"), 10_000),
                $"Conversation did not resync to latest entry after foreground resume. Entries={convVm.Entries.Count}, Error={convVm.ErrorText}");
        }
        finally
        {
            MobileHost.NotifyForegrounded();
            MobilePreferences.Current.ResetToDefaults();
        }
    }
}
