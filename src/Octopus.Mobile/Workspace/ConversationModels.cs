using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.Mobile.Localization;
using Octopus.Ticker;

namespace Octopus.Mobile.Workspace;

/// <summary>How a GUI conversation is doing (the host's <c>AcpStates</c>).</summary>
public enum ConversationState
{
    /// <summary>The agent is starting on the host, or this phone is attaching to it.</summary>
    Connecting,
    /// <summary>Waiting for the next prompt.</summary>
    Ready,
    /// <summary>A turn runs.</summary>
    Working,
    /// <summary>A permission or question card waits for an answer.</summary>
    Waiting,
    /// <summary>The agent could not be started, or the connection to it broke.</summary>
    Error,
}

/// <summary>What can be chosen above the input of a conversation (the host's <c>AcpOptionKinds</c>).</summary>
public enum ConversationOptionKind
{
    Model,
    /// <summary>The permission mode.</summary>
    Mode,
    Effort,
}

/// <summary>A choice the host offers: a model, a mode, an effort, an answer to a question.</summary>
/// <param name="Detail">Permission options: ACP's kind (<c>allow_once</c>…). Answers: a description.</param>
public sealed record ConversationOption(string Id, string Label, string? Detail = null);

/// <summary>
/// One entry of a conversation's timeline. The subclass says what it is; an entry keeps its object for as long as the
/// host keeps its id, so a page showing it keeps what was expanded or typed into it.
/// </summary>
public abstract partial class ConversationEntry(string id) : ObservableObject
{
    /// <summary>Stable for the life of the entry on the host.</summary>
    public string Id { get; } = id;
}

/// <summary>What the person sent.</summary>
public sealed partial class ConversationUserEntry(string id) : ConversationEntry(id)
{
    [ObservableProperty] public partial string Text { get; set; } = "";
    /// <summary>The names of the attached files and images.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasAttachments), nameof(AttachmentsText))]
    public partial IReadOnlyList<string> Attachments { get; set; } = [];

    public bool HasAttachments => Attachments.Count > 0;
    public string AttachmentsText => string.Join("  ", Attachments);
}

/// <summary>One intermediate assistant text segment folded into the mobile work area.</summary>
public sealed record ConversationWorkNote(string Text, int BeforeToolIndex);

/// <summary>One turn of the agent: unified Scheme C pure-text work area (thought, tools, plan, folded text) + Markdown answer.</summary>
public sealed partial class ConversationAssistantEntry(string id) : ConversationEntry(id)
{
    private readonly List<ConversationWorkStep> _workSteps = [];
    private readonly Dictionary<string, ConversationWorkStep> _toolStepsById = new(StringComparer.Ordinal);
    private readonly List<ConversationWorkStep> _noteSteps = [];
    private ConversationWorkStep? _thoughtStep;
    private ConversationWorkStep? _planStep;
    private ObservableCollection<ConversationWorkStep>? _expandedWorkSteps;
    private ConversationWorkTicker? _ticker;
    private bool _wasStreaming;
    private string _workHeaderTitle = "";
    private string _workHeaderCounts = "";
    private string _workHeaderFailedText = "";

