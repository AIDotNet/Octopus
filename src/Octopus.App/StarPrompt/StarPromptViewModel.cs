using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Activity;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.App.Updates;
using Octopus.Core.Projects;
using Octopus.Core.Settings;
using Octopus.Platform;
using Octopus.Storage;
using Octopus.Updates;
using TKCode.Arc.Localization;

namespace Octopus.App.StarPrompt;

/// <summary>
/// Manages eligibility, non-intrusive timing, conflict avoidance, and tiered cooldowns for the bottom-right GitHub Star
/// floating pill (Scheme C). All tracking is strictly local in <c>settings.json</c>.
/// </summary>
public sealed partial class StarPromptViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly IProjectService _projects;
    private readonly IItemActivityService _activity;
    private readonly UpdateCardViewModel _updateCard;
    private readonly Account.AccountViewModel _account;
    private readonly IFileOpener _opener;
    private readonly StarPromptOptions _options;

    private readonly Dictionary<string, ItemActivity> _lastActivities = [];
    private readonly HashSet<string> _countedSessionItems = [];

    private DateTimeOffset _sessionStartedAtUtc;
    private DateTimeOffset _lastUserInputUtc;
    private bool _started;
    private bool _shownInThisRun;
    private bool _isSettingsOpen;
    private bool _isWindowActive = true;
    private double _windowWidth = 1280;
    private bool _disposed;

    private CancellationTokenSource? _momentCts;
    private CancellationTokenSource? _thanksCts;
    private PeriodicTimer? _fallbackTimer;
    private CancellationTokenSource? _fallbackCts;

    public StarPromptViewModel(
        ISettingsStore settings,
        IProjectService projects,
        IItemActivityService activity,
        UpdateCardViewModel updateCard,
        Account.AccountViewModel account,
        IFileOpener opener,
        StarPromptOptions? options = null)
    {
        _settings = settings;
        _projects = projects;
        _activity = activity;
        _updateCard = updateCard;
        _account = account;
        _opener = opener;
        _options = options ?? new StarPromptOptions();

        var now = _options.UtcNow();
        _sessionStartedAtUtc = now;
        _lastUserInputUtc = now;

        _projects.Changed += OnProjectsChanged;
        _activity.Changed += OnActivityChanged;
        _updateCard.PropertyChanged += OnUpdateCardPropertyChanged;
        _account.PropertyChanged += OnAccountPropertyChanged;
        ArcLanguage.Changed += OnLanguageChanged;

        RefreshSnoozeMetadata();
    }

    public string RepositoryDisplay => "github.com/AIDotNet/Octopus";

    [ObservableProperty] public partial bool IsOpen { get; private set; }
    [ObservableProperty] public partial bool IsPromptVisible { get; private set; }
    [ObservableProperty] public partial bool IsThanksVisible { get; private set; }
    [ObservableProperty] public partial bool IsCloseMenuOpen { get; private set; }
    [ObservableProperty] public partial string SnoozeLabel { get; private set; } = "";
    [ObservableProperty] public partial bool CanSnooze { get; private set; } = true;

    /// <summary>Starts daily activity recording and the fallback idle monitor once the main window is up.</summary>
    public void Start()
    {
        if (_disposed || _started) return;
        _started = true;
        var now = _options.UtcNow();
        _sessionStartedAtUtc = now;
        _lastUserInputUtc = now;
        RecordActiveDay();

        if (_options.AutoStart && !IsPermanentlySilenced(_settings.Current.StarPrompt))
        {
            _fallbackCts = new CancellationTokenSource();
            _fallbackTimer = new PeriodicTimer(_options.FallbackPollInterval);
            _ = RunFallbackLoopAsync(_fallbackTimer, _fallbackCts.Token);
        }
    }

    /// <summary>Records local daily usage (first-seen timestamp and distinct calendar days).</summary>
    public void RecordActiveDay()
    {
        if (_disposed) return;
        var now = _options.UtcNow();
        var today = _options.LocalToday().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var current = _settings.Current.StarPrompt;

        if (current.FirstSeenUtc is not null && string.Equals(current.LastActiveDate, today, StringComparison.Ordinal))
        {
            return;
        }

        _settings.Update(s =>
        {
            s.StarPrompt ??= new();
            s.StarPrompt.FirstSeenUtc ??= now;
            if (!string.Equals(s.StarPrompt.LastActiveDate, today, StringComparison.Ordinal))
            {
                s.StarPrompt.LastActiveDate = today;
                s.StarPrompt.ActiveDaysCount++;
            }
        });
    }

    /// <summary>Notifies the engine of recent keyboard or pointer input in the main window.</summary>
    public void NotifyUserInput()
    {
        if (_disposed) return;
        _lastUserInputUtc = _options.UtcNow();
    }

    /// <summary>Tracks whether the full-window settings overlay is covering the workspace.</summary>
    public void SetSettingsOpen(bool open)
    {
        if (_disposed || _isSettingsOpen == open) return;
        _isSettingsOpen = open;
        if (open) YieldToConflict();
    }

    /// <summary>Tracks main window foreground focus and width so the pill stays non-intrusive.</summary>
    public void SetWindowState(bool isActive, double width)
    {
        if (_disposed) return;
        _isWindowActive = isActive;
        if (!double.IsNaN(width) && width > 0) _windowWidth = width;
        if (_windowWidth < _options.MinWindowWidth) YieldToConflict();
    }

    /// <summary>
    /// Marks the repository as starred from another surface (such as the welcome page's GitHub link)
    /// so the user is never reminded afterward.
    /// </summary>
    public void MarkStarredExternally()
    {
        if (_disposed) return;
        CancelMomentTimer();
        StopFallbackTimer();
        _settings.Update(s =>
        {
            s.StarPrompt ??= new();
            s.StarPrompt.Starred = true;
            s.StarPrompt.SnoozeUntilUtc = null;
        });
        if (IsPromptVisible)
        {
            IsCloseMenuOpen = false;
            IsPromptVisible = false;
            IsThanksVisible = false;
            IsOpen = false;
        }
    }

    /// <summary>Evaluates all eligibility, cooldown, and conflict-avoidance rules right now.</summary>
    public bool CanShowNow()
    {
        if (_disposed || _shownInThisRun || IsOpen || IsThanksVisible) return false;

        var s = _settings.Current.StarPrompt;
        if (IsPermanentlySilenced(s)) return false;

        var now = _options.UtcNow();
        if (s.SnoozeUntilUtc is { } snoozeUntil && now < snoozeUntil) return false;

        if (s.FirstSeenUtc is not { } firstSeen || now - firstSeen < TimeSpan.FromDays(_options.MinInstallDays))
        {
            return false;
        }

        if (s.ActiveDaysCount < _options.MinActiveDays) return false;
        if (_projects.Projects.Count == 0) return false;

        var reachedValueMilestone = s.CompletedAgentTurns >= _options.MinCompletedTurns
            || s.SessionStartCount >= _options.MinSessionStarts;
        if (!reachedValueMilestone) return false;

        if (!IsEnvironmentClear()) return false;

        return true;
    }

    /// <summary>Attempts to show the pill immediately if all eligibility and guardrail rules pass.</summary>
    public bool TryTriggerNow()
    {
        if (!CanShowNow()) return false;
        ShowPrompt();
        return true;
    }

    /// <summary>Checks the terminal-only fallback rule (active session duration + quiet input pause).</summary>
    public bool TryEvaluateFallbackIdle()
    {
        if (_disposed) return false;
        RecordActiveDay();
        var now = _options.UtcNow();
        if (now - _sessionStartedAtUtc < _options.FallbackActiveDuration) return false;
        if (now - _lastUserInputUtc < _options.FallbackIdlePause) return false;
        return TryTriggerNow();
    }

    [RelayCommand]
    private void Star()
    {
        if (_disposed) return;
        CancelMomentTimer();
        StopFallbackTimer();
        IsCloseMenuOpen = false;

        _settings.Update(s =>
        {
            s.StarPrompt ??= new();
            s.StarPrompt.Starred = true;
            s.StarPrompt.SnoozeUntilUtc = null;
        });

        _opener.OpenUrl(UpdateEndpoints.RepositoryUrl);

        IsPromptVisible = false;
        IsThanksVisible = true;
        IsOpen = true;
        ScheduleThanksHide();
    }

    [RelayCommand]
    private void ToggleCloseMenu()
    {
        if (_disposed || !IsPromptVisible) return;
        if (!CanSnooze)
        {
            // Final allowed impression: dismiss cleanly and permanently without offering another snooze.
            Snooze();
            return;
        }
        if (IsCloseMenuOpen)
        {
            // Clicking × a second time acts as a quick snooze for impatient users.
            Snooze();
            return;
        }
        RefreshSnoozeMetadata();
        IsCloseMenuOpen = true;
    }

    [RelayCommand]
    private void Snooze()
    {
        if (_disposed) return;
        CancelMomentTimer();
        IsCloseMenuOpen = false;
        IsPromptVisible = false;
        IsThanksVisible = false;
        IsOpen = false;

        var now = _options.UtcNow();
        var exhausted = false;
        _settings.Update(s =>
        {
            s.StarPrompt ??= new();
            s.StarPrompt.PromptCount++;
            if (s.StarPrompt.PromptCount >= _options.MaxPromptCount)
            {
                s.StarPrompt.DismissedForever = true;
                s.StarPrompt.SnoozeUntilUtc = null;
                exhausted = true;
            }
            else
            {
                var days = s.StarPrompt.PromptCount == 1 ? _options.FirstSnoozeDays : _options.SecondSnoozeDays;
                s.StarPrompt.SnoozeUntilUtc = now.AddDays(days);
            }
        });

        RefreshSnoozeMetadata();
        if (exhausted) StopFallbackTimer();
    }

    [RelayCommand]
    private void DismissForever()
    {
        if (_disposed) return;
        CancelMomentTimer();
        StopFallbackTimer();
        IsCloseMenuOpen = false;
        IsPromptVisible = false;
        IsThanksVisible = false;
        IsOpen = false;

        _settings.Update(s =>
        {
            s.StarPrompt ??= new();
            s.StarPrompt.DismissedForever = true;
            s.StarPrompt.SnoozeUntilUtc = null;
        });
    }

    private void ShowPrompt()
    {
        CancelMomentTimer();
        _shownInThisRun = true;
        RefreshSnoozeMetadata();
        IsCloseMenuOpen = false;
        IsThanksVisible = false;
        IsPromptVisible = true;
        IsOpen = true;
    }

    /// <summary>
    /// Hides the prompt without consuming a prompt count or snooze when a higher-priority surface
    /// (update card, settings overlay, sign-in dialog, or narrow window) needs the space.
    /// </summary>
    private void YieldToConflict()
    {
        CancelMomentTimer();
        if (!IsPromptVisible || IsThanksVisible) return;
        IsCloseMenuOpen = false;
        IsPromptVisible = false;
        IsOpen = false;
        _shownInThisRun = false;
    }

    private bool IsPermanentlySilenced(StarPromptSettings s) =>
        s.Starred || s.DismissedForever || s.PromptCount >= _options.MaxPromptCount;

    private bool IsEnvironmentClear()
    {
        if (_updateCard.IsOpen) return false;
        if (_account.IsSignInDialogOpen) return false;
        if (_isSettingsOpen) return false;
        if (!_isWindowActive) return false;
        if (_windowWidth < _options.MinWindowWidth) return false;

        foreach (var project in _projects.Projects)
        {
            foreach (var item in project.Items)
            {
                var state = _activity.Of(item.Id);
                if (state is ItemActivity.Waiting or ItemActivity.Error)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        UiContext.Run(() =>
        {
            if (_disposed) return;
            RecordActiveDay();

            if (e.Kind == ProjectChangeKind.Items && e.Item is { } item)
            {
                var exists = _projects.FindItem(item.Id) is not null;
                if (!exists)
                {
                    _lastActivities.Remove(item.Id);
                    _countedSessionItems.Remove(item.Id);
                    return;
                }

                if (item.Kind is ProjectItemKind.Agent or ProjectItemKind.Terminal
                    && !_projects.WasRestored(item.Id)
                    && _countedSessionItems.Add(item.Id))
                {
                    _settings.Update(s =>
                    {
                        s.StarPrompt ??= new();
                        s.StarPrompt.SessionStartCount++;
                    });
                }
            }
            else if (e.Kind is ProjectChangeKind.Active or ProjectChangeKind.CurrentProject)
            {
                // User just looked at a finished tab; if everything is eligible, let them settle for 3s then trigger.
                if (e.Item is { } active && _lastActivities.GetValueOrDefault(active.Id) == ItemActivity.Done)
                {
                    _lastActivities[active.Id] = _activity.Of(active.Id);
                    SchedulePositiveMoment();
                }
            }
        });
    }

    private void OnActivityChanged(object? sender, string itemId)
    {
        UiContext.Run(() =>
        {
            if (_disposed) return;
            var previous = _lastActivities.GetValueOrDefault(itemId);
            var current = _activity.Of(itemId);
            _lastActivities[itemId] = current;

            if (current is ItemActivity.Error or ItemActivity.Waiting)
            {
                CancelMomentTimer();
                return;
            }

            if (previous == ItemActivity.Working
                && current is ItemActivity.Idle or ItemActivity.Done
                && _projects.FindItem(itemId) is { Kind: ProjectItemKind.Agent })
            {
                RecordActiveDay();
                _settings.Update(s =>
                {
                    s.StarPrompt ??= new();
                    s.StarPrompt.CompletedAgentTurns++;
                });

                // Only schedule immediately if the user is looking at the tab (Idle), or when they later switch to it (Done -> Active).
                if (current == ItemActivity.Idle)
                {
                    SchedulePositiveMoment();
                }
            }
        });
    }

    private void OnUpdateCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UpdateCardViewModel.IsOpen)) return;
        UiContext.Run(() =>
        {
            if (_updateCard.IsOpen) YieldToConflict();
        });
    }

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Account.AccountViewModel.IsSignInDialogOpen)) return;
        UiContext.Run(() =>
        {
            if (_account.IsSignInDialogOpen) YieldToConflict();
        });
    }

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e)
    {
        UiContext.PostToUi(() =>
        {
            if (!_disposed) RefreshSnoozeMetadata();
        });
    }

    private void RefreshSnoozeMetadata()
    {
        var count = _settings.Current.StarPrompt.PromptCount;
        CanSnooze = count + 1 < _options.MaxPromptCount;
        var days = count == 0 ? _options.FirstSnoozeDays : _options.SecondSnoozeDays;
        SnoozeLabel = AppLangResources.Format(AppLangResourceKind.StarPromptSnoozeDays, days);
    }

    private void SchedulePositiveMoment()
    {
        if (!CanShowNow()) return;
        if (_options.CompletionPauseDelay <= TimeSpan.Zero)
        {
            TryTriggerNow();
            return;
        }
        CancelMomentTimer();
        var cts = _momentCts = new CancellationTokenSource();
        _ = WaitForMomentAsync(_options.CompletionPauseDelay, cts.Token);
    }

    private async Task WaitForMomentAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            UiContext.PostToUi(() =>
            {
                if (!ct.IsCancellationRequested && !_disposed)
                {
                    TryTriggerNow();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ScheduleThanksHide()
    {
        CancelThanksTimer();
        if (_options.ThanksDisplayDuration <= TimeSpan.Zero)
        {
            IsThanksVisible = false;
            IsOpen = false;
            return;
        }
        var cts = _thanksCts = new CancellationTokenSource();
        _ = HideThanksAfterAsync(_options.ThanksDisplayDuration, cts.Token);
    }

    private async Task HideThanksAfterAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            UiContext.PostToUi(() =>
            {
                if (!ct.IsCancellationRequested && !_disposed && IsThanksVisible)
                {
                    IsThanksVisible = false;
                    IsOpen = false;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunFallbackLoopAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                UiContext.PostToUi(() =>
                {
                    if (!ct.IsCancellationRequested && !_disposed)
                    {
                        if (IsPermanentlySilenced(_settings.Current.StarPrompt) || _shownInThisRun)
                        {
                            StopFallbackTimer();
                            return;
                        }
                        TryEvaluateFallbackIdle();
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelMomentTimer()
    {
        _momentCts?.Cancel();
        _momentCts?.Dispose();
        _momentCts = null;
    }

    private void CancelThanksTimer()
    {
        _thanksCts?.Cancel();
        _thanksCts?.Dispose();
        _thanksCts = null;
    }

    private void StopFallbackTimer()
    {
        _fallbackCts?.Cancel();
        _fallbackCts?.Dispose();
        _fallbackCts = null;
        _fallbackTimer?.Dispose();
        _fallbackTimer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _projects.Changed -= OnProjectsChanged;
        _activity.Changed -= OnActivityChanged;
        _updateCard.PropertyChanged -= OnUpdateCardPropertyChanged;
        _account.PropertyChanged -= OnAccountPropertyChanged;
        ArcLanguage.Changed -= OnLanguageChanged;
        CancelMomentTimer();
        CancelThanksTimer();
        StopFallbackTimer();
    }
}
