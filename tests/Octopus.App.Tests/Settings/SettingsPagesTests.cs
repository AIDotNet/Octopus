using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.App.Composition;
using Octopus.App.Settings;
using Octopus.App.Settings.About;
using Octopus.App.Settings.Agents;
using Octopus.App.Settings.Appearance;
using Octopus.App.Settings.General;
using Octopus.App.Settings.Shortcuts;
using Octopus.App.Settings.Terminal;
using Octopus.Core.Agents;
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
    [InlineData("appearance")]
    [InlineData("shortcuts")]
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
    public void Nothing_installed_gives_only_the_not_installed_group()
    {
        var agents = NewAgentsPage(new SettingsTestSupport.FakeScanner());

        Assert.Empty(agents.Installed);
        Assert.False(agents.HasInstalled);
        Assert.Equal(AppHost.Get<Octopus.Agents.IAgentCatalog>().Definitions.Count, agents.NotInstalled.Count);
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
}
