using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Acp.Protocol;
using Octopus.App.Localization;
using Octopus.Ticker;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.AgentGui;

/// <summary>Base class for every block rendered in the lightweight Agent GUI timeline.</summary>
public abstract class AgentTimelineItem : ObservableObject;

/// <summary>A user-authored prompt rendered as a compact right-aligned capsule.</summary>
public sealed partial class UserTurnItem : AgentTimelineItem
{
    private readonly Action<string>? _onReuse;

    public UserTurnItem(string text, IReadOnlyList<AcpPromptAttachment>? attachments = null, Action<string>? onReuse = null)
    {
        Text = text;
        _onReuse = onReuse;
        ParseCollabMetadata();
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

    private string _displaySource = "";

    public string Text { get; private set; }

    /// <summary>True when this turn was injected by the Octopus multi-agent collaboration bus rather than typed by the user.</summary>
    public bool IsCollabMessage { get; private set; }
    public bool IsRegularUserMessage => !IsCollabMessage;
    public string CollabHeader { get; private set; } = "";

    /// <summary>Expanded state bound two-way to <see cref="ExpandableContent.IsExpanded"/> so it survives list recycling.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Display text shown in the timeline (full text; visual clamping is handled by <see cref="ExpandableContent"/>).</summary>
    public string DisplayText => _displaySource;

    /// <summary>Message id from a replayed <c>user_message_chunk</c>, used to stitch chunks together.</summary>
    public string? MessageId { get; init; }

    /// <summary>Appends a replayed chunk of the same user message.</summary>
    public void Append(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        Text += delta;
        ParseCollabMetadata();
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(IsCollabMessage));
        OnPropertyChanged(nameof(IsRegularUserMessage));
        OnPropertyChanged(nameof(CollabHeader));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(CanReuse));
    }

    public ObservableCollection<AttachmentChipViewModel> Attachments { get; } = [];
    public bool HasAttachments => Attachments.Count > 0;
    public bool CanReuse => !IsCollabMessage && _onReuse is not null && !string.IsNullOrWhiteSpace(PromptText);

    [RelayCommand]
    private void Reuse()
    {
        if (CanReuse) _onReuse!(PromptText);
    }

    private void ParseCollabMetadata()
    {
        if (Octopus.Remote.Protocol.CollabEnvelope.TryParse(Text) is { } envelope)
        {
            IsCollabMessage = true;
            CollabHeader = envelope.RawHeader;
            _displaySource = envelope.Body.Length > 0 ? envelope.Body : envelope.RawHeader;
            return;
        }

        IsCollabMessage = false;
        CollabHeader = "";
        _displaySource = StripMentionContextBlock(Text);
        if (!ReferenceEquals(_displaySource, Text)) RestoreMentionChips();
    }

    /// <summary>The prompt as the person wrote it: without the <c>&lt;octopus-mentions&gt;</c> block a replayed turn carries.</summary>
    public string PromptText => IsCollabMessage ? Text : _displaySource;

    private const string MentionBlockOpen = "<octopus-mentions>";

    [GeneratedRegex(@"<(session|agent)(?: order=""\d+"")? id=""([^""]*)"" label=""([^""]*)""(?: agent=""([^""]*)"")?")]
    private static partial Regex MentionEntryRegex();

    /// <summary>
    /// Drops the mention context block appended to a sent prompt (<c>CollabCoordinator.FormatMentionContextBlock</c>);
    /// live turns never show it, but <c>session/load</c> replays the prompt as the agent received it. A block still
    /// streaming in (no closing tag yet) is dropped too.
    /// </summary>
    private static string StripMentionContextBlock(string text)
    {
        var at = text.LastIndexOf("\n" + MentionBlockOpen, StringComparison.Ordinal);
        return at < 0 ? text : text[..at].TrimEnd();
    }

    /// <summary>Rebuilds session / agent chips of a replayed turn from its mention block (live turns already have them).</summary>
    private void RestoreMentionChips()
    {
        var at = Text.LastIndexOf(MentionBlockOpen, StringComparison.Ordinal);
        if (at < 0 || Text.IndexOf("</octopus-mentions>", at, StringComparison.Ordinal) < 0) return;
        foreach (Match m in MentionEntryRegex().Matches(Text, at))
        {
            var kind = m.Groups[1].Value;
            var id = WebUtility.HtmlDecode(m.Groups[2].Value);
            if (Attachments.Any(c => c.Attachment.Kind == kind && c.Attachment.Path == id)) continue;
            Attachments.Add(new AttachmentChipViewModel(new AcpPromptAttachment
            {
                Kind = kind,
                Path = id,
                DisplayName = WebUtility.HtmlDecode(m.Groups[3].Value),
                MimeType = m.Groups[4].Success ? WebUtility.HtmlDecode(m.Groups[4].Value) : null,
            }));
        }
        OnPropertyChanged(nameof(HasAttachments));
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
    public string Label
    {
        get
        {
            var raw = string.IsNullOrWhiteSpace(Attachment.DisplayName)
                ? Attachment.Path
                : Attachment.DisplayName;
            if (string.IsNullOrWhiteSpace(raw)) return "";
            if (Attachment.Kind is "file" or "folder" or "image" && Path.IsPathRooted(raw))
            {
                var trimmed = Path.TrimEndingDirectorySeparator(raw);
                var name = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(name) ? raw : name;
            }
            return raw;
        }
    }
    public string? Tooltip => string.IsNullOrWhiteSpace(Attachment.Path) ? null : Attachment.Path;
    public LucideIconKind Icon => Attachment.Kind switch
    {
        "image" => LucideIconKind.Image,
        "folder" => LucideIconKind.Folder,
        "session" => LucideIconKind.MessageSquare,
        "agent" => LucideIconKind.Bot,
        _ => LucideIconKind.FileText,
    };
    public bool IsCollabMention => Attachment.Kind is "session" or "agent";
    public bool CanRemove { get; }
    public IRelayCommand? RemoveChipCommand { get; }

    public static AttachmentChipViewModel Removable(AcpPromptAttachment attachment, Action<AttachmentChipViewModel> onRemove) =>
        new(attachment, onRemove);
}

/// <summary>A single unified-diff line inside an expanded edit tool detail view.</summary>
public sealed record DiffLineViewModel(string Prefix, string Text, bool IsAdd, bool IsRemove);

/// <summary>A single plan checklist step reported via ACP <c>plan</c> updates.</summary>
public sealed class PlanEntryViewModel(AcpPlanEntry entry)
{
    public string Content { get; } = entry.Content;
    public string Status { get; } = entry.Status;
    public bool IsCompleted => Status == "completed";
    public bool IsInProgress => Status == "in_progress";
    public string StatusMark => Status switch
    {
        "completed" => "✓",
        "in_progress" => "◌",
        _ => "○",
    };
    public LucideIconKind Icon => Status switch
    {
        "completed" => LucideIconKind.CircleCheck,
        "in_progress" => LucideIconKind.Loader,
        _ => LucideIconKind.Circle,
    };
}

/// <summary>One tool execution record inside an assistant turn, updated in place by <see cref="ToolCallId"/>.</summary>
public sealed partial class ToolCallItemViewModel : ObservableObject
{
    /// <summary>Maximum characters retained per output stream (Stdout / Stderr / OutputText) to bound memory in long sessions.</summary>
    internal const int MaxOutputChars = 32 * 1024;
    private const int OutputHeadChars = 4 * 1024;
    private const int MaxDiffLines = 120;

    private readonly Action<string>? _openFile;
    private readonly Action<string>? _openDiff;
    private readonly Action<ToolCallItemViewModel>? _openScript;
    private readonly long _startedTimestamp;
    private double? _elapsedSeconds;

    // Cached command metadata recomputed only when CommandText changes.
    private int _commandLineCount;
    private string _singleLineCommandPreview = "";
    private string? _scriptLanguage;

