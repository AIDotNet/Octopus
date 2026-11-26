using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;

namespace Octopus.App.Files.Code;

/// <summary>What the strip under the info bar says about a code file, if anything.</summary>
public enum CodeBanner
{
    None,
    /// <summary>Opened read-only because the bytes are not valid UTF-8.</summary>
    ReadOnlyNotUtf8,
    /// <summary>The file changed on disk while there are unsaved edits.</summary>
    DiskChanged,
    /// <summary>Saving failed.</summary>
    SaveFailed,
}

/// <summary>
/// Text and source code, editable in place through Arc's Monaco-based <c>CodeEditor</c>: syntax highlighted, line
/// numbered, wrapping on request. Opening and looking never writes: a save happens only after an edit, atomically,
/// keeping the file's own BOM and line endings. Non-UTF-8 files open read-only; large and binary files show a notice.
/// </summary>
public sealed partial class CodeViewerModel : FileViewerModel
{
    public const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>Below this many characters the unsaved state follows every keystroke exactly; above it, after a pause.</summary>
    private const int ExactDirtyLimit = 64_000;

    private int _version;
    private int _dirtyVersion;
    private CodeFileText _document = CodeFileText.Empty;
    private byte[]? _diskHash;
    private long _diskLength = -1;
    private FileRead? _pendingDisk;
    private string? _baseline;
    private bool _editorLoading;
    private bool _fileIsUtf8;

    public CodeViewerModel(FileViewerContext context) : base(context)
    {
        Language = FileLanguages.For(context.Location.FileName);
        EditorLanguageId = FileLanguages.ForCodeEditor(context.Location.FileName);
        MonoFontFamily = context.MonoFontFamily;
        FontSize = context.FontSize;
        UseCodeEditor = context.Settings?.Current.Experiments.CodeEditor ?? true;
        UseLanguageServer = ComputeUseLanguageServer(UseCodeEditor);
        if (context.Settings is { } settings) settings.Changed += OnSettingsChanged;
    }

    /// <summary>The short identifier used for display metadata; plain text when unknown.</summary>
    public string Language { get; }

    /// <summary>The Monaco language id handed to <c>CodeEditor.LanguageId</c>.</summary>
    public string EditorLanguageId { get; }

    /// <summary>Owning project root folder, used as the LSP workspace root when local.</summary>
    public string? ProjectRoot => Context.ProjectRoot;

    /// <summary>The terminal's font; null is the theme's monospace stack.</summary>
    public string? MonoFontFamily { get; }

    public double FontSize { get; }

    /// <summary>The view reads the editor's current text through this (set by the view that owns the editor).</summary>
    public Func<string>? ReadEditor { get; set; }

    /// <summary>Long lines wrap instead of scrolling sideways.</summary>
    [ObservableProperty] public partial bool WrapLines { get; set; }

    /// <summary>Controlled by <c>Experiments.CodeEditor</c>: true shows Monaco <c>CodeEditor</c>, false shows read-only <c>CodeSurface</c>.</summary>
    [ObservableProperty] public partial bool UseCodeEditor { get; private set; }

    /// <summary>Controlled by <c>Experiments.LanguageServer</c> (local files only, requires <see cref="UseCodeEditor"/>).</summary>
    [ObservableProperty] public partial bool UseLanguageServer { get; private set; }

    /// <summary>The file's text as loaded from disk (line endings normalized to LF).</summary>
    [ObservableProperty] public partial string Text { get; private set; } = "";
    [ObservableProperty] public partial int LineCount { get; private set; }
    [ObservableProperty] public partial long FileSize { get; private set; }

    /// <summary>The editor is writable (valid UTF-8 file loaded with no fatal notice and <see cref="UseCodeEditor"/> on).</summary>
    [ObservableProperty] public partial bool IsEditing { get; private set; }

    /// <summary>Edits are held that are not saved to disk yet.</summary>
    [ObservableProperty] public partial bool IsDirty { get; private set; }

