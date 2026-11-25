using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Files;
using Octopus.App.Files.Code;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Core.Projects;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests.Files;

public class CodeViewerTests
{
    private const string SampleCs = "using System;\n\nclass Program\n{\n    static void Main() { }\n}\n";
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    // ---- CodeFileText encoding & decoding -----------------------------------------------------------------------

    [Fact]
    public void CodeFileText_round_trips_lf_crlf_bom_and_exact_trailing_newlines()
    {
        var crlfBom = Bom.Concat(Encoding.UTF8.GetBytes("a\r\nb\r\n\r\n")).ToArray();
        Assert.True(CodeFileText.TryDecode(crlfBom, out var doc));
        Assert.True(doc.HasBom);
        Assert.Equal("\r\n", doc.NewLine);
        Assert.Equal("a\nb\n\n", doc.Text);
        Assert.Equal(crlfBom, doc.Encode(doc.Text));

        var noNewline = Encoding.UTF8.GetBytes("line1\nline2");
        Assert.True(CodeFileText.TryDecode(noNewline, out var doc2));
        Assert.Equal(noNewline, doc2.Encode("line1\nline2"));
        Assert.Equal(Encoding.UTF8.GetBytes("line1\nline2\n"), doc2.Encode("line1\nline2\n"));

        Assert.False(CodeFileText.TryDecode(Encoding.Latin1.GetBytes("café"), out _));
    }

    [Theory]
    [InlineData("Program.cs", "csharp")]
    [InlineData("view.axaml", "xml")]
    [InlineData("Octopus.csproj", "xml")]
    [InlineData("app.tsx", "typescript")]
    [InlineData("index.mjs", "javascript")]
    [InlineData("main.py", "python")]
    [InlineData("lib.rs", "rust")]
    [InlineData("run.zsh", "shell")]
    [InlineData("Dockerfile", "dockerfile")]
    [InlineData("notes.txt", "plaintext")]
    [InlineData("LICENSE", "plaintext")]
    public void File_names_map_to_monaco_language_ids(string name, string expected) =>
        Assert.Equal(expected, FileLanguages.ForCodeEditor(name));