    /// <summary>The answer as Markdown, as the host has it so far.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasText))]
    public partial string Text { get; set; } = "";
    /// <summary>The answer cut into blocks for the page; follows <see cref="Text"/>.</summary>
    [ObservableProperty] public partial IReadOnlyList<ConversationTextBlock> Blocks { get; private set; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasThought))]
    public partial string? Thought { get; set; }
    /// <summary>The thought is shown in full; folded otherwise.</summary>
    [ObservableProperty] public partial bool IsThoughtExpanded { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMeta), nameof(HasWorkArea), nameof(ShowStandaloneDuration))]
    public partial bool IsStreaming { get; set; }
    /// <summary>True when the user manually tapped the unified work area header to inspect individual pure-text steps.</summary>
    [ObservableProperty] public partial bool IsWorkExpanded { get; set; }
    /// <summary>Why the turn failed.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; set; }
    /// <summary>How long the turn took, as the host words it ("12s").</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasDuration), nameof(HasMeta), nameof(ShowStandaloneDuration))]
    public partial string? Duration { get; set; }
    public ObservableCollection<ConversationTool> Tools { get; } = [];
    public IReadOnlyList<ConversationWorkNote> Notes { get; set; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPlan), nameof(PlanText))]
    public partial IReadOnlyList<ConversationPlanItem> Plan { get; set; } = [];

    public bool HasText => Text.Length > 0;
    public bool HasThought => !string.IsNullOrEmpty(Thought);
    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public bool HasDuration => !string.IsNullOrEmpty(Duration);
    public bool HasMeta => IsStreaming || HasDuration;
    public bool HasPlan => Plan.Count > 0;

    /// <summary>All unified work steps (thought, tools, plan) for this turn.</summary>
    public IReadOnlyList<ConversationWorkStep> WorkSteps => _workSteps;
    public int WorkStepCount => _workSteps.Count;
    public bool HasWorkArea => IsStreaming || _workSteps.Count > 0;
    public bool CanExpandWork => _workSteps.Count > 0;
    public bool ShowStandaloneDuration => !HasWorkArea && HasDuration;

    /// <summary>Lazy UI collection: null when collapsed so zero step controls exist until tapped.</summary>
    public ObservableCollection<ConversationWorkStep>? ExpandedWorkSteps => _expandedWorkSteps;

    /// <summary>Fixed 3-line sliding live ticker (Scheme C): non-null ONLY while streaming and collapsed.</summary>
    public bool ShowLiveTicker => IsStreaming && !IsWorkExpanded && (!HasText || HasAnyRunningTool());
    public ConversationWorkTicker? LiveTicker => ShowLiveTicker ? _ticker : null;

    public string WorkHeaderTitle => _workHeaderTitle;
    public string WorkHeaderCounts => _workHeaderCounts;
    public bool HasWorkHeaderCounts => !string.IsNullOrEmpty(_workHeaderCounts);
    public string WorkHeaderFailedText => _workHeaderFailedText;
    public bool HasWorkFailures => !string.IsNullOrEmpty(_workHeaderFailedText);

    /// <summary>"Plan 2/3 · the step under way" (the first that is not done when none is under way).</summary>
    public string PlanText
    {
        get
        {
            if (Plan.Count == 0) return "";
            var done = Plan.Count(item => item.IsCompleted);
            var current = Plan.FirstOrDefault(item => item.IsInProgress) ?? Plan.FirstOrDefault(item => !item.IsCompleted);
            var head = string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationPlan), done, Plan.Count);
            return current is null ? head : head + " · " + current.Content;
        }
    }

    partial void OnTextChanged(string value) => Blocks = ConversationMarkdown.Parse(value);

    partial void OnIsWorkExpandedChanged(bool value)
    {
        if (value && _workSteps.Count > 0)
        {
            _expandedWorkSteps = new ObservableCollection<ConversationWorkStep>(_workSteps);
        }
        else if (_expandedWorkSteps is not null)
        {
            for (var i = 0; i < _workSteps.Count; i++)
            {
                if (_workSteps[i].IsExpanded) _workSteps[i].IsExpanded = false;
            }
            _expandedWorkSteps = null;
        }
        OnPropertyChanged(nameof(ExpandedWorkSteps));
        SyncLiveTicker();
    }

    /// <summary>
    /// Synchronizes the unified work steps, summary counts, and 3-line sliding ticker in one pass after a remote update.
    /// Automatically collapses the work area when streaming finishes.
    /// </summary>
    public void SyncWorkArea()
    {
        var hadWork = HasWorkArea;
        var hadExpandable = CanExpandWork;
        var prevCount = _workSteps.Count;

        // Auto-collapse once a streaming turn finishes so only 1 summary line + clean reply text remain.
        if (_wasStreaming && !IsStreaming && IsWorkExpanded)
        {
            IsWorkExpanded = false;
        }
        _wasStreaming = IsStreaming;

        _workSteps.Clear();

        // 1. Thought step (reused in place across updates so expansion state stays intact while open).
        if (HasThought)
        {
            var thoughtActive = IsStreaming && !HasText && Tools.Count == 0;
            _thoughtStep ??= ConversationWorkStep.CreateThought(Thought!, thoughtActive);
            _thoughtStep.UpdateThought(Thought!, thoughtActive);
            _workSteps.Add(_thoughtStep);
        }
        else
        {
            _thoughtStep = null;
        }

        // 2. Tool steps and folded intermediate text notes in chronological host order.
        var noteCursor = 0;
        var noteStepIdx = 0;
        void AppendNotesUpTo(int toolIndex)
        {
            while (noteCursor < Notes.Count && Notes[noteCursor].BeforeToolIndex <= toolIndex)
            {
                var note = Notes[noteCursor];
                if (!string.IsNullOrWhiteSpace(note.Text))
                {
                    ConversationWorkStep noteStep;
                    if (noteStepIdx < _noteSteps.Count)
                    {
                        noteStep = _noteSteps[noteStepIdx];
                        noteStep.UpdateText(note.Text);
                    }
                    else
                    {
                        noteStep = ConversationWorkStep.CreateText(note.Text);
                        _noteSteps.Add(noteStep);
                    }
                    noteStepIdx++;
                    _workSteps.Add(noteStep);
                }
                noteCursor++;
            }
        }

        if (Tools.Count > 0)
        {
            var liveIds = _toolStepsById.Count > 0 ? new HashSet<string>(StringComparer.Ordinal) : null;
            for (var i = 0; i < Tools.Count; i++)
            {
                AppendNotesUpTo(i);
                var tool = Tools[i];
                liveIds?.Add(tool.Id);
                if (!_toolStepsById.TryGetValue(tool.Id, out var step))
                {
                    step = ConversationWorkStep.CreateTool(tool);
                    _toolStepsById[tool.Id] = step;
                }
                else
                {
                    step.NotifyToolUpdated();
                }
                _workSteps.Add(step);
            }
            if (liveIds is not null && _toolStepsById.Count > liveIds.Count)
            {
                foreach (var stale in _toolStepsById.Keys.Where(k => !liveIds.Contains(k)).ToList())
                {
                    _toolStepsById.Remove(stale);
                }
            }
        }
        else if (_toolStepsById.Count > 0)
        {
            _toolStepsById.Clear();
        }
        AppendNotesUpTo(int.MaxValue);
        if (noteStepIdx < _noteSteps.Count)
        {
            _noteSteps.RemoveRange(noteStepIdx, _noteSteps.Count - noteStepIdx);
        }

        // 3. Plan step.
        if (HasPlan)
        {
            _planStep ??= ConversationWorkStep.CreatePlan(Plan, PlanText);
            _planStep.UpdatePlan(Plan, PlanText);
            _workSteps.Add(_planStep);
        }
        else
        {
            _planStep = null;
        }

        if (_expandedWorkSteps is not null)
        {
            if (_workSteps.Count == 0)
            {
                IsWorkExpanded = false;
            }
            else
            {
                ConversationTimeline.Sync(_expandedWorkSteps, _workSteps);
            }
        }

        RefreshWorkSummary();
        SyncLiveTicker();

        if (prevCount != _workSteps.Count) OnPropertyChanged(nameof(WorkStepCount));
        if (hadWork != HasWorkArea)
        {
            OnPropertyChanged(nameof(HasWorkArea));
            OnPropertyChanged(nameof(ShowStandaloneDuration));
        }
        if (hadExpandable != CanExpandWork) OnPropertyChanged(nameof(CanExpandWork));
    }

    private bool HasAnyRunningTool()
    {
        for (var i = 0; i < Tools.Count; i++)
        {
            if (Tools[i].IsRunning) return true;
        }
        return false;
    }

    private void SyncLiveTicker()
    {
        if (ShowLiveTicker)
        {
            var created = _ticker is null;
            _ticker ??= new ConversationWorkTicker();
            _ticker.Sync(_workSteps);
            OnPropertyChanged(nameof(ShowLiveTicker));
            if (created) OnPropertyChanged(nameof(LiveTicker));
        }
        else
        {
            var had = _ticker is not null;
            _ticker = null;
            OnPropertyChanged(nameof(ShowLiveTicker));
            if (had) OnPropertyChanged(nameof(LiveTicker));
        }
    }

    private void RefreshWorkSummary()
    {
        int exec = 0, search = 0, read = 0, edit = 0, fetch = 0, think = 0, plan = 0, other = 0, failed = 0;
        for (var i = 0; i < _workSteps.Count; i++)
        {
            var step = _workSteps[i];
            if (step.IsFailed) failed++;
            switch (step.StepKind)
            {
                case ConversationWorkStepKind.Thought:
                    think++;
                    break;
                case ConversationWorkStepKind.Plan:
                    plan++;
                    break;
                case ConversationWorkStepKind.Tool:
                    switch (step.Tool?.Kind)
                    {
                        case "execute": exec++; break;
                        case "search": search++; break;
                        case "read": read++; break;
                        case "edit" or "delete" or "move": edit++; break;
                        case "fetch": fetch++; break;
                        case "think": think++; break;
                        default: other++; break;
                    }
                    break;
                case ConversationWorkStepKind.Text:
                    // Folded intermediate text is intentionally omitted from header counts and step totals.
                    break;
            }
        }

        var countedSteps = exec + search + read + edit + fetch + think + plan + other;

        string nextTitle;
        string nextCounts;
        if (IsStreaming)
        {
            nextTitle = MobileLangResources.Get(MobileLangResourceKind.ConversationWorkArea);
            var parts = new List<string>(6);
            if (exec > 0) parts.Add(string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkCountExec), exec));
            if (search > 0) parts.Add(string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkCountSearch), search));
            if (read > 0) parts.Add(string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkCountRead), read));
            if (edit > 0) parts.Add(string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkCountEdit), edit));
            if (fetch > 0) parts.Add(string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkCountFetch), fetch));
            if (think > 0) parts.Add(string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkCountThink), think));
            if (plan > 0) parts.Add(string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkCountPlan), plan));
            if (other > 0) parts.Add(string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkCountOther), other));
            nextCounts = parts.Count > 0
                ? string.Join(" · ", parts)
                : MobileLangResources.Get(MobileLangResourceKind.ConversationWorkPreparing);
        }
        else
        {
            nextTitle = countedSteps switch
            {
                0 => MobileLangResources.Get(MobileLangResourceKind.ConversationWorkArea),
                1 => MobileLangResources.Get(MobileLangResourceKind.ConversationWorkCompletedSingleStep),
                _ => string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkCompletedSteps), countedSteps),
            };
            var parts = new List<string>(6);
            if (exec > 0) parts.Add(string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkShortExec), exec));
            if (search > 0) parts.Add(string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkShortSearch), search));
            if (read > 0) parts.Add(string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkShortRead), read));
            if (edit > 0) parts.Add(string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkShortEdit), edit));
            if (fetch > 0) parts.Add(string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkShortFetch), fetch));
            if (think > 0) parts.Add(string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkShortThink), think));
            if (plan > 0) parts.Add(string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkShortPlan), plan));
            if (other > 0) parts.Add(string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkShortOther), other));
            nextCounts = parts.Count > 0 ? $"· {string.Join(" · ", parts)}" : "";
        }

        var nextFailed = failed > 0
            ? $"· {string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationWorkFailedCount), failed)}"
            : "";

        if (_workHeaderTitle != nextTitle)
        {
            _workHeaderTitle = nextTitle;
            OnPropertyChanged(nameof(WorkHeaderTitle));
        }
        if (_workHeaderCounts != nextCounts)
        {
            _workHeaderCounts = nextCounts;
            OnPropertyChanged(nameof(WorkHeaderCounts));
            OnPropertyChanged(nameof(HasWorkHeaderCounts));
        }
        if (_workHeaderFailedText != nextFailed)
        {
            _workHeaderFailedText = nextFailed;
            OnPropertyChanged(nameof(WorkHeaderFailedText));
            OnPropertyChanged(nameof(HasWorkFailures));
        }
    }
}

