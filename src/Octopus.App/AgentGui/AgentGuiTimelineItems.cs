using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Acp.Protocol;
using Octopus.App.Localization;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.AgentGui;

/// <summary>Base class for every block rendered in the compact Style A document-flow timeline.</summary>
public abstract class AgentTimelineItem : ObservableObject;

/// <summary>A user-authored prompt rendered as a compact right-aligned capsule.</summary>
public sealed partial class UserTurnItem : AgentTimelineItem
{
    private readonly Action<string>? _onReuse;

    public UserTurnItem(string text, IReadOnlyList<AcpPromptAttachment>? attachments = null, Action<string>? onReuse = null)
    {
        Text = text;
        _onReuse = onReuse;
        if (attachments is { Count: > 0 })
        {
            foreach (var att in attachments)
            {
                Attachments.Add(new AttachmentChipViewModel(att));
            }
        }
    }

    public string Text { get; private set; }

    /// <summary>Message id from a replayed <c>user_message_chunk</c>, used to stitch chunks together.</summary>
    public string? MessageId { get; init; }

    /// <summary>Appends a replayed chunk of the same user message.</summary>
    public void Append(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        Text += delta;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(CanReuse));
    }

    public ObservableCollection<AttachmentChipViewModel> Attachments { get; } = [];
    public bool HasAttachments => Attachments.Count > 0;
    public bool CanReuse => _onReuse is not null && !string.IsNullOrWhiteSpace(Text);

    [RelayCommand]
    private void Reuse()
    {
        if (CanReuse) _onReuse!(Text);
    }
}

/// <summary>An attachment pill shown in the composer or inside a sent user turn.</summary>
public sealed class AttachmentChipViewModel
{
    private readonly Action? _removeAction;

    public AttachmentChipViewModel(AcpPromptAttachment attachment, Action<AttachmentChipViewModel>? onRemove = null)
    {
        Attachment = attachment;
        if (onRemove is not null)
        {
            _removeAction = () => onRemove(this);
        }
    }

    public AcpPromptAttachment Attachment { get; }
    public string Label => string.IsNullOrWhiteSpace(Attachment.DisplayName)
        ? Path.GetFileName(Attachment.Path)
        : Attachment.DisplayName;
    public LucideIconKind Icon => Attachment.Kind == "image" ? LucideIconKind.Image : LucideIconKind.FileText;
    public bool CanRemove => _removeAction is not null;
    public IRelayCommand RemoveChipCommand => new RelayCommand(() => _removeAction?.Invoke());

    public static AttachmentChipViewModel Removable(AcpPromptAttachment attachment, Action<AttachmentChipViewModel> onRemove) =>
        new(attachment, onRemove);
}

/// <summary>A single unified-diff line inside an expanded edit tool card.</summary>
public sealed record DiffLineViewModel(string Prefix, string Text, bool IsAdd, bool IsRemove);

/// <summary>A single plan checklist step reported via ACP <c>plan</c> updates.</summary>
public sealed class PlanEntryViewModel(AcpPlanEntry entry)
{
    public string Content { get; } = entry.Content;
    public string Status { get; } = entry.Status;
    public bool IsCompleted => Status == "completed";
    public bool IsInProgress => Status == "in_progress";
    public LucideIconKind Icon => Status switch
    {
        "completed" => LucideIconKind.CircleCheck,
        "in_progress" => LucideIconKind.Loader,
        _ => LucideIconKind.Circle,
    };
}

/// <summary>One tool execution row inside an assistant turn, updated in place by <see cref="ToolCallId"/>.</summary>
public sealed partial class ToolCallItemViewModel : ObservableObject
{
    private readonly List<string> _textOutputs = [];
    private readonly Action<string>? _openFile;
    private readonly Action<string>? _openDiff;
    private readonly Action<ToolCallItemViewModel>? _openScript;

    public ToolCallItemViewModel(
        string toolCallId,
        Action<string>? openFile = null,
        Action<string>? openDiff = null,
        Action<ToolCallItemViewModel>? openScript = null)
    {
        ToolCallId = toolCallId;
        _openFile = openFile;
        _openDiff = openDiff;
        _openScript = openScript;
    }

    public string ToolCallId { get; }

    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string? Description { get; set; }
    [ObservableProperty] public partial string Kind { get; set; } = "other";
    [ObservableProperty] public partial string Status { get; set; } = "in_progress";
    [ObservableProperty] public partial string? CommandText { get; set; }
    [ObservableProperty] public partial string? InputSummary { get; set; }
    [ObservableProperty] public partial string? Stdout { get; set; }
    [ObservableProperty] public partial string? Stderr { get; set; }
    [ObservableProperty] public partial int? ExitCode { get; set; }
    [ObservableProperty] public partial string OutputText { get; set; } = "";
    [ObservableProperty] public partial string? PrimaryPath { get; set; }
    [ObservableProperty] public partial int AddedLines { get; set; }
    [ObservableProperty] public partial int RemovedLines { get; set; }
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial bool IsBackground { get; set; }

    public ObservableCollection<DiffLineViewModel> DiffLines { get; } = [];

