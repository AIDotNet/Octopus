using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Files;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Git;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Explorer.Git;

/// <summary>
/// The source control tab: branch (switch, create, delete), pull / push / fetch, stash, a commit box (with amend),
/// collapsible working-tree changes that can be staged, unstaged and discarded, collapsible scroll-loaded recent
/// commits (each opening to the files it changed), and an on-demand collapsible branch graph. Everything in the
/// changes + commits list is one flat list (<see cref="Items"/>) that the view virtualizes, so thousands of changed
/// files stay cheap. Every change to the repository goes through <see cref="IGitService"/>, one at a time
/// (<see cref="IsBusy"/>); what git says when one fails shows under the commit button.
/// </summary>
public sealed partial class GitPanelViewModel : ObservableObject
{
    public const int CommitPageSize = 20;
    public const int CommitCount = CommitPageSize;
    public const int MaxAutoRefreshCommits = 200;
    public const int BranchGraphCommitCount = 20;

    private IGitService _git;
    private readonly IGitService _local;
    private readonly IDialogService? _dialogs;
    private readonly GitChangesSectionHeaderViewModel _changesHeader = new();
    private readonly GitCleanStateRowViewModel _cleanStateRow = new();
    private readonly GitCommitsHeaderViewModel _commitsHeader = new();
    private readonly GitCommitsFooterViewModel _commitsFooter = new();
    private string? _root;
    private int _version;
    private int _branchGraphVersion;
    private bool _branchGraphDirty = true;
    private List<string> _itemKeys = [];
    private Task? _running;
    private Task? _queued;
    private bool _queuedLog;

    public GitPanelViewModel(IGitService git, IDialogService? dialogs = null)
    {
        _git = _local = git;
        _dialogs = dialogs;
    }

    /// <summary>The change groups (staged, changed, untracked), each with its rows.</summary>
    [ObservableProperty] public partial IReadOnlyList<GitChangeGroupViewModel> Groups { get; private set; } = [];

    [ObservableProperty] public partial IReadOnlyList<GitCommitRowViewModel> Commits { get; private set; } = [];

    /// <summary>Local branches, then remote ones; read with the commits (when the Git tab shows).</summary>
    [ObservableProperty] public partial IReadOnlyList<GitBranch> Branches { get; private set; } = [];

