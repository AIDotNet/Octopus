using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Acp.Protocol;
using Octopus.Agents.Subagents;
using Octopus.App.Localization;
using Octopus.App.Sidebar;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.AgentGui;

/// <summary>High-level workflow lifecycle stage.</summary>
public enum WorkflowPhase
{
    Idle,
    Research,
    PlanGate,
    Executing,
    Verifying,
    Completed,
}

/// <summary>One numbered line inside the Style 2 full-script viewer modal.</summary>
public sealed record ScriptLineViewModel(string LineNumber, string Text);

/// <summary>Modal state for viewing a collapsed multi-line Bash script in full with line numbers (Bash Style 2).</summary>
public sealed partial class ScriptPreviewViewModel : ObservableObject
{
    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial string Title { get; private set; } = "";
    [ObservableProperty] public partial string Language { get; private set; } = "";
    [ObservableProperty] public partial string LineCountText { get; private set; } = "";
    [ObservableProperty] public partial string FullScript { get; private set; } = "";

    public bool HasLanguage => !string.IsNullOrWhiteSpace(Language);
    public ObservableCollection<ScriptLineViewModel> Lines { get; } = [];

    public void Open(string title, string? language, string fullScript)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "Bash" : title;
        Language = language ?? "";
        FullScript = fullScript;
        Lines.Clear();
        var rawLines = fullScript.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < rawLines.Length; i++)
        {
            Lines.Add(new ScriptLineViewModel((i + 1).ToString(), rawLines[i]));
        }
        LineCountText = AppLangResources.Format(AppLangResourceKind.AgentGuiBashScriptModalTitle, rawLines.Length);
        OnPropertyChanged(nameof(HasLanguage));
        IsOpen = true;
    }

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        Lines.Clear();
        FullScript = "";
        Title = "";
        Language = "";
        LineCountText = "";
        OnPropertyChanged(nameof(HasLanguage));
    }
}

/// <summary>One task entry in Claude Code's workflow DAG (<c>TodoWrite</c>, <c>TaskCreate</c>, <c>TaskUpdate</c>, or ACP <c>plan</c>).</summary>
public sealed partial class WorkflowTaskViewModel : ObservableObject
{
    private readonly Action<string>? _focusSubagent;

    public WorkflowTaskViewModel(string id, string title, Action<string>? focusSubagent = null)
    {
        Id = id;
        Title = title;
        _focusSubagent = focusSubagent;
    }

    public string Id { get; }
    public string DisplayId => Id.StartsWith('#') ? Id : $"#{Id}";

    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string? ActiveForm { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "pending";
    [ObservableProperty] public partial string? Owner { get; set; }
    [ObservableProperty] public partial string? LinkedSubagentId { get; set; }
    [ObservableProperty] public partial string BlockedByText { get; set; } = "";

    public bool IsCompleted => Status == "completed";
    public bool IsInProgress => Status == "in_progress";
    public bool IsFailed => Status == "failed";
    public bool IsPending => !IsCompleted && !IsInProgress && !IsFailed;
    public bool HasOwner => !string.IsNullOrWhiteSpace(Owner);
    public bool HasLinkedSubagent => !string.IsNullOrWhiteSpace(LinkedSubagentId);
    public bool HasBlockedBy => !string.IsNullOrWhiteSpace(BlockedByText);
    public bool HasActiveForm => IsInProgress
        && !string.IsNullOrWhiteSpace(ActiveForm)
        && !string.Equals(ActiveForm.Trim(), Title.Trim(), StringComparison.Ordinal);

    public LucideIconKind Icon => Status switch
    {
        "completed" => LucideIconKind.CircleCheck,
        "in_progress" => LucideIconKind.Loader,
        "failed" => LucideIconKind.CircleAlert,
        _ => LucideIconKind.Circle,
    };

    [RelayCommand]
    private void FocusOwner()
    {
        if (!string.IsNullOrWhiteSpace(LinkedSubagentId))
        {
            _focusSubagent?.Invoke(LinkedSubagentId!);
        }
    }

    public void Apply(AcpPlanEntry entry, string? linkedSubagentId = null)
    {
        if (!string.IsNullOrWhiteSpace(entry.Content)) Title = entry.Content;
        if (!string.IsNullOrWhiteSpace(entry.ActiveForm)) ActiveForm = entry.ActiveForm;
        if (!string.IsNullOrWhiteSpace(entry.Status)) Status = entry.Status;
        if (!string.IsNullOrWhiteSpace(entry.Owner)) Owner = entry.Owner;
        if (!string.IsNullOrWhiteSpace(linkedSubagentId)) LinkedSubagentId = linkedSubagentId;
        if (entry.BlockedBy.Count > 0)
        {
            var refs = string.Join(", ", entry.BlockedBy.Select(b => b.StartsWith('#') ? b : $"#{b}"));
            BlockedByText = AppLangResources.Format(AppLangResourceKind.AgentGuiTaskBlockedBy, refs);
        }

        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsInProgress));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(HasOwner));
        OnPropertyChanged(nameof(HasLinkedSubagent));
        OnPropertyChanged(nameof(HasBlockedBy));
        OnPropertyChanged(nameof(HasActiveForm));
        OnPropertyChanged(nameof(Icon));
    }
}

