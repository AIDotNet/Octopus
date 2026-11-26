using System.Text.Json;
using System.Text.Json.Serialization;
using Octopus.Git;
using Octopus.Remote.Protocol;

namespace Octopus.App.Remote;

/// <summary>The arguments of any call of <see cref="IGitService"/>, as they travel to a host: each method uses the ones it has.</summary>
public sealed class GitCallArguments
{
    public string Root { get; set; } = "";
    public string? Path { get; set; }
    public List<string>? Paths { get; set; }
    public string? OriginalPath { get; set; }
    public string? Message { get; set; }
    public string? Name { get; set; }
    public string? Commit { get; set; }
    public string? BaseRef { get; set; }
    public int Count { get; set; }
    public int Skip { get; set; }
    public bool Flag { get; set; }
    public bool Flag2 { get; set; }
}

/// <summary>The result of any call of <see cref="IGitService"/>: the one member the method fills.</summary>
public sealed class GitCallReply
{
    public string? Text { get; set; }
    public bool? Flag { get; set; }
    public int? Number { get; set; }
    public GitStatus? Status { get; set; }
    public GitFileDiff? Diff { get; set; }
    public GitDirectories? Directories { get; set; }
    public GitCommandResult? Result { get; set; }
    public List<GitCommit>? Commits { get; set; }
    public List<GitGraphCommit>? GraphCommits { get; set; }
    public List<GitBranch>? Branches { get; set; }
    public List<GitCommitFile>? Files { get; set; }
    public List<GitWorktree>? Worktrees { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GitCallArguments))]
[JsonSerializable(typeof(GitCallReply))]
internal sealed partial class RemoteGitJsonContext : JsonSerializerContext;

/// <summary>
/// The git service of a host, for a project that lives there: every call is carried out by the host's own git, on the
/// host's files (design/12-远程操作.md §6.2). Paths in arguments and results are the host's.
/// </summary>
public sealed class RemoteGitService(RemoteClientRuntime runtime, string hostId) : IGitService
{
    private async Task<GitCallReply> CallAsync(string method, GitCallArguments arguments, CancellationToken cancellationToken)
    {
        var client = await runtime.ConnectAsync(hostId).ConfigureAwait(false);
        var json = await client.GitAsync(method, JsonSerializer.Serialize(arguments, RemoteGitJsonContext.Default.GitCallArguments), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, RemoteGitJsonContext.Default.GitCallReply) ?? new GitCallReply();
    }

    // A host that cannot be reached is, for the panel, a git command that failed with that reason.
    private async Task<GitCommandResult> RunAsync(string method, GitCallArguments arguments, CancellationToken cancellationToken)
    {
        try { return (await CallAsync(method, arguments, cancellationToken).ConfigureAwait(false)).Result ?? new GitCommandResult(false, "The host gave no answer."); }
        catch (Exception e) when (e is IOException or RemoteException) { return new GitCommandResult(false, e.Message); }
    }