    [ObservableProperty] public partial CodeBanner Banner { get; private set; }
    [ObservableProperty] public partial string BannerText { get; private set; } = "";

    /// <summary>What the editor is to load; the view loads it whenever <see cref="LoadId"/> changes.</summary>
    [ObservableProperty] public partial string EditorText { get; private set; } = "";
    [ObservableProperty] public partial int LoadId { get; private set; }

    public bool ShowCodeEditor => HasContent && UseCodeEditor;
    public bool ShowReadOnlyFallback => HasContent && !UseCodeEditor;
    public bool IsReadOnly => !IsEditing;
    public bool ShowUnsaved => IsDirty && IsEditing;
    public bool ShowSave => IsEditing && UseCodeEditor;
    public bool BannerIsInfo => Banner == CodeBanner.ReadOnlyNotUtf8;
    public bool BannerIsDiskChanged => Banner == CodeBanner.DiskChanged;
    public bool BannerIsSaveFailed => Banner == CodeBanner.SaveFailed;

    public override bool HasUnsavedChanges => IsDirty && IsEditing;
    public override bool CanSave => IsEditing && UseCodeEditor;

    [RelayCommand]
    private void ToggleWrap() => WrapLines = !WrapLines;

    /// <summary>"C# · 1,204 lines · 34 KB": what the bar says quietly about the file.</summary>
    public string MetaText => CodeMeta.Describe(Language, LineCount, FileSize);

    partial void OnTextChanged(string value) => LineCount = CodeMeta.CountLines(value);
    partial void OnLineCountChanged(int value) => OnPropertyChanged(nameof(MetaText));
    partial void OnFileSizeChanged(long value) => OnPropertyChanged(nameof(MetaText));