    public bool IsRunning => Status is "pending" or "in_progress";
    public bool IsCompleted => Status == "completed";
    public bool IsFailed => Status == "failed";
    public bool HasDiff => DiffLines.Count > 0;
    public bool HasAddedStats => AddedLines > 0;
    public bool HasRemovedStats => RemovedLines > 0;
    public string AddedStatsText => $"+{AddedLines}";
    public string RemovedStatsText => $"-{RemovedLines}";
    public string DiffStatsText => $"+{AddedLines} -{RemovedLines}";
    public bool HasCommand => !string.IsNullOrWhiteSpace(CommandText);
    public int CommandLineCount => string.IsNullOrWhiteSpace(CommandText) ? 0 : CommandText!.Replace("\r\n", "\n").Split('\n').Length;
    /// <summary>Bash Style 2: multi-line or long commands fold into a 1-line capsule above output with a full-script modal button.</summary>
    public bool IsLargeCommand => HasCommand && (CommandLineCount >= 4 || CommandText!.Length > 180);
    public bool IsCompactCommand => HasCommand && !IsLargeCommand;
    public string? ScriptLanguage => DetectScriptLanguage(CommandText);
    public bool HasScriptLanguage => IsLargeCommand && !string.IsNullOrWhiteSpace(ScriptLanguage);
    public string LinesBadgeText => CommandLineCount >= 2 ? AppLangResources.Format(AppLangResourceKind.AgentGuiBashLinesBadge, CommandLineCount) : "";
    public bool ShowLinesBadge => IsLargeCommand && CommandLineCount >= 2;
    public string LinesCollapsedText => AppLangResources.Format(AppLangResourceKind.AgentGuiBashLinesCollapsed, Math.Max(1, CommandLineCount));
    public string SingleLineCommandPreview => BuildSingleLineCommandPreview(CommandText) ?? "";
    public string? SecondaryHeaderPreview =>
        HasCommand && !string.IsNullOrWhiteSpace(Description)
            ? SingleLineCommandPreview
            : null;
    public bool HasSecondaryHeaderPreview => !string.IsNullOrWhiteSpace(SecondaryHeaderPreview);
    public bool HasInputSummary => !HasCommand && !string.IsNullOrWhiteSpace(InputSummary);
    public bool HasStdout => !string.IsNullOrWhiteSpace(Stdout);
    public bool HasStderr => !string.IsNullOrWhiteSpace(Stderr);
    public bool HasOutputText => !string.IsNullOrWhiteSpace(OutputText);
    public bool HasExitCode => ExitCode.HasValue;
    public string ExitCodeText => ExitCode.HasValue ? $"exit {ExitCode.Value}" : "";
    public bool HasDetails => HasDiff || HasCommand || HasInputSummary || HasStdout || HasStderr || HasOutputText;
    public bool CanExpand => HasDetails || IsRunning;
    public bool IsRunningWithoutOutput => IsRunning && !HasDiff && !HasStdout && !HasStderr && !HasOutputText;
    public bool CanOpenFile => _openFile is not null && !string.IsNullOrWhiteSpace(PrimaryPath);
    public bool CanOpenDiff => _openDiff is not null && !string.IsNullOrWhiteSpace(PrimaryPath) && (HasDiff || Kind is "edit" or "delete" or "move");
    public bool CanOpenScriptPreview => _openScript is not null && IsLargeCommand;
    public bool IsReadOnlyExploration => Status == "completed" && Kind is "read" or "search" or "fetch" or "think";
    public bool IsCompletedCommand => Status == "completed" && !IsBackground && (Kind == "execute" || (Kind == "other" && HasCommand)) && ExitCode is null or 0;

    public string CopyableOutput
    {
        get
        {
            var parts = new List<string>(5);
            if (!string.IsNullOrWhiteSpace(CommandText)) parts.Add($"$ {CommandText}");
            else if (!string.IsNullOrWhiteSpace(InputSummary)) parts.Add(InputSummary!);
            if (DiffLines.Count > 0) parts.Add(string.Join("\n", DiffLines.Select(l => $"{l.Prefix}{l.Text}")));
            if (!string.IsNullOrWhiteSpace(Stdout)) parts.Add(Stdout!);
            if (!string.IsNullOrWhiteSpace(Stderr)) parts.Add(Stderr!);
            if (!string.IsNullOrWhiteSpace(OutputText)) parts.Add(OutputText);
            return string.Join("\n", parts);
        }
    }

    /// <summary>Always a clean single-line header: never explodes the collapsed card on multi-line Bash scripts.</summary>
    public string DisplayTitle
    {
        get
        {
            if (HasCommand && !string.IsNullOrWhiteSpace(Description))
            {
                return ToSingleLine(Description)!;
            }
            if (!string.IsNullOrWhiteSpace(Title))
            {
                var singleTitle = ToSingleLine(Title)!;
                // When the adapter stuffed the raw multiline command into Title, prefer the smart single-line command preview.
                if (HasCommand && Title.Contains('\n'))
                {
                    return SingleLineCommandPreview.Length > 0 ? SingleLineCommandPreview : singleTitle;
                }
                return singleTitle;
            }
            if (HasCommand) return SingleLineCommandPreview;
            if (!string.IsNullOrWhiteSpace(PrimaryPath)) return ToSingleLine(PrimaryPath)!;
            return Kind;
        }
    }

    public string StatusText => Status switch
    {
        "completed" => AppLangResources.Get(AppLangResourceKind.AgentGuiToolDone),
        "failed" => AppLangResources.Get(AppLangResourceKind.AgentGuiToolFailed),
        _ => AppLangResources.Get(AppLangResourceKind.AgentGuiToolRunning),
    };

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

    [RelayCommand]
    private void ToggleExpanded()
    {
        if (CanExpand) IsExpanded = !IsExpanded;
    }

    [RelayCommand]
    private void OpenFile()
    {
        if (CanOpenFile)
        {
            _openFile!(PrimaryPath!);
        }
    }

    [RelayCommand]
    private void OpenDiff()
    {
        if (CanOpenDiff)
        {
            _openDiff!(PrimaryPath!);
        }
    }

