using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.App.Composition;
using Octopus.App.Files;
using Octopus.App.Files.Code;
using Octopus.App.Files.Documents;
using Octopus.App.Files.Lsp;
using Octopus.App.Settings;
using Octopus.App.Settings.Experiments;
using Octopus.App.Tests.Explorer;
using Octopus.App.Tests.Files;
using Octopus.Core.Settings;
using Octopus.FileSystem;
using Octopus.Git;
using Octopus.Platform;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests.Settings;

public class ExperimentsSettingsTests
{
    public ExperimentsSettingsTests() => SettingsTestSupport.Reset();

    [Fact]
    public void Experimental_settings_default_to_code_and_diff_editor_on_and_lsp_off_and_persist()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-exp-").FullName;
        try
        {
            var paths = new TempPaths(dir);
            using (var store = new JsonSettingsStore(paths))
            {
                store.Load();
                Assert.True(store.Current.Experiments.CodeEditor);
                Assert.True(store.Current.Experiments.DiffEditor);
                Assert.False(store.Current.Experiments.LanguageServer);

                store.Update(s =>
                {
                    s.Experiments.CodeEditor = false;
                    s.Experiments.DiffEditor = false;
                    s.Experiments.LanguageServer = true;
                });
                store.Flush();
            }

            using var reloaded = new JsonSettingsStore(paths);
            reloaded.Load();
            Assert.False(reloaded.Current.Experiments.CodeEditor);
            Assert.False(reloaded.Current.Experiments.DiffEditor);
            Assert.True(reloaded.Current.Experiments.LanguageServer);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public async Task Experiments_page_binds_toggles_and_lists_detected_language_servers()
    {
        var lsp = AppHost.Get<FakeLanguageServerManager>();
        lsp.Installed.Add(new DetectedLanguageServer(LanguageServerCatalog.All[0], "/usr/local/bin/csharp-ls"));
        lsp.Installed.Add(new DetectedLanguageServer(LanguageServerCatalog.All[1], null));

        var view = new SettingsView { InitialSection = "experiments" };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var settingsVm = Assert.IsType<SettingsViewModel>(view.DataContext);
        var page = Assert.IsType<ExperimentsSettingsViewModel>(settingsVm.Page);
        await FileTabHarness.WaitUntil(() => page.Servers.Count == 2);

        Assert.True(page.CodeEditor);
        Assert.True(page.DiffEditor);
        Assert.False(page.LanguageServer);
        Assert.True(page.CanConfigureLanguageServer);
        Assert.True(page.Servers[0].IsInstalled);
        Assert.False(page.Servers[1].IsInstalled);

        page.LanguageServer = true;
        Assert.True(AppHost.Get<ISettingsStore>().Current.Experiments.LanguageServer);

        page.CodeEditor = false;
        Assert.False(AppHost.Get<ISettingsStore>().Current.Experiments.CodeEditor);
        Assert.False(page.CanConfigureLanguageServer);

        page.DiffEditor = false;
        Assert.False(AppHost.Get<ISettingsStore>().Current.Experiments.DiffEditor);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Toggling_code_editor_falls_back_clean_tabs_and_preserves_dirty_tabs()
    {
        var settings = AppHost.Get<ISettingsStore>();
        using var h = await FileTabHarness.OpenAsync("class Program {}\n", "Program.cs");
        Assert.True(h.Code.ShowCodeEditor);
        Assert.False(h.Code.ShowReadOnlyFallback);
        Assert.True(h.Code.CanSave);

        // Clean tab switches immediately to lightweight read-only fallback.
        settings.Update(s => s.Experiments.CodeEditor = false);
        await FileTabHarness.WaitUntil(() => h.Code.ShowReadOnlyFallback);
        Assert.False(h.Code.ShowCodeEditor);
        Assert.False(h.Code.CanSave);
        Assert.Equal("class Program {}\n", h.CodeView.FallbackSurface.Text);

        // Turning back on restores CodeEditor.
        settings.Update(s => s.Experiments.CodeEditor = true);
        await FileTabHarness.WaitUntil(() => h.Code.ShowCodeEditor);
        Assert.True(h.Code.CanSave);

        // Dirty tab is NOT interrupted if user turns the toggle off while editing.
        h.TypeCode("// dirty\n");
        Assert.True(h.Code.HasUnsavedChanges);
        settings.Update(s => s.Experiments.CodeEditor = false);
        Frames.Pump();
        Assert.True(h.Code.ShowCodeEditor);
        Assert.True(h.Code.HasUnsavedChanges);
    }

    [AvaloniaFact]
    public async Task Toggling_code_editor_controls_markdown_source_mode_editability()
    {
        var settings = AppHost.Get<ISettingsStore>();
        using var h = await FileTabHarness.OpenAsync("# Title\n\nBody\n", "doc.md");
        h.Markdown.ViewMode = MarkdownViewerModel.SourceMode;
        Frames.Pump();
        Assert.True(h.Markdown.ShowSourceEditor);
        Assert.False(h.Markdown.ShowSourceFallback);
        Assert.True(h.Markdown.CanEditSource);

        settings.Update(s => s.Experiments.CodeEditor = false);
        await FileTabHarness.WaitUntil(() => h.Markdown.ShowSourceFallback);
        Assert.False(h.Markdown.ShowSourceEditor);
        Assert.False(h.Markdown.CanEditSource);
    }

    [AvaloniaFact]
    public async Task Toggling_diff_editor_switches_between_monaco_diff_and_unified_fallback()
    {
        if (ExplorerFixture.FindGit() is null) return;
        var settings = AppHost.Get<ISettingsStore>();
        using var repo = new ExplorerFixture();
        repo.InitRepository();
        repo.Write("a.txt", "one\ntwo\n");
        repo.Git("add", ".");
        repo.Git("commit", "-q", "-m", "init");
        repo.Write("a.txt", "one\nTWO\n");

        using var diff = new DiffTabViewModel(
            new DiffTabRequest(repo.Root, "a.txt", Path.Combine(repo.Root, "a.txt"), false),
            AppHost.Get<IGitService>(), AppHost.Get<IFileTreeService>(), settings);
        await FileTabHarness.WaitUntil(() => diff.ShowDiffEditor);

        settings.Update(s => s.Experiments.DiffEditor = false);
        await FileTabHarness.WaitUntil(() => diff.ShowUnifiedFallback);
        Assert.False(diff.ShowDiffEditor);

        settings.Update(s => s.Experiments.DiffEditor = true);
        await FileTabHarness.WaitUntil(() => diff.ShowDiffEditor);
    }

    [AvaloniaFact]
    public async Task Enabling_and_disabling_lsp_attaches_and_detaches_code_tabs()
    {
        var settings = AppHost.Get<ISettingsStore>();
        var lsp = AppHost.Get<FakeLanguageServerManager>();

        using var h = await FileTabHarness.OpenAsync("class A {}\n", "A.cs");
        Frames.Pump();
        Assert.Empty(lsp.AttachCalls);

        settings.Update(s => s.Experiments.LanguageServer = true);
        await FileTabHarness.WaitUntil(() => lsp.AttachCalls.Count == 1);
        Assert.Equal("csharp", lsp.AttachCalls[0].LanguageId);
        Assert.Equal(h.Path, lsp.AttachCalls[0].FilePath);

        settings.Update(s => s.Experiments.LanguageServer = false);
        await FileTabHarness.WaitUntil(() => lsp.DetachedCount == 1);
    }

    private sealed class TempPaths(string root) : IAppPaths
    {
        public string DataDirectory => root;
        public string CacheDirectory => Path.Combine(root, "cache");
    }
}
