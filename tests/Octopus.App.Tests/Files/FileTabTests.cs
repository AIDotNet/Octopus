using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Files;
using Octopus.App.Files.Code;
using Octopus.App.Files.Documents;
using Octopus.Core.Projects;
using Octopus.FileSystem;
using Octopus.Storage;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests.Files;

public class FileTabTests
{
    [Theory]
    [InlineData("Program.cs", "cs")]
    [InlineData("view.AXAML", "axaml")]
    [InlineData("Octopus.csproj", "xml")]
    [InlineData("a.json", "json")]
    [InlineData("tool.ts", "ts")]
    [InlineData("run.sh", "sh")]
    [InlineData("config.yml", "yaml")]
    [InlineData("README.md", "md")]
    [InlineData("Dockerfile", "dockerfile")]
    [InlineData("Makefile", "make")]
    [InlineData("notes.txt", "txt")]
    [InlineData("app.kt", "txt")]
    [InlineData("LICENSE", "txt")]
    public void A_file_name_maps_to_a_language_arc_knows_or_plain_text(string name, string language) =>
        Assert.Equal(language, FileLanguages.For(name));

    [Theory]
    [InlineData("a.md", true)]
    [InlineData("A.MARKDOWN", true)]
    [InlineData("a.mdx", false)]
    [InlineData("a.txt", false)]
    public void Only_markdown_files_are_markdown(string name, bool markdown) => Assert.Equal(markdown, FileLanguages.IsMarkdown(name));

    private static FileTabViewModel Open(string path) =>
        new(new ProjectItem { Kind = ProjectItemKind.File, FilePath = path }, AppHost.Get<IFileTreeService>(), AppHost.Get<ISettingsStore>());

