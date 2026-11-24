using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
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
                // Strip Base64 image payloads once sent: the timeline chip only displays icon + label.
                var lightweight = string.IsNullOrEmpty(att.Base64Data)
                    ? att
                    : new AcpPromptAttachment
                    {
                        Kind = att.Kind,
                        Path = att.Path,
                        DisplayName = att.DisplayName,
                        MimeType = att.MimeType,
                    };
                Attachments.Add(new AttachmentChipViewModel(lightweight));
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
    public AttachmentChipViewModel(AcpPromptAttachment attachment, Action<AttachmentChipViewModel>? onRemove = null)
    {
        Attachment = attachment;
        if (onRemove is not null)
        {
            CanRemove = true;
            RemoveChipCommand = new RelayCommand(() => onRemove(this));
        }
    }

    public AcpPromptAttachment Attachment { get; }
    public string Label => string.IsNullOrWhiteSpace(Attachment.DisplayName)
        ? Path.GetFileName(Attachment.Path)
        : Attachment.DisplayName;
    public LucideIconKind Icon => Attachment.Kind == "image" ? LucideIconKind.Image : LucideIconKind.FileText;
    public bool CanRemove { get; }
    public IRelayCommand? RemoveChipCommand { get; }

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
    /// <summary>Maximum characters retained per output stream (Stdout / Stderr / OutputText) to bound memory in long sessions.</summary>
    internal const int MaxOutputChars = 32 * 1024;
    private const int OutputHeadChars = 4 * 1024;
    private const int MaxDiffLines = 120;

    private readonly Action<string>? _openFile;
    private readonly Action<string>? _openDiff;
    private readonly Action<ToolCallItemViewModel>? _openScript;

    // Cached command metadata recomputed only when CommandText changes.
    private int _commandLineCount;
    private string _singleLineCommandPreview = "";
    private string? _scriptLanguage;

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
    public int CommandLineCount => _commandLineCount;
    /// <summary>Bash Style 2: multi-line or long commands fold into a 1-line capsule above output with a full-script modal button.</summary>
    public bool IsLargeCommand => HasCommand && (_commandLineCount >= 4 || CommandText!.Length > 180);
    public bool IsCompactCommand => HasCommand && !IsLargeCommand;
    public string? ScriptLanguage => _scriptLanguage;
    public bool HasScriptLanguage => IsLargeCommand && !string.IsNullOrWhiteSpace(_scriptLanguage);
    public string LinesBadgeText => _commandLineCount >= 2 ? AppLangResources.Format(AppLangResourceKind.AgentGuiBashLinesBadge, _commandLineCount) : "";
    public bool ShowLinesBadge => IsLargeCommand && _commandLineCount >= 2;
    public string LinesCollapsedText => AppLangResources.Format(AppLangResourceKind.AgentGuiBashLinesCollapsed, Math.Max(1, _commandLineCount));
    public string SingleLineCommandPreview => _singleLineCommandPreview;
    public string? SecondaryHeaderPreview =>
        HasCommand && !string.IsNullOrWhiteSpace(Description)
            ? _singleLineCommandPreview
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

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            OnPropertyChanged(nameof(CopyableOutput));
        }
    }

    partial void OnCommandTextChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _commandLineCount = 0;
            _singleLineCommandPreview = "";
            _scriptLanguage = null;
        }
        else
        {
            _commandLineCount = CountLines(value);
            _singleLineCommandPreview = BuildSingleLineCommandPreview(value) ?? "";
            _scriptLanguage = DetectScriptLanguage(value);
        }
    }

    private static int CountLines(string text)
    {
        var count = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') count++;
        }
        return count;
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
        var kindChanged = false;
        var titleChanged = false;
        var descChanged = false;
        var statusChanged = false;
        var commandChanged = false;
        var inputChanged = false;
        var outputChanged = false;
        var exitChanged = false;
        var pathChanged = false;
        var diffChanged = false;
        var bgChanged = false;

        if (!string.IsNullOrWhiteSpace(update.Kind) && update.Kind != Kind) { Kind = update.Kind; kindChanged = true; }
        if (!string.IsNullOrWhiteSpace(update.Title) && update.Title != Title) { Title = update.Title; titleChanged = true; }
        if (!string.IsNullOrWhiteSpace(update.Description) && update.Description != Description) { Description = update.Description; descChanged = true; }
        if (!string.IsNullOrWhiteSpace(update.Status) && update.Status != Status) { Status = update.Status; statusChanged = true; }
        if (!string.IsNullOrWhiteSpace(update.Command) && update.Command != CommandText) { CommandText = update.Command; commandChanged = true; }
        if (update.IsBackground && !IsBackground) { IsBackground = true; bgChanged = true; }
        if (update.Stdout is not null)
        {
            var bounded = TruncateOutput(update.Stdout, MaxOutputChars);
            if (bounded != Stdout) { Stdout = bounded; outputChanged = true; }
        }
        if (update.Stderr is not null)
        {
            var bounded = TruncateOutput(update.Stderr, MaxOutputChars);
            if (bounded != Stderr) { Stderr = bounded; outputChanged = true; }
        }
        if (update.ExitCode.HasValue && update.ExitCode != ExitCode) { ExitCode = update.ExitCode; exitChanged = true; }

        if (update.Locations.Count > 0 && string.IsNullOrEmpty(PrimaryPath))
        {
            PrimaryPath = update.Locations[0].Path;
            pathChanged = true;
        }

        if (string.IsNullOrEmpty(InputSummary) && update.RawInput is { ValueKind: System.Text.Json.JsonValueKind.Object } rawIn)
        {
            var summary = SummarizeRawInput(rawIn);
            if (summary != InputSummary) { InputSummary = summary; inputChanged = true; }
        }

        if (update.TextOutputs.Count > 0)
        {
            var nextOut = AppendBoundedOutput(OutputText, update.TextOutputs, MaxOutputChars);
            if (nextOut != OutputText) { OutputText = nextOut; outputChanged = true; }
        }
        else if (string.IsNullOrEmpty(Stdout) && string.IsNullOrEmpty(OutputText)
            && update.RawOutput is { ValueKind: System.Text.Json.JsonValueKind.String } rawOutStr
            && rawOutStr.GetString() is { Length: > 0 } rawStr)
        {
            OutputText = TruncateOutput(rawStr, MaxOutputChars);
            outputChanged = true;
        }

        if (update.Diffs.Count > 0)
        {
            DiffLines.Clear();
            var added = 0;
            var removed = 0;
            var remainingBudget = MaxDiffLines;
            var overflowLines = 0;
            foreach (var diff in update.Diffs)
            {
                if (string.IsNullOrEmpty(PrimaryPath) && !string.IsNullOrEmpty(diff.Path))
                {
                    PrimaryPath = diff.Path;
                    pathChanged = true;
                }
                var lines = BuildSimpleDiff(diff.OldText, diff.NewText, remainingBudget, ref added, ref removed, ref overflowLines);
                foreach (var l in lines) DiffLines.Add(l);
                remainingBudget = Math.Max(0, MaxDiffLines - DiffLines.Count);
            }
            if (overflowLines > 0)
            {
                DiffLines.Add(new DiffLineViewModel(" ", $"… ({overflowLines} more lines)", false, false));
            }
            AddedLines = added;
            RemovedLines = removed;
            diffChanged = true;
        }

        if (kindChanged || titleChanged || descChanged || commandChanged || pathChanged)
        {
            OnPropertyChanged(nameof(DisplayTitle));
        }
        if (kindChanged)
        {
            OnPropertyChanged(nameof(KindIcon));
        }
        if (statusChanged)
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(StatusText));
        }
        if (diffChanged)
        {
            OnPropertyChanged(nameof(HasDiff));
            OnPropertyChanged(nameof(HasAddedStats));
            OnPropertyChanged(nameof(HasRemovedStats));
            OnPropertyChanged(nameof(AddedStatsText));
            OnPropertyChanged(nameof(RemovedStatsText));
            OnPropertyChanged(nameof(DiffStatsText));
        }
        if (commandChanged)
        {
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
            OnPropertyChanged(nameof(CanOpenScriptPreview));
        }
        if (commandChanged || descChanged)
        {
            OnPropertyChanged(nameof(SecondaryHeaderPreview));
            OnPropertyChanged(nameof(HasSecondaryHeaderPreview));
        }
        if (commandChanged || inputChanged)
        {
            OnPropertyChanged(nameof(HasInputSummary));
        }
        if (outputChanged)
        {
            OnPropertyChanged(nameof(HasStdout));
            OnPropertyChanged(nameof(HasStderr));
            OnPropertyChanged(nameof(HasOutputText));
        }
        if (exitChanged)
        {
            OnPropertyChanged(nameof(HasExitCode));
            OnPropertyChanged(nameof(ExitCodeText));
        }
        if (diffChanged || commandChanged || inputChanged || outputChanged || statusChanged)
        {
            OnPropertyChanged(nameof(HasDetails));
            OnPropertyChanged(nameof(CanExpand));
            OnPropertyChanged(nameof(IsRunningWithoutOutput));
        }
        if (pathChanged)
        {
            OnPropertyChanged(nameof(CanOpenFile));
        }
        if (pathChanged || diffChanged || kindChanged)
        {
            OnPropertyChanged(nameof(CanOpenDiff));
        }
        if (statusChanged || kindChanged)
        {
            OnPropertyChanged(nameof(IsReadOnlyExploration));
        }
        if (statusChanged || bgChanged || kindChanged || commandChanged || exitChanged)
        {
            OnPropertyChanged(nameof(IsCompletedCommand));
        }
        // Only notify CopyableOutput when expanded so collapsed cards never allocate a joined output string.
        if (IsExpanded && (commandChanged || inputChanged || diffChanged || outputChanged))
        {
            OnPropertyChanged(nameof(CopyableOutput));
        }
    }

    internal static string TruncateOutput(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var headLen = Math.Min(OutputHeadChars, maxChars / 4);
        var tailLen = maxChars - headLen - 48;
        if (tailLen < 128) tailLen = Math.Max(0, maxChars - headLen);
        var omitted = text.Length - headLen - tailLen;
        if (omitted <= 0) return text;
        var headEnd = headLen;
        if (headEnd > 0 && char.IsHighSurrogate(text[headEnd - 1])) headEnd--;
        var tailStart = text.Length - tailLen;
        if (tailStart < text.Length && char.IsLowSurrogate(text[tailStart])) tailStart++;
        return $"{text[..headEnd]}\n… ({omitted:N0} chars truncated) …\n{text[tailStart..]}";
    }

    private static string AppendBoundedOutput(string existing, IReadOnlyList<string> additions, int maxChars)
    {
        var total = existing.Length;
        for (var i = 0; i < additions.Count; i++)
        {
            if (total > 0 || i > 0) total++;
            total += additions[i].Length;
        }
        var sb = new StringBuilder(Math.Min(total, maxChars + 1024));
        if (existing.Length > 0) sb.Append(existing);
        for (var i = 0; i < additions.Count; i++)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(additions[i]);
        }
        return TruncateOutput(sb.ToString(), maxChars);
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

    private static List<DiffLineViewModel> BuildSimpleDiff(
        string? oldText,
        string newText,
        int maxLines,
        ref int added,
        ref int removed,
        ref int overflowLines)
    {
        var oldLines = SplitLinesNormalized(oldText);
        var newLines = SplitLinesNormalized(newText);

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

        var oldChangedEnd = oldLines.Length - suffix;
        var newChangedEnd = newLines.Length - suffix;
        removed += oldChangedEnd - prefix;
        added += newChangedEnd - prefix;

        var result = new List<DiffLineViewModel>(Math.Min(maxLines, 32));
        var overflow = 0;
        void AddLine(string linePrefix, string text, bool isAdd, bool isRemove)
        {
            if (result.Count < maxLines)
            {
                result.Add(new DiffLineViewModel(linePrefix, text, isAdd, isRemove));
            }
            else
            {
                overflow++;
            }
        }

        var contextStart = Math.Max(0, prefix - 2);
        for (var i = contextStart; i < prefix; i++)
        {
            AddLine(" ", oldLines[i], false, false);
        }
        for (var i = prefix; i < oldChangedEnd; i++)
        {
            AddLine("-", oldLines[i], false, true);
        }
        for (var i = prefix; i < newChangedEnd; i++)
        {
            AddLine("+", newLines[i], true, false);
        }
        var contextEnd = Math.Min(newLines.Length, newChangedEnd + 2);
        for (var i = newChangedEnd; i < contextEnd; i++)
        {
            AddLine(" ", newLines[i], false, false);
        }

        overflowLines += overflow;
        return result;
    }

    private static string[] SplitLinesNormalized(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var normalized = text.Contains('\r') ? text.Replace("\r\n", "\n") : text;
        return normalized.Split('\n');
    }
}

