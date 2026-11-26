using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.App.Files.Code;
using Octopus.App.Localization;
using Octopus.App.Services;

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
/// A Markdown file as a rendered document that can be edited in place (Arc's rich text editor), with an editable raw
/// Markdown source mode (Arc's <c>CodeEditor</c>) and a rendered preview beside it. Opening and looking never writes:
/// a save happens only after an edit, atomically, in the file's own line ending, final newline and byte order mark.
/// </summary>
public sealed partial class MarkdownViewerModel : FileViewerModel
{
    /// <summary>The largest file shown at all (as the code viewer).</summary>
    public const long MaxBytes = CodeViewerModel.MaxBytes;

    /// <summary>The largest file offered to the rich text editor, which lays out every block: bigger ones open read-only in rendered mode.</summary>
    public const long MaxEditBytes = 512 * 1024;

    public const string RenderedMode = "rendered";
    /// <summary>The read-only preview of a document that is open in the editor: what it looks like rendered, diagrams included.</summary>
    public const string PreviewMode = "preview";
    public const string SourceMode = "source";

    /// <summary>Below this many characters the unsaved state follows every keystroke exactly; above it, after a pause.</summary>
    private const int ExactDirtyLimit = 64_000;

    private int _version;
    private int _dirtyVersion;
    private MarkdownFileText _document = MarkdownFileText.Empty;
    private byte[]? _diskHash;
    private long _diskLength = -1;
    private FileRead? _pendingDisk;
    private string? _baseline;
    private string? _sourceBaseline;
    private string? _assessedText;
    private int _lineCount;
    private bool _editorLoading;
    private bool _sourceLoading;
    private bool _saveConfirmed;
    private bool _forcedEdit;
    private bool _applying;
    private bool _isUtf8;
    private bool _sourceDirty;

    public MarkdownViewerModel(FileViewerContext context) : base(context)
    {
        MonoFontFamily = context.MonoFontFamily;
        FontSize = context.FontSize;
        UseCodeEditor = context.Settings?.Current.Experiments.CodeEditor ?? true;
        if (context.Settings is { } settings) settings.Changed += OnSettingsChanged;
    }

    public string? MonoFontFamily { get; }
    public double FontSize { get; }

    /// <summary>The folder the file is in: where a relative picture address starts.</summary>
    public string FolderPath => System.IO.Path.GetDirectoryName(Path) ?? "";

    /// <summary>The view reads the rich text editor's current Markdown through this (set by the view).</summary>
    public Func<string>? ReadEditor { get; set; }

    /// <summary>The view reads the source <c>CodeEditor</c>'s current Markdown through this (set by the view).</summary>
    public Func<string>? ReadSourceEditor { get; set; }

    /// <summary>
    /// What the editor writes for <see cref="EditorText"/> when the fidelity check already exported that same text (it
    /// loads it the same way, into an editor of the same profile); null when it did not. Saves the view a second export.
    /// </summary>
    public string? ExportOfEditorText =>
        Fidelity is { } fidelity && string.Equals(_assessedText, EditorText, StringComparison.Ordinal) ? fidelity.Exported : null;

    /// <summary>The file's text, as the file has it on disk (line endings made LF).</summary>
    [ObservableProperty] public partial string Text { get; private set; } = "";
    [ObservableProperty] public partial long FileSize { get; private set; }

    /// <summary>The text of the source view and the preview: the file's, or the active editor's live text while there are edits.</summary>
    [ObservableProperty] public partial string SourceText { get; private set; } = "";

    /// <summary>
    /// What the read-only preview and the source editor are given when that view shows. A hidden view keeps what it
    /// last had instead of parsing or re-loading at every change on disk, and gets the current text when shown again.
    /// </summary>
    [ObservableProperty] public partial string PreviewText { get; private set; } = "";
    [ObservableProperty] public partial string SourceViewText { get; private set; } = "";

    [ObservableProperty] public partial string ViewMode { get; set; } = RenderedMode;

