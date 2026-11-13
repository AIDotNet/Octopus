using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Octopus.App.Composition;
using Octopus.App.Files;
using Octopus.App.Files.Images;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.App.Workspace;
using Octopus.Core.Projects;
using SkiaSharp;
using Xunit;

namespace Octopus.App.Tests.Files;

public class ImageViewerTests
{
    private static ImageViewerModel Image(FileTabHarness h) => Assert.IsType<ImageViewerModel>(h.Tab.Viewer);
    private static ImageViewerView ImageView(FileTabHarness h) => Assert.IsType<ImageViewerView>(h.View.ViewerView);

    private static async Task<FileTabHarness> OpenImage(byte[] bytes, string name = "pic.png", double width = 1100, double height = 760)
    {
        var h = await FileTabHarness.OpenAsync(bytes, name, width, height);
        await FileTabHarness.WaitUntil(() => Image(h).Image is not null || Image(h).HasNotice);
        h.Settle(100);
        return h;
    }

    [AvaloniaFact]
    public async Task A_small_picture_opens_fitted_at_100_percent_with_its_facts_in_the_bar()
    {
        using var h = await OpenImage(TestImages.Png(300, 200));
        var model = Image(h);
        Assert.Equal(FileKind.Image, h.Tab.Kind);
        Assert.NotNull(model.Image);
        Assert.Equal(new PixelSize(300, 200), model.Image!.PixelSize);
        Assert.True(model.IsFit);
        Assert.Equal("100%", model.ZoomText);
        Assert.Contains("PNG", model.MetaText);
        Assert.Contains("300", model.MetaText);
        Assert.Contains("200", model.MetaText);
        Assert.Contains("KB", model.MetaText);
        // Centered in the canvas, not stretched.
        var canvas = ImageView(h).Canvas;
        var rect = model.Viewport!.ImageRect;
        Assert.Equal(canvas.Bounds.Width / 2, rect.Center.X, 1);
        Assert.Equal(canvas.Bounds.Height / 2, rect.Center.Y, 1);
        Assert.Equal(300, rect.Width, 1);
    }

    [AvaloniaFact]
    public async Task A_large_picture_opens_fitted_to_the_window_and_the_buttons_zoom()
    {
        using var h = await OpenImage(TestImages.Png(3200, 2400));
        var model = Image(h);
        Assert.True(model.IsFit);
        Assert.True(model.Viewport!.Zoom < 0.5);
        var fit = model.ZoomText;

        model.ZoomInCommand.Execute(null);
        Assert.False(model.IsFit);
        Assert.NotEqual(fit, model.ZoomText);
        model.ActualSizeCommand.Execute(null);
        Assert.Equal("100%", model.ZoomText);
        Assert.Equal(1, model.Viewport.Zoom);
        model.ZoomOutCommand.Execute(null);
        Assert.Equal("75%", model.ZoomText);
        model.FitCommand.Execute(null);
        Assert.True(model.IsFit);
        Assert.Equal(fit, model.ZoomText);
    }

    [AvaloniaTheory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("webp")]
    [InlineData("bmp")]
    [InlineData("ico")]
    [InlineData("gif")]
    public async Task Each_supported_format_decodes_and_reports_its_own_name(string extension)
    {
        var bytes = extension switch
        {
            "png" => TestImages.Png(40, 30),
            "jpg" => TestImages.Jpeg(40, 30),
            "webp" => TestImages.WebP(40, 30),
            "bmp" => TestImages.Bmp(40, 30),
            "ico" => TestImages.Ico(32),
            _ => TestImages.Gif(),
        };
        using var h = await OpenImage(bytes, "pic." + extension);
        var model = Image(h);
        Assert.Null(model.Notice);
        Assert.NotNull(model.Image);
        Assert.Equal(ImageProbe.FormatName(model.Info!.Format), model.MetaText.Split(" · ")[0]);
        Assert.Equal(extension == "ico" ? 32 : extension == "gif" ? 1 : 40, model.Image!.PixelSize.Width);
    }

