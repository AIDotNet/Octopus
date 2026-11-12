using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Octopus.App.Composition;
using Octopus.App.Files;
using Octopus.App.Files.Documents;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Core.Projects;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests.Files;

public class MarkdownViewerTests
{
    private const string Plain = "# Title\n\nSome **bold** text.\n\n- one\n- two\n";
    private const string FrontMatter = "---\ntitle: Hello\ntags: [a, b]\n---\n\n# Doc\n\nbody text\n";
    private const string Wrapped = "A paragraph that was wrapped by hand\nat a fixed column, like many READMEs.\n\n| a | b |\n|---|---|\n| 1 | 2 |\n";

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    // ---- opening and looking never writes ------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData("plain")]
    [InlineData("front-matter")]
    [InlineData("wrapped")]
    [InlineData("crlf-bom")]
    [InlineData("no-final-newline")]
    [InlineData("not-utf8")]
    public async Task Opening_and_closing_a_file_changes_not_one_byte_nor_the_modification_time(string which)
    {
        var bytes = which switch
        {
            "plain" => Encoding.UTF8.GetBytes(Plain),
            "front-matter" => Encoding.UTF8.GetBytes(FrontMatter),
            "wrapped" => Encoding.UTF8.GetBytes(Wrapped),
            "crlf-bom" => Bom.Concat(Encoding.UTF8.GetBytes(Plain.Replace("\n", "\r\n"))).ToArray(),
            "no-final-newline" => Encoding.UTF8.GetBytes("# T\n\nno newline at the end"),
            _ => [0x23, 0x20, 0x54, 0xFF, 0xFE, 0x0A],
        };
        using var h = await FileTabHarness.OpenAsync(bytes);
        var modified = File.GetLastWriteTimeUtc(h.Path);
        h.Settle(500);
        // Even the save shortcut and a look at the source do nothing to a file that was not edited.
        h.PressSave();
        h.Markdown.ViewMode = MarkdownViewerModel.SourceMode;
        h.Settle(100);
        h.Markdown.ViewMode = MarkdownViewerModel.RenderedMode;
        h.Settle(100);
        Assert.False(h.Markdown.HasUnsavedChanges);
        Assert.Equal(0, h.Dialogs.Asked);
        h.Window.Close();
        h.Tab.Dispose();

        Assert.Equal(bytes, h.Disk);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(h.Path));
        Assert.Equal(["doc.md"], Directory.GetFileSystemEntries(h.Directory).Select(Path.GetFileName));
    }

    // ---- what opens how ------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task A_markdown_file_the_editor_keeps_exactly_opens_in_the_editor_with_the_rendered_text()
    {
        using var h = await FileTabHarness.OpenAsync(Plain);
        Assert.Equal(FileKind.Markdown, h.Tab.Kind);
        Assert.True(h.Markdown.IsEditing);
        Assert.Equal(MarkdownBanner.None, h.Markdown.Banner);
        Assert.True(h.Markdown.ShowEditor);
        Assert.False(h.Markdown.ShowPreview);
        Assert.NotNull(h.MarkdownView.EditorScrollArea);
        Assert.Contains("Some **bold** text.", h.Editor.GetMarkdown());
        Assert.True(h.Editor.IsEffectivelyVisible);
        Assert.False(h.Markdown.IsDirty);
        Assert.False(h.Editor.CanUndo, "undo must not be able to step back past the loaded file");
        Assert.Contains(h.View.GetVisualDescendants(), d => d is SegmentedControl);
    }

    [AvaloniaFact]
    public async Task A_file_with_content_the_editor_would_lose_opens_as_a_read_only_preview_with_an_explanation()
    {
        using var h = await FileTabHarness.OpenAsync(FrontMatter);
        Assert.False(h.Markdown.IsEditing);
        Assert.True(h.Markdown.CanEdit);
        Assert.Equal(MarkdownBanner.ReadOnlyLossy, h.Markdown.Banner);
        Assert.Contains(MarkdownViewerModel.Describe(MarkdownIssue.FrontMatter), h.Markdown.BannerText);
        Assert.True(h.Markdown.ShowPreview);
        Assert.False(h.Markdown.ShowEditor);
        Assert.Null(h.MarkdownView.Editor);
        Assert.False(h.Markdown.CanSave);
        // The same text still shows as the file has it.
        Assert.Equal(FrontMatter, h.Markdown.Text);
    }

    [AvaloniaFact]
    public async Task Edit_anyway_opens_the_editor_and_the_first_save_names_what_will_change()
    {
        using var h = await FileTabHarness.OpenAsync(FrontMatter);
        h.Markdown.EditAnywayCommand.Execute(null);
        h.Settle(100);
        Assert.True(h.Markdown.IsEditing);
        Assert.Equal(MarkdownBanner.None, h.Markdown.Banner);
        Assert.NotNull(h.MarkdownView.Editor);

        // Declined: nothing is written.
        h.Type("X");
        Assert.True(h.Markdown.IsDirty);
        h.Dialogs.Answer = false;
        Assert.False(await h.Tab.SaveAsync());
        Assert.Equal(1, h.Dialogs.Asked);
        Assert.Contains(MarkdownViewerModel.Describe(MarkdownIssue.FrontMatter), h.Dialogs.LastDescription);
        Assert.Equal(Encoding.UTF8.GetBytes(FrontMatter), h.Disk);

        // Confirmed once: it saves, and a second save does not ask again.
        h.Dialogs.Answer = true;
        Assert.True(await h.Tab.SaveAsync());
        Assert.Equal(2, h.Dialogs.Asked);
        Assert.Contains("X", Encoding.UTF8.GetString(h.Disk));
        h.Type("Y");
        Assert.True(await h.Tab.SaveAsync());
        Assert.Equal(2, h.Dialogs.Asked);
    }

    [AvaloniaFact]
    public async Task A_file_that_only_needs_reformatting_opens_in_the_editor_and_the_first_save_says_so()
    {
        using var h = await FileTabHarness.OpenAsync(Wrapped);
        Assert.True(h.Markdown.IsEditing);
        Assert.True(h.Markdown.Fidelity!.Reformats);
        Assert.False(h.Markdown.Fidelity.HasLoss);
        h.Type("Z");
        Assert.True(await h.Tab.SaveAsync());
        Assert.Equal(1, h.Dialogs.Asked);
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.MdSaveReformatTitle), h.Dialogs.LastTitle);
    }

    [AvaloniaFact]
    public async Task A_large_file_and_a_file_that_is_not_utf8_open_read_only_with_no_way_to_edit_them()
    {
        var large = "# Big\n\n" + string.Concat(Enumerable.Repeat("A line of text in a big document.\n\n", 20000)); // about 700 KB
        using var big = await FileTabHarness.OpenAsync(large);
        Assert.False(big.Markdown.CanEdit);
        Assert.False(big.Markdown.IsEditing);
        Assert.Equal(MarkdownBanner.ReadOnlyTooLarge, big.Markdown.Banner);
        Assert.True(big.Markdown.ShowPreview);
        big.Markdown.EditAnywayCommand.Execute(null);
        Assert.False(big.Markdown.IsEditing);

        using var latin = await FileTabHarness.OpenAsync(Encoding.Latin1.GetBytes("# café\n"));
        Assert.Equal(MarkdownBanner.ReadOnlyNotUtf8, latin.Markdown.Banner);
        Assert.False(latin.Markdown.CanEdit);
    }

    [AvaloniaFact]
    public async Task A_file_over_the_viewing_limit_and_a_missing_file_say_so()
    {
        var huge = new byte[(int)MarkdownViewerModel.MaxBytes + 10];
        Array.Fill(huge, (byte)'a');
        using var h = await FileTabHarness.OpenAsync(huge);
        Assert.True(h.Markdown.HasNotice);
        Assert.False(h.Markdown.ShowEditor);

        using var gone = await FileTabHarness.OpenAsync("# x\n");
        File.Delete(gone.Path);
        await gone.Markdown.OnFileChangedAsync();
        Assert.True(gone.Markdown.HasNotice);
    }

    // ---- editing and saving --------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Typing_marks_the_document_unsaved_and_the_save_shortcut_writes_it()
    {
        using var h = await FileTabHarness.OpenAsync(Plain);
        h.Type("Hello ");
        Assert.True(h.Markdown.IsDirty);
        Assert.True(h.Tab.HasUnsavedChanges);
        Assert.True(h.Markdown.ShowUnsaved);
        Assert.Equal(Encoding.UTF8.GetBytes(Plain), h.Disk);

        h.PressSave();
        await FileTabHarness.WaitUntil(() => !h.Markdown.IsDirty);
        var written = Encoding.UTF8.GetString(h.Disk);
        Assert.Contains("Hello ", written);
        Assert.StartsWith("# ", written.TrimStart('H', 'e', 'l', 'o', ' '));
        Assert.EndsWith("\n", written);
        Assert.False(h.Tab.HasUnsavedChanges);
        Assert.Equal(["doc.md"], Directory.GetFileSystemEntries(h.Directory).Select(Path.GetFileName));
        Assert.Equal(written.Replace("\r\n", "\n"), h.Markdown.Text);
    }

    [AvaloniaFact]
    public async Task Saving_keeps_the_crlf_line_endings_the_bom_and_the_final_newline()
    {
        var original = Bom.Concat(Encoding.UTF8.GetBytes(Plain.Replace("\n", "\r\n"))).ToArray();
        using var h = await FileTabHarness.OpenAsync(original);
        Assert.True(h.Markdown.IsEditing);
        h.Type("Edited ");
        Assert.True(await h.Tab.SaveAsync());
        var disk = h.Disk;
        Assert.Equal(Bom, disk[..3]);
        var text = Encoding.UTF8.GetString(disk, 3, disk.Length - 3);
        Assert.Contains("Edited ", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
        Assert.EndsWith("\r\n", text);
    }

    [AvaloniaFact]
    public async Task Saving_a_file_that_had_no_final_newline_adds_none()
    {
        using var h = await FileTabHarness.OpenAsync("# T\n\nno newline at the end");
        h.Type("!");
        Assert.True(await h.Tab.SaveAsync());
        var text = Encoding.UTF8.GetString(h.Disk);
        Assert.Contains("!", text);
        Assert.False(text.EndsWith('\n'));
    }

    [AvaloniaFact]
    public async Task An_edit_that_is_undone_leaves_nothing_to_save_and_the_file_alone()
    {
        using var h = await FileTabHarness.OpenAsync(Plain);
        var modified = File.GetLastWriteTimeUtc(h.Path);
        h.Type("temporary");
        Assert.True(h.Markdown.IsDirty);
        for (var i = 0; i < 10 && h.Editor.CanUndo; i++) h.Editor.Undo();
        Frames.Pump();
        Assert.False(h.Markdown.IsDirty);
        Assert.False(h.Tab.HasUnsavedChanges);
        h.PressSave();
        Assert.Equal(Encoding.UTF8.GetBytes(Plain), h.Disk);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(h.Path));
    }

    [AvaloniaFact]
    public async Task Saving_through_the_header_button_works_like_the_shortcut_and_the_view_switch_shows_the_edited_markdown()
    {
        using var h = await FileTabHarness.OpenAsync(Plain);
        h.Type("Typed ");
        h.Markdown.ViewMode = MarkdownViewerModel.SourceMode;
        Frames.Pump();
        Assert.True(h.Markdown.ShowSource);
        Assert.Contains("Typed ", h.Markdown.SourceText);
        Assert.False(h.Markdown.ShowEditor);
        Assert.Equal(Encoding.UTF8.GetBytes(Plain), h.Disk);

        h.Markdown.ViewMode = MarkdownViewerModel.RenderedMode;
        Frames.Pump();
        Assert.True(h.Markdown.IsDirty, "the edits are still there after looking at the source");
        h.Markdown.SaveDocumentCommand.Execute(null);
        await FileTabHarness.WaitUntil(() => !h.Markdown.IsDirty);
        Assert.Contains("Typed ", Encoding.UTF8.GetString(h.Disk));
    }

    [AvaloniaFact]
    public async Task The_source_view_of_an_unedited_file_is_the_file_as_it_is()
    {
        using var h = await FileTabHarness.OpenAsync(Wrapped);
        h.Markdown.ViewMode = MarkdownViewerModel.SourceMode;
        Assert.Equal(Wrapped, h.Markdown.SourceText);
    }

    [AvaloniaFact]
    public async Task A_failed_save_says_so_and_keeps_the_edits()
    {
        using var h = await FileTabHarness.OpenAsync(Plain);
        h.Type("keep me ");
        // The folder goes away: the temporary file cannot be created.
        var moved = h.Directory + "-gone";
        Directory.Move(h.Directory, moved);
        try
        {
            Assert.False(await h.Tab.SaveAsync());
            Assert.Equal(MarkdownBanner.SaveFailed, h.Markdown.Banner);
            Assert.True(h.Markdown.IsDirty);
        }
        finally
        {
            Directory.Move(moved, h.Directory);
        }
        Assert.True(await h.Tab.SaveAsync());
        Assert.Contains("keep me ", Encoding.UTF8.GetString(h.Disk));
    }

    // ---- the file changes on disk --------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task A_change_on_disk_with_no_edits_reloads_into_the_editor()
    {
        using var h = await FileTabHarness.OpenAsync(Plain);
        File.WriteAllText(h.Path, "# Changed outside\n\nnew words\n");
        await h.Markdown.OnFileChangedAsync();
        h.Settle(100);
        Assert.Equal(MarkdownBanner.None, h.Markdown.Banner);
        Assert.Contains("new words", h.Editor.GetMarkdown());
        Assert.Contains("Changed outside", h.Markdown.Text);
        Assert.False(h.Markdown.IsDirty);
    }

    [AvaloniaFact]
    public async Task The_file_watcher_reloads_an_unedited_document_by_itself()
    {
        using var h = await FileTabHarness.OpenAsync(Plain);
        await Task.Delay(500);
        File.WriteAllText(h.Path, "# Watched\n\nfrom the watcher\n");
        await FileTabHarness.WaitUntil(() => h.Markdown.Text.Contains("from the watcher"), 8000);
        Assert.Contains("from the watcher", h.Editor.GetMarkdown());
    }

    [AvaloniaFact]
    public async Task A_change_on_disk_under_unsaved_edits_is_not_applied_and_asks_which_to_keep()
    {
        using var h = await FileTabHarness.OpenAsync(Plain);
        h.Type("mine ");
        File.WriteAllText(h.Path, "# Theirs\n\nsomeone else wrote this\n");
        await h.Markdown.OnFileChangedAsync();
        Assert.Equal(MarkdownBanner.DiskChanged, h.Markdown.Banner);
        Assert.True(h.Markdown.BannerIsDiskChanged);
        Assert.Contains("mine ", h.Editor.GetMarkdown());
        Assert.DoesNotContain("someone else", h.Editor.GetMarkdown());
        Assert.True(h.Markdown.IsDirty);

        // Reload (discard my edits): the file replaces the document.
        h.Markdown.ReloadFromDiskCommand.Execute(null);
        h.Settle(100);
        Assert.Equal(MarkdownBanner.None, h.Markdown.Banner);
        Assert.False(h.Markdown.IsDirty);
        Assert.Contains("someone else wrote this", h.Editor.GetMarkdown());
        Assert.DoesNotContain("mine ", h.Editor.GetMarkdown());
    }

    [AvaloniaFact]
    public async Task Keeping_my_edits_dismisses_the_notice_and_the_next_save_overwrites_the_file()
    {
        using var h = await FileTabHarness.OpenAsync(Plain);
        h.Type("mine ");
        File.WriteAllText(h.Path, "# Theirs\n\nsomeone else wrote this\n");
        await h.Markdown.OnFileChangedAsync();
        Assert.Equal(MarkdownBanner.DiskChanged, h.Markdown.Banner);

        h.Markdown.KeepMineCommand.Execute(null);
        Assert.Equal(MarkdownBanner.None, h.Markdown.Banner);
        Assert.True(h.Markdown.IsDirty);
        Assert.Contains("mine ", h.Editor.GetMarkdown());
        // The same change again does not bring the notice back.
        await h.Markdown.OnFileChangedAsync();
        Assert.Equal(MarkdownBanner.None, h.Markdown.Banner);

        Assert.True(await h.Tab.SaveAsync());
        Assert.Contains("mine ", Encoding.UTF8.GetString(h.Disk));
        Assert.DoesNotContain("someone else", Encoding.UTF8.GetString(h.Disk));
    }

    [AvaloniaFact]
    public async Task Saving_while_the_notice_is_open_asks_before_overwriting()
    {
        using var h = await FileTabHarness.OpenAsync(Plain);
        h.Type("mine ");
        File.WriteAllText(h.Path, "# Theirs\n");
        await h.Markdown.OnFileChangedAsync();
        h.Dialogs.Answer = false;
        Assert.False(await h.Tab.SaveAsync());
        Assert.Equal(1, h.Dialogs.Asked);
        Assert.Equal("# Theirs\n", Encoding.UTF8.GetString(h.Disk));
        h.Dialogs.Answer = true;
        Assert.True(await h.Tab.SaveAsync());
        Assert.Contains("mine ", Encoding.UTF8.GetString(h.Disk));
    }

    [AvaloniaFact]
    public async Task The_save_of_this_tab_is_not_mistaken_for_a_change_by_someone_else()
    {
        using var h = await FileTabHarness.OpenAsync(Plain);
        h.Type("mine ");
        Assert.True(await h.Tab.SaveAsync());
        h.Type("more ");
        await h.Markdown.OnFileChangedAsync(); // what the watcher reports after our own write: the same bytes
        Assert.Equal(MarkdownBanner.None, h.Markdown.Banner);
        Assert.True(h.Markdown.IsDirty);
        Assert.Contains("more ", h.Editor.GetMarkdown());
    }

    // ---- closing and quitting ------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Closing_a_tab_with_unsaved_edits_asks_and_a_clean_one_does_not()
    {
        var project = AppHost.Get<IProjectService>().AddProject(Directory.CreateTempSubdirectory("octopus-close-").FullName);
        var path = Path.Combine(project.Path, "doc.md");
        File.WriteAllText(path, Plain);
        var item = AppHost.Get<IItemOpener>().OpenFile(path)!;
        var registry = AppHost.Get<ITabContentRegistry>();
        var tab = (FileTabView)registry.GetOrCreate(project, item);
        var window = new Window { Width = 900, Height = 600, Content = tab };
        window.Show();
        var model = (FileTabViewModel)tab.DataContext!;
        await FileTabHarness.WaitUntil(() => model.Viewer is MarkdownViewerModel { IsEditing: true });
        var confirmation = AppHost.Get<CloseConfirmation>();
        var dialogs = AppHost.Get<FakeDialogService>();

        Assert.True(await confirmation.ConfirmCloseTabAsync(item.Id));
        Assert.True(await confirmation.ConfirmQuitAsync());
        Assert.Equal(0, dialogs.Asked);

        var md = (MarkdownViewerModel)model.Viewer;
        md.ReadEditor = () => "# Edited\n";
        md.EditorChanged();
        Assert.True(model.HasUnsavedChanges);
        Assert.True(await tab.HasUnsavedChangesAsync());

        dialogs.Answer = false;
        Assert.False(await confirmation.ConfirmCloseTabAsync(item.Id));
        Assert.False(await confirmation.ConfirmQuitAsync());
        Assert.Equal(2, dialogs.Asked);
        dialogs.Answer = true;
        Assert.True(await confirmation.ConfirmCloseTabAsync(item.Id));
        Assert.True(await confirmation.ConfirmQuitAsync());
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_tab_with_unsaved_edits_shows_a_dot_before_its_title()
    {
        var window = new Octopus.App.Shell.MainWindow();
        window.Show();
        var project = AppHost.Get<IProjectService>().AddProject(Directory.CreateTempSubdirectory("octopus-dot-").FullName);
        var path = Path.Combine(project.Path, "doc.md");
        File.WriteAllText(path, Plain);
        var item = AppHost.Get<IItemOpener>().OpenFile(path)!;
        Frames.Pump();
        var layout = (DockLayout)AppHost.Get<WorkspaceViewModel>().CurrentLayout!;
        var pane = layout.Workspace.FindPane(item.Id)!;
        var tab = (FileTabView)pane.Content!;
        var model = (FileTabViewModel)tab.DataContext!;
        await FileTabHarness.WaitUntil(() => model.Viewer is MarkdownViewerModel { IsEditing: true });
        Assert.Equal("doc.md", pane.Title);

        var md = (MarkdownViewerModel)model.Viewer;
        md.ReadEditor = () => "# Edited\n";
        md.EditorChanged();
        Assert.StartsWith("● ", pane.Title);
        Assert.EndsWith("doc.md", pane.Title);

        md.ReadEditor = () => md.Fidelity!.Exported;
        md.EditorChanged();
        Assert.Equal("doc.md", pane.Title);
        window.Close();
    }

    // ---- pictures inside the document ----------------------------------------------------------------------------

    [Theory]
    [InlineData("./img.png", true)]
    [InlineData("img.png", true)]
    [InlineData("../shared/img.png", true)]
    [InlineData("sub%20dir/my%20pic.png", true)]
    [InlineData("img.png#frag", true)]
    [InlineData("https://example.com/a.png", false)]
    [InlineData("data:image/png;base64,AAAA", false)]
    [InlineData("avares://x/y.png", false)]
    [InlineData("file:///tmp/a.png", false)]
    [InlineData("", false)]
    public void A_relative_picture_address_starts_at_the_markdown_files_folder(string source, bool resolved)
    {
        var folder = Path.Combine(Path.GetTempPath(), "octopus-docs", "notes");
        var path = MarkdownImagePath.Resolve(folder, source);
        Assert.Equal(resolved, path is not null);
        if (path is null) return;
        Assert.True(Path.IsPathRooted(path));
        if (source.StartsWith("./")) Assert.Equal(Path.Combine(folder, "img.png"), path);
        if (source.StartsWith("../")) Assert.Equal(Path.Combine(Path.GetTempPath(), "octopus-docs", "shared", "img.png"), path);
        if (source.StartsWith("sub%20")) Assert.EndsWith(Path.Combine("sub dir", "my pic.png"), path);
    }

    [Fact]
    public void A_picture_file_is_opened_when_it_exists_and_is_not_too_large()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-mdimg-").FullName;
        try
        {
            var path = Path.Combine(dir, "a.png");
            File.WriteAllBytes(path, TestImages.Png(8, 8));
            using (var stream = MarkdownImagePath.Open(path)) Assert.NotNull(stream);
            Assert.Null(MarkdownImagePath.Open(Path.Combine(dir, "missing.png")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