    public ToolCallItemViewModel(
        string toolCallId,
        Action<string>? openFile = null,
        Action<string>? openDiff = null,
        Action<ToolCallItemViewModel>? openScript = null,
        bool isReplay = false)
    {
        ToolCallId = toolCallId;
        _openFile = openFile;
        _openDiff = openDiff;
        _openScript = openScript;
        _startedTimestamp = isReplay ? 0 : Stopwatch.GetTimestamp();
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
    public bool IsCompleted => Status == "completed" && !HasFailedExit;
    public bool IsFailed => Status == "failed" || HasFailedExit;
    public bool HasFailedExit => ExitCode.HasValue && ExitCode.Value != 0;
    public bool HasDiff => DiffLines.Count > 0;
    public bool HasAddedStats => AddedLines > 0;
    public bool HasRemovedStats => RemovedLines > 0;
    public string AddedStatsText => $"+{AddedLines}";
    public string RemovedStatsText => $"-{RemovedLines}";
    public string DiffStatsText => $"+{AddedLines} -{RemovedLines}";
    public bool HasCommand => !string.IsNullOrWhiteSpace(CommandText);
    public int CommandLineCount => _commandLineCount;
    /// <summary>Multi-line or long commands fold into a 1-line text summary with a full-script modal link.</summary>
    public bool IsLargeCommand => HasCommand && (_commandLineCount >= 4 || CommandText!.Length > 180);
    public bool IsCompactCommand => HasCommand && !IsLargeCommand;
    public string? ScriptLanguage => _scriptLanguage;
    public bool HasScriptLanguage => IsLargeCommand && !string.IsNullOrWhiteSpace(_scriptLanguage);
    public string LinesBadgeText => _commandLineCount >= 2 ? AppLangResources.Format(AppLangResourceKind.AgentGuiBashLinesBadge, _commandLineCount) : "";
    public bool ShowLinesBadge => IsLargeCommand && _commandLineCount >= 2;
    public string LinesCollapsedText => AppLangResources.Format(AppLangResourceKind.AgentGuiBashLinesCollapsed, Math.Max(1, _commandLineCount));
    public string SingleLineCommandPreview => _singleLineCommandPreview;
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

    public string DurationText => _elapsedSeconds is >= 0.1
        ? FormatShortDuration(_elapsedSeconds.Value)
        : "";

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

    /// <summary>Always a clean single-line header: never explodes on multi-line Bash scripts.</summary>
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

    public string StatusText => IsFailed
        ? AppLangResources.Get(AppLangResourceKind.AgentGuiToolFailed)
        : IsRunning
            ? AppLangResources.Get(AppLangResourceKind.AgentGuiToolRunning)
            : AppLangResources.Get(AppLangResourceKind.AgentGuiToolDone);

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
        var wasRunning = IsRunning;
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

        if (!string.IsNullOrWhiteSpace(update.Kind) && update.Kind != Kind) { Kind = update.Kind; kindChanged = true; }
        if (!string.IsNullOrWhiteSpace(update.Title) && update.Title != Title) { Title = update.Title; titleChanged = true; }
        if (!string.IsNullOrWhiteSpace(update.Description) && update.Description != Description) { Description = update.Description; descChanged = true; }
        if (!string.IsNullOrWhiteSpace(update.Status) && update.Status != Status) { Status = update.Status; statusChanged = true; }
        if (!string.IsNullOrWhiteSpace(update.Command) && update.Command != CommandText) { CommandText = update.Command; commandChanged = true; }
        if (update.IsBackground && !IsBackground) { IsBackground = true; }
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

        if (wasRunning && !IsRunning && _startedTimestamp != 0 && _elapsedSeconds is null)
        {
            _elapsedSeconds = Stopwatch.GetElapsedTime(_startedTimestamp).TotalSeconds;
            OnPropertyChanged(nameof(DurationText));
        }

        if (kindChanged || titleChanged || descChanged || commandChanged || pathChanged)
        {
            OnPropertyChanged(nameof(DisplayTitle));
        }
        if (statusChanged || exitChanged)
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(HasFailedExit));
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
        if (IsExpanded && (commandChanged || inputChanged || diffChanged || outputChanged))
        {
            OnPropertyChanged(nameof(CopyableOutput));
        }
    }

    internal static string FormatShortDuration(double seconds) =>
        seconds < 60
            ? $"{seconds:0.#}s"
            : $"{(int)seconds / 60}m {(int)seconds % 60}s";

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

/// <summary>Category of a step recorded inside an <see cref="AssistantTurnItem"/>'s single unified work area.</summary>
public enum WorkStepKind
{
    Thought,
    Tool,
    Plan,
    Text,
}

/// <summary>
/// One pure-text step inside an assistant turn's single unified work area (thinking, tool call, plan, or intermediate text).
/// Visual details are only materialized when <see cref="IsExpanded"/> is toggled on, and immediately released on collapse.
/// </summary>
public sealed partial class WorkStepViewModel : ObservableObject
{
    private const int MaxThoughtChars = 64 * 1024;
    private const int ThoughtPreviewMaxChars = 110;

    private StringBuilder? _thoughtBuffer;
    private string _thoughtText = "";
    private string _thoughtPreview = "";
    private string _noteText = "";
    private string _notePreview = "";
    private bool _isThoughtActive;
    private readonly long _thoughtStartedTimestamp;
    private double? _thoughtElapsedSeconds;
    private ObservableCollection<PlanEntryViewModel>? _planEntries;
    private string? _planMarkdown;

    private WorkStepViewModel(WorkStepKind stepKind, long startedTimestamp = 0)
    {
        StepKind = stepKind;
        _thoughtStartedTimestamp = startedTimestamp;
    }

    public static WorkStepViewModel CreateThought(string initialText, string? messageId, bool isActive)
    {
        var step = new WorkStepViewModel(WorkStepKind.Thought, isActive ? Stopwatch.GetTimestamp() : 0)
        {
            MessageId = messageId,
            _isThoughtActive = isActive,
        };
        if (isActive)
        {
            step._thoughtBuffer = new StringBuilder(Math.Max(256, initialText.Length));
            step._thoughtBuffer.Append(initialText);
        }
        step._thoughtText = ToolCallItemViewModel.TruncateOutput(initialText, MaxThoughtChars);
        step._thoughtPreview = BuildThoughtPreview(step._thoughtText);
        return step;
    }

    public static WorkStepViewModel CreateText(string text, string? messageId = null)
    {
        var trimmed = text.Trim();
        return new WorkStepViewModel(WorkStepKind.Text)
        {
            MessageId = messageId,
            _noteText = trimmed,
            _notePreview = BuildTextPreview(trimmed),
        };
    }

    public static WorkStepViewModel CreateTool(ToolCallItemViewModel tool) =>
        new(WorkStepKind.Tool) { Tool = tool };

    public static WorkStepViewModel CreatePlan(ObservableCollection<PlanEntryViewModel> entries, string? markdown) =>
        new(WorkStepKind.Plan) { _planEntries = entries, _planMarkdown = markdown };

    public WorkStepKind StepKind { get; }
    public string? MessageId { get; internal set; }
    public ToolCallItemViewModel? Tool { get; private init; }
    public ObservableCollection<PlanEntryViewModel>? PlanEntries => _planEntries;
    public string? PlanMarkdown => _planMarkdown;

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public bool IsThought => StepKind == WorkStepKind.Thought;
    public bool IsTool => StepKind == WorkStepKind.Tool;
    public bool IsPlan => StepKind == WorkStepKind.Plan;
    public bool IsText => StepKind == WorkStepKind.Text;
    public bool IsProse => StepKind is WorkStepKind.Thought or WorkStepKind.Text;
    public bool IsRunning => StepKind switch
    {
        WorkStepKind.Tool => Tool!.IsRunning,
        WorkStepKind.Thought => _isThoughtActive,
        _ => false,
    };
    public bool IsFailed => StepKind == WorkStepKind.Tool && Tool!.IsFailed;
    public bool IsCompleted => !IsRunning && !IsFailed;

    public string ThoughtText => _thoughtText;
    public string NoteText => _noteText;
    public int ThoughtCharLength => _thoughtBuffer?.Length ?? _thoughtText.Length;
    public bool HasPlanEntries => _planEntries is { Count: > 0 };
    public bool HasPlanMarkdown => !string.IsNullOrWhiteSpace(_planMarkdown);

    public string StatusMark => IsFailed ? "✕" : IsExpanded ? "▾" : "·";

