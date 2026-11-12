using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Files;
using Octopus.App.Localization;
using Octopus.Git;

namespace Octopus.App.Explorer.Git;

/// <summary>The source control tab: branch, grouped changes and the latest commits. Read only.</summary>
public sealed partial class GitPanelViewModel : ObservableObject
{
    public const int CommitCount = 30;

    private readonly IGitService _git;
    private string? _root;
    private int _version;

    public GitPanelViewModel(IGitService git)
    {
        _git = git;
    }

    public ObservableCollection<GitChangeGroupViewModel> Groups { get; } = [];
    public ObservableCollection<GitCommitRowViewModel> Commits { get; } = [];

    [ObservableProperty] public partial string BranchName { get; private set; } = "";
    [ObservableProperty] public partial string AheadBehind { get; private set; } = "";
    [ObservableProperty] public partial bool IsClean { get; private set; }
    [ObservableProperty] public partial bool IsRefreshing { get; private set; }
    [ObservableProperty] public partial GitChangeRowViewModel? SelectedChange { get; set; }
    [ObservableProperty] public partial GitCommitRowViewModel? SelectedCommit { get; set; }

    public bool HasAheadBehind => AheadBehind.Length > 0;
    public bool HasCommits => Commits.Count > 0;
    public bool HasNoCommits => Commits.Count == 0 && _root is not null;

    /// <summary>Raised after every status read (null when the repository could not be read).</summary>
    public event EventHandler<GitStatus?>? StatusChanged;

    /// <summary>Raised with the absolute path of a changed file to open.</summary>
    public event EventHandler<string>? OpenRequested;

    partial void OnSelectedChangeChanged(GitChangeRowViewModel? oldValue, GitChangeRowViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    partial void OnSelectedCommitChanged(GitCommitRowViewModel? oldValue, GitCommitRowViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    [RelayCommand]
    private Task RefreshNow() => RefreshAsync();

    partial void OnAheadBehindChanged(string value) => OnPropertyChanged(nameof(HasAheadBehind));

    public void SetRepository(string? root)
    {
        _version++;
        _root = root;
        Groups.Clear();
        Commits.Clear();
        BranchName = "";
        AheadBehind = "";
        IsClean = false;
        OnPropertyChanged(nameof(HasCommits));
        OnPropertyChanged(nameof(HasNoCommits));
    }

    /// <summary>Reads the status (and, when asked, the latest commits). A newer refresh makes an older one drop its result.</summary>
    public async Task RefreshAsync(bool includeLog = true)
    {
        var root = _root;
        if (root is null) return;
        var version = ++_version;
        IsRefreshing = true;
        try
        {
            var statusTask = _git.GetStatusAsync(root);
            var logTask = includeLog ? _git.GetLogAsync(root, CommitCount) : null;
            var status = await statusTask;
            var log = logTask is null ? null : await logTask;
            if (version != _version) return;
            Apply(status, log);
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception)
        {
            if (version == _version) StatusChanged?.Invoke(this, null);
        }
        finally
        {
            if (version == _version) IsRefreshing = false;
        }
    }

    /// <summary>Raised when a change is clicked: its read-only diff should show.</summary>
    public event EventHandler<DiffTabRequest>? DiffRequested;

    /// <summary>Click on a change: its diff opens (staged and unstaged changes of one file are two diffs). A folder has none.</summary>
    public void RequestDiff(GitChangeRowViewModel? row)
    {
        if (row is null || row.Change.IsDirectory || _root is null) return;
        DiffRequested?.Invoke(this, DiffTabRequest.For(_root, row.Change));
    }

    /// <summary>Double-click on a change: the file opens, unless it was deleted.</summary>
    public void RequestOpen(GitChangeRowViewModel? row)
    {
        if (row is null || row.Kind == GitChangeKind.Deleted || row.Change.IsDirectory) return;
        OpenRequested?.Invoke(this, row.FullPath);
    }

    private void Apply(GitStatus? status, IReadOnlyList<GitCommit>? log)
    {
        if (status is not null)
        {
            BranchName = status.Branch ?? status.Head ?? AppLangResources.Get(AppLangResourceKind.ExplorerDetached);
            AheadBehind = status.Upstream is null || (status.Ahead == 0 && status.Behind == 0)
                ? ""
                : string.Join(' ', new[] { status.Ahead > 0 ? $"↑{status.Ahead}" : null, status.Behind > 0 ? $"↓{status.Behind}" : null }.OfType<string>());

            var staged = status.Changes.Where(c => c.IsStaged).Select(c => new GitChangeRowViewModel(c)).ToList();
            var changed = status.Changes.Where(c => !c.IsStaged && c.Kind != GitChangeKind.Untracked).Select(c => new GitChangeRowViewModel(c)).ToList();
            var untracked = status.Changes.Where(c => c.Kind == GitChangeKind.Untracked).Select(c => new GitChangeRowViewModel(c)).ToList();
            Groups.Clear();
            AddGroup(AppLangResourceKind.ExplorerStagedChanges, staged);
            AddGroup(AppLangResourceKind.ExplorerChanges, changed);
            AddGroup(AppLangResourceKind.ExplorerUntracked, untracked);
            IsClean = Groups.Count == 0;
        }
        if (log is not null)
        {
            Commits.Clear();
            foreach (var commit in log) Commits.Add(new GitCommitRowViewModel(commit));
            OnPropertyChanged(nameof(HasCommits));
            OnPropertyChanged(nameof(HasNoCommits));
        }
    }

    private void AddGroup(AppLangResourceKind title, List<GitChangeRowViewModel> rows)
    {
        if (rows.Count > 0) Groups.Add(new GitChangeGroupViewModel(AppLangResources.Get(title), rows));
    }
}
