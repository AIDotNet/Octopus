using Octopus.Git;

namespace Octopus.App.Explorer.Git;

internal static class GitKindInfo
{
    /// <summary>The one-letter status of a change, the way source control panels show it.</summary>
    public static string Letter(GitChangeKind kind) => kind switch
    {
        GitChangeKind.Modified => "M",
        GitChangeKind.Added => "A",
        GitChangeKind.Deleted => "D",
        GitChangeKind.Renamed => "R",
        GitChangeKind.Conflicted => "U",
        _ => "?",
    };
}