    /// <summary>Commits formatted for <see cref="BranchGraph"/>, loaded lazily when the branch graph section is expanded.</summary>
    [ObservableProperty] public partial IReadOnlyList<BranchGraphCommit> BranchGraphCommits { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StashPopCommand))]
    public partial int StashCount { get; private set; }

    /// <summary>
    /// What the list shows, in order: the collapsible changes section header and its clean state or groups/rows,
    /// then the collapsible commits section header, the loaded commits (and opened files), and the load-more footer.
    /// Replaced whole (one reset for the list) and only when something in it changed.
    /// </summary>
    [ObservableProperty] public partial IReadOnlyList<object> Items { get; private set; } = [];

    [ObservableProperty] public partial string BranchName { get; private set; } = "";
    [ObservableProperty] public partial string AheadBehind { get; private set; } = "";
    [ObservableProperty] public partial bool IsClean { get; private set; }
    [ObservableProperty] public partial bool IsRefreshing { get; private set; }
    [ObservableProperty] public partial bool IsChangesExpanded { get; set; } = true;
    [ObservableProperty] public partial bool IsCommitsExpanded { get; set; } = true;
    [ObservableProperty] public partial bool IsBranchGraphExpanded { get; set; }
    [ObservableProperty] public partial bool HasMoreCommits { get; private set; }
    [ObservableProperty] public partial bool IsLoadingMoreCommits { get; private set; }
    [ObservableProperty] public partial bool IsLoadingBranchGraph { get; private set; }
    [ObservableProperty] public partial GitChangeRowViewModel? SelectedChange { get; set; }
    [ObservableProperty] public partial GitCommitRowViewModel? SelectedCommit { get; set; }
    [ObservableProperty] public partial GitCommitFileRowViewModel? SelectedCommitFile { get; set; }

    /// <summary>The message typed into the commit box.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    public partial string CommitMessage { get; set; } = "";

    /// <summary>The commit button replaces the last commit instead of adding one.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    public partial bool IsAmend { get; set; }

    /// <summary>A git command that changes the repository is running: the buttons wait.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(StageCommand), nameof(UnstageCommand), nameof(StageGroupCommand),
        nameof(ToggleStageCommand), nameof(DiscardCommand), nameof(DiscardGroupCommand), nameof(PullCommand), nameof(PushCommand),
        nameof(FetchCommand), nameof(StashCommand), nameof(StashPopCommand), nameof(SwitchBranchCommand), nameof(NewBranchCommand),
        nameof(DeleteBranchCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>What is running ("Pushing…"); empty when nothing is.</summary>
    [ObservableProperty] public partial string BusyText { get; private set; } = "";

    /// <summary>Why the last command failed (git's words); null after a success.</summary>
    [ObservableProperty] public partial string? GitError { get; private set; }

    public bool HasGitError => GitError is not null;
    public bool HasRepository => _root is not null;
    public bool HasAheadBehind => AheadBehind.Length > 0;
    public bool HasCommits => Commits.Count > 0;
    public bool HasNoCommits => Commits.Count == 0 && _root is not null;
    public bool HasStaged => Groups.Any(g => g.IsStaged);
    public bool ShowBusy => IsBusy && BusyText.Length > 0;
    public bool HasBranchGraphCommits => BranchGraphCommits.Count > 0;
    public bool ShowBranchGraphControl => IsBranchGraphExpanded && BranchGraphCommits.Count > 0;
    public bool ShowBranchGraphEmpty => IsBranchGraphExpanded && !IsLoadingBranchGraph && BranchGraphCommits.Count == 0 && _root is not null;
    public LucideIconKind BranchGraphChevronIcon => IsBranchGraphExpanded ? LucideIconKind.ChevronDown : LucideIconKind.ChevronRight;
    public string BranchGraphTitle => BranchName.Length > 0 ? BranchName : AppLangResources.Get(AppLangResourceKind.ExplorerBranchGraph);
    public IEnumerable<GitBranch> LocalBranches => Branches.Where(b => !b.IsRemote);
    public IEnumerable<GitBranch> RemoteBranches => Branches.Where(b => b.IsRemote);

    public BranchGraphLabels BranchGraphLabels { get; } = new()
    {
        JustNow = AppLangResources.Get(AppLangResourceKind.ExplorerAgoNow),
        Files = static count => AppLangResources.Format(AppLangResourceKind.ExplorerCommitFiles, count),
        HistorySummary = static (commits, branches) => AppLangResources.Format(AppLangResourceKind.ExplorerBranchGraphSummary, commits, branches),
    };

    /// <summary>With nothing staged, the button stages every change first (what a person who types a message and presses it means).</summary>
    public string CommitButtonText => AppLangResources.Get(
        IsAmend ? AppLangResourceKind.ExplorerAmend
        : HasStaged || IsClean ? AppLangResourceKind.ExplorerCommit
        : AppLangResourceKind.ExplorerStageAllAndCommit);

    /// <summary>Raised after every status read (null when the repository could not be read).</summary>
    public event EventHandler<GitStatus?>? StatusChanged;

    /// <summary>Raised with the absolute path of a changed file to open.</summary>
    public event EventHandler<string>? OpenRequested;

    /// <summary>Raised when a change, or a file of a commit, is clicked: its read-only diff should show.</summary>
    public event EventHandler<DiffTabRequest>? DiffRequested;

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

    partial void OnSelectedCommitFileChanged(GitCommitFileRowViewModel? oldValue, GitCommitFileRowViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    partial void OnGroupsChanged(IReadOnlyList<GitChangeGroupViewModel> value)
    {
        _changesHeader.ChangeCount = value.Sum(g => g.Count);
        OnPropertyChanged(nameof(HasStaged));
        OnPropertyChanged(nameof(CommitButtonText));
        CommitCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCleanChanged(bool value) => OnPropertyChanged(nameof(CommitButtonText));

    partial void OnBranchNameChanged(string value) => OnPropertyChanged(nameof(BranchGraphTitle));

    partial void OnCommitsChanged(IReadOnlyList<GitCommitRowViewModel> value)
    {
        SyncCommitsHeader();
        OnPropertyChanged(nameof(HasCommits));
        OnPropertyChanged(nameof(HasNoCommits));
        CommitCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasMoreCommitsChanged(bool value)
    {
        SyncCommitsHeader();
        _commitsFooter.HasMore = value;
    }

    partial void OnIsLoadingMoreCommitsChanged(bool value) => _commitsFooter.IsLoading = value;

    partial void OnIsChangesExpandedChanged(bool value)
    {
        _changesHeader.IsExpanded = value;
        Rebuild();
    }

    partial void OnIsCommitsExpandedChanged(bool value)
    {
        _commitsHeader.IsExpanded = value;
        Rebuild();
    }

    partial void OnIsBranchGraphExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(BranchGraphChevronIcon));
        OnPropertyChanged(nameof(ShowBranchGraphControl));
        OnPropertyChanged(nameof(ShowBranchGraphEmpty));
    }

    partial void OnIsLoadingBranchGraphChanged(bool value) => OnPropertyChanged(nameof(ShowBranchGraphEmpty));

    partial void OnBranchGraphCommitsChanged(IReadOnlyList<BranchGraphCommit> value)
    {
        OnPropertyChanged(nameof(HasBranchGraphCommits));
        OnPropertyChanged(nameof(ShowBranchGraphControl));
        OnPropertyChanged(nameof(ShowBranchGraphEmpty));
    }

    private void SyncCommitsHeader()
    {
        _commitsHeader.HasNoCommits = HasNoCommits;
        _commitsHeader.CountText = Commits.Count == 0
            ? ""
            : HasMoreCommits ? $"{Commits.Count}+" : Commits.Count.ToString();
    }

    partial void OnBranchesChanged(IReadOnlyList<GitBranch> value)
    {
        OnPropertyChanged(nameof(LocalBranches));
        OnPropertyChanged(nameof(RemoteBranches));
    }

    partial void OnIsAmendChanged(bool value)
    {
        OnPropertyChanged(nameof(CommitButtonText));
        // Amending starts from the message there is, as `git commit --amend` does.
        if (value && string.IsNullOrWhiteSpace(CommitMessage) && _root is { } root) _ = FillLastMessageAsync(root);
    }

    private async Task FillLastMessageAsync(string root)
    {
        var message = await _git.GetLastCommitMessageAsync(root);
        if (IsAmend && _root == root && string.IsNullOrWhiteSpace(CommitMessage) && message is not null) CommitMessage = message;
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowBusy));
    partial void OnBusyTextChanged(string value) => OnPropertyChanged(nameof(ShowBusy));
    partial void OnGitErrorChanged(string? value) => OnPropertyChanged(nameof(HasGitError));
    partial void OnAheadBehindChanged(string value) => OnPropertyChanged(nameof(HasAheadBehind));

    [RelayCommand]
    private Task RefreshNow() => RefreshAsync();

    [RelayCommand]
    private void ToggleAmend() => IsAmend = !IsAmend;

    [RelayCommand]
    private void ToggleChangesSection() => IsChangesExpanded = !IsChangesExpanded;

    [RelayCommand]
    private void ToggleCommitsSection() => IsCommitsExpanded = !IsCommitsExpanded;

    [RelayCommand]
    private Task ToggleBranchGraph()
    {
        IsBranchGraphExpanded = !IsBranchGraphExpanded;
        return IsBranchGraphExpanded && _branchGraphDirty ? LoadBranchGraphAsync() : Task.CompletedTask;
    }

    [RelayCommand]
    private Task RefreshBranchGraph() => LoadBranchGraphAsync(force: true);

    [RelayCommand]
    public Task LoadMoreCommitsAsync() => LoadMoreCommitsCoreAsync();

    /// <param name="service">The git service of the machine the repository is on; null: this computer's.</param>
    public void SetRepository(string? root, IGitService? service = null)
    {
        _version++;
        _branchGraphVersion++;
        _branchGraphDirty = true;
        _git = service ?? _local;
        _root = root;
        // A read of the previous repository still running is dropped (by the version); the new one need not wait for it.
        _running = null;
        _queued = null;
        _queuedLog = false;
        Groups = [];
        Commits = [];
        Branches = [];
        BranchGraphCommits = [];
        StashCount = 0;
        HasMoreCommits = false;
        IsLoadingMoreCommits = false;
        IsLoadingBranchGraph = false;
        IsBranchGraphExpanded = false;
        _itemKeys = [];
        Items = [];
        SelectedChange = null;
        SelectedCommit = null;
        SelectedCommitFile = null;
        BranchName = "";
        AheadBehind = "";
        IsClean = false;
        IsAmend = false;
        GitError = null;
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(HasNoCommits));
        OnPropertyChanged(nameof(ShowBranchGraphEmpty));
        SyncCommitsHeader();
    }

    /// <summary>
    /// Reads the status (and, when asked, the latest commits, the branches and the stashes). One read runs at a time:
    /// asked again while one runs, a single further read follows it (with the commits when any of the asks wanted them),
    /// and the returned task completes after that one. A file watcher, the window coming to the front and a command
    /// finishing often ask at once; each would otherwise start up to four git processes.
    /// </summary>
    public Task RefreshAsync(bool includeLog = true)
    {
        if (_root is null) return Task.CompletedTask;
        if (_running is { IsCompleted: false })
        {
            _queuedLog |= includeLog;
            return _queued ??= RefreshAfterRunningAsync();
        }
        return _running = ReadAsync(includeLog);
    }

    private async Task RefreshAfterRunningAsync()
    {
        try
        {
            await _running!;
        }
        catch (Exception)
        {
            // Its failure was already reported; the next read starts afresh.
        }
        var includeLog = _queuedLog;
        _queuedLog = false;
        _queued = null;
        var next = ReadAsync(includeLog);
        _running = next;
        await next;
    }

    private async Task ReadAsync(bool includeLog)
    {
        var root = _root;
        if (root is null) return;
        var version = ++_version;
        IsRefreshing = true;
        var refreshGraph = false;
        try
        {
            var commitLimit = Commits.Count > CommitPageSize ? Math.Min(Commits.Count, MaxAutoRefreshCommits) : CommitPageSize;
            var statusTask = _git.GetStatusAsync(root);
            var logTask = includeLog ? _git.GetLogPageAsync(root, commitLimit + 1, skip: 0) : null;
            var branchesTask = includeLog ? _git.GetBranchesAsync(root) : null;
            var stashTask = includeLog ? _git.GetStashCountAsync(root) : null;
            var status = await statusTask;
            var logRaw = logTask is null ? null : await logTask;
            var branches = branchesTask is null ? null : await branchesTask;
            var stashes = stashTask is null ? (int?)null : await stashTask;
            if (version != _version) return;
            if (branches is not null && !branches.SequenceEqual(Branches)) Branches = branches;
            if (stashes is { } count) StashCount = count;
            IReadOnlyList<GitCommit>? log = null;
            bool? hasMore = null;
            if (logRaw is not null)
            {
                hasMore = logRaw.Count > commitLimit;
                log = hasMore.Value ? logRaw.Take(commitLimit).ToList() : logRaw;
                _branchGraphDirty = true;
                refreshGraph = IsBranchGraphExpanded;
            }
            Apply(status, log, hasMore);
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
        if (refreshGraph && version == _version) await LoadBranchGraphAsync(force: true);
    }

    private async Task LoadMoreCommitsCoreAsync()
    {
        if (_root is not { } root || !HasMoreCommits || IsLoadingMoreCommits || IsRefreshing) return;
        var version = _version;
        var skip = Commits.Count;
        IsLoadingMoreCommits = true;
        try
        {
            var raw = await _git.GetLogPageAsync(root, CommitPageSize + 1, skip);
            if (version != _version || _root != root) return;
            var hasMore = raw.Count > CommitPageSize;
            var page = hasMore ? raw.Take(CommitPageSize) : raw;
            var existing = Commits.Select(c => c.Commit.Hash).ToHashSet(StringComparer.Ordinal);
            var added = new List<GitCommitRowViewModel>();
            foreach (var commit in page)
            {
                if (existing.Add(commit.Hash)) added.Add(new GitCommitRowViewModel(commit));
            }
            HasMoreCommits = hasMore && added.Count > 0;
            if (added.Count > 0) Commits = [.. Commits, .. added];
        }
        catch (Exception)
        {
            // Keep existing commits intact if loading a page fails.
        }
        finally
        {
            if (version == _version)
            {
                IsLoadingMoreCommits = false;
                Rebuild();
            }
        }
    }

    public async Task LoadBranchGraphAsync(bool force = false)
    {
        if (_root is not { } root || !IsBranchGraphExpanded) return;
        if (!force && !_branchGraphDirty) return;

        var graphVersion = ++_branchGraphVersion;
        var repoVersion = _version;
        IsLoadingBranchGraph = true;
        try
        {
            var commits = await _git.GetBranchGraphAsync(root, BranchGraphCommitCount);
            if (graphVersion != _branchGraphVersion || repoVersion != _version || _root != root) return;
            BranchGraphCommits = GitBranchGraphBuilder.Build(commits, BranchName.Length > 0 ? BranchName : null);
            _branchGraphDirty = false;
        }
        catch (Exception)
        {
            if (graphVersion == _branchGraphVersion && _root == root) BranchGraphCommits = [];
        }
        finally
        {
            if (graphVersion == _branchGraphVersion) IsLoadingBranchGraph = false;
        }
    }

    /// <summary>Click on a change: its diff opens (staged and unstaged changes of one file are two diffs). A folder has none.</summary>
    public void RequestDiff(GitChangeRowViewModel? row)
    {
        if (row is null || row.Change.IsDirectory || _root is null) return;
        DiffRequested?.Invoke(this, DiffTabRequest.For(_root, row.Change));
    }

    /// <summary>Click on a file of an opened commit: what the commit did to it.</summary>
    public void RequestCommitFileDiff(GitCommitFileRowViewModel? row)
    {
        if (row is null || _root is null) return;
        SelectedCommitFile = row;
        DiffRequested?.Invoke(this, DiffTabRequest.ForCommit(_root, row.Owner.Commit.Hash, row.File));
    }

    /// <summary>Double-click on a change: the file opens, unless it was deleted.</summary>
    public void RequestOpen(GitChangeRowViewModel? row)
    {
        if (row is null || row.Kind == GitChangeKind.Deleted || row.Change.IsDirectory) return;
        OpenRequested?.Invoke(this, row.FullPath);
    }

    /// <summary>Click on a commit: it opens to its files (read the first time), or closes again.</summary>
    [RelayCommand]
    public async Task ToggleCommit(GitCommitRowViewModel? row)
    {
        if (row is null || _root is not { } root) return;
        SelectedCommit = row;
        if (row.IsExpanded)
        {
            row.IsExpanded = false;
            Rebuild();
            return;
        }
        if (row.Files is null)
        {
            var files = await _git.GetCommitFilesAsync(root, row.Commit.Hash);
            row.Files = files.Select(f => new GitCommitFileRowViewModel(row, f)).ToList();
        }
        if (_root != root) return;
        row.IsExpanded = true;
        Rebuild();
    }

    /// <summary>The row's button: a staged change is unstaged, any other one staged.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task ToggleStage(GitChangeRowViewModel? row) =>
        row is null ? Task.CompletedTask : row.IsStaged ? Unstage(row) : Stage(row);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Stage(GitChangeRowViewModel? row) =>
        row is null ? Task.CompletedTask : RunAsync(root => _git.StageAsync(root, PathsOf([row])), includeLog: false);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Unstage(GitChangeRowViewModel? row) =>
        row is null ? Task.CompletedTask : RunAsync(root => _git.UnstageAsync(root, PathsOf([row])), includeLog: false);

    /// <summary>The group header's button: the staged group is unstaged whole, the others staged whole.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task StageGroup(GitChangeGroupViewModel? group)
    {
        if (group is null || group.Rows.Count == 0) return Task.CompletedTask;
        var paths = PathsOf(group.Rows);
        return RunAsync(root => group.IsStaged ? _git.UnstageAsync(root, paths) : _git.StageAsync(root, paths), includeLog: false);
    }

    /// <summary>Throws one unstaged change away, after asking: a tracked file goes back to the index, an untracked one is deleted.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Discard(GitChangeRowViewModel? row) => row is { CanDiscard: true } ? DiscardAsync([row], row.Name) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task DiscardGroup(GitChangeGroupViewModel? group) =>
        group is { CanDiscard: true, Rows.Count: > 0 } ? DiscardAsync(group.Rows, null) : Task.CompletedTask;

    private async Task DiscardAsync(IReadOnlyList<GitChangeRowViewModel> rows, string? name)
    {
        var untracked = rows.Where(r => r.IsUntracked).ToList();
        var tracked = rows.Where(r => !r.IsUntracked && !r.IsStaged).ToList();
        if (untracked.Count == 0 && tracked.Count == 0) return;
        var deleting = tracked.Count == 0;
        var title = (deleting, name) switch
        {
            (true, { } one) => AppLangResources.Format(AppLangResourceKind.ExplorerDeleteUntrackedTitle, one),
            (true, null) => AppLangResources.Format(AppLangResourceKind.ExplorerDeleteUntrackedManyTitle, untracked.Count),
            (false, { } one) => AppLangResources.Format(AppLangResourceKind.ExplorerDiscardTitle, one),
            _ => AppLangResources.Format(AppLangResourceKind.ExplorerDiscardManyTitle, rows.Count),
        };
        var description = AppLangResources.Get(deleting ? AppLangResourceKind.ExplorerDeleteUntrackedDescription : AppLangResourceKind.ExplorerDiscardDescription);
        var confirm = AppLangResources.Get(deleting ? AppLangResourceKind.ExplorerDelete : AppLangResourceKind.ExplorerDiscard);
        if (_dialogs is null || !await _dialogs.ConfirmAsync(title, description, confirm, isDanger: true)) return;
        await RunAsync(async root =>
        {
            if (tracked.Count > 0)
            {
                var result = await _git.DiscardAsync(root, PathsOf(tracked));
                if (!result.Succeeded) return result;
            }
            return untracked.Count > 0 ? await _git.DeleteUntrackedAsync(root, untracked.Select(r => r.Change.Path).ToList()) : GitCommandResult.Ok;
        }, includeLog: false);
    }

    private bool IsIdle() => !IsBusy;

    /// <summary>Commits what is staged (with nothing staged, every change is staged first), or amends the last commit.</summary>
    [RelayCommand(CanExecute = nameof(CanCommit))]
    private Task Commit()
    {
        var message = CommitMessage;
        var amend = IsAmend;
        var stageAll = !amend && !HasStaged;
        return RunAsync(async root =>
        {
            if (stageAll)
            {
                var staged = await _git.StageAsync(root, ["."]);
                if (!staged.Succeeded) return staged;
            }
            var result = amend ? await _git.AmendAsync(root, message) : await _git.CommitAsync(root, message);
            if (result.Succeeded)
            {
                // Typed again while the hooks ran: keep what is there now.
                if (CommitMessage == message) CommitMessage = "";
                IsAmend = false;
            }
            return result;
        }, includeLog: true, AppLangResourceKind.ExplorerBusyCommit);
    }

    private bool CanCommit() => !IsBusy && _root is not null && (IsAmend
        ? Commits.Count > 0
        : !string.IsNullOrWhiteSpace(CommitMessage) && Groups.Count > 0);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Pull() => RunAsync(root => _git.PullAsync(root), includeLog: true, AppLangResourceKind.ExplorerBusyPull);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Push() => RunAsync(root => _git.PushAsync(root), includeLog: true, AppLangResourceKind.ExplorerBusyPush);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Fetch() => RunAsync(root => _git.FetchAsync(root), includeLog: true, AppLangResourceKind.ExplorerBusyFetch);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Stash() => RunAsync(root => _git.StashAsync(root, null), includeLog: true);

    [RelayCommand(CanExecute = nameof(CanStashPop))]
    private Task StashPop() => RunAsync(root => _git.StashPopAsync(root), includeLog: true);

    private bool CanStashPop() => !IsBusy && StashCount > 0;

    /// <summary>Checks a branch out; a remote one becomes a local branch that follows it.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task SwitchBranch(GitBranch? branch) =>
        branch is null || branch.IsCurrent ? Task.CompletedTask : RunAsync(root => _git.SwitchBranchAsync(root, branch.Name, branch.IsRemote), includeLog: true);

    /// <summary>Asks for a name and starts a branch there at HEAD.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task NewBranch()
    {
        if (_dialogs is null || _root is null) return;
        var name = await _dialogs.AskNameAsync(
            AppLangResources.Get(AppLangResourceKind.ExplorerNewBranchTitle),
            AppLangResources.Get(AppLangResourceKind.ExplorerBranchName),
            null,
            AppLangResources.Get(AppLangResourceKind.ExplorerCreate));
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(root => _git.CreateBranchAsync(root, name.Trim()), includeLog: true);
    }

    /// <summary>Deletes a local branch after asking; one that is not merged is deleted only when asked a second time.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task DeleteBranch(GitBranch? branch)
    {
        if (branch is null || branch.IsRemote || branch.IsCurrent || _dialogs is null || _root is null) return;
        if (!await _dialogs.ConfirmAsync(
                AppLangResources.Format(AppLangResourceKind.ExplorerDeleteBranchTitle, branch.Name),
                AppLangResources.Get(AppLangResourceKind.ExplorerDeleteBranchDescription),
                AppLangResources.Get(AppLangResourceKind.ExplorerDelete), isDanger: true))
        {
            return;
        }

        var unmerged = false;
        await RunAsync(async root =>
        {
            var result = await _git.DeleteBranchAsync(root, branch.Name, force: false);
            unmerged = !result.Succeeded && result.Error?.Contains("not fully merged", StringComparison.Ordinal) == true;
            return unmerged ? GitCommandResult.Ok : result;
        }, includeLog: true);
        if (!unmerged) return;
        if (!await _dialogs.ConfirmAsync(
                AppLangResources.Format(AppLangResourceKind.ExplorerDeleteBranchUnmerged, branch.Name),
                AppLangResources.Get(AppLangResourceKind.ExplorerDeleteBranchDescription),
                AppLangResources.Get(AppLangResourceKind.ExplorerDelete), isDanger: true))
        {
            return;
        }

        await RunAsync(root => _git.DeleteBranchAsync(root, branch.Name, force: true), includeLog: true);
    }

    /// <summary>Runs a command that changes the repository, shows git's error when it fails, then reads the repository again.</summary>
    private async Task RunAsync(Func<string, Task<GitCommandResult>> command, bool includeLog, AppLangResourceKind? busy = null)
    {
        if (_root is not { } root || IsBusy) return;
        BusyText = busy is { } text ? AppLangResources.Get(text) : "";
        IsBusy = true;
        GitError = null;
        try
        {
            var result = await command(root);
            if (!result.Succeeded) GitError = AppLangResources.Format(AppLangResourceKind.ExplorerGitFailed, result.Error ?? "");
        }
        catch (Exception e)
        {
            GitError = AppLangResources.Format(AppLangResourceKind.ExplorerGitFailed, e.Message);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
        if (_root == root) await RefreshAsync(includeLog);
    }

    /// <summary>The paths git needs for these rows: a staged rename has its old name too, so unstaging it restores both.</summary>
    private static List<string> PathsOf(IEnumerable<GitChangeRowViewModel> rows)
    {
        var paths = new List<string>();
        foreach (var row in rows)
        {
            paths.Add(row.Change.Path);
            if (row.Change.OriginalPath is { } original) paths.Add(original);
        }
        return paths;
    }

    private void Apply(GitStatus? status, IReadOnlyList<GitCommit>? log, bool? hasMoreCommits)
    {
        if (status is not null)
        {
            BranchName = status.Branch ?? status.Head ?? AppLangResources.Get(AppLangResourceKind.ExplorerDetached);
            AheadBehind = status.Upstream is null || (status.Ahead == 0 && status.Behind == 0)
                ? ""
                : string.Join(' ', new[] { status.Ahead > 0 ? $"↑{status.Ahead}" : null, status.Behind > 0 ? $"↓{status.Behind}" : null }.OfType<string>());

            var groups = new List<GitChangeGroupViewModel>(3);
            AddGroup(groups, AppLangResourceKind.ExplorerStagedChanges, GitChangeGroupKind.Staged, status.Changes.Where(c => c.IsStaged));
            AddGroup(groups, AppLangResourceKind.ExplorerChanges, GitChangeGroupKind.Changed, status.Changes.Where(c => !c.IsStaged && c.Kind != GitChangeKind.Untracked));
            AddGroup(groups, AppLangResourceKind.ExplorerUntracked, GitChangeGroupKind.Untracked, status.Changes.Where(c => c.Kind == GitChangeKind.Untracked));
            if (!SameChanges(Groups, groups)) Groups = groups;
            IsClean = Groups.Count == 0;
        }
        if (log is not null)
        {
            if (!Commits.Select(c => c.Commit.Hash).SequenceEqual(log.Select(c => c.Hash)))
            {
                // Commits that stay keep their row, so an opened one stays open.
                var old = Commits.ToDictionary(c => c.Commit.Hash);
                Commits = log.Select(c => old.TryGetValue(c.Hash, out var row) ? row : new GitCommitRowViewModel(c)).ToList();
            }
            if (hasMoreCommits is { } more) HasMoreCommits = more;
        }
        Rebuild();
    }

    private static void AddGroup(List<GitChangeGroupViewModel> groups, AppLangResourceKind title, GitChangeGroupKind kind, IEnumerable<GitChange> changes)
    {
        var rows = changes.Select(c => new GitChangeRowViewModel(c)).ToList();
        if (rows.Count > 0) groups.Add(new GitChangeGroupViewModel(AppLangResources.Get(title), rows, kind));
    }

    private static bool SameChanges(IReadOnlyList<GitChangeGroupViewModel> before, List<GitChangeGroupViewModel> after) =>
        before.Count == after.Count && before.Zip(after).All(p => p.First.Title == p.Second.Title
            && p.First.Rows.Select(r => r.Change).SequenceEqual(p.Second.Rows.Select(r => r.Change)));

    /// <summary>Lays the collapsible changes and commits sections out as one list; replaced only when its content changed.</summary>
    private void Rebuild()
    {
        if (_root is null)
        {
            if (Items.Count > 0)
            {
                _itemKeys = [];
                Items = [];
            }
            return;
        }

        var items = new List<object>(Groups.Sum(g => g.Rows.Count + 1) + Commits.Count + 4);
        var keys = new List<string>(items.Capacity);

        items.Add(_changesHeader);
        keys.Add(IsChangesExpanded ? "sh+" : "sh-");
        if (IsChangesExpanded)
        {
            if (IsClean)
            {
                items.Add(_cleanStateRow);
                keys.Add("clean");
            }
            else
            {
                foreach (var group in Groups)
                {
                    items.Add(group);
                    keys.Add("g" + group.Title);
                    foreach (var row in group.Rows)
                    {
                        items.Add(row);
                        keys.Add("r" + row.Key);
                    }
                }
            }
        }

        items.Add(_commitsHeader);
        keys.Add(IsCommitsExpanded ? "ch+" : "ch-");
        if (IsCommitsExpanded)
        {
            foreach (var commit in Commits)
            {
                items.Add(commit);
                keys.Add("c" + commit.Commit.Hash + (commit.IsExpanded ? "+" : ""));
                if (!commit.IsExpanded || commit.Files is null) continue;
                foreach (var file in commit.Files)
                {
                    items.Add(file);
                    keys.Add("f" + commit.Commit.Hash + file.File.Path);
                }
            }
            if (HasMoreCommits || IsLoadingMoreCommits)
            {
                items.Add(_commitsFooter);
                keys.Add("more");
            }
        }

        if (keys.SequenceEqual(_itemKeys, StringComparer.Ordinal) && items.SequenceEqual(Items)) return;
        _itemKeys = keys;
        if (SelectedChange is { } selected)
        {
            SelectedChange = Groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Key == selected.Key);
        }
        if (SelectedCommit is { } commitSelected)
        {
            SelectedCommit = Commits.FirstOrDefault(c => c.Commit.Hash == commitSelected.Commit.Hash);
        }
        SyncCommitsHeader();
        Items = items;
    }
}

