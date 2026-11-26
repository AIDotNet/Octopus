using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.Acp.Protocol;
using Octopus.App.AgentGui;
using Octopus.App.Composition;
using Octopus.App.Files;
using Octopus.App.Files.Code;
using Octopus.App.Files.Documents;
using Octopus.App.Files.Lsp;
using Octopus.App.Plugins.Rendering;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.App.Tests.Explorer;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.FileSystem;
using Octopus.Git;
using Octopus.Storage;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

public class MemoryDisposalTests
{
    private static readonly byte[] OnePixelPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82,
    ];

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition(), "the condition did not become true in time");
    }

    [AvaloniaFact]
    public async Task FileTabView_clears_inner_code_viewer_DataContext_and_editor_text_on_close()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-code-close-").FullName;
        try
        {
            var path = Path.Combine(dir, "sample.cs");
            File.WriteAllText(path, "public sealed class Sample { public int Value => 42; }\n");
            using var model = new FileTabViewModel(
                new ProjectItem { Kind = ProjectItemKind.File, FilePath = path },
                AppHost.Get<IFileTreeService>(),
                AppHost.Get<ISettingsStore>());
            var codeModel = Assert.IsType<CodeViewerModel>(model.Viewer);
            await WaitUntil(() => codeModel.LoadId > 0);

            var view = new FileTabView { DataContext = model };
            var window = new Window { Width = 600, Height = 400, Content = view };
            window.Show();
            Frames.Pump();

            var inner = Assert.IsType<CodeViewerView>(view.ViewerView);
            Assert.Contains("Sample", inner.EditorControl.Text);
            Assert.NotNull(codeModel.ReadEditor);

            view.DataContext = null;
            Frames.Pump();

            Assert.Null(view.Content);
            Assert.Null(inner.DataContext);
            Assert.Equal("", inner.EditorControl.Text);
            Assert.Null(codeModel.ReadEditor);
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task FileTabView_clears_inner_markdown_viewer_editor_and_source_text_on_close()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-md-close-").FullName;
        try
        {
            var path = Path.Combine(dir, "notes.md");
            File.WriteAllText(path, "# Heading\n\nSome markdown content.\n");
            using var model = new FileTabViewModel(
                new ProjectItem { Kind = ProjectItemKind.File, FilePath = path },
                AppHost.Get<IFileTreeService>(),
                AppHost.Get<ISettingsStore>());
            var mdModel = Assert.IsType<MarkdownViewerModel>(model.Viewer);
            await WaitUntil(() => mdModel.HasContent);

            var view = new FileTabView { DataContext = model };
            var window = new Window { Width = 600, Height = 400, Content = view };
            window.Show();
            Frames.Pump();

            var inner = Assert.IsType<MarkdownViewerView>(view.ViewerView);
            mdModel.SetViewModeCommand.Execute(MarkdownViewerModel.SourceMode);
            await WaitUntil(() => inner.SourceEditor.Text.Contains("Heading"));

            view.DataContext = null;
            Frames.Pump();

            Assert.Null(view.Content);
            Assert.Null(inner.DataContext);
            Assert.Null(inner.Editor);
            Assert.Equal("", inner.SourceEditor.Text);
            Assert.Null(mdModel.ReadEditor);
            Assert.Null(mdModel.ReadSourceEditor);
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task DiffTabView_clears_diff_editor_buffers_when_DataContext_cleared()
    {
        if (ExplorerFixture.FindGit() is null) return;
        using var repo = new ExplorerFixture();
        repo.InitRepository();
        repo.Write("a.cs", "class A { int X = 1; }\n");
        repo.Git("add", ".");
        repo.Git("commit", "-q", "-m", "init");
        repo.Write("a.cs", "class A { int X = 2; }\n");

        using var model = new DiffTabViewModel(
            new DiffTabRequest(repo.Root, "a.cs", Path.Combine(repo.Root, "a.cs"), Staged: false),
            AppHost.Get<IGitService>(),
            AppHost.Get<IFileTreeService>(),
            AppHost.Get<ISettingsStore>());
        await WaitUntil(() => model.DiffLoadId > 0);

        var view = new DiffTabView { DataContext = model };
        var window = new Window { Width = 600, Height = 400, Content = view };
        window.Show();
        Frames.Pump();

        Assert.Contains("int X = 1", view.DiffEditorControl.OriginalText);
        Assert.Contains("int X = 2", view.DiffEditorControl.ModifiedText);

        view.DataContext = null;
        Frames.Pump();

        Assert.Equal("", view.DiffEditorControl.OriginalText);
        Assert.Equal("", view.DiffEditorControl.ModifiedText);
        Assert.Null(model.ReadModifiedEditor);
        window.Close();
    }

    [AvaloniaFact]
    public void ProjectDockController_clears_pane_content_and_icon_when_tab_closes()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-dock-close-").FullName;
        try
        {
            var file = Path.Combine(dir, "hello.cs");
            File.WriteAllText(file, "class Hello {}\n");

            var window = new MainWindow();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                var projects = AppHost.Get<IProjectService>();
                projects.AddProject(dir);
                var item = AppHost.Get<IItemOpener>().OpenFile(file)!;
                Dispatcher.UIThread.RunJobs();

                var layout = (DockLayout)AppHost.Get<WorkspaceViewModel>().CurrentLayout!;
                var pane = layout.Workspace.FindPane(item.Id)!;
                Assert.NotNull(pane.Content);

                projects.RemoveItem(item.Id);
                Dispatcher.UIThread.RunJobs();

                Assert.Null(pane.Content);
                Assert.Null(pane.Icon);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunningServer_shuts_down_only_after_last_attachment_is_released_and_idle_delay_elapses()
    {
        var idleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleCalls = 0;
        var disposedCalls = 0;
        using var server = new LanguageServerManager.RunningServer(
            isAlive: () => true,
            idleDelay: TimeSpan.FromMilliseconds(80),
            onIdle: s =>
            {
                Interlocked.Increment(ref idleCalls);
                s.Dispose();
                idleTcs.TrySetResult();
            },
            disposeCore: () => Interlocked.Increment(ref disposedCalls));

        Assert.True(server.TryAcquire());
        Assert.True(server.TryAcquire());

        // Releasing one of two attachments keeps the server alive.
        server.Release();
        await Task.Delay(140, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref idleCalls));

        // Releasing the last attachment starts the idle timer; re-acquiring before expiry cancels it.
        server.Release();
        await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(server.TryAcquire());
        await Task.Delay(120, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref idleCalls));

        // Final release triggers idle eviction and disposal exactly once.
        server.Release();
        await idleTcs.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref idleCalls));
        Assert.Equal(1, Volatile.Read(ref disposedCalls));
        Assert.False(server.TryAcquire());
    }

    [Fact]
    public void AttachmentHandle_disposes_inner_and_releases_once()
    {
        var innerDisposed = 0;
        var released = 0;
        var handle = new LanguageServerManager.AttachmentHandle(
            new CallbackDisposable(() => innerDisposed++),
            () => released++);

        handle.Dispose();
        handle.Dispose();

        Assert.Equal(1, innerDisposed);
        Assert.Equal(1, released);
    }

    [AvaloniaFact]
    public void UiIcons_reloads_when_file_timestamp_changes()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-ui-icons-").FullName;
        try
        {
            var iconPath = Path.Combine(dir, "icon.png");
            File.WriteAllBytes(iconPath, OnePixelPng);
            var first = UiIcons.TryLoadPackageBitmap(dir, "icon.png");
            Assert.NotNull(first);
            Assert.Same(first, UiIcons.TryLoadPackageBitmap(dir, "icon.png"));

            File.SetLastWriteTimeUtc(iconPath, DateTime.UtcNow.AddSeconds(5));
            var second = UiIcons.TryLoadPackageBitmap(dir, "icon.png");
            Assert.NotNull(second);
            Assert.NotSame(first, second);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task AgentGuiTabViewModel_ClearTimeline_and_NewSession_dispose_assistant_turns()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-acp-clear-").FullName;
        var settings = AppHost.Get<ISettingsStore>();
        var origSurface = settings.Current.Agents.DefaultSurface;
        settings.Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(dir, "acp-clear");
            var item = AppHost.Get<IItemOpener>().OpenAgent("opencode")!;
            var registry = AppHost.Get<ITabContentRegistry>();
            var view = Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(project, item));
            var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);
            var window = new Window { Width = 900, Height = 600, Content = view };
            window.Show();
            try
            {
                await WaitUntil(() => vm.State == AgentGuiState.Ready);

                var turn1 = new AssistantTurnItem("opencode", "OpenCode");
                turn1.AddGeneratedImage(new AcpImageContent
                {
                    Base64Data = Convert.ToBase64String(OnePixelPng),
                    MimeType = "image/png",
                });
                var img1 = Assert.Single(turn1.GeneratedImages);
                Assert.NotNull(img1.Source);
                vm.Timeline.Add(turn1);

                vm.ClearTimelineCommand.Execute(null);
                Assert.Empty(vm.Timeline);
                Assert.Empty(turn1.GeneratedImages);
                Assert.Null(img1.Source);

                var turn2 = new AssistantTurnItem("opencode", "OpenCode");
                turn2.AddGeneratedImage(new AcpImageContent
                {
                    Base64Data = Convert.ToBase64String(OnePixelPng),
                    MimeType = "image/png",
                });
                var img2 = Assert.Single(turn2.GeneratedImages);
                Assert.NotNull(img2.Source);
                vm.Timeline.Add(turn2);

                await vm.NewSessionAsync();
                Assert.Empty(vm.Timeline);
                Assert.Empty(turn2.GeneratedImages);
                Assert.Null(img2.Source);
            }
            finally
            {
                window.Close();
                await registry.ReleaseAsync(item.Id);
            }
        }
        finally
        {
            settings.Update(s => s.Agents.DefaultSurface = origSurface);
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public void AppSetup_uses_selective_Blocks_and_Special_registration_and_shared_highlighter()
    {
        var runtime = ArcRuntime.Current;
        Assert.NotNull(runtime);
        Assert.Contains("TKCode.Arc.Markdown", runtime!.Packages);
        Assert.Contains("TKCode.Arc.Blocks", runtime.Packages);
        Assert.Contains("TKCode.Arc.Special", runtime.Packages);

        Assert.True(runtime.Themes.Defines(typeof(SettingsGroup)));
        Assert.True(runtime.Themes.Defines(typeof(SettingsRow)));
        Assert.True(runtime.Themes.Defines(typeof(TKCode.Arc.Sidebar)));
        Assert.True(runtime.Themes.Defines(typeof(ImageReveal)));
        Assert.True(runtime.Themes.Defines(typeof(BranchGraph)));

        Assert.False(runtime.Themes.Defines(typeof(CommandPalette)));
        Assert.False(runtime.Themes.Defines(typeof(EmojiPicker)));

        Assert.True(CodeSyntax.Supports("cs"));
        Assert.NotNull(CodeSyntax.CreateSession("cs"));
        Assert.NotNull(ArcHighlighting.SharedHighlighter);
    }

    private sealed class CallbackDisposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
