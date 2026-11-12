using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Composition;
using Octopus.App.Files;
using Octopus.App.Files.Documents;
using Octopus.App.Services;
using Octopus.Core.Projects;
using Octopus.FileSystem;
using Octopus.Storage;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests.Files;

/// <summary>A file in a temporary folder, opened in a file tab inside a window: what the viewer tests drive.</summary>
internal sealed class FileTabHarness : IDisposable
{
    private FileTabHarness(string directory, string path, FileTabViewModel tab, FileTabView view, Window window)
    {
        Directory = directory;
        Path = path;
        Tab = tab;
        View = view;
        Window = window;
    }

    public string Directory { get; }
    public string Path { get; }
    public FileTabViewModel Tab { get; }
    public FileTabView View { get; }
    public Window Window { get; }

    public MarkdownViewerModel Markdown => Assert.IsType<MarkdownViewerModel>(Tab.Viewer);
    public MarkdownViewerView MarkdownView => Assert.IsType<MarkdownViewerView>(View.ViewerView);
    public RichTextEditor Editor => MarkdownView.Editor ?? throw new InvalidOperationException("no editor is open");
    public FakeDialogService Dialogs => AppHost.Get<FakeDialogService>();

    public byte[] Disk => File.ReadAllBytes(Path);

    public static async Task<FileTabHarness> OpenAsync(byte[] bytes, string name = "doc.md", double width = 1100, double height = 760,
        Action<string>? beforeOpen = null)
    {
        var dir = System.IO.Directory.CreateTempSubdirectory("octopus-file-").FullName;
        var path = System.IO.Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        // An old modification time: any rewrite of the file shows as a newer one.
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        beforeOpen?.Invoke(dir);
        var tab = new FileTabViewModel(new ProjectItem { Kind = ProjectItemKind.File, FilePath = path },
            AppHost.Get<IFileTreeService>(), AppHost.Get<ISettingsStore>(), dir);
        var view = new FileTabView { DataContext = tab };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        var harness = new FileTabHarness(dir, path, tab, view, window);
        await WaitUntil(() => tab.Viewer.HasContent && ViewerLoaded(tab.Viewer) || tab.Viewer.HasNotice);
        Frames.Pump();
        return harness;
    }

    public static Task<FileTabHarness> OpenAsync(string text, string name = "doc.md", double width = 1100, double height = 760) =>
        OpenAsync(new System.Text.UTF8Encoding(false).GetBytes(text), name, width, height);

    private static bool ViewerLoaded(FileViewerModel viewer) => viewer switch
    {
        MarkdownViewerModel md => md.FileSize > 0 || md.Text.Length > 0 || md.Banner != MarkdownBanner.None || md.LoadId > 0 || md.IsEditing,
        _ => true,
    };

    public static async Task WaitUntil(Func<bool> condition, int milliseconds = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition() && DateTime.UtcNow < end)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
            Frames.Pump();
        }
        Assert.True(condition(), "the condition did not become true in time");
    }

    public void Settle(int milliseconds = 300)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            Thread.Sleep(15);
            Frames.Pump();
        }
    }

    /// <summary>Types into the editor the way a person does: key text to the focused editor.</summary>
    public void Type(string text)
    {
        Editor.FocusEditor();
        Frames.Pump();
        Window.KeyTextInput(text);
        Frames.Pump();
    }

    /// <summary>The platform's save shortcut, as the window receives it (Cmd or Ctrl by the platform settings).</summary>
    public void PressSave()
    {
        var modifiers = (RawInputModifiers)(int)(Avalonia.Application.Current!.PlatformSettings!.HotkeyConfiguration.CommandModifiers);
        Window.KeyPress(Key.S, modifiers, PhysicalKey.S, "s");
        Frames.Pump();
    }

    public void Dispose()
    {
        Window.Close();
        Tab.Dispose();
        try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { }
    }
}