    public string Verb => StepKind switch
    {
        WorkStepKind.Thought => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbThink),
        WorkStepKind.Plan => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbPlan),
        WorkStepKind.Text => "",
        _ => ResolveToolVerb(Tool?.Kind, active: false, done: false),
    };

    public bool HasVerb => !string.IsNullOrEmpty(Verb);

    public string ActiveVerb => StepKind switch
    {
        WorkStepKind.Thought => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbThinkActive),
        WorkStepKind.Plan => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbPlanActive),
        WorkStepKind.Text => "",
        _ => ResolveToolVerb(Tool?.Kind, active: true, done: false),
    };

    public string DoneVerb => StepKind switch
    {
        WorkStepKind.Thought => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbThinkDone),
        WorkStepKind.Plan => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbPlanDone),
        WorkStepKind.Text => "",
        _ => ResolveToolVerb(Tool?.Kind, active: false, done: true),
    };

    public string Target => StepKind switch
    {
        WorkStepKind.Thought => _thoughtPreview,
        WorkStepKind.Text => _notePreview,
        WorkStepKind.Plan => FormatPlanPreview(),
        _ => Tool!.DisplayTitle,
    };

    public bool HasAddedStats => Tool is { HasAddedStats: true };
    public bool HasRemovedStats => Tool is { HasRemovedStats: true };
    public string AddedStatsText => Tool?.AddedStatsText ?? "";
    public string RemovedStatsText => Tool?.RemovedStatsText ?? "";

    public string TickerStatText
    {
        get
        {
            if (StepKind == WorkStepKind.Tool && Tool is not null)
            {
                if (Tool.HasAddedStats || Tool.HasRemovedStats) return Tool.DiffStatsText;
                if (Tool.IsFailed && Tool.HasExitCode) return Tool.ExitCodeText;
                return Tool.IsRunning ? "" : Tool.DurationText;
            }
            if (StepKind == WorkStepKind.Thought && !_isThoughtActive)
            {
                return _thoughtElapsedSeconds is >= 0.1
                    ? ToolCallItemViewModel.FormatShortDuration(_thoughtElapsedSeconds.Value)
                    : $"{ThoughtCharLength}";
            }
            return "";
        }
    }

    public string MetaText
    {
        get
        {
            if (IsRunning) return AppLangResources.Get(AppLangResourceKind.AgentGuiToolRunning);
            if (StepKind == WorkStepKind.Tool && Tool is not null)
            {
                if (Tool.IsFailed && Tool.HasExitCode) return Tool.ExitCodeText;
                return Tool.DurationText;
            }
            if (StepKind == WorkStepKind.Thought)
            {
                return _thoughtElapsedSeconds is >= 0.1
                    ? ToolCallItemViewModel.FormatShortDuration(_thoughtElapsedSeconds.Value)
                    : $"{ThoughtCharLength}";
            }
            return "";
        }
    }

    public bool HasMeta => !string.IsNullOrEmpty(MetaText);

    public bool CanExpand => StepKind switch
    {
        WorkStepKind.Thought => !string.IsNullOrWhiteSpace(_thoughtText),
        WorkStepKind.Text => !string.IsNullOrWhiteSpace(_noteText),
        WorkStepKind.Plan => HasPlanEntries || HasPlanMarkdown,
        _ => Tool?.CanExpand ?? false,
    };

    /// <summary>Lazy detail binding: returns <c>null</c> when collapsed so Avalonia instantiates zero detail controls.</summary>
    public WorkStepViewModel? ExpandedDetail => IsExpanded && CanExpand ? this : null;

    partial void OnIsExpandedChanged(bool value)
    {
        if (Tool is not null && Tool.IsExpanded != value)
        {
            Tool.IsExpanded = value;
        }
        OnPropertyChanged(nameof(StatusMark));
        OnPropertyChanged(nameof(ExpandedDetail));
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        if (CanExpand) IsExpanded = !IsExpanded;
    }

    internal void AppendThought(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        _thoughtBuffer ??= new StringBuilder(_thoughtText, Math.Max(256, _thoughtText.Length + delta.Length));
        if (_thoughtBuffer.Length < MaxThoughtChars)
        {
            var remaining = MaxThoughtChars - _thoughtBuffer.Length;
            _thoughtBuffer.Append(delta.Length <= remaining ? delta : delta[..remaining]);
        }
        _thoughtText = _thoughtBuffer.ToString();
        _thoughtPreview = BuildThoughtPreview(_thoughtText);
        OnPropertyChanged(nameof(ThoughtText));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(HasMeta));
    }

    internal void FinishThought()
    {
        if (!_isThoughtActive) return;
        _isThoughtActive = false;
        if (_thoughtStartedTimestamp != 0 && _thoughtElapsedSeconds is null)
        {
            _thoughtElapsedSeconds = Stopwatch.GetElapsedTime(_thoughtStartedTimestamp).TotalSeconds;
        }
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(HasMeta));
    }

    internal void Seal()
    {
        FinishThought();
        if (_thoughtBuffer is not null)
        {
            if (_thoughtText.Length != _thoughtBuffer.Length)
            {
                _thoughtText = _thoughtBuffer.ToString();
                _thoughtPreview = BuildThoughtPreview(_thoughtText);
                OnPropertyChanged(nameof(ThoughtText));
                OnPropertyChanged(nameof(Target));
            }
            _thoughtBuffer = null;
        }
    }

    internal void NotifyToolUpdated()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(StatusMark));
        OnPropertyChanged(nameof(Verb));
        OnPropertyChanged(nameof(ActiveVerb));
        OnPropertyChanged(nameof(DoneVerb));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(HasAddedStats));
        OnPropertyChanged(nameof(HasRemovedStats));
        OnPropertyChanged(nameof(AddedStatsText));
        OnPropertyChanged(nameof(RemovedStatsText));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(HasMeta));
        OnPropertyChanged(nameof(CanExpand));
    }

    internal void UpdatePlan(string? markdown)
    {
        if (!string.IsNullOrWhiteSpace(markdown)) _planMarkdown = markdown;
        OnPropertyChanged(nameof(PlanEntries));
        OnPropertyChanged(nameof(PlanMarkdown));
        OnPropertyChanged(nameof(HasPlanEntries));
        OnPropertyChanged(nameof(HasPlanMarkdown));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(CanExpand));
    }

    private string FormatPlanPreview()
    {
        if (_planEntries is { Count: > 0 })
        {
            var done = 0;
            string? active = null;
            for (var i = 0; i < _planEntries.Count; i++)
            {
                var entry = _planEntries[i];
                if (entry.IsCompleted) done++;
                else if (active is null && entry.IsInProgress) active = entry.Content;
            }
            active ??= _planEntries[0].Content;
            var single = ToolCallItemViewModel.ToSingleLine(active) ?? "";
            return $"{done}/{_planEntries.Count} · {single}";
        }
        return ToolCallItemViewModel.ToSingleLine(_planMarkdown)
            ?? AppLangResources.Get(AppLangResourceKind.AgentGuiPlanTitle);
    }

    private static string BuildThoughtPreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        // Prefer the latest non-empty line so the 3-line ticker reflects live reasoning progress.
        var end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        if (end == 0) return "";
        var lineStart = text.LastIndexOf('\n', end - 1) + 1;
        var slice = text.AsSpan(lineStart, end - lineStart).Trim();
        if (slice.IsEmpty) slice = text.AsSpan(0, end).Trim();
        if (slice.Length <= ThoughtPreviewMaxChars) return slice.ToString();
        return string.Concat(slice[..ThoughtPreviewMaxChars], "…");
    }

    private static string BuildTextPreview(string text)
    {
        var single = ToolCallItemViewModel.ToSingleLine(text);
        if (string.IsNullOrEmpty(single)) return "";
        return single.Length <= ThoughtPreviewMaxChars
            ? single
            : string.Concat(single.AsSpan(0, ThoughtPreviewMaxChars), "…");
    }

    private static string ResolveToolVerb(string? kind, bool active, bool done) => (kind, active, done) switch
    {
        ("read", true, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbReadActive),
        ("read", false, true) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbReadDone),
        ("read", _, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbRead),

        ("search", true, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbSearchActive),
        ("search", false, true) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbSearchDone),
        ("search", _, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbSearch),

        ("edit", true, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbEditActive),
        ("edit", false, true) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbEditDone),
        ("edit", _, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbEdit),

        ("delete", true, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbDeleteActive),
        ("delete", false, true) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbDeleteDone),
        ("delete", _, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbDelete),

        ("move", true, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbMoveActive),
        ("move", false, true) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbMoveDone),
        ("move", _, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbMove),

        ("execute", true, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbExecActive),
        ("execute", false, true) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbExecDone),
        ("execute", _, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbExec),

        ("fetch", true, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbFetchActive),
        ("fetch", false, true) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbFetchDone),
        ("fetch", _, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbFetch),

        ("think", true, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbThinkActive),
        ("think", false, true) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbThinkDone),
        ("think", _, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbThink),

        ("image", true, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbImageActive),
        ("image", false, true) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbImageDone),
        ("image", _, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbImage),

        (_, true, _) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbOtherActive),
        (_, false, true) => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbOtherDone),
        _ => AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbOther),
    };
}

/// <summary>One fixed slot inside the Scheme C 5-line sliding live ticker window (mutated in place, zero allocation).</summary>
public sealed partial class WorkTickerLineViewModel : ObservableObject
{
    [ObservableProperty] public partial bool IsVisible { get; set; }
    [ObservableProperty] public partial double RowOpacity { get; set; } = 1.0;
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial bool IsFailed { get; set; }
    [ObservableProperty] public partial bool IsThought { get; set; }
    [ObservableProperty] public partial object? StepKey { get; set; }
    [ObservableProperty] public partial string Verb { get; set; } = "";
    [ObservableProperty] public partial string Target { get; set; } = "";
    [ObservableProperty] public partial string StatText { get; set; } = "";

    public bool IsCompleted => !IsRunning && !IsFailed;
    public bool IsActiveThought => IsThought && IsRunning;
    public bool HasStat => !string.IsNullOrEmpty(StatText);

    public void SetFrom(WorkStepViewModel step, double opacity)
    {
        var running = step.IsRunning;
        var failed = step.IsFailed;
        var thought = step.IsThought;
        var verb = running ? step.ActiveVerb : step.DoneVerb;
        var target = step.Target;
        var stat = step.TickerStatText;

        IsVisible = true;
        RowOpacity = opacity;
        var activeThoughtChanged = IsActiveThought != (thought && running);
        if (IsRunning != running)
        {
            IsRunning = running;
            OnPropertyChanged(nameof(IsCompleted));
        }
        if (IsFailed != failed)
        {
            IsFailed = failed;
            OnPropertyChanged(nameof(IsCompleted));
        }
        if (IsThought != thought)
        {
            IsThought = thought;
        }
        if (activeThoughtChanged)
        {
            OnPropertyChanged(nameof(IsActiveThought));
        }
        // Assign StepKey before Target so TickerTargetText knows whether this is the same step updating in place.
        StepKey = step;
        Verb = verb;
        Target = target;
        if (StatText != stat)
        {
            StatText = stat;
            OnPropertyChanged(nameof(HasStat));
        }
    }

    public void SetPreparing(object preparingKey)
    {
        IsVisible = true;
        RowOpacity = 1.0;
        var activeThoughtChanged = !IsActiveThought;
        if (!IsRunning)
        {
            IsRunning = true;
            OnPropertyChanged(nameof(IsCompleted));
        }
        if (IsFailed)
        {
            IsFailed = false;
            OnPropertyChanged(nameof(IsCompleted));
        }
        if (!IsThought)
        {
            IsThought = true;
        }
        if (activeThoughtChanged)
        {
            OnPropertyChanged(nameof(IsActiveThought));
        }
        StepKey = preparingKey;
        Verb = AppLangResources.Get(AppLangResourceKind.AgentGuiStepVerbThinkActive);
        Target = AppLangResources.Get(AppLangResourceKind.AgentGuiWorkPreparing);
        if (StatText.Length > 0)
        {
            StatText = "";
            OnPropertyChanged(nameof(HasStat));
        }
    }

    public void CopyFrom(WorkTickerLineViewModel source)
    {
        IsVisible = source.IsVisible;
        RowOpacity = source.RowOpacity;
        var activeThoughtChanged = IsActiveThought != source.IsActiveThought;
        if (IsRunning != source.IsRunning)
        {
            IsRunning = source.IsRunning;
            OnPropertyChanged(nameof(IsCompleted));
        }
        if (IsFailed != source.IsFailed)
        {
            IsFailed = source.IsFailed;
            OnPropertyChanged(nameof(IsCompleted));
        }
        if (IsThought != source.IsThought)
        {
            IsThought = source.IsThought;
        }
        if (activeThoughtChanged)
        {
            OnPropertyChanged(nameof(IsActiveThought));
        }
        StepKey = source.StepKey;
        Verb = source.Verb;
        Target = source.Target;
        if (StatText != source.StatText)
        {
            StatText = source.StatText;
            OnPropertyChanged(nameof(HasStat));
        }
    }

    public void Clear()
    {
        IsVisible = false;
        var activeThoughtChanged = IsActiveThought;
        if (IsRunning)
        {
            IsRunning = false;
            OnPropertyChanged(nameof(IsCompleted));
        }
        if (activeThoughtChanged)
        {
            OnPropertyChanged(nameof(IsActiveThought));
        }
        StepKey = null;
    }
}

