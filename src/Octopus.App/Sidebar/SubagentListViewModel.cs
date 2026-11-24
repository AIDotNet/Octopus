using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents.Subagents;
using Octopus.App.Localization;

namespace Octopus.App.Sidebar;

/// <summary>Broadcast when a user clicks a specific subagent card in the sidebar so an open ACP GUI tab can focus it.</summary>
public sealed record FocusSubagentRequested(string ItemId, string SubagentId);

/// <summary>
/// The subagents under an agent's row: collapsed by default as a compact icon pill on the parent row
/// (one bot icon per subagent, up to <see cref="MaxPillIcons"/> plus an ellipsis when more exist),
/// and expanded on click to reveal active cards plus a folded "N finished" group. Kept per item across panel rebuilds.
/// </summary>
public sealed partial class SubagentListViewModel : ObservableObject
{
    public const int MaxPillIcons = 4;

    private readonly Action _activate;
    private readonly Action<string>? _focusSubagent;

    public SubagentListViewModel(Action activate, Action<string>? focusSubagent = null)
    {
        _activate = activate;
        _focusSubagent = focusSubagent;
    }

    /// <summary>Running and failed ones, oldest first.</summary>
    public ObservableCollection<SubagentCardViewModel> Active { get; } = [];

    /// <summary>Done and interrupted ones, behind the fold.</summary>
    public ObservableCollection<SubagentCardViewModel> Finished { get; } = [];

    /// <summary>Up to <see cref="MaxPillIcons"/> subagents shown as icons in the collapsed pill (active first, then finished).</summary>
    public ObservableCollection<SubagentCardViewModel> PillIcons { get; } = [];

    [ObservableProperty] public partial bool HasAny { get; private set; }
    [ObservableProperty] public partial bool HasPillOverflow { get; private set; }
    [ObservableProperty] public partial bool IsExpanded { get; private set; }
    [ObservableProperty] public partial bool IsFoldOpen { get; private set; }
    /// <summary>"1|3": finished of all.</summary>
    [ObservableProperty] public partial string CountText { get; private set; } = "";
    [ObservableProperty] public partial string FinishedCountText { get; private set; } = "0";
    [ObservableProperty] public partial string TotalCountText { get; private set; } = "0";
    [ObservableProperty] public partial string ToggleTip { get; private set; } = "";
    [ObservableProperty] public partial bool HasRunning { get; private set; }
    [ObservableProperty] public partial bool HasFailed { get; private set; }
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
        HasPillOverflow = runs.Count > MaxPillIcons;
        SyncPillIcons();
        FinishedCountText = finished.Count.ToString();
        TotalCountText = runs.Count.ToString();
        CountText = $"{finished.Count}|{runs.Count}";
        ToggleTip = HasAny
            ? $"{AppLangResources.Get(AppLangResourceKind.SubagentsToggle)} · {finished.Count}/{runs.Count}"
            : AppLangResources.Get(AppLangResourceKind.SubagentsToggle);
        HasRunning = runs.Any(r => r.State == SubagentState.Running);
        HasFailed = runs.Any(r => r.State == SubagentState.Failed);
        FinishedText = AppLangResources.Format(AppLangResourceKind.SubagentsFinished, finished.Count);
        OnPropertyChanged(nameof(HasFinished));

        // Collapsed by default: the parent row displays the compact icon pill and only expands on click.
        if (!HasAny)
        {
            IsExpanded = false;
            IsFoldOpen = false;
        }
    }

    private void SyncPillIcons()
    {
        var desired = new List<SubagentCardViewModel>(MaxPillIcons);
        foreach (var card in Active)
        {
            if (desired.Count >= MaxPillIcons) break;
            desired.Add(card);
        }
        foreach (var card in Finished)
        {
            if (desired.Count >= MaxPillIcons) break;
            desired.Add(card);
        }

        for (var i = PillIcons.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(PillIcons[i]))
            {
                PillIcons.RemoveAt(i);
            }
        }
        for (var i = 0; i < desired.Count; i++)
        {
            var at = PillIcons.IndexOf(desired[i]);
            if (at < 0)
            {
                PillIcons.Insert(i, desired[i]);
            }
            else if (at != i)
            {
                PillIcons.Move(at, i);
            }
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
            if (runs.All(r => r.Id != cards[i].Run.Id))
            {
                cards.RemoveAt(i);
            }
        }
        for (var i = 0; i < runs.Count; i++)
        {
            var at = IndexOf(cards, runs[i]);
            if (at < 0)
            {
                cards.Insert(i, new SubagentCardViewModel(runs[i], _activate, _focusSubagent));
            }
            else
            {
                if (at != i) cards.Move(at, i);
                cards[i].UpdateRun(runs[i]);
            }
            cards[i].Refresh(now);
        }
    }

    private static int IndexOf(ObservableCollection<SubagentCardViewModel> cards, SubagentRun run)
    {
        for (var i = 0; i < cards.Count; i++)
        {
            if (ReferenceEquals(cards[i].Run, run) || string.Equals(cards[i].Run.Id, run.Id, StringComparison.Ordinal))
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
    private readonly Action<string>? _focusSubagent;

    public SubagentCardViewModel(SubagentRun run, Action activate, Action<string>? focusSubagent = null)
    {
        Run = run;
        _activate = activate;
        _focusSubagent = focusSubagent;
    }

    public SubagentRun Run { get; private set; }
    public string Type => Run.Type;

    internal void UpdateRun(SubagentRun run)
    {
        if (ReferenceEquals(Run, run)) return;
        var typeChanged = !string.Equals(Run.Type, run.Type, StringComparison.Ordinal);
        Run = run;
        if (typeChanged) OnPropertyChanged(nameof(Type));
    }

    [ObservableProperty] public partial string Title { get; private set; } = "";
    [ObservableProperty] public partial string Detail { get; private set; } = "";
    [ObservableProperty] public partial string ToolsText { get; private set; } = "";
    [ObservableProperty] public partial string Elapsed { get; private set; } = "";
    [ObservableProperty] public partial string Tip { get; private set; } = "";
    [ObservableProperty] public partial bool IsRunning { get; private set; }
    [ObservableProperty] public partial bool IsDone { get; private set; }
    [ObservableProperty] public partial bool IsFailed { get; private set; }
    [ObservableProperty] public partial bool IsInterrupted { get; private set; }

    [RelayCommand]
    private void Activate()
    {
        _activate();
        _focusSubagent?.Invoke(Run.Id);
    }

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
