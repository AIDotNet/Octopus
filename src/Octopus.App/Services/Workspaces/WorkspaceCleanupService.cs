using Avalonia.Threading;
using Octopus.App.Activity;
using Octopus.App.Localization;
using Octopus.App.Workspace;
using Octopus.Core.Projects;
using Octopus.Core.Settings;
using Octopus.Git;
using Octopus.Storage;
using Octopus.Terminal.Avalonia;
using TKCode.Arc;

namespace Octopus.App.Services.Workspaces;

public interface IWorkspaceCleanupService : IDisposable
{
    event EventHandler? Changed;

    WorkspaceCleanupRunSummary? LastRun { get; }
    bool IsRunning { get; }

    /// <summary>Starts activity tracking and the background cleanup schedule. Safe to call more than once.</summary>
    void Start();

    /// <summary>Scans every local project's branch workspaces and computes cleanup reasons and protections.</summary>
    Task<IReadOnlyList<WorkspaceCleanupCandidate>> InspectAllAsync(bool? fetchRemotes = null, CancellationToken cancellationToken = default);

    /// <summary>Runs one automatic cleanup pass using the currently enabled rules, re-checking protections before each removal.</summary>
    Task<WorkspaceCleanupRunSummary> RunAutoCleanupAsync(bool isAutomatic = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the workspaces selected in the manual inspection dialog, re-checking unsaved work and protections
    /// immediately before removing each workspace.
    /// </summary>
    Task<WorkspaceCleanupRunSummary> CleanSelectedAsync(IReadOnlyList<WorkspaceCleanupSelection> selections, CancellationToken cancellationToken = default);
}

/// <summary>
/// Evaluates and executes automatic and manual git worktree workspace cleanup with strict pre-removal safety checks
/// (pinned workspaces, running agents/terminals, unsaved editor changes, uncommitted changes, unpushed commits).
/// </summary>
public sealed class WorkspaceCleanupService : IWorkspaceCleanupService
{
    private readonly IProjectService _projects;
    private readonly IWorkspaceManager _workspaces;
    private readonly IGitService _git;
    private readonly IGitHubTaskProvider _tasks;
    private readonly ISettingsStore _settings;
    private readonly IItemActivityService _activity;
    private readonly ITabContentRegistry _tabs;
    private readonly TerminalSessionRegistry? _sessions;
    private readonly IWindowAccessor? _windows;
    private readonly WorkspaceCleanupOptions _options;
    private readonly TimeProvider _time;

    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly Lock _scheduleLock = new();
    private readonly CancellationTokenSource _life = new();
    private CancellationTokenSource _wake = new();
    private bool _started;
    private bool _disposed;
    private Task? _loop;
    private WorkspaceCleanupRunSummary? _lastRun;
    private bool _isRunning;
    private (bool Merged, bool Unchanged, bool Inactive, int Days, int IntervalMinutes, int GraceHours) _lastScheduleKey;

    public WorkspaceCleanupService(
        IProjectService projects,
        IWorkspaceManager workspaces,
        IGitService git,
        IGitHubTaskProvider tasks,
        ISettingsStore settings,
        IItemActivityService activity,
        ITabContentRegistry tabs,
        TerminalSessionRegistry? sessions = null,
        IWindowAccessor? windows = null,
        WorkspaceCleanupOptions? options = null)
    {
        _projects = projects;
        _workspaces = workspaces;
        _git = git;
        _tasks = tasks;
        _settings = settings;
        _activity = activity;
        _tabs = tabs;
        _sessions = sessions;
        _windows = windows;
        _options = options ?? new WorkspaceCleanupOptions();
        _time = _options.TimeProvider ?? TimeProvider.System;
    }

    public event EventHandler? Changed;

    public WorkspaceCleanupRunSummary? LastRun => _lastRun;

    public bool IsRunning => _isRunning;

    public void Start()
    {
        lock (_scheduleLock)
        {
            if (_started || _disposed) return;
            _started = true;
            _lastScheduleKey = ScheduleKeyOf(_settings.Current.WorkspaceCleanup);
            _projects.Changed += OnProjectsChanged;
            _activity.Changed += OnItemActivityChanged;
            _settings.Changed += OnSettingsChanged;
            if (_options.AutoStart)
            {
                _loop = Task.Run(RunScheduleLoopAsync);
            }
        }
    }

