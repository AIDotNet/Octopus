using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Common;
using Octopus.Git;

namespace Octopus.App.Explorer.Git;

public sealed partial class GitCommitRowViewModel(GitCommit commit) : ObservableObject
{
    [ObservableProperty] public partial bool IsSelected { get; set; }

    public GitCommit Commit { get; } = commit;
    public string ShortHash => Commit.ShortHash;
    public string Subject => Commit.Subject;

    /// <summary>"Author · 3 h ago".</summary>
    public string Meta => $"{Commit.Author} · {RelativeTime.Format(Commit.Date, DateTimeOffset.Now)}";
}