/// <summary>Category of a step inside <see cref="ConversationAssistantEntry"/>'s unified work area.</summary>
public enum ConversationWorkStepKind
{
    Thought,
    Tool,
    Plan,
    Text,
}

/// <summary>
/// One pure-text step inside a mobile assistant turn's single unified work area.
/// Details are lazy-mounted via <see cref="ExpandedDetail"/> only when tapped.
/// </summary>
public sealed partial class ConversationWorkStep : ObservableObject
{
    private const int ThoughtPreviewMaxChars = 90;

    private string _thoughtText = "";
    private string _thoughtPreview = "";
    private string _noteText = "";
    private string _notePreview = "";
    private bool _isThoughtActive;
    private IReadOnlyList<ConversationPlanItem> _planItems = [];
    private string _planPreview = "";

    private ConversationWorkStep(ConversationWorkStepKind stepKind) => StepKind = stepKind;

    public static ConversationWorkStep CreateThought(string text, bool isActive)
    {
        var step = new ConversationWorkStep(ConversationWorkStepKind.Thought);
        step.UpdateThought(text, isActive);
        return step;
    }

    public static ConversationWorkStep CreateText(string text)
    {
        var step = new ConversationWorkStep(ConversationWorkStepKind.Text);
        step.UpdateText(text);
        return step;
    }