/// <summary>
/// Fixed 5-slot live ticker window (Scheme C) materialized ONLY while a turn is actively running and collapsed.
/// Immediately detached (set to <c>null</c>) once the turn finishes or expands so idle turns keep zero ticker visuals.
/// </summary>
public sealed class WorkTickerViewModel : IWorkTickerSource
{
    public const int MaxLines = WorkTickerViewport.MaxVisibleRows;

    private static readonly object PreparingKey = new();

    private readonly WorkTickerLineViewModel[] _lines = CreateSlots(MaxLines);
    private readonly WorkTickerLineViewModel[] _exits = CreateSlots(MaxLines);
    private readonly object?[] _prevKeys = new object?[MaxLines];
    private readonly object?[] _nextKeys = new object?[MaxLines];
    private readonly WorkStepViewModel?[] _selected = new WorkStepViewModel?[MaxLines];

    private bool _hasSynced;
    private int _prevCount;

    private static WorkTickerLineViewModel[] CreateSlots(int count)
    {
        var slots = new WorkTickerLineViewModel[count];
        for (var i = 0; i < count; i++) slots[i] = new WorkTickerLineViewModel();
        return slots;
    }

    public WorkTickerLineViewModel Line0 => _lines[0];
    public WorkTickerLineViewModel Line1 => _lines[1];
    public WorkTickerLineViewModel Line2 => _lines[2];
    public WorkTickerLineViewModel Line3 => _lines[3];
    public WorkTickerLineViewModel Line4 => _lines[4];

    internal WorkTickerLineViewModel Exit0 => _exits[0];
    internal WorkTickerLineViewModel Exit1 => _exits[1];
    internal WorkTickerLineViewModel Exit2 => _exits[2];
    internal WorkTickerLineViewModel Exit3 => _exits[3];
    internal WorkTickerLineViewModel Exit4 => _exits[4];

    public int VisibleCount => _prevCount;

    object IWorkTickerSource.GetLine(int index) => _lines[index];
    object IWorkTickerSource.GetExit(int index) => _exits[index];

    public event Action<int, int>? Updated;

    public void ClearExiting()
    {
        for (var i = 0; i < _exits.Length; i++) _exits[i].Clear();
    }

    public void Sync(IReadOnlyList<WorkStepViewModel> steps)
    {
        Array.Clear(_selected);
        Array.Clear(_nextKeys);
        var count = 0;

        // Intermediate text steps stay in the expanded work area only; the 5-line live ticker focuses on work operations.
        var nonTextCount = 0;
        var runningCount = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].StepKind == WorkStepKind.Text) continue;
            nonTextCount++;
            if (steps[i].IsRunning) runningCount++;
        }

        var preparing = nonTextCount == 0;

        if (preparing)
        {
            count = 1;
            _nextKeys[0] = PreparingKey;
        }
        else
        {
            // Pick up to 5 items: prioritize currently running steps at the bottom (newest = opacity 1.0),
            // filled above with the most recent completed steps so height stays strictly O(1) <= 5 rows.
            void Push(WorkStepViewModel item)
            {
                _selected[count] = item;
                _nextKeys[count] = item;
                count++;
            }

            if (runningCount >= MaxLines)
            {
                var skipped = runningCount - MaxLines;
                for (var i = 0; i < steps.Count && count < MaxLines; i++)
                {
                    if (steps[i].StepKind == WorkStepKind.Text || !steps[i].IsRunning) continue;
                    if (skipped > 0) { skipped--; continue; }
                    Push(steps[i]);
                }
            }
            else if (runningCount > 0)
            {
                var doneSlots = MaxLines - runningCount;
                // Collect last `doneSlots` non-running steps in chronological order.
                var totalDone = nonTextCount - runningCount;
                var skipDone = Math.Max(0, totalDone - doneSlots);
                for (var i = 0; i < steps.Count && count < doneSlots; i++)
                {
                    if (steps[i].StepKind == WorkStepKind.Text || steps[i].IsRunning) continue;
                    if (skipDone > 0) { skipDone--; continue; }
                    Push(steps[i]);
                }
                for (var i = 0; i < steps.Count && count < MaxLines; i++)
                {
                    if (steps[i].StepKind != WorkStepKind.Text && steps[i].IsRunning) Push(steps[i]);
                }
            }
            else
            {
                var skip = Math.Max(0, nonTextCount - MaxLines);
                for (var i = 0; i < steps.Count && count < MaxLines; i++)
                {
                    if (steps[i].StepKind == WorkStepKind.Text) continue;
                    if (skip > 0) { skip--; continue; }
                    Push(steps[i]);
                }
            }
        }

        var shift = _hasSynced
            ? WorkTickerShift.Compute(_prevKeys, _prevCount, _nextKeys, count)
            : 0;
        var prevCount = _prevCount;

        if (shift > 0)
        {
            ShiftExiting(shift);
        }

        if (preparing)
        {
            _lines[0].SetPreparing(PreparingKey);
            for (var i = 1; i < MaxLines; i++) _lines[i].Clear();
        }
        else
        {
            for (var i = 0; i < count; i++)
            {
                _lines[i].SetFrom(_selected[i]!, WorkTickerViewport.ComputeRowOpacity(i, count));
            }
            for (var i = count; i < MaxLines; i++) _lines[i].Clear();
        }

        var wasSynced = _hasSynced;
        _hasSynced = true;
        _prevCount = count;
        Array.Copy(_nextKeys, _prevKeys, MaxLines);

        if (wasSynced && (shift > 0 || prevCount != count))
        {
            Updated?.Invoke(shift, count);
        }
    }

    private void ShiftExiting(int shift)
    {
        var clamped = Math.Clamp(shift, 1, MaxLines);
        for (var dst = MaxLines - 1; dst >= clamped; dst--)
        {
            _exits[dst].CopyFrom(_exits[dst - clamped]);
        }
        for (var i = 0; i < clamped; i++)
        {
            _exits[clamped - 1 - i].CopyFrom(_lines[i]);
        }
    }
}

/// <summary>
/// An independent Markdown response segment for an assistant turn.
/// Splits completed paragraphs/code-blocks into immutable <see cref="FrozenChunks"/> during long streaming replies
/// so Avalonia's Markdown renderer only re-parses the short active <see cref="LiveText"/> tail.
/// </summary>
public sealed partial class MarkdownBlockViewModel : ObservableObject
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

/// <summary>
/// An inline generated image canvas rendered by <see cref="ImageReveal"/> inside an <see cref="AssistantTurnItem"/>.
/// Supports live queued / generating / progress / preview / done / error transitions as well as direct ACP image blocks.
/// </summary>
public sealed partial class ImageGenerationItemViewModel : ObservableObject, IDisposable
{
    private readonly Action<string>? _openFile;
    private readonly string? _workingDirectory;
    private readonly List<Bitmap> _ownedBitmaps = [];
    private string? _lastSourceKey;
    private string? _cachedBase64;
    private string? _cachedMimeType;
    private string? _materializedTempPath;
    private bool _disposed;

    public ImageGenerationItemViewModel(
        string id,
        Action<string>? openFile = null,
        string? workingDirectory = null,
        bool isReplay = false)
    {
        Id = id;
        _openFile = openFile;
        _workingDirectory = workingDirectory;
        IsReplay = isReplay;
        Seed = Math.Abs(StringComparer.Ordinal.GetHashCode(id)) % 997 + 1;
        Labels = CreateLocalizedLabels();
    }

    public string Id { get; }
    public bool IsReplay { get; }
    public int Seed { get; }
    public ImageRevealLabels Labels { get; }

    [ObservableProperty] public partial IImage? Source { get; private set; }
    [ObservableProperty] public partial ImageRevealStatus? RevealStatus { get; private set; }
    [ObservableProperty] public partial double? Progress { get; private set; }
    [ObservableProperty] public partial double AspectRatio { get; private set; } = 1.0;
    [ObservableProperty] public partial string? Prompt { get; private set; }
    [ObservableProperty] public partial string? AltText { get; private set; }
    [ObservableProperty] public partial string? ErrorMessage { get; private set; }
    [ObservableProperty] public partial string? FilePath { get; private set; }

    public bool HasPrompt => !string.IsNullOrWhiteSpace(Prompt);
    public bool HasFilePath => !string.IsNullOrWhiteSpace(FilePath);
    public bool CanOpenImage => _openFile is not null
        && (RevealStatus is null or ImageRevealStatus.Done)
        && (!string.IsNullOrWhiteSpace(FilePath) || !string.IsNullOrWhiteSpace(_cachedBase64));

    public string FileDisplayName => string.IsNullOrWhiteSpace(FilePath)
        ? ""
        : Path.GetFileName(FilePath);

    [RelayCommand]
    private void OpenImage()
    {
        if (!CanOpenImage || _openFile is null) return;
        if (ResolveExistingFilePath(FilePath, _workingDirectory) is { } existing)
        {
            _openFile(existing);
            return;
        }
        if (TryMaterializeBase64ToTempFile() is { } tempPath)
        {
            _openFile(tempPath);
        }
    }