    public async Task<IReadOnlyList<WorkspaceCleanupCandidate>> InspectAllAsync(bool? fetchRemotes = null, CancellationToken cancellationToken = default)
    {
        var cfg = _settings.Current.WorkspaceCleanup;
        var shouldFetch = fetchRemotes ?? cfg.FetchBeforeCheck;
        var snapshots = await SnapshotLocalWorkspacesAsync().ConfigureAwait(false);
        if (snapshots.Count == 0) return [];

        var now = _time.GetUtcNow();
        var results = new List<WorkspaceCleanupCandidate>();
        var prCache = new Dictionary<string, IReadOnlyList<PullRequestMergeInfo>>(StringComparer.Ordinal);
        var fetchedRepos = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (project, workspaces) in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(project.Path)) continue;
            var repoRoot = await _git.FindRepositoryRootAsync(project.Path, cancellationToken).ConfigureAwait(false);
            if (repoRoot is null) continue;

            if (shouldFetch && fetchedRepos.Add(repoRoot))
            {
                try { await _git.FetchAsync(repoRoot, cancellationToken).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException) { }
            }

            if (!prCache.TryGetValue(repoRoot, out var mergedPrs))
            {
                try { mergedPrs = await _tasks.ListMergedPullRequestsAsync(repoRoot, cancellationToken).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException) { mergedPrs = []; }
                prCache[repoRoot] = mergedPrs;
            }

            foreach (var ws in workspaces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = await EvaluateWorkspaceAsync(project, ws, repoRoot, mergedPrs, now, cfg, cancellationToken).ConfigureAwait(false);
                results.Add(candidate);
            }
        }