    public static ConversationWorkStep CreateTool(ConversationTool tool) =>
        new(ConversationWorkStepKind.Tool) { Tool = tool };

    public static ConversationWorkStep CreatePlan(IReadOnlyList<ConversationPlanItem> items, string preview)
    {
        var step = new ConversationWorkStep(ConversationWorkStepKind.Plan);
        step.UpdatePlan(items, preview);
        return step;
    }

    public ConversationWorkStepKind StepKind { get; }
    public ConversationTool? Tool { get; private init; }
    public IReadOnlyList<ConversationPlanItem> PlanItems => _planItems;
    public string ThoughtText => _thoughtText;
    public string NoteText => _noteText;

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public bool IsThought => StepKind == ConversationWorkStepKind.Thought;
    public bool IsTool => StepKind == ConversationWorkStepKind.Tool;
    public bool IsPlan => StepKind == ConversationWorkStepKind.Plan;
    public bool IsText => StepKind == ConversationWorkStepKind.Text;
    public bool IsProse => StepKind is ConversationWorkStepKind.Thought or ConversationWorkStepKind.Text;

    public bool IsRunning => StepKind switch
    {
        ConversationWorkStepKind.Tool => Tool!.IsRunning,
        ConversationWorkStepKind.Thought => _isThoughtActive,
        _ => false,
    };

    public bool IsFailed => StepKind == ConversationWorkStepKind.Tool && (Tool!.IsFailed || Tool.HasExitCode);
    public bool IsCompleted => !IsRunning && !IsFailed;
    public string StatusMark => IsFailed ? "✕" : IsExpanded ? "▾" : "·";

