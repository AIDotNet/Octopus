using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Octopus.Core.Projects;
using Octopus.Platform;

namespace Octopus.App.Services.Workspaces;

/// <summary>One issue or pull request fetched from GitHub via the local <c>gh</c> CLI.</summary>
public sealed record RemoteTaskItem(
    WorkspaceTaskKind Kind,
    int Number,
    string Key,
    string Title,
    string? Url,
    string? Body,
    string? Author,
    string? HeadBranch,
    IReadOnlyList<string> Labels)
{
    public WorkspaceTaskLink ToTaskLink() => new()
    {
        Kind = Kind,
        Key = Key,
        Title = Title,
        Url = Url,
        Body = TruncateBody(Body),
    };

    /// <summary>Generates a clean git branch suggestion from the PR head branch or issue number + title.</summary>
    public string SuggestedBranch()
    {
        if (Kind == WorkspaceTaskKind.GitHubPr && !string.IsNullOrWhiteSpace(HeadBranch))
        {
            return HeadBranch.Trim();
        }
        var slug = Slugify(Title);
        var prefix = Labels.Any(l => l.Contains("bug", StringComparison.OrdinalIgnoreCase) || l.Contains("fix", StringComparison.OrdinalIgnoreCase))
            ? "fix"
            : "feat";
        return slug.Length > 0 ? $"{prefix}/{Number}-{slug}" : $"{prefix}/issue-{Number}";
    }

    internal static string Slugify(string text)
    {
        var sb = new StringBuilder();
        var lastDash = false;
        foreach (var ch in text.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }
            if (sb.Length >= 36) break;
        }
        return sb.ToString().Trim('-');
    }

    private static string? TruncateBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var trimmed = body.Trim();
        return trimmed.Length <= 800 ? trimmed : trimmed[..800].TrimEnd() + "…";
    }
}

public sealed record TaskProviderResult(bool IsAvailable, string? Message, IReadOnlyList<RemoteTaskItem> Items);

/// <summary>Metadata of a merged GitHub pull request used to verify whether a workspace's branch is merged.</summary>
public sealed record PullRequestMergeInfo(
    int Number,
    string HeadBranch,
    string? HeadRefOid,
    DateTimeOffset? MergedAt,
    string? Url);