    /// <summary>The rendered view is the rich text editor (otherwise the read-only preview).</summary>
    [ObservableProperty] public partial bool IsEditing { get; private set; }

    /// <summary>The file may be edited in the rich text editor: valid UTF-8 and within <see cref="MaxEditBytes"/>.</summary>
    [ObservableProperty] public partial bool CanEdit { get; private set; }

    /// <summary>Controlled by <c>Experiments.CodeEditor</c>: true enables editable Monaco source mode; false uses read-only <c>CodeSurface</c>.</summary>
    [ObservableProperty] public partial bool UseCodeEditor { get; private set; }

    /// <summary>The file may be edited losslessly in the raw Markdown source editor: valid UTF-8, loaded, and <see cref="UseCodeEditor"/> enabled.</summary>
    public bool CanEditSource => _isUtf8 && HasContent && UseCodeEditor;
    public bool IsSourceReadOnly => !CanEditSource;

    [ObservableProperty] public partial MarkdownBanner Banner { get; private set; }
    [ObservableProperty] public partial string BannerText { get; private set; } = "";

    /// <summary>Edits are held that are not saved (in either the rich text editor or the source editor).</summary>
    [ObservableProperty] public partial bool IsDirty { get; private set; }

    /// <summary>What the rich text editor is to load; the view loads it whenever <see cref="LoadId"/> changes.</summary>
    [ObservableProperty] public partial string EditorText { get; private set; } = "";
    [ObservableProperty] public partial int LoadId { get; private set; }

    /// <summary>Increments whenever the source <c>CodeEditor</c> must load <see cref="SourceViewText"/>.</summary>
    [ObservableProperty] public partial int SourceLoadId { get; private set; }

    public MarkdownFidelity? Fidelity { get; private set; }

    public bool ShowEditor => HasContent && IsEditing && ViewMode == RenderedMode;
    public bool ShowPreview => HasContent && (ViewMode == PreviewMode || (!IsEditing && ViewMode == RenderedMode));
    public bool ShowSource => HasContent && ViewMode == SourceMode;
    public bool ShowSourceEditor => ShowSource && UseCodeEditor;
    public bool ShowSourceFallback => ShowSource && !UseCodeEditor;

    public bool IsRenderedMode => ViewMode == RenderedMode;
    public bool IsPreviewMode => ViewMode == PreviewMode;
    public bool IsSourceMode => ViewMode == SourceMode;

    /// <summary>The preview is a view of its own only beside the rich text editor; a read-only file's rendered view already is it.</summary>
    public bool CanPreview => IsEditing;

    /// <summary>The bar's icons: the rendered view, the preview (while editing) or the source.</summary>
    [RelayCommand]
    private void SetViewMode(string? mode)
    {
        if (mode is RenderedMode or SourceMode || (mode == PreviewMode && CanPreview)) ViewMode = mode;
    }

    private bool IsEditableNow => IsEditing || (CanEditSource && (ViewMode == SourceMode || _sourceDirty));

    public bool ShowUnsaved => IsDirty && IsEditableNow;
    public bool ShowSave => IsEditableNow;
    public bool BannerIsLossy => Banner == MarkdownBanner.ReadOnlyLossy;
    public bool BannerIsInfo => Banner is MarkdownBanner.ReadOnlyTooLarge or MarkdownBanner.ReadOnlyNotUtf8;
    public bool BannerIsDiskChanged => Banner == MarkdownBanner.DiskChanged;
    public bool BannerIsSaveFailed => Banner == MarkdownBanner.SaveFailed;
    public string LanguageName => "md";

    public string MetaText => CodeMeta.Describe("md", _lineCount, FileSize);

    public override bool HasUnsavedChanges => IsDirty && IsEditableNow;
    public override bool CanSave => IsEditableNow;

    partial void OnTextChanged(string value)
    {
        _lineCount = CodeMeta.CountLines(value);
        OnPropertyChanged(nameof(MetaText));
        RefreshSource();
        FeedViews();
    }

    partial void OnSourceTextChanged(string value) => FeedViews();

