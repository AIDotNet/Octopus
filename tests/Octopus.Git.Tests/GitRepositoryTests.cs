using Octopus.Platform;

namespace Octopus.Git.Tests;

/// <summary>Branches, remotes, stashes, discarding and single commits, against real repositories in a temporary folder.</summary>
public class GitRepositoryTests : IDisposable
{
    private sealed class PathEnvironment : ILoginEnvironment
    {
        public Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "" });
    }

    private readonly string _root = Directory.CreateTempSubdirectory("octopus-gitrepo-").FullName;
    private readonly string _work;
    private readonly string _remote;
    private readonly string? _git;
    private readonly GitService _service;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public GitRepositoryTests()
    {
        _work = Path.Combine(_root, "work");
        _remote = Path.Combine(_root, "remote.git");
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

    private void Write(string name, string content)
    {
        var path = Path.Combine(_work, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Read(string name) => File.ReadAllText(Path.Combine(_work, name));

    private void Init()
    {
        Git(_work, "init", "-q", "-b", "main");
        Git(_work, "config", "user.name", "Tester");
        Git(_work, "config", "user.email", "t@example.com");
        Git(_work, "config", "commit.gpgsign", "false");
        Git(_work, "config", "core.hooksPath", "/dev/null");
        Write("a.txt", "one\n");
        Git(_work, "add", ".");
        Git(_work, "commit", "-q", "-m", "first");
    }

    [Fact]
    public async Task Branches_are_listed_created_switched_and_deleted()
    {
        if (_git is null) return;
        Init();
        Assert.True((await _service.CreateBranchAsync(_work, "feature/x", _ct)).Succeeded);
        var branches = await _service.GetBranchesAsync(_work, _ct);
        Assert.Equal(["feature/x", "main"], branches.Select(b => b.Name).ToArray());
        Assert.True(branches.Single(b => b.Name == "feature/x").IsCurrent);

        Assert.False((await _service.CreateBranchAsync(_work, "bad name", _ct)).Succeeded);
        Assert.False((await _service.CreateBranchAsync(_work, "-x", _ct)).Succeeded);
        Assert.False(await _service.IsValidBranchNameAsync(_work, "a..b", _ct));

        Write("b.txt", "b\n");
        Git(_work, "add", ".");
        Git(_work, "commit", "-q", "-m", "on feature");
        Assert.True((await _service.SwitchBranchAsync(_work, "main", false, _ct)).Succeeded);
        Assert.Equal("main", (await _service.GetStatusAsync(_work, _ct))!.Branch);

        // Not merged: refused unless forced.
        var refused = await _service.DeleteBranchAsync(_work, "feature/x", false, _ct);
        Assert.False(refused.Succeeded);
        Assert.Contains("not fully merged", refused.Error);
        Assert.True((await _service.DeleteBranchAsync(_work, "feature/x", true, _ct)).Succeeded);
        Assert.Equal(["main"], (await _service.GetBranchesAsync(_work, _ct)).Select(b => b.Name).ToArray());
    }

    [Fact]
    public async Task Push_fetch_pull_and_remote_branches()
    {
        if (_git is null) return;
        Init();
        Git(_root, "init", "-q", "--bare", "-b", "main", _remote);
        Git(_work, "remote", "add", "origin", _remote);

        // No upstream yet: the first push sets it.
        var push = await _service.PushAsync(_work, _ct);
        Assert.True(push.Succeeded, push.Error);
        var status = await _service.GetStatusAsync(_work, _ct);
        Assert.Equal("origin/main", status!.Upstream);

        // Someone else pushes a commit and a branch.
        var other = Path.Combine(_root, "other");
        Git(_root, "clone", "-q", _remote, other);
        Git(other, "config", "user.name", "Other");
        Git(other, "config", "user.email", "o@example.com");
        File.WriteAllText(Path.Combine(other, "a.txt"), "one\nfrom other\n");
        Git(other, "commit", "-q", "-am", "from other");
        Git(other, "push", "-q");
        Git(other, "push", "-q", "origin", "HEAD:refs/heads/topic");

        Assert.True((await _service.FetchAsync(_work, _ct)).Succeeded);
        status = await _service.GetStatusAsync(_work, _ct);
        Assert.Equal(1, status!.Behind);
        var branches = await _service.GetBranchesAsync(_work, _ct);
        Assert.Contains(branches, b => b is { Name: "origin/topic", IsRemote: true });
        Assert.DoesNotContain(branches, b => b.Name.EndsWith("/HEAD", StringComparison.Ordinal));

        var pull = await _service.PullAsync(_work, _ct);
        Assert.True(pull.Succeeded, pull.Error);
        Assert.Equal("one\nfrom other\n", Read("a.txt"));

        // A remote branch becomes a local one that follows it.
        Assert.True((await _service.SwitchBranchAsync(_work, "origin/topic", true, _ct)).Succeeded);
        status = await _service.GetStatusAsync(_work, _ct);
        Assert.Equal("topic", status!.Branch);
        Assert.Equal("origin/topic", status.Upstream);
        // Asking again finds the local one.
        Assert.True((await _service.SwitchBranchAsync(_work, "main", false, _ct)).Succeeded);
        Assert.True((await _service.SwitchBranchAsync(_work, "origin/topic", true, _ct)).Succeeded);
    }

    [Fact]
    public async Task A_push_without_a_remote_says_so()
    {
        if (_git is null) return;
        Init();
        var push = await _service.PushAsync(_work, _ct);
        Assert.False(push.Succeeded);
        Assert.Contains("no remote", push.Error);
    }

    [Fact]
    public async Task Changes_are_discarded_and_untracked_files_deleted()
    {
        if (_git is null) return;
        Init();
        Write("a.txt", "changed\n");
        Write("new/file.txt", "x");
        Write("keep.txt", "keep");
        Assert.True((await _service.DiscardAsync(_work, ["a.txt"], _ct)).Succeeded);
        Assert.Equal("one\n", Read("a.txt"));
        Assert.True((await _service.DeleteUntrackedAsync(_work, ["new"], _ct)).Succeeded);
        Assert.False(Directory.Exists(Path.Combine(_work, "new")));
        Assert.True(File.Exists(Path.Combine(_work, "keep.txt")));
    }

    [Fact]
    public async Task Amend_stash_and_the_files_of_a_commit()
    {
        if (_git is null) return;
        Init();
        Write("a.txt", "one\ntwo\n");
        Git(_work, "mv", "a.txt", "renamed.txt");
        Write("c.txt", "see\n");
        Git(_work, "add", ".");
        Git(_work, "commit", "-q", "-m", "second");

        Write("d.txt", "dee\n");
        await _service.StageAsync(_work, ["d.txt"], _ct);
        Assert.True((await _service.AmendAsync(_work, null, _ct)).Succeeded);
        Assert.Equal("second", await _service.GetLastCommitMessageAsync(_work, _ct));
        Assert.True((await _service.AmendAsync(_work, "second, amended", _ct)).Succeeded);
        var log = await _service.GetLogAsync(_work, 5, _ct);
        Assert.Equal(2, log.Count);
        Assert.Equal("second, amended", log[0].Subject);

        var files = await _service.GetCommitFilesAsync(_work, log[0].Hash, _ct);
        Assert.Equal(["c.txt", "d.txt", "renamed.txt"], files.Select(f => f.Path).Order().ToArray());
        var renamed = files.Single(f => f.Path == "renamed.txt");
        Assert.Equal(GitChangeKind.Renamed, renamed.Kind);
        Assert.Equal("a.txt", renamed.OriginalPath);
        var diff = await _service.GetCommitFileDiffAsync(_work, log[0].Hash, renamed.Path, renamed.OriginalPath, _ct);
        Assert.Equal(GitFileDiffKind.Text, diff!.Kind);
        Assert.Contains(diff.Lines, l => l is { Kind: GitDiffLineKind.Added, Text: "two" });
        // The first commit: everything added.
        var first = await _service.GetCommitFilesAsync(_work, log[1].Hash, _ct);
        Assert.Equal(GitChangeKind.Added, first.Single().Kind);
        Assert.Empty(await _service.GetCommitFilesAsync(_work, "--all", _ct));

        // Stash takes untracked files too, and pop brings them back.
        Write("c.txt", "changed\n");
        Write("loose.txt", "loose");
        Assert.True((await _service.StashAsync(_work, "wip", _ct)).Succeeded);
        Assert.Equal(1, await _service.GetStashCountAsync(_work, _ct));
        Assert.Empty((await _service.GetStatusAsync(_work, _ct))!.Changes);
        Assert.True((await _service.StashPopAsync(_work, _ct)).Succeeded);
        Assert.Equal(0, await _service.GetStashCountAsync(_work, _ct));
        Assert.Equal("changed\n", Read("c.txt"));
        Assert.True(File.Exists(Path.Combine(_work, "loose.txt")));
    }
}
