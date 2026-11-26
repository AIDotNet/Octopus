using System.Text.RegularExpressions;
using Octopus.App.Common;
using Octopus.Git;
using TKCode.Arc;

namespace Octopus.App.Explorer.Git;

/// <summary>
/// Converts <see cref="GitGraphCommit"/> records (newest first, topological order) into oldest-first
/// <see cref="BranchGraphCommit"/> items for <see cref="BranchGraph"/>, inferring lane branch names along
/// first-parent chains and merge subjects so branch splits and merges draw cleanly.
/// </summary>
internal static partial class GitBranchGraphBuilder
{
    [GeneratedRegex(@"^Merge (?:remote-tracking )?branch '(?<name>[^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex MergeBranchRegex();

    [GeneratedRegex(@"^Merge pull request #\d+ from (?<name>\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex MergePrRegex();

    public static IReadOnlyList<BranchGraphCommit> Build(
        IReadOnlyList<GitGraphCommit> commits,
        string? currentBranch,
        DateTimeOffset? now = null)
    {
        if (commits.Count == 0) return [];

        var referenceNow = now ?? DateTimeOffset.Now;
        var primaryBranch = ResolvePrimaryBranch(commits, currentBranch);
        var assigned = new Dictionary<string, string>(commits.Count, StringComparer.Ordinal);
        var included = new HashSet<string>(commits.Count, StringComparer.Ordinal);
        foreach (var commit in commits) included.Add(commit.Hash);

        // Commits arrive newest first in topological order, so children are always visited before their parents.
        foreach (var commit in commits)
        {
            var branch = ChooseBranch(commit, assigned, primaryBranch, currentBranch);
            assigned[commit.Hash] = branch;

            if (commit.ParentHashes.Count == 0) continue;

            var firstParent = commit.ParentHashes[0];
            if (!assigned.ContainsKey(firstParent) || string.Equals(branch, primaryBranch, StringComparison.Ordinal))
            {
                assigned[firstParent] = branch;
            }

            for (var i = 1; i < commit.ParentHashes.Count; i++)
            {
                var parent = commit.ParentHashes[i];
                if (assigned.ContainsKey(parent)) continue;
                var inferred = InferMergedBranchName(commit.Subject, parent);
                if (string.Equals(inferred, branch, StringComparison.Ordinal))
                {
                    inferred = FallbackBranchName(parent);
                }
                assigned[parent] = inferred;
            }
        }

        // BranchGraph expects commits oldest first and renders the newest on top.
        var result = new List<BranchGraphCommit>(commits.Count);
        for (var i = commits.Count - 1; i >= 0; i--)
        {
            var commit = commits[i];
            var parentsInSlice = commit.ParentHashes.Where(included.Contains).ToList();
            result.Add(new BranchGraphCommit
            {
                Id = commit.Hash,
                Hash = commit.ShortHash,
                Branch = assigned.GetValueOrDefault(commit.Hash, primaryBranch),
                Parents = parentsInSlice.Count > 0 ? string.Join(' ', parentsInSlice) : null,
                Message = commit.Subject,
                Body = commit.Body,
                Author = commit.Author,
                Tag = commit.Tag,
                Date = RelativeTime.Format(commit.Date, referenceNow),
                Additions = commit.Additions,
                Deletions = commit.Deletions,
                Files = commit.Files,
            });
        }

        return result;
    }

    private static string ResolvePrimaryBranch(IReadOnlyList<GitGraphCommit> commits, string? currentBranch)
    {
        foreach (var commit in commits)
        {
            if (commit.Branches.Contains("main", StringComparer.Ordinal)) return "main";
        }
        foreach (var commit in commits)
        {
            if (commit.Branches.Contains("master", StringComparer.Ordinal)) return "master";
        }
        if (!string.IsNullOrWhiteSpace(currentBranch)) return currentBranch;
        foreach (var commit in commits)
        {
            if (commit.Branches.Count > 0) return commit.Branches[0];
        }
        return "main";
    }

    private static string ChooseBranch(
        GitGraphCommit commit,
        Dictionary<string, string> assigned,
        string primaryBranch,
        string? currentBranch)
    {
        if (commit.Branches.Count > 0)
        {
            if (commit.Branches.Contains(primaryBranch, StringComparer.Ordinal)) return primaryBranch;
            if (!string.IsNullOrWhiteSpace(currentBranch) && commit.Branches.Contains(currentBranch, StringComparer.Ordinal))
            {
                return currentBranch;
            }
            if (assigned.TryGetValue(commit.Hash, out var existing) && commit.Branches.Contains(existing, StringComparer.Ordinal))
            {
                return existing;
            }
            return commit.Branches[0];
        }
        return assigned.TryGetValue(commit.Hash, out var propagated) ? propagated : primaryBranch;
    }

    private static string InferMergedBranchName(string subject, string parentHash)
    {
        var branchMatch = MergeBranchRegex().Match(subject);
        if (branchMatch.Success)
        {
            var name = branchMatch.Groups["name"].Value.Trim();
            if (name.Length > 0) return name;
        }
        var prMatch = MergePrRegex().Match(subject);
        if (prMatch.Success)
        {
            var name = prMatch.Groups["name"].Value.Trim();
            var slash = name.IndexOf('/');
            if (slash >= 0 && slash + 1 < name.Length) name = name[(slash + 1)..];
            if (name.Length > 0) return name;
        }
        return FallbackBranchName(parentHash);
    }

    private static string FallbackBranchName(string hash) =>
        "branch-" + (hash.Length > 7 ? hash[..7] : hash);
}
