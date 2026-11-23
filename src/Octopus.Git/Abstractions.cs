namespace Octopus.Git;

public enum GitChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Untracked,
    Conflicted
}

/// <summary>One changed file of the working tree or the index.</summary>
/// <param name="Path">Relative to the repository root, with forward slashes.</param>
/// <param name="OriginalPath">For a rename: the old path.</param>
/// <param name="IsDirectory">An untracked folder listed as one entry (see <see cref="IGitService.GetStatusAsync"/>); <paramref name="Path"/> has no trailing slash.</param>
public sealed record GitChange(string Path, string FullPath, GitChangeKind Kind, bool IsStaged, string? OriginalPath = null, bool IsDirectory = false);

/// <param name="Branch">The current branch; null on a detached HEAD.</param>
/// <param name="Head">Short hash of HEAD; null in a repository without commits.</param>
public sealed record GitStatus(
    string? Branch,
    string? Head,
    string? Upstream,
    int Ahead,
    int Behind,
    IReadOnlyList<GitChange> Changes);

public sealed record GitCommit(string Hash, string ShortHash, string Subject, string Author, DateTimeOffset Date);

/// <summary>What a command that changes the repository did: success, or git's own words on why not.</summary>
public sealed record GitCommandResult(bool Succeeded, string? Error = null)
{
    public static GitCommandResult Ok { get; } = new(true);
}

/// <summary>A branch: local (<c>main</c>) or of a remote (<c>origin/main</c>).</summary>
/// <param name="Upstream">For a local branch: the remote branch it follows, if any.</param>
public sealed record GitBranch(string Name, bool IsRemote, bool IsCurrent, string ShortHash, string? Upstream, DateTimeOffset? Date);

/// <summary>A file a commit changed, against its first parent (a root commit: against nothing).</summary>
public sealed record GitCommitFile(string Path, GitChangeKind Kind, string? OriginalPath = null);

/// <summary>One worktree reported by <c>git worktree list --porcelain</c>.</summary>
/// <param name="Branch">Short branch name (without <c>refs/heads/</c>); null when detached or bare.</param>
public sealed record GitWorktree(
    string Path,
    string? Head,
    string? Branch,
    bool IsBare,
    bool IsDetached,
    bool IsLocked,
    bool IsPrunable,
    bool IsMain);

/// <summary>
/// Read-only git snapshot of a linked worktree relative to the repository's default branch and remotes, used to
/// evaluate automatic and manual workspace cleanup rules safely.
/// </summary>
public sealed record GitWorkspaceCleanupProbe(
    string? DefaultBranch,
    string? DefaultRef,
    string? HeadHash,
    int CommitsAheadOfDefault,
    bool IsAncestorOfDefault,
    bool IsMergedByCommitOrPatch,
    bool IsMergedIntoRemoteDefault,
    bool HasRemotes,
    int UnpushedCommitCount,
    DateTimeOffset? LastCommitDate,
    bool HasUncommittedChanges,
    int UncommittedChangeCount,
    bool StatusUnavailable);

/// <summary>
/// A git repository, through the user's own <c>git</c>. The <c>Get</c> methods only read; the others change the
/// repository the way the same git command in a terminal would. Commands that reach a remote use the login shell's
/// PATH and never ask for a password on a terminal (there is none): credential helpers and the SSH agent answer, or
/// the command fails with git's words.
/// </summary>
public interface IGitService
{
    /// <summary>The root of the repository that contains <paramref name="directory"/>; null when it is not in one or git is not installed.</summary>
    Task<string?> FindRepositoryRootAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Branch, upstream distance and changed files; null when the command fails. Every untracked file is listed; when
    /// that takes longer than two seconds (a huge repository with nothing ignored) the repository is read with untracked
    /// folders as single entries from then on.
    /// </summary>
    Task<GitStatus?> GetStatusAsync(string repositoryRoot, CancellationToken cancellationToken = default);