/// <summary>One nested tool invocation executed inside a spawned subagent.</summary>
public sealed partial class SubagentStepViewModel : ObservableObject
{
    public SubagentStepViewModel(string toolCallId)
    {
        ToolCallId = toolCallId;
        StartedAt = DateTimeOffset.Now;
    }

    public string ToolCallId { get; }
    public DateTimeOffset StartedAt { get; }

    [ObservableProperty] public partial string ToolName { get; set; } = "";
    [ObservableProperty] public partial string Target { get; set; } = "";
    [ObservableProperty] public partial string Kind { get; set; } = "other";
    [ObservableProperty] public partial string Status { get; set; } = "in_progress";
    [ObservableProperty] public partial string DurationText { get; set; } = "";

    public bool IsRunning => Status is "pending" or "in_progress";
    public bool IsCompleted => Status == "completed";
    public bool IsFailed => Status == "failed";

    public LucideIconKind KindIcon => Kind switch
    {
        "read" => LucideIconKind.FileText,
        "edit" => LucideIconKind.FileDiff,
        "delete" => LucideIconKind.Trash2,
        "move" => LucideIconKind.FolderSync,
        "search" => LucideIconKind.Search,
        "execute" => LucideIconKind.SquareTerminal,
        "fetch" => LucideIconKind.Globe,
        "think" => LucideIconKind.Brain,
        _ => LucideIconKind.Wrench,
    };

    public string ActionSummary => string.IsNullOrWhiteSpace(Target) ? ToolName : $"{ToolName} {Target}";

    public void Apply(AcpToolCallUpdate update)
    {
        if (!string.IsNullOrWhiteSpace(update.Kind)) Kind = update.Kind;
        if (!string.IsNullOrWhiteSpace(update.Name)) ToolName = update.Name;
        if (!string.IsNullOrWhiteSpace(update.Status)) Status = update.Status;

        var rawTarget = !string.IsNullOrWhiteSpace(update.Description) ? update.Description
            : !string.IsNullOrWhiteSpace(update.Command) ? ToolCallItemViewModel.ToSingleLine(update.Command)
            : update.Locations.FirstOrDefault()?.Path
            ?? ToolCallItemViewModel.ToSingleLine(update.Title);

        if (string.IsNullOrWhiteSpace(ToolName))
        {
            ToolName = string.IsNullOrWhiteSpace(Kind) ? "Tool" : Kind;
        }
        if (!string.IsNullOrWhiteSpace(rawTarget) && !string.Equals(rawTarget, ToolName, StringComparison.OrdinalIgnoreCase))
        {
            Target = rawTarget!;
        }

        if (!IsRunning && string.IsNullOrEmpty(DurationText))
        {
            var elapsed = DateTimeOffset.Now - StartedAt;
            DurationText = elapsed.TotalSeconds < 1 ? "<1s" : $"{elapsed.TotalSeconds:0.#}s";
        }

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(KindIcon));
        OnPropertyChanged(nameof(ActionSummary));
    }
}

