using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.Mobile.Localization;

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

/// <summary>One turn of the agent: what it thought, its answer (Markdown), its tool calls and its plan.</summary>
public sealed partial class ConversationAssistantEntry(string id) : ConversationEntry(id)
{
    /// <summary>The answer as Markdown, as the host has it so far.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasText))]
    public partial string Text { get; set; } = "";
    /// <summary>The answer cut into blocks for the page; follows <see cref="Text"/>.</summary>
    [ObservableProperty] public partial IReadOnlyList<ConversationTextBlock> Blocks { get; private set; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasThought))]
    public partial string? Thought { get; set; }
    /// <summary>The thought is shown in full; folded to one line otherwise.</summary>
    [ObservableProperty] public partial bool IsThoughtExpanded { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMeta))]
    public partial bool IsStreaming { get; set; }
    /// <summary>Why the turn failed.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; set; }
    /// <summary>How long the turn took, as the host words it ("12s").</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasDuration), nameof(HasMeta))]
    public partial string? Duration { get; set; }
    public ObservableCollection<ConversationTool> Tools { get; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPlan), nameof(PlanText))]
    public partial IReadOnlyList<ConversationPlanItem> Plan { get; set; } = [];

    public bool HasText => Text.Length > 0;
    public bool HasThought => !string.IsNullOrEmpty(Thought);
    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public bool HasDuration => !string.IsNullOrEmpty(Duration);
    public bool HasMeta => IsStreaming || HasDuration;
    public bool HasPlan => Plan.Count > 0;

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