    public void ApplyToolUpdate(AcpToolCallUpdate update, AcpImageContent? image)
    {
        if (_disposed) return;

        if (!string.IsNullOrWhiteSpace(update.ImagePrompt))
        {
            SetPrompt(update.ImagePrompt!);
        }
        else if (string.IsNullOrWhiteSpace(Prompt) && !string.IsNullOrWhiteSpace(update.Description))
        {
            SetPrompt(update.Description!);
        }

        if (update.ImageAspectRatio is > 0.2 and <= 4.0)
        {
            AspectRatio = Math.Clamp(update.ImageAspectRatio.Value, 0.45, 2.4);
        }
        if (update.ImageProgress is >= 0.0 and <= 1.0)
        {
            Progress = update.ImageProgress.Value;
        }

        if (image is not null)
        {
            ApplyImageContent(image);
        }

        var status = update.Status?.Trim().ToLowerInvariant();
        if (status == "failed")
        {
            ErrorMessage = FirstNonEmpty(update.Stderr, update.TextOutputs.FirstOrDefault(), AppLangResources.Get(AppLangResourceKind.AgentGuiImageFailed));
            RevealStatus = ImageRevealStatus.Error;
        }
        else if (status == "completed")
        {
            if (Source is not null)
            {
                ErrorMessage = null;
                Progress = null;
                RevealStatus = IsReplay ? null : ImageRevealStatus.Done;
            }
            else
            {
                ErrorMessage = FirstNonEmpty(update.Stderr, update.TextOutputs.FirstOrDefault(), AppLangResources.Get(AppLangResourceKind.AgentGuiImageFailed));
                RevealStatus = ImageRevealStatus.Error;
            }
        }
        else if (status == "pending")
        {
            if (RevealStatus is null or ImageRevealStatus.Queued)
            {
                RevealStatus = ImageRevealStatus.Queued;
            }
        }
        else
        {
            RevealStatus = ImageRevealStatus.Generating;
        }

        NotifyComputedProperties();
    }

    public void ApplyDirectImage(AcpImageContent image)
    {
        if (_disposed) return;
        ApplyImageContent(image);
        if (Source is not null)
        {
            ErrorMessage = null;
            Progress = null;
            RevealStatus = IsReplay ? null : ImageRevealStatus.Done;
        }
        else
        {
            ErrorMessage = AppLangResources.Get(AppLangResourceKind.AgentGuiImageFailed);
            RevealStatus = ImageRevealStatus.Error;
        }
        NotifyComputedProperties();
    }

    public void SettleOnTurnComplete(string? turnError)
    {
        if (_disposed) return;
        if (RevealStatus is ImageRevealStatus.Queued or ImageRevealStatus.Generating)
        {
            if (Source is not null && string.IsNullOrWhiteSpace(turnError))
            {
                ErrorMessage = null;
                Progress = null;
                RevealStatus = ImageRevealStatus.Done;
            }
            else
            {
                ErrorMessage = string.IsNullOrWhiteSpace(turnError)
                    ? AppLangResources.Get(AppLangResourceKind.AgentGuiImageFailed)
                    : turnError;
                RevealStatus = ImageRevealStatus.Error;
            }
            NotifyComputedProperties();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Source = null;
        for (var i = 0; i < _ownedBitmaps.Count; i++)
        {
            try { _ownedBitmaps[i].Dispose(); } catch { }
        }
        _ownedBitmaps.Clear();
    }

    private void SetPrompt(string value)
    {
        var single = ToolCallItemViewModel.ToSingleLine(value);
        if (string.IsNullOrWhiteSpace(single)) return;
        if (Prompt != single)
        {
            Prompt = single;
            OnPropertyChanged(nameof(HasPrompt));
        }
        if (string.IsNullOrWhiteSpace(AltText))
        {
            AltText = single;
        }
    }

    private void ApplyImageContent(AcpImageContent image)
    {
        if (!string.IsNullOrWhiteSpace(image.AltText))
        {
            AltText = image.AltText;
            if (string.IsNullOrWhiteSpace(Prompt))
            {
                SetPrompt(image.AltText!);
            }
        }
        if (image.Width is > 0 && image.Height is > 0)
        {
            AspectRatio = Math.Clamp((double)image.Width.Value / image.Height.Value, 0.45, 2.4);
        }
        if (!string.IsNullOrWhiteSpace(image.FilePath))
        {
            FilePath = image.FilePath;
            OnPropertyChanged(nameof(HasFilePath));
            OnPropertyChanged(nameof(FileDisplayName));
        }
        if (!string.IsNullOrWhiteSpace(image.Base64Data))
        {
            _cachedBase64 = image.Base64Data;
            _cachedMimeType = image.MimeType ?? _cachedMimeType;
        }

        // Compute a lightweight fingerprint so identical updates don't re-decode bitmaps unnecessarily.
        var key = !string.IsNullOrEmpty(image.Base64Data)
            ? $"b64:{image.Base64Data.Length}:{(image.Base64Data.Length > 24 ? image.Base64Data[^24..] : image.Base64Data)}"
            : !string.IsNullOrEmpty(image.FilePath)
                ? $"file:{image.FilePath}"
                : null;
        if (key is null || string.Equals(key, _lastSourceKey, StringComparison.Ordinal))
        {
            return;
        }

        var decoded = TryDecodeBitmap(image, _workingDirectory);
        if (decoded is not null)
        {
            _lastSourceKey = key;
            _ownedBitmaps.Add(decoded);
            if (decoded.PixelSize.Width > 0 && decoded.PixelSize.Height > 0)
            {
                AspectRatio = Math.Clamp((double)decoded.PixelSize.Width / decoded.PixelSize.Height, 0.45, 2.4);
            }
            Source = decoded;
        }
    }

    private void NotifyComputedProperties()
    {
        OnPropertyChanged(nameof(HasPrompt));
        OnPropertyChanged(nameof(HasFilePath));
        OnPropertyChanged(nameof(FileDisplayName));
        OnPropertyChanged(nameof(CanOpenImage));
    }

    private string? TryMaterializeBase64ToTempFile()
    {
        if (!string.IsNullOrEmpty(_materializedTempPath) && File.Exists(_materializedTempPath))
        {
            return _materializedTempPath;
        }
        if (string.IsNullOrWhiteSpace(_cachedBase64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(_cachedBase64);
            var ext = _cachedMimeType?.ToLowerInvariant() switch
            {
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                "image/bmp" => ".bmp",
                _ => ".png",
            };
            var dir = Path.Combine(Path.GetTempPath(), "octopus-acp-images");
            Directory.CreateDirectory(dir);
            var safe = new StringBuilder(Id.Length);
            foreach (var ch in Id)
            {
                safe.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
            }
            var path = Path.Combine(dir, $"{safe}-{Seed}{ext}");
            File.WriteAllBytes(path, bytes);
            _materializedTempPath = path;
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? TryDecodeBitmap(AcpImageContent image, string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(image.Base64Data))
        {
            try
            {
                var bytes = Convert.FromBase64String(image.Base64Data);
                if (bytes.Length > 0)
                {
                    using var ms = new MemoryStream(bytes, writable: false);
                    return new Bitmap(ms);
                }
            }
            catch
            {
                // Fall back to file path if Base64 payload is incomplete or malformed.
            }
        }
        if (ResolveExistingFilePath(image.FilePath, workingDirectory) is { } resolved)
        {
            try
            {
                var bytes = File.ReadAllBytes(resolved);
                if (bytes.Length > 0)
                {
                    using var ms = new MemoryStream(bytes, writable: false);
                    return new Bitmap(ms);
                }
            }
            catch
            {
                // Ignore unreadable or unsupported file formats.
            }
        }
        return null;
    }

    private static string? ResolveExistingFilePath(string? path, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var candidate = path.Trim();
        if (Path.IsPathRooted(candidate))
        {
            return File.Exists(candidate) ? candidate : null;
        }
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var combined = Path.Combine(workingDirectory, candidate);
            if (File.Exists(combined)) return combined;
        }
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        for (var i = 0; i < candidates.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(candidates[i])) return candidates[i]!.Trim();
        }
        return "";
    }

    private static ImageRevealLabels CreateLocalizedLabels() => new()
    {
        Queued = AppLangResources.Get(AppLangResourceKind.AgentGuiImageQueued),
        Generating = AppLangResources.Get(AppLangResourceKind.AgentGuiImageGenerating),
        Done = AppLangResources.Get(AppLangResourceKind.AgentGuiImageDone),
        Failed = AppLangResources.Get(AppLangResourceKind.AgentGuiImageFailed),
        GeneratedImage = AppLangResources.Get(AppLangResourceKind.AgentGuiImageAltDefault),
    };
}

/// <summary>
/// An assistant response turn rendered with Scheme C:
/// 1) One unified pure-text Work Area (running summary + fixed 3-line sliding live ticker, auto-collapsed on finish),
/// 2) Inline generated images rendered with <see cref="ImageReveal"/>,
/// 3) Clean Markdown final response directly underneath.
/// Zero cards, zero per-turn timers, lazy-mounted expanded step details.
/// </summary>
public sealed partial class AssistantTurnItem : AgentTimelineItem, IDisposable
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

    private readonly Dictionary<string, (ToolCallItemViewModel Tool, WorkStepViewModel Step)> _toolsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageGenerationItemViewModel> _imagesById = new(StringComparer.Ordinal);
    private readonly List<WorkStepViewModel> _workSteps = [];
    private readonly Action<string>? _openFile;
    private readonly Action<string>? _openDiff;
    private readonly Action<ToolCallItemViewModel>? _openScript;
    private readonly string? _workingDirectory;
    private readonly Stopwatch? _elapsed;
    private readonly bool _isReplay;

    private WorkStepViewModel? _activeThoughtStep;
    private WorkStepViewModel? _planStep;
    private MarkdownBlockViewModel? _markdown;
    private WorkTickerViewModel? _ticker;
    private ObservableCollection<WorkStepViewModel>? _expandedWorkSteps;
    private int _phraseIndex = Random.Shared.Next(WorkingPhrases.Length);
    private int _statusTicks;
    private bool _hasThoughtContent;
    private bool _hasMarkdownContent;
    private bool _isWritingReply;
    private bool _markdownBreakPending;
    private string _workHeaderTitle = "";
    private string _workHeaderCounts = "";
    private string _workHeaderFailedText = "";

    public AssistantTurnItem(
        string agentId,
        string agentName,
        Action<string>? openFile = null,
        Action<string>? openDiff = null,
        Action<ToolCallItemViewModel>? openScript = null,
        bool isReplay = false,
        string? workingDirectory = null)
    {
        AgentId = agentId;
        AgentName = agentName;
        _openFile = openFile;
        _openDiff = openDiff;
        _openScript = openScript;
        _workingDirectory = workingDirectory;
        _isReplay = isReplay;
        if (isReplay)
        {
            IsStreaming = false;
        }
        else
        {
            _elapsed = Stopwatch.StartNew();
        }
        RefreshWorkSummary();
        SyncLiveTicker();
    }