/// <summary>One spawned subagent tracked in the GUI swimlane, HUD popover, slide-over drawer, and sidebar.</summary>
public sealed partial class SubagentRunViewModel : ObservableObject
{
    private readonly Dictionary<string, SubagentStepViewModel> _stepsById = new(StringComparer.Ordinal);
    private readonly Action<SubagentRunViewModel>? _onSelect;
    private readonly Action<SubagentRunViewModel>? _onStop;

    public SubagentRunViewModel(
        string id,
        string agentType,
        string? description,
        DateTimeOffset startedAt,
        Action<SubagentRunViewModel>? onSelect = null,
        Action<SubagentRunViewModel>? onStop = null)
    {
        Id = id;
        AgentType = string.IsNullOrWhiteSpace(agentType) ? "general-purpose" : agentType;
        Description = description ?? "";
        StartedAt = startedAt;
        _onSelect = onSelect;
        _onStop = onStop;
        Tick(startedAt);
    }

    public string Id { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? EndedAt { get; private set; }
    /// <summary>True when the launch tool returns immediately while the subagent runs asynchronously (e.g. Antigravity <c>invoke_subagent</c>).</summary>
    public bool RunsAsynchronously { get; private set; }
    /// <summary>Underlying subagent conversation id discovered from the parent transcript (Antigravity CLI).</summary>
    public string? ConversationId { get; set; }
    /// <summary>Path to the subagent's own live transcript file when tracked locally (Antigravity CLI).</summary>
    public string? TranscriptPath { get; set; }
    internal long TranscriptOffset { get; set; }

    [ObservableProperty] public partial string AgentType { get; set; } = "general-purpose";
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial string? Prompt { get; set; }
    [ObservableProperty] public partial string? Model { get; set; }
    [ObservableProperty] public partial bool IsBackground { get; set; }
    [ObservableProperty] public partial string? WorktreeIsolation { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "running";
    [ObservableProperty] public partial string ElapsedText { get; set; } = "0s";
    [ObservableProperty] public partial string LastAction { get; set; } = "";
    [ObservableProperty] public partial string? Summary { get; set; }

    public ObservableCollection<SubagentStepViewModel> Steps { get; } = [];

    public string DisplayTitle => string.IsNullOrWhiteSpace(Description) ? AgentType : Description;
    public bool HasModel => !string.IsNullOrWhiteSpace(Model);
    public bool HasWorktree => !string.IsNullOrWhiteSpace(WorktreeIsolation);
    public bool HasLastAction => !string.IsNullOrWhiteSpace(LastAction);
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public string SubtitleText => HasLastAction
        ? LastAction
        : HasSummary
            ? ToolCallItemViewModel.ToSingleLine(Summary) ?? Summary!
            : "";
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(SubtitleText);
    public bool IsRunning => Status == "running";
    public bool IsDone => Status == "done";
    public bool IsFailed => Status == "failed";
    public bool IsInterrupted => Status == "interrupted";
    public int ToolCount => Steps.Count;
    public string StepsHeaderText => AppLangResources.Format(AppLangResourceKind.AgentGuiSubagentStepsHeader, Steps.Count);
    public string ToolCountText => Steps.Count > 0 ? AppLangResources.Format(AppLangResourceKind.SubagentToolCount, Steps.Count) : "";
    public string MetaText => Steps.Count > 0 ? $"{ToolCountText} · {ElapsedText}" : ElapsedText;
    public string BarLabel => HasLastAction ? $"{LastAction} · {ElapsedText}" : $"{DisplayTitle} · {ElapsedText}";

    [RelayCommand]
    private void Select() => _onSelect?.Invoke(this);

    [RelayCommand]
    private void Stop()
    {
        if (IsRunning) _onStop?.Invoke(this);
    }

    public void ApplyLaunchUpdate(AcpToolCallUpdate update, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(update.SubagentType)) AgentType = update.SubagentType!;
        var desc = !string.IsNullOrWhiteSpace(update.Description)
            ? ToolCallItemViewModel.ToSingleLine(update.Description)
            : ToolCallItemViewModel.ToSingleLine(update.Title);
        if (!string.IsNullOrWhiteSpace(desc)) Description = desc!;
        if (!string.IsNullOrWhiteSpace(update.SubagentPrompt)) Prompt = update.SubagentPrompt;
        if (!string.IsNullOrWhiteSpace(update.SubagentModel)) Model = update.SubagentModel;
        if (update.IsBackground) IsBackground = true;
        if (update.SubagentRunsAsynchronously)
        {
            RunsAsynchronously = true;
            IsBackground = true;
        }
        if (!string.IsNullOrWhiteSpace(update.WorktreeIsolation)) WorktreeIsolation = update.WorktreeIsolation;

        if (update.TextOutputs.Count > 0)
        {
            Summary = string.Join("\n", update.TextOutputs).Trim();
        }
        else if (!string.IsNullOrWhiteSpace(update.Stdout))
        {
            Summary = update.Stdout.Trim();
        }
        else if (update.RawOutput is { ValueKind: System.Text.Json.JsonValueKind.String } s && s.GetString() is { Length: > 0 } str)
        {
            Summary = str.Trim();
        }

        if (update.Status == "completed" && !RunsAsynchronously)
        {
            Status = "done";
            EndedAt ??= now;
        }
        else if (update.Status == "failed")
        {
            Status = "failed";
            EndedAt ??= now;
        }

        Tick(now);
        NotifyDerived();
    }

    public void CompleteOldestRunningSteps(int count, bool failed)
    {
        if (count <= 0) return;
        var remaining = count;
        var targetStatus = failed ? "failed" : "completed";
        foreach (var step in Steps)
        {
            if (remaining <= 0) break;
            if (!step.IsRunning) continue;
            step.Apply(new AcpToolCallUpdate { ToolCallId = step.ToolCallId, Status = targetStatus });
            remaining--;
        }
        NotifyDerived();
    }

    public void MarkCompleted(string? summary, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(summary))
        {
            Summary = summary.Trim();
        }
        CompleteOldestRunningSteps(Steps.Count, failed: false);
        if (Status != "done")
        {
            Status = "done";
            EndedAt ??= now;
        }
        Tick(now);
        NotifyDerived();
    }