    public string Verb => StepKind switch
    {
        ConversationWorkStepKind.Thought => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbThink),
        ConversationWorkStepKind.Plan => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbPlan),
        ConversationWorkStepKind.Text => "",
        _ => ResolveToolVerb(Tool?.Kind, active: false, done: false),
    };

    public bool HasVerb => !string.IsNullOrEmpty(Verb);

    public string ActiveVerb => StepKind switch
    {
        ConversationWorkStepKind.Thought => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbThinkActive),
        ConversationWorkStepKind.Plan => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbPlanActive),
        ConversationWorkStepKind.Text => "",
        _ => ResolveToolVerb(Tool?.Kind, active: true, done: false),
    };

    public string DoneVerb => StepKind switch
    {
        ConversationWorkStepKind.Thought => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbThinkDone),
        ConversationWorkStepKind.Plan => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbPlanDone),
        ConversationWorkStepKind.Text => "",
        _ => ResolveToolVerb(Tool?.Kind, active: false, done: true),
    };

    public string Target => StepKind switch
    {
        ConversationWorkStepKind.Thought => _thoughtPreview,
        ConversationWorkStepKind.Text => _notePreview,
        ConversationWorkStepKind.Plan => _planPreview,
        _ => ToSingleLine(Tool!.Title) ?? Tool.Kind,
    };

    public bool HasAdded => Tool is { HasAdded: true };
    public bool HasRemoved => Tool is { HasRemoved: true };
    public string AddedText => Tool?.AddedText ?? "";
    public string RemovedText => Tool?.RemovedText ?? "";
    public bool HasCommand => Tool is { Command.Length: > 0 };
    public string CommandText => Tool?.Command is { Length: > 0 } cmd ? $"$ {cmd}" : "";

    public string TickerStatText
    {
        get
        {
            if (StepKind == ConversationWorkStepKind.Tool && Tool is not null)
            {
                if (Tool.HasCounts)
                {
                    if (Tool.HasAdded && Tool.HasRemoved) return $"{Tool.AddedText} {Tool.RemovedText}";
                    return Tool.HasAdded ? Tool.AddedText : Tool.RemovedText;
                }
                if (Tool.HasExitCode) return Tool.ExitCodeText;
            }
            return "";
        }
    }

    public string MetaText
    {
        get
        {
            if (IsRunning) return MobileLangResources.Get(MobileLangResourceKind.ConversationStepRunning);
            if (StepKind == ConversationWorkStepKind.Tool && Tool is { HasExitCode: true }) return Tool.ExitCodeText;
            return "";
        }
    }

    public bool HasMeta => !string.IsNullOrEmpty(MetaText);

    public bool CanExpand => StepKind switch
    {
        ConversationWorkStepKind.Thought => !string.IsNullOrWhiteSpace(_thoughtText),
        ConversationWorkStepKind.Text => !string.IsNullOrWhiteSpace(_noteText),
        ConversationWorkStepKind.Plan => _planItems.Count > 0,
        _ => Tool is not null && (Tool.HasDetails || !string.IsNullOrEmpty(Tool.Command)),
    };

    /// <summary>Lazy detail binding: null when collapsed so Avalonia creates zero detail controls.</summary>
    public ConversationWorkStep? ExpandedDetail => IsExpanded && CanExpand ? this : null;

    partial void OnIsExpandedChanged(bool value)
    {
        if (Tool is not null && Tool.IsExpanded != value)
        {
            Tool.IsExpanded = value;
        }
        OnPropertyChanged(nameof(StatusMark));
        OnPropertyChanged(nameof(ExpandedDetail));
    }

    internal void UpdateThought(string text, bool isActive)
    {
        var changed = _thoughtText != text;
        var activeChanged = _isThoughtActive != isActive;
        _thoughtText = text;
        _isThoughtActive = isActive;
        if (changed)
        {
            _thoughtPreview = BuildThoughtPreview(text);
            OnPropertyChanged(nameof(ThoughtText));
            OnPropertyChanged(nameof(Target));
            OnPropertyChanged(nameof(CanExpand));
        }
        if (activeChanged)
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(MetaText));
            OnPropertyChanged(nameof(HasMeta));
        }
    }

    internal void UpdateText(string text)
    {
        var trimmed = text.Trim();
        if (_noteText == trimmed) return;
        _noteText = trimmed;
        _notePreview = BuildTextPreview(trimmed);
        OnPropertyChanged(nameof(NoteText));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(CanExpand));
    }

    internal void UpdatePlan(IReadOnlyList<ConversationPlanItem> items, string preview)
    {
        _planItems = items;
        _planPreview = preview;
        OnPropertyChanged(nameof(PlanItems));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(CanExpand));
    }

    internal void NotifyToolUpdated()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(StatusMark));
        OnPropertyChanged(nameof(Verb));
        OnPropertyChanged(nameof(ActiveVerb));
        OnPropertyChanged(nameof(DoneVerb));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(HasAdded));
        OnPropertyChanged(nameof(HasRemoved));
        OnPropertyChanged(nameof(AddedText));
        OnPropertyChanged(nameof(RemovedText));
        OnPropertyChanged(nameof(HasCommand));
        OnPropertyChanged(nameof(CommandText));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(HasMeta));
        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(ExpandedDetail));
    }

    private static string BuildThoughtPreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        if (end == 0) return "";
        var lineStart = text.LastIndexOf('\n', end - 1) + 1;
        var slice = text.AsSpan(lineStart, end - lineStart).Trim();
        if (slice.IsEmpty) slice = text.AsSpan(0, end).Trim();
        return slice.Length <= ThoughtPreviewMaxChars ? slice.ToString() : string.Concat(slice[..ThoughtPreviewMaxChars], "…");
    }

    private static string BuildTextPreview(string text)
    {
        var single = ToSingleLine(text);
        if (string.IsNullOrEmpty(single)) return "";
        return single.Length <= ThoughtPreviewMaxChars
            ? single
            : string.Concat(single.AsSpan(0, ThoughtPreviewMaxChars), "…");
    }

    private static string? ToSingleLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : string.Join(' ', parts);
    }

    private static string ResolveToolVerb(string? kind, bool active, bool done) => (kind, active, done) switch
    {
        ("read", true, _) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbReadActive),
        ("read", false, true) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbReadDone),
        ("read", _, _) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbRead),

        ("search", true, _) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbSearchActive),
        ("search", false, true) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbSearchDone),
        ("search", _, _) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbSearch),

        ("edit", true, _) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbEditActive),
        ("edit", false, true) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbEditDone),
        ("edit", _, _) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbEdit),

        ("delete", true, _) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbDeleteActive),
        ("delete", false, true) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbDeleteDone),
        ("delete", _, _) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbDelete),

        ("move", true, _) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbMoveActive),
        ("move", false, true) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbMoveDone),
        ("move", _, _) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbMove),

        ("execute", true, _) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbExecActive),
        ("execute", false, true) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbExecDone),
        ("execute", _, _) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbExec),

        ("fetch", true, _) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbFetchActive),
        ("fetch", false, true) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbFetchDone),
        ("fetch", _, _) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbFetch),

        ("think", true, _) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbThinkActive),
        ("think", false, true) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbThinkDone),
        ("think", _, _) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbThink),

        (_, true, _) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbOtherActive),
        (_, false, true) => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbOtherDone),
        _ => MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbOther),
    };
}

/// <summary>One fixed slot inside the mobile Scheme C 3-line sliding live ticker (mutated in place).</summary>
public sealed partial class ConversationTickerLine : ObservableObject
{
    [ObservableProperty] public partial bool IsVisible { get; set; }
    [ObservableProperty] public partial double RowOpacity { get; set; } = 1.0;
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial bool IsFailed { get; set; }
    [ObservableProperty] public partial bool IsThought { get; set; }
    [ObservableProperty] public partial string Verb { get; set; } = "";
    [ObservableProperty] public partial string Target { get; set; } = "";
    [ObservableProperty] public partial string StatText { get; set; } = "";

    public bool IsCompleted => !IsRunning && !IsFailed;
    public bool HasStat => !string.IsNullOrEmpty(StatText);