    public string AgentId { get; }
    public string AgentName { get; }

    /// <summary>Computed on demand from thought steps so large thought streams are never duplicated in memory.</summary>
    public string ThoughtText
    {
        get
        {
            string? single = null;
            StringBuilder? sb = null;
            for (var i = 0; i < _workSteps.Count; i++)
            {
                if (_workSteps[i].StepKind != WorkStepKind.Thought) continue;
                var text = _workSteps[i].ThoughtText;
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
    }

    /// <summary>Final Markdown response text, computed on demand from the single Markdown block.</summary>
    public string MarkdownText => _markdown?.Text ?? "";

    /// <summary>Only materialized for the Copy button once streaming has finished, avoiding LOH allocations while streaming.</summary>
    public string CopyReplyValue => IsStreaming || !_hasMarkdownContent ? "" : MarkdownText;

    /// <summary>Single Markdown block bound directly below the unified work area (null until text arrives).</summary>
    public MarkdownBlockViewModel? ResponseMarkdown => _markdown;

    /// <summary>All recorded work steps (thinking, tools, plan) in chronological order.</summary>
    public IReadOnlyList<WorkStepViewModel> WorkSteps => _workSteps;

    /// <summary>Lazy UI collection: null when collapsed (zero visual controls), materialized on manual expand.</summary>
    public ObservableCollection<WorkStepViewModel>? ExpandedWorkSteps => _expandedWorkSteps;

    /// <summary>Fixed 5-line sliding ticker bound ONLY while running and collapsed; null otherwise.</summary>
    public WorkTickerViewModel? LiveTicker => ShowLiveTicker ? _ticker : null;

    /// <summary>Retained for remote mobile sync (<see cref="Remote.HostConversations"/>) and tool callbacks; not bound to cards.</summary>
    public ObservableCollection<ToolCallItemViewModel> ToolCalls { get; } = [];
    public ObservableCollection<PlanEntryViewModel> PlanEntries { get; } = [];
    /// <summary>Inline image generation outputs rendered with <see cref="ImageReveal"/> directly inside the turn.</summary>
    public ObservableCollection<ImageGenerationItemViewModel> GeneratedImages { get; } = [];

    [ObservableProperty] public partial string? PlanMarkdown { get; set; }
    [ObservableProperty] public partial bool IsStreaming { get; set; } = true;
    [ObservableProperty] public partial bool IsWaitingForInput { get; set; }
    [ObservableProperty] public partial bool IsWorkExpanded { get; set; }
    [ObservableProperty] public partial string? ErrorText { get; set; }
    [ObservableProperty] public partial string? StopReasonNotice { get; set; }
    [ObservableProperty] public partial string? CompactionNotice { get; set; }
    [ObservableProperty] public partial string DurationText { get; set; } = "";
    [ObservableProperty] public partial string LiveElapsedText { get; set; } = "";

    public bool HasThought => _hasThoughtContent;
    public bool HasPlan => PlanEntries.Count > 0 || !string.IsNullOrWhiteSpace(PlanMarkdown);
    public bool HasToolCalls => ToolCalls.Count > 0;
    public bool HasGeneratedImages => GeneratedImages.Count > 0;
    public bool HasMarkdown => _hasMarkdownContent;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public bool HasStopReasonNotice => !string.IsNullOrWhiteSpace(StopReasonNotice);
    public bool HasCompactionNotice => !string.IsNullOrWhiteSpace(CompactionNotice);
    public bool HasDuration => !string.IsNullOrEmpty(DurationText);
    public bool HasLiveElapsed => !string.IsNullOrEmpty(LiveElapsedText);
    public bool CanCopyReply => !IsStreaming && _hasMarkdownContent;

    public int WorkStepCount => _workSteps.Count;
    public bool HasWorkArea => IsStreaming || _workSteps.Count > 0;
    public bool CanExpandWork => _workSteps.Count > 0;
    public bool ShowLiveTicker => IsStreaming && !IsWorkExpanded && !IsWaitingForInput && !_isWritingReply;
    public bool ShowLiveStatus => IsStreaming && !IsWaitingForInput;
    public bool ShowWaitingBadge => IsStreaming && IsWaitingForInput;
    public string LiveStatusText => AppLangResources.Get(WorkingPhrases[_phraseIndex]);

    public string WorkHeaderTitle => _workHeaderTitle;
    public string WorkHeaderCounts => _workHeaderCounts;
    public bool HasWorkHeaderCounts => !string.IsNullOrEmpty(_workHeaderCounts);
    public string WorkHeaderFailedText => _workHeaderFailedText;
    public bool HasWorkFailures => !string.IsNullOrEmpty(_workHeaderFailedText);
    public string WorkHeaderDurationText => IsStreaming ? LiveElapsedText : DurationText;
    public bool HasWorkHeaderDuration => !string.IsNullOrEmpty(WorkHeaderDurationText);

    partial void OnIsWorkExpandedChanged(bool value)
    {
        if (value && _workSteps.Count > 0)
        {
            _expandedWorkSteps = new ObservableCollection<WorkStepViewModel>(_workSteps);
        }
        else
        {
            // Collapse nested step details and release the expanded visual tree immediately.
            if (_expandedWorkSteps is not null)
            {
                for (var i = 0; i < _workSteps.Count; i++)
                {
                    if (_workSteps[i].IsExpanded) _workSteps[i].IsExpanded = false;
                }
                _expandedWorkSteps = null;
            }
        }
        OnPropertyChanged(nameof(ExpandedWorkSteps));
        SyncLiveTicker();
    }

    [RelayCommand]
    private void ToggleWorkExpanded()
    {
        if (CanExpandWork) IsWorkExpanded = !IsWorkExpanded;
    }

    public void SetWaitingForInput(bool waiting)
    {
        if (IsWaitingForInput == waiting) return;
        IsWaitingForInput = waiting;
        OnPropertyChanged(nameof(ShowLiveStatus));
        OnPropertyChanged(nameof(ShowWaitingBadge));
        SyncLiveTicker();
    }

    /// <summary>Advances the rotating whimsical live status phrase immediately.</summary>
    public void AdvanceLiveStatus()
    {
        if (!IsStreaming) return;
        _phraseIndex = (_phraseIndex + 1) % WorkingPhrases.Length;
        OnPropertyChanged(nameof(LiveStatusText));
    }

    /// <summary>Called once per second by the tab's single shared timer while this turn is streaming.</summary>
    public void TickElapsed()
    {
        if (!IsStreaming || _elapsed is null) return;
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
            OnPropertyChanged(nameof(WorkHeaderDurationText));
            OnPropertyChanged(nameof(HasWorkHeaderDuration));
        }
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

    public void AppendThought(string delta, string? messageId = null)
    {
        if (string.IsNullOrEmpty(delta)) return;

        var hadWork = HasWorkArea;
        var hadExpandable = CanExpandWork;

        if (_activeThoughtStep is not null
            && _workSteps.Count > 0
            && ReferenceEquals(_workSteps[^1], _activeThoughtStep)
            && CanContinueMessage(_activeThoughtStep.MessageId, messageId))
        {
            if (string.IsNullOrEmpty(_activeThoughtStep.MessageId) && !string.IsNullOrEmpty(messageId))
            {
                _activeThoughtStep.MessageId = messageId;
            }
            _activeThoughtStep.AppendThought(delta);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(delta)) return;
            ArchiveCurrentMarkdownToWorkStep();
            _activeThoughtStep?.FinishThought();
            var step = WorkStepViewModel.CreateThought(delta, messageId, isActive: IsStreaming);
            _activeThoughtStep = step;
            AddWorkStep(step);
        }

        _isWritingReply = false;

        var becameNonEmpty = !_hasThoughtContent && !string.IsNullOrWhiteSpace(delta);
        if (becameNonEmpty) _hasThoughtContent = true;

        RefreshWorkSummary();
        SyncLiveTicker();

        OnPropertyChanged(nameof(ThoughtText));
        if (becameNonEmpty) OnPropertyChanged(nameof(HasThought));
        if (hadWork != HasWorkArea) OnPropertyChanged(nameof(HasWorkArea));
        if (hadExpandable != CanExpandWork) OnPropertyChanged(nameof(CanExpandWork));
    }

    public void AppendMessage(string delta, string? messageId = null)
    {
        if (string.IsNullOrEmpty(delta)) return;

        _activeThoughtStep?.FinishThought();
        _activeThoughtStep = null;

        if (_markdown is null)
        {
            if (string.IsNullOrWhiteSpace(delta)) return;
            _markdown = new MarkdownBlockViewModel(delta, messageId, sealedInitial: !IsStreaming);
            OnPropertyChanged(nameof(ResponseMarkdown));
        }
        else
        {
            if (_markdownBreakPending && !string.IsNullOrWhiteSpace(delta))
            {
                _markdownBreakPending = false;
                if (_markdown.CharLength > 0 && !_markdown.LiveText.EndsWith("\n\n", StringComparison.Ordinal))
                {
                    _markdown.Append("\n\n");
                }
            }
            _markdown.Append(delta);
        }

        var becameNonEmpty = !_hasMarkdownContent && !string.IsNullOrWhiteSpace(delta);
        if (becameNonEmpty) _hasMarkdownContent = true;

        // Collapse the 3-line ticker as soon as final reply text starts streaming (unless a tool is still running).
        if (!HasAnyRunningTool() && !_isWritingReply)
        {
            _isWritingReply = true;
            SyncLiveTicker();
        }

        OnPropertyChanged(nameof(MarkdownText));
        if (becameNonEmpty)
        {
            OnPropertyChanged(nameof(HasMarkdown));
            OnPropertyChanged(nameof(CanCopyReply));
        }
    }

    public void UpsertToolCall(AcpToolCallUpdate update)
    {
        var hadWork = HasWorkArea;
        var hadExpandable = CanExpandWork;

        _activeThoughtStep?.FinishThought();
        _activeThoughtStep = null;

        var id = string.IsNullOrWhiteSpace(update.ToolCallId) ? $"tool-{ToolCalls.Count + 1}" : update.ToolCallId;
        if (!_toolsById.TryGetValue(id, out var entry))
        {
            ArchiveCurrentMarkdownToWorkStep();
            var vm = new ToolCallItemViewModel(id, _openFile, _openDiff, _openScript, isReplay: _isReplay);
            vm.Apply(update);
            var step = WorkStepViewModel.CreateTool(vm);
            _toolsById[id] = (vm, step);
            ToolCalls.Add(vm);
            AddWorkStep(step);
            OnPropertyChanged(nameof(HasToolCalls));
        }
        else
        {
            entry.Tool.Apply(update);
            entry.Step.NotifyToolUpdated();
        }

        _isWritingReply = false;

        if (update.IsImageGeneration || update.Images.Count > 0)
        {
            UpsertImageGeneration(id, update);
        }

        RefreshWorkSummary();
        SyncLiveTicker();

        if (hadWork != HasWorkArea) OnPropertyChanged(nameof(HasWorkArea));
        if (hadExpandable != CanExpandWork) OnPropertyChanged(nameof(CanExpandWork));
    }

    public void AddGeneratedImage(AcpImageContent image, string? messageId = null)
    {
        var key = !string.IsNullOrWhiteSpace(messageId)
            ? $"msg-img:{messageId}:{GeneratedImages.Count + 1}"
            : $"msg-img:{GeneratedImages.Count + 1}";
        var vm = new ImageGenerationItemViewModel(key, _openFile, _workingDirectory, isReplay: _isReplay);
        vm.ApplyDirectImage(image);
        _imagesById[key] = vm;
        var hadImages = HasGeneratedImages;
        GeneratedImages.Add(vm);
        if (!hadImages) OnPropertyChanged(nameof(HasGeneratedImages));
    }

    private void UpsertImageGeneration(string toolCallId, AcpToolCallUpdate update)
    {
        var hadImages = HasGeneratedImages;
        if (update.Images.Count <= 1)
        {
            var vm = GetOrCreateImageItem(toolCallId);
            vm.ApplyToolUpdate(update, update.Images.Count == 1 ? update.Images[0] : null);
        }
        else
        {
            for (var i = 0; i < update.Images.Count; i++)
            {
                var slotId = i == 0 ? toolCallId : $"{toolCallId}#{i + 1}";
                var vm = GetOrCreateImageItem(slotId);
                vm.ApplyToolUpdate(update, update.Images[i]);
            }
        }
        if (hadImages != HasGeneratedImages)
        {
            OnPropertyChanged(nameof(HasGeneratedImages));
        }
    }

    private ImageGenerationItemViewModel GetOrCreateImageItem(string key)
    {
        if (_imagesById.TryGetValue(key, out var existing))
        {
            return existing;
        }
        var created = new ImageGenerationItemViewModel(key, _openFile, _workingDirectory, isReplay: _isReplay);
        _imagesById[key] = created;
        GeneratedImages.Add(created);
        return created;
    }

    public void SetPlan(IReadOnlyList<AcpPlanEntry> entries, string? markdown)
    {
        var hadWork = HasWorkArea;
        var hadExpandable = CanExpandWork;

        PlanEntries.Clear();
        foreach (var e in entries) PlanEntries.Add(new PlanEntryViewModel(e));
        if (!string.IsNullOrWhiteSpace(markdown)) PlanMarkdown = markdown;

        if (HasPlan)
        {
            if (_planStep is null)
            {
                ArchiveCurrentMarkdownToWorkStep();
                _planStep = WorkStepViewModel.CreatePlan(PlanEntries, PlanMarkdown);
                AddWorkStep(_planStep);
            }
            else
            {
                _planStep.UpdatePlan(PlanMarkdown);
            }
        }

        RefreshWorkSummary();
        SyncLiveTicker();
        OnPropertyChanged(nameof(HasPlan));
        if (hadWork != HasWorkArea) OnPropertyChanged(nameof(HasWorkArea));
        if (hadExpandable != CanExpandWork) OnPropertyChanged(nameof(CanExpandWork));
    }

    public void ClearPlan()
    {
        PlanEntries.Clear();
        PlanMarkdown = null;
        if (_planStep is not null)
        {
            _workSteps.Remove(_planStep);
            _expandedWorkSteps?.Remove(_planStep);
            _planStep = null;
            OnPropertyChanged(nameof(WorkStepCount));
        }
        RefreshWorkSummary();
        SyncLiveTicker();
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasWorkArea));
        OnPropertyChanged(nameof(CanExpandWork));
    }

    public void SetCompactionNotice(string? summary)
    {
        var label = AppLangResources.Get(AppLangResourceKind.AgentGuiCompactionNotice);
        CompactionNotice = string.IsNullOrWhiteSpace(summary) ? label : $"{label}: {summary.Trim()}";
        OnPropertyChanged(nameof(HasCompactionNotice));
    }

    public void Complete(string? error = null, bool showDuration = true, string? stopReasonNotice = null)
    {
        _activeThoughtStep?.FinishThought();
        _activeThoughtStep = null;
        for (var i = 0; i < _workSteps.Count; i++)
        {
            _workSteps[i].Seal();
        }
        for (var i = 0; i < GeneratedImages.Count; i++)
        {
            GeneratedImages[i].SettleOnTurnComplete(error);
        }
        _markdown?.Seal();

        // If the agent produced no text after its final tool call, surface the last emitted text as the final reply.
        if (!_hasMarkdownContent && string.IsNullOrWhiteSpace(error))
        {
            PromoteLastTextStepToResponse();
        }

        IsStreaming = false;
        IsWaitingForInput = false;
        _isWritingReply = false;

        // Auto-collapse the entire work area once complete so only the 1-line summary + response text remain.
        if (IsWorkExpanded)
        {
            IsWorkExpanded = false;
        }
        _ticker = null;

        if (_elapsed is not null)
        {
            _elapsed.Stop();
            if (showDuration && _elapsed.Elapsed.TotalMilliseconds >= 100)
            {
                DurationText = ToolCallItemViewModel.FormatShortDuration(_elapsed.Elapsed.TotalSeconds);
                OnPropertyChanged(nameof(HasDuration));
            }
        }
        if (!string.IsNullOrWhiteSpace(error))
        {
            ErrorText = error;
            OnPropertyChanged(nameof(HasError));
        }
        if (!string.IsNullOrWhiteSpace(stopReasonNotice))
        {
            StopReasonNotice = stopReasonNotice;
            OnPropertyChanged(nameof(HasStopReasonNotice));
        }

        RefreshWorkSummary();
        OnPropertyChanged(nameof(HasWorkArea));
        OnPropertyChanged(nameof(CanCopyReply));
        OnPropertyChanged(nameof(CopyReplyValue));
        OnPropertyChanged(nameof(ShowLiveStatus));
        OnPropertyChanged(nameof(ShowWaitingBadge));
        OnPropertyChanged(nameof(ShowLiveTicker));
        OnPropertyChanged(nameof(LiveTicker));
        OnPropertyChanged(nameof(WorkHeaderDurationText));
        OnPropertyChanged(nameof(HasWorkHeaderDuration));
    }

    /// <summary>Folds any intermediate text emitted before a subsequent work step into the unified work area.</summary>
    private void ArchiveCurrentMarkdownToWorkStep()
    {
        if (_markdown is null) return;
        _markdown.Seal();
        var text = _markdown.Text;
        var messageId = _markdown.MessageId;
        _markdown = null;
        _hasMarkdownContent = false;
        _markdownBreakPending = false;
        if (!string.IsNullOrWhiteSpace(text))
        {
            AddWorkStep(WorkStepViewModel.CreateText(text, messageId));
        }
        OnPropertyChanged(nameof(ResponseMarkdown));
        OnPropertyChanged(nameof(MarkdownText));
        OnPropertyChanged(nameof(HasMarkdown));
        OnPropertyChanged(nameof(CanCopyReply));
    }

    private void PromoteLastTextStepToResponse()
    {
        for (var i = _workSteps.Count - 1; i >= 0; i--)
        {
            if (_workSteps[i].StepKind != WorkStepKind.Text) continue;
            var step = _workSteps[i];
            _workSteps.RemoveAt(i);
            _expandedWorkSteps?.Remove(step);
            _markdown = new MarkdownBlockViewModel(step.NoteText, step.MessageId, sealedInitial: true);
            _hasMarkdownContent = !string.IsNullOrWhiteSpace(step.NoteText);
            OnPropertyChanged(nameof(WorkStepCount));
            OnPropertyChanged(nameof(CanExpandWork));
            OnPropertyChanged(nameof(ResponseMarkdown));
            OnPropertyChanged(nameof(MarkdownText));
            OnPropertyChanged(nameof(HasMarkdown));
            break;
        }
    }

    private void AddWorkStep(WorkStepViewModel step)
    {
        _workSteps.Add(step);
        _expandedWorkSteps?.Add(step);
        OnPropertyChanged(nameof(WorkStepCount));
    }

    private bool HasAnyRunningTool()
    {
        for (var i = 0; i < ToolCalls.Count; i++)
        {
            if (ToolCalls[i].IsRunning) return true;
        }
        return false;
    }

    private void SyncLiveTicker()
    {
        var show = ShowLiveTicker;
        if (show)
        {
            var created = _ticker is null;
            _ticker ??= new WorkTickerViewModel();
            _ticker.Sync(_workSteps);
            OnPropertyChanged(nameof(ShowLiveTicker));
            if (created) OnPropertyChanged(nameof(LiveTicker));
        }
        else
        {
            var hadTicker = _ticker is not null;
            _ticker = null;
            OnPropertyChanged(nameof(ShowLiveTicker));
            if (hadTicker) OnPropertyChanged(nameof(LiveTicker));
        }
    }

    private void RefreshWorkSummary()
    {
        int exec = 0, search = 0, read = 0, edit = 0, fetch = 0, think = 0, plan = 0, image = 0, other = 0, failed = 0;
        for (var i = 0; i < _workSteps.Count; i++)
        {
            var step = _workSteps[i];
            if (step.IsFailed) failed++;
            switch (step.StepKind)
            {
                case WorkStepKind.Thought:
                    think++;
                    break;
                case WorkStepKind.Plan:
                    plan++;
                    break;
                case WorkStepKind.Tool:
                    switch (step.Tool?.Kind)
                    {
                        case "execute": exec++; break;
                        case "search": search++; break;
                        case "read": read++; break;
                        case "edit" or "delete" or "move": edit++; break;
                        case "fetch": fetch++; break;
                        case "think": think++; break;
                        case "image": image++; break;
                        default: other++; break;
                    }
                    break;
                case WorkStepKind.Text:
                    // Folded intermediate text is intentionally omitted from header counts and step totals.
                    break;
            }
        }

        var countedSteps = exec + search + read + edit + fetch + think + plan + image + other;

        string nextTitle;
        string nextCounts;
        if (IsStreaming)
        {
            nextTitle = AppLangResources.Get(AppLangResourceKind.AgentGuiWorkAreaLabel);
            var parts = new List<string>(6);
            if (exec > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkCountExec, exec));
            if (search > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkCountSearch, search));
            if (read > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkCountRead, read));
            if (edit > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkCountEdit, edit));
            if (fetch > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkCountFetch, fetch));
            if (image > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkCountImage, image));
            if (think > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkCountThink, think));
            if (plan > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkCountPlan, plan));
            if (other > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkCountOther, other));
            nextCounts = parts.Count > 0
                ? string.Join(" · ", parts)
                : AppLangResources.Get(AppLangResourceKind.AgentGuiWorkPreparing);
        }
        else
        {
            nextTitle = countedSteps switch
            {
                0 => AppLangResources.Get(AppLangResourceKind.AgentGuiWorkAreaLabel),
                1 => AppLangResources.Get(AppLangResourceKind.AgentGuiWorkCompletedSingleStep),
                _ => AppLangResources.Format(AppLangResourceKind.AgentGuiWorkCompletedSteps, countedSteps),
            };
            var parts = new List<string>(6);
            if (exec > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkShortExec, exec));
            if (search > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkShortSearch, search));
            if (read > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkShortRead, read));
            if (edit > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkShortEdit, edit));
            if (fetch > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkShortFetch, fetch));
            if (image > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkShortImage, image));
            if (think > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkShortThink, think));
            if (plan > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkShortPlan, plan));
            if (other > 0) parts.Add(AppLangResources.Format(AppLangResourceKind.AgentGuiWorkShortOther, other));
            nextCounts = parts.Count > 0 ? $"· {string.Join(" · ", parts)}" : "";
        }

        var nextFailed = failed > 0
            ? $"· {AppLangResources.Format(AppLangResourceKind.AgentGuiWorkFailedCount, failed)}"
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

    public void Dispose()
    {
        for (var i = 0; i < GeneratedImages.Count; i++)
        {
            GeneratedImages[i].Dispose();
        }
        GeneratedImages.Clear();
        _imagesById.Clear();
    }

    private static bool CanContinueMessage(string? existingId, string? incomingId) =>
        string.IsNullOrEmpty(existingId)
        || string.IsNullOrEmpty(incomingId)
        || string.Equals(existingId, incomingId, StringComparison.Ordinal);
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

