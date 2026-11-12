using Octopus.App.Localization;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Explorer.Git;

public enum GitChangeGroupKind { Staged, Changed, Untracked }

/// <summary>
/// A titled group of changes: staged, changed or untracked. Its buttons stage the whole group (or unstage it, for the
/// staged one) and throw its changes away (discard them, or delete the untracked files).
/// </summary>
public sealed class GitChangeGroupViewModel(string title, IReadOnlyList<GitChangeRowViewModel> rows, GitChangeGroupKind kind = GitChangeGroupKind.Changed)
{
    public string Title { get; } = title;
    public IReadOnlyList<GitChangeRowViewModel> Rows { get; } = rows;
    public int Count => Rows.Count;
    public GitChangeGroupKind Kind { get; } = kind;
    public bool IsStaged => Kind == GitChangeGroupKind.Staged;
    public LucideIconKind ActionIcon => IsStaged ? LucideIconKind.Minus : LucideIconKind.Plus;
    public string ActionText => AppLangResources.Get(IsStaged ? AppLangResourceKind.ExplorerUnstageAll : AppLangResourceKind.ExplorerStageAll);
    public bool CanDiscard => !IsStaged;
    public LucideIconKind DiscardIcon => Kind == GitChangeGroupKind.Untracked ? LucideIconKind.Trash2 : LucideIconKind.Undo2;
    public string DiscardText => AppLangResources.Get(Kind == GitChangeGroupKind.Untracked ? AppLangResourceKind.ExplorerDeleteAllUntracked : AppLangResourceKind.ExplorerDiscardAll);
}
