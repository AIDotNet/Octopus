using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Localization;
using Octopus.Git;
using TKCode.Arc.Icons.Lucide;

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
    public bool IsStaged => Change.IsStaged;

    /// <summary>The same file in the same group across refreshes.</summary>
    public string Key => (Change.IsStaged ? "s:" : "w:") + Change.Path;

    public LucideIconKind ActionIcon => IsStaged ? LucideIconKind.Minus : LucideIconKind.Plus;
    public string ActionText => AppLangResources.Get(IsStaged ? AppLangResourceKind.ExplorerUnstage : AppLangResourceKind.ExplorerStage);

    public bool IsUntracked => Change.Kind == GitChangeKind.Untracked;

    /// <summary>A change not staged can be thrown away: a tracked file goes back to the index, an untracked one is deleted.</summary>
    public bool CanDiscard => !IsStaged;
    public LucideIconKind DiscardIcon => IsUntracked ? LucideIconKind.Trash2 : LucideIconKind.Undo2;
    public string DiscardText => AppLangResources.Get(IsUntracked ? AppLangResourceKind.ExplorerDeleteUntracked : AppLangResourceKind.ExplorerDiscard);
}