    public void SetFrom(ConversationWorkStep step, double opacity)
    {
        var running = step.IsRunning;
        var failed = step.IsFailed;
        IsVisible = true;
        RowOpacity = opacity;
        if (IsRunning != running) { IsRunning = running; OnPropertyChanged(nameof(IsCompleted)); }
        if (IsFailed != failed) { IsFailed = failed; OnPropertyChanged(nameof(IsCompleted)); }
        IsThought = step.IsThought;
        Verb = running ? step.ActiveVerb : step.DoneVerb;
        Target = step.Target;
        var stat = step.TickerStatText;
        if (StatText != stat) { StatText = stat; OnPropertyChanged(nameof(HasStat)); }
    }

    public void SetPreparing()
    {
        IsVisible = true;
        RowOpacity = 1.0;
        if (!IsRunning) { IsRunning = true; OnPropertyChanged(nameof(IsCompleted)); }
        if (IsFailed) { IsFailed = false; OnPropertyChanged(nameof(IsCompleted)); }
        IsThought = true;
        Verb = MobileLangResources.Get(MobileLangResourceKind.ConversationStepVerbThinkActive);
        Target = MobileLangResources.Get(MobileLangResourceKind.ConversationWorkPreparing);
        if (StatText.Length > 0) { StatText = ""; OnPropertyChanged(nameof(HasStat)); }
    }

    public void CopyFrom(ConversationTickerLine source)
    {
        IsVisible = source.IsVisible;
        RowOpacity = source.RowOpacity;
        if (IsRunning != source.IsRunning) { IsRunning = source.IsRunning; OnPropertyChanged(nameof(IsCompleted)); }
        if (IsFailed != source.IsFailed) { IsFailed = source.IsFailed; OnPropertyChanged(nameof(IsCompleted)); }
        IsThought = source.IsThought;
        Verb = source.Verb;
        Target = source.Target;
        if (StatText != source.StatText) { StatText = source.StatText; OnPropertyChanged(nameof(HasStat)); }
    }

    public void Clear()
    {
        IsVisible = false;
        if (IsRunning) { IsRunning = false; OnPropertyChanged(nameof(IsCompleted)); }
    }
}

/// <summary>Fixed 3-slot sliding live ticker container for mobile (Scheme C), mounted only while streaming &amp; collapsed.</summary>
public sealed class ConversationWorkTicker : IWorkTickerSource
{
    private static readonly object PreparingKey = new();

    private bool _hasSynced;
    private int _prevCount;
    private object? _prevKey0;
    private object? _prevKey1;
    private object? _prevKey2;

    public ConversationTickerLine Line0 { get; } = new();
    public ConversationTickerLine Line1 { get; } = new();
    public ConversationTickerLine Line2 { get; } = new();

    internal ConversationTickerLine Exit0 { get; } = new();
    internal ConversationTickerLine Exit1 { get; } = new();
    internal ConversationTickerLine Exit2 { get; } = new();

    public int VisibleCount => _prevCount;

    object IWorkTickerSource.Line0 => Line0;
    object IWorkTickerSource.Line1 => Line1;
    object IWorkTickerSource.Line2 => Line2;
    object IWorkTickerSource.Exit0 => Exit0;
    object IWorkTickerSource.Exit1 => Exit1;
    object IWorkTickerSource.Exit2 => Exit2;

    public event Action<int, int>? Updated;

    public void ClearExiting()
    {
        Exit0.Clear();
        Exit1.Clear();
        Exit2.Clear();
    }

    public void Sync(IReadOnlyList<ConversationWorkStep> steps)
    {
        ConversationWorkStep? s0 = null, s1 = null, s2 = null;
        var count = 0;

        var nonTextCount = 0;
        var runningCount = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].StepKind == ConversationWorkStepKind.Text) continue;
            nonTextCount++;
            if (steps[i].IsRunning) runningCount++;
        }

        var preparing = nonTextCount == 0;

        if (preparing)
        {
            count = 1;
        }
        else
        {
            void Push(ConversationWorkStep item)
            {
                if (count == 0) s0 = item;
                else if (count == 1) s1 = item;
                else s2 = item;
                count++;
            }

            if (runningCount >= 3)
            {
                var skipped = runningCount - 3;
                for (var i = 0; i < steps.Count && count < 3; i++)
                {
                    if (steps[i].StepKind == ConversationWorkStepKind.Text || !steps[i].IsRunning) continue;
                    if (skipped > 0) { skipped--; continue; }
                    Push(steps[i]);
                }
            }
            else if (runningCount > 0)
            {
                var doneSlots = 3 - runningCount;
                var totalDone = nonTextCount - runningCount;
                var skipDone = Math.Max(0, totalDone - doneSlots);
                for (var i = 0; i < steps.Count && count < doneSlots; i++)
                {
                    if (steps[i].StepKind == ConversationWorkStepKind.Text || steps[i].IsRunning) continue;
                    if (skipDone > 0) { skipDone--; continue; }
                    Push(steps[i]);
                }
                for (var i = 0; i < steps.Count && count < 3; i++)
                {
                    if (steps[i].StepKind != ConversationWorkStepKind.Text && steps[i].IsRunning) Push(steps[i]);
                }
            }
            else
            {
                var skip = Math.Max(0, nonTextCount - 3);
                for (var i = 0; i < steps.Count && count < 3; i++)
                {
                    if (steps[i].StepKind == ConversationWorkStepKind.Text) continue;
                    if (skip > 0) { skip--; continue; }
                    Push(steps[i]);
                }
            }
        }

        object? n0 = preparing ? PreparingKey : s0;
        object? n1 = preparing ? null : s1;
        object? n2 = preparing ? null : s2;

        var shift = _hasSynced
            ? WorkTickerShift.Compute(_prevCount, _prevKey0, _prevKey1, _prevKey2, count, n0, n1, n2)
            : 0;
        var prevCount = _prevCount;

        if (shift > 0)
        {
            ShiftExiting(shift);
        }

        if (preparing)
        {
            Line0.SetPreparing();
            Line1.Clear();
            Line2.Clear();
        }
        else if (count == 1)
        {
            Line0.SetFrom(s0!, 1.0);
            Line1.Clear();
            Line2.Clear();
        }
        else if (count == 2)
        {
            Line0.SetFrom(s0!, 0.65);
            Line1.SetFrom(s1!, 1.0);
            Line2.Clear();
        }
        else
        {
            Line0.SetFrom(s0!, 0.38);
            Line1.SetFrom(s1!, 0.66);
            Line2.SetFrom(s2!, 1.0);
        }

        var wasSynced = _hasSynced;
        _hasSynced = true;
        _prevCount = count;
        _prevKey0 = n0;
        _prevKey1 = n1;
        _prevKey2 = n2;

        if (wasSynced && (shift > 0 || prevCount != count))
        {
            Updated?.Invoke(shift, count);
        }
    }

    private void ShiftExiting(int shift)
    {
        if (shift == 1)
        {
            Exit2.CopyFrom(Exit1);
            Exit1.CopyFrom(Exit0);
            Exit0.CopyFrom(Line0);
        }
        else if (shift == 2)
        {
            Exit2.CopyFrom(Exit0);
            Exit1.CopyFrom(Line0);
            Exit0.CopyFrom(Line1);
        }
        else
        {
            Exit2.CopyFrom(Line0);
            Exit1.CopyFrom(Line1);
            Exit0.CopyFrom(Line2);
        }
    }
}

