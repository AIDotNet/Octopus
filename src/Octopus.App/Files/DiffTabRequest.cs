using Octopus.Git;

namespace Octopus.App.Files;

/// <summary>Which change of which repository a diff tab shows. A diff tab is never saved with the project.</summary>
/// <param name="Path">Relative to the repository root, with forward slashes.</param>
public sealed record DiffTabRequest(string RepositoryRoot, string Path, string FullPath, bool Staged, string? OriginalPath = null)
{
    /// <summary>One tab per file and side (staged or not): asking again activates the tab that is open.</summary>
    public string Key => $"diff:{(Staged ? "staged" : "worktree")}:{FullPath}";

    public static DiffTabRequest For(string repositoryRoot, GitChange change) =>
        new(repositoryRoot, change.Path, change.FullPath, change.IsStaged, change.OriginalPath);
}