/// <summary>Fetches open issues and pull requests for a git repository (first edition: local <c>gh</c> CLI).</summary>
public interface IGitHubTaskProvider
{
    Task<TaskProviderResult> ListIssuesAsync(string repositoryRoot, CancellationToken cancellationToken = default);
    Task<TaskProviderResult> ListPullRequestsAsync(string repositoryRoot, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PullRequestMergeInfo>> ListMergedPullRequestsAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PullRequestMergeInfo>>([]);
}

public sealed class GhCliTaskProvider(IProcessRunner runner, IExecutableLocator locator, ILoginEnvironment loginEnvironment) : IGitHubTaskProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);
    private string? _gh;
    private bool _looked;

    public Task<TaskProviderResult> ListIssuesAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        RunListAsync(
            repositoryRoot,
            ["issue", "list", "--state", "open", "--limit", "30", "--json", "number,title,url,body,author,labels"],
            WorkspaceTaskKind.GitHubIssue,
            cancellationToken);

    public Task<TaskProviderResult> ListPullRequestsAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        RunListAsync(
            repositoryRoot,
            ["pr", "list", "--state", "open", "--limit", "30", "--json", "number,title,url,body,headRefName,author,labels"],
            WorkspaceTaskKind.GitHubPr,
            cancellationToken);

    public async Task<IReadOnlyList<PullRequestMergeInfo>> ListMergedPullRequestsAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var gh = await FindGhAsync(cancellationToken).ConfigureAwait(false);
        if (gh is null) return [];
        var login = await loginEnvironment.GetAsync(cancellationToken).ConfigureAwait(false);
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["GH_PROMPT_DISABLED"] = "1" };
        foreach (var key in new[] { "PATH", "HOME", "GH_TOKEN", "GITHUB_TOKEN", "GH_CONFIG_DIR", "XDG_CONFIG_HOME" })
        {
            if (login.TryGetValue(key, out var val) && val.Length > 0) env[key] = val;
        }
        var result = await runner.RunAsync(
            gh,
            ["pr", "list", "--state", "merged", "--limit", "100", "--json", "number,headRefName,headRefOid,mergedAt,url"],
            env,
            repositoryRoot,
            Timeout,
            cancellationToken).ConfigureAwait(false);
        if (result is not { Succeeded: true }) return [];
        return ParseMergedPullRequests(result.StandardOutput);
    }

    internal static IReadOnlyList<PullRequestMergeInfo> ParseMergedPullRequests(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var raw = JsonSerializer.Deserialize(json, GhCliJsonContext.Default.ListGhRawMergedPr);
            if (raw is null) return [];
            var items = new List<PullRequestMergeInfo>(raw.Count);
            foreach (var entry in raw)
            {
                if (entry.Number <= 0 || string.IsNullOrWhiteSpace(entry.HeadRefName)) continue;
                DateTimeOffset? mergedAt = !string.IsNullOrWhiteSpace(entry.MergedAt)
                    && DateTimeOffset.TryParse(entry.MergedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed
                    : null;
                items.Add(new PullRequestMergeInfo(
                    entry.Number,
                    entry.HeadRefName.Trim(),
                    string.IsNullOrWhiteSpace(entry.HeadRefOid) ? null : entry.HeadRefOid.Trim(),
                    mergedAt,
                    entry.Url));
            }
            return items;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<TaskProviderResult> RunListAsync(string repositoryRoot, string[] args, WorkspaceTaskKind kind, CancellationToken cancellationToken)
    {
        var gh = await FindGhAsync(cancellationToken).ConfigureAwait(false);
        if (gh is null)
        {
            return new TaskProviderResult(false, null, []);
        }
        var login = await loginEnvironment.GetAsync(cancellationToken).ConfigureAwait(false);
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["GH_PROMPT_DISABLED"] = "1" };
        foreach (var key in new[] { "PATH", "HOME", "GH_TOKEN", "GITHUB_TOKEN", "GH_CONFIG_DIR", "XDG_CONFIG_HOME" })
        {
            if (login.TryGetValue(key, out var val) && val.Length > 0) env[key] = val;
        }
        var result = await runner.RunAsync(gh, args, env, repositoryRoot, Timeout, cancellationToken).ConfigureAwait(false);
        if (result is null) return new TaskProviderResult(false, null, []);
        if (result.TimedOut) return new TaskProviderResult(true, "gh timed out.", []);
        if (!result.Succeeded)
        {
            var err = (result.StandardError.Trim().Length > 0 ? result.StandardError : result.StandardOutput).Trim();
            var firstLine = err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return new TaskProviderResult(true, firstLine ?? $"gh exited with {result.ExitCode}.", []);
        }
        return new TaskProviderResult(true, null, ParseItems(result.StandardOutput, kind));
    }

    internal static IReadOnlyList<RemoteTaskItem> ParseItems(string json, WorkspaceTaskKind kind)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var raw = JsonSerializer.Deserialize(json, GhCliJsonContext.Default.ListGhRawItem);
            if (raw is null) return [];
            var items = new List<RemoteTaskItem>(raw.Count);
            foreach (var entry in raw)
            {
                if (entry.Number <= 0 || string.IsNullOrWhiteSpace(entry.Title)) continue;
                var key = kind == WorkspaceTaskKind.GitHubPr
                    ? string.Create(CultureInfo.InvariantCulture, $"PR #{entry.Number}")
                    : string.Create(CultureInfo.InvariantCulture, $"#{entry.Number}");
                var labels = entry.Labels?.Select(l => l.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Cast<string>().ToList() ?? [];
                items.Add(new RemoteTaskItem(
                    kind,
                    entry.Number,
                    key,
                    entry.Title.Trim(),
                    entry.Url,
                    entry.Body,
                    entry.Author?.Login,
                    entry.HeadRefName,
                    labels));
            }
            return items;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<string?> FindGhAsync(CancellationToken cancellationToken)
    {
        if (_looked) return _gh;
        var login = await loginEnvironment.GetAsync(cancellationToken).ConfigureAwait(false);
        login.TryGetValue("PATH", out var path);
        _gh = locator.FindAll("gh", path).FirstOrDefault();
        _looked = true;
        return _gh;
    }
}

internal sealed class GhRawItem
{
    public int Number { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }
    public string? Body { get; set; }
    public string? HeadRefName { get; set; }
    public GhRawAuthor? Author { get; set; }
    public List<GhRawLabel>? Labels { get; set; }
}

internal sealed class GhRawAuthor
{
    public string? Login { get; set; }
}

internal sealed class GhRawLabel
{
    public string? Name { get; set; }
}

internal sealed class GhRawMergedPr
{
    public int Number { get; set; }
    public string? HeadRefName { get; set; }
    public string? HeadRefOid { get; set; }
    public string? MergedAt { get; set; }
    public string? Url { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<GhRawItem>))]
[JsonSerializable(typeof(List<GhRawMergedPr>))]
internal sealed partial class GhCliJsonContext : JsonSerializerContext;