    public void MarkFailed(string? summary, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(summary))
        {
            Summary = summary.Trim();
        }
        CompleteOldestRunningSteps(Steps.Count, failed: true);
        if (Status != "failed")
        {
            Status = "failed";
            EndedAt ??= now;
        }
        Tick(now);
        NotifyDerived();
    }

    private const int MaxSteps = 200;

    public void UpsertStep(AcpToolCallUpdate update)
    {
        var id = string.IsNullOrWhiteSpace(update.ToolCallId) ? $"step-{Steps.Count + 1}" : update.ToolCallId;
        if (!_stepsById.TryGetValue(id, out var step))
        {
            step = new SubagentStepViewModel(id);
            _stepsById[id] = step;
            Steps.Add(step);
            while (Steps.Count > MaxSteps)
            {
                var oldest = Steps[0];
                Steps.RemoveAt(0);
                _stepsById.Remove(oldest.ToolCallId);
            }
        }
        step.Apply(update);
        LastAction = step.ActionSummary;
        NotifyDerived();
    }

    public void MarkInterrupted(DateTimeOffset now)
    {
        if (!IsRunning) return;
        Status = "interrupted";
        EndedAt ??= now;
        Tick(now);
        NotifyDerived();
    }

    public void Tick(DateTimeOffset now)
    {
        var next = SubagentCardViewModel.FormatElapsed((EndedAt ?? now) - StartedAt);
        if (ElapsedText != next)
        {
            ElapsedText = next;
            OnPropertyChanged(nameof(MetaText));
            OnPropertyChanged(nameof(BarLabel));
        }
    }

    public SubagentRun ToExternalRun()
    {
        var state = Status switch
        {
            "done" => SubagentState.Done,
            "failed" => SubagentState.Failed,
            "interrupted" => SubagentState.Interrupted,
            _ => SubagentState.Running,
        };
        return SubagentRun.CreateExternal(
            Id,
            AgentType,
            string.IsNullOrWhiteSpace(Description) ? null : Description,
            state,
            StartedAt,
            EndedAt,
            Steps.Count,
            HasLastAction ? LastAction : null,
            IsBackground);
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(HasModel));
        OnPropertyChanged(nameof(HasWorktree));
        OnPropertyChanged(nameof(HasLastAction));
        OnPropertyChanged(nameof(HasSummary));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(HasSubtitle));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsInterrupted));
        OnPropertyChanged(nameof(ToolCount));
        OnPropertyChanged(nameof(StepsHeaderText));
        OnPropertyChanged(nameof(ToolCountText));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(BarLabel));
    }
}

