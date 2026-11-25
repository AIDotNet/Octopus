using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Files.Code;
using Octopus.App.Localization;
using Octopus.FileSystem;
using Octopus.Git;
using Octopus.Storage;

namespace Octopus.App.Files;

/// <summary>
/// A diff of one changed file (staged, unstaged, or a commit), rendered with Arc's Monaco <c>DiffEditor</c> when both
/// full sides are available (with live editing and saving for local unstaged working-tree diffs), falling back to the
/// unified <c>CodeView</c> for truncated or remote-only diffs. Watches the file and <c>.git</c> for live updates.
/// </summary>
public sealed partial class DiffTabViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly IGitService _git;
    private readonly IFileTreeService _files;
    private readonly ISettingsStore _settings;
    private readonly UiContext _ui = new();
    private readonly List<IDisposable> _watches = [];
    private CodeFileText _worktreeDocument = CodeFileText.Empty;
    private byte[]? _diskHash;
    private long _diskLength = -1;
    private string? _modifiedBaseline;
    private bool _editorLoading;
    private int _version;
    private bool _disposed;

    public DiffTabViewModel(DiffTabRequest request, IGitService git, IFileTreeService files, ISettingsStore settings)
    {
        Request = request;
        _git = git;
        _files = files;
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
        var name = System.IO.Path.GetFileName(request.Path);
        Title = request.Commit is { } commit
            ? AppLangResources.Format(AppLangResourceKind.DiffTitleCommit, name, commit[..Math.Min(7, commit.Length)])
            : AppLangResources.Format(request.Staged ? AppLangResourceKind.DiffTitleStaged : AppLangResourceKind.DiffTitleChanges, name);
        MonoFontFamily = string.IsNullOrWhiteSpace(settings.Current.Terminal.FontFamily) ? null : settings.Current.Terminal.FontFamily;
        FontSize = settings.Current.Terminal.FontSize is > 6 and < 60 ? settings.Current.Terminal.FontSize : 13;
        var shown = request.Path.Replace('\\', '/');
        var cut = shown.LastIndexOf('/');
        FolderText = cut < 0 ? "" : shown[..(cut + 1)];
        FileLabel = cut < 0 ? shown : shown[(cut + 1)..];
        Language = FileLanguages.For(name);
        EditorLanguageId = FileLanguages.ForCodeEditor(name);

        // What a commit changed never changes: nothing to watch.
        if (request.Commit is null)
        {
            _watches.Add(files.WatchFile(request.FullPath, Reload, Debounce));
            _ = WatchRepositoryAsync();
        }
        _ = RefreshAsync();
    }

    public DiffTabRequest Request { get; }
    public string Title { get; }

    /// <summary>The terminal's font; null is the theme's monospace stack.</summary>
    public string? MonoFontFamily { get; }

    /// <summary>The folder part of the path (relative to the repository) and the file name, as the bar shows them.</summary>
    public string FolderText { get; }
    public string FileLabel { get; }

    public string Language { get; }
    public string EditorLanguageId { get; }
    public double FontSize { get; }

    /// <summary>Reads the live right-hand modified text from <c>DiffEditor</c> (set by the view).</summary>
    public Func<string>? ReadModifiedEditor { get; set; }

    /// <summary>Long lines wrap instead of scrolling sideways (unified fallback view).</summary>
    [ObservableProperty] public partial bool WrapLines { get; set; }

    /// <summary>Side-by-side diff (true) vs inline diff (false) in <c>DiffEditor</c>.</summary>
    [ObservableProperty] public partial bool RenderSideBySide { get; set; } = true;

    /// <summary>Collapse long runs of unchanged lines in <c>DiffEditor</c>.</summary>
    [ObservableProperty] public partial bool HideUnchangedRegions { get; set; } = true;

    /// <summary>Full original and modified texts for <c>DiffEditor</c>.</summary>
    [ObservableProperty] public partial string OriginalText { get; private set; } = "";
    [ObservableProperty] public partial string ModifiedText { get; private set; } = "";
    [ObservableProperty] public partial int DiffLoadId { get; private set; }
    [ObservableProperty] public partial bool UseDiffEditor { get; private set; }

    /// <summary>True when the right-hand modified pane can be edited and saved back to the working tree.</summary>
    [ObservableProperty] public partial bool IsEditing { get; private set; }
    [ObservableProperty] public partial bool IsDirty { get; private set; }

    /// <summary>The lines' text (one per row, each ended by a newline) and what the unified fallback view needs.</summary>
    [ObservableProperty] public partial string CodeText { get; private set; } = "";
    [ObservableProperty] public partial IReadOnlyList<CodeLineMark> Marks { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DiffRowViewModel> Rows { get; private set; } = [];

    /// <summary>Said instead of the lines: binary, too large, no changes, unreadable. Null while lines show.</summary>
    [ObservableProperty] public partial string? Notice { get; private set; }
    /// <summary>Said above the lines when only the first part of a long diff shows.</summary>
    [ObservableProperty] public partial string? TruncatedNotice { get; private set; }
    [ObservableProperty] public partial string? SaveError { get; private set; }
    [ObservableProperty] public partial int Added { get; private set; }
    [ObservableProperty] public partial int Removed { get; private set; }

    public bool HasChangeCounts => Added > 0 || Removed > 0;
    public string AddedText => "+" + Added;
    public string RemovedText => "−" + Removed;
    public bool HasTruncatedNotice => TruncatedNotice is not null;
    public bool HasSaveError => SaveError is not null;
    public bool HasNotice => Notice is not null;
    public bool HasRows => Notice is null;
    public bool ShowDiffEditor => HasRows && UseDiffEditor;
    public bool ShowUnifiedFallback => HasRows && !UseDiffEditor;
    public bool IsDiffReadOnly => !IsEditing;
    public bool HasUnsavedChanges => IsDirty && IsEditing;
    public bool CanSave => IsEditing;
    public bool ShowUnsaved => IsDirty && IsEditing;
    public bool ShowSave => IsEditing && ShowDiffEditor;

    [RelayCommand]
    private void ToggleWrap() => WrapLines = !WrapLines;

    [RelayCommand]
    private void ToggleSideBySide() => RenderSideBySide = !RenderSideBySide;

    [RelayCommand]
    private void ToggleHideUnchanged() => HideUnchangedRegions = !HideUnchangedRegions;

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private Task SaveDocumentAsync() => SaveAsync();

    partial void OnAddedChanged(int value) => NotifyCounts();
    partial void OnRemovedChanged(int value) => NotifyCounts();
    partial void OnTruncatedNoticeChanged(string? value) => OnPropertyChanged(nameof(HasTruncatedNotice));
    partial void OnSaveErrorChanged(string? value) => OnPropertyChanged(nameof(HasSaveError));

    partial void OnNoticeChanged(string? value)
    {
        OnPropertyChanged(nameof(HasNotice));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ShowDiffEditor));
        OnPropertyChanged(nameof(ShowUnifiedFallback));
        OnPropertyChanged(nameof(ShowSave));
    }

    partial void OnUseDiffEditorChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDiffEditor));
        OnPropertyChanged(nameof(ShowUnifiedFallback));
        OnPropertyChanged(nameof(ShowSave));
    }

    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDiffReadOnly));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ShowUnsaved));
        OnPropertyChanged(nameof(ShowSave));
    }

    partial void OnIsDirtyChanged(bool value)
    {
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(ShowUnsaved));
        SaveDocumentCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(HasChangeCounts));
        OnPropertyChanged(nameof(AddedText));
        OnPropertyChanged(nameof(RemovedText));
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _ui.Post(() =>
        {
            if (!_disposed && !HasUnsavedChanges) _ = RefreshAsync();
        });
    }

    private void Reload() => _ui.Post(() =>
    {
        if (!HasUnsavedChanges) _ = RefreshAsync();
    });

    private async Task WatchRepositoryAsync()
    {
        var directory = System.IO.Path.GetDirectoryName(Request.FullPath);
        if (directory is null || !System.IO.Directory.Exists(directory)) return;
        var directories = await _git.GetGitDirectoriesAsync(directory);
        if (directories is null) return;
        if (_disposed) return;
        _watches.Add(_files.WatchGitDirectory(directories.GitDirectory, directories.CommonDirectory, Reload, Debounce));
    }

    /// <summary>Reads the diff again. A newer read makes an older one drop its result.</summary>
    public void Refresh() => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        var version = ++_version;
        GitFileDiff? diff;
        GitFileDiffPair? pair = null;
        CodeFileText? worktreeDoc = null;
        byte[]? diskHash = null;
        long diskLength = -1;
        try
        {
            diff = Request.Commit is { } commit
                ? await _git.GetCommitFileDiffAsync(Request.RepositoryRoot, commit, Request.Path, Request.OriginalPath)
                : await _git.GetFileDiffAsync(Request.RepositoryRoot, Request.Path, Request.Staged, Request.OriginalPath);

            var experiments = _settings.Current.Experiments;
            if (experiments.DiffEditor && diff is { Kind: GitFileDiffKind.Text, IsTruncated: false })
            {
                pair = await _git.GetFileDiffPairAsync(Request.RepositoryRoot, Request.Path, Request.Staged, Request.OriginalPath, Request.Commit);
                if (pair is { CanEditModified: true } && experiments.CodeEditor && File.Exists(Request.FullPath))
                {
                    var bytes = await Task.Run(() => File.ReadAllBytes(Request.FullPath));
                    diskHash = SHA256.HashData(bytes);
                    diskLength = bytes.Length;
                    if (CodeFileText.TryDecode(bytes, out var decoded)) worktreeDoc = decoded;
                }
            }
        }
        catch (Exception)
        {
            diff = null;
            pair = null;
        }
        if (version != _version || _disposed) return;
        Apply(diff, pair, worktreeDoc, diskHash, diskLength);
    }

    private void Apply(GitFileDiff? diff, GitFileDiffPair? pair, CodeFileText? worktreeDoc, byte[]? diskHash, long diskLength)
    {
        TruncatedNotice = null;
        Added = diff?.Added ?? 0;
        Removed = diff?.Removed ?? 0;
        if (diff is null or { Kind: not GitFileDiffKind.Text })
        {
            Rows = [];
            CodeText = "";
            Marks = [];
            UseDiffEditor = false;
            IsEditing = false;
            IsDirty = false;
            _modifiedBaseline = null;
            Notice = AppLangResources.Get(diff?.Kind switch
            {
                GitFileDiffKind.Binary => AppLangResourceKind.DiffBinary,
                GitFileDiffKind.TooLarge => AppLangResourceKind.DiffTooLarge,
                GitFileDiffKind.Empty => AppLangResourceKind.DiffEmpty,
                _ => AppLangResourceKind.DiffFailed,
            });
            return;
        }
        var digits = diff.Lines.Max(l => Math.Max(l.OldNumber ?? 0, l.NewNumber ?? 0)).ToString().Length;
        var rows = diff.Lines.Select(l => new DiffRowViewModel(l, digits)).ToArray();
        var marks = rows.Select(r => r.ToMark()).ToArray();
        var text = string.Concat(rows.Select(r => r.Text + "\n"));
        // Any change under .git reads the diff again, mostly to find it as it was: then the view keeps what it has
        // instead of laying out and coloring every line again.
        if (!string.Equals(text, CodeText, StringComparison.Ordinal) || !marks.SequenceEqual(Marks))
        {
            Rows = rows;
            Marks = marks;
            CodeText = text;
        }
        Notice = null;
        if (diff.IsTruncated)
        {
            TruncatedNotice = AppLangResources.Format(AppLangResourceKind.DiffTruncated, diff.Lines.Count, diff.TotalLines);
            UseDiffEditor = false;
            IsEditing = false;
            IsDirty = false;
            return;
        }
        if (pair is not null)
        {
            _worktreeDocument = worktreeDoc ?? CodeFileText.Empty;
            _diskHash = diskHash;
            _diskLength = diskLength;
            IsEditing = pair.CanEditModified && worktreeDoc is not null;
            UseDiffEditor = true;
            if (DiffLoadId == 0
                || !string.Equals(OriginalText, pair.OriginalText, StringComparison.Ordinal)
                || !string.Equals(ModifiedText, pair.ModifiedText, StringComparison.Ordinal))
            {
                OriginalText = pair.OriginalText;
                ModifiedText = pair.ModifiedText;
                _modifiedBaseline = pair.ModifiedText;
                IsDirty = false;
                DiffLoadId++;
            }
            else if (_modifiedBaseline is null)
            {
                _modifiedBaseline = pair.ModifiedText;
            }
        }
        else
        {
            UseDiffEditor = false;
            IsEditing = false;
            IsDirty = false;
        }
    }

    public void BeginDiffLoad() => _editorLoading = true;

    public void DiffLoaded(string modifiedText)
    {
        _modifiedBaseline = modifiedText;
        _editorLoading = false;
        IsDirty = false;
    }

    public void ModifiedEditorChanged()
    {
        if (_editorLoading || !IsEditing || _modifiedBaseline is null || ReadModifiedEditor is not { } read) return;
        SaveError = null;
        IsDirty = !string.Equals(read(), _modifiedBaseline, StringComparison.Ordinal);
    }

    public async Task<bool> SaveAsync()
    {
        if (!IsEditing || ReadModifiedEditor is not { } read || _modifiedBaseline is null) return true;
        var current = read();
        if (string.Equals(current, _modifiedBaseline, StringComparison.Ordinal))
        {
            IsDirty = false;
            return true;
        }
        var bytes = _worktreeDocument.Encode(current);
        try
        {
            await Task.Run(() => AtomicFileWriter.WriteAllBytes(Request.FullPath, bytes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SaveError = e.Message;
            return false;
        }
        _diskHash = SHA256.HashData(bytes);
        _diskLength = bytes.Length;
        _modifiedBaseline = current;
        ModifiedText = current;
        IsDirty = false;
        SaveError = null;
        _ = RefreshAsync();
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        foreach (var watch in _watches) watch.Dispose();
        _watches.Clear();
    }
}
