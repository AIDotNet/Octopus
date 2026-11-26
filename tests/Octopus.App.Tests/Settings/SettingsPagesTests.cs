using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Composition;
using Octopus.App.Localization;
using Octopus.App.Settings;
using Octopus.App.Settings.About;
using Octopus.App.Settings.Agents;
using Octopus.App.Settings.Appearance;
using Octopus.App.Settings.General;
using Octopus.App.Settings.Shortcuts;
using Octopus.App.Settings.Terminal;
using Octopus.Platform;
using Octopus.Storage;
using TKCode.Arc;
using TKCode.Arc.Localization;
using Xunit;

namespace Octopus.App.Tests.Settings;

public class SettingsPagesTests
{
    public SettingsPagesTests() => SettingsTestSupport.Reset();

    [AvaloniaTheory]
    [InlineData("agents")]
    [InlineData("general")]
    [InlineData("workspace-cleanup")]
    [InlineData("terminal")]
    [InlineData("ssh")]
    [InlineData("account-devices")]
    [InlineData("remote")]
    [InlineData("remote-hosts")]
    [InlineData("appearance")]
    [InlineData("shortcuts")]
    [InlineData("plugins")]
    [InlineData("experiments")]
    [InlineData("about")]
    public void The_view_opens_on_the_requested_section(string section)
    {
        var view = new SettingsView { InitialSection = section };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var viewModel = Assert.IsType<SettingsViewModel>(view.DataContext);
        Assert.Equal(section, viewModel.Selected.Key);
        Assert.NotNull(viewModel.Page);
        window.Close();
    }