    [RelayCommand]
    private void OpenScriptPreview()
    {
        if (CanOpenScriptPreview)
        {
            _openScript!(this);
        }
    }

    public void Apply(AcpToolCallUpdate update)
    {
        if (!string.IsNullOrWhiteSpace(update.Kind)) Kind = update.Kind;
        if (!string.IsNullOrWhiteSpace(update.Title)) Title = update.Title;
        if (!string.IsNullOrWhiteSpace(update.Description)) Description = update.Description;
        if (!string.IsNullOrWhiteSpace(update.Status)) Status = update.Status;
        if (!string.IsNullOrWhiteSpace(update.Command)) CommandText = update.Command;
        if (update.IsBackground) IsBackground = true;
        if (update.Stdout is not null) Stdout = update.Stdout;
        if (update.Stderr is not null) Stderr = update.Stderr;
        if (update.ExitCode.HasValue) ExitCode = update.ExitCode;

        if (update.Locations.Count > 0 && string.IsNullOrEmpty(PrimaryPath))
        {
            PrimaryPath = update.Locations[0].Path;
        }

        if (string.IsNullOrEmpty(InputSummary) && update.RawInput is { ValueKind: System.Text.Json.JsonValueKind.Object } rawIn)
        {
            InputSummary = SummarizeRawInput(rawIn);
        }

        if (update.TextOutputs.Count > 0)
        {
            _textOutputs.AddRange(update.TextOutputs);
            OutputText = string.Join("\n", _textOutputs);
        }
        else if (string.IsNullOrEmpty(Stdout) && string.IsNullOrEmpty(OutputText)
            && update.RawOutput is { ValueKind: System.Text.Json.JsonValueKind.String } rawOutStr
            && rawOutStr.GetString() is { Length: > 0 } rawStr)
        {
            OutputText = rawStr;
        }

        if (update.Diffs.Count > 0)
        {
            DiffLines.Clear();
            var added = 0;
            var removed = 0;
            foreach (var diff in update.Diffs)
            {
                if (string.IsNullOrEmpty(PrimaryPath) && !string.IsNullOrEmpty(diff.Path))
                {
                    PrimaryPath = diff.Path;
                }
                var lines = BuildSimpleDiff(diff.OldText, diff.NewText, ref added, ref removed);
                foreach (var l in lines) DiffLines.Add(l);
            }
            AddedLines = added;
            RemovedLines = removed;
        }

        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(KindIcon));
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(HasAddedStats));
        OnPropertyChanged(nameof(HasRemovedStats));
        OnPropertyChanged(nameof(AddedStatsText));
        OnPropertyChanged(nameof(RemovedStatsText));
        OnPropertyChanged(nameof(DiffStatsText));
        OnPropertyChanged(nameof(HasCommand));
        OnPropertyChanged(nameof(CommandLineCount));
        OnPropertyChanged(nameof(IsLargeCommand));
        OnPropertyChanged(nameof(IsCompactCommand));
        OnPropertyChanged(nameof(ScriptLanguage));
        OnPropertyChanged(nameof(HasScriptLanguage));
        OnPropertyChanged(nameof(LinesBadgeText));
        OnPropertyChanged(nameof(ShowLinesBadge));
        OnPropertyChanged(nameof(LinesCollapsedText));
        OnPropertyChanged(nameof(SingleLineCommandPreview));
        OnPropertyChanged(nameof(SecondaryHeaderPreview));
        OnPropertyChanged(nameof(HasSecondaryHeaderPreview));
        OnPropertyChanged(nameof(HasInputSummary));
        OnPropertyChanged(nameof(HasStdout));
        OnPropertyChanged(nameof(HasStderr));
        OnPropertyChanged(nameof(HasOutputText));
        OnPropertyChanged(nameof(HasExitCode));
        OnPropertyChanged(nameof(ExitCodeText));
        OnPropertyChanged(nameof(HasDetails));
        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(IsRunningWithoutOutput));
        OnPropertyChanged(nameof(CanOpenFile));
        OnPropertyChanged(nameof(CanOpenDiff));
        OnPropertyChanged(nameof(CanOpenScriptPreview));
        OnPropertyChanged(nameof(IsReadOnlyExploration));
        OnPropertyChanged(nameof(IsCompletedCommand));
        OnPropertyChanged(nameof(CopyableOutput));
    }

    internal static string? ToSingleLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : string.Join(' ', parts);
    }

    internal static string? BuildSingleLineCommandPreview(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var lines = command.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return null;
        if (lines.Length == 1) return lines[0];
        var first = lines[0];
        return first.Length <= 20 && lines.Length > 1
            ? $"{first} {lines[1]}"
            : first;
    }

    private static string? DetectScriptLanguage(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var firstLine = command.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(firstLine)) return null;
        var tok = firstLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(tok)) return null;
        var exe = Path.GetFileName(tok).ToLowerInvariant();
        return exe switch
        {
            "python3" or "python" or "node" or "bash" or "sh" or "zsh" or "pwsh" or "powershell" or "ruby" or "perl" or "jq" or "dotnet" or "npm" or "pnpm" or "git" or "docker" => exe,
            _ => "bash",
        };
    }

    private static string? SummarizeRawInput(System.Text.Json.JsonElement obj)
    {
        var lines = new List<string>(6);
        foreach (var prop in obj.EnumerateObject())
        {
            if (lines.Count >= 6) break;
            if (prop.Name is "WaitMsBeforeAsync" or "toolSummary") continue;
            var val = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                ? prop.Value.GetString()
                : prop.Value.GetRawText();
            if (string.IsNullOrWhiteSpace(val)) continue;
            if (val.Length > 240) val = val[..240] + "…";
            lines.Add($"{prop.Name}: {val}");
        }
        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    private static List<DiffLineViewModel> BuildSimpleDiff(string? oldText, string newText, ref int added, ref int removed)
    {
        var result = new List<DiffLineViewModel>();
        var oldLines = string.IsNullOrEmpty(oldText) ? [] : oldText.Replace("\r\n", "\n").Split('\n');
        var newLines = string.IsNullOrEmpty(newText) ? [] : newText.Replace("\r\n", "\n").Split('\n');

        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix])
        {
            prefix++;
        }
        var suffix = 0;
        while (suffix < oldLines.Length - prefix
            && suffix < newLines.Length - prefix
            && oldLines[oldLines.Length - 1 - suffix] == newLines[newLines.Length - 1 - suffix])
        {
            suffix++;
        }

        var contextStart = Math.Max(0, prefix - 2);
        for (var i = contextStart; i < prefix; i++)
        {
            result.Add(new DiffLineViewModel(" ", oldLines[i], false, false));
        }
        for (var i = prefix; i < oldLines.Length - suffix; i++)
        {
            result.Add(new DiffLineViewModel("-", oldLines[i], false, true));
            removed++;
        }
        for (var i = prefix; i < newLines.Length - suffix; i++)
        {
            result.Add(new DiffLineViewModel("+", newLines[i], true, false));
            added++;
        }
        var contextEnd = Math.Min(newLines.Length, newLines.Length - suffix + 2);
        for (var i = newLines.Length - suffix; i < contextEnd; i++)
        {
            result.Add(new DiffLineViewModel(" ", newLines[i], false, false));
        }

        if (result.Count > 120)
        {
            result = [.. result.Take(120), new DiffLineViewModel(" ", $"… ({result.Count - 120} more lines)", false, false)];
        }
        return result;
    }
}