    partial void OnUseCodeEditorChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCodeEditor));
        OnPropertyChanged(nameof(ShowReadOnlyFallback));
        OnPropertyChanged(nameof(ShowSave));
        OnPropertyChanged(nameof(CanSave));
    }

    protected override void NoticeChanged()
    {
        OnPropertyChanged(nameof(ShowCodeEditor));
        OnPropertyChanged(nameof(ShowReadOnlyFallback));
    }

    private bool ComputeUseLanguageServer(bool codeEditorEnabled) =>
        codeEditorEnabled
        && (Context.Settings?.Current.Experiments.LanguageServer ?? false)
        && Context.Remote is null
        && !string.IsNullOrWhiteSpace(Path);

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (Disposed || Context.Settings is not { } settings) return;
        UiContext.PostToUi(() =>
        {
            if (Disposed) return;
            var wantEditor = settings.Current.Experiments.CodeEditor;
            if (wantEditor != UseCodeEditor && !HasUnsavedChanges)
            {
                UseCodeEditor = wantEditor;
                if (HasContent)
                {
                    IsEditing = wantEditor && _fileIsUtf8;
                    if (!wantEditor)
                    {
                        SetBanner(CodeBanner.None, "");
                    }
                    else
                    {
                        if (_fileIsUtf8) SetBanner(CodeBanner.None, "");
                        else SetBanner(CodeBanner.ReadOnlyNotUtf8, AppLangResources.Get(AppLangResourceKind.MdReadOnlyNotUtf8));
                        EditorText = Text;
                        LoadId++;
                    }
                }
            }
            UseLanguageServer = ComputeUseLanguageServer(UseCodeEditor);
        });
    }

    public override void Dispose()
    {
        if (Context.Settings is { } settings) settings.Changed -= OnSettingsChanged;
        base.Dispose();
    }

    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsReadOnly));
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

    partial void OnBannerChanged(CodeBanner value)
    {
        OnPropertyChanged(nameof(BannerIsInfo));
        OnPropertyChanged(nameof(BannerIsDiskChanged));
        OnPropertyChanged(nameof(BannerIsSaveFailed));
    }

    // ---- loading ------------------------------------------------------------------------------------------------

    /// <summary>Reads the file. A newer read makes an older one drop its result.</summary>
    public override async Task LoadAsync()
    {
        var version = ++_version;
        var read = await ReadFromDiskAsync();
        if (version != _version || Disposed) return;
        ApplyRead(read);
    }

    /// <summary>The file changed on disk: its own write is ignored, a change over unsaved edits waits for a decision, any other is loaded.</summary>
    public override async Task OnFileChangedAsync()
    {
        var read = await ReadFromDiskAsync();
        if (Disposed) return;
        if (read.Bytes is { } bytes && bytes.Length == _diskLength && _diskHash is { } known
            && CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), known))
        {
            return;
        }
        if (!HasUnsavedChanges)
        {
            _version++;
            ApplyRead(read);
            return;
        }
        _pendingDisk = read;
        SetBanner(CodeBanner.DiskChanged, "");
    }

    private Task<FileRead> ReadFromDiskAsync() =>
        Task.Run(() =>
        {
            try
            {
                return FileReader.Read(Path, MaxBytes);
            }
            catch (Exception)
            {
                return new FileRead(null, AppLangResourceKind.FileUnreadable, 0);
            }
        });

    private void ApplyRead(FileRead read)
    {
        _pendingDisk = null;
        IsDirty = false;
        _baseline = null;
        if (read.Problem is { } problem)
        {
            _diskHash = null;
            _diskLength = -1;
            Text = "";
            EditorText = "";
            FileSize = 0;
            IsEditing = false;
            SetBanner(CodeBanner.None, "");
            Notice = AppLangResources.Get(problem);
            return;
        }
        var bytes = read.Bytes!;
        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0)
        {
            _diskHash = null;
            _diskLength = -1;
            Text = "";
            EditorText = "";
            FileSize = 0;
            IsEditing = false;
            SetBanner(CodeBanner.None, "");
            Notice = AppLangResources.Get(AppLangResourceKind.FileBinary);
            return;
        }
        var hadNotice = Notice is not null;
        Notice = null;
        _diskHash = SHA256.HashData(bytes);
        _diskLength = bytes.Length;
        FileSize = bytes.Length;

        var utf8 = CodeFileText.TryDecode(bytes, out var document);
        _fileIsUtf8 = utf8;
        if (!utf8)
        {
            var raw = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Replace('\r', '\n');
            document = CodeFileText.Empty with { Text = raw };
        }
        _document = document;
        IsEditing = UseCodeEditor && utf8;
        if (!UseCodeEditor || utf8) SetBanner(CodeBanner.None, "");
        else SetBanner(CodeBanner.ReadOnlyNotUtf8, AppLangResources.Get(AppLangResourceKind.MdReadOnlyNotUtf8));

        var changed = !string.Equals(Text, document.Text, StringComparison.Ordinal);
        if (changed) Text = document.Text;
        else LineCount = CodeMeta.CountLines(document.Text);

        // Reload into the editor on the first load, after a notice cleared, or whenever the text changed.
        if (UseCodeEditor && (LoadId == 0 || hadNotice || changed || !string.Equals(EditorText, document.Text, StringComparison.Ordinal)))
        {
            EditorText = document.Text;
            LoadId++;
        }
        else
        {
            _baseline = document.Text;
        }
    }

    private void SetBanner(CodeBanner banner, string text)
    {
        BannerText = text;
        Banner = banner;
    }

    // ---- the editor ---------------------------------------------------------------------------------------------

    /// <summary>The view is about to put <see cref="EditorText"/> into the editor: what the editor reports meanwhile is not an edit.</summary>
    public void BeginEditorLoad() => _editorLoading = true;

    /// <summary>The editor holds the loaded document; <paramref name="loadedText"/> is the baseline edits are measured against.</summary>
    public void EditorLoaded(string loadedText)
    {
        _baseline = loadedText;
        _editorLoading = false;
        IsDirty = false;
    }

    /// <summary>The editor's content changed (typing, paste, undo, redo).</summary>
    public void EditorChanged()
    {
        if (_editorLoading || _baseline is null || !IsEditing) return;
        if (Banner == CodeBanner.SaveFailed) SetBanner(CodeBanner.None, "");
        if (Text.Length <= ExactDirtyLimit)
        {
            UpdateDirty();
            return;
        }
        // A large file is not compared on every keystroke: mark dirty immediately and verify after a pause.
        IsDirty = true;
        var version = ++_dirtyVersion;
        _ = Task.Delay(400).ContinueWith(
            _ => UiContext.Run(() => { if (version == _dirtyVersion && !Disposed) UpdateDirty(); }),
            TaskScheduler.Default);
    }

    private void UpdateDirty()
    {
        if (ReadEditor is not { } read || _baseline is null) return;
        var current = read();
        IsDirty = !string.Equals(current, _baseline, StringComparison.Ordinal);
        LineCount = CodeMeta.CountLines(current);
    }

    // ---- disk conflicts ------------------------------------------------------------------------------------------

    /// <summary>"Reload (discard my edits)": the file on disk replaces the document.</summary>
    [RelayCommand]
    private void ReloadFromDisk()
    {
        if (_pendingDisk is not { } pending) return;
        _version++;
        ApplyRead(pending);
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
        SetBanner(CodeBanner.None, "");
    }

    // ---- saving --------------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private Task SaveDocumentAsync() => SaveAsync();

    public override async Task<bool> SaveAsync()
    {
        if (!IsEditing || ReadEditor is not { } read || _baseline is null) return true;
        var current = read();
        // Nothing changed (or every change was undone): do not touch the file or its mtime.
        if (string.Equals(current, _baseline, StringComparison.Ordinal))
        {
            IsDirty = false;
            return true;
        }
        if (Banner == CodeBanner.DiskChanged
            && !await Context.Dialogs.ConfirmAsync(
                AppLangResources.Get(AppLangResourceKind.MdOverwriteTitle),
                AppLangResources.Get(AppLangResourceKind.MdOverwriteBody),
                AppLangResources.Get(AppLangResourceKind.MdOverwrite),
                isDanger: true))
        {
            return false;
        }
        // Re-read in case the user kept editing while deciding.
        current = read();
        var bytes = _document.Encode(current);
        // A file on an SSH / remote host: write to the host first so a failure leaves edits intact locally.
        if (Context.Remote is { } remote)
        {
            var sent = await remote.WriteAsync(bytes);
            if (sent.Outcome == RemoteWriteOutcome.Declined) return false;
            if (sent.Outcome == RemoteWriteOutcome.Failed)
            {
                SetBanner(CodeBanner.SaveFailed, AppLangResources.Format(AppLangResourceKind.MdSaveFailed, sent.Error ?? ""));
                return false;
            }
        }
        try
        {
            await Task.Run(() => AtomicFileWriter.WriteAllBytes(Path, bytes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetBanner(CodeBanner.SaveFailed, AppLangResources.Format(AppLangResourceKind.MdSaveFailed, e.Message));
            return false;
        }
        _diskHash = SHA256.HashData(bytes);
        _diskLength = bytes.Length;
        _pendingDisk = null;
        _baseline = current;
        FileSize = bytes.Length;
        Text = current;
        EditorText = current;
        IsDirty = false;
        SetBanner(CodeBanner.None, "");
        return true;
    }
}

/// <summary>The quiet line of facts the bar shows for a text file; shared by the code and the Markdown viewer.</summary>
internal static class CodeMeta
{
    public static int CountLines(string text) => text.Length == 0 ? 0 : text.AsSpan().Count('\n') + (text[^1] == '\n' ? 0 : 1);

    public static string Describe(string language, int lines, long size)
    {
        var name = language == FileLanguages.PlainText ? AppLangResources.Get(AppLangResourceKind.FilePlainText) : FileLanguages.DisplayName(language);
        return string.Join(" · ", name, AppLangResources.Format(AppLangResourceKind.FileLines, lines.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)), FileReader.FormatSize(size));
    }
}