    partial void OnFileSizeChanged(long value) => OnPropertyChanged(nameof(MetaText));

    partial void OnViewModeChanging(string oldValue, string newValue)
    {
        if (oldValue == SourceMode && newValue != SourceMode && _sourceDirty && ReadSourceEditor is { } readSrc)
        {
            var editedSource = readSrc();
            SourceText = editedSource;
            if (newValue == RenderedMode && IsEditing)
            {
                // Push source edits back into the rich text editor so rendered mode stays in sync.
                var fidelity = MarkdownFidelity.Assess(editedSource);
                Fidelity = fidelity;
                _assessedText = editedSource;
                if (_forcedEdit || !fidelity.HasLoss)
                {
                    EditorText = editedSource;
                    LoadId++;
                }
            }
        }
    }

    partial void OnViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsRenderedMode));
        OnPropertyChanged(nameof(IsPreviewMode));
        OnPropertyChanged(nameof(IsSourceMode));
        RefreshSource();
        RaiseShown();
        NotifyEditableState();
    }

    partial void OnIsEditingChanged(bool value)
    {
        // Without the editor the rendered view is the preview: one icon for it, not two.
        if (!value && ViewMode == PreviewMode) ViewMode = RenderedMode;
        OnPropertyChanged(nameof(CanPreview));
        RaiseShown();
        NotifyEditableState();
    }

    partial void OnIsDirtyChanged(bool value)
    {
        NotifyEditableState();
        SaveDocumentCommand.NotifyCanExecuteChanged();
    }

    partial void OnBannerChanged(MarkdownBanner value)
    {
        OnPropertyChanged(nameof(BannerIsLossy));
        OnPropertyChanged(nameof(BannerIsInfo));
        OnPropertyChanged(nameof(BannerIsDiskChanged));
        OnPropertyChanged(nameof(BannerIsSaveFailed));
    }

    protected override void NoticeChanged()
    {
        OnPropertyChanged(nameof(CanEditSource));
        OnPropertyChanged(nameof(IsSourceReadOnly));
        RaiseShown();
        NotifyEditableState();
    }

    private void NotifyEditableState()
    {
        OnPropertyChanged(nameof(ShowUnsaved));
        OnPropertyChanged(nameof(ShowSave));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(CanSave));
    }

    partial void OnUseCodeEditorChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditSource));
        OnPropertyChanged(nameof(IsSourceReadOnly));
        RaiseShown();
        NotifyEditableState();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (Disposed || Context.Settings is not { } settings) return;
        UiContext.PostToUi(() =>
        {
            if (Disposed) return;
            var want = settings.Current.Experiments.CodeEditor;
            if (want == UseCodeEditor || _sourceDirty) return;
            UseCodeEditor = want;
        });
    }

    public override void Dispose()
    {
        if (Context.Settings is { } settings) settings.Changed -= OnSettingsChanged;
        base.Dispose();
    }

    private void RaiseShown()
    {
        OnPropertyChanged(nameof(ShowEditor));
        OnPropertyChanged(nameof(ShowPreview));
        OnPropertyChanged(nameof(ShowSource));
        OnPropertyChanged(nameof(ShowSourceEditor));
        OnPropertyChanged(nameof(ShowSourceFallback));
        FeedViews();
    }

    private void FeedViews()
    {
        if (_applying) return;
        if (ShowPreview) PreviewText = SourceText;
        if (ShowSource && !_sourceDirty)
        {
            if (!string.Equals(SourceViewText, SourceText, StringComparison.Ordinal))
            {
                SourceViewText = SourceText;
                if (UseCodeEditor) SourceLoadId++;
            }
            else if (UseCodeEditor && SourceLoadId == 0)
            {
                SourceLoadId++;
            }
        }
    }

    private void RefreshSource()
    {
        if (_sourceDirty && ReadSourceEditor is { } readSrc)
        {
            SourceText = readSrc();
            return;
        }
        SourceText = (ViewMode is SourceMode or PreviewMode) && IsEditing && IsDirty && ReadEditor is { } read ? read() : Text;
    }

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
        if (read.Bytes is { } bytes && bytes.Length == _diskLength && _diskHash is { } known
            && CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), known))
        {
            return;
        }

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
        // The views are fed once the verdict on editing is in: the preview of a file that opens in the editor gets nothing.
        _applying = true;
        try
        {
            ApplyRead(read);
        }
        finally
        {
            _applying = false;
            FeedViews();
        }
    }

    private void ApplyRead(FileRead read)
    {
        _pendingDisk = null;
        _saveConfirmed = false;
        _sourceDirty = false;
        IsDirty = false;
        _baseline = null;
        _sourceBaseline = null;
        if (read.Problem is { } problem)
        {
            _diskHash = null;
            _diskLength = -1;
            _isUtf8 = false;
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
        _diskHash = SHA256.HashData(bytes);
        _diskLength = bytes.Length;
        FileSize = bytes.Length;
        var utf8 = MarkdownFileText.TryDecode(bytes, out var document);
        _isUtf8 = utf8;
        OnPropertyChanged(nameof(CanEditSource));
        OnPropertyChanged(nameof(IsSourceReadOnly));
        if (!utf8) document = MarkdownFileText.Empty with { Text = Encoding.UTF8.GetString(bytes) };
        _document = document;
        Text = document.Text;
        CanEdit = utf8 && bytes.Length <= MaxEditBytes;
        Fidelity = CanEdit ? MarkdownFidelity.Assess(document.Text) : null;
        _assessedText = document.Text;
        IsEditing = CanEdit && (_forcedEdit || Fidelity is { HasLoss: false });
        NotifyEditableState();
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

    // ---- the rich text editor -----------------------------------------------------------------------------------

    /// <summary>The view is about to put <see cref="EditorText"/> into the editor: what the editor reports meanwhile is not an edit.</summary>
    public void BeginEditorLoad() => _editorLoading = true;

    /// <summary>The editor holds the loaded document; <paramref name="exported"/> is what it writes for it, the baseline edits are measured against.</summary>
    public void EditorLoaded(string exported)
    {
        if (!_sourceDirty) _baseline = exported;
        _editorLoading = false;
        if (!_sourceDirty) IsDirty = false;
    }

    /// <summary>The rich text editor's content changed (typing, a toolbar command, undo).</summary>
    public void EditorChanged()
    {
        if (_editorLoading || _baseline is null || !IsEditing) return;
        _sourceDirty = false;
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

    // ---- the source code editor ---------------------------------------------------------------------------------

    /// <summary>The view is about to put <see cref="SourceViewText"/> into the source editor: ignore events during load.</summary>
    public void BeginSourceLoad() => _sourceLoading = true;

    /// <summary>The source editor holds the loaded Markdown; edits in source mode are measured against <paramref name="loaded"/>.</summary>
    public void SourceLoaded(string loaded)
    {
        // When entering source mode after rich-text edits, compare against the disk text so IsDirty reflects unsaved state.
        _sourceBaseline = IsDirty ? Text : loaded;
        _sourceLoading = false;
    }

    /// <summary>The source <c>CodeEditor</c>'s content changed (typing, paste, undo, redo).</summary>
    public void SourceEditorChanged()
    {
        if (_sourceLoading || _sourceBaseline is null || !CanEditSource) return;
        if (Banner == MarkdownBanner.SaveFailed) SetBanner(MarkdownBanner.None, "");
        if (Text.Length <= ExactDirtyLimit)
        {
            UpdateSourceDirty();
            return;
        }
        _sourceDirty = true;
        IsDirty = true;
        var version = ++_dirtyVersion;
        _ = Task.Delay(400).ContinueWith(_ => UiContext.Run(() => { if (version == _dirtyVersion && !Disposed) UpdateSourceDirty(); }), TaskScheduler.Default);
    }

    private void UpdateSourceDirty()
    {
        if (ReadSourceEditor is not { } read || _sourceBaseline is null) return;
        var current = read();
        var dirty = !string.Equals(current, _sourceBaseline, StringComparison.Ordinal);
        _sourceDirty = dirty;
        IsDirty = dirty;
        _lineCount = CodeMeta.CountLines(current);
        OnPropertyChanged(nameof(MetaText));
    }

    /// <summary>"Edit anyway" on a file the rich text editor cannot keep intact.</summary>
    [RelayCommand]
    private void EditAnyway()
    {
        if (!CanEdit || IsEditing) return;
        _forcedEdit = true;
        SetBanner(MarkdownBanner.None, "");
        ViewMode = RenderedMode;
        EditorText = _sourceDirty && ReadSourceEditor is { } readSrc ? readSrc() : Text;
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
        if (_pendingDisk?.Bytes is { } bytes)
        {
            _diskHash = SHA256.HashData(bytes);
            _diskLength = bytes.Length;
        }
        else
        {
            _diskHash = null;
            _diskLength = -1;
        }
        _pendingDisk = null;
        SetBanner(MarkdownBanner.None, "");
    }

    // ---- saving --------------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private Task SaveDocumentAsync() => SaveAsync();

    public override async Task<bool> SaveAsync()
    {
        if (_sourceDirty && CanEditSource && ReadSourceEditor is { } readSrc)
        {
            return await SaveRawSourceAsync(readSrc);
        }
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
        if (!await WriteBytesAsync(bytes)) return false;
        _baseline = markdown;
        var savedText = MarkdownFileText.TryDecode(bytes, out var saved) ? saved.Text : markdown;
        _sourceBaseline = savedText;
        Text = savedText;
        IsDirty = false;
        SetBanner(MarkdownBanner.None, "");
        return true;
    }

    private async Task<bool> SaveRawSourceAsync(Func<string> readSrc)
    {
        var source = readSrc();
        if (string.Equals(source, _sourceBaseline, StringComparison.Ordinal))
        {
            _sourceDirty = false;
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
        source = readSrc();
        var bytes = _document.EncodeRaw(source);
        if (!await WriteBytesAsync(bytes)) return false;
        _sourceDirty = false;
        _sourceBaseline = source;
        if (MarkdownFileText.TryDecode(bytes, out var saved))
        {
            _document = saved;
            Text = saved.Text;
        }
        else
        {
            Text = source;
        }
        SourceViewText = Text;
        // Re-assess fidelity after raw edits and sync the rich text editor if it is active.
        CanEdit = _isUtf8 && bytes.Length <= MaxEditBytes;
        Fidelity = CanEdit ? MarkdownFidelity.Assess(Text) : null;
        _assessedText = Text;
        if (IsEditing && CanEdit && (_forcedEdit || Fidelity is { HasLoss: false }))
        {
            EditorText = Text;
            LoadId++;
        }
        else if (IsEditing)
        {
            IsEditing = false;
        }
        IsDirty = false;
        if (Banner is MarkdownBanner.DiskChanged or MarkdownBanner.SaveFailed) SetBanner(MarkdownBanner.None, "");
        return true;
    }

    private async Task<bool> WriteBytesAsync(byte[] bytes)
    {
        // A file of an SSH host: the host gets the text first, so a failure leaves the edits in place here.
        if (Context.Remote is { } remote)
        {
            var sent = await remote.WriteAsync(bytes);
            if (sent.Outcome == RemoteWriteOutcome.Declined) return false;
            if (sent.Outcome == RemoteWriteOutcome.Failed)
            {
                SetBanner(MarkdownBanner.SaveFailed, AppLangResources.Format(AppLangResourceKind.MdSaveFailed, sent.Error ?? ""));
                return false;
            }
        }
        try
        {
            await Task.Run(() => AtomicFileWriter.WriteAllBytes(Path, bytes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetBanner(MarkdownBanner.SaveFailed, AppLangResources.Format(AppLangResourceKind.MdSaveFailed, e.Message));
            return false;
        }
        _diskHash = SHA256.HashData(bytes);
        _diskLength = bytes.Length;
        _pendingDisk = null;
        FileSize = bytes.Length;
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
