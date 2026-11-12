using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.Git;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Explorer.Git;

/// <summary>A commit in the list; a click opens it to show the files it changed.</summary>
public sealed partial class GitCommitRowViewModel(GitCommit commit) : ObservableObject
{
    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>Its files show under it.</summary>
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    /// <summary>The files it changed; null until it is first opened.</summary>
    public IReadOnlyList<GitCommitFileRowViewModel>? Files { get; set; }

    public GitCommit Commit { get; } = commit;
    public string ShortHash => Commit.ShortHash;
    public string Subject => Commit.Subject;

    /// <summary>"Author · 3 h ago".</summary>
    public string Meta => $"{Commit.Author} · {RelativeTime.Format(Commit.Date, DateTimeOffset.Now)}";

    public LucideIconKind ChevronIcon => IsExpanded ? LucideIconKind.ChevronDown : LucideIconKind.ChevronRight;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ChevronIcon));
}

/// <summary>A file an opened commit changed; a click shows what the commit did to it.</summary>
public sealed partial class GitCommitFileRowViewModel(GitCommitRowViewModel owner, GitCommitFile file) : ObservableObject
{
    [ObservableProperty] public partial bool IsSelected { get; set; }

    public GitCommitRowViewModel Owner { get; } = owner;
    public GitCommitFile File { get; } = file;
    public string Name { get; } = Path.GetFileName(file.Path);
    public string Directory { get; } = file.Path.Contains('/') ? file.Path[..file.Path.LastIndexOf('/')] : "";
    public bool HasDirectory => Directory.Length > 0;
    public GitChangeKind Kind => File.Kind;
    public string Tip => File.OriginalPath is { } original ? $"{original} → {File.Path}" : File.Path;
}