    /// <summary>The latest commits of the current branch, newest first; empty when there are none.</summary>
    Task<IReadOnlyList<GitCommit>> GetLogAsync(string repositoryRoot, int count = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// The diff of one file, ready to show: staged (index against HEAD) or unstaged (working tree against index); an
    /// untracked file is all added. Binary and oversized files give a kind without lines. Null when git fails.
    /// <paramref name="originalPath"/> (staged renames) lets git pair the old and new name.
    /// </summary>
    Task<GitFileDiff?> GetFileDiffAsync(string repositoryRoot, string path, bool staged, string? originalPath = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The directories git keeps its state in for the repository that contains <paramref name="directory"/>: the real
    /// git directory (a file named <c>.git</c> in a worktree points elsewhere) and the common one that holds the refs.
    /// Null when it is not in a repository.
    /// </summary>
    Task<GitDirectories?> GetGitDirectoriesAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>Puts the files as they are now into the index: changed, new and deleted alike (<c>git add -A</c>). Paths are relative to the root.</summary>
    Task<GitCommandResult> StageAsync(string repositoryRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Takes the files out of the index again, keeping the working tree as it is (<c>git reset</c>).</summary>
    Task<GitCommandResult> UnstageAsync(string repositoryRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits what is staged with <paramref name="message"/>. The repository's hooks run, with the login shell's PATH,
    /// and may take a while; the error is git's (or the hook's) last words when it fails.
    /// </summary>
    Task<GitCommandResult> CommitAsync(string repositoryRoot, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the last commit with one that also has what is staged (<c>git commit --amend</c>); an empty
    /// <paramref name="message"/> keeps the old message.
    /// </summary>
    Task<GitCommandResult> AmendAsync(string repositoryRoot, string? message, CancellationToken cancellationToken = default);

    /// <summary>The full message of the last commit; null when there is none.</summary>
    Task<string?> GetLastCommitMessageAsync(string repositoryRoot, CancellationToken cancellationToken = default);

    /// <summary>Puts the working tree back to the index for these tracked files, throwing their unstaged changes away.</summary>
    Task<GitCommandResult> DiscardAsync(string repositoryRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Deletes these untracked files and folders (<c>git clean -fd</c>, never ignored files).</summary>
    Task<GitCommandResult> DeleteUntrackedAsync(string repositoryRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Local branches, then remote ones, each by name; the remotes' <c>HEAD</c> pointers are left out.</summary>
    Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryRoot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a branch out. A remote branch (<c>origin/feature</c>) becomes a local one of the same short name that
    /// follows it, or the local one that already exists.
    /// </summary>
    Task<GitCommandResult> SwitchBranchAsync(string repositoryRoot, string name, bool isRemote, CancellationToken cancellationToken = default);

    /// <summary>Starts a branch at HEAD and checks it out.</summary>
    Task<GitCommandResult> CreateBranchAsync(string repositoryRoot, string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes a local branch; without <paramref name="force"/> git refuses one that is not merged.</summary>
    Task<GitCommandResult> DeleteBranchAsync(string repositoryRoot, string name, bool force, CancellationToken cancellationToken = default);

    /// <summary>True when the name is one git accepts for a branch (<c>git check-ref-format --branch</c>).</summary>
    Task<bool> IsValidBranchNameAsync(string repositoryRoot, string name, CancellationToken cancellationToken = default);

    /// <summary>Reads every remote (<c>git fetch --all --prune</c>).</summary>
    Task<GitCommandResult> FetchAsync(string repositoryRoot, CancellationToken cancellationToken = default);

    /// <summary>Brings the upstream's commits in (<c>git pull</c>, merging or rebasing as the repository is configured; a merge commit takes git's message).</summary>
    Task<GitCommandResult> PullAsync(string repositoryRoot, CancellationToken cancellationToken = default);

    /// <summary>Sends the branch to its upstream; a branch without one is pushed to the first remote and follows it from then on.</summary>
    Task<GitCommandResult> PushAsync(string repositoryRoot, CancellationToken cancellationToken = default);

    /// <summary>Puts every change, untracked files included, away in a new stash.</summary>
    Task<GitCommandResult> StashAsync(string repositoryRoot, string? message, CancellationToken cancellationToken = default);

    /// <summary>Applies the latest stash and drops it (git keeps it when it does not apply cleanly).</summary>
    Task<GitCommandResult> StashPopAsync(string repositoryRoot, CancellationToken cancellationToken = default);

    /// <summary>How many stashes there are.</summary>
    Task<int> GetStashCountAsync(string repositoryRoot, CancellationToken cancellationToken = default);

    /// <summary>The files a commit changed, against its first parent.</summary>
    Task<IReadOnlyList<GitCommitFile>> GetCommitFilesAsync(string repositoryRoot, string commit, CancellationToken cancellationToken = default);

    /// <summary>The diff one commit made to one file (against its first parent), ready to show; null when git fails.</summary>
    Task<GitFileDiff?> GetCommitFileDiffAsync(string repositoryRoot, string commit, string path, string? originalPath = null, CancellationToken cancellationToken = default);

    /// <summary>Every worktree linked to the repository (<c>git worktree list --porcelain</c>), main worktree first.</summary>
    Task<IReadOnlyList<GitWorktree>> GetWorktreesAsync(string repositoryRoot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a git worktree at <paramref name="path"/>. When <paramref name="createBranch"/> is true, creates a new
    /// branch <paramref name="branch"/> starting from <paramref name="baseRef"/> (or HEAD when null). Otherwise checks out
    /// the existing local or remote branch <paramref name="branch"/>.
    /// </summary>
    Task<GitCommandResult> AddWorktreeAsync(
        string repositoryRoot,
        string path,
        string branch,
        string? baseRef = null,
        bool createBranch = true,
        bool isRemote = false,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a linked worktree (<c>git worktree remove</c>); without <paramref name="force"/> git refuses dirty worktrees.</summary>
    Task<GitCommandResult> RemoveWorktreeAsync(string repositoryRoot, string path, bool force = false, CancellationToken cancellationToken = default);

    /// <summary>Cleans stale administrative files for deleted worktrees (<c>git worktree prune</c>).</summary>
    Task<GitCommandResult> PruneWorktreesAsync(string repositoryRoot, CancellationToken cancellationToken = default);

    /// <summary>Full 40-character hash of <c>HEAD</c> in <paramref name="repositoryRoot"/>; null when unborn or unavailable.</summary>
    Task<string?> GetHeadHashAsync(string repositoryRoot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes a linked worktree's git status, merge relationship to the repository's default branch, unpushed commits,
    /// and last commit timestamp for workspace cleanup evaluation.
    /// </summary>
    Task<GitWorkspaceCleanupProbe> ProbeWorkspaceCleanupAsync(
        string repositoryRoot,
        string worktreePath,
        string? baseRef = null,
        CancellationToken cancellationToken = default);
}

/// <param name="GitDirectory">Holds HEAD and index (per worktree).</param>
/// <param name="CommonDirectory">Holds refs and packed-refs; the same as <paramref name="GitDirectory"/> outside a linked worktree.</param>
public sealed record GitDirectories(string GitDirectory, string CommonDirectory);
