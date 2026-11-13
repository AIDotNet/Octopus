using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents.Subagents;
using Octopus.App.Localization;

namespace Octopus.App.Sidebar;

/// <summary>
/// The subagents under an agent's row: a card per running (or failed) one, and the finished ones folded into one
/// "N finished" line. Kept per item across rebuilds of the panel, so what the user opened stays open.
/// </summary>
public sealed partial class SubagentListViewModel : ObservableObject
{
    private readonly Action _activate;
    private int _knownStarts;
    /// <summary>The user opened or closed the list since the last subagent started: their choice wins until then.</summary>
    private bool _userChose;

    public SubagentListViewModel(Action activate) => _activate = activate;

    /// <summary>Running and failed ones, oldest first.</summary>
    public ObservableCollection<SubagentCardViewModel> Active { get; } = [];

    /// <summary>Done and interrupted ones, behind the fold.</summary>
    public ObservableCollection<SubagentCardViewModel> Finished { get; } = [];

    [ObservableProperty] public partial bool HasAny { get; private set; }
    [ObservableProperty] public partial bool IsExpanded { get; private set; }
    [ObservableProperty] public partial bool IsFoldOpen { get; private set; }
    /// <summary>"1/3": finished of all.</summary>
    [ObservableProperty] public partial string CountText { get; private set; } = "";
    [ObservableProperty] public partial string FinishedText { get; private set; } = "";

    public bool HasFinished => Finished.Count > 0;
    public bool ShowList => HasAny && IsExpanded;

    /// <summary>The finished cards while the fold is open, null while it is closed: closed, they get no views at all.</summary>
    public ObservableCollection<SubagentCardViewModel>? VisibleFinished => IsFoldOpen ? Finished : null;

    partial void OnIsFoldOpenChanged(bool value) => OnPropertyChanged(nameof(VisibleFinished));

    partial void OnHasAnyChanged(bool value) => OnPropertyChanged(nameof(ShowList));
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowList));

    [RelayCommand]
    private void ToggleExpanded()
    {
        _userChose = true;
        IsExpanded = !IsExpanded;
    }

    [RelayCommand] private void ToggleFold() => IsFoldOpen = !IsFoldOpen;

    /// <summary>Brings the cards in line with <paramref name="runs"/>, keeping the cards (and their views) that stay.</summary>
    public void Sync(IReadOnlyList<SubagentRun> runs, DateTimeOffset now)
    {
        var active = runs.Where(r => r.State is SubagentState.Running or SubagentState.Failed).ToList();
        var finished = runs.Where(r => r.State is SubagentState.Done or SubagentState.Interrupted).ToList();
        Reconcile(Active, active, now);
        Reconcile(Finished, finished, now);

        HasAny = runs.Count > 0;
        CountText = $"{finished.Count}/{runs.Count}";
        FinishedText = AppLangResources.Format(AppLangResourceKind.SubagentsFinished, finished.Count);
        OnPropertyChanged(nameof(HasFinished));

        // Opens when a subagent starts, closes when none runs any more; a choice of the user holds until the next start.
        var starts = runs.Count;
        var running = runs.Any(r => r.State == SubagentState.Running);
        if (starts > _knownStarts && running)
        {
            _userChose = false;
        }
        _knownStarts = starts;
        if (!_userChose)
        {
            IsExpanded = running || active.Count > 0;
        }
        if (!HasAny)
        {
            IsFoldOpen = false;
        }
    }

    /// <summary>Moves the elapsed times of running cards on.</summary>
    public void Tick(DateTimeOffset now)
    {
        foreach (var card in Active)
        {
            card.Tick(now);
        }
    }

    private void Reconcile(ObservableCollection<SubagentCardViewModel> cards, List<SubagentRun> runs, DateTimeOffset now)
    {
        for (var i = cards.Count - 1; i >= 0; i--)
        {
            if (!runs.Contains(cards[i].Run))
            {
                cards.RemoveAt(i);
            }
        }
        for (var i = 0; i < runs.Count; i++)
        {
            var at = IndexOf(cards, runs[i]);
            if (at < 0)
            {
                cards.Insert(i, new SubagentCardViewModel(runs[i], _activate));
            }
            else if (at != i)
            {
                cards.Move(at, i);
            }
            cards[i].Refresh(now);
        }
    }

    private static int IndexOf(ObservableCollection<SubagentCardViewModel> cards, SubagentRun run)
    {
        for (var i = 0; i < cards.Count; i++)
        {
            if (ReferenceEquals(cards[i].Run, run))
            {
                return i;
            }
        }
        return -1;
    }
}

/// <summary>One subagent: its type and task on the first line, what it is doing (or how it ended), its tool calls and time on the second.</summary>
public sealed partial class SubagentCardViewModel : ObservableObject
{
    private readonly Action _activate;

    public SubagentCardViewModel(SubagentRun run, Action activate)
    {
        Run = run;
        _activate = activate;
    }

    public SubagentRun Run { get; }
    public string Type => Run.Type;

    [ObservableProperty] public partial string Title { get; private set; } = "";
    [ObservableProperty] public partial string Detail { get; private set; } = "";
    [ObservableProperty] public partial string ToolsText { get; private set; } = "";
    [ObservableProperty] public partial string Elapsed { get; private set; } = "";
    [ObservableProperty] public partial string Tip { get; private set; } = "";
    [ObservableProperty] public partial bool IsRunning { get; private set; }
    [ObservableProperty] public partial bool IsDone { get; private set; }
    [ObservableProperty] public partial bool IsFailed { get; private set; }
    [ObservableProperty] public partial bool IsInterrupted { get; private set; }

    [RelayCommand] private void Activate() => _activate();

    public void Refresh(DateTimeOffset now)
    {
        Title = string.IsNullOrWhiteSpace(Run.Description) ? Run.Type : Run.Description!;
        IsRunning = Run.State == SubagentState.Running;
        IsDone = Run.State == SubagentState.Done;
        IsFailed = Run.State == SubagentState.Failed;
        IsInterrupted = Run.State == SubagentState.Interrupted;
        Detail = Run.State switch
        {
            SubagentState.Running => Run.LastAction is { } action ? "→ " + action : AppLangResources.Get(AppLangResourceKind.SubagentStarting),
            SubagentState.Done => AppLangResources.Get(AppLangResourceKind.SubagentDone),
            SubagentState.Failed => AppLangResources.Get(AppLangResourceKind.SubagentFailed),
            _ => AppLangResources.Get(AppLangResourceKind.SubagentInterrupted),
        };
        ToolsText = Run.ToolCount > 0 ? AppLangResources.Format(AppLangResourceKind.SubagentToolCount, Run.ToolCount) : "";
        Tip = Run.Description is { Length: > 0 } description ? $"{Run.Type} · {description}" : Run.Type;
        Tick(now);
    }

    public void Tick(DateTimeOffset now) => Elapsed = FormatElapsed((Run.EndedAt ?? now) - Run.StartedAt);

    /// <summary><c>42s</c>, <c>1m 12s</c>, <c>1h 05m</c>.</summary>
    public static string FormatElapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
            : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds:00}s"
            : $"{(int)span.TotalSeconds}s";
    }
}