    // Reading: nothing to show while the host is away.
    private async Task<GitCallReply?> ReadAsync(string method, GitCallArguments arguments, CancellationToken cancellationToken)
    {
        try { return await CallAsync(method, arguments, cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or RemoteException) { return null; }
    }

    public async Task<string?> FindRepositoryRootAsync(string directory, CancellationToken cancellationToken = default) =>
        (await ReadAsync(nameof(FindRepositoryRootAsync), new GitCallArguments { Root = directory }, cancellationToken).ConfigureAwait(false))?.Text;

    public async Task<GitStatus?> GetStatusAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        (await ReadAsync(nameof(GetStatusAsync), new GitCallArguments { Root = repositoryRoot }, cancellationToken).ConfigureAwait(false))?.Status;

    public async Task<IReadOnlyList<GitCommit>> GetLogAsync(string repositoryRoot, int count = 50, CancellationToken cancellationToken = default) =>
        (await ReadAsync(nameof(GetLogAsync), new GitCallArguments { Root = repositoryRoot, Count = count }, cancellationToken).ConfigureAwait(false))?.Commits ?? [];

    public async Task<IReadOnlyList<GitCommit>> GetLogPageAsync(string repositoryRoot, int count, int skip, CancellationToken cancellationToken = default) =>
        (await ReadAsync(nameof(GetLogPageAsync), new GitCallArguments { Root = repositoryRoot, Count = count, Skip = skip }, cancellationToken).ConfigureAwait(false))?.Commits ?? [];

    public async Task<IReadOnlyList<GitGraphCommit>> GetBranchGraphAsync(string repositoryRoot, int count = 20, CancellationToken cancellationToken = default) =>
        (await ReadAsync(nameof(GetBranchGraphAsync), new GitCallArguments { Root = repositoryRoot, Count = count }, cancellationToken).ConfigureAwait(false))?.GraphCommits ?? [];

    public async Task<GitFileDiff?> GetFileDiffAsync(string repositoryRoot, string path, bool staged, string? originalPath = null, CancellationToken cancellationToken = default) =>
        (await ReadAsync(nameof(GetFileDiffAsync), new GitCallArguments { Root = repositoryRoot, Path = path, Flag = staged, OriginalPath = originalPath }, cancellationToken).ConfigureAwait(false))?.Diff;

    public async Task<GitDirectories?> GetGitDirectoriesAsync(string directory, CancellationToken cancellationToken = default) =>
        (await ReadAsync(nameof(GetGitDirectoriesAsync), new GitCallArguments { Root = directory }, cancellationToken).ConfigureAwait(false))?.Directories;

    public Task<GitCommandResult> StageAsync(string repositoryRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(StageAsync), new GitCallArguments { Root = repositoryRoot, Paths = [.. paths] }, cancellationToken);

    public Task<GitCommandResult> UnstageAsync(string repositoryRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(UnstageAsync), new GitCallArguments { Root = repositoryRoot, Paths = [.. paths] }, cancellationToken);

    public Task<GitCommandResult> CommitAsync(string repositoryRoot, string message, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(CommitAsync), new GitCallArguments { Root = repositoryRoot, Message = message }, cancellationToken);

    public Task<GitCommandResult> AmendAsync(string repositoryRoot, string? message, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(AmendAsync), new GitCallArguments { Root = repositoryRoot, Message = message }, cancellationToken);

    public async Task<string?> GetLastCommitMessageAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        (await ReadAsync(nameof(GetLastCommitMessageAsync), new GitCallArguments { Root = repositoryRoot }, cancellationToken).ConfigureAwait(false))?.Text;

    public Task<GitCommandResult> DiscardAsync(string repositoryRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(DiscardAsync), new GitCallArguments { Root = repositoryRoot, Paths = [.. paths] }, cancellationToken);

    public Task<GitCommandResult> DeleteUntrackedAsync(string repositoryRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(DeleteUntrackedAsync), new GitCallArguments { Root = repositoryRoot, Paths = [.. paths] }, cancellationToken);

    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        (await ReadAsync(nameof(GetBranchesAsync), new GitCallArguments { Root = repositoryRoot }, cancellationToken).ConfigureAwait(false))?.Branches ?? [];

    public Task<GitCommandResult> SwitchBranchAsync(string repositoryRoot, string name, bool isRemote, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(SwitchBranchAsync), new GitCallArguments { Root = repositoryRoot, Name = name, Flag = isRemote }, cancellationToken);

    public Task<GitCommandResult> CreateBranchAsync(string repositoryRoot, string name, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(CreateBranchAsync), new GitCallArguments { Root = repositoryRoot, Name = name }, cancellationToken);

    public Task<GitCommandResult> DeleteBranchAsync(string repositoryRoot, string name, bool force, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(DeleteBranchAsync), new GitCallArguments { Root = repositoryRoot, Name = name, Flag = force }, cancellationToken);

    public async Task<bool> IsValidBranchNameAsync(string repositoryRoot, string name, CancellationToken cancellationToken = default) =>
        (await ReadAsync(nameof(IsValidBranchNameAsync), new GitCallArguments { Root = repositoryRoot, Name = name }, cancellationToken).ConfigureAwait(false))?.Flag ?? false;

    public Task<GitCommandResult> FetchAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(FetchAsync), new GitCallArguments { Root = repositoryRoot }, cancellationToken);