        return results;
    }

    public async Task<WorkspaceCleanupRunSummary> RunAutoCleanupAsync(bool isAutomatic = false, CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetRunning(true);
        try
        {
            var cfg = _settings.Current.WorkspaceCleanup;
            if (!cfg.HasAutoRulesEnabled())
            {
                var empty = new WorkspaceCleanupRunSummary(_time.GetUtcNow(), isAutomatic, 0, []);
                SetLastRun(empty);
                return empty;
            }

            var candidates = await InspectAllAsync(cfg.FetchBeforeCheck, cancellationToken).ConfigureAwait(false);
            var removalResults = new List<WorkspaceCleanupRemovalResult>();

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!candidate.CanAutoClean) continue;

                // Always re-check unsaved work and protections immediately before removing anything.
                var repoRoot = await _git.FindRepositoryRootAsync(candidate.Project.Path, cancellationToken).ConfigureAwait(false);
                if (repoRoot is null) continue;
                IReadOnlyList<PullRequestMergeInfo> mergedPrs = candidate.MergedPullRequest is not null ? [candidate.MergedPullRequest] : [];

                var currentWorkspace = await FindTrackedWorkspaceAsync(candidate.Project, candidate.Workspace.Id).ConfigureAwait(false);
                if (currentWorkspace is null) continue;

                var fresh = await EvaluateWorkspaceAsync(candidate.Project, currentWorkspace, repoRoot, mergedPrs, _time.GetUtcNow(), _settings.Current.WorkspaceCleanup, cancellationToken).ConfigureAwait(false);
                if (!fresh.CanAutoClean)
                {
                    removalResults.Add(new WorkspaceCleanupRemovalResult(
                        candidate.Project,
                        currentWorkspace,
                        Removed: false,
                        BranchDeleted: false,
                        SkippedDueToProtection: true,
                        Protections: fresh.Protections));
                    continue;
                }

                var deleteBranch = fresh.IsMerged && _settings.Current.WorkspaceCleanup.DeleteMergedBranch;
                var removed = await _workspaces.RemoveWorkspaceAsync(
                    candidate.Project,
                    currentWorkspace,
                    force: false,
                    deleteBranch: deleteBranch,
                    forceDeleteBranch: deleteBranch,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                removalResults.Add(new WorkspaceCleanupRemovalResult(
                    candidate.Project,
                    currentWorkspace,
                    Removed: removed.Succeeded,
                    BranchDeleted: removed.BranchDeleted,
                    SkippedDueToProtection: false,
                    Error: removed.Error));
            }

            var summary = new WorkspaceCleanupRunSummary(_time.GetUtcNow(), isAutomatic, candidates.Count, removalResults);
            SetLastRun(summary);
            if (isAutomatic && summary.RemovedCount > 0)
            {
                NotifyAutoCleanupToast(summary.RemovedCount);
            }
            return summary;
        }
        finally
        {
            SetRunning(false);
            _runGate.Release();
        }
    }

    public async Task<WorkspaceCleanupRunSummary> CleanSelectedAsync(IReadOnlyList<WorkspaceCleanupSelection> selections, CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetRunning(true);
        try
        {
            var cfg = _settings.Current.WorkspaceCleanup;
            var results = new List<WorkspaceCleanupRemovalResult>(selections.Count);
            var prCache = new Dictionary<string, IReadOnlyList<PullRequestMergeInfo>>(StringComparer.Ordinal);

            foreach (var selection in selections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentWorkspace = await FindTrackedWorkspaceAsync(selection.Project, selection.Workspace.Id).ConfigureAwait(false);
                if (currentWorkspace is null) continue;

                var repoRoot = await _git.FindRepositoryRootAsync(selection.Project.Path, cancellationToken).ConfigureAwait(false);
                if (repoRoot is null) continue;

                if (!prCache.TryGetValue(repoRoot, out var mergedPrs))
                {
                    try { mergedPrs = await _tasks.ListMergedPullRequestsAsync(repoRoot, cancellationToken).ConfigureAwait(false); }
                    catch (Exception e) when (e is not OperationCanceledException) { mergedPrs = []; }
                    prCache[repoRoot] = mergedPrs;
                }

                // Re-check for unsaved work, pinned status, and newly introduced dirty state immediately before removal.
                var fresh = await EvaluateWorkspaceAsync(selection.Project, currentWorkspace, repoRoot, mergedPrs, _time.GetUtcNow(), cfg, cancellationToken).ConfigureAwait(false);

                // Pinned workspaces and in-memory unsaved editor buffers are never silently discarded.
                var alwaysBlocked = WorkspaceProtectionReason.Pinned | WorkspaceProtectionReason.UnsavedEditorChanges;
                if ((fresh.HardProtections & alwaysBlocked) != WorkspaceProtectionReason.None)
                {
                    results.Add(new WorkspaceCleanupRemovalResult(
                        selection.Project,
                        currentWorkspace,
                        Removed: false,
                        BranchDeleted: false,
                        SkippedDueToProtection: true,
                        Protections: fresh.HardProtections));
                    continue;
                }

                // If the user selected a clean workspace and new uncommitted/unpushed/running work appeared before removal, protect it.
                if (!selection.AllowForceRemoval && fresh.IsProtected)
                {
                    results.Add(new WorkspaceCleanupRemovalResult(
                        selection.Project,
                        currentWorkspace,
                        Removed: false,
                        BranchDeleted: false,
                        SkippedDueToProtection: true,
                        Protections: fresh.HardProtections));
                    continue;
                }

                var forceRemove = selection.AllowForceRemoval && fresh.UncommittedChangeCount > 0;
                var deleteBranch = fresh.IsMerged && cfg.DeleteMergedBranch;
                var removed = await _workspaces.RemoveWorkspaceAsync(
                    selection.Project,
                    currentWorkspace,
                    force: forceRemove,
                    deleteBranch: deleteBranch,
                    forceDeleteBranch: deleteBranch,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                results.Add(new WorkspaceCleanupRemovalResult(
                    selection.Project,
                    currentWorkspace,
                    Removed: removed.Succeeded,
                    BranchDeleted: removed.BranchDeleted,
                    SkippedDueToProtection: false,
                    Error: removed.Error));
            }

            var summary = new WorkspaceCleanupRunSummary(_time.GetUtcNow(), IsAutomatic: false, selections.Count, results);
            SetLastRun(summary);
            return summary;
        }
        finally
        {
            SetRunning(false);
            _runGate.Release();
        }
    }

    internal async Task<WorkspaceCleanupCandidate> EvaluateWorkspaceAsync(
        Project project,
        ProjectWorkspace workspace,
        string repoRoot,
        IReadOnlyList<PullRequestMergeInfo> mergedPrs,
        DateTimeOffset now,
        WorkspaceCleanupSettings cfg,
        CancellationToken cancellationToken)
    {
        var protections = WorkspaceProtectionReason.None;
        if (workspace.IsPinned)
        {
            protections |= WorkspaceProtectionReason.Pinned;
        }

        var (openItems, runningItems, hasUnsavedEditor) = await InspectUiStateAsync(project, workspace).ConfigureAwait(false);
        if (runningItems > 0)
        {
            protections |= WorkspaceProtectionReason.RunningSession;
        }
        if (hasUnsavedEditor)
        {
            protections |= WorkspaceProtectionReason.UnsavedEditorChanges;
        }

        var probe = await _git.ProbeWorkspaceCleanupAsync(repoRoot, workspace.Path, workspace.BaseRef, cancellationToken).ConfigureAwait(false);
        if (probe.StatusUnavailable)
        {
            protections |= WorkspaceProtectionReason.GitStatusUnavailable;
        }
        if (probe.HasUncommittedChanges)
        {
            protections |= WorkspaceProtectionReason.UncommittedChanges;
        }

        var isDefaultBranch = !string.IsNullOrWhiteSpace(probe.DefaultBranch)
            && string.Equals(workspace.Branch, probe.DefaultBranch, StringComparison.Ordinal);

        var matchedPr = FindMergedPullRequest(workspace, mergedPrs);
        var prMergedComplete = matchedPr is not null
            && !probe.StatusUnavailable
            && !probe.HasUncommittedChanges
            && (SameCommitHash(probe.HeadHash, matchedPr.HeadRefOid) || probe.IsAncestorOfDefault || probe.IsMergedByCommitOrPatch);

        var fastForwardMerged = probe.IsAncestorOfDefault
            && !string.IsNullOrWhiteSpace(workspace.InitialHead)
            && !string.IsNullOrWhiteSpace(probe.HeadHash)
            && !SameCommitHash(probe.HeadHash, workspace.InitialHead);

        var isMerged = !isDefaultBranch
            && !probe.StatusUnavailable
            && (prMergedComplete || probe.IsMergedByCommitOrPatch || fastForwardMerged);

        // Unpushed commits protect the workspace unless all local commits are already present in a merged PR or remote default branch.
        if (probe.HasRemotes && probe.UnpushedCommitCount > 0 && !prMergedComplete && !probe.IsMergedIntoRemoteDefault)
        {
            protections |= WorkspaceProtectionReason.UnpushedCommits;
        }

        var isUnchanged = !isDefaultBranch
            && !probe.StatusUnavailable
            && !probe.HasUncommittedChanges
            && probe.CommitsAheadOfDefault == 0
            && !isMerged;

        var lastActivity = await ComputeLastActivityAsync(workspace, probe.LastCommitDate, cancellationToken).ConfigureAwait(false);
        var age = now - lastActivity;
        var inactiveDays = age > TimeSpan.Zero ? (int)age.TotalDays : 0;
        var thresholdDays = Math.Clamp(cfg.InactiveDays, WorkspaceCleanupSettings.MinInactiveDays, WorkspaceCleanupSettings.MaxInactiveDays);
        var isInactive = !isDefaultBranch && age >= TimeSpan.FromDays(thresholdDays);

        if (cfg.UnchangedGraceHours > 0 && age < TimeSpan.FromHours(cfg.UnchangedGraceHours))
        {
            protections |= WorkspaceProtectionReason.GracePeriod;
        }

        var matchedReasons = WorkspaceCleanupReason.None;
        if (isMerged) matchedReasons |= WorkspaceCleanupReason.Merged;
        if (isUnchanged) matchedReasons |= WorkspaceCleanupReason.Unchanged;
        if (isInactive) matchedReasons |= WorkspaceCleanupReason.Inactive;

        var activeRuleMatches = WorkspaceCleanupReason.None;
        if (cfg.DeleteMerged && isMerged) activeRuleMatches |= WorkspaceCleanupReason.Merged;
        if (cfg.DeleteUnchanged && isUnchanged) activeRuleMatches |= WorkspaceCleanupReason.Unchanged;
        if (cfg.DeleteInactive && isInactive) activeRuleMatches |= WorkspaceCleanupReason.Inactive;

        return new WorkspaceCleanupCandidate(
            Project: project,
            Workspace: workspace,
            ShortPath: WorkspacePaths.FormatHomePath(workspace.Path, _workspaces.HomeDirectory),
            MatchedReasons: matchedReasons,
            ActiveRuleMatches: activeRuleMatches,
            Protections: protections,
            IsMerged: isMerged,
            IsUnchanged: isUnchanged,
            IsInactive: isInactive,
            InactiveDays: inactiveDays,
            LastActivityAt: lastActivity,
            UncommittedChangeCount: probe.UncommittedChangeCount,
            UnpushedCommitCount: protections.HasFlag(WorkspaceProtectionReason.UnpushedCommits) ? probe.UnpushedCommitCount : 0,
            OpenItemCount: openItems,
            RunningItemCount: runningItems,
            MergedPullRequest: prMergedComplete ? matchedPr : null,
            DefaultBranch: probe.DefaultBranch);
    }

    private async Task<(int OpenItems, int RunningItems, bool HasUnsavedEditor)> InspectUiStateAsync(Project project, ProjectWorkspace workspace)
    {
        var items = await RunOnUiAsync(() => project.Items.ToList()).ConfigureAwait(false);
        var openCount = 0;
        var runningCount = 0;
        var hasUnsaved = false;

        foreach (var item in items)
        {
            var belongsToWorkspace = item.WorkspaceId == workspace.Id
                || (item.Kind == ProjectItemKind.File && IsPathInsideDirectory(item.FilePath, workspace.Path))
                || (item.Kind is ProjectItemKind.Agent or ProjectItemKind.Terminal
                    && string.IsNullOrEmpty(item.WorkspaceId)
                    && IsPathInsideOrEqual(item.WorkingDirectory, workspace.Path));
            if (!belongsToWorkspace) continue;

            openCount++;

            var tab = await RunOnUiAsync(() => _tabs.Find(item.Id)).ConfigureAwait(false);
            if (tab is not null)
            {
                var unsaved = await RunOnUiAsync(() => tab.HasUnsavedChangesAsync()).ConfigureAwait(false);
                if (unsaved) hasUnsaved = true;
            }

            if (item.Kind is ProjectItemKind.Agent or ProjectItemKind.Terminal)
            {
                var isRunning = false;
                var act = await RunOnUiAsync(() => _activity.Of(item.Id)).ConfigureAwait(false);
                if (act is ItemActivity.Working or ItemActivity.Waiting)
                {
                    isRunning = true;
                }
                else
                {
                    var session = _sessions?.Find(item.Id);
                    if (session is not null)
                    {
                        isRunning = session.IsRunning;
                    }
                    else if (tab is not null)
                    {
                        isRunning = await RunOnUiAsync(() => tab.HasRunningChildAsync()).ConfigureAwait(false);
                    }
                    else
                    {
                        // An open agent/terminal tab whose PTY hasn't exited (or hasn't attached yet) still protects the workspace.
                        isRunning = true;
                    }
                }
                if (isRunning) runningCount++;
            }
        }

        return (openCount, runningCount, hasUnsaved);
    }

    private async Task<DateTimeOffset> ComputeLastActivityAsync(ProjectWorkspace workspace, DateTimeOffset? lastCommitDate, CancellationToken cancellationToken)
    {
        var latest = workspace.CreatedAt;
        if (workspace.LastActivityAt is { } recorded && recorded > latest)
        {
            latest = recorded;
        }
        if (lastCommitDate is { } committed && committed > latest)
        {
            latest = committed;
        }

        try
        {
            if (Directory.Exists(workspace.Path))
            {
                var dirWrite = new DateTimeOffset(Directory.GetLastWriteTimeUtc(workspace.Path), TimeSpan.Zero);
                if (dirWrite > latest) latest = dirWrite;

                var gitDirs = await _git.GetGitDirectoriesAsync(workspace.Path, cancellationToken).ConfigureAwait(false);
                if (gitDirs is not null)
                {
                    foreach (var file in new[] { "index", "HEAD" })
                    {
                        var path = Path.Combine(gitDirs.GitDirectory, file);
                        if (File.Exists(path))
                        {
                            var write = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                            if (write > latest) latest = write;
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return latest;
    }

    private static PullRequestMergeInfo? FindMergedPullRequest(ProjectWorkspace workspace, IReadOnlyList<PullRequestMergeInfo> mergedPrs)
    {
        if (mergedPrs.Count == 0) return null;

        // Prefer explicit PR task link match first (e.g. "PR #138").
        if (workspace.Task is { Kind: WorkspaceTaskKind.GitHubPr } task && TryParsePrNumber(task.Key, out var linkedNumber))
        {
            var byNumber = mergedPrs.FirstOrDefault(pr => pr.Number == linkedNumber);
            if (byNumber is not null) return byNumber;
        }

        if (string.IsNullOrWhiteSpace(workspace.Branch)) return null;
        return mergedPrs
            .Where(pr => string.Equals(pr.HeadBranch, workspace.Branch, StringComparison.Ordinal))
            .OrderByDescending(pr => pr.MergedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(pr => pr.Number)
            .FirstOrDefault();
    }

    private static bool TryParsePrNumber(string? key, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var hash = key.LastIndexOf('#');
        var span = hash >= 0 ? key.AsSpan(hash + 1) : key.AsSpan();
        return int.TryParse(span.Trim(), out number) && number > 0;
    }

    private static bool SameCommitHash(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var left = a.Trim();
        var right = b.Trim();
        return left.Equals(right, StringComparison.OrdinalIgnoreCase)
            || (left.Length >= 7 && right.Length >= 7
                && (left.StartsWith(right, StringComparison.OrdinalIgnoreCase) || right.StartsWith(left, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task RunScheduleLoopAsync()
    {
        var token = _life.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var cfg = _settings.Current.WorkspaceCleanup;
                if (!cfg.HasAutoRulesEnabled())
                {
                    await WaitForWakeOrCancelAsync(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                    continue;
                }

                // Wait an initial grace delay after startup or enabling rules, then tick on the configured interval.
                var delay = _lastRun is null ? _options.InitialDelay : TimeSpan.FromMinutes(Math.Clamp(cfg.CheckIntervalMinutes, WorkspaceCleanupSettings.MinCheckIntervalMinutes, WorkspaceCleanupSettings.MaxCheckIntervalMinutes));
                var wokenBySettings = await WaitForWakeOrCancelAsync(delay, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) break;
                if (wokenBySettings)
                {
                    // Settings changed (e.g. toggled a rule or interval): wait the fresh delay before sweeping.
                    continue;
                }

                if (!_settings.Current.WorkspaceCleanup.HasAutoRulesEnabled()) continue;
                try
                {
                    await RunAutoCleanupAsync(isAutomatic: true, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Background cleanup failures never crash the app; the next interval retries.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> WaitForWakeOrCancelAsync(TimeSpan delay, CancellationToken lifeToken)
    {
        CancellationToken wakeToken;
        lock (_scheduleLock)
        {
            wakeToken = _wake.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifeToken, wakeToken);
        try
        {
            await Task.Delay(delay, _time, linked.Token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (!lifeToken.IsCancellationRequested && wakeToken.IsCancellationRequested)
        {
            return true;
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var key = ScheduleKeyOf(_settings.Current.WorkspaceCleanup);
        lock (_scheduleLock)
        {
            if (_disposed || key == _lastScheduleKey) return;
            _lastScheduleKey = key;
            var old = _wake;
            _wake = new CancellationTokenSource();
            try { old.Cancel(); } catch (ObjectDisposedException) { }
            old.Dispose();
        }
    }

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (e.Project is not { } project) return;
        if (e.Kind is ProjectChangeKind.Items or ProjectChangeKind.Active && e.Item?.WorkspaceId is { Length: > 0 } wsId)
        {
            _projects.TouchWorkspaceActivity(project, wsId, _time.GetUtcNow());
        }
    }

    private void OnItemActivityChanged(object? sender, string itemId)
    {
        var project = _projects.FindProjectOf(itemId);
        var item = project?.Items.FirstOrDefault(i => i.Id == itemId);
        if (project is not null && item?.WorkspaceId is { Length: > 0 } wsId)
        {
            _projects.TouchWorkspaceActivity(project, wsId, _time.GetUtcNow());
        }
    }

    private void NotifyAutoCleanupToast(int removedCount)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_windows?.Window is not { } window) return;
            Toast.Show(
                window,
                AppLangResources.Format(AppLangResourceKind.WorkspaceCleanupAutoToast, removedCount),
                ToastKind.Info,
                null!,
                null!,
                TimeSpan.FromSeconds(5),
                null!);
        });
    }

    private Task<List<(Project Project, List<ProjectWorkspace> Workspaces)>> SnapshotLocalWorkspacesAsync() =>
        RunOnUiAsync(() => _projects.Projects
            .Where(p => ProjectLocation.IsOnThisComputer(p) && p.Workspaces.Count > 0)
            .Select(p => (p, p.Workspaces.ToList()))
            .ToList());

    private Task<ProjectWorkspace?> FindTrackedWorkspaceAsync(Project project, string workspaceId) =>
        RunOnUiAsync(() => _projects.FindWorkspace(project, workspaceId));

    private void SetRunning(bool running)
    {
        _isRunning = running;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetLastRun(WorkspaceCleanupRunSummary summary)
    {
        _lastRun = summary;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static (bool, bool, bool, int, int, int) ScheduleKeyOf(WorkspaceCleanupSettings cfg) =>
        (cfg.DeleteMerged, cfg.DeleteUnchanged, cfg.DeleteInactive, cfg.InactiveDays, cfg.CheckIntervalMinutes, cfg.UnchangedGraceHours);

    private static bool IsPathInsideDirectory(string? filePath, string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(directoryPath)) return false;
        var root = NormalizeRealPath(directoryPath).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        var fullFile = NormalizeRealPath(filePath);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return fullFile.StartsWith(root, comparison);
    }

    private static bool IsPathInsideOrEqual(string? candidatePath, string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(directoryPath)) return false;
        var root = NormalizeRealPath(directoryPath).TrimEnd('/', '\\');
        var candidate = NormalizeRealPath(candidatePath).TrimEnd('/', '\\');
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return candidate.Equals(root, comparison) || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static string NormalizeRealPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full))
            {
                return new DirectoryInfo(full).ResolveLinkTarget(true)?.FullName ?? full;
            }
            if (File.Exists(full))
            {
                return new FileInfo(full).ResolveLinkTarget(true)?.FullName ?? full;
            }
            if (Path.GetDirectoryName(full) is { } parent && Directory.Exists(parent))
            {
                var resolvedParent = new DirectoryInfo(parent).ResolveLinkTarget(true)?.FullName ?? parent;
                return Path.Combine(resolvedParent, Path.GetFileName(full));
            }
            return full;
        }
        catch (IOException)
        {
            return path;
        }
    }

    private static Task<T> RunOnUiAsync<T>(Func<T> func)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return Task.FromResult(func());
        }
        return Dispatcher.UIThread.InvokeAsync(func).GetTask();
    }

    private static Task<T> RunOnUiAsync<T>(Func<Task<T>> func)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return func();
        }
        return Dispatcher.UIThread.InvokeAsync(func);
    }

    public void Dispose()
    {
        lock (_scheduleLock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_started)
            {
                _projects.Changed -= OnProjectsChanged;
                _activity.Changed -= OnItemActivityChanged;
                _settings.Changed -= OnSettingsChanged;
            }
            try { _life.Cancel(); } catch (ObjectDisposedException) { }
            try { _wake.Cancel(); } catch (ObjectDisposedException) { }
            _life.Dispose();
            _wake.Dispose();
        }
        _runGate.Dispose();
    }
}