/// <summary>Interactive inline permission approval strip (never auto-approved when user confirmation is required).</summary>
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
        ValueType = item.ValueType;
        Required = item.Required;
        MultiSelect = item.MultiSelect;
        AllowFreeText = item.AllowFreeText || item.Options.Count == 0;
        foreach (var opt in item.Options)
        {
            Choices.Add(new QuestionChoiceViewModel(opt, OnChoiceToggled));
        }
        if (!string.IsNullOrEmpty(item.DefaultValue))
        {
            var matched = Choices.FirstOrDefault(c => string.Equals(c.Id, item.DefaultValue, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                matched.IsSelected = true;
            }
            else if (AllowFreeText)
            {
                FreeText = item.DefaultValue;
            }
        }
        else if (Choices.Count > 0 && !MultiSelect)
        {
            Choices[0].IsSelected = true;
        }
    }

    public string Id { get; }
    public string Prompt { get; }
    public string ValueType { get; }
    public bool Required { get; }
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

/// <summary>Interactive inline elicitation / question prompt (ACP <c>elicitation/create</c> mode=form or dialect questions).</summary>
public sealed partial class QuestionCardItem : AgentTimelineItem
{
    private readonly Action<AcpFormElicitationResponse> _resolve;

    public QuestionCardItem(AcpQuestionRequest request, Action<IReadOnlyDictionary<string, IReadOnlyList<string>>?> resolve)
        : this(request, resp => resolve(resp.Action == AcpElicitationAction.Accept ? resp.Answers : null))
    {
    }

