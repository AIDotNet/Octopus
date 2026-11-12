using Octopus.Git;

namespace Octopus.App.Files;

/// <summary>Which change of which repository a diff tab shows. A diff tab is never saved with the project.</summary>
/// <param name="Path">Relative to the repository root, with forward slashes.</param>
/// <param name="Commit">The change a commit made (its hash) instead of one in the index or working tree.</param>
public sealed record DiffTabRequest(string RepositoryRoot, string Path, string FullPath, bool Staged, string? OriginalPath = null, string? Commit = null)
{
    /// <summary>One tab per file and side (staged, not staged, or a commit): asking again activates the tab that is open.</summary>
    public string Key => Commit is { } commit ? $"diff:commit:{commit}:{FullPath}" : $"diff:{(Staged ? "staged" : "worktree")}:{FullPath}";

    /// <summary>The change <paramref name="commit"/> made to one of its files.</summary>
    public static DiffTabRequest ForCommit(string repositoryRoot, string commit, GitCommitFile file) =>
        new(repositoryRoot, file.Path, System.IO.Path.Combine(repositoryRoot, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar)), false, file.OriginalPath, commit);

    public static DiffTabRequest For(string repositoryRoot, GitChange change) =>
        new(repositoryRoot, change.Path, change.FullPath, change.IsStaged, change.OriginalPath);
}
