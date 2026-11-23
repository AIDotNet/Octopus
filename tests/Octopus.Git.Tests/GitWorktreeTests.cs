using Octopus.Platform;

namespace Octopus.Git.Tests;

/// <summary>Linked git worktrees: porcelain parsing, adding new/existing/remote branches, removing and pruning.</summary>
public class GitWorktreeTests : IDisposable
{
    private sealed class PathEnvironment : ILoginEnvironment
    {
        public Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "" });
    }

    private readonly string _root = Directory.CreateTempSubdirectory("octopus-gitwt-").FullName;
    private readonly string _work;
    private readonly string _wtBase;
    private readonly string? _git;
    private readonly GitService _service;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public GitWorktreeTests()
    {
        _work = Path.Combine(_root, "work");
        _wtBase = Path.Combine(_root, "workspaces");
        Directory.CreateDirectory(_work);
        var locator = new Octopus.Platform.Unix.UnixExecutableLocator();
        _git = locator.FindAll("git", Environment.GetEnvironmentVariable("PATH")).FirstOrDefault();
        _service = new GitService(new ProcessRunner(), locator, new PathEnvironment());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Git(string dir, params string[] args)
    {
        var result = new ProcessRunner().RunAsync(_git!, ["-C", dir, "-c", "commit.gpgsign=false", "-c", "core.hooksPath=/dev/null", .. args]).GetAwaiter().GetResult();
        Assert.True(result.Succeeded, result.StandardError);
        return result.StandardOutput.Trim();
    }

    private void Init()
    {
        Git(_work, "init", "-q", "-b", "main");
        Git(_work, "config", "user.name", "Tester");
        Git(_work, "config", "user.email", "t@example.com");
        Git(_work, "config", "commit.gpgsign", "false");
        Git(_work, "config", "core.hooksPath", "/dev/null");
        File.WriteAllText(Path.Combine(_work, "README.md"), "hello\n");
        Git(_work, "add", ".");
        Git(_work, "commit", "-q", "-m", "initial");
    }

    [Fact]
    public void ParseWorktrees_reads_main_linked_detached_and_prunable_entries()
    {
        var raw = """
            worktree /repo/main
            HEAD 1111111111111111111111111111111111111111
            branch refs/heads/main

            worktree /home/u/.octopus/workspaces/app/feat-ui
            HEAD 2222222222222222222222222222222222222222
            branch refs/heads/feat/ui
            locked

            worktree /home/u/.octopus/workspaces/app/detached-one
            HEAD 3333333333333333333333333333333333333333
            detached
            prunable gitdir file points to non-existent location

            """;

        var parsed = GitService.ParseWorktrees(raw);
        Assert.Equal(3, parsed.Count);
        Assert.True(parsed[0].IsMain);
        Assert.Equal("main", parsed[0].Branch);
        Assert.False(parsed[0].IsLocked);

        Assert.False(parsed[1].IsMain);
        Assert.Equal("feat/ui", parsed[1].Branch);
        Assert.True(parsed[1].IsLocked);

        Assert.True(parsed[2].IsDetached);
        Assert.Null(parsed[2].Branch);
        Assert.True(parsed[2].IsPrunable);
    }

    [Fact]
    public async Task Add_list_and_remove_new_branch_worktree_preserves_slashes_in_branch_name()
    {
        if (_git is null) return;
        Init();

        var wtPath = Path.Combine(_wtBase, "Octopus", "feat-142-worktree-ui");
        var add = await _service.AddWorktreeAsync(_work, wtPath, "feat/142-worktree-ui", baseRef: "main", createBranch: true, cancellationToken: _ct);
        Assert.True(add.Succeeded, add.Error);
        Assert.True(Directory.Exists(wtPath));
        Assert.True(File.Exists(Path.Combine(wtPath, "README.md")));

        var worktrees = await _service.GetWorktreesAsync(_work, _ct);
        Assert.Equal(2, worktrees.Count);
        Assert.Contains(worktrees, w => w.IsMain && w.Branch == "main");
        Assert.Contains(worktrees, w => !w.IsMain && w.Branch == "feat/142-worktree-ui");

        // Dirty worktree refuses non-force remove, succeeds with force.
        File.WriteAllText(Path.Combine(wtPath, "README.md"), "dirty\n");
        var softRemove = await _service.RemoveWorktreeAsync(_work, wtPath, force: false, cancellationToken: _ct);
        Assert.False(softRemove.Succeeded);

        var forceRemove = await _service.RemoveWorktreeAsync(_work, wtPath, force: true, cancellationToken: _ct);
        Assert.True(forceRemove.Succeeded, forceRemove.Error);
        Assert.False(Directory.Exists(wtPath));
    }

    [Fact]
    public async Task Add_existing_local_branch_succeeds_and_occupied_branch_fails()
    {
        if (_git is null) return;
        Init();
        Git(_work, "branch", "existing/topic");

        // Main is already checked out in the main repository: git refuses checking it out again.
        var conflict = await _service.AddWorktreeAsync(_work, Path.Combine(_wtBase, "main-dup"), "main", createBranch: false, cancellationToken: _ct);
        Assert.False(conflict.Succeeded);

        var topicPath = Path.Combine(_wtBase, "existing-topic");
        var ok = await _service.AddWorktreeAsync(_work, topicPath, "existing/topic", createBranch: false, cancellationToken: _ct);
        Assert.True(ok.Succeeded, ok.Error);

        var status = await _service.GetStatusAsync(topicPath, _ct);
        Assert.Equal("existing/topic", status?.Branch);

        // Removing the directory externally and pruning cleans git metadata.
        Directory.Delete(topicPath, true);
        var prune = await _service.PruneWorktreesAsync(_work, _ct);
        Assert.True(prune.Succeeded, prune.Error);
        Assert.Single(await _service.GetWorktreesAsync(_work, _ct));
    }

    [Fact]
    public async Task ProbeWorkspaceCleanup_detects_unchanged_merge_squash_rebase_dirty_and_unpushed()
    {
        if (_git is null) return;
        Init();

        // Add a local bare remote and push main so origin/main + origin/HEAD exist.
        var bare = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(bare);
        Git(bare, "init", "--bare", "-q");
        Git(_work, "remote", "add", "origin", bare);
        Git(_work, "push", "-q", "-u", "origin", "main");
        Git(_work, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");

        // 1. Fresh untouched branch: unchanged, NOT merged.
        var unchangedPath = Path.Combine(_wtBase, "unchanged");
        Assert.True((await _service.AddWorktreeAsync(_work, unchangedPath, "feat/unchanged", "main", createBranch: true, cancellationToken: _ct)).Succeeded);
        var unchangedProbe = await _service.ProbeWorkspaceCleanupAsync(_work, unchangedPath, "main", _ct);
        Assert.False(unchangedProbe.StatusUnavailable);
        Assert.Equal("main", unchangedProbe.DefaultBranch);
        Assert.Equal(0, unchangedProbe.CommitsAheadOfDefault);
        Assert.True(unchangedProbe.IsAncestorOfDefault);
        Assert.False(unchangedProbe.IsMergedByCommitOrPatch);
        Assert.False(unchangedProbe.HasUncommittedChanges);
        Assert.Equal(0, unchangedProbe.UnpushedCommitCount);

        // 2. Branch merged via --no-ff merge commit into main and pushed.
        var mergePath = Path.Combine(_wtBase, "merged-noff");
        Assert.True((await _service.AddWorktreeAsync(_work, mergePath, "feat/merged-noff", "main", createBranch: true, cancellationToken: _ct)).Succeeded);
        File.WriteAllText(Path.Combine(mergePath, "noff.txt"), "noff\n");
        Git(mergePath, "add", ".");
        Git(mergePath, "commit", "-q", "-m", "add noff");
        Git(_work, "merge", "--no-ff", "-q", "-m", "merge feat/merged-noff", "feat/merged-noff");
        Git(_work, "push", "-q", "origin", "main");
        var mergedProbe = await _service.ProbeWorkspaceCleanupAsync(_work, mergePath, "main", _ct);
        Assert.True(mergedProbe.IsAncestorOfDefault);
        Assert.True(mergedProbe.IsMergedByCommitOrPatch);
        Assert.True(mergedProbe.IsMergedIntoRemoteDefault);
        Assert.Equal(0, mergedProbe.UnpushedCommitCount);

        // 3. Multi-commit branch squash-merged into main and pushed.
        var squashPath = Path.Combine(_wtBase, "merged-squash");
        Assert.True((await _service.AddWorktreeAsync(_work, squashPath, "feat/merged-squash", "main", createBranch: true, cancellationToken: _ct)).Succeeded);
        File.WriteAllText(Path.Combine(squashPath, "sq1.txt"), "one\n");
        Git(squashPath, "add", ".");
        Git(squashPath, "commit", "-q", "-m", "sq part 1");
        File.WriteAllText(Path.Combine(squashPath, "sq2.txt"), "two\n");
        Git(squashPath, "add", ".");
        Git(squashPath, "commit", "-q", "-m", "sq part 2");
        Git(_work, "merge", "--squash", "-q", "feat/merged-squash");
        Git(_work, "commit", "-q", "-m", "squash feat/merged-squash");
        Git(_work, "push", "-q", "origin", "main");
        var squashProbe = await _service.ProbeWorkspaceCleanupAsync(_work, squashPath, "main", _ct);
        Assert.Equal(2, squashProbe.CommitsAheadOfDefault);
        Assert.False(squashProbe.IsAncestorOfDefault);
        Assert.True(squashProbe.IsMergedByCommitOrPatch);
        Assert.True(squashProbe.IsMergedIntoRemoteDefault);

        // 4. Branch with unpushed commit + dirty file.
        var unpushedPath = Path.Combine(_wtBase, "unpushed-dirty");
        Assert.True((await _service.AddWorktreeAsync(_work, unpushedPath, "feat/unpushed", "main", createBranch: true, cancellationToken: _ct)).Succeeded);
        File.WriteAllText(Path.Combine(unpushedPath, "wip.txt"), "wip\n");
        Git(unpushedPath, "add", ".");
        Git(unpushedPath, "commit", "-q", "-m", "local wip");
        var unpushedProbe = await _service.ProbeWorkspaceCleanupAsync(_work, unpushedPath, "main", _ct);
        Assert.Equal(1, unpushedProbe.CommitsAheadOfDefault);
        Assert.False(unpushedProbe.IsMergedByCommitOrPatch);
        Assert.True(unpushedProbe.HasRemotes);
        Assert.Equal(1, unpushedProbe.UnpushedCommitCount);
        Assert.False(unpushedProbe.HasUncommittedChanges);

        File.WriteAllText(Path.Combine(unpushedPath, "dirty.txt"), "dirty\n");
        var dirtyProbe = await _service.ProbeWorkspaceCleanupAsync(_work, unpushedPath, "main", _ct);
        Assert.True(dirtyProbe.HasUncommittedChanges);
        Assert.Equal(1, dirtyProbe.UncommittedChangeCount);
    }
}
