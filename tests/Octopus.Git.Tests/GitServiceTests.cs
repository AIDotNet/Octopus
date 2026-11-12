using Octopus.Platform;

namespace Octopus.Git.Tests;

public class GitServiceTests : IDisposable
{
    private sealed class FixedEnvironment : ILoginEnvironment
    {
        public Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
            {
                ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "",
            });
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("octopus-git-").FullName;
    private readonly string? _git;
    private readonly GitService _service;

    public GitServiceTests()
    {
        var locator = new Octopus.Platform.Unix.UnixExecutableLocator();
        _git = locator.FindAll("git", Environment.GetEnvironmentVariable("PATH")).FirstOrDefault();
        _service = new GitService(new ProcessRunner(), locator, new FixedEnvironment());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private void Git(params string[] args)
    {
        Assert.NotNull(_git);
        var result = new ProcessRunner().RunAsync(_git, ["-C", _dir, "-c", "commit.gpgsign=false", .. args]).GetAwaiter().GetResult();
        Assert.True(result.Succeeded, result.StandardError);
    }

    private void Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void InitWithCommit()
    {
        Git("init", "-q");
        Git("config", "user.name", "Tester");
        Git("config", "user.email", "t@example.com");
        Write("a.txt", "one\n");
        Write("b.txt", "bee\n");
        Git("add", ".");
        Git("commit", "-q", "-m", "first commit");
    }

    [Fact]
    public async Task Directory_outside_a_repository_has_no_root()
    {
        if (_git is null) return;
        Assert.Null(await _service.FindRepositoryRootAsync(_dir, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Root_status_log_and_diff_of_a_repository()
    {
        if (_git is null) return;
        InitWithCommit();
        Write("a.txt", "one\ntwo\n");                 // unstaged
        Write("b.txt", "bee\nstaged\n");
        Git("add", "b.txt");                          // staged
        Write("dir/new file.txt", "fresh\n");         // untracked
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));

        var root = await _service.FindRepositoryRootAsync(Path.Combine(_dir, "sub"), TestContext.Current.CancellationToken);
        Assert.NotNull(root);
        Assert.Equal(Path.GetFileName(_dir), Path.GetFileName(root));

        var status = await _service.GetStatusAsync(root, TestContext.Current.CancellationToken);
        Assert.NotNull(status);
        Assert.NotNull(status.Branch);
        Assert.NotNull(status.Head);
        Assert.Contains(status.Changes, c => c.Path == "a.txt" && !c.IsStaged && c.Kind == GitChangeKind.Modified);
        Assert.Contains(status.Changes, c => c.Path == "b.txt" && c.IsStaged && c.Kind == GitChangeKind.Modified);
        Assert.Contains(status.Changes, c => c.Path == "dir/new file.txt" && c.Kind == GitChangeKind.Untracked);

        var log = await _service.GetLogAsync(root, 10, TestContext.Current.CancellationToken);
        var commit = Assert.Single(log);
        Assert.Equal("first commit", commit.Subject);
        Assert.Equal("Tester", commit.Author);
        Assert.Equal(40, commit.Hash.Length);

        var unstaged = await _service.GetFileDiffAsync(root, "a.txt", false, null, TestContext.Current.CancellationToken);
        Assert.Contains(unstaged!.Lines, l => l.Kind == GitDiffLineKind.Added && l.Text == "two");
        var staged = await _service.GetFileDiffAsync(root, "b.txt", true, null, TestContext.Current.CancellationToken);
        Assert.Contains(staged!.Lines, l => l.Kind == GitDiffLineKind.Added && l.Text == "staged");
        var untracked = await _service.GetFileDiffAsync(root, "dir/new file.txt", false, null, TestContext.Current.CancellationToken);
        Assert.Contains(untracked!.Lines, l => l.Kind == GitDiffLineKind.Added && l.Text == "fresh");
    }

    [Fact]
    public async Task A_diff_has_hunks_and_numbered_lines_and_staged_apart_from_unstaged()
    {
        if (_git is null) return;
        InitWithCommit();
        Write("a.txt", "one\ntwo\nthree\n");
        Git("add", "a.txt");
        Write("a.txt", "one\nTWO\nthree\nfour\n");

        var staged = await _service.GetFileDiffAsync(_dir, "a.txt", true, null, TestContext.Current.CancellationToken);
        var unstaged = await _service.GetFileDiffAsync(_dir, "a.txt", false, null, TestContext.Current.CancellationToken);

        Assert.Equal(GitFileDiffKind.Text, staged!.Kind);
        Assert.Equal(2, staged.Added);
        Assert.Equal(GitDiffLineKind.Hunk, staged.Lines[0].Kind);
        Assert.StartsWith("@@ -1 +1,3 @@", staged.Lines[0].Text);
        Assert.Equal(new GitDiffLine(GitDiffLineKind.Context, 1, 1, "one"), staged.Lines[1]);
        Assert.Equal(new GitDiffLine(GitDiffLineKind.Added, null, 2, "two"), staged.Lines[2]);
        Assert.Equal(new GitDiffLine(GitDiffLineKind.Removed, 2, null, "two"), unstaged!.Lines.First(l => l.Kind == GitDiffLineKind.Removed));
        Assert.Equal(1, unstaged.Removed);
        Assert.Equal(2, unstaged.Added);
    }

    [Fact]
    public async Task A_binary_file_and_an_unchanged_file_have_no_lines()
    {
        if (_git is null) return;
        InitWithCommit();
        File.WriteAllBytes(Path.Combine(_dir, "pic.bin"), [1, 2, 0, 3, 0, 0]);
        Git("add", "pic.bin");
        Git("commit", "-q", "-m", "bin");
        File.WriteAllBytes(Path.Combine(_dir, "pic.bin"), [9, 9, 0, 3]);
        File.WriteAllBytes(Path.Combine(_dir, "new.bin"), [0, 0, 0]);

        var changed = await _service.GetFileDiffAsync(_dir, "pic.bin", false, null, TestContext.Current.CancellationToken);
        var untracked = await _service.GetFileDiffAsync(_dir, "new.bin", false, null, TestContext.Current.CancellationToken);
        var same = await _service.GetFileDiffAsync(_dir, "a.txt", false, null, TestContext.Current.CancellationToken);

        Assert.Equal(GitFileDiffKind.Binary, changed!.Kind);
        Assert.Equal(GitFileDiffKind.Binary, untracked!.Kind);
        Assert.Equal(GitFileDiffKind.Empty, same!.Kind);
    }

    [Fact]
    public async Task A_big_untracked_file_is_a_placeholder_and_a_long_diff_is_cut()
    {
        if (_git is null) return;
        InitWithCommit();
        Write("big.txt", new string('x', (int)GitService.MaxFileBytes + 10));
        Write("many.txt", string.Concat(Enumerable.Range(0, 9000).Select(i => $"line {i}\n")));

        var big = await _service.GetFileDiffAsync(_dir, "big.txt", false, null, TestContext.Current.CancellationToken);
        var many = await _service.GetFileDiffAsync(_dir, "many.txt", false, null, TestContext.Current.CancellationToken);

        Assert.Equal(GitFileDiffKind.TooLarge, big!.Kind);
        Assert.Equal(GitFileDiffKind.Text, many!.Kind);
        Assert.True(many.IsTruncated);
        Assert.Equal(GitDiffParser.MaxLines, many.Lines.Count);
        Assert.Equal(9000, many.Added);
    }

    [Fact]
    public async Task A_deleted_file_diffs_as_all_removed()
    {
        if (_git is null) return;
        InitWithCommit();
        File.Delete(Path.Combine(_dir, "a.txt"));

        var diff = await _service.GetFileDiffAsync(_dir, "a.txt", false, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, diff!.Removed);
        Assert.Equal(0, diff.Added);
    }

    [Fact]
    public async Task The_git_directory_of_a_sub_folder_is_the_one_above()
    {
        if (_git is null) return;
        InitWithCommit();
        Directory.CreateDirectory(Path.Combine(_dir, "sub", "deep"));

        var dirs = await _service.GetGitDirectoriesAsync(Path.Combine(_dir, "sub", "deep"), TestContext.Current.CancellationToken);

        Assert.NotNull(dirs);
        Assert.Equal(".git", Path.GetFileName(dirs.GitDirectory));
        Assert.True(Directory.Exists(dirs.GitDirectory));
        Assert.Equal(dirs.GitDirectory, dirs.CommonDirectory);
        Assert.Null(await _service.GetGitDirectoriesAsync(Directory.CreateTempSubdirectory("octopus-nogit-").FullName, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_linked_worktree_has_its_own_git_directory_and_a_shared_one()
    {
        if (_git is null) return;
        InitWithCommit();
        var worktree = Path.Combine(Directory.CreateTempSubdirectory("octopus-wt-").FullName, "wt");
        Git("worktree", "add", "-q", "-b", "other", worktree);

        var dirs = await _service.GetGitDirectoriesAsync(worktree, TestContext.Current.CancellationToken);

        Assert.NotNull(dirs);
        Assert.NotEqual(dirs.GitDirectory, dirs.CommonDirectory);
        Assert.True(File.Exists(Path.Combine(worktree, ".git")));     // a file, not a directory
        Assert.True(File.Exists(Path.Combine(dirs.GitDirectory, "HEAD")));
        Assert.True(Directory.Exists(Path.Combine(dirs.CommonDirectory, "refs")));
    }

    [Fact]
    public async Task Missing_git_gives_nothing()
    {
        var service = new GitService(new ProcessRunner(), new Octopus.Platform.Unix.UnixExecutableLocator(), new EmptyPathEnvironment());
        Assert.Null(await service.FindRepositoryRootAsync(_dir, TestContext.Current.CancellationToken));
        Assert.Null(await service.GetStatusAsync(_dir, TestContext.Current.CancellationToken));
        Assert.Empty(await service.GetLogAsync(_dir, 5, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_slow_listing_of_untracked_files_falls_back_to_untracked_folders_and_stays_there()
    {
        var runner = new ScriptedRunner();
        var service = new GitService(runner, new FakeGit(), new FixedEnvironment());

        var first = await service.GetStatusAsync("/repo", TestContext.Current.CancellationToken);
        var second = await service.GetStatusAsync("/repo", TestContext.Current.CancellationToken);

        Assert.Equal(["all", "normal", "normal"], runner.UntrackedModes);
        var untracked = Assert.Single(first!.Changes);
        Assert.True(untracked.IsDirectory);
        Assert.Equal("bulk", untracked.Path);
        Assert.Equal(GitChangeKind.Untracked, untracked.Kind);
        Assert.Single(second!.Changes);
    }

    private sealed class FakeGit : IExecutableLocator
    {
        public IReadOnlyList<string> FindAll(string name, string? pathVariable) => ["/usr/bin/git"];
    }

    /// <summary>Answers <c>--untracked-files=all</c> with a timeout and <c>normal</c> with one untracked folder.</summary>
    private sealed class ScriptedRunner : IProcessRunner
    {
        public List<string> UntrackedModes { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var mode = arguments.First(a => a.StartsWith("--untracked-files=", StringComparison.Ordinal))["--untracked-files=".Length..];
            UntrackedModes.Add(mode);
            return Task.FromResult(mode == "all"
                ? new ProcessResult(-1, "", "", true)
                : new ProcessResult(0, "# branch.head main\0? bulk/\0", "", false));
        }
    }

    private sealed class EmptyPathEnvironment : ILoginEnvironment
    {
        public Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["PATH"] = "/nonexistent" });
    }
    [Fact]
    public async Task Staging_unstaging_and_committing_change_the_repository()
    {
        if (_git is null) return;
        var ct = TestContext.Current.CancellationToken;
        InitWithCommit();
        Git("config", "commit.gpgsign", "false");
        Write("a.txt", "one\ntwo\n");
        Write("new dir/c.txt", "see\n");
        File.Delete(Path.Combine(_dir, "b.txt"));

        Assert.True((await _service.StageAsync(_dir, ["a.txt", "new dir/c.txt", "b.txt"], ct)).Succeeded);
        var status = await _service.GetStatusAsync(_dir, ct);
        Assert.All(status!.Changes, c => Assert.True(c.IsStaged));
        Assert.Equal(3, status.Changes.Count);

        Assert.True((await _service.UnstageAsync(_dir, ["a.txt"], ct)).Succeeded);
        status = await _service.GetStatusAsync(_dir, ct);
        Assert.False(status!.Changes.Single(c => c.Path == "a.txt").IsStaged);

        var commit = await _service.CommitAsync(_dir, "second commit\n\nwith a body", ct);
        Assert.True(commit.Succeeded, commit.Error);
        var log = await _service.GetLogAsync(_dir, 5, ct);
        Assert.Equal("second commit", log[0].Subject);
        status = await _service.GetStatusAsync(_dir, ct);
        // Only what was staged went in: a.txt is still changed.
        Assert.Equal(["a.txt"], status!.Changes.Select(c => c.Path).ToArray());

        // Nothing staged: git says so.
        var nothing = await _service.CommitAsync(_dir, "third", ct);
        Assert.False(nothing.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(nothing.Error));
        Assert.False((await _service.CommitAsync(_dir, "  ", ct)).Succeeded);
    }

    [Fact]
    public async Task The_first_commit_of_a_new_repository_can_be_staged_and_unstaged()
    {
        if (_git is null) return;
        var ct = TestContext.Current.CancellationToken;
        Git("init", "-q");
        Write(":odd.txt", "x\n");
        Assert.True((await _service.StageAsync(_dir, [":odd.txt"], ct)).Succeeded);
        Assert.True((await _service.GetStatusAsync(_dir, ct))!.Changes.Single().IsStaged);
        Assert.True((await _service.UnstageAsync(_dir, [":odd.txt"], ct)).Succeeded);
        Assert.Equal(GitChangeKind.Untracked, (await _service.GetStatusAsync(_dir, ct))!.Changes.Single().Kind);
    }
}