/// <summary>One timestamped line of live output captured from a background Bash process.</summary>
public sealed record BackgroundBashLogLine(string Timestamp, string Text, bool IsError, bool IsSuccess, bool IsInfo);

/// <summary>A persistent background Bash task tracked across turns in the Floating HUD Popover and Swimlane.</summary>
public sealed partial class BackgroundBashViewModel : ObservableObject
{
    private const int MaxLogLines = 300;

    private readonly Action<BackgroundBashViewModel>? _onSelect;
    private readonly Action<BackgroundBashViewModel>? _onStop;
    private readonly Action<BackgroundBashViewModel>? _onOpenTerminal;

    public BackgroundBashViewModel(
        string id,
        string? taskId,
        string command,
        string? description,
        DateTimeOffset startedAt,
        Action<BackgroundBashViewModel>? onSelect = null,
        Action<BackgroundBashViewModel>? onStop = null,
        Action<BackgroundBashViewModel>? onOpenTerminal = null)
    {
        Id = id;
        TaskId = taskId;
        Command = command;
        Description = description ?? "";
        StartedAt = startedAt;
        _onSelect = onSelect;
        _onStop = onStop;
        _onOpenTerminal = onOpenTerminal;
        Tick(startedAt);
    }

    public string Id { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? EndedAt { get; private set; }

    [ObservableProperty] public partial string? TaskId { get; set; }
    [ObservableProperty] public partial string Command { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "running";
    [ObservableProperty] public partial int? ExitCode { get; set; }
    [ObservableProperty] public partial string? DetectedPort { get; set; }
    [ObservableProperty] public partial string ElapsedText { get; set; } = "0s";
    [ObservableProperty] public partial int UnreadCount { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsLogsExpanded { get; set; }

    public ObservableCollection<BackgroundBashLogLine> Logs { get; } = [];

    public string DisplayCommand => ToolCallItemViewModel.ToSingleLine(Command) ?? Command;
    public string CompactCommand => FormatCompactCommand(DisplayCommand);
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasPort => !string.IsNullOrWhiteSpace(DetectedPort);
    public bool IsRunning => Status == "running";
    public bool IsFailed => Status == "failed";
    public bool IsCompleted => Status is "completed" or "stopped";
    public bool HasUnread => UnreadCount > 0;
    public string UnreadBadgeText => UnreadCount > 0 ? $"+{UnreadCount}" : "";
    public string LastLogLine => Logs.Count > 0 ? Logs[^1].Text : "";
    public bool HasLastLogLine => Logs.Count > 0;

    public string LogsToggleLabel => AppLangResources.Get(
        IsLogsExpanded ? AppLangResourceKind.AgentGuiBgBashHideLogs : AppLangResourceKind.AgentGuiBgBashViewLogs);

    public string StatusText => Status switch
    {
        "running" => AppLangResources.Get(AppLangResourceKind.AgentGuiBgBashRunning),
        "failed" => AppLangResources.Format(AppLangResourceKind.AgentGuiBgBashFailed, ExitCode ?? 1),
        _ => AppLangResources.Format(AppLangResourceKind.AgentGuiBgBashExited, ExitCode ?? 0),
    };

    public string StatusElapsedText => $"{StatusText} · {ElapsedText}";

    public string CombinedLogText =>
        Logs.Count == 0
            ? $"$ {Command}"
            : $"$ {Command}\n" + string.Join("\n", Logs.Select(l => $"[{l.Timestamp}] {l.Text}"));

    public string BarLabel =>
        HasPort
            ? $"{DetectedPort} · {StatusText} · {ElapsedText}"
            : $"{StatusText} · {ElapsedText}";

    partial void OnIsLogsExpandedChanged(bool value)
    {
        if (value) ClearUnread();
        OnPropertyChanged(nameof(LogsToggleLabel));
    }

    [RelayCommand]
    private void Select() => _onSelect?.Invoke(this);

    [RelayCommand]
    private void ToggleLogs() => _onSelect?.Invoke(this);

    [RelayCommand]
    private void Stop()
    {
        if (IsRunning) _onStop?.Invoke(this);
    }

    [RelayCommand]
    private void OpenInTerminal() => _onOpenTerminal?.Invoke(this);

    public void ClearUnread()
    {
        if (UnreadCount == 0) return;
        UnreadCount = 0;
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(UnreadBadgeText));
    }

    public void AppendOutput(string? stdout, string? stderr, bool markUnread)
    {
        var added = 0;
        var ts = DateTime.Now.ToString("HH:mm:ss");
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            added += AppendChunk(ts, stdout, isError: false);
        }
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            added += AppendChunk(ts, stderr, isError: true);
        }
        if (added > 0)
        {
            if (markUnread && !IsLogsExpanded)
            {
                UnreadCount += added;
                OnPropertyChanged(nameof(HasUnread));
                OnPropertyChanged(nameof(UnreadBadgeText));
            }
            OnPropertyChanged(nameof(CombinedLogText));
            OnPropertyChanged(nameof(LastLogLine));
            OnPropertyChanged(nameof(HasLastLogLine));
            OnPropertyChanged(nameof(HasPort));
            OnPropertyChanged(nameof(BarLabel));
        }
    }