/// <summary>Base class for chronological content segments inside an <see cref="AssistantTurnItem"/>.</summary>
public abstract class AssistantTurnBlock : ObservableObject;

/// <summary>An independent collapsible Thinking segment in an assistant turn's chronological stream.</summary>
public sealed partial class ThoughtBlockViewModel : AssistantTurnBlock
{
    private StringBuilder? _buffer;

    public ThoughtBlockViewModel(string initialText, string? messageId = null, bool sealedInitial = false)
    {
        MessageId = messageId;
        if (!sealedInitial)
        {
            _buffer = new StringBuilder(Math.Max(256, initialText.Length));
            _buffer.Append(initialText);
        }
        Text = initialText;
    }

    public string? MessageId { get; internal set; }

    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public int CharLength => _buffer?.Length ?? Text.Length;

    public string Header =>
        $"{AppLangResources.Get(AppLangResourceKind.AgentGuiThinkingLabel)} · {CharLength}";

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    public void Append(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        _buffer ??= new StringBuilder(Text, Math.Max(256, Text.Length + delta.Length));
        _buffer.Append(delta);
        Text = _buffer.ToString();
        OnPropertyChanged(nameof(Header));
    }

    /// <summary>Releases the mutable streaming buffer once the turn completes.</summary>
    public void Seal()
    {
        if (_buffer is not null)
        {
            if (Text.Length != _buffer.Length)
            {
                Text = _buffer.ToString();
                OnPropertyChanged(nameof(Header));
            }
            _buffer = null;
        }
    }
}

