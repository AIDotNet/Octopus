using System.Text;
using Avalonia.Headless.XUnit;
using Octopus.App.Files.Documents;
using Octopus.App.Files.Images;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests.Files;

/// <summary>Renders the Markdown editor and the image viewer with Skia on the headless platform and saves PNGs to <c>bin/shots</c> for a person to look at.</summary>
public class FileScreenshots
{
    private static readonly string s_dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "shots"));

    private const string Document = """
        # Octopus roadmap

        A **terminal** and project manager for *coding agents*. Type `#` and a space for a heading, `-` for a list, or `/` for blocks; see the [docs](https://example.com/docs).

        ## Checklist

        - [x] Rendered editing
        - [ ] Saving with the shortcut
        - [ ] Pictures next to the document

        ## Steps

        1. Open the file
        2. Edit it in place
           - nested item
           - another one

        > Opening a file never changes it. Only an edit and a save do.

        ```csharp
        var path = "notes.md";
        File.WriteAllText(path, editor.GetMarkdown());
        ```

        | Name | Status | Notes |
        |------|:------:|-------|
        | Editor | done | Arc rich text editor |
        | Images | done | relative paths work |

        ![A generated diagram](./pics/diagram.png)

        The end of the document, with enough text after the picture to see the spacing below it.
        """;

    [AvaloniaFact]
    public async Task Render_the_markdown_editor_and_its_states()
    {
        Directory.CreateDirectory(s_dir);
        foreach (var dark in new[] { false, true })
        {
            ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
            var suffix = dark ? "dark" : "light";

            using (var h = await Open(Document, "roadmap.md", 1100, 760))
            {
                h.Settle(1200);
                Frames.Save(h.Window, Path.Combine(s_dir, $"file-md-editor-{suffix}.png"));

                h.Type("Edited: ");
                h.Settle(400);
                Frames.Save(h.Window, Path.Combine(s_dir, $"file-md-unsaved-{suffix}.png"));

                File.WriteAllText(h.Path, "# Changed outside\n\nSomeone else saved this file while it was open here.\n");
                await h.Markdown.OnFileChangedAsync();
                h.Settle(700);
                Frames.Save(h.Window, Path.Combine(s_dir, $"file-md-disk-changed-{suffix}.png"));

                h.Markdown.KeepMineCommand.Execute(null);
                h.Markdown.ViewMode = MarkdownViewerModel.SourceMode;
                h.Settle(500);
                Frames.Save(h.Window, Path.Combine(s_dir, $"file-md-source-{suffix}.png"));
            }

            using (var h = await Open("---\ntitle: Release notes\ntags: [release, notes]\n---\n\n# Release 1.4\n\nThe front matter above cannot be kept by the editor, so this file opens as a read-only preview.\n\n<div align=\"center\">raw html</div>\n\n- one\n- two\n", "release.md", 1100, 520))
            {
                h.Settle(900);
                Frames.Save(h.Window, Path.Combine(s_dir, $"file-md-readonly-{suffix}.png"));
            }

            using (var h = await Open("# Narrow\n\nThe bar must not crowd at this width.\n", "a-long-file-name-for-the-narrow-case.md", 640, 360))
            {
                h.Type("x");
                h.Settle(500);
                Frames.Save(h.Window, Path.Combine(s_dir, $"file-md-narrow-{suffix}.png"));
            }
        }
    }

    private static async Task<FileTabHarness> Open(string markdown, string name, double width, double height) =>
        await FileTabHarness.OpenAsync(Encoding.UTF8.GetBytes(markdown), name, width, height, beforeOpen: dir =>
        {
            Directory.CreateDirectory(Path.Combine(dir, "pics"));
            File.WriteAllBytes(Path.Combine(dir, "pics", "diagram.png"), TestImages.Png(640, 280));
        });

    [AvaloniaFact]
    public async Task Render_the_image_viewer_and_its_states()
    {
        Directory.CreateDirectory(s_dir);
        foreach (var dark in new[] { false, true })
        {
            ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
            var suffix = dark ? "dark" : "light";

            using (var h = await Image(TestImages.Png(3200, 2400), "photo.png"))
            {
                Frames.Save(h.Window, Path.Combine(s_dir, $"file-image-fit-{suffix}.png"));
                var model = (ImageViewerModel)h.Tab.Viewer;
                model.ActualSizeCommand.Execute(null);
                model.ZoomInCommand.Execute(null);
                model.ZoomInCommand.Execute(null);
                model.Pan(new Avalonia.Vector(-500, -300));
                h.Settle(300);
                Frames.Save(h.Window, Path.Combine(s_dir, $"file-image-zoomed-{suffix}.png"));
            }
            using (var h = await Image(TestImages.Ico(48), "favicon.ico", 1100, 520))
            {
                Frames.Save(h.Window, Path.Combine(s_dir, $"file-image-icon-{suffix}.png"));
            }
            using (var h = await Image(TestImages.Png(96, 64), "small.png", 1100, 520))
            {
                var model = (ImageViewerModel)h.Tab.Viewer;
                model.ZoomInCommand.Execute(null);
                model.ZoomInCommand.Execute(null);
                model.ZoomInCommand.Execute(null);
                h.Settle(300);
                Frames.Save(h.Window, Path.Combine(s_dir, $"file-image-small-zoomed-{suffix}.png"));
            }
            using (var h = await Image(TestImages.Png(300, 200)[..70], "broken.png", 1100, 360))
            {
                Frames.Save(h.Window, Path.Combine(s_dir, $"file-image-broken-{suffix}.png"));
            }
            using (var h = await Image(TestImages.PngHeaderOnly(20000, 20000), "huge.png", 1100, 360))
            {
                Frames.Save(h.Window, Path.Combine(s_dir, $"file-image-huge-{suffix}.png"));
            }
            using (var h = await Image(TestImages.Png(1600, 900), "a-long-image-name-for-the-narrow-case.png", 560, 380))
            {
                Frames.Save(h.Window, Path.Combine(s_dir, $"file-image-narrow-{suffix}.png"));
            }
        }
    }

    private static async Task<FileTabHarness> Image(byte[] bytes, string name, double width = 1100, double height = 760)
    {
        var h = await FileTabHarness.OpenAsync(bytes, name, width, height);
        var model = (ImageViewerModel)h.Tab.Viewer;
        await FileTabHarness.WaitUntil(() => model.Image is not null || model.HasNotice);
        h.Settle(400);
        return h;
    }
}