    public void MarkExited(int? exitCode, bool failed, DateTimeOffset now)
    {
        ExitCode = exitCode;
        Status = failed || exitCode is not null and not 0 ? "failed" : "completed";
        EndedAt ??= now;
        Tick(now);
        NotifyStatus();
    }

    public void MarkStopped(DateTimeOffset now)
    {
        if (!IsRunning) return;
        Status = "stopped";
        ExitCode ??= 143;
        EndedAt ??= now;
        AppendOutput(null, "Process stopped.", markUnread: false);
        Tick(now);
        NotifyStatus();
    }

    public void Tick(DateTimeOffset now)
    {
        var next = SubagentCardViewModel.FormatElapsed((EndedAt ?? now) - StartedAt);
        if (ElapsedText != next)
        {
            ElapsedText = next;
            OnPropertyChanged(nameof(StatusElapsedText));
            OnPropertyChanged(nameof(BarLabel));
        }
    }

    public static string FormatCompactCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        return LongPathRegex().Replace(command, m =>
        {
            var raw = m.Value;
            var trimmed = raw.TrimEnd('/', '\\');
            var sep = trimmed.LastIndexOfAny(['/', '\\']);
            if (sep < 0 || sep >= trimmed.Length - 1) return raw;
            var tail = trimmed[(sep + 1)..];
            return $"…/{tail}";
        });
    }

    private int AppendChunk(string ts, string text, bool isError)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var added = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            // Avoid duplicating identical tail line when polling returns cumulative buffer.
            if (Logs.Count > 0 && string.Equals(Logs[^1].Text, line, StringComparison.Ordinal))
            {
                continue;
            }
            if (DetectedPort is null)
            {
                var m = PortRegex().Match(line);
                if (m.Success)
                {
                    DetectedPort = ":" + m.Groups[1].Value;
                }
            }
            var isSuccess = !isError && (line.Contains("Passed!", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ready in", StringComparison.OrdinalIgnoreCase)
                || line.Contains("listening on", StringComparison.OrdinalIgnoreCase));
            var isInfo = !isError && !isSuccess && (line.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                || line.Contains("watching", StringComparison.OrdinalIgnoreCase)
                || line.Contains("started", StringComparison.OrdinalIgnoreCase));
            Logs.Add(new BackgroundBashLogLine(ts, line, isError, isSuccess, isInfo));
            while (Logs.Count > MaxLogLines) Logs.RemoveAt(0);
            added++;
        }
        return added;
    }

    private void NotifyStatus()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusElapsedText));
        OnPropertyChanged(nameof(BarLabel));
    }

    [GeneratedRegex(@"(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::\]|port\s+)[:\s]?(\d{2,5})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PortRegex();

    [GeneratedRegex(@"(?:(?<=\s)|^)(?:/[^\s""']+){3,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongPathRegex();
}

/// <summary>Interactive Claude Code Plan Approval Gate card (<c>ExitPlanMode</c>) in the timeline.</summary>
public sealed partial class PlanApprovalCardItem : AgentTimelineItem
{
    private readonly Action<string?, string?> _resolve;

    public PlanApprovalCardItem(AcpPermissionRequest request, Action<string?, string?> resolve)
    {
        _resolve = resolve;
        Title = AppLangResources.Get(AppLangResourceKind.AgentGuiPlanGateTitle);
        PlanMarkdown = !string.IsNullOrWhiteSpace(request.PlanMarkdown)
            ? request.PlanMarkdown
            : request.ActionDetail ?? request.ToolCall.PlanMarkdown ?? request.ToolCall.Title;
        foreach (var opt in request.Options)
        {
            Actions.Add(new PermissionActionViewModel(opt, Select));
        }
    }

    public string Title { get; }
    public string? PlanMarkdown { get; }
    public bool HasPlanMarkdown => !string.IsNullOrWhiteSpace(PlanMarkdown);
    public ObservableCollection<PermissionActionViewModel> Actions { get; } = [];

    [ObservableProperty] public partial bool IsFeedbackOpen { get; set; }
    [ObservableProperty] public partial string FeedbackText { get; set; } = "";
    [ObservableProperty] public partial bool IsResolved { get; set; }
    [ObservableProperty] public partial string ResolvedSummary { get; set; } = "";

    private void Select(string optionId)
    {
        if (IsResolved) return;
        IsResolved = true;
        IsFeedbackOpen = false;
        ResolvedSummary = Actions.FirstOrDefault(a => a.OptionId == optionId)?.Label ?? optionId;
        _resolve(optionId, null);
    }

    [RelayCommand]
    private void ToggleFeedback()
    {
        if (!IsResolved) IsFeedbackOpen = !IsFeedbackOpen;
    }

    [RelayCommand]
    private void SubmitFeedback()
    {
        if (IsResolved) return;
        var text = FeedbackText.Trim();
        IsResolved = true;
        IsFeedbackOpen = false;
        ResolvedSummary = string.IsNullOrWhiteSpace(text)
            ? AppLangResources.Get(AppLangResourceKind.AgentGuiPlanReviseToggle)
            : text;
        _resolve(null, text);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsResolved) return;
        IsResolved = true;
        IsFeedbackOpen = false;
        ResolvedSummary = AppLangResources.Get(AppLangResourceKind.AgentGuiPermissionCancel);
        _resolve(null, null);
    }
}