/// <summary>
/// An independent Markdown response segment in an assistant turn's chronological stream.
/// Splits completed paragraphs/code-blocks into immutable <see cref="FrozenChunks"/> during long streaming replies
/// so Avalonia's Markdown renderer only re-parses the short active <see cref="LiveText"/> tail.
/// </summary>
public sealed partial class MarkdownBlockViewModel : AssistantTurnBlock
{
    private const int FreezeThresholdChars = 480;

    private StringBuilder? _liveBuffer;
    private int _frozenCharLength;

    public MarkdownBlockViewModel(string initialText, string? messageId = null, bool sealedInitial = false)
    {
        MessageId = messageId;
        if (!sealedInitial)
        {
            _liveBuffer = new StringBuilder(Math.Max(256, initialText.Length));
            _liveBuffer.Append(initialText);
            TryFreezeCompletedParagraphs();
        }
        else
        {
            LiveText = initialText;
        }
    }

    public string? MessageId { get; internal set; }

    public ObservableCollection<string> FrozenChunks { get; } = [];
    [ObservableProperty] public partial string LiveText { get; set; } = "";

    public bool HasFrozenChunks => FrozenChunks.Count > 0;
    public bool HasLiveText => LiveText.Length > 0;

    public int CharLength => _frozenCharLength + (_liveBuffer?.Length ?? LiveText.Length);

