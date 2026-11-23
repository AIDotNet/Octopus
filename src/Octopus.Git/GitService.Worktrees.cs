using System.Globalization;

namespace Octopus.Git;

/// <summary>Linked worktrees: list, add, remove, prune and cleanup inspection.</summary>
public sealed partial class GitService
{
    public async Task<IReadOnlyList<GitWorktree>> GetWorktreesAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["-C", repositoryRoot, "worktree", "list", "--porcelain"], null, cancellationToken).ConfigureAwait(false);
        return result is { Succeeded: true } ? ParseWorktrees(result.StandardOutput) : [];
    }

    public async Task<GitCommandResult> AddWorktreeAsync(
        string repositoryRoot,
        string path,
        string branch,
        string? baseRef = null,
        bool createBranch = true,
        bool isRemote = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new GitCommandResult(false, "Worktree path is empty.");
        }
        var fullPath = Path.GetFullPath(path);
        var branchName = branch.Trim();
        if (createBranch && !await IsValidBranchNameAsync(repositoryRoot, branchName, cancellationToken).ConfigureAwait(false))
        {
            return new GitCommandResult(false, $"'{branchName}' is not a valid branch name.");
        }
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return new GitCommandResult(false, "Branch name is empty.");
        }

        try
        {
            if (Path.GetDirectoryName(fullPath) is { Length: > 0 } parent)
            {
                Directory.CreateDirectory(parent);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new GitCommandResult(false, e.Message);
        }

        List<string> args = ["-C", repositoryRoot, "worktree", "add"];
        if (createBranch)
        {
            args.Add("-b");
            args.Add(branchName);
            args.Add(fullPath);
            if (!string.IsNullOrWhiteSpace(baseRef))
            {
                args.Add(baseRef.Trim());
            }
        }
        else if (isRemote)
        {
            var slash = branchName.IndexOf('/');
            var local = slash >= 0 ? branchName[(slash + 1)..] : branchName;
            var exists = await RunAsync(["-C", repositoryRoot, "rev-parse", "--verify", "-q", "refs/heads/" + local], null, cancellationToken).ConfigureAwait(false);
            if (exists is { Succeeded: true })
            {
                args.Add(fullPath);
                args.Add(local);
            }
            else
            {
                args.Add("--track");
                args.Add("-b");
                args.Add(local);
                args.Add(fullPath);
                args.Add(branchName);
            }
        }
        else
        {
            args.Add(fullPath);
            args.Add(branchName);
        }

        return await RunLongAsync([.. args], cancellationToken).ConfigureAwait(false);
    }

    public Task<GitCommandResult> RemoveWorktreeAsync(string repositoryRoot, string path, bool force = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(new GitCommandResult(false, "Worktree path is empty."));
        }
        var fullPath = Path.GetFullPath(path);
        string[] args = force
            ? ["-C", repositoryRoot, "worktree", "remove", "--force", fullPath]
            : ["-C", repositoryRoot, "worktree", "remove", fullPath];
        return RunLongAsync(args, cancellationToken);
    }

    public async Task<GitCommandResult> PruneWorktreesAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["-C", repositoryRoot, "worktree", "prune"], null, cancellationToken).ConfigureAwait(false);
        return Failure(result) ?? GitCommandResult.Ok;
    }

    public async Task<string?> GetHeadHashAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(repositoryRoot)) return null;
        var result = await RunAsync(["-C", repositoryRoot, "rev-parse", "--verify", "-q", "HEAD"], null, cancellationToken).ConfigureAwait(false);
        if (result is not { Succeeded: true }) return null;
        var hash = result.StandardOutput.Trim();
        return hash.Length >= 7 ? hash : null;
    }

    public async Task<GitWorkspaceCleanupProbe> ProbeWorkspaceCleanupAsync(
        string repositoryRoot,
        string worktreePath,
        string? baseRef = null,
        CancellationToken cancellationToken = default)
    {
        var unavailable = new GitWorkspaceCleanupProbe(
            DefaultBranch: null,
            DefaultRef: null,
            HeadHash: null,
            CommitsAheadOfDefault: 0,
            IsAncestorOfDefault: false,
            IsMergedByCommitOrPatch: false,
            IsMergedIntoRemoteDefault: false,
            HasRemotes: false,
            UnpushedCommitCount: 0,
            LastCommitDate: null,
            HasUncommittedChanges: false,
            UncommittedChangeCount: 0,
            StatusUnavailable: true);

        if (!Directory.Exists(repositoryRoot) || !Directory.Exists(worktreePath))
        {
            return unavailable;
        }

        var status = await GetStatusAsync(worktreePath, cancellationToken).ConfigureAwait(false);
        if (status is null)
        {
            return unavailable;
        }

        var uncommittedCount = status.Changes.Select(c => c.Path).Distinct().Count();
        var hasUncommitted = uncommittedCount > 0;
        var headHash = await GetHeadHashAsync(worktreePath, cancellationToken).ConfigureAwait(false);

        DateTimeOffset? lastCommitDate = null;
        var commitDateResult = await RunAsync(["-C", worktreePath, "log", "-1", "--format=%cI"], null, cancellationToken).ConfigureAwait(false);
        if (commitDateResult is { Succeeded: true }
            && DateTimeOffset.TryParse(commitDateResult.StandardOutput.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            lastCommitDate = parsedDate;
        }

        var (defaultBranch, defaultRefs) = await ResolveDefaultRefsAsync(repositoryRoot, baseRef, cancellationToken).ConfigureAwait(false);
        var primaryDefaultRef = defaultRefs.FirstOrDefault().Ref;

        var commitsAhead = 0;
        var isAncestor = false;
        var isMergedByCommitOrPatch = false;
        var isMergedIntoRemoteDefault = false;

        if (headHash is not null && defaultRefs.Count > 0)
        {
            int? minAhead = null;
            foreach (var (candidateRef, isRemote) in defaultRefs)
            {
                var ahead = await CountRevListAsync(worktreePath, [$"{candidateRef}..HEAD"], cancellationToken).ConfigureAwait(false);
                if (ahead.HasValue)
                {
                    minAhead = minAhead.HasValue ? Math.Min(minAhead.Value, ahead.Value) : ahead.Value;
                }

                var ancestorResult = await RunAsync(["-C", worktreePath, "merge-base", "--is-ancestor", headHash, candidateRef], null, cancellationToken).ConfigureAwait(false);
                if (ancestorResult is { ExitCode: 0 })
                {
                    isAncestor = true;
                    // Distinguish an actual merge commit from a branch sitting untouched on the default branch's first-parent history.
                    var onFirstParent = await IsOnFirstParentAsync(worktreePath, headHash, candidateRef, cancellationToken).ConfigureAwait(false);
                    if (!onFirstParent)
                    {
                        isMergedByCommitOrPatch = true;
                        if (isRemote) isMergedIntoRemoteDefault = true;
                    }
                    continue;
                }

                // Branch has commits not reachable by SHA from candidateRef: check rebase-merge (git cherry) and squash-merge.
                if (ahead is > 0 && await IsMergedByPatchOrSquashAsync(worktreePath, headHash, candidateRef, ahead.Value, cancellationToken).ConfigureAwait(false))
                {
                    isMergedByCommitOrPatch = true;
                    if (isRemote) isMergedIntoRemoteDefault = true;
                }
            }
            commitsAhead = minAhead ?? 0;
        }

        // Remotes & unpushed commit detection.
        var remotesResult = await RunAsync(["-C", worktreePath, "remote"], null, cancellationToken).ConfigureAwait(false);
        var hasRemotes = remotesResult is { Succeeded: true }
            && remotesResult.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 0;
        var unpushedCount = 0;
        if (hasRemotes && headHash is not null)
        {
            var notOnAnyRemote = await CountRevListAsync(worktreePath, ["HEAD", "--not", "--remotes"], cancellationToken).ConfigureAwait(false) ?? 0;
            unpushedCount = Math.Max(status.Ahead, notOnAnyRemote);
        }

        return new GitWorkspaceCleanupProbe(
            DefaultBranch: defaultBranch,
            DefaultRef: primaryDefaultRef,
            HeadHash: headHash,
            CommitsAheadOfDefault: commitsAhead,
            IsAncestorOfDefault: isAncestor,
            IsMergedByCommitOrPatch: isMergedByCommitOrPatch,
            IsMergedIntoRemoteDefault: isMergedIntoRemoteDefault,
            HasRemotes: hasRemotes,
            UnpushedCommitCount: unpushedCount,
            LastCommitDate: lastCommitDate,
            HasUncommittedChanges: hasUncommitted,
            UncommittedChangeCount: uncommittedCount,
            StatusUnavailable: false);
    }

    private async Task<(string? Branch, List<(string Ref, bool IsRemote)> Refs)> ResolveDefaultRefsAsync(
        string repositoryRoot,
        string? baseRef,
        CancellationToken cancellationToken)
    {
        var refs = new List<(string Ref, bool IsRemote)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? branch = null;

        async Task TryAddRefAsync(string candidate, bool isRemote)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate)) return;
            var verify = await RunAsync(["-C", repositoryRoot, "rev-parse", "--verify", "-q", candidate], null, cancellationToken).ConfigureAwait(false);
            if (verify is { Succeeded: true })
            {
                refs.Add((candidate, isRemote));
            }
        }

        // 1. Remote default branch pointer (origin/HEAD -> origin/main).
        var originHead = await RunAsync(["-C", repositoryRoot, "symbolic-ref", "-q", "--short", "refs/remotes/origin/HEAD"], null, cancellationToken).ConfigureAwait(false);
        if (originHead is { Succeeded: true })
        {
            var shortRemote = originHead.StandardOutput.Trim();
            if (shortRemote.Length > 0)
            {
                var slash = shortRemote.IndexOf('/');
                branch = slash >= 0 ? shortRemote[(slash + 1)..] : shortRemote;
                await TryAddRefAsync("refs/remotes/" + shortRemote, isRemote: true).ConfigureAwait(false);
                await TryAddRefAsync("refs/heads/" + branch, isRemote: false).ConfigureAwait(false);
            }
        }

        // 2. Fall back to common default branch names or the main worktree's current branch.
        if (branch is null)
        {
            foreach (var candidate in new[] { "main", "master", "trunk" })
            {
                var remoteCheck = await RunAsync(["-C", repositoryRoot, "rev-parse", "--verify", "-q", "refs/remotes/origin/" + candidate], null, cancellationToken).ConfigureAwait(false);
                var localCheck = await RunAsync(["-C", repositoryRoot, "rev-parse", "--verify", "-q", "refs/heads/" + candidate], null, cancellationToken).ConfigureAwait(false);
                if (remoteCheck is { Succeeded: true } || localCheck is { Succeeded: true })
                {
                    branch = candidate;
                    if (remoteCheck is { Succeeded: true }) await TryAddRefAsync("refs/remotes/origin/" + candidate, isRemote: true).ConfigureAwait(false);
                    if (localCheck is { Succeeded: true }) await TryAddRefAsync("refs/heads/" + candidate, isRemote: false).ConfigureAwait(false);
                    break;
                }
            }
        }

        if (branch is null)
        {
            var mainStatus = await GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(mainStatus?.Branch))
            {
                branch = mainStatus.Branch;
                await TryAddRefAsync("refs/remotes/origin/" + branch, isRemote: true).ConfigureAwait(false);
                await TryAddRefAsync("refs/heads/" + branch, isRemote: false).ConfigureAwait(false);
            }
        }

        // 3. If no standard default ref resolved, fall back to the workspace's recorded BaseRef.
        if (refs.Count == 0 && !string.IsNullOrWhiteSpace(baseRef))
        {
            var trimmed = baseRef.Trim();
            var isRemote = trimmed.Contains('/');
            if (isRemote)
            {
                await TryAddRefAsync("refs/remotes/" + trimmed, isRemote: true).ConfigureAwait(false);
            }
            await TryAddRefAsync("refs/heads/" + trimmed, isRemote: false).ConfigureAwait(false);
            await TryAddRefAsync(trimmed, isRemote: isRemote).ConfigureAwait(false);
            branch ??= isRemote && trimmed.StartsWith("origin/", StringComparison.Ordinal) ? trimmed["origin/".Length..] : trimmed;
        }

        return (branch, refs);
    }

    private async Task<int?> CountRevListAsync(string workingDirectory, string[] revSpec, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["-C", workingDirectory, "rev-list", "--count", .. revSpec], null, cancellationToken).ConfigureAwait(false);
        if (result is not { Succeeded: true }) return null;
        return int.TryParse(result.StandardOutput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : null;
    }

    /// <summary>
    /// Returns true when <paramref name="headHash"/> lies on <paramref name="targetRef"/>'s own first-parent line
    /// (for example a freshly created branch sitting on main), and false when it was merged in as a side branch.
    /// </summary>
    private async Task<bool> IsOnFirstParentAsync(string workingDirectory, string headHash, string targetRef, CancellationToken cancellationToken)
    {
        var targetHashResult = await RunAsync(["-C", workingDirectory, "rev-parse", "--verify", "-q", targetRef], null, cancellationToken).ConfigureAwait(false);
        if (targetHashResult is not { Succeeded: true }) return true;
        var targetHash = targetHashResult.StandardOutput.Trim();
        if (string.Equals(headHash, targetHash, StringComparison.OrdinalIgnoreCase)) return true;

        var chainResult = await RunAsync(
            ["-C", workingDirectory, "rev-list", "--first-parent", "--parents", $"{headHash}..{targetRef}"],
            null,
            cancellationToken).ConfigureAwait(false);
        if (chainResult is not { Succeeded: true }) return true;

        // Walking first-parents from targetRef stops at the first ancestor of headHash.
        // That stopping commit's hash is the first parent (column 1) of the last emitted line:
        // it equals headHash iff headHash itself lies on targetRef's first-parent line.
        var lines = chainResult.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return true;
        var lastParts = lines[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return lastParts.Length >= 2 && string.Equals(lastParts[1], headHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Detects rebase-merged branches (<c>git cherry</c>) and multi-commit squash-merged branches.</summary>
    private async Task<bool> IsMergedByPatchOrSquashAsync(
        string workingDirectory,
        string headHash,
        string targetRef,
        int commitsAhead,
        CancellationToken cancellationToken)
    {
        var cherry = await RunAsync(["-C", workingDirectory, "cherry", targetRef, headHash], null, cancellationToken).ConfigureAwait(false);
        if (cherry is { Succeeded: true })
        {
            var lines = cherry.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length > 0 && lines.All(l => l.StartsWith('-')))
            {
                return true;
            }
        }

        if (commitsAhead <= 1) return false;

        // Multi-commit squash merge: synthesize a single tree commit on top of merge-base and compare patch-id with targetRef.
        var mergeBaseResult = await RunAsync(["-C", workingDirectory, "merge-base", targetRef, headHash], null, cancellationToken).ConfigureAwait(false);
        var treeResult = await RunAsync(["-C", workingDirectory, "rev-parse", "--verify", "-q", $"{headHash}^{{tree}}"], null, cancellationToken).ConfigureAwait(false);
        if (mergeBaseResult is not { Succeeded: true } || treeResult is not { Succeeded: true }) return false;
        var mergeBase = mergeBaseResult.StandardOutput.Trim();
        var tree = treeResult.StandardOutput.Trim();
        if (mergeBase.Length == 0 || tree.Length == 0) return false;

        var commitTree = await RunAsync(["-C", workingDirectory, "commit-tree", tree, "-p", mergeBase, "-m", "octopus-squash-check"], null, cancellationToken).ConfigureAwait(false);
        if (commitTree is not { Succeeded: true }) return false;
        var synthetic = commitTree.StandardOutput.Trim();
        if (synthetic.Length == 0) return false;

        var squashCherry = await RunAsync(["-C", workingDirectory, "cherry", targetRef, synthetic], null, cancellationToken).ConfigureAwait(false);
        if (squashCherry is not { Succeeded: true }) return false;
        var squashLines = squashCherry.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return squashLines.Length == 1 && squashLines[0].StartsWith('-');
    }

    /// <summary>Parses <c>git worktree list --porcelain</c> output into worktree entries.</summary>
    public static IReadOnlyList<GitWorktree> ParseWorktrees(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        var list = new List<GitWorktree>();
        string? currentPath = null;
        string? head = null;
        string? branch = null;
        var isBare = false;
        var isDetached = false;
        var isLocked = false;
        var isPrunable = false;

        void Flush()
        {
            if (currentPath is null) return;
            list.Add(new GitWorktree(
                Path.GetFullPath(currentPath),
                head,
                branch,
                isBare,
                isDetached,
                isLocked,
                isPrunable,
                list.Count == 0));
            currentPath = null;
            head = null;
            branch = null;
            isBare = false;
            isDetached = false;
            isLocked = false;
            isPrunable = false;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                Flush();
                continue;
            }
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Flush();
                currentPath = line["worktree ".Length..];
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = line["HEAD ".Length..];
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var fullRef = line["branch ".Length..];
                branch = fullRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? fullRef["refs/heads/".Length..]
                    : fullRef;
            }
            else if (line == "bare")
            {
                isBare = true;
            }
            else if (line == "detached")
            {
                isDetached = true;
            }
            else if (line == "locked" || line.StartsWith("locked ", StringComparison.Ordinal))
            {
                isLocked = true;
            }
            else if (line == "prunable" || line.StartsWith("prunable ", StringComparison.Ordinal))
            {
                isPrunable = true;
            }
        }
        Flush();
        return list;
    }
}
