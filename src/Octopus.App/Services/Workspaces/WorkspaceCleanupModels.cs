using Octopus.Core.Projects;

namespace Octopus.App.Services.Workspaces;

/// <summary>Cleanup rules that a branch workspace currently satisfies.</summary>
[Flags]
public enum WorkspaceCleanupReason
{
    None = 0,
    Merged = 1 << 0,
    Unchanged = 1 << 1,
    Inactive = 1 << 2,
}

/// <summary>Protections that prevent a workspace from being removed automatically (or warn on manual removal).</summary>
[Flags]
public enum WorkspaceProtectionReason
{
    None = 0,
    Pinned = 1 << 0,
    RunningSession = 1 << 1,
    UnsavedEditorChanges = 1 << 2,
    UncommittedChanges = 1 << 3,
    UnpushedCommits = 1 << 4,
    GitStatusUnavailable = 1 << 5,
    /// <summary>Workspace was recently created/active; blocks automatic removal under the Unchanged rule only.</summary>
    GracePeriod = 1 << 6,
}

/// <summary>Evaluation snapshot of one branch workspace for manual inspection or automatic cleanup.</summary>
public sealed record WorkspaceCleanupCandidate(
    Project Project,
    ProjectWorkspace Workspace,
    string ShortPath,
    WorkspaceCleanupReason MatchedReasons,
    WorkspaceCleanupReason ActiveRuleMatches,
    WorkspaceProtectionReason Protections,
    bool IsMerged,
    bool IsUnchanged,
    bool IsInactive,
    int InactiveDays,
    DateTimeOffset LastActivityAt,
    int UncommittedChangeCount,
    int UnpushedCommitCount,
    int OpenItemCount,
    int RunningItemCount,
    PullRequestMergeInfo? MergedPullRequest,
    string? DefaultBranch)
{
    /// <summary>Hard protections that always block automatic cleanup.</summary>
    public WorkspaceProtectionReason HardProtections => Protections & ~WorkspaceProtectionReason.GracePeriod;

    public bool IsProtected => HardProtections != WorkspaceProtectionReason.None;

    /// <summary>True when this workspace qualifies for automatic cleanup under currently enabled rules.</summary>
    public bool CanAutoClean =>
        ActiveRuleMatches != WorkspaceCleanupReason.None
        && !IsProtected
        && (ActiveRuleMatches != WorkspaceCleanupReason.Unchanged || !Protections.HasFlag(WorkspaceProtectionReason.GracePeriod));

    /// <summary>True when this workspace matches at least one cleanup rule and has no hard protections.</summary>
    public bool IsRecommendedForManualCleanup => MatchedReasons != WorkspaceCleanupReason.None && !IsProtected;
}

/// <summary>One workspace chosen by the user in the manual inspection dialog.</summary>
public sealed record WorkspaceCleanupSelection(
    Project Project,
    ProjectWorkspace Workspace,
    bool AllowForceRemoval);

/// <summary>Outcome of attempting to remove one workspace during a cleanup pass.</summary>
public sealed record WorkspaceCleanupRemovalResult(
    Project Project,
    ProjectWorkspace Workspace,
    bool Removed,
    bool BranchDeleted,
    bool SkippedDueToProtection,
    WorkspaceProtectionReason Protections = WorkspaceProtectionReason.None,
    string? Error = null);

/// <summary>Summary of a manual or automatic workspace cleanup pass.</summary>
public sealed record WorkspaceCleanupRunSummary(
    DateTimeOffset CompletedAt,
    bool IsAutomatic,
    int InspectedCount,
    IReadOnlyList<WorkspaceCleanupRemovalResult> Results)
{
    public int RemovedCount => Results.Count(r => r.Removed);
    public int BranchesDeletedCount => Results.Count(r => r.Removed && r.BranchDeleted);
    public int SkippedCount => Results.Count(r => r.SkippedDueToProtection);
    public int FailedCount => Results.Count(r => !r.Removed && !r.SkippedDueToProtection);
}

/// <summary>Runtime configuration for the automatic workspace cleanup scheduler (tests disable <see cref="AutoStart"/>).</summary>
public sealed record WorkspaceCleanupOptions
{
    public bool AutoStart { get; init; } = true;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(45);
    public TimeProvider? TimeProvider { get; init; }
}