/// <summary>Base class for chronological content segments inside an <see cref="AssistantTurnItem"/>.</summary>
public abstract class AssistantTurnBlock : ObservableObject;

/// <summary>An independent collapsible Thinking segment in an assistant turn's chronological stream.</summary>
public sealed partial class ThoughtBlockViewModel : AssistantTurnBlock
{
    public ThoughtBlockViewModel(string initialText, string? messageId = null)
    {
        MessageId = messageId;
        Text = initialText;
    }

    public string? MessageId { get; internal set; }

    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public string Header =>
        $"{AppLangResources.Get(AppLangResourceKind.AgentGuiThinkingLabel)} · {Text.Length}";

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    public void Append(string delta)
    {
        Text += delta;
        OnPropertyChanged(nameof(Header));
    }
}

/// <summary>An independent Markdown response segment in an assistant turn's chronological stream.</summary>
public sealed partial class MarkdownBlockViewModel : AssistantTurnBlock
{
    public MarkdownBlockViewModel(string initialText, string? messageId = null)
    {
        MessageId = messageId;
        Text = initialText;
    }

    public string? MessageId { get; internal set; }

    [ObservableProperty] public partial string Text { get; set; } = "";

    public void Append(string delta) => Text += delta;
}

/// <summary>A contiguous burst of tool executions in an assistant turn, with local exploration and command folding.</summary>
public sealed partial class ToolRunBlockViewModel : AssistantTurnBlock
{
    [ObservableProperty] public partial bool IsExplorationExpanded { get; set; }
    [ObservableProperty] public partial bool IsCommandsExpanded { get; set; }

    public ObservableCollection<ToolCallItemViewModel> ToolCalls { get; } = [];
    public ObservableCollection<ToolCallItemViewModel> ExplorationTools { get; } = [];
    public ObservableCollection<ToolCallItemViewModel> CommandTools { get; } = [];
    public ObservableCollection<ToolCallItemViewModel> PrimaryTools { get; } = [];

    public bool HasExplorationGroup => ExplorationTools.Count > 0;
    public bool HasCommandGroup => CommandTools.Count > 0;
    public bool HasPrimaryTools => PrimaryTools.Count > 0;

    public string ExplorationSummary => FormatExplorationSummary(ExplorationTools);

    public string CommandGroupSummary =>
        CommandTools.Count == 0
            ? ""
            : AppLangResources.Format(AppLangResourceKind.AgentGuiCommandGroupSummary, CommandTools.Count);

    [RelayCommand]
    private void ToggleExploration() => IsExplorationExpanded = !IsExplorationExpanded;

    [RelayCommand]
    private void ToggleCommands() => IsCommandsExpanded = !IsCommandsExpanded;

    public void AddTool(ToolCallItemViewModel vm)
    {
        ToolCalls.Add(vm);
        RebuildToolGroups();
    }

    public void RebuildToolGroups()
    {
        PartitionTools(ToolCalls, ExplorationTools, CommandTools, PrimaryTools);
        OnPropertyChanged(nameof(HasExplorationGroup));
        OnPropertyChanged(nameof(HasCommandGroup));
        OnPropertyChanged(nameof(HasPrimaryTools));
        OnPropertyChanged(nameof(ExplorationSummary));
        OnPropertyChanged(nameof(CommandGroupSummary));
    }