/// <summary>One tool call of a turn.</summary>
public sealed partial class ConversationTool(string id) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] public partial string Title { get; set; } = "";
    /// <summary>ACP's kind: <c>read</c>, <c>edit</c>, <c>delete</c>, <c>move</c>, <c>search</c>, <c>execute</c>, <c>think</c>, <c>fetch</c>, <c>other</c>.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEdit), nameof(IsExecute), nameof(IsRead), nameof(IsSearch), nameof(IsOther))]
    public partial string Kind { get; set; } = "other";
    /// <summary><c>pending</c>, <c>in_progress</c>, <c>completed</c> or <c>failed</c>.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsFailed), nameof(IsCompleted))]
    public partial string Status { get; set; } = "in_progress";
    [ObservableProperty] public partial string? Command { get; set; }
    [ObservableProperty] public partial string? Path { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCounts), nameof(HasAdded), nameof(AddedText))]
    public partial int Added { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCounts), nameof(HasRemoved), nameof(RemovedText))]
    public partial int Removed { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasExitCode), nameof(ExitCodeText))]
    public partial int? ExitCode { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOutput), nameof(HasDetails))]
    public partial string? Output { get; set; }
    /// <summary>The diff of an edit, one line each.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasDiff), nameof(HasDetails))]
    public partial IReadOnlyList<ConversationDiffLine> Diff { get; set; } = [];
    /// <summary>The diff or the output is shown.</summary>
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public bool IsRunning => Status is "pending" or "in_progress";
    public bool IsFailed => Status == "failed";
    public bool IsCompleted => Status == "completed";
    public bool IsEdit => Kind is "edit" or "delete" or "move";
    public bool IsExecute => Kind == "execute";
    public bool IsRead => Kind is "read" or "fetch";
    public bool IsSearch => Kind == "search";
    public bool IsOther => !IsEdit && !IsExecute && !IsRead && !IsSearch;
    public bool HasCounts => Added > 0 || Removed > 0;
    public bool HasAdded => Added > 0;
    public bool HasRemoved => Removed > 0;
    public string AddedText => "+" + Added;
    public string RemovedText => "−" + Removed;
    public bool HasExitCode => ExitCode is not null and not 0;
    public string ExitCodeText => ExitCode is { } code ? string.Format(MobileLangResources.Get(MobileLangResourceKind.ConversationExitCode), code) : "";
    public bool HasOutput => !string.IsNullOrEmpty(Output);
    public bool HasDiff => Diff.Count > 0;
    public bool HasDetails => HasOutput || HasDiff;
}

/// <summary>A line of an edit's diff: <c>+</c> added, <c>-</c> removed, anything else unchanged.</summary>
public sealed record ConversationDiffLine(string Text)
{
    public bool IsAdded => Text.StartsWith('+');
    public bool IsRemoved => Text.StartsWith('-');
}

/// <summary>One step of a turn's plan.</summary>
/// <param name="Status"><c>pending</c>, <c>in_progress</c> or <c>completed</c>.</param>
public sealed record ConversationPlanItem(string Content, string Status)
{
    public bool IsCompleted => Status == "completed";
    public bool IsInProgress => Status == "in_progress";
    public string StatusMark => Status switch
    {
        "completed" => "✓",
        "in_progress" => "◌",
        _ => "○",
    };
}