    public Task<GitCommandResult> PullAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(PullAsync), new GitCallArguments { Root = repositoryRoot }, cancellationToken);

    public Task<GitCommandResult> PushAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(PushAsync), new GitCallArguments { Root = repositoryRoot }, cancellationToken);

    public Task<GitCommandResult> StashAsync(string repositoryRoot, string? message, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(StashAsync), new GitCallArguments { Root = repositoryRoot, Message = message }, cancellationToken);

    public Task<GitCommandResult> StashPopAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(StashPopAsync), new GitCallArguments { Root = repositoryRoot }, cancellationToken);

    public async Task<int> GetStashCountAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        (await ReadAsync(nameof(GetStashCountAsync), new GitCallArguments { Root = repositoryRoot }, cancellationToken).ConfigureAwait(false))?.Number ?? 0;

    public async Task<IReadOnlyList<GitCommitFile>> GetCommitFilesAsync(string repositoryRoot, string commit, CancellationToken cancellationToken = default) =>
        (await ReadAsync(nameof(GetCommitFilesAsync), new GitCallArguments { Root = repositoryRoot, Commit = commit }, cancellationToken).ConfigureAwait(false))?.Files ?? [];

    public async Task<GitFileDiff?> GetCommitFileDiffAsync(string repositoryRoot, string commit, string path, string? originalPath = null, CancellationToken cancellationToken = default) =>
        (await ReadAsync(nameof(GetCommitFileDiffAsync), new GitCallArguments { Root = repositoryRoot, Commit = commit, Path = path, OriginalPath = originalPath }, cancellationToken).ConfigureAwait(false))?.Diff;

    public async Task<IReadOnlyList<GitWorktree>> GetWorktreesAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        (await ReadAsync(nameof(GetWorktreesAsync), new GitCallArguments { Root = repositoryRoot }, cancellationToken).ConfigureAwait(false))?.Worktrees ?? [];

    public Task<GitCommandResult> AddWorktreeAsync(string repositoryRoot, string path, string branch, string? baseRef = null, bool createBranch = true, bool isRemote = false, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(AddWorktreeAsync), new GitCallArguments { Root = repositoryRoot, Path = path, Name = branch, BaseRef = baseRef, Flag = createBranch, Flag2 = isRemote }, cancellationToken);

    public Task<GitCommandResult> RemoveWorktreeAsync(string repositoryRoot, string path, bool force = false, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(RemoveWorktreeAsync), new GitCallArguments { Root = repositoryRoot, Path = path, Flag = force }, cancellationToken);

    public Task<GitCommandResult> PruneWorktreesAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        RunAsync(nameof(PruneWorktreesAsync), new GitCallArguments { Root = repositoryRoot }, cancellationToken);

    public Task<string?> GetHeadHashAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<GitWorkspaceCleanupProbe> ProbeWorkspaceCleanupAsync(
        string repositoryRoot,
        string worktreePath,
        string? baseRef = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GitWorkspaceCleanupProbe(null, null, null, 0, false, false, false, false, 0, null, false, 0, StatusUnavailable: true));
}

/// <summary>The host's end of <see cref="RemoteGitService"/>: one call of this computer's git service, by its method's name.</summary>
internal static class RemoteGitDispatcher
{
    public static string RootOf(string arguments) =>
        (JsonSerializer.Deserialize(arguments, RemoteGitJsonContext.Default.GitCallArguments) ?? throw new RemoteException(RemoteProtocol.InvalidParams, "Arguments are missing.")).Root;

