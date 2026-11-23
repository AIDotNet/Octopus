using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.App.Services.Workspaces;
using Octopus.App.Settings.Agents;
using Octopus.Core.Projects;
using Octopus.Core.Settings;
using Octopus.Platform;
using Octopus.Storage;
using TKCode.Arc;
using TKCode.Arc.Localization;

namespace Octopus.App.Settings.Workspaces;

/// <summary>The workspaces settings page: default storage directory, manual inspection, scheduled auto-cleanup rules, advanced options and protections.</summary>
public sealed partial class WorkspaceCleanupSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly IWorkspaceCleanupService _cleanup;
    private readonly IProjectService _projects;
    private readonly IDialogService _dialogs;
    private readonly IFileOpener? _fileOpener;
    private readonly IWorkspaceManager? _workspaces;
    private readonly IPathPicker? _pathPicker;
    private readonly Debouncer _rootDirDebounce = new();
    private string? _lastPersistedRootDirectory;
    private bool _syncingRootDir;
    private bool _disposed;

    [ObservableProperty] public partial string RootDirectoryInput { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRootDirectoryError))]
    public partial string? RootDirectoryError { get; private set; }
    [ObservableProperty] public partial bool IsAdvancedExpanded { get; set; }
    [ObservableProperty] public partial bool IsRunningNow { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunMessage))]
    public partial string? LastRunMessage { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<SelectOption> IntervalOptions { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<SelectOption> GraceOptions { get; private set; } = [];

    public WorkspaceCleanupSettingsViewModel(
        ISettingsStore settings,
        IWorkspaceCleanupService cleanup,
        IProjectService projects,
        IDialogService dialogs,
        IFileOpener? fileOpener = null,
        IWorkspaceManager? workspaces = null,
        IPathPicker? pathPicker = null)
    {
        _settings = settings;
        _cleanup = cleanup;
        _projects = projects;
        _dialogs = dialogs;
        _fileOpener = fileOpener;
        _workspaces = workspaces;
        _pathPicker = pathPicker;

        _lastPersistedRootDirectory = _settings.Current.Workspaces.RootDirectory;
        _syncingRootDir = true;
        try
        {
            RootDirectoryInput = _lastPersistedRootDirectory ?? "";
        }
        finally
        {
            _syncingRootDir = false;
        }
        EffectiveRootDirectoryPath = WorkspacePaths.WorkspacesRoot(HomeDirectory, _lastPersistedRootDirectory);
        UpdateRootDirectoryState();

        BuildSelectOptions();
        RefreshFromService();
        _cleanup.Changed += OnCleanupChanged;
        _settings.Changed += OnSettingsChanged;
        ArcLanguage.Changed += OnLanguageChanged;
    }

    private string HomeDirectory => _workspaces?.HomeDirectory
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string DefaultRootDirectoryDisplay =>
        WorkspacePaths.FormatHomePath(WorkspacePaths.DefaultWorkspacesRoot(HomeDirectory), HomeDirectory);

    public string EffectiveRootDirectoryPath { get; private set; }

    public string EffectiveRootDirectoryPreview =>
        AppLangResources.Format(
            AppLangResourceKind.WorkspaceDirectoryPreview,
            WorkspacePaths.FormatHomePath(EffectiveRootDirectoryPath, HomeDirectory));

    public bool HasCustomRootDirectory =>
        !string.IsNullOrWhiteSpace(RootDirectoryInput)
        || !string.IsNullOrWhiteSpace(_settings.Current.Workspaces.RootDirectory);

    public bool HasRootDirectoryError => !string.IsNullOrEmpty(RootDirectoryError);

    public bool CanBrowseRootDirectory => _pathPicker is not null;

    public bool CanRevealRootDirectory => _fileOpener is not null;

    partial void OnRootDirectoryInputChanged(string value)
    {
        UpdateRootDirectoryState();
        if (!_syncingRootDir)
        {
            _rootDirDebounce.Schedule(CommitRootDirectory);
        }
    }

    [RelayCommand]
    private async Task BrowseRootDirectoryAsync()
    {
        if (_pathPicker is null) return;
        var picked = await _pathPicker.PickAsync(
            folder: true,
            AppLangResources.Get(AppLangResourceKind.WorkspaceDirectoryPickTitle));
        if (string.IsNullOrWhiteSpace(picked)) return;
        RootDirectoryInput = picked.Trim();
        FlushPending();
    }

    [RelayCommand]
    private void ResetRootDirectory()
    {
        _rootDirDebounce.Cancel();
        _syncingRootDir = true;
        try
        {
            RootDirectoryInput = "";
        }
        finally
        {
            _syncingRootDir = false;
        }
        CommitRootDirectory();
        UpdateRootDirectoryState();
    }

    [RelayCommand]
    private void RevealRootDirectory()
    {
        if (_fileOpener is null) return;
        FlushPending();
        var target = EffectiveRootDirectoryPath;
        if (string.IsNullOrWhiteSpace(target) || File.Exists(target)) return;
        if (!Directory.Exists(target))
        {
            try
            {
                Directory.CreateDirectory(target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
        _fileOpener.Reveal(target);
    }

    /// <summary>Writes any pending workspace directory text edit immediately.</summary>
    public void FlushPending() => _rootDirDebounce.Flush();

    private bool ValidateRootDirectory(string? input, out string resolved, out string? error)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            resolved = WorkspacePaths.DefaultWorkspacesRoot(HomeDirectory);
            error = null;
            return true;
        }
        if (!WorkspacePaths.TryResolveRootDirectory(input, HomeDirectory, out resolved))
        {
            error = AppLangResources.Get(AppLangResourceKind.WorkspaceDirectoryErrorInvalid);
            return false;
        }
        if (File.Exists(resolved))
        {
            error = AppLangResources.Get(AppLangResourceKind.WorkspaceDirectoryErrorIsFile);
            return false;
        }
        error = null;
        return true;
    }

    private void UpdateRootDirectoryState()
    {
        var valid = ValidateRootDirectory(RootDirectoryInput, out var resolved, out var error);
        RootDirectoryError = error;
        EffectiveRootDirectoryPath = valid
            ? resolved
            : WorkspacePaths.WorkspacesRoot(HomeDirectory, _settings.Current.Workspaces.RootDirectory);
        OnPropertyChanged(nameof(HasCustomRootDirectory));
        OnPropertyChanged(nameof(EffectiveRootDirectoryPath));
        OnPropertyChanged(nameof(EffectiveRootDirectoryPreview));
    }

    private void CommitRootDirectory()
    {
        var trimmed = RootDirectoryInput.Trim();
        if (!ValidateRootDirectory(trimmed, out _, out var error))
        {
            RootDirectoryError = error;
            return;
        }
        RootDirectoryError = null;
        var next = trimmed.Length == 0 ? null : trimmed;
        if (string.Equals(next, _settings.Current.Workspaces.RootDirectory, StringComparison.Ordinal))
        {
            _lastPersistedRootDirectory = next;
            return;
        }
        _lastPersistedRootDirectory = next;
        _settings.Update(s => s.Workspaces.RootDirectory = next);
        UpdateRootDirectoryState();
    }

    public bool DeleteMerged
    {
        get => _settings.Current.WorkspaceCleanup.DeleteMerged;
        set
        {
            if (value == DeleteMerged) return;
            _settings.Update(s => s.WorkspaceCleanup.DeleteMerged = value);
            NotifyRuleStateChanged(nameof(DeleteMerged));
        }
    }

    public bool DeleteUnchanged
    {
        get => _settings.Current.WorkspaceCleanup.DeleteUnchanged;
        set
        {
            if (value == DeleteUnchanged) return;
            _settings.Update(s => s.WorkspaceCleanup.DeleteUnchanged = value);
            NotifyRuleStateChanged(nameof(DeleteUnchanged));
        }
    }

    public bool DeleteInactive
    {
        get => _settings.Current.WorkspaceCleanup.DeleteInactive;
        set
        {
            if (value == DeleteInactive) return;
            _settings.Update(s => s.WorkspaceCleanup.DeleteInactive = value);
            NotifyRuleStateChanged(nameof(DeleteInactive));
        }
    }

    public decimal InactiveDays
    {
        get => _settings.Current.WorkspaceCleanup.InactiveDays;
        set
        {
            var clamped = (int)Math.Clamp(value, WorkspaceCleanupSettings.MinInactiveDays, WorkspaceCleanupSettings.MaxInactiveDays);
            if (clamped == _settings.Current.WorkspaceCleanup.InactiveDays) return;
            _settings.Update(s => s.WorkspaceCleanup.InactiveDays = clamped);
            OnPropertyChanged();
            OnPropertyChanged(nameof(InactiveRuleDescription));
        }
    }

    public string CheckIntervalKey
    {
        get => NormalizeInterval(_settings.Current.WorkspaceCleanup.CheckIntervalMinutes);
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !int.TryParse(value, out var minutes)) return;
            var clamped = Math.Clamp(minutes, WorkspaceCleanupSettings.MinCheckIntervalMinutes, WorkspaceCleanupSettings.MaxCheckIntervalMinutes);
            if (clamped == _settings.Current.WorkspaceCleanup.CheckIntervalMinutes) return;
            _settings.Update(s => s.WorkspaceCleanup.CheckIntervalMinutes = clamped);
            OnPropertyChanged();
        }
    }

    public string UnchangedGraceKey
    {
        get => NormalizeGrace(_settings.Current.WorkspaceCleanup.UnchangedGraceHours);
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !int.TryParse(value, out var hours)) return;
            var clamped = Math.Clamp(hours, 0, WorkspaceCleanupSettings.MaxUnchangedGraceHours);
            if (clamped == _settings.Current.WorkspaceCleanup.UnchangedGraceHours) return;
            _settings.Update(s => s.WorkspaceCleanup.UnchangedGraceHours = clamped);
            OnPropertyChanged();
        }
    }

    public bool DeleteMergedBranch
    {
        get => _settings.Current.WorkspaceCleanup.DeleteMergedBranch;
        set
        {
            if (value == DeleteMergedBranch) return;
            _settings.Update(s => s.WorkspaceCleanup.DeleteMergedBranch = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(BranchNoteText));
        }
    }

    public bool FetchBeforeCheck
    {
        get => _settings.Current.WorkspaceCleanup.FetchBeforeCheck;
        set
        {
            if (value == FetchBeforeCheck) return;
            _settings.Update(s => s.WorkspaceCleanup.FetchBeforeCheck = value);
            OnPropertyChanged();
        }
    }

    public bool IsAutoEnabled => _settings.Current.WorkspaceCleanup.HasAutoRulesEnabled();

    public bool HasRunMessage => !string.IsNullOrWhiteSpace(LastRunMessage);

    public string InactiveRuleDescription =>
        AppLangResources.Format(AppLangResourceKind.WorkspaceCleanupRuleInactiveDescription, _settings.Current.WorkspaceCleanup.InactiveDays);

    public string BranchNoteText => AppLangResources.Get(
        DeleteMergedBranch
            ? AppLangResourceKind.WorkspaceCleanupBranchNoteDelete
            : AppLangResourceKind.WorkspaceCleanupBranchNoteKeep);

    [RelayCommand]
    private void ToggleAdvanced() => IsAdvancedExpanded = !IsAdvancedExpanded;

    [RelayCommand]
    private async Task InspectWorkspacesAsync()
    {
        var summary = await _dialogs.ShowInspectWorkspacesAsync(_cleanup, _projects, _fileOpener);
        if (summary is not null)
        {
            LastRunMessage = FormatSummary(summary);
        }
    }

    [RelayCommand]
    private async Task RunNowAsync()
    {
        if (IsRunningNow || !IsAutoEnabled) return;
        IsRunningNow = true;
        try
        {
            var summary = await _cleanup.RunAutoCleanupAsync(isAutomatic: false);
            LastRunMessage = FormatSummary(summary);
        }
        finally
        {
            IsRunningNow = false;
        }
    }

    internal static string FormatSummary(WorkspaceCleanupRunSummary summary)
    {
        if (summary.RemovedCount == 0 && summary.SkippedCount == 0 && summary.FailedCount == 0)
        {
            return AppLangResources.Format(AppLangResourceKind.WorkspaceCleanupRunResultNone, summary.InspectedCount);
        }
        var parts = new List<string>();
        if (summary.RemovedCount > 0)
        {
            parts.Add(AppLangResources.Format(AppLangResourceKind.WorkspaceCleanupRunResultRemoved, summary.RemovedCount));
        }
        if (summary.SkippedCount > 0)
        {
            parts.Add(AppLangResources.Format(AppLangResourceKind.WorkspaceCleanupRunResultSkipped, summary.SkippedCount));
        }
        if (summary.FailedCount > 0)
        {
            parts.Add(AppLangResources.Format(AppLangResourceKind.WorkspaceCleanupRunResultFailed, summary.FailedCount));
        }
        return string.Join(" · ", parts);
    }

    private void NotifyRuleStateChanged(string propertyName)
    {
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(IsAutoEnabled));
    }

    private void BuildSelectOptions()
    {
        IntervalOptions =
        [
            new("30", AppLangResources.Get(AppLangResourceKind.WorkspaceCleanupInterval30m)),
            new("60", AppLangResources.Get(AppLangResourceKind.WorkspaceCleanupInterval1h)),
            new("360", AppLangResources.Get(AppLangResourceKind.WorkspaceCleanupInterval6h)),
            new("1440", AppLangResources.Get(AppLangResourceKind.WorkspaceCleanupInterval24h)),
        ];
        GraceOptions =
        [
            new("0", AppLangResources.Get(AppLangResourceKind.WorkspaceCleanupGraceNone)),
            new("1", AppLangResources.Get(AppLangResourceKind.WorkspaceCleanupGrace1h)),
            new("6", AppLangResources.Get(AppLangResourceKind.WorkspaceCleanupGrace6h)),
            new("24", AppLangResources.Get(AppLangResourceKind.WorkspaceCleanupGrace24h)),
        ];
    }

    private static string NormalizeInterval(int minutes) => minutes switch
    {
        <= 30 => "30",
        <= 60 => "60",
        <= 360 => "360",
        _ => "1440",
    };

    private static string NormalizeGrace(int hours) => hours switch
    {
        <= 0 => "0",
        <= 1 => "1",
        <= 6 => "6",
        _ => "24",
    };

    private void RefreshFromService()
    {
        IsRunningNow = _cleanup.IsRunning;
        if (_cleanup.LastRun is { } run)
        {
            LastRunMessage = FormatSummary(run);
        }
    }

    private void OnCleanupChanged(object? sender, EventArgs e) => UiContext.Run(() =>
    {
        if (_disposed) return;
        RefreshFromService();
    });

    private void OnSettingsChanged(object? sender, EventArgs e) => UiContext.Run(() =>
    {
        if (_disposed) return;
        var savedRoot = _settings.Current.Workspaces.RootDirectory;
        if (!string.Equals(savedRoot, _lastPersistedRootDirectory, StringComparison.Ordinal))
        {
            _lastPersistedRootDirectory = savedRoot;
            _rootDirDebounce.Cancel();
            _syncingRootDir = true;
            try
            {
                RootDirectoryInput = savedRoot ?? "";
            }
            finally
            {
                _syncingRootDir = false;
            }
            UpdateRootDirectoryState();
        }
        OnPropertyChanged(nameof(DeleteMerged));
        OnPropertyChanged(nameof(DeleteUnchanged));
        OnPropertyChanged(nameof(DeleteInactive));
        OnPropertyChanged(nameof(InactiveDays));
        OnPropertyChanged(nameof(CheckIntervalKey));
        OnPropertyChanged(nameof(UnchangedGraceKey));
        OnPropertyChanged(nameof(DeleteMergedBranch));
        OnPropertyChanged(nameof(FetchBeforeCheck));
        OnPropertyChanged(nameof(IsAutoEnabled));
        OnPropertyChanged(nameof(InactiveRuleDescription));
        OnPropertyChanged(nameof(BranchNoteText));
    });

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e) => UiContext.Run(() =>
    {
        if (_disposed) return;
        BuildSelectOptions();
        UpdateRootDirectoryState();
        OnPropertyChanged(nameof(CheckIntervalKey));
        OnPropertyChanged(nameof(UnchangedGraceKey));
        OnPropertyChanged(nameof(InactiveRuleDescription));
        OnPropertyChanged(nameof(BranchNoteText));
        if (_cleanup.LastRun is { } run) LastRunMessage = FormatSummary(run);
    });

    public void Dispose()
    {
        if (_disposed) return;
        FlushPending();
        _disposed = true;
        _cleanup.Changed -= OnCleanupChanged;
        _settings.Changed -= OnSettingsChanged;
        ArcLanguage.Changed -= OnLanguageChanged;
    }
}
