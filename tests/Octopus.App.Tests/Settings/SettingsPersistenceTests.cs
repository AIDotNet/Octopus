using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.Agents;
using Octopus.App.Composition;
using Octopus.App.Settings.Agents;
using Octopus.App.Settings.Terminal;
using Octopus.Platform;
using Octopus.Storage;
using TKCode.Arc;
using TKCode.Arc.Localization;
using Xunit;

namespace Octopus.App.Tests.Settings;

public sealed class SettingsPersistenceTests
{
    [AvaloniaFact]
    public async Task Shell_selection_survives_discovery_language_change_and_disk_reload()
    {
        using var paths = new SettingsPaths();
        using var settings = new JsonSettingsStore(paths);
        var first = new ShellInfo(@"C:\shells\pwsh.exe", "pwsh", ShellKind.PowerShell);
        var second = new ShellInfo(@"C:\shells\cmd.exe", "cmd", ShellKind.Cmd);
        settings.Update(s => s.Terminal.Shell = first.Path);
        var resolver = new ControlledShellResolver(first);
        using var model = new TerminalSettingsViewModel(settings, resolver);
        var loaded = WatchShellOptions(model);
        var page = new TerminalPageView { DataContext = model };
        var window = new Window { Width = 1100, Height = 700, Content = page };
        var language = ArcLanguage.Current;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var select = Assert.Single(page.GetVisualDescendants().OfType<Select>());
            resolver.Available.SetResult([first, second]);
            await loaded.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(first.Path, select.SelectedValue);
            Assert.Equal(first.Path, settings.Current.Terminal.Shell);

            select.SetCurrentValue(Select.SelectedValueProperty, second.Path);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(second.Path, settings.Current.Terminal.Shell);
            Assert.True(LanguageTag.TryParse(language.ToString() == "en-US" ? "zh-CN" : "en-US", out var other));
            ArcLanguage.Set(other);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(second.Path, select.SelectedValue);
            Assert.Equal(second.Path, settings.Current.Terminal.Shell);
            model.FlushPending();
            settings.Flush();
        }
        finally
        {
            resolver.Available.TrySetResult([]);
            window.Close();
            ArcLanguage.Set(language);
        }

        using var reloaded = new JsonSettingsStore(paths);
        reloaded.Load();
        Assert.Equal(second.Path, reloaded.Current.Terminal.Shell);
        var nextResolver = new ControlledShellResolver(first);
        using var nextModel = new TerminalSettingsViewModel(reloaded, nextResolver);
        var nextLoaded = WatchShellOptions(nextModel);
        var nextPage = new TerminalPageView { DataContext = nextModel };
        var nextWindow = new Window { Width = 1100, Height = 700, Content = nextPage };
        try
        {
            nextWindow.Show();
            nextResolver.Available.SetResult([first, second]);
            await nextLoaded.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(second.Path, Assert.Single(nextPage.GetVisualDescendants().OfType<Select>()).SelectedValue);
            Assert.Equal(second.Path, reloaded.Current.Terminal.Shell);
        }
        finally
        {
            nextResolver.Available.TrySetResult([]);
            nextWindow.Close();
        }
    }

    [AvaloniaFact]
    public void Agent_expansion_choices_survive_rebuild_navigation_and_disk_reload_including_all_collapsed()
    {
        using var paths = new SettingsPaths();
        using (var settings = new JsonSettingsStore(paths))
        {
            using var page = AgentsPage(settings);
            var claude = page.Installed.Single(a => a.Id == "claude");
            var opencode = page.Installed.Single(a => a.Id == "opencode");
            Assert.True(claude.IsExpanded);
            claude.ToggleExpandedCommand.Execute(null);
            opencode.ToggleExpandedCommand.Execute(null);
            page.Rebuild();
            Assert.False(page.Installed.Single(a => a.Id == "claude").IsExpanded);
            Assert.True(page.Installed.Single(a => a.Id == "opencode").IsExpanded);
        }
        using (var settings = new JsonSettingsStore(paths))
        {
            settings.Load();
            using var page = AgentsPage(settings);
            Assert.False(page.Installed.Single(a => a.Id == "claude").IsExpanded);
            Assert.True(page.Installed.Single(a => a.Id == "opencode").IsExpanded);
            page.Installed.Single(a => a.Id == "opencode").ToggleExpandedCommand.Execute(null);
        }
        using (var settings = new JsonSettingsStore(paths))
        {
            settings.Load();
            using var page = AgentsPage(settings);
            Assert.All(page.Installed, a => Assert.False(a.IsExpanded));
        }
    }

    private static Task WatchShellOptions(TerminalSettingsViewModel model)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(model.ShellModes)) loaded.TrySetResult();
        };
        return loaded.Task;
    }

    private static AgentsSettingsViewModel AgentsPage(ISettingsStore settings) => new(
        settings, AppHost.Get<IAgentCatalog>(), new SettingsTestSupport.FakeScanner("claude", "opencode"),
        AppHost.Get<IAgentCommandCompiler>(), AppHost.Get<IModelDiscovery>(), AppHost.Get<IFileOpener>(), new StoragePathPicker());

    private sealed class ControlledShellResolver(ShellInfo shell) : IShellResolver
    {
        public TaskCompletionSource<IReadOnlyList<ShellInfo>> Available { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ShellInfo GetDefaultShell() => shell;
        public ShellInfo? Describe(string path) => null;
        public IReadOnlyList<ShellInfo> GetAvailableShells() => Available.Task.GetAwaiter().GetResult();
        public IReadOnlyList<string> GetInteractiveArguments(ShellInfo info) => [];
        public IReadOnlyList<string> GetCommandArguments(ShellInfo info, string commandLine) => [commandLine];
    }

    private sealed class SettingsPaths : IAppPaths, IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("octopus-settings-persistence-");
        public string DataDirectory => Path.Combine(_root.FullName, "data");
        public string CacheDirectory => Path.Combine(_root.FullName, "cache");
        public void Dispose() => _root.Delete(true);
    }
}