    internal static string FormatExplorationSummary(IReadOnlyCollection<ToolCallItemViewModel> tools)
    {
        if (tools.Count == 0) return "";
        var reads = tools.Count(t => t.Kind == "read");
        var searches = tools.Count(t => t.Kind == "search");
        var fetches = tools.Count(t => t.Kind == "fetch");
        var others = tools.Count - reads - searches - fetches;
        var parts = new List<string>(4);
        if (reads > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiExploreRead, reads));
        if (searches > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiExploreSearch, searches));
        if (fetches > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiExploreFetch, fetches));
        if (others > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiExploreOther, others));
        return AppLangResources.Format(AppLangResourceKind.AgentGuiExplorationSummary, tools.Count, string.Join(" · ", parts));
    }

    internal static void PartitionTools(
        IReadOnlyList<ToolCallItemViewModel> all,
        ObservableCollection<ToolCallItemViewModel> explorationTarget,
        ObservableCollection<ToolCallItemViewModel> commandTarget,
        ObservableCollection<ToolCallItemViewModel> primaryTarget)
    {
        var completedExplorations = all.Where(t => t.IsReadOnlyExploration).ToList();
        var nextExplore = completedExplorations.Count >= 2 ? completedExplorations : [];

        var completedCommands = all.Where(t => t.IsCompletedCommand).ToList();
        var nextCommands = completedCommands.Count >= 2 ? completedCommands : [];

        var foldedSet = new HashSet<ToolCallItemViewModel>(nextExplore);
        foreach (var cmd in nextCommands) foldedSet.Add(cmd);

        var nextPrimary = foldedSet.Count > 0
            ? all.Where(t => !foldedSet.Contains(t)).ToList()
            : [.. all];

        SyncCollection(explorationTarget, nextExplore);
        SyncCollection(commandTarget, nextCommands);
        SyncCollection(primaryTarget, nextPrimary);
    }

    /// <summary>
    /// Updates <paramref name="target"/> in place without clearing existing items so live tool cards
    /// preserve their visual tree, pointer capture, and expanded state during streaming execution.
    /// </summary>
    internal static void SyncCollection(ObservableCollection<ToolCallItemViewModel> target, List<ToolCallItemViewModel> desired)
    {
        if (target.Count == desired.Count && target.SequenceEqual(desired)) return;

        var desiredSet = desired.ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < target.Count && ReferenceEquals(target[i], item)) continue;
            var existingIdx = target.IndexOf(item);
            if (existingIdx >= 0)
            {
                target.Move(existingIdx, i);
            }
            else
            {
                target.Insert(i, item);
            }
        }
    }
}

/// <summary>An assistant response turn rendered in compact Style A editorial document flow.</summary>
public sealed partial class AssistantTurnItem : AgentTimelineItem
{
    private static readonly AppLangResourceKind[] WorkingPhrases =
    [
        AppLangResourceKind.AgentGuiWorkingPhrase01,
        AppLangResourceKind.AgentGuiWorkingPhrase02,
        AppLangResourceKind.AgentGuiWorkingPhrase03,
        AppLangResourceKind.AgentGuiWorkingPhrase04,
        AppLangResourceKind.AgentGuiWorkingPhrase05,
        AppLangResourceKind.AgentGuiWorkingPhrase06,
        AppLangResourceKind.AgentGuiWorkingPhrase07,
        AppLangResourceKind.AgentGuiWorkingPhrase08,
        AppLangResourceKind.AgentGuiWorkingPhrase09,
        AppLangResourceKind.AgentGuiWorkingPhrase10,
        AppLangResourceKind.AgentGuiWorkingPhrase11,
        AppLangResourceKind.AgentGuiWorkingPhrase12,
    ];

    private const int PhraseRotateIntervalSeconds = 4;

    private readonly Dictionary<string, (ToolCallItemViewModel Tool, ToolRunBlockViewModel Owner)> _toolsById = new(StringComparer.Ordinal);
    private readonly Action<string>? _openFile;
    private readonly Action<string>? _openDiff;
    private readonly Action<ToolCallItemViewModel>? _openScript;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private DispatcherTimer? _statusTimer;
    private int _phraseIndex = Random.Shared.Next(WorkingPhrases.Length);
    private int _statusTicks;

    public AssistantTurnItem(
        string agentId,
        string agentName,
        Action<string>? openFile = null,
        Action<string>? openDiff = null,
        Action<ToolCallItemViewModel>? openScript = null)
    {
        AgentId = agentId;
        AgentName = agentName;
        _openFile = openFile;
        _openDiff = openDiff;
        _openScript = openScript;
        StartStatusTimer();
    }

    public string AgentId { get; }
    public string AgentName { get; }

    [ObservableProperty] public partial string ThoughtText { get; set; } = "";
    [ObservableProperty] public partial bool IsThoughtExpanded { get; set; }
    [ObservableProperty] public partial bool IsExplorationExpanded { get; set; }
    [ObservableProperty] public partial bool IsCommandsExpanded { get; set; }
    [ObservableProperty] public partial string MarkdownText { get; set; } = "";
    [ObservableProperty] public partial string? PlanMarkdown { get; set; }
    [ObservableProperty] public partial bool IsStreaming { get; set; } = true;
    [ObservableProperty] public partial bool IsWaitingForInput { get; set; }
    [ObservableProperty] public partial string? ErrorText { get; set; }
    [ObservableProperty] public partial string DurationText { get; set; } = "";
    [ObservableProperty] public partial string LiveElapsedText { get; set; } = "";

    /// <summary>Chronological stream of thinking, tool execution, and markdown response segments.</summary>
    public ObservableCollection<AssistantTurnBlock> Blocks { get; } = [];
    public ObservableCollection<PlanEntryViewModel> PlanEntries { get; } = [];
    public ObservableCollection<ToolCallItemViewModel> ToolCalls { get; } = [];
    /// <summary>Completed read-only exploration steps across the turn once >= 2 accumulate.</summary>
    public ObservableCollection<ToolCallItemViewModel> ExplorationTools { get; } = [];
    /// <summary>Completed successful shell commands across the turn once >= 2 accumulate.</summary>
    public ObservableCollection<ToolCallItemViewModel> CommandTools { get; } = [];
    /// <summary>Prominent tools across the turn (running, failed, edits, or a lone exploration/command step).</summary>
    public ObservableCollection<ToolCallItemViewModel> PrimaryTools { get; } = [];