    private static CodeViewerModel Code(FileTabViewModel model) => Assert.IsType<CodeViewerModel>(model.Viewer);

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
    public async Task A_code_file_has_no_view_switch_and_a_notice_hides_the_code()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-code-").FullName;
        try
        {
            var path = Path.Combine(dir, "a.cs");
            File.WriteAllText(path, "class A { }\n");
            using var model = Open(path);
            Assert.Equal(FileKind.Code, model.Kind);
            await WaitUntil(() => Code(model).Text.Length > 0);
            Assert.Equal("cs", Code(model).Language);

            var view = new FileTabView { DataContext = model };
            var window = new Window { Width = 600, Height = 400, Content = view };
            window.Show();
            Frames.Pump();
            Assert.DoesNotContain(view.GetVisualDescendants(), d => d is SegmentedControl);
            Assert.NotNull(view.GetVisualDescendants().OfType<CodeSurface>().Single());

            File.Delete(path);
            await model.Viewer.LoadAsync();
            Assert.True(Code(model).HasNotice);
            Assert.False(Code(model).HasContent);
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public async Task The_bar_shows_the_path_relative_to_the_project_and_quiet_facts_about_the_file()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-bar-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "src", "App"));
            var path = Path.Combine(dir, "src", "App", "data.json");
            File.WriteAllText(path, "{\n  \"a\": 1\n}\n");
            using var model = new FileTabViewModel(new ProjectItem { Kind = ProjectItemKind.File, FilePath = path },
                AppHost.Get<IFileTreeService>(), AppHost.Get<ISettingsStore>(), dir);
            await WaitUntil(() => Code(model).Text.Length > 0);
            Assert.Equal("src/App/", model.FolderText);
            Assert.Equal("data.json", model.FileLabel);
            Assert.Equal(3, Code(model).LineCount);
            Assert.Contains("JSON", Code(model).MetaText);
            Assert.Contains("3", Code(model).MetaText);
            Assert.Contains("B", Code(model).MetaText);

            // Outside the project the whole path shows.
            using var outside = new FileTabViewModel(new ProjectItem { Kind = ProjectItemKind.File, FilePath = path },
                AppHost.Get<IFileTreeService>(), AppHost.Get<ISettingsStore>(), Path.Combine(dir, "other"));
            Assert.EndsWith("src/App/", outside.FolderText);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public async Task A_change_on_disk_reloads_the_text_and_the_code_view_keeps_its_place()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-reload-").FullName;
        try
        {
            var path = Path.Combine(dir, "long.cs");
            File.WriteAllText(path, string.Concat(Enumerable.Range(1, 3000).Select(i => $"int v{i} = {i};\n")));
            using var model = Open(path);
            await WaitUntil(() => Code(model).Text.Length > 0);
            var view = new FileTabView { DataContext = model };
            var window = new Window { Width = 800, Height = 500, Content = view };
            window.Show();
            Frames.Pump();
            var surface = view.GetVisualDescendants().OfType<CodeSurface>().Single();
            var scroller = surface.ScrollArea;
            var code = surface.CodeView;
            Assert.Equal(3000, code.LineCount);

            scroller.Offset = new Avalonia.Vector(0, 1200);
            Frames.Pump();
            Assert.Equal(1200, scroller.Offset.Y, 1);

            File.WriteAllText(path, string.Concat(Enumerable.Range(1, 3001).Select(i => $"int v{i} = {i};\n")));
            await model.Viewer.LoadAsync();
            Frames.Pump();
            Assert.Equal(3001, code.LineCount);
            Assert.Equal(1200, scroller.Offset.Y, 1);
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public async Task The_wrap_button_state_reaches_the_code_view()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-wrap-").FullName;
        try
        {
            var path = Path.Combine(dir, "a.txt");
            File.WriteAllText(path, string.Join(' ', Enumerable.Repeat("word", 200)) + "\n");
            using var model = Open(path);
            await WaitUntil(() => Code(model).Text.Length > 0);
            var view = new FileTabView { DataContext = model };
            var window = new Window { Width = 500, Height = 300, Content = view };
            window.Show();
            Frames.Pump();
            var code = view.GetVisualDescendants().OfType<CodeSurface>().Single().CodeView;
            Assert.False(code.WrapLines);
            Assert.Equal(1, code.TotalRows);
            Code(model).ToggleWrapCommand.Execute(null);
            Frames.Pump();
            Assert.True(code.WrapLines);
            Assert.True(code.TotalRows > 1);
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaTheory]
    [InlineData(260)]
    [InlineData(380)]
    [InlineData(520)]
    public async Task A_narrow_tab_still_lays_out_without_looping(double width)
    {
        var dir = Directory.CreateTempSubdirectory("octopus-narrow-").FullName;
        try
        {
            var path = Path.Combine(dir, "README.md");
            File.WriteAllText(path, "# Title\n\nSome *text*.\n");
            using var model = Open(path);
            await WaitUntil(() => ((MarkdownViewerModel)model.Viewer).Text.Length > 0);
            var view = new FileTabView { DataContext = model };
            var window = new Window { Width = width, Height = 400, Content = view };
            window.Show();
            Frames.Pump();
            ((MarkdownViewerModel)model.Viewer).ViewMode = MarkdownViewerModel.SourceMode;
            Frames.Pump();
            window.Width = width + 40;
            Frames.Pump();
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resizing_the_tab_step_by_step_never_loops_layout(bool source)
    {
        var dir = Directory.CreateTempSubdirectory("octopus-resize-").FullName;
        try
        {
            var path = Path.Combine(dir, "README.md");
            File.WriteAllText(path, "# Title\n\n" + string.Join("\n\n", Enumerable.Repeat("Some *text* that wraps when the tab is narrow, `code` and a longer sentence to fill the line.", 30)) + "\n");
            using var model = Open(path);
            await WaitUntil(() => ((MarkdownViewerModel)model.Viewer).Text.Length > 0);
            if (source) ((MarkdownViewerModel)model.Viewer).ViewMode = MarkdownViewerModel.SourceMode;
            var host = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var view = new FileTabView { DataContext = model };
            Grid.SetColumn(view, 1);
            var side = new Border { Width = 260 };
            host.Children.Add(side);
            host.Children.Add(view);
            var window = new Window { Width = 1000, Height = 500, Content = host };
            window.Show();
            Frames.Pump();
            for (var width = 260.0; width >= 0; width -= 13)
            {
                side.Width = width;
                Frames.Pump();
            }
            for (var width = 0.0; width <= 500; width += 17)
            {
                side.Width = width;
                Frames.Pump();
            }
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