/// <summary>The collapsible "Working changes" section header in the list.</summary>
public sealed partial class GitChangesSectionHeaderViewModel : ObservableObject
{
    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;
    [ObservableProperty] public partial int ChangeCount { get; set; }

    public bool HasCount => ChangeCount > 0;
    public string CountText => ChangeCount > 0 ? ChangeCount.ToString() : "";
    public LucideIconKind ChevronIcon => IsExpanded ? LucideIconKind.ChevronDown : LucideIconKind.ChevronRight;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ChevronIcon));

    partial void OnChangeCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasCount));
        OnPropertyChanged(nameof(CountText));
    }
}

/// <summary>Shown inside the expanded working changes section when there are no uncommitted changes.</summary>
public sealed class GitCleanStateRowViewModel;

/// <summary>The collapsible "Recent commits" section header in the list, with the note shown when there are none.</summary>
public sealed partial class GitCommitsHeaderViewModel : ObservableObject
{
    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;
    [ObservableProperty] public partial bool HasNoCommits { get; set; }
    [ObservableProperty] public partial string CountText { get; set; } = "";

    public bool HasCount => CountText.Length > 0;
    public bool ShowNoCommits => IsExpanded && HasNoCommits;
    public LucideIconKind ChevronIcon => IsExpanded ? LucideIconKind.ChevronDown : LucideIconKind.ChevronRight;

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ChevronIcon));
        OnPropertyChanged(nameof(ShowNoCommits));
    }

    partial void OnHasNoCommitsChanged(bool value) => OnPropertyChanged(nameof(ShowNoCommits));

    partial void OnCountTextChanged(string value) => OnPropertyChanged(nameof(HasCount));
}

/// <summary>The scroll-loading footer shown at the end of recent commits when more history is available.</summary>
public sealed partial class GitCommitsFooterViewModel : ObservableObject
{
    [ObservableProperty] public partial bool HasMore { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }

    public bool CanLoadMore => HasMore && !IsLoading;

    partial void OnHasMoreChanged(bool value) => OnPropertyChanged(nameof(CanLoadMore));
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(CanLoadMore));
}