    [AvaloniaFact]
    public void Setting_descriptions_stay_hidden_inline_and_show_on_help_tip_hover()
    {
        var previous = ArcLanguage.Current;
        var view = new SettingsView { InitialSection = "general" };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        try
        {
            ArcLanguage.Set(LanguageTags.ZhCN);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var rows = view.GetVisualDescendants().OfType<SettingsRow>().ToList();
            Assert.NotEmpty(rows);
            foreach (var row in rows)
            {
                Assert.True(string.IsNullOrEmpty(row.Description));
                var tip = row.GetVisualDescendants().OfType<SettingHelpTip>().Single();
                Assert.True(tip.IsVisible);
                Assert.False(string.IsNullOrWhiteSpace(tip.Tip));
                var trigger = tip.FindControl<Border>("HitTarget");
                Assert.NotNull(trigger);
                Assert.Equal(tip.Tip, TKCode.Arc.Tooltip.GetTip(trigger));
                Assert.True(TKCode.Arc.Tooltip.GetShowDelay(trigger) <= TimeSpan.FromMilliseconds(250));
            }

            // Hovering the first ? icon opens its tooltip popup shortly after pointer enter.
            var hoverTrigger = rows[0].GetVisualDescendants().OfType<SettingHelpTip>().Single().FindControl<Border>("HitTarget")!;
            var center = hoverTrigger.TranslatePoint(new Point(hoverTrigger.Bounds.Width / 2, hoverTrigger.Bounds.Height / 2), window);
            Assert.NotNull(center);
            var getPopup = typeof(TKCode.Arc.Tooltip).GetMethod("GetPopup", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!;
            TKCode.Arc.Primitives.ArcPopup? PopupOf(Control c) => (TKCode.Arc.Primitives.ArcPopup?)getPopup.Invoke(null, [c]);

            window.MouseMove(center.Value);
            Dispatcher.UIThread.RunJobs();
            var opened = Frames.Until(() => PopupOf(hoverTrigger)?.IsOpen == true, milliseconds: 1500);
            var popup = PopupOf(hoverTrigger);
            Assert.True(opened, $"Tooltip did not open. isPointerOver={hoverTrigger.IsPointerOver}, popup={popup is not null}, popupOpen={popup?.IsOpen}, delay={TKCode.Arc.Tooltip.GetShowDelay(hoverTrigger)}");

            // Switching language updates the tooltip text live.
            var expectedEn = AppLangResources.Get(AppLangResourceKind.SettingsLanguageHint);
            ArcLanguage.Set(LanguageTags.EnUS);
            Dispatcher.UIThread.RunJobs();
            var firstTip = rows[0].GetVisualDescendants().OfType<SettingHelpTip>().Single();
            Assert.Equal(AppLangResources.Get(AppLangResourceKind.SettingsLanguageHint), firstTip.Tip);
            Assert.NotEqual(expectedEn, firstTip.Tip);

            // An empty SettingHelpTip collapses itself automatically once styled in the visual tree.
            var emptyTip = new SettingHelpTip { Tip = "" };
            window.Content = emptyTip;
            Dispatcher.UIThread.RunJobs();
            Assert.False(emptyTip.IsVisible);
            emptyTip.Tip = "Detailed help";
            Dispatcher.UIThread.RunJobs();
            Assert.True(emptyTip.IsVisible);
        }
        finally
        {
            window.Close();
            ArcLanguage.Set(previous);
        }
    }

    [AvaloniaFact]
    public void Settings_back_button_reserves_room_below_macOS_traffic_lights()
    {
        var view = new SettingsView();
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var spacer = view.FindControl<Border>("MacTitleBarSpacer");
            var backButton = view.FindControl<TKCode.Arc.Button>("BackButton");
            Assert.NotNull(spacer);
            Assert.NotNull(backButton);
            Assert.Equal(OperatingSystem.IsMacOS(), spacer.IsVisible);
            if (OperatingSystem.IsMacOS())
            {
                var topLeft = backButton.TranslatePoint(default, view);
                Assert.NotNull(topLeft);
                Assert.True(topLeft.Value.Y >= 34, $"Expected BackButton below macOS traffic lights, got Y={topLeft.Value.Y}");

                window.WindowState = WindowState.FullScreen;
                Dispatcher.UIThread.RunJobs();
                Assert.False(spacer.IsVisible);

                window.WindowState = WindowState.Normal;
                Dispatcher.UIThread.RunJobs();
                Assert.True(spacer.IsVisible);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Escape_and_back_ask_the_shell_to_close()
    {
        var view = new SettingsView();
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var closed = 0;
        view.CloseRequested += (_, _) => closed++;

        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Escape, Avalonia.Input.RawInputModifiers.None);

        Assert.Equal(1, closed);
        window.Close();
    }

    [AvaloniaFact]
    public void An_unknown_section_opens_the_agents_page()
    {
        var view = new SettingsView { InitialSection = "nope" };
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("agents", ((SettingsViewModel)view.DataContext!).Selected.Key);
        window.Close();
    }

    [AvaloniaFact]
    public void The_agents_page_splits_the_catalog_into_installed_and_not_installed()
    {
        var agents = NewAgentsPage(new SettingsTestSupport.FakeScanner("claude", "opencode"));

        Assert.Equal(["claude", "opencode"], agents.Installed.Select(a => a.Id));
        Assert.DoesNotContain(agents.NotInstalled, a => a.Id == "claude");
        Assert.Equal(AppHost.Get<Octopus.Agents.IAgentCatalog>().Definitions.Count - 2, agents.NotInstalled.Count);
        Assert.Contains(agents.NotInstalled, a => a.Id == "codex" && a.InstallCommand.Length > 0);
        // The first installed agent is open, and its details are there.
        Assert.True(agents.Installed[0].IsExpanded);
        Assert.False(agents.Installed[1].IsExpanded);
        agents.Dispose();
    }

    [AvaloniaFact]
    public async Task Install_button_confirms_runs_the_hint_and_moves_the_agent_to_installed()
    {
        var scanner = new SettingsTestSupport.FakeScanner("claude");
        var installer = new FakeAgentInstaller
        {
            OnInstalled = plan => scanner.Current.Installations.Add(new Octopus.Core.Agents.AgentInstallation { AgentId = plan.AgentId, Path = "/usr/local/bin/" + plan.AgentId }),
        };
        var dialogs = new FakeDialogService { Answer = true };
        var agents = NewAgentsPage(scanner, installer, dialogs);
        await Files.FileTabHarness.WaitUntil(() => agents.NotInstalled.FirstOrDefault(a => a.Id == "codex")?.ShowInstallButton == true);

        var codex = agents.NotInstalled.Single(a => a.Id == "codex");
        await codex.RunInstallCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, dialogs.Asked);
        Assert.Contains(codex.InstallCommand, dialogs.LastDescription);
        Assert.Equal("codex", Assert.Single(installer.Installed).AgentId);
        Assert.Contains(agents.Installed, a => a.Id == "codex");
        Assert.DoesNotContain(agents.NotInstalled, a => a.Id == "codex");
        agents.Dispose();
    }

    [AvaloniaFact]
    public async Task Declined_or_failed_installs_keep_the_agent_and_show_the_error()
    {
        var installer = new FakeAgentInstaller { Result = new Octopus.Agents.AgentInstallResult(false, "npm ERR! code EACCES") };
        var dialogs = new FakeDialogService { Answer = false };
        var agents = NewAgentsPage(new SettingsTestSupport.FakeScanner(), installer, dialogs);
        await Files.FileTabHarness.WaitUntil(() => agents.NotInstalled.FirstOrDefault(a => a.Id == "codex")?.ShowInstallButton == true);
        var codex = agents.NotInstalled.Single(a => a.Id == "codex");

        await codex.RunInstallCommand.ExecuteAsync(null);
        Assert.Empty(installer.Installed);

        dialogs.Answer = true;
        await codex.RunInstallCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(installer.Installed);
        Assert.True(codex.HasInstallError);
        Assert.Contains("EACCES", codex.InstallError);
        Assert.True(codex.ShowInstallButton);
        agents.Dispose();
    }

    [AvaloniaFact]
    public async Task An_install_that_needs_admin_runs_sudo_in_a_terminal_instead_of_in_the_background()
    {
        var scanner = new SettingsTestSupport.FakeScanner();
        var installer = new FakeAgentInstaller { Elevation = Octopus.Agents.InstallElevation.Required };
        var dialogs = new FakeDialogService
        {
            OnRunAccountLogin = _ =>
            {
                scanner.Current.Installations.Add(new Octopus.Core.Agents.AgentInstallation { AgentId = "codex", Path = "/usr/local/bin/codex" });
                return Task.FromResult(true);
            },
        };
        var agents = NewAgentsPage(scanner, installer, dialogs);
        await Files.FileTabHarness.WaitUntil(() => agents.NotInstalled.FirstOrDefault(a => a.Id == "codex")?.ShowInstallButton == true);
        var codex = agents.NotInstalled.Single(a => a.Id == "codex");
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.SettingsInstallAsAdmin), codex.InstallButtonText);

