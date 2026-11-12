using System.Globalization;

namespace Octopus.Git;

/// <summary>Branches, remotes, stashes, discarding and the history of single commits.</summary>
public sealed partial class GitService
{
    public async Task<GitCommandResult> AmendAsync(string repositoryRoot, string? message, CancellationToken cancellationToken = default)
    {
        string[] keep = string.IsNullOrWhiteSpace(message) ? ["--no-edit"] : ["-m", message];
        return await RunLongAsync(["-C", repositoryRoot, "commit", "-q", "--amend", .. keep], cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetLastCommitMessageAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["-C", repositoryRoot, "log", "-1", "--format=%B"], null, cancellationToken).ConfigureAwait(false);
        return result is { Succeeded: true } ? result.StandardOutput.TrimEnd() : null;
    }

    public Task<GitCommandResult> DiscardAsync(string repositoryRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        // "checkout --" (not "restore", git 2.23) puts the index's version back into the working tree.
        ChangeIndexAsync(repositoryRoot, ["checkout", "-q"], paths, cancellationToken);

    public Task<GitCommandResult> DeleteUntrackedAsync(string repositoryRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        ChangeIndexAsync(repositoryRoot, ["clean", "-f", "-d", "-q"], paths, cancellationToken);

    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            ["-C", repositoryRoot, "for-each-ref", "--sort=refname",
             "--format=%(refname)%1f%(HEAD)%1f%(objectname:short)%1f%(upstream:short)%1f%(committerdate:iso-strict)%1f%(symref)",
             "refs/heads", "refs/remotes"], null, cancellationToken).ConfigureAwait(false);
        if (result is not { Succeeded: true }) return [];
        var branches = new List<GitBranch>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\x1f');
            if (f.Length < 6 || f[5].Length > 0) continue;   // origin/HEAD points at another branch
            var remote = f[0].StartsWith("refs/remotes/", StringComparison.Ordinal);
            var name = remote ? f[0]["refs/remotes/".Length..] : f[0].StartsWith("refs/heads/", StringComparison.Ordinal) ? f[0]["refs/heads/".Length..] : f[0];
            DateTimeOffset? date = DateTimeOffset.TryParse(f[4], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            branches.Add(new GitBranch(name, remote, f[1] == "*", f[2], f[3].Length > 0 ? f[3] : null, date));
        }
        return branches.OrderBy(b => b.IsRemote).ThenBy(b => b.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<GitCommandResult> SwitchBranchAsync(string repositoryRoot, string name, bool isRemote, CancellationToken cancellationToken = default)
    {
        if (!isRemote) return await RunLongAsync(["-C", repositoryRoot, "checkout", "-q", name, "--"], cancellationToken).ConfigureAwait(false);
        var local = name[(name.IndexOf('/') + 1)..];
        var exists = await RunAsync(["-C", repositoryRoot, "rev-parse", "--verify", "-q", "refs/heads/" + local], null, cancellationToken).ConfigureAwait(false);
        return exists is { Succeeded: true }
            ? await RunLongAsync(["-C", repositoryRoot, "checkout", "-q", local, "--"], cancellationToken).ConfigureAwait(false)
            : await RunLongAsync(["-C", repositoryRoot, "checkout", "-q", "--track", "-b", local, name, "--"], cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitCommandResult> CreateBranchAsync(string repositoryRoot, string name, CancellationToken cancellationToken = default)
    {
        if (!await IsValidBranchNameAsync(repositoryRoot, name, cancellationToken).ConfigureAwait(false))
        {
            return new GitCommandResult(false, $"'{name}' is not a valid branch name.");
        }
        return await RunLongAsync(["-C", repositoryRoot, "checkout", "-q", "-b", name], cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitCommandResult> DeleteBranchAsync(string repositoryRoot, string name, bool force, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["-C", repositoryRoot, "branch", force ? "-D" : "-d", "--", name], null, cancellationToken).ConfigureAwait(false);
        return Failure(result) ?? GitCommandResult.Ok;
    }

    public async Task<bool> IsValidBranchNameAsync(string repositoryRoot, string name, CancellationToken cancellationToken = default)
    {
        // "-" would be read as an option, "@{-1}" as the previous branch: neither is a name to create.
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-') || name.Contains("@{", StringComparison.Ordinal)) return false;
        var result = await RunAsync(["-C", repositoryRoot, "check-ref-format", "--branch", name], null, cancellationToken).ConfigureAwait(false);
        return result is { Succeeded: true };
    }

    public Task<GitCommandResult> FetchAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        RunLongAsync(["-C", repositoryRoot, "fetch", "--all", "--prune", "-q"], cancellationToken);

    public Task<GitCommandResult> PullAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        RunLongAsync(["-C", repositoryRoot, "pull", "-q", "--no-edit"], cancellationToken);

    public async Task<GitCommandResult> PushAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var upstream = await RunAsync(["-C", repositoryRoot, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], null, cancellationToken).ConfigureAwait(false);
        if (upstream is { Succeeded: true }) return await RunLongAsync(["-C", repositoryRoot, "push", "-q"], cancellationToken).ConfigureAwait(false);

        var remotes = await RunAsync(["-C", repositoryRoot, "remote"], null, cancellationToken).ConfigureAwait(false);
        var names = remotes is { Succeeded: true } ? remotes.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        if (names.Length == 0) return new GitCommandResult(false, "This repository has no remote to push to.");
        var remote = names.Contains("origin") ? "origin" : names[0];
        return await RunLongAsync(["-C", repositoryRoot, "push", "-q", "-u", remote, "HEAD"], cancellationToken).ConfigureAwait(false);
    }

    public Task<GitCommandResult> StashAsync(string repositoryRoot, string? message, CancellationToken cancellationToken = default) =>
        RunLongAsync(string.IsNullOrWhiteSpace(message)
            ? ["-C", repositoryRoot, "stash", "push", "-q", "--include-untracked"]
            : ["-C", repositoryRoot, "stash", "push", "-q", "--include-untracked", "-m", message], cancellationToken);

    public Task<GitCommandResult> StashPopAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        RunLongAsync(["-C", repositoryRoot, "stash", "pop", "-q"], cancellationToken);

    public async Task<int> GetStashCountAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["-C", repositoryRoot, "stash", "list", "--format=%gd"], null, cancellationToken).ConfigureAwait(false);
        return result is { Succeeded: true } ? result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : 0;
    }

    public async Task<IReadOnlyList<GitCommitFile>> GetCommitFilesAsync(string repositoryRoot, string commit, CancellationToken cancellationToken = default)
    {
        if (!IsHash(commit)) return [];
        var result = await RunAsync(["-C", repositoryRoot, "show", "--format=", "--name-status", "-M", "-z", "--first-parent", commit, "--"], null, cancellationToken).ConfigureAwait(false);
        if (result is not { Succeeded: true }) return [];
        var fields = result.StandardOutput.Split('\0');
        var files = new List<GitCommitFile>();
        for (var i = 0; i < fields.Length; i++)
        {
            var status = fields[i].Trim('\n');
            if (status.Length == 0) continue;
            var kind = status[0] switch
            {
                'A' => GitChangeKind.Added,
                'D' => GitChangeKind.Deleted,
                'R' or 'C' => GitChangeKind.Renamed,
                'U' => GitChangeKind.Conflicted,
                _ => GitChangeKind.Modified,
            };
            if (status[0] is 'R' or 'C')
            {
                if (i + 2 >= fields.Length) break;
                files.Add(new GitCommitFile(fields[i + 2], kind, fields[i + 1]));
                i += 2;
            }
            else
            {
                if (i + 1 >= fields.Length) break;
                files.Add(new GitCommitFile(fields[i + 1], kind));
                i += 1;
            }
        }
        return files;
    }

    public async Task<GitFileDiff?> GetCommitFileDiffAsync(string repositoryRoot, string commit, string path, string? originalPath = null, CancellationToken cancellationToken = default)
    {
        if (!IsHash(commit)) return null;
        string[] paths = originalPath is not null ? [originalPath, path] : [path];
        var numstat = await RunAsync(["-C", repositoryRoot, "show", "--format=", "--numstat", "-M", "--first-parent", commit, "--", .. paths], null, cancellationToken).ConfigureAwait(false);
        if (numstat is not { Succeeded: true }) return null;
        var counts = numstat.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (counts.Length == 0) return GitFileDiff.Of(GitFileDiffKind.Empty);
        long changed = 0;
        foreach (var line in counts)
        {
            var f = line.Split('\t');
            if (f.Length < 3) continue;
            if (f[0] == "-" || f[1] == "-") return GitFileDiff.Of(GitFileDiffKind.Binary);
            if (long.TryParse(f[0], out var a) && long.TryParse(f[1], out var r)) changed += a + r;
        }
        if (changed > MaxChangedLines) return GitFileDiff.Of(GitFileDiffKind.TooLarge);
        var diff = await RunAsync(["-C", repositoryRoot, "show", "--format=", .. DiffOptions, "-M", "--first-parent", commit, "--", .. paths], null, cancellationToken).ConfigureAwait(false);
        return diff is { Succeeded: true } ? GitDiffParser.Parse(diff.StandardOutput) : null;
    }

    /// <summary>A commit is named by its hash here: anything else could be read by git as an option or a range.</summary>
    private static bool IsHash(string commit) => commit.Length is >= 4 and <= 64 && commit.All(Uri.IsHexDigit);
}