    public bool HasThought => !string.IsNullOrWhiteSpace(ThoughtText);
    public bool HasPlan => PlanEntries.Count > 0 || !string.IsNullOrWhiteSpace(PlanMarkdown);
    public bool HasPlanEntries => PlanEntries.Count > 0;
    public bool HasPlanMarkdown => !string.IsNullOrWhiteSpace(PlanMarkdown);
    public bool HasToolCalls => ToolCalls.Count > 0;
    public bool HasExplorationGroup => ExplorationTools.Count > 0;
    public bool HasCommandGroup => CommandTools.Count > 0;
    public bool HasPrimaryTools => PrimaryTools.Count > 0;
    public bool HasMarkdown => !string.IsNullOrWhiteSpace(MarkdownText);
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public bool HasDuration => !string.IsNullOrEmpty(DurationText);
    public bool CanCopyReply => !IsStreaming && HasMarkdown;
    public bool ShowLiveStatus => IsStreaming && !IsWaitingForInput;
    public bool HasLiveElapsed => !string.IsNullOrEmpty(LiveElapsedText);
    public string LiveStatusText => AppLangResources.Get(WorkingPhrases[_phraseIndex]);

    public string ThoughtHeader =>
        $"{AppLangResources.Get(AppLangResourceKind.AgentGuiThinkingLabel)} · {ThoughtText.Length}";

    public string ExplorationSummary => ToolRunBlockViewModel.FormatExplorationSummary(ExplorationTools);

    public string CommandGroupSummary =>
        CommandTools.Count == 0
            ? ""
            : AppLangResources.Format(AppLangResourceKind.AgentGuiCommandGroupSummary, CommandTools.Count);

    [RelayCommand]
    private void ToggleThought()
    {
        IsThoughtExpanded = !IsThoughtExpanded;
        foreach (var thought in Blocks.OfType<ThoughtBlockViewModel>())
        {
            thought.IsExpanded = IsThoughtExpanded;
        }
    }

    [RelayCommand]
    private void ToggleExploration()
    {
        IsExplorationExpanded = !IsExplorationExpanded;
        foreach (var group in Blocks.OfType<ToolRunBlockViewModel>())
        {
            group.IsExplorationExpanded = IsExplorationExpanded;
        }
    }

    [RelayCommand]
    private void ToggleCommands()
    {
        IsCommandsExpanded = !IsCommandsExpanded;
        foreach (var group in Blocks.OfType<ToolRunBlockViewModel>())
        {
            group.IsCommandsExpanded = IsCommandsExpanded;
        }
    }

    public void SetWaitingForInput(bool waiting)
    {
        if (IsWaitingForInput == waiting) return;
        IsWaitingForInput = waiting;
        OnPropertyChanged(nameof(ShowLiveStatus));
    }

    /// <summary>Advances the live status phrase and refreshes the elapsed counter.</summary>
    public void AdvanceLiveStatus()
    {
        if (!IsStreaming) return;
        _phraseIndex = (_phraseIndex + 1) % WorkingPhrases.Length;
        OnPropertyChanged(nameof(LiveStatusText));
        UpdateLiveElapsed();
    }

    private void StartStatusTimer()
    {
        if (Application.Current is null || !Dispatcher.UIThread.CheckAccess()) return;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += OnStatusTimerTick;
        _statusTimer.Start();
    }

    private void StopStatusTimer()
    {
        if (_statusTimer is null) return;
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimerTick;
        _statusTimer = null;
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        if (!IsStreaming)
        {
            StopStatusTimer();
            return;
        }
        UpdateLiveElapsed();
        if (!IsWaitingForInput)
        {
            _statusTicks++;
            if (_statusTicks % PhraseRotateIntervalSeconds == 0)
            {
                _phraseIndex = (_phraseIndex + 1) % WorkingPhrases.Length;
                OnPropertyChanged(nameof(LiveStatusText));
            }
        }
    }

    private void UpdateLiveElapsed()
    {
        var secs = (int)_elapsed.Elapsed.TotalSeconds;
        var next = secs switch
        {
            < 1 => "",
            < 60 => $"{secs}s",
            _ => $"{secs / 60}m {secs % 60:00}s",
        };
        if (LiveElapsedText != next)
        {
            LiveElapsedText = next;
            OnPropertyChanged(nameof(HasLiveElapsed));
        }
    }

    public void AppendThought(string delta, string? messageId = null)
    {
        if (string.IsNullOrEmpty(delta)) return;

        if (Blocks.Count > 0
            && Blocks[^1] is ThoughtBlockViewModel lastThought
            && CanContinueMessage(lastThought.MessageId, messageId))
        {
            if (string.IsNullOrEmpty(lastThought.MessageId) && !string.IsNullOrEmpty(messageId))
            {
                lastThought.MessageId = messageId;
            }
            lastThought.Append(delta);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(delta)) return;
            Blocks.Add(new ThoughtBlockViewModel(delta, messageId));
        }

