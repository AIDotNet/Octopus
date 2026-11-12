using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.App.Files.Code;
using Octopus.App.Localization;

namespace Octopus.App.Files.Documents;

/// <summary>What the strip under the info bar says about a Markdown file, if anything.</summary>
public enum MarkdownBanner
{
    None,
    /// <summary>Opened read-only because the editor cannot keep everything in the file; offers "edit anyway".</summary>
    ReadOnlyLossy,
    /// <summary>Opened read-only because the file is large.</summary>
    ReadOnlyTooLarge,
    /// <summary>Read-only because the bytes are not valid UTF-8.</summary>
    ReadOnlyNotUtf8,
    /// <summary>The file changed on disk while there are unsaved edits.</summary>
    DiskChanged,
    /// <summary>Saving failed.</summary>
    SaveFailed,
}

/// <summary>
/// A Markdown file as a rendered document that can be edited in place (Arc's rich text editor), with the source as the
/// other view. Opening and looking never writes: a save happens only after an edit, atomically, in the file's own
/// line ending, final newline and byte order mark. A file the editor cannot keep intact opens as a read-only preview
/// (see <see cref="MarkdownFidelity"/>); one that changes on disk under unsaved edits asks which side to keep.
/// </summary>
public sealed partial class MarkdownViewerModel : FileViewerModel
{
    /// <summary>The largest file shown at all (as the code viewer).</summary>
    public const long MaxBytes = CodeViewerModel.MaxBytes;

    /// <summary>The largest file offered to the editor, which lays out every block: bigger ones are read-only.</summary>
    public const long MaxEditBytes = 512 * 1024;

    public const string RenderedMode = "rendered";
    public const string SourceMode = "source";

    /// <summary>Below this many characters the unsaved state follows every keystroke exactly; above it, after a pause.</summary>
    private const int ExactDirtyLimit = 64_000;

    private int _version;
    private int _dirtyVersion;
    private MarkdownFileText _document = MarkdownFileText.Empty;
    private byte[]? _diskBytes;
    private FileRead? _pendingDisk;
    private string? _baseline;
    private bool _editorLoading;
    private bool _saveConfirmed;
    private bool _forcedEdit;

    public MarkdownViewerModel(FileViewerContext context) : base(context)
    {
        MonoFontFamily = context.MonoFontFamily;
        FontSize = context.FontSize;
    }

    public string? MonoFontFamily { get; }
    public double FontSize { get; }

    /// <summary>The folder the file is in: where a relative picture address starts.</summary>
    public string FolderPath => System.IO.Path.GetDirectoryName(Path) ?? "";

    /// <summary>The view reads the editor's current Markdown through this (set by the view that owns the editor).</summary>
    public Func<string>? ReadEditor { get; set; }

    /// <summary>The file's text, as the file has it on disk (line endings made LF).</summary>
    [ObservableProperty] public partial string Text { get; private set; } = "";
    [ObservableProperty] public partial long FileSize { get; private set; }

    /// <summary>The text of the source view: the file's, or the editor's export while there are edits.</summary>
    [ObservableProperty] public partial string SourceText { get; private set; } = "";

    [ObservableProperty] public partial string ViewMode { get; set; } = RenderedMode;

    /// <summary>The rendered view is the editor (otherwise the read-only preview).</summary>
    [ObservableProperty] public partial bool IsEditing { get; private set; }

    /// <summary>The file may be edited at all: it is valid UTF-8 and not too large.</summary>
    [ObservableProperty] public partial bool CanEdit { get; private set; }

    [ObservableProperty] public partial MarkdownBanner Banner { get; private set; }
    [ObservableProperty] public partial string BannerText { get; private set; } = "";

    /// <summary>Edits are held that are not saved.</summary>
    [ObservableProperty] public partial bool IsDirty { get; private set; }

    /// <summary>What the editor is to load; the view loads it whenever <see cref="LoadId"/> changes.</summary>
    [ObservableProperty] public partial string EditorText { get; private set; } = "";
    [ObservableProperty] public partial int LoadId { get; private set; }