    public string Text
    {
        get
        {
            if (FrozenChunks.Count == 0) return LiveText;
            var total = _frozenCharLength + LiveText.Length + FrozenChunks.Count * 2;
            var sb = new StringBuilder(total);
            for (var i = 0; i < FrozenChunks.Count; i++)
            {
                if (i > 0) sb.Append("\n\n");
                sb.Append(FrozenChunks[i]);
            }
            if (LiveText.Length > 0)
            {
                sb.Append("\n\n");
                sb.Append(LiveText);
            }
            return sb.ToString();
        }
    }

    public void Append(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        _liveBuffer ??= new StringBuilder(LiveText, Math.Max(256, LiveText.Length + delta.Length));
        _liveBuffer.Append(delta);
        TryFreezeCompletedParagraphs();
        OnPropertyChanged(nameof(Text));
    }

    /// <summary>Releases the mutable streaming buffer once the turn completes.</summary>
    public void Seal()
    {
        if (_liveBuffer is not null)
        {
            if (LiveText.Length != _liveBuffer.Length)
            {
                LiveText = _liveBuffer.ToString();
                OnPropertyChanged(nameof(HasLiveText));
                OnPropertyChanged(nameof(Text));
            }
            _liveBuffer = null;
        }
    }

    private void TryFreezeCompletedParagraphs()
    {
        if (_liveBuffer is null) return;
        if (_liveBuffer.Length >= FreezeThresholdChars)
        {
            var current = _liveBuffer.ToString();
            var splitIdx = FindSafeMarkdownSplitIndex(current);
            if (splitIdx > 0 && splitIdx + 2 < current.Length)
            {
                var frozen = current[..splitIdx];
                var tail = current[(splitIdx + 2)..];
                if (frozen.Length > 0 && tail.Length > 0)
                {
                    FrozenChunks.Add(frozen);
                    _frozenCharLength += frozen.Length;
                    OnPropertyChanged(nameof(HasFrozenChunks));
                    _liveBuffer.Clear();
                    _liveBuffer.Append(tail);
                    LiveText = tail;
                    OnPropertyChanged(nameof(HasLiveText));
                    return;
                }
            }
            LiveText = current;
            OnPropertyChanged(nameof(HasLiveText));
            return;
        }
        LiveText = _liveBuffer.ToString();
        OnPropertyChanged(nameof(HasLiveText));
    }