        await codex.RunInstallCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(installer.Installed);
        Assert.Equal(1, dialogs.LoginsRun);
        Assert.Contains(dialogs.LastLoginSpec!.Arguments, a => a.Contains("sudo " + codex.InstallCommand, StringComparison.Ordinal));
        Assert.Contains("sudo", dialogs.LastDescription);
        Assert.Contains(agents.Installed, a => a.Id == "codex");
        agents.Dispose();
    }

    [AvaloniaFact]
    public async Task A_permission_failure_turns_the_button_into_install_as_administrator()
    {
        var installer = new FakeAgentInstaller
        {
            Result = new Octopus.Agents.AgentInstallResult(false, "npm ERR! code EACCES", NeedsAdmin: true),
            CanElevateInBackground = true, // Windows: UAC instead of a terminal
        };
        var dialogs = new FakeDialogService();
        var agents = NewAgentsPage(new SettingsTestSupport.FakeScanner(), installer, dialogs);
        await Files.FileTabHarness.WaitUntil(() => agents.NotInstalled.FirstOrDefault(a => a.Id == "codex")?.ShowInstallButton == true);
        var codex = agents.NotInstalled.Single(a => a.Id == "codex");

        await codex.RunInstallCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(codex.NeedsAdmin);
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.SettingsInstallNeedsAdmin), codex.InstallError);
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.SettingsInstallAsAdmin), codex.InstallButtonText);

        await codex.RunInstallCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(installer.Installed);
        Assert.Single(installer.InstalledElevated);
        Assert.Equal(0, dialogs.LoginsRun);
        agents.Dispose();
    }

    [AvaloniaFact]
    public void Nothing_installed_gives_only_the_not_installed_group()
    {
        var agents = NewAgentsPage(new SettingsTestSupport.FakeScanner());

        Assert.Empty(agents.Installed);
        Assert.False(agents.HasInstalled);
        Assert.Equal(AppHost.Get<Octopus.Agents.IAgentCatalog>().Definitions.Count, agents.NotInstalled.Count);
        Assert.False(agents.IsNotInstalledExpanded);
        agents.Dispose();
    }

    [AvaloniaFact]
    public void Not_installed_agents_are_collapsed_by_default_and_toggle_on_click()
    {
        var agents = NewAgentsPage(new SettingsTestSupport.FakeScanner("claude"));
        var view = new AgentsPageView { DataContext = agents };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var listBorder = view.FindControl<Border>("NotInstalledListBorder");
        Assert.NotNull(listBorder);
        Assert.False(agents.IsNotInstalledExpanded);
        Assert.False(listBorder.IsVisible);

        agents.ToggleNotInstalledCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(agents.IsNotInstalledExpanded);
        Assert.True(listBorder.IsVisible);

        agents.Rebuild();
        Dispatcher.UIThread.RunJobs();
        Assert.True(agents.IsNotInstalledExpanded);
        Assert.True(listBorder.IsVisible);

        agents.ToggleNotInstalledCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(agents.IsNotInstalledExpanded);
        Assert.False(listBorder.IsVisible);

        window.Close();
        agents.Dispose();
    }

    [AvaloniaFact]
    public void The_global_permission_is_saved_and_full_access_shows_the_warning()
    {
        var agents = NewAgentsPage(new SettingsTestSupport.FakeScanner());
        var settings = AppHost.Get<ISettingsStore>();
        Assert.False(agents.ShowFullWarning);

        agents.GlobalPermission = "full";

        Assert.Equal("full", settings.Current.Agents.DefaultPermission);
        Assert.True(agents.ShowFullWarning);
        agents.Dispose();
    }

    [AvaloniaFact]
    public void Launch_at_login_is_enabled_by_default_and_toggling_syncs_auto_start()
    {
        var autoStart = AppHost.Get<FakeAutoStartService>();
        autoStart.Calls.Clear();
        var general = AppHost.Get<GeneralSettingsViewModel>();
        var settings = AppHost.Get<ISettingsStore>();

        Assert.True(general.LaunchAtLogin);
        Assert.True(settings.Current.Startup.LaunchAtLogin);

        general.LaunchAtLogin = false;
        Assert.False(general.LaunchAtLogin);
        Assert.False(settings.Current.Startup.LaunchAtLogin);
        Assert.False(autoStart.Enabled);
        Assert.Equal([false], autoStart.Calls);

        general.LaunchAtLogin = true;
        Assert.True(general.LaunchAtLogin);
        Assert.True(settings.Current.Startup.LaunchAtLogin);
        Assert.True(autoStart.Enabled);
        Assert.Equal([false, true], autoStart.Calls);
    }

    [AvaloniaFact]
    public void Tray_settings_are_saved()
    {
        var general = AppHost.Get<GeneralSettingsViewModel>();
        var settings = AppHost.Get<ISettingsStore>();
        // Shown by default; the close button keeps quitting until the person asks otherwise.
        Assert.True(general.TrayEnabled);
        Assert.False(general.CloseToTray);

        general.CloseToTray = true;
        general.TrayEnabled = false;

        Assert.True(settings.Current.Tray.CloseToTray);
        Assert.False(settings.Current.Tray.Enabled);
    }

    [AvaloniaFact]
    public void Agents_use_the_system_proxy_by_default_and_the_switch_is_saved()
    {
        var agents = AppHost.Get<AgentsSettingsViewModel>();
        var settings = AppHost.Get<ISettingsStore>();
        Assert.True(agents.UseSystemProxy);

        agents.UseSystemProxy = false;

        Assert.False(settings.Current.Agents.UseSystemProxy);
        Assert.False(agents.UseSystemProxy);
    }

    [AvaloniaFact]
    public void General_settings_are_saved()
    {
        var general = AppHost.Get<GeneralSettingsViewModel>();
        var settings = AppHost.Get<ISettingsStore>();

        general.Language = "en-US";

        Assert.Equal("en-US", settings.Current.Appearance.Language);
        Assert.Equal("en-US", ArcLanguage.Current.ToString());
        Assert.Contains(general.Languages, l => (string)l.Value! == "zh-CN");
        general.Language = "zh-CN";

        Assert.True(general.RestoreItemsOnStartup);
        Assert.True(general.ResumeAgentsOnRestart);
        general.ResumeAgentsOnRestart = false;
        general.RestoreItemsOnStartup = false;
        Assert.False(settings.Current.Terminal.ResumeAgentsOnRestart);
        Assert.False(settings.Current.Terminal.RestoreItemsOnStartup);
    }

    [AvaloniaFact]
    public void Terminal_settings_are_saved_and_clamped()
    {
        var terminal = AppHost.Get<TerminalSettingsViewModel>();
        var settings = AppHost.Get<ISettingsStore>();

        terminal.FontSize = 40;
        terminal.ScrollbackLines = 5;
        terminal.FontFamily = "Menlo";
        terminal.ShellMode = TerminalSettingsViewModel.CustomShell;
        terminal.ShellPath = "/bin/zsh";
        terminal.FlushPending();

        Assert.Equal(24, settings.Current.Terminal.FontSize);
        Assert.Equal(100, settings.Current.Terminal.ScrollbackLines);
        Assert.Equal("Menlo", settings.Current.Terminal.FontFamily);
        Assert.Equal("/bin/zsh", settings.Current.Terminal.Shell);

        terminal.ShellMode = TerminalSettingsViewModel.AutoShell;
        Assert.Null(settings.Current.Terminal.Shell);
    }

    [AvaloniaFact]
    public void Appearance_drives_the_arc_theme()
    {
        var appearance = new AppearanceSettingsViewModel();

        appearance.Theme = "dark";
        Assert.Equal(ArcAppearance.Dark, ArcTheme.State.Appearance);

        appearance.SelectAccentCommand.Execute(appearance.Accents.Single(a => a.Preset == ArcAccentPreset.Violet));
        Assert.Equal(ArcAccentPreset.Violet.ToString(), appearance.Accents.Single(a => a.IsSelected).Preset.ToString());

        // A change made elsewhere (the top bar's switch) shows on the page.
        ArcTheme.Apply(t => t.Appearance(ArcAppearance.Light));
        Assert.Equal("light", appearance.Theme);
        appearance.Dispose();
    }

    [Fact]
    public void Shortcuts_show_command_on_mac_and_control_elsewhere()
    {
        var mac = ShortcutCatalog.For(new Octopus.App.Shell.Keymap.MacAppKeymap());
        var other = ShortcutCatalog.For(new Octopus.App.Shell.Keymap.DefaultAppKeymap());

        Assert.Equal(mac.Count, other.Count);
        Assert.All(mac, e => Assert.All(e.Combos, c => Assert.Contains("⌘", c)));
        Assert.All(other, e => Assert.All(e.Combos, c => Assert.DoesNotContain("⌘", c)));
        Assert.Contains(other, e => e.Combos.Any(c => c.SequenceEqual(["Ctrl", "Shift", "T"])));
        Assert.Contains(mac, e => e.Combos.Any(c => c.SequenceEqual(["⌘", "⌥", "T"])));
    }

    [AvaloniaFact]
    public void About_page_exposes_links_paths_and_diagnostics()
    {
        var opener = new FakeOpener();
        var paths = AppHost.Get<IAppPaths>();
        var updates = AppHost.Get<Octopus.App.Updates.UpdateSettingsViewModel>();
        var updateService = AppHost.Get<Octopus.Updates.IUpdateService>();
        using var about = new AboutSettingsViewModel(updates, updateService, paths, opener);

        Assert.Equal(Octopus.Updates.UpdateEndpoints.ServiceUrl, about.WebsiteUrl);
        Assert.Equal(Octopus.Updates.UpdateEndpoints.RepositoryUrl, about.RepositoryUrl);
        Assert.Equal(paths.DataDirectory, about.DataDirectory);
        Assert.Equal(paths.CacheDirectory, about.CacheDirectory);
        Assert.Contains("Avalonia", about.TechStackSummary);

        about.OpenWebsiteCommand.Execute(null);
        about.OpenRepositoryCommand.Execute(null);
        about.OpenChangelogCommand.Execute(null);
        about.OpenIssuesCommand.Execute(null);
        about.OpenDataDirectoryCommand.Execute(null);
        about.OpenCacheDirectoryCommand.Execute(null);

        Assert.Equal(
        [
            Octopus.Updates.UpdateEndpoints.ServiceUrl,
            Octopus.Updates.UpdateEndpoints.RepositoryUrl,
            $"{Octopus.Updates.UpdateEndpoints.RepositoryUrl}/releases",
            $"{Octopus.Updates.UpdateEndpoints.RepositoryUrl}/issues",
        ], opener.Urls);
        Assert.Equal([paths.DataDirectory, paths.CacheDirectory], opener.Revealed);

        var diag = about.BuildDiagnosticsText();
        Assert.Contains("Octopus", diag);
        Assert.Contains(paths.DataDirectory, diag);
        Assert.Contains(Octopus.Updates.UpdateEndpoints.ServiceUrl, diag);

        Assert.False(about.DiagnosticsCopied);
        about.MarkDiagnosticsCopied();
        Assert.True(about.DiagnosticsCopied);
    }

    private sealed class FakeOpener : IFileOpener
    {
        public List<string> Urls { get; } = [];
        public List<string> Revealed { get; } = [];
        public void Open(string path) { }
        public void Reveal(string path) => Revealed.Add(path);
        public bool OpenUrl(string url) { Urls.Add(url); return true; }
    }

    private static AgentsSettingsViewModel NewAgentsPage(Octopus.Agents.IAgentScanner scanner) => new(
        AppHost.Get<ISettingsStore>(),
        AppHost.Get<Octopus.Agents.IAgentCatalog>(),
        scanner,
        AppHost.Get<Octopus.Agents.IAgentCommandCompiler>(),
        AppHost.Get<Octopus.Agents.IModelDiscovery>(),
        AppHost.Get<IFileOpener>(),
        new StoragePathPicker());

    private static AgentsSettingsViewModel NewAgentsPage(
        Octopus.Agents.IAgentScanner scanner,
        Octopus.Agents.IAgentInstaller installer,
        Octopus.App.Services.IDialogService dialogs) => new(
        AppHost.Get<ISettingsStore>(),
        AppHost.Get<Octopus.Agents.IAgentCatalog>(),
        scanner,
        AppHost.Get<Octopus.Agents.IAgentCommandCompiler>(),
        AppHost.Get<Octopus.Agents.IModelDiscovery>(),
        AppHost.Get<IFileOpener>(),
        new StoragePathPicker(),
        dialogs: dialogs,
        launchPlanner: AppHost.Get<Octopus.Terminal.ITerminalLaunchPlanner>(),
        installer: installer);
}