    public MarkdownFidelity? Fidelity { get; private set; }

    public bool ShowEditor => HasContent && IsEditing && ViewMode == RenderedMode;
    public bool ShowPreview => HasContent && !IsEditing && ViewMode == RenderedMode;
    public bool ShowSource => HasContent && ViewMode == SourceMode;
    public bool ShowUnsaved => IsDirty && IsEditing;
    public bool ShowSave => IsEditing;
    public bool BannerIsLossy => Banner == MarkdownBanner.ReadOnlyLossy;
    public bool BannerIsInfo => Banner is MarkdownBanner.ReadOnlyTooLarge or MarkdownBanner.ReadOnlyNotUtf8;
    public bool BannerIsDiskChanged => Banner == MarkdownBanner.DiskChanged;
    public bool BannerIsSaveFailed => Banner == MarkdownBanner.SaveFailed;
    public string LanguageName => "md";

    public string MetaText => CodeMeta.Describe("md", CodeMeta.CountLines(Text), FileSize);

    public override bool HasUnsavedChanges => IsDirty && IsEditing;
    public override bool CanSave => IsEditing;

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(MetaText));
        RefreshSource();
    }

    partial void OnFileSizeChanged(long value) => OnPropertyChanged(nameof(MetaText));
    partial void OnViewModeChanged(string value)
    {
        RefreshSource();
        RaiseShown();
    }

    partial void OnIsEditingChanged(bool value)
    {
        RaiseShown();
        OnPropertyChanged(nameof(ShowUnsaved));
        OnPropertyChanged(nameof(ShowSave));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(CanSave));
    }

    partial void OnIsDirtyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowUnsaved));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        SaveDocumentCommand.NotifyCanExecuteChanged();
    }

    partial void OnBannerChanged(MarkdownBanner value)
    {
        OnPropertyChanged(nameof(BannerIsLossy));
        OnPropertyChanged(nameof(BannerIsInfo));
        OnPropertyChanged(nameof(BannerIsDiskChanged));
        OnPropertyChanged(nameof(BannerIsSaveFailed));
    }

    protected override void NoticeChanged() => RaiseShown();

    private void RaiseShown()
    {
        OnPropertyChanged(nameof(ShowEditor));
        OnPropertyChanged(nameof(ShowPreview));
        OnPropertyChanged(nameof(ShowSource));
    }

    private void RefreshSource() =>
        SourceText = ViewMode == SourceMode && IsEditing && IsDirty && ReadEditor is { } read ? read() : Text;

    // ---- loading ------------------------------------------------------------------------------------------------

    public override async Task LoadAsync()
    {
        var version = ++_version;
        var read = await Task.Run(() => FileReader.Read(Path, MaxBytes));
        if (version != _version || Disposed) return;
        Apply(read);
    }

    /// <summary>The file changed on disk: its own write is ignored, a change over unsaved edits waits for a decision, any other is loaded.</summary>
    public override async Task OnFileChangedAsync()
    {
        var read = await Task.Run(() => FileReader.Read(Path, MaxBytes));
        if (Disposed) return;
        if (read.Bytes is { } bytes && _diskBytes is { } known && bytes.AsSpan().SequenceEqual(known)) return;
        if (!HasUnsavedChanges)
        {
            _version++;
            Apply(read);
            return;
        }
        _pendingDisk = read;
        SetBanner(MarkdownBanner.DiskChanged, "");
    }

    /// <summary>Takes a read of the file as the new truth: text, verdict on editing, what the view shows. Edits are gone.</summary>
    private void Apply(FileRead read)
    {
        _pendingDisk = null;
        _saveConfirmed = false;
        IsDirty = false;
        _baseline = null;
        if (read.Problem is { } problem)
        {
            _diskBytes = null;
            Text = "";
            FileSize = 0;
            IsEditing = false;
            CanEdit = false;
            SetBanner(MarkdownBanner.None, "");
            Notice = AppLangResources.Get(problem);
            return;
        }
        Notice = null;
        var bytes = read.Bytes!;
        _diskBytes = bytes;
        FileSize = bytes.Length;
        var utf8 = MarkdownFileText.TryDecode(bytes, out var document);
        if (!utf8) document = MarkdownFileText.Empty with { Text = Encoding.UTF8.GetString(bytes) };
        _document = document;
        Text = document.Text;
        CanEdit = utf8 && bytes.Length <= MaxEditBytes;
        Fidelity = CanEdit ? MarkdownFidelity.Assess(document.Text) : null;
        IsEditing = CanEdit && (_forcedEdit || Fidelity is { HasLoss: false });
        if (!CanEdit)
        {
            _forcedEdit = false;
            if (!utf8) SetBanner(MarkdownBanner.ReadOnlyNotUtf8, AppLangResources.Get(AppLangResourceKind.MdReadOnlyNotUtf8));
            else SetBanner(MarkdownBanner.ReadOnlyTooLarge, AppLangResources.Format(AppLangResourceKind.MdReadOnlyTooLarge, FileReader.FormatSize(bytes.Length)));
            return;
        }
        if (!IsEditing)
        {
            SetBanner(MarkdownBanner.ReadOnlyLossy, AppLangResources.Format(AppLangResourceKind.MdReadOnlyLossy, DescribeLoss(Fidelity!)));
            return;
        }
        SetBanner(MarkdownBanner.None, "");
        EditorText = document.Text;
        LoadId++;
    }

    private void SetBanner(MarkdownBanner banner, string text)
    {
        BannerText = text;
        Banner = banner;
    }

    // ---- the editor ---------------------------------------------------------------------------------------------

    /// <summary>The view is about to put <see cref="EditorText"/> into the editor: what the editor reports meanwhile is not an edit.</summary>
    public void BeginEditorLoad() => _editorLoading = true;

    /// <summary>The editor holds the loaded document; <paramref name="exported"/> is what it writes for it, the baseline edits are measured against.</summary>
    public void EditorLoaded(string exported)
    {
        _baseline = exported;
        _editorLoading = false;
        IsDirty = false;
    }

    /// <summary>The editor's content changed (typing, a toolbar command, undo).</summary>
    public void EditorChanged()
    {
        if (_editorLoading || _baseline is null || !IsEditing) return;
        if (Banner == MarkdownBanner.SaveFailed) SetBanner(MarkdownBanner.None, "");
        if (Text.Length <= ExactDirtyLimit)
        {
            UpdateDirty();
            return;
        }
        // A large document is not exported at every keystroke: it counts as edited now and is compared after a pause.
        IsDirty = true;
        var version = ++_dirtyVersion;
        _ = Task.Delay(400).ContinueWith(_ => UiContext.Run(() => { if (version == _dirtyVersion && !Disposed) UpdateDirty(); }), TaskScheduler.Default);
    }

    private void UpdateDirty()
    {
        if (ReadEditor is not { } read || _baseline is null) return;
        IsDirty = !string.Equals(read(), _baseline, StringComparison.Ordinal);
    }

    /// <summary>"Edit anyway" on a file the editor cannot keep intact.</summary>
    [RelayCommand]
    private void EditAnyway()
    {
        if (!CanEdit || IsEditing) return;
        _forcedEdit = true;
        SetBanner(MarkdownBanner.None, "");
        ViewMode = RenderedMode;
        EditorText = Text;
        IsEditing = true;
        LoadId++;
    }

    // ---- disk conflicts ------------------------------------------------------------------------------------------

    /// <summary>"Reload (discard my edits)": the file on disk replaces the document.</summary>
    [RelayCommand]
    private void ReloadFromDisk()
    {
        if (_pendingDisk is not { } pending) return;
        _version++;
        Apply(pending);
    }

    /// <summary>"Keep my edits": the change on disk is acknowledged and the next save overwrites it.</summary>
    [RelayCommand]
    private void KeepMine()
    {
        _diskBytes = _pendingDisk?.Bytes;
        _pendingDisk = null;
        SetBanner(MarkdownBanner.None, "");
    }

    // ---- saving --------------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private Task SaveDocumentAsync() => SaveAsync();

    public override async Task<bool> SaveAsync()
    {
        if (!IsEditing || ReadEditor is not { } read || _baseline is null) return true;
        var markdown = read();
        // Nothing was changed (or every change was undone): the file is not touched.
        if (string.Equals(markdown, _baseline, StringComparison.Ordinal))
        {
            IsDirty = false;
            return true;
        }
        if (Banner == MarkdownBanner.DiskChanged
            && !await Context.Dialogs.ConfirmAsync(
                AppLangResources.Get(AppLangResourceKind.MdOverwriteTitle), AppLangResources.Get(AppLangResourceKind.MdOverwriteBody),
                AppLangResources.Get(AppLangResourceKind.MdOverwrite), isDanger: true))
        {
            return false;
        }
        if (!_saveConfirmed && Fidelity is { IsExact: false } fidelity)
        {
            var loss = fidelity.HasLoss;
            var confirmed = await Context.Dialogs.ConfirmAsync(
                AppLangResources.Get(loss ? AppLangResourceKind.MdSaveLossTitle : AppLangResourceKind.MdSaveReformatTitle),
                AppLangResources.Format(loss ? AppLangResourceKind.MdSaveLossBody : AppLangResourceKind.MdSaveReformatBody, DescribeLoss(fidelity)),
                AppLangResources.Get(AppLangResourceKind.MdSaveAnyway), isDanger: loss);
            if (!confirmed) return false;
            _saveConfirmed = true;
        }
        // The editor may have changed while the person decided.
        markdown = read();
        var bytes = _document.Encode(markdown);
        try
        {
            await Task.Run(() => AtomicFileWriter.WriteAllBytes(Path, bytes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetBanner(MarkdownBanner.SaveFailed, AppLangResources.Format(AppLangResourceKind.MdSaveFailed, e.Message));
            return false;
        }
        _diskBytes = bytes;
        _pendingDisk = null;
        _baseline = markdown;
        FileSize = bytes.Length;
        // What the file now holds, as the preview and the source show it.
        Text = MarkdownFileText.TryDecode(bytes, out var saved) ? saved.Text : markdown;
        IsDirty = false;
        SetBanner(MarkdownBanner.None, "");
        return true;
    }

    /// <summary>The kinds of content named in a warning or a banner: "front matter, HTML blocks".</summary>
    public static string DescribeLoss(MarkdownFidelity fidelity)
    {
        var parts = fidelity.Lost.Select(Describe).ToList();
        if (parts.Count == 0) parts.Add(AppLangResources.Get(AppLangResourceKind.MdIssueReformat));
        return string.Join(AppLangResources.Get(AppLangResourceKind.ListSeparator), parts);
    }

    public static string Describe(MarkdownIssue issue) => AppLangResources.Get(issue switch
    {
        MarkdownIssue.FrontMatter => AppLangResourceKind.MdIssueFrontMatter,
        MarkdownIssue.HtmlBlock => AppLangResourceKind.MdIssueHtmlBlock,
        MarkdownIssue.HardLineBreak => AppLangResourceKind.MdIssueHardBreak,
        MarkdownIssue.InlineImage => AppLangResourceKind.MdIssueInlineImage,
        MarkdownIssue.IndentedCode => AppLangResourceKind.MdIssueIndentedCode,
        MarkdownIssue.TildeFence => AppLangResourceKind.MdIssueTildeFence,
        MarkdownIssue.SetextHeading => AppLangResourceKind.MdIssueSetext,
        MarkdownIssue.ListStartNumber => AppLangResourceKind.MdIssueListStart,
        MarkdownIssue.NestedBlocksInList => AppLangResourceKind.MdIssueNestedBlocks,
        _ => AppLangResourceKind.MdIssueUnrecognized,
    });
}
