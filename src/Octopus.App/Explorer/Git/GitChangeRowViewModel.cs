using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.Git;

namespace Octopus.App.Explorer.Git;

/// <summary>One changed file in the source control list.</summary>
public sealed partial class GitChangeRowViewModel(GitChange change) : ObservableObject
{
    public GitChange Change { get; } = change;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>The file name; an untracked folder listed whole ends with a slash.</summary>
    public string Name { get; } = Path.GetFileName(change.Path) + (change.IsDirectory ? "/" : "");

    /// <summary>The folder of the file, relative to the repository (empty at the top).</summary>
    public string Directory { get; } = change.Path.Contains('/') ? change.Path[..change.Path.LastIndexOf('/')] : "";

    public string FullPath => Change.FullPath;
    public GitChangeKind Kind => Change.Kind;
    public string Letter => GitKindInfo.Letter(Change.Kind);
    public bool HasDirectory => Directory.Length > 0;
}