/// <summary>The agent asks to be allowed to do something; one button per option.</summary>
public sealed partial class ConversationPermissionEntry(string id) : ConversationEntry(id)
{
    [ObservableProperty] public partial string Title { get; set; } = "";
    /// <summary>What exactly it wants to do (a command, a path).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial string? Detail { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial string? Warning { get; set; }
    [ObservableProperty] public partial IReadOnlyList<ConversationPermissionOption> Options { get; set; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsOpen), nameof(ResolvedText))]
    public partial bool IsResolved { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ResolvedText))]
    public partial string? ResolvedSummary { get; set; }
    /// <summary>An answer was sent and the host has not closed the card yet: the buttons wait.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsOpen))]
    public partial bool IsAnswering { get; set; }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public bool HasWarning => !string.IsNullOrEmpty(Warning);
    /// <summary>The buttons are there to tap.</summary>
    public bool IsOpen => !IsResolved && !IsAnswering;
    public string ResolvedText => ResolvedSummary is { Length: > 0 } summary ? summary : MobileLangResources.Get(MobileLangResourceKind.ConversationResolved);
}

/// <summary>A button of a permission card.</summary>
public sealed record ConversationPermissionOption(ConversationPermissionEntry Card, string Id, string Label, string? Kind)
{
    /// <summary>Allowing: the filled button.</summary>
    public bool IsAllow => Kind is "allow_once";
    public bool IsReject => Kind is "reject_once" or "reject_always";
}

/// <summary>The agent asks the person one or more questions.</summary>
public sealed partial class ConversationQuestionEntry(string id) : ConversationEntry(id)
{
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<ConversationQuestionField> Fields { get; set; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsOpen), nameof(ResolvedText))]
    public partial bool IsResolved { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ResolvedText))]
    public partial string? ResolvedSummary { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsOpen))]
    public partial bool IsAnswering { get; set; }

    public bool IsOpen => !IsResolved && !IsAnswering;
    public string ResolvedText => ResolvedSummary is { Length: > 0 } summary ? summary : MobileLangResources.Get(MobileLangResourceKind.ConversationResolved);
}

/// <summary>One question of a question card, with what the person picked or typed so far.</summary>
public sealed partial class ConversationQuestionField : ObservableObject
{
    public ConversationQuestionField(string id, string prompt, bool multiSelect, bool allowFreeText, IEnumerable<ConversationOption> choices)
    {
        Id = id;
        Prompt = prompt;
        MultiSelect = multiSelect;
        AllowFreeText = allowFreeText;
        Choices = [.. choices.Select(choice => new ConversationChoice(choice.Id, choice.Label, choice.Detail))];
        foreach (var choice in Choices) choice.PropertyChanged += OnChoiceChanged;
    }

    public string Id { get; }
    public string Prompt { get; }
    public bool MultiSelect { get; }
    public bool SingleSelect => !MultiSelect;
    public bool AllowFreeText { get; }
    public IReadOnlyList<ConversationChoice> Choices { get; }
    [ObservableProperty] public partial string FreeText { get; set; } = "";

    public bool HasPrompt => Prompt.Length > 0;

    /// <summary>True when this is the same question as <paramref name="other"/> (same id, choices and kind): what was picked can stay.</summary>
    internal bool IsSameAs(ConversationQuestionField other) =>
        Id == other.Id && MultiSelect == other.MultiSelect && AllowFreeText == other.AllowFreeText &&
        Choices.Select(choice => (choice.Id, choice.Label)).SequenceEqual(other.Choices.Select(choice => (choice.Id, choice.Label)));

    // One answer only: picking one clears the others.
    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (MultiSelect || e.PropertyName != nameof(ConversationChoice.IsSelected) || sender is not ConversationChoice { IsSelected: true } picked) return;
        foreach (var choice in Choices)
        {
            if (!ReferenceEquals(choice, picked)) choice.IsSelected = false;
        }
    }
}

/// <summary>A choice of a question.</summary>
public sealed partial class ConversationChoice(string id, string label, string? detail) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public string? Detail { get; } = detail;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>What can be chosen above the input: model, mode (permissions) and effort, each with the current value.</summary>
public sealed partial class ConversationComposer : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasModels), nameof(ModelLabel), nameof(HasChoices))]
    public partial IReadOnlyList<ConversationOption> Models { get; set; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ModelLabel))]
    public partial string? Model { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasModes), nameof(ModeLabel), nameof(HasChoices))]
    public partial IReadOnlyList<ConversationOption> Modes { get; set; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ModeLabel))]
    public partial string? Mode { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasEfforts), nameof(EffortLabel), nameof(HasChoices))]
    public partial IReadOnlyList<ConversationOption> Efforts { get; set; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(EffortLabel))]
    public partial string? Effort { get; set; }

    public bool HasModels => Models.Count > 0;
    public bool HasModes => Modes.Count > 0;
    public bool HasEfforts => Efforts.Count > 0;
    public bool HasChoices => HasModels || HasModes || HasEfforts;
    public string ModelLabel => LabelOf(Models, Model);
    public string ModeLabel => LabelOf(Modes, Mode);
    public string EffortLabel => LabelOf(Efforts, Effort);

    /// <summary>The choices of one kind and the chosen one.</summary>
    public (IReadOnlyList<ConversationOption> Options, string? Current) Of(ConversationOptionKind kind) => kind switch
    {
        ConversationOptionKind.Model => (Models, Model),
        ConversationOptionKind.Mode => (Modes, Mode),
        _ => (Efforts, Effort),
    };

    // The chosen one's label; the first one's when the host names none (it uses its default).
    private static string LabelOf(IReadOnlyList<ConversationOption> options, string? id) =>
        options.FirstOrDefault(option => option.Id == id)?.Label ?? id ?? options.FirstOrDefault()?.Label ?? "";
}