    [AvaloniaFact]
    public async Task A_file_without_an_extension_is_opened_as_a_picture_by_its_bytes()
    {
        var path = Path.Combine(Path.GetTempPath(), "octopus-noext-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, TestImages.Png(20, 20));
        try
        {
            var tab = new FileTabViewModel(new ProjectItem { Kind = ProjectItemKind.File, FilePath = path }, AppHost.Get<Octopus.FileSystem.IFileTreeService>(), AppHost.Get<Octopus.Storage.ISettingsStore>());
            Assert.Equal(FileKind.Image, tab.Kind);
            tab.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task A_damaged_picture_says_so_instead_of_failing()
    {
        var truncated = TestImages.Png(200, 200)[..60];
        using var h = await OpenImage(truncated);
        var model = Image(h);
        Assert.Null(model.Image);
        Assert.True(model.HasNotice);
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.ImageBroken), model.Notice);
        Assert.False(model.HasContent);

        using var text = await OpenImage("this is not a png at all"u8.ToArray(), "fake.png");
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.ImageBroken), Image(text).Notice);
    }

    [AvaloniaFact]
    public async Task A_picture_with_too_many_pixels_is_not_decoded_and_says_how_big_it_is()
    {
        using var h = await OpenImage(TestImages.PngHeaderOnly(20000, 20000)); // 400 million pixels claimed by a 33-byte file
        var model = Image(h);
        Assert.Null(model.Image);
        Assert.NotNull(model.Notice);
        Assert.Equal(400_000_000, model.Info!.Pixels);
        Assert.Contains("20", model.Notice);
        Assert.NotEqual(AppLangResources.Get(AppLangResourceKind.ImageBroken), model.Notice);
        Assert.Contains("PNG", model.MetaText);
    }

    [AvaloniaFact]
    public async Task A_picture_file_over_50_megabytes_is_not_read()
    {
        using var h = await OpenImage(TestImages.Png(10, 10));
        await using (var stream = new FileStream(h.Path, FileMode.Create))
        {
            stream.SetLength(ImageViewerModel.MaxFileBytes + 1);
        }
        await Image(h).OnFileChangedAsync();
        await FileTabHarness.WaitUntil(() => Image(h).HasNotice);
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.ImageFileTooLarge), Image(h).Notice);
        Assert.Null(Image(h).Image);
    }

    [AvaloniaFact]
    public async Task A_change_on_disk_loads_the_new_picture_and_keeps_a_chosen_zoom_when_the_size_is_the_same()
    {
        using var h = await OpenImage(TestImages.Png(2000, 1000));
        var model = Image(h);
        model.ActualSizeCommand.Execute(null);
        model.Pan(new Vector(-300, -100));
        var offset = model.Viewport!.Offset;

        File.WriteAllBytes(h.Path, TestImages.Jpeg(2000, 1000));
        await model.OnFileChangedAsync();
        Assert.Contains("JPEG", model.MetaText);
        Assert.False(model.IsFit);
        Assert.Equal(offset.X, model.Viewport!.Offset.X, 3);
        Assert.Equal(offset.Y, model.Viewport.Offset.Y, 3);

        File.WriteAllBytes(h.Path, TestImages.Png(500, 500));
        await model.OnFileChangedAsync();
        Assert.Contains("500", model.MetaText);
        Assert.True(model.IsFit);

        File.Delete(h.Path);
        await model.OnFileChangedAsync();
        Assert.True(model.HasNotice);
        Assert.Null(model.Image);
    }

    [AvaloniaFact]
    public async Task The_file_watcher_reloads_a_changed_picture()
    {
        using var h = await OpenImage(TestImages.Png(100, 100));
        await Task.Delay(500);
        File.WriteAllBytes(h.Path, TestImages.Png(123, 77));
        await FileTabHarness.WaitUntil(() => Image(h).Info?.Width == 123, 8000);
    }

    // ---- input ---------------------------------------------------------------------------------------------------

    private static RawInputModifiers Command => (RawInputModifiers)(int)Application.Current!.PlatformSettings!.HotkeyConfiguration.CommandModifiers;

    [AvaloniaFact]
    public async Task The_wheel_with_the_command_key_zooms_about_the_pointer_and_a_plain_wheel_moves_the_picture()
    {
        using var h = await OpenImage(TestImages.Png(3000, 2000));
        var model = Image(h);
        var canvas = ImageView(h).Canvas;
        var origin = canvas.TranslatePoint(new Point(300, 200), h.Window)!.Value;

        h.Window.MouseWheel(origin, new Vector(0, 1), Command);
        Frames.Pump();
        Assert.False(model.IsFit);
        var zoomed = model.Viewport!.Zoom;
        h.Window.MouseWheel(origin, new Vector(0, 1), Command);
        Frames.Pump();
        Assert.True(model.Viewport.Zoom > zoomed);
        h.Window.MouseWheel(origin, new Vector(0, -2), Command);
        Frames.Pump();
        Assert.True(model.Viewport.Zoom < zoomed);

        model.ActualSizeCommand.Execute(null);
        var before = model.Viewport.Offset;
        h.Window.MouseWheel(origin, new Vector(0, -1), RawInputModifiers.None);
        Frames.Pump();
        Assert.NotEqual(before, model.Viewport.Offset);
        Assert.Equal(1, model.Viewport.Zoom);
    }

    [AvaloniaFact]
    public async Task Dragging_moves_a_zoomed_picture_and_a_double_click_toggles_fit_and_100_percent()
    {
        using var h = await OpenImage(TestImages.Png(3000, 2000));
        var model = Image(h);
        var canvas = ImageView(h).Canvas;
        var start = canvas.TranslatePoint(new Point(500, 300), h.Window)!.Value;

        h.Window.MouseDown(start, MouseButton.Left);
        h.Window.MouseUp(start, MouseButton.Left);
        h.Window.MouseDown(start, MouseButton.Left); // the second press of a double click
        h.Window.MouseUp(start, MouseButton.Left);
        Frames.Pump();
        Assert.False(model.IsFit);
        Assert.Equal(1, model.Viewport!.Zoom);

        var before = model.Viewport.Offset;
        h.Window.MouseDown(start, MouseButton.Left);
        h.Window.MouseMove(start + new Point(-120, -80));
        h.Window.MouseUp(start + new Point(-120, -80), MouseButton.Left);
        Frames.Pump();
        Assert.Equal(before.X - 120, model.Viewport.Offset.X, 1);
        Assert.Equal(before.Y - 80, model.Viewport.Offset.Y, 1);

        await Task.Delay(700); // long enough that the next press is not part of the same click run
        h.Window.MouseDown(start, MouseButton.Left);
        h.Window.MouseUp(start, MouseButton.Left);
        h.Window.MouseDown(start, MouseButton.Left);
        h.Window.MouseUp(start, MouseButton.Left);
        Frames.Pump();
        Assert.True(model.IsFit);
    }

    [AvaloniaFact]
    public async Task Resizing_the_window_refits_the_picture()
    {
        using var h = await OpenImage(TestImages.Png(3000, 2000));
        var model = Image(h);
        var before = model.Viewport!.Zoom;
        h.Window.Width = 500;
        h.Window.Height = 400;
        h.Settle(100);
        Assert.True(model.IsFit);
        Assert.True(model.Viewport!.Zoom < before);
        Assert.True(model.Viewport.ImageRect.Width <= ImageView(h).Canvas.Bounds.Width);
    }

    // ---- opened from the file tree -------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task A_picture_clicked_in_the_file_tree_opens_in_the_image_viewer()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-tree-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "logo.png"), TestImages.Png(64, 64));
            File.WriteAllBytes(Path.Combine(dir, "icon.ico"), TestImages.Ico(32));
            File.WriteAllBytes(Path.Combine(dir, "photo.jpg"), TestImages.Jpeg(64, 64));
            var project = AppHost.Get<IProjectService>().AddProject(dir);
            var window = new MainWindow();
            window.Show();
            Frames.Pump();
            var shell = AppHost.Get<ShellViewModel>();
            var registry = AppHost.Get<ITabContentRegistry>();
            foreach (var name in new[] { "logo.png", "icon.ico", "photo.jpg" })
            {
                // What a double click in the tree (and a click on a search hit) does.
                shell.OpenFileFromPanel(Path.Combine(dir, name));
                var item = project.Items.Single(i => i.FilePath == Path.Combine(dir, name));
                var tab = Assert.IsType<FileTabView>(registry.GetOrCreate(project, item));
                var model = (FileTabViewModel)tab.DataContext!;
                Assert.Equal(FileKind.Image, model.Kind);
                var image = Assert.IsType<ImageViewerModel>(model.Viewer);
                await FileTabHarness.WaitUntil(() => image.Image is not null);
                Assert.Null(image.Notice);
            }
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public async Task The_zoom_buttons_are_in_the_bar_and_only_while_there_is_a_picture()
    {
        using var h = await OpenImage(TestImages.Png(300, 200));
        Assert.Contains(ImageView(h).GetVisualDescendants(), d => d is TextBlock { Text: "1:1" });
        using var broken = await OpenImage("x"u8.ToArray(), "bad.png");
        var button = broken.View.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "1:1");
        Assert.False(button.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task A_picture_longer_than_the_memory_limit_is_decoded_smaller_but_still_counts_as_its_own_size()
    {
        using var h = await OpenImage(TestImages.Jpeg(6000, 1500), "wide.jpg");
        var model = Image(h);
        Assert.NotNull(model.Image);
        // Kept at 4096 on the long side, in proportion...
        Assert.Equal(new PixelSize(ImageDecoders.MaxStillSide, 1024), model.Image!.PixelSize);
        // ... while the bar and the zoom talk about the real picture.
        Assert.Equal(new ImageInfo(ImageFormat.Jpeg, 6000, 1500), model.Info);
        Assert.Contains("6,000", model.MetaText);
        model.ActualSizeCommand.Execute(null);
        Assert.Equal("100%", model.ZoomText);
        Assert.Equal(6000, model.Viewport!.ImageRect.Width, 1);
        Assert.Equal(1500, model.Viewport.ImageRect.Height, 1);
    }

    [AvaloniaFact]
    public async Task A_tall_picture_is_shrunk_by_its_height()
    {
        using var h = await OpenImage(TestImages.Png(1200, 5000), "tall.png");
        var model = Image(h);
        Assert.Equal(ImageDecoders.MaxStillSide, model.Image!.PixelSize.Height);
        Assert.Equal(new ImageInfo(ImageFormat.Png, 1200, 5000), model.Info);
    }

    [Fact]
    public void Fit_leaves_a_small_size_alone_and_keeps_the_proportions_of_a_big_one()
    {
        Assert.Equal(new PixelSize(4096, 4096), ImageDecoders.Fit(4096, 4096, 4096));
        Assert.Equal(new PixelSize(4096, 2048), ImageDecoders.Fit(8192, 4096, 4096));
        Assert.Equal(new PixelSize(1, 4096), ImageDecoders.Fit(1, 40960, 4096));
        Assert.Equal(new PixelSize(1280, 720), ImageDecoders.Fit(1920, 1080, 1280));
    }
}
