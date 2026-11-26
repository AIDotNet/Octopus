using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.App.Services.Workspaces;
using Octopus.Platform;

namespace Octopus.App.Dialogs;

/// <summary>One workspace row inside the manual "Inspect workspaces" cleanup dialog.</summary>
public sealed partial class InspectWorkspaceRowViewModel : ObservableObject
{
    private readonly Action<InspectWorkspaceRowViewModel> _onSelectionChanged;
    private readonly Func<InspectWorkspaceRowViewModel, Task> _togglePin;
    private readonly Action<InspectWorkspaceRowViewModel> _reveal;

    public InspectWorkspaceRowViewModel(
        WorkspaceCleanupCandidate candidate,
        Action<InspectWorkspaceRowViewModel> onSelectionChanged,
        Func<InspectWorkspaceRowViewModel, Task> togglePin,
        Action<InspectWorkspaceRowViewModel> reveal)
    {
        Candidate = candidate;
        _onSelectionChanged = onSelectionChanged;
        _togglePin = togglePin;
        _reveal = reveal;

        ProjectName = candidate.Project.Name;
        BranchName = candidate.Workspace.DisplayName;
        ShortPath = candidate.ShortPath;
        TaskKey = candidate.Workspace.Task?.Key;
        IsPinned = candidate.Workspace.IsPinned;
        HasUnsavedEdits = candidate.Protections.HasFlag(WorkspaceProtectionReason.UnsavedEditorChanges);
        HasRunning = candidate.Protections.HasFlag(WorkspaceProtectionReason.RunningSession);
        HasUncommitted = candidate.Protections.HasFlag(WorkspaceProtectionReason.UncommittedChanges);
        HasUnpushed = candidate.Protections.HasFlag(WorkspaceProtectionReason.UnpushedCommits);
        HasStatusUnavailable = candidate.Protections.HasFlag(WorkspaceProtectionReason.GitStatusUnavailable);

        IsMerged = candidate.IsMerged;
        IsUnchanged = candidate.IsUnchanged;
        IsInactive = candidate.IsInactive;
        IsRecommended = candidate.IsRecommendedForManualCleanup;
        IsProtected = candidate.IsProtected;

        MergedBadgeText = candidate.MergedPullRequest is { } pr
            ? AppLangResources.Format(AppLangResourceKind.WorkspaceInspectBadgeMergedPr, pr.Number)
            : AppLangResources.Get(AppLangResourceKind.WorkspaceInspectBadgeMerged);
        InactiveBadgeText = AppLangResources.Format(AppLangResourceKind.WorkspaceInspectBadgeInactive, candidate.InactiveDays);
        RunningBadgeText = AppLangResources.Format(AppLangResourceKind.WorkspaceInspectBadgeRunning, candidate.RunningItemCount);
        UncommittedBadgeText = AppLangResources.Format(AppLangResourceKind.WorkspaceInspectBadgeUncommitted, candidate.UncommittedChangeCount);
        UnpushedBadgeText = AppLangResources.Format(AppLangResourceKind.WorkspaceInspectBadgeUnpushed, candidate.UnpushedCommitCount);
        LastActivityText = candidate.InactiveDays <= 0
            ? AppLangResources.Get(AppLangResourceKind.WorkspaceInspectLastActivityToday)
            : AppLangResources.Format(AppLangResourceKind.WorkspaceInspectLastActivityDays, candidate.InactiveDays);

        // Pinned workspaces and editors with unsaved in-memory edits cannot be bulk-deleted.
        CanSelect = !IsPinned && !HasUnsavedEdits;
        RequiresForceWarning = HasUncommitted || HasUnpushed || HasRunning || HasStatusUnavailable;
    }

    public WorkspaceCleanupCandidate Candidate { get; }
    public string ProjectName { get; }
    public string BranchName { get; }
    public string ShortPath { get; }
    public string? TaskKey { get; }
    public bool HasTask => !string.IsNullOrWhiteSpace(TaskKey);

    public bool IsPinned { get; }
    public bool HasUnsavedEdits { get; }
    public bool HasRunning { get; }
    public bool HasUncommitted { get; }
    public bool HasUnpushed { get; }
    public bool HasStatusUnavailable { get; }

    public bool IsMerged { get; }
    public bool IsUnchanged { get; }
    public bool IsInactive { get; }
    public bool IsRecommended { get; }
    public bool IsProtected { get; }
    public bool CanSelect { get; }
    public bool RequiresForceWarning { get; }

    public string MergedBadgeText { get; }
    public string InactiveBadgeText { get; }
    public string RunningBadgeText { get; }
    public string UncommittedBadgeText { get; }
    public string UnpushedBadgeText { get; }
    public string LastActivityText { get; }
    public string PinTooltip => AppLangResources.Get(IsPinned ? AppLangResourceKind.WorkspaceUnpin : AppLangResourceKind.WorkspacePin);

    [ObservableProperty] public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !CanSelect)
        {
            IsSelected = false;
            return;
        }
        _onSelectionChanged(this);
    }

    [RelayCommand]
    private Task TogglePinAsync() => _togglePin(this);

    [RelayCommand]
    private void Reveal() => _reveal(this);
}

/// <summary>View model for the manual "Inspect workspaces" cleanup dialog.</summary>
public sealed partial class InspectWorkspacesViewModel : ObservableObject
{
    private readonly IWorkspaceCleanupService _cleanup;
    private readonly IProjectService _projects;
    private readonly IFileOpener? _fileOpener;
    private readonly bool _deleteMergedBranch;
    private readonly List<InspectWorkspaceRowViewModel> _all = [];