        ThoughtText = string.Join("\n\n", Blocks.OfType<ThoughtBlockViewModel>().Select(b => b.Text));
        OnPropertyChanged(nameof(HasThought));
        OnPropertyChanged(nameof(ThoughtHeader));
    }

    public void AppendMessage(string delta, string? messageId = null)
    {
        if (string.IsNullOrEmpty(delta)) return;

        if (Blocks.Count > 0
            && Blocks[^1] is MarkdownBlockViewModel lastMarkdown
            && CanContinueMessage(lastMarkdown.MessageId, messageId))
        {
            if (string.IsNullOrEmpty(lastMarkdown.MessageId) && !string.IsNullOrEmpty(messageId))
            {
                lastMarkdown.MessageId = messageId;
            }
            lastMarkdown.Append(delta);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(delta)) return;
            Blocks.Add(new MarkdownBlockViewModel(delta, messageId));
        }

        MarkdownText = string.Join("\n\n", Blocks.OfType<MarkdownBlockViewModel>().Select(b => b.Text));
        OnPropertyChanged(nameof(HasMarkdown));
        OnPropertyChanged(nameof(CanCopyReply));
    }

    private static bool CanContinueMessage(string? existingId, string? incomingId) =>
        string.IsNullOrEmpty(existingId)
        || string.IsNullOrEmpty(incomingId)
        || string.Equals(existingId, incomingId, StringComparison.Ordinal);

    public void UpsertToolCall(AcpToolCallUpdate update)
    {
        var id = string.IsNullOrWhiteSpace(update.ToolCallId) ? $"tool-{ToolCalls.Count + 1}" : update.ToolCallId;
        if (!_toolsById.TryGetValue(id, out var entry))
        {
            var vm = new ToolCallItemViewModel(id, _openFile, _openDiff, _openScript);
            ToolRunBlockViewModel owner;
            if (Blocks.Count > 0 && Blocks[^1] is ToolRunBlockViewModel trailingRun)
            {
                owner = trailingRun;
            }
            else
            {
                owner = new ToolRunBlockViewModel();
                Blocks.Add(owner);
            }
            entry = (vm, owner);
            _toolsById[id] = entry;
            ToolCalls.Add(vm);
            vm.Apply(update);
            owner.AddTool(vm);
            OnPropertyChanged(nameof(HasToolCalls));
        }
        else
        {
            entry.Tool.Apply(update);
            entry.Owner.RebuildToolGroups();
        }
        RebuildToolGroups();
    }

    private void RebuildToolGroups()
    {
        ToolRunBlockViewModel.PartitionTools(ToolCalls, ExplorationTools, CommandTools, PrimaryTools);
        OnPropertyChanged(nameof(HasExplorationGroup));
        OnPropertyChanged(nameof(HasCommandGroup));
        OnPropertyChanged(nameof(HasPrimaryTools));
        OnPropertyChanged(nameof(ExplorationSummary));
        OnPropertyChanged(nameof(CommandGroupSummary));
    }

    public void SetPlan(IReadOnlyList<AcpPlanEntry> entries, string? markdown)
    {
        PlanEntries.Clear();
        foreach (var e in entries) PlanEntries.Add(new PlanEntryViewModel(e));
        if (!string.IsNullOrWhiteSpace(markdown)) PlanMarkdown = markdown;
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasPlanEntries));
        OnPropertyChanged(nameof(HasPlanMarkdown));
    }

    public void Complete(string? error = null, bool showDuration = true)
    {
        StopStatusTimer();
        IsStreaming = false;
        IsWaitingForInput = false;
        _elapsed.Stop();
        if (showDuration && _elapsed.Elapsed.TotalMilliseconds >= 100)
        {
            DurationText = _elapsed.Elapsed.TotalSeconds < 60
                ? $"{_elapsed.Elapsed.TotalSeconds:0.#}s"
                : $"{(int)_elapsed.Elapsed.TotalMinutes}m {_elapsed.Elapsed.Seconds}s";
            OnPropertyChanged(nameof(HasDuration));
        }
        if (!string.IsNullOrWhiteSpace(error))
        {
            ErrorText = error;
            OnPropertyChanged(nameof(HasError));
        }
        OnPropertyChanged(nameof(CanCopyReply));
        OnPropertyChanged(nameof(ShowLiveStatus));
    }
}

/// <summary>An actionable permission option button inside <see cref="PermissionCardItem"/>.</summary>
public sealed class PermissionActionViewModel(AcpPermissionOption option, Action<string> choose)
{
    public string OptionId { get; } = option.OptionId;
    public string Label { get; } = string.IsNullOrWhiteSpace(option.Name) ? option.OptionId : option.Name;
    /// <summary>ACP's kind of the option (<c>allow_once</c>, <c>reject_always</c>…), for a device that shows the card.</summary>
    public string Kind { get; } = option.Kind;
    public bool IsPrimary => option.Kind == "allow_once";
    public bool IsDanger => option.Kind is "reject_once" or "reject_always";
    public ButtonVariant Variant => option.Kind switch
    {
        "allow_once" => ButtonVariant.Primary,
        "reject_once" or "reject_always" => ButtonVariant.Ghost,
        _ => ButtonVariant.Secondary,
    };
    public IRelayCommand ChooseCommand { get; } = new RelayCommand(() => choose(option.OptionId));
}

/// <summary>Interactive inline permission approval card (never auto-approved when user confirmation is required).</summary>
public sealed partial class PermissionCardItem : AgentTimelineItem
{
    private readonly Action<string?> _resolve;

