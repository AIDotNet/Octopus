using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Acp.Protocol;
using Octopus.Agents.Subagents;
using Octopus.App.Activity;
using Octopus.App.AgentGui;
using Octopus.App.Common;
using Octopus.App.Files;
using Octopus.App.Localization;
using Octopus.App.Sidebar;

namespace Octopus.App.Explorer.Subagent;

/// <summary>One selectable row in the compact single-line subagent switcher dropdown (Scheme A).</summary>
public sealed partial class SubagentSiblingItem : ObservableObject
{
    private readonly Action<SubagentSiblingItem> _onSelect;

    public SubagentSiblingItem(
        string id,
        string agentType,
        string title,
        string status,
        string metaText,
        bool isSelected,
        SubagentRunViewModel? liveRun,
        SubagentRun? hookRun,
        Action<SubagentSiblingItem> onSelect)
    {
        Id = id;
        AgentType = agentType;
        Title = title;
        Status = status;
        MetaText = metaText;
        IsSelected = isSelected;
        LiveRun = liveRun;
        HookRun = hookRun;
        _onSelect = onSelect;
    }

    public string Id { get; }
    public string AgentType { get; }
    public string Title { get; }
    public string Status { get; }
    public string MetaText { get; }
    public bool IsSelected { get; }
    public SubagentRunViewModel? LiveRun { get; }
    public SubagentRun? HookRun { get; }

    public bool IsRunning => Status == "running";
    public bool IsDone => Status == "done";
    public bool IsFailed => Status == "failed";
    public bool IsInterrupted => Status == "interrupted";

    [RelayCommand]
    private void Select() => _onSelect(this);
}