    public QuestionCardItem(AcpQuestionRequest request, Action<AcpFormElicitationResponse> resolve)
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
        _resolve(new AcpFormElicitationResponse(AcpElicitationAction.Accept, answers));
    }

    [RelayCommand]
    private void Decline()
    {
        if (IsResolved) return;
        IsResolved = true;
        ResolvedSummary = AppLangResources.Get(AppLangResourceKind.AgentGuiQuestionDecline);
        _resolve(new AcpFormElicitationResponse(AcpElicitationAction.Decline));
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsResolved) return;
        IsResolved = true;
        ResolvedSummary = AppLangResources.Get(AppLangResourceKind.AgentGuiQuestionCancel);
        _resolve(new AcpFormElicitationResponse(AcpElicitationAction.Cancel));
    }
}

/// <summary>Interactive out-of-band URL elicitation card (ACP <c>elicitation/create</c> mode=url).</summary>
public sealed partial class UrlElicitationCardItem : AgentTimelineItem
{
    private readonly Action<AcpElicitationAction> _resolve;
    private readonly Action<string>? _openUrl;

    public UrlElicitationCardItem(
        AcpUrlElicitationRequest request,
        Action<AcpElicitationAction> resolve,
        Action<string>? openUrl = null)
    {
        _resolve = resolve;
        _openUrl = openUrl;
        ElicitationId = request.ElicitationId;
        Title = AppLangResources.Get(AppLangResourceKind.AgentGuiUrlElicitationTitle);
        Message = request.Message;
        Url = request.Url;
    }

    public string ElicitationId { get; }
    public string Title { get; }
    public string Message { get; }
    public string Url { get; }

    [ObservableProperty] public partial bool IsResolved { get; set; }
    [ObservableProperty] public partial string ResolvedSummary { get; set; } = "";

    [RelayCommand]
    private void OpenUrl() => _openUrl?.Invoke(Url);

    [RelayCommand]
    private void Confirm()
    {
        if (IsResolved) return;
        IsResolved = true;
        ResolvedSummary = AppLangResources.Get(AppLangResourceKind.AgentGuiUrlElicitationConfirm);
        _resolve(AcpElicitationAction.Accept);
    }

    [RelayCommand]
    private void Decline()
    {
        if (IsResolved) return;
        IsResolved = true;
        ResolvedSummary = AppLangResources.Get(AppLangResourceKind.AgentGuiUrlElicitationDecline);
        _resolve(AcpElicitationAction.Decline);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsResolved) return;
        IsResolved = true;
        ResolvedSummary = AppLangResources.Get(AppLangResourceKind.AgentGuiPermissionCancel);
        _resolve(AcpElicitationAction.Cancel);
    }

    public void MarkCompletedFromServer()
    {
        if (IsResolved) return;
        IsResolved = true;
        ResolvedSummary = AppLangResources.Get(AppLangResourceKind.AgentGuiUrlElicitationConfirm);
        _resolve(AcpElicitationAction.Accept);
    }
}

/// <summary>Action button representing one ACP <c>authMethods</c> entry in the Auth Required banner.</summary>
public sealed class AuthMethodActionViewModel(AcpAuthMethod method, Func<AcpAuthMethod, Task> run)
{
    public AcpAuthMethod Method { get; } = method;
    public string Id => Method.Id;
    public string Label => string.IsNullOrWhiteSpace(Method.Name) ? Method.Id : Method.Name;
    public string? Description => Method.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool IsTerminalAuth => string.Equals(Method.Type, "terminal", StringComparison.OrdinalIgnoreCase);
    public IAsyncRelayCommand RunCommand { get; } = new AsyncRelayCommand(() => run(method));
}

/// <summary>One ACP saved session row returned by <c>session/list</c>.</summary>
public sealed class AcpSessionListEntryViewModel(
    AcpSessionInfo info,
    bool isCurrent,
    bool canDelete,
    Func<string, Task> onLoad,
    Func<string, Task> onDelete)
{
    public string SessionId { get; } = info.SessionId;
    public string Title { get; } = string.IsNullOrWhiteSpace(info.Title) ? info.SessionId : info.Title!;
    public string? Cwd { get; } = info.Cwd;
    public string UpdatedAtText { get; } = info.UpdatedAt?.ToLocalTime().ToString("g") ?? "";
    public bool HasUpdatedAt => !string.IsNullOrEmpty(UpdatedAtText);
    public bool IsCurrent { get; } = isCurrent;
    public bool CanDelete { get; } = canDelete && !isCurrent;
    public IAsyncRelayCommand LoadCommand { get; } = new AsyncRelayCommand(() => onLoad(info.SessionId));
    public IAsyncRelayCommand DeleteCommand { get; } = new AsyncRelayCommand(() => onDelete(info.SessionId));
}