    // ---- opening and looking never writes -----------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData("plain")]
    [InlineData("crlf-bom")]
    [InlineData("no-final-newline")]
    [InlineData("not-utf8")]
    public async Task Opening_and_closing_a_code_file_never_writes_or_changes_mtime(string which)
    {
        var bytes = which switch
        {
            "plain" => Encoding.UTF8.GetBytes(SampleCs),
            "crlf-bom" => Bom.Concat(Encoding.UTF8.GetBytes(SampleCs.Replace("\n", "\r\n"))).ToArray(),
            "no-final-newline" => Encoding.UTF8.GetBytes("const x = 1;"),
            _ => Encoding.Latin1.GetBytes("// café\n"),
        };
        using var h = await FileTabHarness.OpenAsync(bytes, "sample.cs");
        var modified = File.GetLastWriteTimeUtc(h.Path);
        h.Settle(250);
        h.PressSave();
        Assert.False(h.Code.HasUnsavedChanges);
        Assert.Equal(0, h.Dialogs.Asked);
        h.Window.Close();
        h.Tab.Dispose();

        Assert.Equal(bytes, h.Disk);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(h.Path));
        Assert.Equal(["sample.cs"], Directory.GetFileSystemEntries(h.Directory).Select(Path.GetFileName));
    }

    // ---- editing and saving -------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Typing_in_code_editor_marks_dirty_and_save_shortcut_writes_atomically()
    {
        using var h = await FileTabHarness.OpenAsync(SampleCs, "Program.cs");
        Assert.Equal(FileKind.Code, h.Tab.Kind);
        Assert.True(h.Code.IsEditing);
        Assert.False(h.CodeEditor.IsReadOnly);
        Assert.Equal("csharp", h.CodeEditor.LanguageId);
        Assert.False(h.Code.IsDirty);

        h.TypeCode("// header\n");
        Assert.True(h.Code.IsDirty);
        Assert.True(h.Tab.HasUnsavedChanges);
        Assert.True(h.Code.ShowUnsaved);
        Assert.StartsWith("// header\n", h.CodeEditor.Text);
        Assert.Equal(Encoding.UTF8.GetBytes(SampleCs), h.Disk);

        h.PressSave();
        await FileTabHarness.WaitUntil(() => !h.Code.IsDirty);
        var written = Encoding.UTF8.GetString(h.Disk);
        Assert.StartsWith("// header\n", written);
        Assert.False(h.Tab.HasUnsavedChanges);
        Assert.Equal(["Program.cs"], Directory.GetFileSystemEntries(h.Directory).Select(Path.GetFileName));
        Assert.Equal(written, h.Code.Text);
    }

    [AvaloniaFact]
    public async Task Saving_preserves_crlf_line_endings_and_bom()
    {
        var original = Bom.Concat(Encoding.UTF8.GetBytes(SampleCs.Replace("\n", "\r\n"))).ToArray();
        using var h = await FileTabHarness.OpenAsync(original, "Program.cs");
        h.TypeCode("// bom\n");
        Assert.True(await h.Tab.SaveAsync());
        var disk = h.Disk;
        Assert.Equal(Bom, disk[..3]);
        var text = Encoding.UTF8.GetString(disk, 3, disk.Length - 3);
        Assert.StartsWith("// bom\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    [AvaloniaFact]
    public async Task Undoing_all_edits_clears_dirty_state_without_touching_disk()
    {
        using var h = await FileTabHarness.OpenAsync(SampleCs, "Program.cs");
        var modified = File.GetLastWriteTimeUtc(h.Path);
        h.TypeCode("X");
        Assert.True(h.Code.IsDirty);

        h.PressUndo();
        await FileTabHarness.WaitUntil(() => !h.Code.IsDirty);
        Assert.False(h.Tab.HasUnsavedChanges);
        Assert.Equal(SampleCs, h.CodeEditor.Text);
        h.PressSave();
        Assert.Equal(Encoding.UTF8.GetBytes(SampleCs), h.Disk);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(h.Path));
    }

    [AvaloniaFact]
    public async Task Invalid_utf8_file_opens_read_only_with_info_banner()
    {
        using var h = await FileTabHarness.OpenAsync(Encoding.Latin1.GetBytes("// café\n"), "latin.cs");
        Assert.False(h.Code.IsEditing);
        Assert.True(h.Code.IsReadOnly);
        Assert.True(h.CodeEditor.IsReadOnly);
        Assert.False(h.Code.CanSave);
        Assert.Equal(CodeBanner.ReadOnlyNotUtf8, h.Code.Banner);
        Assert.True(h.Code.BannerIsInfo);
    }

    [AvaloniaFact]
    public async Task Failed_save_shows_banner_and_keeps_unsaved_edits()
    {
        using var h = await FileTabHarness.OpenAsync(SampleCs, "Program.cs");
        h.TypeCode("// keep\n");
        var moved = h.Directory + "-gone";
        Directory.Move(h.Directory, moved);
        try
        {
            Assert.False(await h.Tab.SaveAsync());
            Assert.Equal(CodeBanner.SaveFailed, h.Code.Banner);
            Assert.True(h.Code.BannerIsSaveFailed);
            Assert.True(h.Code.IsDirty);
        }
        finally
        {
            Directory.Move(moved, h.Directory);
        }
        Assert.True(await h.Tab.SaveAsync());
        Assert.Equal(CodeBanner.None, h.Code.Banner);
        Assert.StartsWith("// keep\n", Encoding.UTF8.GetString(h.Disk));
    }

    // ---- disk changes & conflicts -------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task External_change_with_unsaved_edits_shows_conflict_banner_and_supports_reload_or_keep()
    {
        using var h = await FileTabHarness.OpenAsync(SampleCs, "Program.cs");
        h.TypeCode("// mine\n");
        File.WriteAllText(h.Path, "// theirs\nclass B {}\n");
        await h.Code.OnFileChangedAsync();

        Assert.Equal(CodeBanner.DiskChanged, h.Code.Banner);
        Assert.True(h.Code.BannerIsDiskChanged);
        Assert.StartsWith("// mine\n", h.CodeEditor.Text);

        // Declining overwrite confirmation leaves disk untouched.
        h.Dialogs.Answer = false;
        Assert.False(await h.Tab.SaveAsync());
        Assert.Equal(1, h.Dialogs.Asked);
        Assert.Equal("// theirs\nclass B {}\n", Encoding.UTF8.GetString(h.Disk));

        // Keep mine dismisses banner and allows saving over disk without asking again.
        h.Code.KeepMineCommand.Execute(null);
        Assert.Equal(CodeBanner.None, h.Code.Banner);
        Assert.True(h.Code.IsDirty);
        Assert.True(await h.Tab.SaveAsync());
        Assert.StartsWith("// mine\n", Encoding.UTF8.GetString(h.Disk));

        // Self-save watcher notification is ignored even after further typing.
        h.TypeCode("// more\n");
        await h.Code.OnFileChangedAsync();
        Assert.Equal(CodeBanner.None, h.Code.Banner);
        Assert.StartsWith("// mine\n// more\n", h.CodeEditor.Text);

        // ReloadFromDisk discards local edits.
        File.WriteAllText(h.Path, "// fresh\n");
        await h.Code.OnFileChangedAsync();
        Assert.Equal(CodeBanner.DiskChanged, h.Code.Banner);
        h.Code.ReloadFromDiskCommand.Execute(null);
        Frames.Pump();
        Assert.Equal(CodeBanner.None, h.Code.Banner);
        Assert.False(h.Code.IsDirty);
        Assert.Equal("// fresh\n", h.CodeEditor.Text);
    }

    // ---- workspace integration (tab dot, close confirmation, responsive minimap) --------------------------------

    [AvaloniaFact]
    public async Task Dirty_code_tab_shows_dot_in_title_and_confirms_on_close()
    {
        var window = new Octopus.App.Shell.MainWindow();
        window.Show();
        var project = AppHost.Get<IProjectService>().AddProject(Directory.CreateTempSubdirectory("octopus-code-dot-").FullName);
        var path = Path.Combine(project.Path, "main.cs");
        File.WriteAllText(path, SampleCs);
        var item = AppHost.Get<IItemOpener>().OpenFile(path)!;
        Frames.Pump();
        var layout = (DockLayout)AppHost.Get<WorkspaceViewModel>().CurrentLayout!;
        var pane = layout.Workspace.FindPane(item.Id)!;
        var tab = (FileTabView)pane.Content!;
        var model = (FileTabViewModel)tab.DataContext!;
        await FileTabHarness.WaitUntil(() => model.Viewer is CodeViewerModel { IsEditing: true, LoadId: > 0 });
        Assert.Equal("main.cs", pane.Title);

        var confirmation = AppHost.Get<CloseConfirmation>();
        var dialogs = AppHost.Get<FakeDialogService>();
        Assert.True(await confirmation.ConfirmCloseTabAsync(item.Id));
        Assert.Equal(0, dialogs.Asked);

        var code = (CodeViewerModel)model.Viewer;
        code.ReadEditor = () => "// edited\n" + SampleCs;
        code.EditorChanged();
        Assert.StartsWith("● ", pane.Title);
        Assert.True(model.HasUnsavedChanges);

        dialogs.Answer = false;
        Assert.False(await confirmation.ConfirmCloseTabAsync(item.Id));
        Assert.Equal(1, dialogs.Asked);
        dialogs.Answer = true;
        Assert.True(await confirmation.ConfirmCloseTabAsync(item.Id));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Minimap_hides_in_narrow_view_and_shows_in_wide_view()
    {
        using var h = await FileTabHarness.OpenAsync(SampleCs, "Program.cs", width: 380, height: 400);
        Assert.False(h.CodeEditor.ShowMinimap);

        h.Window.Width = 760;
        Frames.Pump();
        Assert.True(h.CodeEditor.ShowMinimap);
    }
}