/// <summary>
/// Singleton right-panel subagent message inspector (Scheme A).
/// Reuses the exact same <see cref="AgentTimelineItem"/> models and <see cref="AgentTimelineView"/>
/// as the main Agent GUI, while guaranteeing O(1) visual tree instances, on-demand incremental transcript
/// tailing, and immediate memory release whenever the tab is switched away or the right panel closes.
/// </summary>
public sealed partial class SubagentPanelViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan TailInterval = TimeSpan.FromSeconds(1);

    private readonly ISubagentService? _subagents;
    private readonly Dictionary<string, string> _toolCallIdsByRawId = new(StringComparer.Ordinal);
    private readonly Queue<string> _pendingToolCallIds = new();

    private string? _parentItemId;
    private string? _subagentId;
    private string? _workingDirectory;
    private SubagentRunViewModel? _liveRun;
    private IReadOnlyList<SubagentRunViewModel>? _liveSiblings;
    private INotifyCollectionChanged? _liveSiblingsNotify;
    private SubagentRun? _hookRun;

    private bool _isActive;
    private bool _disposed;
    private int _sessionVersion;

    // Incremental transcript reading state (only alive while IsMounted is true).
    private string? _activeTranscriptPath;
    private long _transcriptOffset;
    private bool _transcriptHasEvents;
    private bool _tailing;
    private bool _tailAgain;
    private int _syntheticToolCounter;
    private string? _initialPromptText;
    private AssistantTurnItem? _activeTurn;
    private DispatcherTimer? _tailTimer;

    public SubagentPanelViewModel(ISubagentService? subagents = null)
    {
        _subagents = subagents;
        if (_subagents is not null)
        {
            _subagents.Changed += OnSubagentServiceChanged;
            _subagents.Tick += OnSubagentServiceTick;
        }
    }

    /// <summary>Single shared timeline collection rendered by <see cref="AgentTimelineView"/>; empty when unmounted.</summary>
    public ObservableCollection<AgentTimelineItem> Timeline { get; } = [];

    /// <summary>Sibling subagents of the current parent session, populated lazily for the compact switcher dropdown.</summary>
    public ObservableCollection<SubagentSiblingItem> Siblings { get; } = [];

    [ObservableProperty] public partial bool HasTarget { get; private set; }
    [ObservableProperty] public partial string DisplayTitle { get; private set; } = "";
    [ObservableProperty] public partial string AgentType { get; private set; } = "";
    [ObservableProperty] public partial string? Model { get; private set; }
    [ObservableProperty] public partial string? WorktreeIsolation { get; private set; }
    [ObservableProperty] public partial string Status { get; private set; } = "running";
    [ObservableProperty] public partial string ElapsedText { get; private set; } = "";
    [ObservableProperty] public partial string ToolsText { get; private set; } = "";
    [ObservableProperty] public partial string SiblingCountText { get; private set; } = "";
    [ObservableProperty] public partial bool HasMultipleSiblings { get; private set; }
    [ObservableProperty] public partial bool HasRunningSibling { get; private set; }
    [ObservableProperty] public partial bool IsSwitcherOpen { get; set; }
    [ObservableProperty] public partial bool IsPinnedToBottom { get; set; } = true;
    [ObservableProperty] public partial string? CopyableConclusion { get; private set; }

    public string? ParentItemId => _parentItemId;
    public string? SubagentId => _subagentId;
    public string? WorkingDirectory => _workingDirectory;

    /// <summary>True only when the right panel is open AND showing the Subagent tab with an active target.</summary>
    public bool IsMounted => _isActive && HasTarget;

    public bool HasTimeline => Timeline.Count > 0;
    public bool ShowEmptyHint => IsMounted && Timeline.Count == 0;
    public bool ShowJumpToBottom => HasTimeline && !IsPinnedToBottom;
    public bool HasModel => !string.IsNullOrWhiteSpace(Model);
    public bool HasWorktree => !string.IsNullOrWhiteSpace(WorktreeIsolation);
    public bool HasToolsText => !string.IsNullOrWhiteSpace(ToolsText);
    public bool HasCountBadge => HasTarget && SiblingCountText.Length > 0;
    public bool IsRunning => Status == "running";
    public bool IsDone => Status == "done";
    public bool IsFailed => Status == "failed";
    public bool IsInterrupted => Status == "interrupted";
    public bool CanStop => IsRunning && _liveRun is not null;
    public bool HasCopyableConclusion => !string.IsNullOrWhiteSpace(CopyableConclusion);

    public string StatusText => Status switch
    {
        "done" => AppLangResources.Get(AppLangResourceKind.SubagentDone),
        "failed" => AppLangResources.Get(AppLangResourceKind.SubagentFailed),
        "interrupted" => AppLangResources.Get(AppLangResourceKind.SubagentInterrupted),
        _ => AppLangResources.Get(AppLangResourceKind.SubagentStarting),
    };

    public string TabTooltip => HasTarget
        ? $"{AppLangResources.Get(AppLangResourceKind.ExplorerTabSubagent)} · {DisplayTitle}"
        : AppLangResources.Get(AppLangResourceKind.ExplorerTabSubagent);

    /// <summary>Raised when the user clicks a local file link or "Open file" inside the subagent's timeline.</summary>
    public event EventHandler<string>? FileOpenRequested;

    /// <summary>Raised when the user clicks "Open diff" inside a subagent's edit step.</summary>
    public event EventHandler<DiffTabRequest>? DiffRequested;

    /// <summary>Raised when the user explicitly closes the subagent tab.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when new timeline items arrive and the view should scroll to the bottom.</summary>
    public event EventHandler<bool>? ScrollToBottomRequested;

    /// <summary>
    /// Whether the subagent view is currently visible on screen (right panel open AND Subagent tab active).
    /// Leaving the tab or collapsing the right panel immediately stops tailing and releases the timeline
    /// so zero message visual nodes or transcript strings stay resident in memory.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            if (!value)
            {
                IsSwitcherOpen = false;
                ReleaseTimeline();
            }
            else if (HasTarget)
            {
                ActivateTimeline();
            }
            OnPropertyChanged(nameof(IsMounted));
            OnPropertyChanged(nameof(ShowEmptyHint));
        }
    }

    partial void OnIsPinnedToBottomChanged(bool value) => OnPropertyChanged(nameof(ShowJumpToBottom));

    /// <summary>Opens a subagent in the singleton inspector, replacing any previously displayed target.</summary>
    public void Open(OpenSubagentPanelRequested request)
    {
        if (_disposed) return;

        var sameTarget = string.Equals(_parentItemId, request.ParentItemId, StringComparison.Ordinal)
            && string.Equals(_subagentId, request.SubagentId, StringComparison.Ordinal)
            && ReferenceEquals(_liveRun, request.LiveRun);

        DetachLiveSubscriptions();

        _parentItemId = request.ParentItemId;
        _subagentId = request.SubagentId;
        _workingDirectory = request.WorkingDirectory;
        _liveRun = request.LiveRun;
        _liveSiblings = request.SiblingRuns;
        _hookRun = _liveRun is null ? FindHookRun(request.ParentItemId, request.SubagentId) : null;

        AttachLiveSubscriptions();

        HasTarget = _liveRun is not null || _hookRun is not null;
        IsSwitcherOpen = false;
        SyncMetadataAndSiblings();
        OnPropertyChanged(nameof(ParentItemId));
        OnPropertyChanged(nameof(SubagentId));
        OnPropertyChanged(nameof(WorkingDirectory));
        OnPropertyChanged(nameof(IsMounted));

        if (!HasTarget)
        {
            ReleaseTimeline();
            return;
        }

        if (_isActive && (!sameTarget || Timeline.Count == 0))
        {
            ActivateTimeline();
        }
        else if (_isActive)
        {
            TriggerIncrementalPoll();
        }
    }

    /// <summary>Opens a hook-tracked (terminal Claude Code) subagent by parent item id and subagent id.</summary>
    public bool TryOpenHookRun(string parentItemId, string subagentId, string? workingDirectory = null)
    {
        var run = FindHookRun(parentItemId, subagentId);
        if (run is null) return false;
        Open(new OpenSubagentPanelRequested(parentItemId, subagentId, workingDirectory));
        return true;
    }

    /// <summary>Clears the active target and releases all timeline data and timers.</summary>
    [RelayCommand]
    public void Close()
    {
        DetachLiveSubscriptions();
        _parentItemId = null;
        _subagentId = null;
        _workingDirectory = null;
        _liveRun = null;
        _liveSiblings = null;
        _hookRun = null;
        HasTarget = false;
        IsSwitcherOpen = false;
        Siblings.Clear();
        SiblingCountText = "";
        HasMultipleSiblings = false;
        HasRunningSibling = false;
        DisplayTitle = "";
        AgentType = "";
        Model = null;
        WorktreeIsolation = null;
        ElapsedText = "";
        ToolsText = "";
        CopyableConclusion = null;
        ReleaseTimeline();
        NotifyMetadataDerived();
        OnPropertyChanged(nameof(IsMounted));
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ToggleSwitcher()
    {
        if (Siblings.Count == 0) SyncMetadataAndSiblings();
        IsSwitcherOpen = !IsSwitcherOpen;
    }

    [RelayCommand]
    private void Stop()
    {
        if (CanStop)
        {
            _liveRun!.StopCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void JumpToBottom()
    {
        IsPinnedToBottom = true;
        ScrollToBottomRequested?.Invoke(this, true);
    }

    /// <summary>Forwards a clicked local Markdown link in the subagent timeline to the right panel host.</summary>
    public void RequestOpenFile(string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) FileOpenRequested?.Invoke(this, path);
    }

    /// <summary>Synchronously runs one transcript poll cycle on the calling thread. For tests.</summary>
    internal void PollTranscriptNow()
    {
        if (!IsMounted) return;
        var path = ResolveTranscriptPath();
        if (string.IsNullOrEmpty(path)) return;
        if (!string.Equals(_activeTranscriptPath, path, StringComparison.Ordinal))
        {
            _activeTranscriptPath = path;
            _transcriptOffset = 0;
        }
        var delta = SubagentTimelineTranscript.ReadDelta(path, _transcriptOffset);
        ApplyTranscriptDelta(delta, _sessionVersion);
    }

    // ---- Timeline Lifecycle (Lazy Mount + Off-Screen Release) -----------------------------------

    private void ActivateTimeline()
    {
        ReleaseTimeline();
        if (!HasTarget || !_isActive) return;

        var version = ++_sessionVersion;
        IsPinnedToBottom = true;

        var path = ResolveTranscriptPath();
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            _activeTranscriptPath = path;
            _transcriptOffset = 0;
            SeedPromptIfKnown();
            TriggerIncrementalPoll(version);
        }
        else
        {
            // No local transcript yet (remote ACP or just spawned): render immediately from live snapshot
            // using the exact same main-Agent timeline items, and upgrade seamlessly if a transcript appears.
            _activeTranscriptPath = path;
            _transcriptOffset = 0;
            RebuildFromFallbackSnapshot();
            if (!string.IsNullOrEmpty(path))
            {
                TriggerIncrementalPoll(version);
            }
        }

        EnsureTailTimer();
        OnPropertyChanged(nameof(HasTimeline));
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(ShowJumpToBottom));
        ScrollToBottomRequested?.Invoke(this, true);
    }

    /// <summary>Immediately clears the timeline visual data and stops all background tailing.</summary>
    public void ReleaseTimeline()
    {
        _sessionVersion++;
        StopTailTimer();
        _tailing = false;
        _tailAgain = false;
        _activeTranscriptPath = null;
        _transcriptOffset = 0;
        _transcriptHasEvents = false;
        _syntheticToolCounter = 0;
        _initialPromptText = null;
        _toolCallIdsByRawId.Clear();
        _pendingToolCallIds.Clear();

        foreach (var item in Timeline)
        {
            if (item is AssistantTurnItem turn) turn.Dispose();
        }
        Timeline.Clear();
        _activeTurn = null;

        OnPropertyChanged(nameof(HasTimeline));
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(ShowJumpToBottom));
    }

    private void SeedPromptIfKnown()
    {
        var prompt = _liveRun?.Prompt?.Trim();
        if (string.IsNullOrWhiteSpace(prompt)) return;
        _initialPromptText = prompt;
        Timeline.Add(new UserTurnItem(prompt));
    }

    private void RebuildFromFallbackSnapshot()
    {
        if (_transcriptHasEvents) return;

        foreach (var item in Timeline)
        {
            if (item is AssistantTurnItem turn) turn.Dispose();
        }
        Timeline.Clear();
        _activeTurn = null;
        _toolCallIdsByRawId.Clear();
        _pendingToolCallIds.Clear();

        if (_liveRun is not null)
        {
            var prompt = !string.IsNullOrWhiteSpace(_liveRun.Prompt)
                ? _liveRun.Prompt!.Trim()
                : !string.IsNullOrWhiteSpace(_liveRun.Description)
                    ? _liveRun.Description.Trim()
                    : null;
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                _initialPromptText = prompt;
                Timeline.Add(new UserTurnItem(prompt));
            }

            if (_liveRun.Steps.Count > 0 || _liveRun.HasSummary || _liveRun.IsRunning)
            {
                var turn = CreateAssistantTurn();
                foreach (var step in _liveRun.Steps)
                {
                    turn.UpsertToolCall(new AcpToolCallUpdate
                    {
                        ToolCallId = step.ToolCallId,
                        Name = step.ToolName,
                        Kind = step.Kind,
                        Title = string.IsNullOrWhiteSpace(step.Target) ? step.ToolName : step.Target,
                        Description = step.Target,
                        Status = step.Status,
                    });
                }

                if (_liveRun.HasSummary)
                {
                    turn.AppendMessage(_liveRun.Summary!);
                }

                if (!_liveRun.IsRunning)
                {
                    turn.Complete(error: _liveRun.IsFailed ? _liveRun.Summary : null);
                }
            }
        }
        else if (_hookRun is not null)
        {
            if (!string.IsNullOrWhiteSpace(_hookRun.Description))
            {
                _initialPromptText = _hookRun.Description.Trim();
                Timeline.Add(new UserTurnItem(_initialPromptText));
            }

            if (!string.IsNullOrWhiteSpace(_hookRun.LastAction) || _hookRun.State == SubagentState.Running)
            {
                var turn = CreateAssistantTurn();
                if (!string.IsNullOrWhiteSpace(_hookRun.LastAction))
                {
                    turn.UpsertToolCall(new AcpToolCallUpdate
                    {
                        ToolCallId = "hook-latest",
                        Name = _hookRun.Type,
                        Kind = "other",
                        Title = _hookRun.LastAction,
                        Status = _hookRun.State == SubagentState.Running ? "in_progress"
                            : _hookRun.State == SubagentState.Failed ? "failed"
                            : "completed",
                    });
                }
                if (_hookRun.IsFinished)
                {
                    turn.Complete();
                }
            }
        }

        OnPropertyChanged(nameof(HasTimeline));
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(ShowJumpToBottom));
    }

    // ---- Incremental Transcript Tailing ---------------------------------------------------------

    private void EnsureTailTimer()
    {
        if (Application.Current is null || !Dispatcher.UIThread.CheckAccess()) return;
        var need = IsMounted && IsRunning;
        if (need && _tailTimer is null)
        {
            _tailTimer = new DispatcherTimer { Interval = TailInterval };
            _tailTimer.Tick += OnTailTick;
            _tailTimer.Start();
        }
        else if (!need && _tailTimer is not null)
        {
            StopTailTimer();
        }
    }

    private void StopTailTimer()
    {
        if (_tailTimer is null) return;
        _tailTimer.Stop();
        _tailTimer.Tick -= OnTailTick;
        _tailTimer = null;
    }

    private void OnTailTick(object? sender, EventArgs e)
    {
        if (_disposed || !IsMounted)
        {
            StopTailTimer();
            return;
        }
        SyncMetadataAndSiblings();
        _activeTurn?.TickElapsed();
        TriggerIncrementalPoll();
        if (!IsRunning)
        {
            StopTailTimer();
        }
    }

    private void TriggerIncrementalPoll(int? expectedVersion = null)
    {
        if (_disposed || !IsMounted) return;
        var path = ResolveTranscriptPath();
        if (string.IsNullOrEmpty(path)) return;

        if (_tailing)
        {
            _tailAgain = true;
            return;
        }

        var version = expectedVersion ?? _sessionVersion;
        if (!string.Equals(_activeTranscriptPath, path, StringComparison.Ordinal))
        {
            _activeTranscriptPath = path;
            _transcriptOffset = 0;
        }

        var offset = _transcriptOffset;
        _tailing = true;
        _ = Task.Run(() => SubagentTimelineTranscript.ReadDelta(path, offset))
            .ContinueWith(task => UiContext.Run(() =>
            {
                _tailing = false;
                if (!_disposed && version == _sessionVersion && task.IsCompletedSuccessfully)
                {
                    ApplyTranscriptDelta(task.Result, version);
                }
                if (_tailAgain && !_disposed && IsMounted)
                {
                    _tailAgain = false;
                    TriggerIncrementalPoll();
                }
            }), TaskScheduler.Default);
    }

    private void ApplyTranscriptDelta(SubagentTimelineDelta delta, int version)
    {
        if (_disposed || version != _sessionVersion || !IsMounted) return;
        if (delta.ToOffset == _transcriptOffset && delta.Events.Count == 0)
        {
            FinalizeTurnIfSubagentEnded();
            return;
        }

        _transcriptOffset = delta.ToOffset;
        if (delta.Events.Count == 0)
        {
            FinalizeTurnIfSubagentEnded();
            return;
        }

        // First real transcript events arrived: clear any fallback snapshot so there is zero duplication.
        if (delta.Reset || !_transcriptHasEvents)
        {
            foreach (var item in Timeline)
            {
                if (item is AssistantTurnItem oldTurn) oldTurn.Dispose();
            }
            Timeline.Clear();
            _activeTurn = null;
            _toolCallIdsByRawId.Clear();
            _pendingToolCallIds.Clear();
            _syntheticToolCounter = 0;
            _transcriptHasEvents = true;

            // Keep the known initial prompt if the transcript didn't start with a UserPrompt event.
            if (delta.Events[0].Kind != SubagentTranscriptEventKind.UserPrompt && !string.IsNullOrWhiteSpace(_initialPromptText))
            {
                Timeline.Add(new UserTurnItem(_initialPromptText!));
            }
        }

        foreach (var ev in delta.Events)
        {
            ApplyTranscriptEvent(ev);
        }

        FinalizeTurnIfSubagentEnded();
        UpdateConclusionFromTimeline();

        OnPropertyChanged(nameof(HasTimeline));
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(ShowJumpToBottom));
        if (IsPinnedToBottom)
        {
            ScrollToBottomRequested?.Invoke(this, false);
        }
    }

    private void ApplyTranscriptEvent(SubagentTranscriptEvent ev)
    {
        switch (ev.Kind)
        {
            case SubagentTranscriptEventKind.UserPrompt:
                if (string.IsNullOrWhiteSpace(ev.Text)) return;
                if (_activeTurn is { IsStreaming: true })
                {
                    _activeTurn.Complete();
                    _activeTurn = null;
                }
                Timeline.Add(new UserTurnItem(ev.Text!));
                break;

            case SubagentTranscriptEventKind.Thought:
                if (string.IsNullOrWhiteSpace(ev.Text)) return;
                EnsureActiveTurn().AppendThought(ev.Text!);
                break;

            case SubagentTranscriptEventKind.ToolCall:
            {
                var turn = EnsureActiveTurn();
                var rawId = ev.ToolCallId;
                var id = !string.IsNullOrWhiteSpace(rawId)
                    ? rawId!
                    : $"sub-tc-{++_syntheticToolCounter}";
                if (!string.IsNullOrWhiteSpace(rawId))
                {
                    _toolCallIdsByRawId[rawId!] = id;
                }
                _pendingToolCallIds.Enqueue(id);

                var update = new AcpToolCallUpdate
                {
                    ToolCallId = id,
                    Name = ev.ToolName,
                    Kind = ev.ToolKind ?? "other",
                    Title = ev.ToolTarget ?? ev.ToolName ?? "",
                    Description = ev.ToolTarget,
                    Command = ev.Command,
                    Status = "in_progress",
                };
                if (!string.IsNullOrWhiteSpace(ev.FilePath))
                {
                    update.Locations.Add(new AcpToolLocation { Path = ev.FilePath! });
                }
                if (ev.DiffOld is not null || ev.DiffNew is not null)
                {
                    update.Diffs.Add(new AcpToolDiff
                    {
                        Path = ev.FilePath ?? ev.ToolTarget ?? "",
                        OldText = ev.DiffOld,
                        NewText = ev.DiffNew ?? "",
                    });
                }
                turn.UpsertToolCall(update);
                break;
            }

            case SubagentTranscriptEventKind.ToolResult:
            {
                var turn = EnsureActiveTurn();
                string? targetId = null;
                if (!string.IsNullOrWhiteSpace(ev.ToolCallId)
                    && _toolCallIdsByRawId.TryGetValue(ev.ToolCallId!, out var mapped))
                {
                    targetId = mapped;
                }
                else if (_pendingToolCallIds.Count > 0)
                {
                    targetId = _pendingToolCallIds.Dequeue();
                }

                if (targetId is null) return;
                var update = new AcpToolCallUpdate
                {
                    ToolCallId = targetId,
                    Status = ev.IsError ? "failed" : "completed",
                };
                if (!string.IsNullOrWhiteSpace(ev.Output))
                {
                    if (ev.IsError) update.Stderr = ev.Output;
                    else update.TextOutputs.Add(ev.Output!);
                }
                turn.UpsertToolCall(update);
                break;
            }

            case SubagentTranscriptEventKind.AssistantText:
                if (string.IsNullOrWhiteSpace(ev.Text)) return;
                EnsureActiveTurn().AppendMessage(ev.Text!);
                break;
        }
    }

    private void FinalizeTurnIfSubagentEnded()
    {
        if (IsRunning || _activeTurn is null || !_activeTurn.IsStreaming) return;
        foreach (var tc in _activeTurn.ToolCalls)
        {
            if (tc.IsRunning)
            {
                _activeTurn.UpsertToolCall(new AcpToolCallUpdate
                {
                    ToolCallId = tc.ToolCallId,
                    Status = IsFailed ? "failed" : "completed",
                });
            }
        }
        _activeTurn.Complete(error: IsFailed ? _liveRun?.Summary : null);
        _activeTurn = null;
    }

    private AssistantTurnItem EnsureActiveTurn()
    {
        if (_activeTurn is not null) return _activeTurn;
        return CreateAssistantTurn();
    }

    private AssistantTurnItem CreateAssistantTurn()
    {
        var turn = new AssistantTurnItem(
            agentId: AgentType,
            agentName: DisplayTitle,
            openFile: path => FileOpenRequested?.Invoke(this, path),
            openDiff: path => OpenToolDiff(path),
            openScript: null,
            workingDirectory: _workingDirectory);
        _activeTurn = turn;
        Timeline.Add(turn);
        return turn;
    }

    private void OpenToolDiff(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var root = _workingDirectory ?? Path.GetDirectoryName(path) ?? "";
        var full = Path.IsPathRooted(path) ? path : Path.Combine(root, path);
        var rel = path;
        if (!string.IsNullOrWhiteSpace(root) && Path.IsPathRooted(full))
        {
            try { rel = Path.GetRelativePath(root, full); } catch { }
        }
        DiffRequested?.Invoke(this, new DiffTabRequest(root, rel, full, Staged: false));
    }

    private void UpdateConclusionFromTimeline()
    {
        for (var i = Timeline.Count - 1; i >= 0; i--)
        {
            if (Timeline[i] is AssistantTurnItem turn && !string.IsNullOrWhiteSpace(turn.CopyReplyValue))
            {
                CopyableConclusion = turn.CopyReplyValue;
                OnPropertyChanged(nameof(HasCopyableConclusion));
                return;
            }
        }
        if (_liveRun?.HasSummary == true)
        {
            CopyableConclusion = _liveRun.Summary;
            OnPropertyChanged(nameof(HasCopyableConclusion));
        }
    }

    // ---- Subscriptions & Metadata Sync ----------------------------------------------------------

    private void AttachLiveSubscriptions()
    {
        if (_liveRun is not null)
        {
            _liveRun.PropertyChanged += OnLiveRunPropertyChanged;
            _liveRun.Steps.CollectionChanged += OnLiveRunStepsChanged;
        }
        if (_liveSiblings is INotifyCollectionChanged notify)
        {
            _liveSiblingsNotify = notify;
            _liveSiblingsNotify.CollectionChanged += OnLiveSiblingsChanged;
        }
    }

    private void DetachLiveSubscriptions()
    {
        if (_liveRun is not null)
        {
            _liveRun.PropertyChanged -= OnLiveRunPropertyChanged;
            _liveRun.Steps.CollectionChanged -= OnLiveRunStepsChanged;
        }
        _liveSiblingsNotify?.CollectionChanged -= OnLiveSiblingsChanged;
        _liveSiblingsNotify = null;
    }

    private void OnLiveRunPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        SyncMetadataAndSiblings();
        if (!IsMounted) return;

        if (!_transcriptHasEvents)
        {
            // Check if a transcript path was just discovered (e.g. Antigravity async spawn bound)
            var path = ResolveTranscriptPath();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                TriggerIncrementalPoll();
            }
            else if (e.PropertyName is nameof(SubagentRunViewModel.Status) or nameof(SubagentRunViewModel.Summary) or nameof(SubagentRunViewModel.Prompt))
            {
                RebuildFromFallbackSnapshot();
            }
        }
        else if (e.PropertyName == nameof(SubagentRunViewModel.Status) && !IsRunning)
        {
            // One final read when a running subagent completes so the last response is never missed.
            TriggerIncrementalPoll();
        }
        EnsureTailTimer();
    }

    private void OnLiveRunStepsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed || !IsMounted) return;
        SyncMetadataAndSiblings();
        if (!_transcriptHasEvents)
        {
            RebuildFromFallbackSnapshot();
        }
        else
        {
            TriggerIncrementalPoll();
        }
    }

    private void OnLiveSiblingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;
        SyncMetadataAndSiblings();
    }

    private void OnSubagentServiceChanged(object? sender, string itemId)
    {
        if (_disposed || !string.Equals(itemId, _parentItemId, StringComparison.Ordinal)) return;
        if (_liveRun is null && _subagentId is not null)
        {
            _hookRun = FindHookRun(itemId, _subagentId);
        }
        SyncMetadataAndSiblings();
        if (IsMounted)
        {
            TriggerIncrementalPoll();
            EnsureTailTimer();
        }
    }

    private void OnSubagentServiceTick(object? sender, EventArgs e)
    {
        if (_disposed || !HasTarget || _liveRun is not null) return;
        SyncMetadataAndSiblings();
    }

    private void SyncMetadataAndSiblings()
    {
        if (_liveRun is not null)
        {
            DisplayTitle = _liveRun.DisplayTitle;
            AgentType = _liveRun.AgentType;
            Model = _liveRun.Model;
            WorktreeIsolation = _liveRun.WorktreeIsolation;
            Status = _liveRun.Status;
            ElapsedText = _liveRun.ElapsedText;
            ToolsText = _liveRun.ToolCountText;
            if (_liveRun.HasSummary && string.IsNullOrWhiteSpace(CopyableConclusion))
            {
                CopyableConclusion = _liveRun.Summary;
            }
        }
        else if (_hookRun is not null)
        {
            DisplayTitle = string.IsNullOrWhiteSpace(_hookRun.Description) ? _hookRun.Type : _hookRun.Description!;
            AgentType = _hookRun.Type;
            Model = null;
            WorktreeIsolation = null;
            Status = _hookRun.State switch
            {
                SubagentState.Done => "done",
                SubagentState.Failed => "failed",
                SubagentState.Interrupted => "interrupted",
                _ => "running",
            };
            ElapsedText = SubagentCardViewModel.FormatElapsed((_hookRun.EndedAt ?? DateTimeOffset.Now) - _hookRun.StartedAt);
            ToolsText = _hookRun.ToolCount > 0 ? AppLangResources.Format(AppLangResourceKind.SubagentToolCount, _hookRun.ToolCount) : "";
        }

        RebuildSiblings();
        NotifyMetadataDerived();
    }

    private void RebuildSiblings()
    {
        Siblings.Clear();
        var runningAny = false;

        if (_liveSiblings is { Count: > 0 })
        {
            foreach (var run in _liveSiblings)
            {
                if (run.IsRunning) runningAny = true;
                Siblings.Add(new SubagentSiblingItem(
                    run.Id,
                    run.AgentType,
                    run.DisplayTitle,
                    run.Status,
                    run.MetaText,
                    string.Equals(run.Id, _subagentId, StringComparison.Ordinal),
                    run,
                    hookRun: null,
                    OnSelectSibling));
            }
        }
        else if (_subagents is not null && !string.IsNullOrEmpty(_parentItemId))
        {
            var runs = _subagents.Of(_parentItemId);
            var now = DateTimeOffset.Now;
            foreach (var run in runs)
            {
                if (run.State == SubagentState.Running) runningAny = true;
                var status = run.State switch
                {
                    SubagentState.Done => "done",
                    SubagentState.Failed => "failed",
                    SubagentState.Interrupted => "interrupted",
                    _ => "running",
                };
                var elapsed = SubagentCardViewModel.FormatElapsed((run.EndedAt ?? now) - run.StartedAt);
                var tools = run.ToolCount > 0 ? AppLangResources.Format(AppLangResourceKind.SubagentToolCount, run.ToolCount) : "";
                var meta = tools.Length > 0 ? $"{tools} · {elapsed}" : elapsed;
                Siblings.Add(new SubagentSiblingItem(
                    run.Id,
                    run.Type,
                    string.IsNullOrWhiteSpace(run.Description) ? run.Type : run.Description!,
                    status,
                    meta,
                    string.Equals(run.Id, _subagentId, StringComparison.Ordinal),
                    liveRun: null,
                    run,
                    OnSelectSibling));
            }
        }

        HasRunningSibling = runningAny || IsRunning;
        HasMultipleSiblings = Siblings.Count > 1;
        SiblingCountText = Siblings.Count > 0 ? Siblings.Count.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
    }

    private void OnSelectSibling(SubagentSiblingItem item)
    {
        IsSwitcherOpen = false;
        if (string.Equals(item.Id, _subagentId, StringComparison.Ordinal)) return;
        Open(new OpenSubagentPanelRequested(
            _parentItemId ?? "",
            item.Id,
            _workingDirectory,
            item.LiveRun,
            _liveSiblings));
    }

    private SubagentRun? FindHookRun(string parentItemId, string subagentId) =>
        _subagents?.Of(parentItemId).FirstOrDefault(r => string.Equals(r.Id, subagentId, StringComparison.Ordinal));

    private string? ResolveTranscriptPath() =>
        !string.IsNullOrWhiteSpace(_liveRun?.TranscriptPath) ? _liveRun!.TranscriptPath
        : !string.IsNullOrWhiteSpace(_hookRun?.TranscriptPath) ? _hookRun!.TranscriptPath
        : null;

    private void NotifyMetadataDerived()
    {
        OnPropertyChanged(nameof(HasModel));
        OnPropertyChanged(nameof(HasWorktree));
        OnPropertyChanged(nameof(HasToolsText));
        OnPropertyChanged(nameof(HasCountBadge));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsInterrupted));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(HasCopyableConclusion));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(TabTooltip));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachLiveSubscriptions();
        if (_subagents is not null)
        {
            _subagents.Changed -= OnSubagentServiceChanged;
            _subagents.Tick -= OnSubagentServiceTick;
        }
        ReleaseTimeline();
    }
}