    /// <summary>
    /// Finds the last top-level blank-line (<c>\n\n</c>) boundary outside fenced code blocks so completed paragraphs
    /// can be frozen without corrupting open fences or tables.
    /// </summary>
    internal static int FindSafeMarkdownSplitIndex(string text)
    {
        var inFence = false;
        var fenceChar = '\0';
        var fenceLen = 0;
        var lastSafe = -1;
        var lineStart = 0;

        while (lineStart < text.Length)
        {
            var nl = text.IndexOf('\n', lineStart);
            var lineEnd = nl < 0 ? text.Length : nl;
            var span = text.AsSpan(lineStart, lineEnd - lineStart).TrimStart();

            if (span.Length >= 3 && (span[0] is '`' or '~'))
            {
                var ch = span[0];
                var run = 0;
                while (run < span.Length && span[run] == ch) run++;
                if (!inFence)
                {
                    inFence = true;
                    fenceChar = ch;
                    fenceLen = run;
                }
                else if (ch == fenceChar && run >= fenceLen && span[run..].Trim().IsEmpty)
                {
                    inFence = false;
                }
            }

            if (!inFence && nl >= 0 && nl + 1 < text.Length && text[nl + 1] == '\n')
            {
                // Avoid splitting inside markdown tables (adjacent pipe-prefixed lines) or list continuations
                var afterBlank = nl + 2;
                if (afterBlank < text.Length && text[afterBlank] != '|' && !span.StartsWith("|"))
                {
                    lastSafe = nl;
                }
            }

            if (nl < 0) break;
            lineStart = nl + 1;
        }

        return lastSafe;
    }
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
        if (all.Count < 2)
        {
            if (explorationTarget.Count > 0) explorationTarget.Clear();
            if (commandTarget.Count > 0) commandTarget.Clear();
            if (all.Count == 0)
            {
                if (primaryTarget.Count > 0) primaryTarget.Clear();
            }
            else if (primaryTarget.Count != 1 || !ReferenceEquals(primaryTarget[0], all[0]))
            {
                SyncCollection(primaryTarget, [all[0]]);
            }
            return;
        }