    public PermissionCardItem(AcpPermissionRequest request, Action<string?> resolve)
    {
        _resolve = resolve;
        var rawTitle = request.ToolCall.Title?.Trim();
        var rawDetail = (request.ActionDetail ?? request.ToolCall.Command ?? request.ToolCall.Locations.FirstOrDefault()?.Path)?.Trim();
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            Title = AppLangResources.Get(AppLangResourceKind.AgentGuiPermissionTitle);
            ActionDetail = rawDetail;
        }
        else if (!string.IsNullOrEmpty(rawDetail) && string.Equals(rawTitle, rawDetail, StringComparison.Ordinal))
        {
            // Avoid repeating an identical shell command in both Title and ActionDetail.
            Title = AppLangResources.Get(AppLangResourceKind.AgentGuiPermissionTitle);
            ActionDetail = rawDetail;
        }
        else
        {
            Title = rawTitle;
            ActionDetail = rawDetail;
        }
        WarningMessage = request.WarningMessage;
        foreach (var opt in request.Options)
        {
            Actions.Add(new PermissionActionViewModel(opt, Select));
        }
    }

    public string Title { get; }
    public string? ActionDetail { get; }
    public string? WarningMessage { get; }
    public bool HasActionDetail => !string.IsNullOrWhiteSpace(ActionDetail);
    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningMessage);
    public ObservableCollection<PermissionActionViewModel> Actions { get; } = [];

    [ObservableProperty] public partial bool IsResolved { get; set; }
    [ObservableProperty] public partial string ResolvedSummary { get; set; } = "";

    private void Select(string optionId)
    {
        if (IsResolved) return;
        IsResolved = true;
        var chosen = Actions.FirstOrDefault(a => a.OptionId == optionId)?.Label ?? optionId;
        ResolvedSummary = chosen;
        _resolve(optionId);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsResolved) return;
        IsResolved = true;
        ResolvedSummary = AppLangResources.Get(AppLangResourceKind.AgentGuiPermissionCancel);
        _resolve(null);
    }
}

/// <summary>One selectable choice chip inside a <see cref="QuestionFieldViewModel"/>.</summary>
public sealed partial class QuestionChoiceViewModel : ObservableObject
{
    private readonly Action<QuestionChoiceViewModel> _toggle;

    public QuestionChoiceViewModel(AcpQuestionOption option, Action<QuestionChoiceViewModel> toggle)
    {
        Id = option.Id;
        Label = option.Label;
        Description = option.Description;
        _toggle = toggle;
    }

    public string Id { get; }
    public string Label { get; }
    public string? Description { get; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    [ObservableProperty] public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Toggle() => _toggle(this);
}

/// <summary>One question field inside a <see cref="QuestionCardItem"/>.</summary>
public sealed partial class QuestionFieldViewModel : ObservableObject
{
    public QuestionFieldViewModel(AcpQuestionItem item)
    {
        Id = item.Id;
        Prompt = item.Prompt;
        MultiSelect = item.MultiSelect;
        AllowFreeText = item.AllowFreeText || item.Options.Count == 0;
        foreach (var opt in item.Options)
        {
            Choices.Add(new QuestionChoiceViewModel(opt, OnChoiceToggled));
        }
        if (Choices.Count > 0 && !MultiSelect)
        {
            Choices[0].IsSelected = true;
        }
    }

    public string Id { get; }
    public string Prompt { get; }
    public bool MultiSelect { get; }
    public bool AllowFreeText { get; }
    public bool HasChoices => Choices.Count > 0;
    public ObservableCollection<QuestionChoiceViewModel> Choices { get; } = [];

    [ObservableProperty] public partial string FreeText { get; set; } = "";

    private void OnChoiceToggled(QuestionChoiceViewModel choice)
    {
        if (MultiSelect)
        {
            choice.IsSelected = !choice.IsSelected;
        }
        else
        {
            foreach (var c in Choices) c.IsSelected = ReferenceEquals(c, choice);
        }
    }

    public IReadOnlyList<string> CollectAnswers()
    {
        var list = Choices.Where(c => c.IsSelected).Select(c => c.Id).ToList();
        if (AllowFreeText && !string.IsNullOrWhiteSpace(FreeText))
        {
            list.Add(FreeText.Trim());
        }
        return list;
    }
}

/// <summary>Interactive inline elicitation / question card (ACP <c>elicitation/create</c> or dialect questions).</summary>
public sealed partial class QuestionCardItem : AgentTimelineItem
{
    private readonly Action<IReadOnlyDictionary<string, IReadOnlyList<string>>?> _resolve;

    public QuestionCardItem(AcpQuestionRequest request, Action<IReadOnlyDictionary<string, IReadOnlyList<string>>?> resolve)
    {
        _resolve = resolve;
        Title = string.IsNullOrWhiteSpace(request.Title) ? "Question" : request.Title;
        foreach (var q in request.Questions)
        {
            Fields.Add(new QuestionFieldViewModel(q));
        }
    }

    public string Title { get; }
    public ObservableCollection<QuestionFieldViewModel> Fields { get; } = [];

    [ObservableProperty] public partial bool IsResolved { get; set; }
    [ObservableProperty] public partial string ResolvedSummary { get; set; } = "";

    [RelayCommand]
    private void Submit()
    {
        if (IsResolved) return;
        var answers = new Dictionary<string, IReadOnlyList<string>>();
        var summaryParts = new List<string>();
        foreach (var field in Fields)
        {
            var vals = field.CollectAnswers();
            if (vals.Count > 0)
            {
                answers[field.Id] = vals;
                summaryParts.Add(string.Join(", ", vals));
            }
        }
        IsResolved = true;
        ResolvedSummary = summaryParts.Count > 0 ? string.Join(" · ", summaryParts) : AppLangResources.Get(AppLangResourceKind.AgentGuiQuestionSubmit);
        _resolve(answers);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsResolved) return;
        IsResolved = true;
        ResolvedSummary = AppLangResources.Get(AppLangResourceKind.AgentGuiQuestionCancel);
        _resolve(null);
    }
}
