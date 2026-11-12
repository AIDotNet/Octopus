using System.Collections.Concurrent;
using System.Globalization;
using Octopus.Platform;

namespace Octopus.Git;

/// <summary>
/// Reads a repository by running the user's own <c>git</c>. Every command reads, except staging, unstaging and
/// committing (see <see cref="IGitService"/>).
/// </summary>
public sealed partial class GitService(IProcessRunner runner, IExecutableLocator locator, ILoginEnvironment loginEnvironment) : IGitService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Listing every untracked file may be slow in a huge repository; past this the folders are listed instead.</summary>
    private static readonly TimeSpan AllUntrackedTimeout = TimeSpan.FromSeconds(2);

    private static readonly IReadOnlyDictionary<string, string> GitEnvironment = new Dictionary<string, string>
    {
        ["GIT_OPTIONAL_LOCKS"] = "0",
        ["LC_ALL"] = "C",
    };

    private readonly ConcurrentDictionary<string, bool> _slowRepositories = new(StringComparer.Ordinal);
    private string? _git;
    private bool _looked;

    public async Task<string?> FindRepositoryRootAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) return null;
        var result = await RunAsync(["-C", directory, "rev-parse", "--show-toplevel"], null, cancellationToken).ConfigureAwait(false);
        if (result is not { Succeeded: true }) return null;
        var root = result.StandardOutput.Trim();
        return root.Length == 0 ? null : Path.GetFullPath(root);
    }

    public async Task<GitStatus?> GetStatusAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        string[] Command(string untracked) =>
            ["-C", repositoryRoot, "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=" + untracked];

        ProcessResult? result = null;
        if (!_slowRepositories.ContainsKey(repositoryRoot))
        {
            result = await RunAsync(Command("all"), null, cancellationToken, AllUntrackedTimeout).ConfigureAwait(false);
            if (result is { TimedOut: true }) _slowRepositories[repositoryRoot] = true;
        }
        if (result is null or { TimedOut: true })
        {
            result = await RunAsync(Command("normal"), null, cancellationToken).ConfigureAwait(false);
        }
        return result is { Succeeded: true } ? PorcelainV2Parser.Parse(result.StandardOutput, repositoryRoot) : null;
    }

    public async Task<IReadOnlyList<GitCommit>> GetLogAsync(string repositoryRoot, int count = 50, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            ["-C", repositoryRoot, "log", "-n", count.ToString(CultureInfo.InvariantCulture),
             "--pretty=format:%H%x1f%h%x1f%s%x1f%an%x1f%aI%x1e"], null, cancellationToken).ConfigureAwait(false);
        if (result is not { Succeeded: true }) return [];
        var commits = new List<GitCommit>();
        foreach (var record in result.StandardOutput.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Trim('\n', '\r').Split('\x1f');
            if (fields.Length < 5) continue;
            if (!DateTimeOffset.TryParse(fields[4], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            commits.Add(new GitCommit(fields[0], fields[1], fields[2], fields[3], date));
        }
        return commits;
    }

    /// <summary>A file bigger than this is not read to be shown as "all added".</summary>
    public const long MaxFileBytes = 1024 * 1024;

    /// <summary>A diff with more changed lines than this is not even asked for.</summary>
    public const int MaxChangedLines = 100_000;

    private static readonly string[] DiffOptions = ["--no-color", "--no-ext-diff", "--no-textconv", "-U3"];

    public async Task<GitFileDiff?> GetFileDiffAsync(string repositoryRoot, string path, bool staged, string? originalPath = null, CancellationToken cancellationToken = default)
    {
        string[] paths = staged && originalPath is not null ? [originalPath, path] : [path];
        string[] scope = staged ? ["--cached", "-M", "--"] : ["--"];

        // Counting first tells binary files and huge diffs apart without reading them.
        var numstat = await RunAsync(["-C", repositoryRoot, "diff", .. scope.Take(scope.Length - 1), "--numstat", "--", .. paths], null, cancellationToken).ConfigureAwait(false);
        if (numstat is not { Succeeded: true }) return null;
        var counts = numstat.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (counts.Length == 0)
        {
            return staged ? GitFileDiff.Of(GitFileDiffKind.Empty) : await UntrackedDiffAsync(repositoryRoot, path, cancellationToken).ConfigureAwait(false);
        }
        long changed = 0;
        foreach (var line in counts)
        {
            var fields = line.Split('\t');
            if (fields.Length < 3) continue;
            if (fields[0] == "-" || fields[1] == "-") return GitFileDiff.Of(GitFileDiffKind.Binary);
            if (long.TryParse(fields[0], out var a) && long.TryParse(fields[1], out var r)) changed += a + r;
        }
        if (changed > MaxChangedLines) return GitFileDiff.Of(GitFileDiffKind.TooLarge);

        var diff = await RunAsync(["-C", repositoryRoot, "diff", .. DiffOptions, .. scope, .. paths], null, cancellationToken).ConfigureAwait(false);
        return diff is { Succeeded: true } ? GitDiffParser.Parse(diff.StandardOutput) : null;
    }

    /// <summary>A file git does not track yet shows as all added, read here (git has no read-only way to diff it against nothing).</summary>
    private async Task<GitFileDiff?> UntrackedDiffAsync(string repositoryRoot, string path, CancellationToken cancellationToken)
    {
        var untracked = await RunAsync(
            ["-C", repositoryRoot, "ls-files", "--others", "--exclude-standard", "--", path], null, cancellationToken).ConfigureAwait(false);
        if (untracked is not { Succeeded: true }) return null;
        if (untracked.StandardOutput.Length == 0) return GitFileDiff.Of(GitFileDiffKind.Empty);
        try
        {
            var full = Path.Combine(repositoryRoot, path);
            var info = new FileInfo(full);
            if (!info.Exists) return GitFileDiff.Of(GitFileDiffKind.Empty);
            if (info.Length > MaxFileBytes) return GitFileDiff.Of(GitFileDiffKind.TooLarge);
            var bytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0) return GitFileDiff.Of(GitFileDiffKind.Binary);
            return GitDiffParser.AllAdded(System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<GitDirectories?> GetGitDirectoriesAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) return null;
        // --path-format=absolute (git 2.31) makes both answers absolute; older git prints the common one relative to the directory.
        var result = await RunAsync(["-C", directory, "rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir"], null, cancellationToken).ConfigureAwait(false);
        if (result is not { Succeeded: true })
        {
            result = await RunAsync(["-C", directory, "rev-parse", "--absolute-git-dir", "--git-common-dir"], null, cancellationToken).ConfigureAwait(false);
        }
        if (result is not { Succeeded: true }) return null;
        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 1) return null;
        var gitDir = Path.GetFullPath(lines[0]);
        // --git-common-dir may be relative to the directory the command ran in.
        var common = lines.Length > 1 ? Path.GetFullPath(lines[1], Path.GetFullPath(directory)) : gitDir;
        return new GitDirectories(gitDir, common);
    }

    /// <summary>Hooks (a formatter, a test run) may take long; past this the commit is given up.</summary>
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Paths are given to git in batches, so a long list never exceeds the command line limit.</summary>
    private const int PathBatch = 200;

    public Task<GitCommandResult> StageAsync(string repositoryRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        ChangeIndexAsync(repositoryRoot, ["add", "-A"], paths, cancellationToken);

    public Task<GitCommandResult> UnstageAsync(string repositoryRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        // "reset" also works before the first commit, where "restore --staged" has no HEAD to restore from.
        ChangeIndexAsync(repositoryRoot, ["reset", "-q"], paths, cancellationToken);

    private async Task<GitCommandResult> ChangeIndexAsync(string repositoryRoot, string[] command, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (paths.Count == 0) return GitCommandResult.Ok;
        // A path that starts with ":" would be a pathspec magic; "--" alone does not stop that.
        foreach (var batch in paths.Distinct(StringComparer.Ordinal).Select(p => p.StartsWith(':') ? "./" + p : p).Chunk(PathBatch))
        {
            var result = await RunAsync(["-C", repositoryRoot, .. command, "--", .. batch], null, cancellationToken).ConfigureAwait(false);
            if (Failure(result) is { } failure) return failure;
        }
        return GitCommandResult.Ok;
    }

    public async Task<GitCommandResult> CommitAsync(string repositoryRoot, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return new GitCommandResult(false, "Empty commit message.");
        return await RunLongAsync(["-C", repositoryRoot, "commit", "-q", "-m", message], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The environment of a command that runs the repository's hooks or reaches a remote: the login shell's PATH (hooks are
    /// programs such as husky; ssh and credential helpers live there too) and the SSH agent; never a terminal prompt or
    /// an editor, which would wait for ever with no one to answer.
    /// </summary>
    private async Task<Dictionary<string, string>> LongEnvironmentAsync(CancellationToken cancellationToken)
    {
        var login = await loginEnvironment.GetAsync(cancellationToken).ConfigureAwait(false);
        var environment = new Dictionary<string, string>(GitEnvironment)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_EDITOR"] = "true",
            ["GIT_MERGE_AUTOEDIT"] = "no",
        };
        foreach (var name in new[] { "PATH", "SSH_AUTH_SOCK", "HOME" })
        {
            if (login.TryGetValue(name, out var value) && value.Length > 0) environment[name] = value;
        }
        // ssh would ask on its terminal for an unknown host or a passphrase: there is none, so it must fail instead.
        if (!login.ContainsKey("GIT_SSH_COMMAND") && Environment.GetEnvironmentVariable("GIT_SSH_COMMAND") is null)
        {
            environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
        }
        return environment;
    }

    private async Task<GitCommandResult> RunLongAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var environment = await LongEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var result = await RunAsync(arguments, null, cancellationToken, CommitTimeout, environment).ConfigureAwait(false);
        return Failure(result) ?? GitCommandResult.Ok;
    }

    private static GitCommandResult? Failure(ProcessResult? result)
    {
        if (result is null) return new GitCommandResult(false, "git was not found.");
        if (result.TimedOut) return new GitCommandResult(false, "git did not finish in time.");
        if (result.ExitCode == 0) return null;
        var text = (result.StandardError.Trim().Length > 0 ? result.StandardError : result.StandardOutput).Trim();
        // The last lines say why; a hook may have printed pages before them.
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new GitCommandResult(false, lines.Length == 0 ? $"git exited with {result.ExitCode}." : string.Join('\n', lines.TakeLast(8)));
    }

    private async Task<ProcessResult?> RunAsync(string[] arguments, string? workingDirectory, CancellationToken cancellationToken, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        var git = await FindGitAsync(cancellationToken).ConfigureAwait(false);
        if (git is null) return null;
        string[] full = ["-c", "core.quotepath=false", .. arguments];
        return await runner.RunAsync(git, full, environment ?? GitEnvironment, workingDirectory, timeout ?? Timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> FindGitAsync(CancellationToken cancellationToken)
    {
        if (_looked) return _git;
        var environment = await loginEnvironment.GetAsync(cancellationToken).ConfigureAwait(false);
        environment.TryGetValue("PATH", out var path);
        _git = locator.FindAll("git", path).FirstOrDefault();
        _looked = true;
        return _git;
    }
}