        var exploreCount = 0;
        var cmdCount = 0;
        for (var i = 0; i < all.Count; i++)
        {
            var t = all[i];
            if (t.IsReadOnlyExploration) exploreCount++;
            if (t.IsCompletedCommand) cmdCount++;
        }

        if (exploreCount < 2 && cmdCount < 2)
        {
            if (explorationTarget.Count > 0) explorationTarget.Clear();
            if (commandTarget.Count > 0) commandTarget.Clear();
            SyncCollection(primaryTarget, all is List<ToolCallItemViewModel> list ? list : [.. all]);
            return;
        }

        var nextExplore = exploreCount >= 2 ? new List<ToolCallItemViewModel>(exploreCount) : [];
        var nextCommands = cmdCount >= 2 ? new List<ToolCallItemViewModel>(cmdCount) : [];
        var nextPrimary = new List<ToolCallItemViewModel>(all.Count);

        for (var i = 0; i < all.Count; i++)
        {
            var t = all[i];
            var folded = false;
            if (exploreCount >= 2 && t.IsReadOnlyExploration)
            {
                nextExplore.Add(t);
                folded = true;
            }
            if (cmdCount >= 2 && t.IsCompletedCommand)
            {
                nextCommands.Add(t);
                folded = true;
            }
            if (!folded)
            {
                nextPrimary.Add(t);
            }
        }

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
    private readonly Stopwatch? _elapsed;
    private DispatcherTimer? _statusTimer;
    private int _phraseIndex = Random.Shared.Next(WorkingPhrases.Length);
    private int _statusTicks;
    private int _thoughtCharLength;
    private bool _hasThoughtContent;
    private bool _hasMarkdownContent;

    public AssistantTurnItem(
        string agentId,
        string agentName,
        Action<string>? openFile = null,
        Action<string>? openDiff = null,
        Action<ToolCallItemViewModel>? openScript = null,
        bool isReplay = false)
    {
        AgentId = agentId;
        AgentName = agentName;
        _openFile = openFile;
        _openDiff = openDiff;
        _openScript = openScript;
        if (isReplay)
        {
            IsStreaming = false;
        }
        else
        {
            _elapsed = Stopwatch.StartNew();
            StartStatusTimer();
        }
    }

    public string AgentId { get; }
    public string AgentName { get; }

    /// <summary>Computed on demand from <see cref="Blocks"/> so large thought streams are never duplicated in memory.</summary>
    public string ThoughtText => JoinBlockText<ThoughtBlockViewModel>(b => b.Text);

    /// <summary>Computed on demand from <see cref="Blocks"/> so large Markdown replies are never duplicated in memory.</summary>
    public string MarkdownText => JoinBlockText<MarkdownBlockViewModel>(b => b.Text);

    /// <summary>Only materialized for the Copy button once streaming has finished, avoiding LOH allocations while streaming.</summary>
    public string CopyReplyValue => IsStreaming || !_hasMarkdownContent ? "" : MarkdownText;

    [ObservableProperty] public partial bool IsThoughtExpanded { get; set; }
    [ObservableProperty] public partial bool IsExplorationExpanded { get; set; }
    [ObservableProperty] public partial bool IsCommandsExpanded { get; set; }
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