    public InspectWorkspacesViewModel(
        IWorkspaceCleanupService cleanup,
        IProjectService projects,
        IFileOpener? fileOpener,
        bool deleteMergedBranch)
    {
        _cleanup = cleanup;
        _projects = projects;
        _fileOpener = fileOpener;
        _deleteMergedBranch = deleteMergedBranch;
    }

    public ObservableCollection<InspectWorkspaceRowViewModel> Filtered { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanConfirmRemove), nameof(ShowEmpty), nameof(ShowFilterEmpty))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanConfirmRemove))]
    public partial bool IsRemoving { get; private set; }

    [ObservableProperty] public partial string Filter { get; set; } = "all";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanConfirmRemove), nameof(RemoveButtonText))]
    public partial int SelectedCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForceWarning), nameof(ForceWarningText))]
    public partial int SelectedWarningCount { get; private set; }

    [ObservableProperty] public partial string SummaryText { get; private set; } = "";

    public bool IsBusy => IsLoading || IsRemoving;
    public bool HasSelection => SelectedCount > 0;
    public bool CanConfirmRemove => SelectedCount > 0 && !IsBusy;
    public bool HasForceWarning => SelectedWarningCount > 0;
    public bool ShowEmpty => !IsLoading && _all.Count == 0;
    public bool ShowFilterEmpty => !IsLoading && _all.Count > 0 && Filtered.Count == 0;
    public bool HasAnySuggested => _all.Any(r => r.IsRecommended);

    public string RemoveButtonText => SelectedCount > 0
        ? AppLangResources.Format(AppLangResourceKind.WorkspaceInspectRemoveButton, SelectedCount)
        : AppLangResources.Get(AppLangResourceKind.WorkspaceInspectRemoveDisabled);

    public string ForceWarningText => AppLangResources.Format(AppLangResourceKind.WorkspaceInspectForceWarning, SelectedWarningCount);

    public string BranchNoteText => AppLangResources.Get(
        _deleteMergedBranch
            ? AppLangResourceKind.WorkspaceCleanupBranchNoteDelete
            : AppLangResourceKind.WorkspaceCleanupBranchNoteKeep);

    public event EventHandler? SelectionStateChanged;

    partial void OnFilterChanged(string value) => ApplyFilter();

    public async Task LoadAsync(bool selectRecommended = true, CancellationToken cancellationToken = default)
    {
        var previouslySelected = selectRecommended
            ? null
            : _all.Where(r => r.IsSelected).Select(r => r.Candidate.Workspace.Id).ToHashSet(StringComparer.Ordinal);

        IsLoading = true;
        try
        {
            var candidates = await _cleanup.InspectAllAsync(fetchRemotes: false, cancellationToken);
            _all.Clear();
            foreach (var candidate in candidates
                .OrderByDescending(c => c.IsRecommendedForManualCleanup)
                .ThenBy(c => c.IsProtected)
                .ThenBy(c => c.Project.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(c => c.Workspace.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var row = new InspectWorkspaceRowViewModel(candidate, OnRowSelectionChanged, TogglePinAsync, RevealRow);
                if (selectRecommended)
                {
                    if (row.IsRecommended && row.CanSelect) row.IsSelected = true;
                }
                else if (previouslySelected is not null && previouslySelected.Contains(candidate.Workspace.Id) && row.CanSelect)
                {
                    row.IsSelected = true;
                }
                _all.Add(row);
            }
            UpdateSummary();
            ApplyFilter();
            UpdateSelectionCounts();
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasAnySuggested));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(ShowFilterEmpty));
            SelectionStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<WorkspaceCleanupSelection> BuildSelections() =>
        _all.Where(r => r.IsSelected && r.CanSelect)
            .Select(r => new WorkspaceCleanupSelection(r.Candidate.Project, r.Candidate.Workspace, r.RequiresForceWarning))
            .ToList();

    public async Task<WorkspaceCleanupRunSummary?> RemoveSelectedAsync(CancellationToken cancellationToken = default)
    {
        var selections = BuildSelections();
        if (selections.Count == 0) return null;
        IsRemoving = true;
        SelectionStateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            return await _cleanup.CleanSelectedAsync(selections, cancellationToken);
        }
        finally
        {
            IsRemoving = false;
            SelectionStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(selectRecommended: false);

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var row in _all)
        {
            row.IsSelected = row.IsRecommended && row.CanSelect;
        }
        UpdateSelectionCounts();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in _all)
        {
            row.IsSelected = false;
        }
        UpdateSelectionCounts();
    }

    private async Task TogglePinAsync(InspectWorkspaceRowViewModel row)
    {
        _projects.SetWorkspacePinned(row.Candidate.Project, row.Candidate.Workspace.Id, !row.IsPinned);
        await LoadAsync(selectRecommended: false);
    }

    private void RevealRow(InspectWorkspaceRowViewModel row) => _fileOpener?.Reveal(row.Candidate.Workspace.Path);

    private void OnRowSelectionChanged(InspectWorkspaceRowViewModel _) => UpdateSelectionCounts();

    private void UpdateSelectionCounts()
    {
        SelectedCount = _all.Count(r => r.IsSelected);
        SelectedWarningCount = _all.Count(r => r.IsSelected && r.RequiresForceWarning);
        SelectionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSummary()
    {
        var suggested = _all.Count(r => r.IsRecommended);
        var protectedCount = _all.Count(r => r.IsProtected);
        SummaryText = AppLangResources.Format(AppLangResourceKind.WorkspaceInspectSummary, _all.Count, suggested, protectedCount);
    }

    private void ApplyFilter()
    {
        Filtered.Clear();
        foreach (var row in _all)
        {
            var match = Filter switch
            {
                "eligible" => row.IsRecommended,
                "protected" => row.IsProtected,
                _ => true,
            };
            if (match) Filtered.Add(row);
        }
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowFilterEmpty));
    }
}
