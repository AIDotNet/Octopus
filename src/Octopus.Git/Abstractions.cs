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

/// <summary>Read-only view of a git repository, through the user's own <c>git</c>. Nothing here changes the repository.</summary>
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
}

/// <param name="GitDirectory">Holds HEAD and index (per worktree).</param>
/// <param name="CommonDirectory">Holds refs and packed-refs; the same as <paramref name="GitDirectory"/> outside a linked worktree.</param>
public sealed record GitDirectories(string GitDirectory, string CommonDirectory);