    public bool HasThought => _hasThoughtContent;
    public bool HasPlan => PlanEntries.Count > 0 || !string.IsNullOrWhiteSpace(PlanMarkdown);
    public bool HasPlanEntries => PlanEntries.Count > 0;
    public bool HasPlanMarkdown => !string.IsNullOrWhiteSpace(PlanMarkdown);
    public bool HasToolCalls => ToolCalls.Count > 0;
    public bool HasExplorationGroup => ExplorationTools.Count > 0;
    public bool HasCommandGroup => CommandTools.Count > 0;
    public bool HasPrimaryTools => PrimaryTools.Count > 0;
    public bool HasMarkdown => _hasMarkdownContent;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public bool HasDuration => !string.IsNullOrEmpty(DurationText);
    public bool CanCopyReply => !IsStreaming && _hasMarkdownContent;
    public bool ShowLiveStatus => IsStreaming && !IsWaitingForInput;
    public bool HasLiveElapsed => !string.IsNullOrEmpty(LiveElapsedText);
    public string LiveStatusText => AppLangResources.Get(WorkingPhrases[_phraseIndex]);

    public string ThoughtHeader =>
        $"{AppLangResources.Get(AppLangResourceKind.AgentGuiThinkingLabel)} · {_thoughtCharLength}";

    private string JoinBlockText<TBlock>(Func<TBlock, string> selector) where TBlock : AssistantTurnBlock
    {
        string? single = null;
        StringBuilder? sb = null;
        for (var i = 0; i < Blocks.Count; i++)
        {
            if (Blocks[i] is not TBlock block) continue;
            var text = selector(block);
            if (single is null)
            {
                single = text;
            }
            else
            {
                if (sb is null)
                {
                    sb = new StringBuilder(single.Length + text.Length + 2);
                    sb.Append(single);
                }
                sb.Append("\n\n");
                sb.Append(text);
            }
        }
        return sb is not null ? sb.ToString() : (single ?? "");
    }

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
        if (_elapsed is null) return;
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

        var addedBlock = false;
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
            Blocks.Add(new ThoughtBlockViewModel(delta, messageId, sealedInitial: !IsStreaming));
            addedBlock = true;
        }

        if (addedBlock && _thoughtCharLength > 0) _thoughtCharLength += 2;
        _thoughtCharLength += delta.Length;
        var becameNonEmpty = !_hasThoughtContent && !string.IsNullOrWhiteSpace(delta);
        if (becameNonEmpty) _hasThoughtContent = true;

        OnPropertyChanged(nameof(ThoughtText));
        if (becameNonEmpty) OnPropertyChanged(nameof(HasThought));
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
            Blocks.Add(new MarkdownBlockViewModel(delta, messageId, sealedInitial: !IsStreaming));
        }

        var becameNonEmpty = !_hasMarkdownContent && !string.IsNullOrWhiteSpace(delta);
        if (becameNonEmpty) _hasMarkdownContent = true;

        OnPropertyChanged(nameof(MarkdownText));
        if (becameNonEmpty)
        {
            OnPropertyChanged(nameof(HasMarkdown));
            OnPropertyChanged(nameof(CanCopyReply));
        }
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
        foreach (var block in Blocks)
        {
            if (block is ThoughtBlockViewModel tb) tb.Seal();
            else if (block is MarkdownBlockViewModel mb) mb.Seal();
        }
        IsStreaming = false;
        IsWaitingForInput = false;
        if (_elapsed is not null)
        {
            _elapsed.Stop();
            if (showDuration && _elapsed.Elapsed.TotalMilliseconds >= 100)
            {
                DurationText = _elapsed.Elapsed.TotalSeconds < 60
                    ? $"{_elapsed.Elapsed.TotalSeconds:0.#}s"
                    : $"{(int)_elapsed.Elapsed.TotalMinutes}m {_elapsed.Elapsed.Seconds}s";
                OnPropertyChanged(nameof(HasDuration));
            }
        }
        if (!string.IsNullOrWhiteSpace(error))
        {
            ErrorText = error;
            OnPropertyChanged(nameof(HasError));
        }
        OnPropertyChanged(nameof(CanCopyReply));
        OnPropertyChanged(nameof(CopyReplyValue));
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