    public static async Task<string> CallAsync(IGitService git, string method, string arguments)
    {
        var a = JsonSerializer.Deserialize(arguments, RemoteGitJsonContext.Default.GitCallArguments) ?? throw new RemoteException(RemoteProtocol.InvalidParams, "Arguments are missing.");
        IReadOnlyList<string> paths = a.Paths ?? [];
        var reply = method switch
        {
            nameof(IGitService.FindRepositoryRootAsync) => new GitCallReply { Text = await git.FindRepositoryRootAsync(a.Root) },
            nameof(IGitService.GetStatusAsync) => new GitCallReply { Status = await git.GetStatusAsync(a.Root) },
            nameof(IGitService.GetLogAsync) => new GitCallReply { Commits = [.. await git.GetLogAsync(a.Root, a.Count is > 0 and <= 500 ? a.Count : 50)] },
            nameof(IGitService.GetLogPageAsync) => new GitCallReply { Commits = [.. await git.GetLogPageAsync(a.Root, a.Count is > 0 and <= 500 ? a.Count : 50, Math.Max(0, a.Skip))] },
            nameof(IGitService.GetBranchGraphAsync) => new GitCallReply { GraphCommits = [.. await git.GetBranchGraphAsync(a.Root, a.Count is > 0 and <= 100 ? a.Count : 20)] },
            nameof(IGitService.GetFileDiffAsync) => new GitCallReply { Diff = await git.GetFileDiffAsync(a.Root, Need(a.Path), a.Flag, a.OriginalPath) },
            nameof(IGitService.GetGitDirectoriesAsync) => new GitCallReply { Directories = await git.GetGitDirectoriesAsync(a.Root) },
            nameof(IGitService.StageAsync) => new GitCallReply { Result = await git.StageAsync(a.Root, paths) },
            nameof(IGitService.UnstageAsync) => new GitCallReply { Result = await git.UnstageAsync(a.Root, paths) },
            nameof(IGitService.CommitAsync) => new GitCallReply { Result = await git.CommitAsync(a.Root, Need(a.Message)) },
            nameof(IGitService.AmendAsync) => new GitCallReply { Result = await git.AmendAsync(a.Root, a.Message) },
            nameof(IGitService.GetLastCommitMessageAsync) => new GitCallReply { Text = await git.GetLastCommitMessageAsync(a.Root) },
            nameof(IGitService.DiscardAsync) => new GitCallReply { Result = await git.DiscardAsync(a.Root, paths) },
            nameof(IGitService.DeleteUntrackedAsync) => new GitCallReply { Result = await git.DeleteUntrackedAsync(a.Root, paths) },
            nameof(IGitService.GetBranchesAsync) => new GitCallReply { Branches = [.. await git.GetBranchesAsync(a.Root)] },
            nameof(IGitService.SwitchBranchAsync) => new GitCallReply { Result = await git.SwitchBranchAsync(a.Root, Need(a.Name), a.Flag) },
            nameof(IGitService.CreateBranchAsync) => new GitCallReply { Result = await git.CreateBranchAsync(a.Root, Need(a.Name)) },
            nameof(IGitService.DeleteBranchAsync) => new GitCallReply { Result = await git.DeleteBranchAsync(a.Root, Need(a.Name), a.Flag) },
            nameof(IGitService.IsValidBranchNameAsync) => new GitCallReply { Flag = await git.IsValidBranchNameAsync(a.Root, Need(a.Name)) },
            nameof(IGitService.FetchAsync) => new GitCallReply { Result = await git.FetchAsync(a.Root) },
            nameof(IGitService.PullAsync) => new GitCallReply { Result = await git.PullAsync(a.Root) },
            nameof(IGitService.PushAsync) => new GitCallReply { Result = await git.PushAsync(a.Root) },
            nameof(IGitService.StashAsync) => new GitCallReply { Result = await git.StashAsync(a.Root, a.Message) },
            nameof(IGitService.StashPopAsync) => new GitCallReply { Result = await git.StashPopAsync(a.Root) },
            nameof(IGitService.GetStashCountAsync) => new GitCallReply { Number = await git.GetStashCountAsync(a.Root) },
            nameof(IGitService.GetCommitFilesAsync) => new GitCallReply { Files = [.. await git.GetCommitFilesAsync(a.Root, Need(a.Commit))] },
            nameof(IGitService.GetCommitFileDiffAsync) => new GitCallReply { Diff = await git.GetCommitFileDiffAsync(a.Root, Need(a.Commit), Need(a.Path), a.OriginalPath) },
            nameof(IGitService.GetWorktreesAsync) => new GitCallReply { Worktrees = [.. await git.GetWorktreesAsync(a.Root)] },
            nameof(IGitService.AddWorktreeAsync) => new GitCallReply { Result = await git.AddWorktreeAsync(a.Root, Need(a.Path), Need(a.Name), a.BaseRef, a.Flag, a.Flag2) },
            nameof(IGitService.RemoveWorktreeAsync) => new GitCallReply { Result = await git.RemoveWorktreeAsync(a.Root, Need(a.Path), a.Flag) },
            nameof(IGitService.PruneWorktreesAsync) => new GitCallReply { Result = await git.PruneWorktreesAsync(a.Root) },
            _ => throw new RemoteException(RemoteProtocol.MethodNotFound, $"Unknown git call '{method}'."),
        };
        return JsonSerializer.Serialize(reply, RemoteGitJsonContext.Default.GitCallReply);
    }

    private static string Need(string? value) => value ?? throw new RemoteException(RemoteProtocol.InvalidParams, "An argument is missing.");
}
