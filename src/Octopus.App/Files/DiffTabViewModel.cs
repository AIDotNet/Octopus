using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Localization;
using Octopus.FileSystem;
using Octopus.Git;
using Octopus.Storage;

namespace Octopus.App.Files;

/// <summary>
/// A read-only diff of one changed file (staged or not), as unified lines. It reads again when the file changes on disk
/// or the repository moves (index, HEAD, refs): a tab that stays open shows what is true now.
/// </summary>
public sealed partial class DiffTabViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly IGitService _git;
    private readonly IFileTreeService _files;
    private readonly UiContext _ui = new();
    private readonly List<IDisposable> _watches = [];
    private int _version;
    private bool _disposed;

    public DiffTabViewModel(DiffTabRequest request, IGitService git, IFileTreeService files, ISettingsStore settings)
    {
        Request = request;
        _git = git;
        _files = files;
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
    public double FontSize { get; }

    /// <summary>Long lines wrap instead of scrolling sideways.</summary>
    [ObservableProperty] public partial bool WrapLines { get; set; }

    [RelayCommand]
    private void ToggleWrap() => WrapLines = !WrapLines;

    /// <summary>The lines' text (one per row, each ended by a newline) and what the code view needs to draw them as a diff.</summary>
    [ObservableProperty] public partial string CodeText { get; private set; } = "";
    [ObservableProperty] public partial IReadOnlyList<CodeLineMark> Marks { get; private set; } = [];

    [ObservableProperty] public partial IReadOnlyList<DiffRowViewModel> Rows { get; private set; } = [];
    /// <summary>Said instead of the lines: binary, too large, no changes, unreadable. Null while lines show.</summary>
    [ObservableProperty] public partial string? Notice { get; private set; }
    /// <summary>Said above the lines when only the first part of a long diff shows.</summary>
    [ObservableProperty] public partial string? TruncatedNotice { get; private set; }
    [ObservableProperty] public partial int Added { get; private set; }
    [ObservableProperty] public partial int Removed { get; private set; }

    public bool HasChangeCounts => Added > 0 || Removed > 0;
    public string AddedText => "+" + Added;
    public string RemovedText => "−" + Removed;
    public bool HasTruncatedNotice => TruncatedNotice is not null;
    public bool HasNotice => Notice is not null;
    public bool HasRows => Notice is null;

    partial void OnAddedChanged(int value) => NotifyCounts();
    partial void OnRemovedChanged(int value) => NotifyCounts();
    partial void OnTruncatedNoticeChanged(string? value) => OnPropertyChanged(nameof(HasTruncatedNotice));

    partial void OnNoticeChanged(string? value)
    {
        OnPropertyChanged(nameof(HasNotice));
        OnPropertyChanged(nameof(HasRows));
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(HasChangeCounts));
        OnPropertyChanged(nameof(AddedText));
        OnPropertyChanged(nameof(RemovedText));
    }

    private void Reload() => _ui.Post(() => _ = RefreshAsync());

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
        try
        {
            diff = Request.Commit is { } commit
                ? await _git.GetCommitFileDiffAsync(Request.RepositoryRoot, commit, Request.Path, Request.OriginalPath)
                : await _git.GetFileDiffAsync(Request.RepositoryRoot, Request.Path, Request.Staged, Request.OriginalPath);
        }
        catch (Exception)
        {
            diff = null;
        }
        if (version != _version || _disposed) return;
        Apply(diff);
    }

    private void Apply(GitFileDiff? diff)
    {
        TruncatedNotice = null;
        Added = diff?.Added ?? 0;
        Removed = diff?.Removed ?? 0;
        if (diff is null or { Kind: not GitFileDiffKind.Text })
        {
            Rows = [];
            CodeText = "";
            Marks = [];
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
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var watch in _watches) watch.Dispose();
        _watches.Clear();
    }
}
