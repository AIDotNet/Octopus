using System.Globalization;
using System.Text;

namespace Octopus.Agents.Sessions;

/// <summary>
/// Carries a session to any agent that takes a prompt: the conversation is written as a Markdown note the new agent
/// reads before it carries on. The prompt is a CLI argument (Windows caps command lines), so it holds a condensed
/// note of at most <see cref="MaxPromptLength"/> characters; the whole conversation goes to a file the prompt points at.
/// </summary>
public sealed class HandoffSessionConverter(TimeProvider? timeProvider = null) : ISessionConverter
{
    /// <summary>What a command line safely carries on every platform, with room left for the CLI's own arguments.</summary>
    public const int MaxPromptLength = 12_000;

    private const int RecentToolOutputs = 10;
    private const int PromptOutputLength = 600;
    private const int NoteOutputLength = 4096;
    private const int ToolLineLength = 200;
    private const int PromptTurnLength = 1500;
    private const int PromptInstructionLength = 1500;
    private const int PromptGoalLength = 3000;
    private const int PromptFilesLength = 1500;
    private const int PromptLastLength = 3000;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public SessionMigrationKind Kind => SessionMigrationKind.Handoff;

    public bool CanConvert(string sourceAgentId, string targetAgentId) => true;

    public async Task<SessionMigrationResult> ConvertAsync(SessionTranscript transcript, SessionMigrationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var t = request.Language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true ? Chinese : English;
        var parts = Parts.Of(transcript, request, t);

        string? notePath = null;
        if (request.HandoffDirectory is { } dir)
        {
            var note = parts.Note(request.IncludeToolOutput);
            Directory.CreateDirectory(dir);
            var name = $"{_time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Sanitize(transcript.Session.SessionId)}.md";
            notePath = Path.Combine(dir, name);
            await WriteAtomicAsync(notePath, note, cancellationToken).ConfigureAwait(false);
        }

        var prompt = parts.Prompt(request.IncludeToolOutput, notePath);
        // A CLI reads a leading '-' as a flag and a leading '/' as a slash command.
        if (prompt.Length > 0 && prompt[0] is '-' or '/') prompt = "# " + prompt;
        return new SessionMigrationResult(SessionMigrationKind.Handoff, Prompt: prompt, NotePath: notePath);
    }

    private static string Sanitize(string id)
    {
        var sb = new StringBuilder();
        foreach (var c in id)
        {
            if (sb.Length >= 64) break;
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }
        return sb.Length == 0 ? "session" : sb.ToString();
    }

    /// <summary>Temp file plus move, so a reader never meets a half-written note.</summary>
    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        var n = max;
        if (n > 0 && char.IsHighSurrogate(text[n - 1])) n--;
        return text[..n] + "…";
    }

    private static string OneLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Quote(string text)
    {
        var lines = text.ReplaceLineEndings("\n").TrimEnd().Split('\n');
        return string.Join('\n', lines.Select(l => l.Length == 0 ? ">" : "> " + l));
    }

    private static string Fence(string body)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in body)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}\n{body.TrimEnd()}\n{fence}";
    }

    private static string FormatTime(DateTimeOffset value) => value.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);

    private sealed record Strings(
        string Title,
        string Instruction,
        string Goal,
        string Files,
        string Conversation,
        string LastState,
        string User,
        string Assistant,
        string Omitted,
        string Unanswered,
        string NoReply,
        string FullNote);

    private static readonly Strings English = new(
        "Session handoff",
        "This session is continued from another agent (`{0}`). Session \"{1}\", original id `{2}`, project `{3}`, {4}. " +
        "Read the note below, briefly confirm what the task is and what state it is in, then wait for or continue with the user's next instruction. " +
        "Do NOT redo work that is already done, and do NOT trust the note blindly: check the files before you rely on them.",
        "Goal",
        "Files changed",
        "Conversation",
        "Last state",
        "User",
        "Assistant",
        "{0} earlier turns omitted.",
        "The last user message above has not been answered yet.",
        "The assistant has not replied yet.",
        "The full conversation is in this note: {0}");

    private static readonly Strings Chinese = new(
        "会话交接",
        "本会话由另一个智能体（`{0}`）接续而来。会话“{1}”，原会话 ID `{2}`，项目 `{3}`，{4}。" +
        "请先阅读下面的记录，简要确认任务是什么、进展到哪一步，然后等待或继续执行用户的下一条指令。" +
        "不要重做已经完成的工作，也不要盲目相信记录：依赖文件内容之前先检查它们。",
        "目标",
        "改动的文件",
        "对话",
        "最后状态",
        "用户",
        "助手",
        "省略了更早的 {0} 条记录。",
        "上面最后一条用户消息还没有得到回复。",
        "助手还没有回复。",
        "完整对话见此文件：{0}");

    /// <summary>The note's pieces, so the full note and the condensed prompt are cut from the same cloth.</summary>
    private sealed class Parts
    {
        private Strings _t = null!;
        private string _instruction = "";
        private string _goal = "";
        private IReadOnlyList<string> _files = [];
        private IReadOnlyList<SessionTurn> _kept = [];
        private int _omittedBefore;
        private string _lastText = "";
        private bool _unanswered;

        public static Parts Of(SessionTranscript transcript, SessionMigrationRequest request, Strings t)
        {
            var turns = transcript.Turns;
            var start = 0;
            if (request.RecentTurns is { } n)
            {
                var wanted = Math.Max(1, n);
                var seen = 0;
                for (var i = turns.Count - 1; i >= 0; i--)
                {
                    if (turns[i].Role != SessionTurnRole.User) continue;
                    if (++seen == wanted)
                    {
                        start = i;
                        break;
                    }
                }
            }

            var s = transcript.Session;
            var range = $"{FormatTime(s.StartedAt)} - {FormatTime(s.UpdatedAt)}";
            var goal = turns.FirstOrDefault(x => x.Role == SessionTurnRole.User && !string.IsNullOrWhiteSpace(x.Text))?.Text.Trim() ?? "";
            var lastAssistant = turns.LastOrDefault(x => x.Role == SessionTurnRole.Assistant && !string.IsNullOrWhiteSpace(x.Text))?.Text.Trim() ?? "";
            return new Parts
            {
                _t = t,
                _instruction = string.Format(CultureInfo.InvariantCulture, t.Instruction, s.AgentId, OneLine(s.Title), s.SessionId, request.ProjectPath, range),
                _goal = goal,
                _files = ChangedFiles(turns, request.ProjectPath),
                _kept = [.. turns.Skip(start)],
                _omittedBefore = start,
                _lastText = lastAssistant,
                _unanswered = turns.Count > 0 && turns[^1].Role == SessionTurnRole.User,
            };
        }

        private static List<string> ChangedFiles(IReadOnlyList<SessionTurn> turns, string projectPath)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var turn in turns)
            {
                if (turn.Files is null) continue;
                foreach (var file in turn.Files)
                {
                    if (string.IsNullOrWhiteSpace(file)) continue;
                    set.Add(Relative(file.Trim(), projectPath));
                }
            }
            return [.. set];
        }

        private static string Relative(string file, string projectPath)
        {
            if (Path.IsPathRooted(file) && !string.IsNullOrEmpty(projectPath))
            {
                var rel = Path.GetRelativePath(projectPath, file);
                if (!Path.IsPathRooted(rel) && rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !rel.StartsWith("../", StringComparison.Ordinal))
                {
                    return rel.Replace('\\', '/');
                }
            }
            return file.Replace('\\', '/');
        }

        private string Files(int maxLength)
        {
            if (_files.Count == 0) return "";
            var sb = new StringBuilder();
            for (var i = 0; i < _files.Count; i++)
            {
                var line = "- `" + _files[i] + "`\n";
                if (sb.Length + line.Length > maxLength)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"- … (+{_files.Count - i})\n");
                    break;
                }
                sb.Append(line);
            }
            return sb.ToString().TrimEnd();
        }

        private string Last(int maxLength)
        {
            var sb = new StringBuilder();
            sb.Append(_lastText.Length > 0 ? Cut(_lastText, maxLength) : _t.NoReply);
            if (_unanswered) sb.Append("\n\n").Append(_t.Unanswered);
            return sb.ToString();
        }

        private string RenderTurn(SessionTurn turn, bool withOutput, int outputLength, int textLength)
        {
            switch (turn.Role)
            {
                case SessionTurnRole.User:
                    return $"**{_t.User}:**\n\n{Quote(Cut(turn.Text.Trim(), textLength))}";
                case SessionTurnRole.Assistant:
                    return $"**{_t.Assistant}:**\n\n{Quote(Cut(turn.Text.Trim(), textLength))}";
                default:
                    var line = $"- {turn.ToolName ?? "tool"}: {Cut(OneLine(turn.Text), ToolLineLength)}";
                    if (withOutput && !string.IsNullOrWhiteSpace(turn.Output)) line += "\n\n" + Fence(Cut(turn.Output.Trim(), outputLength)) + "\n";
                    return line;
            }
        }

        /// <summary>Tool results are quoted for the last few calls only, to keep the size down.</summary>
        private List<string> Blocks(bool includeOutput, int outputLength, int textLength)
        {
            var toolIndexes = new List<int>();
            for (var i = 0; i < _kept.Count; i++)
            {
                if (_kept[i].Role == SessionTurnRole.Tool) toolIndexes.Add(i);
            }
            var withOutput = toolIndexes.Skip(Math.Max(0, toolIndexes.Count - RecentToolOutputs)).ToHashSet();
            var blocks = new List<string>(_kept.Count);
            for (var i = 0; i < _kept.Count; i++)
            {
                blocks.Add(RenderTurn(_kept[i], includeOutput && withOutput.Contains(i), outputLength, textLength));
            }
            return blocks;
        }

        private static string Join(IEnumerable<string> blocks)
        {
            // Consecutive tool lines stay a tight list; everything else is separated by a blank line.
            var sb = new StringBuilder();
            string? previous = null;
            foreach (var block in blocks)
            {
                if (previous is not null)
                {
                    var tight = previous.StartsWith("- ", StringComparison.Ordinal) && block.StartsWith("- ", StringComparison.Ordinal) && !previous.EndsWith('\n') && !previous.Contains('\n');
                    sb.Append(tight ? "\n" : "\n\n");
                }
                sb.Append(block);
                previous = block;
            }
            return sb.ToString();
        }

        private string Head(string instruction, string goal, string files) =>
            $"# {_t.Title}\n\n{instruction}\n\n## {_t.Goal}\n\n{Quote(goal)}\n\n" +
            (files.Length > 0 ? $"## {_t.Files}\n\n{files}\n\n" : "");

        public string Note(bool includeOutput)
        {
            var conversation = Join(Blocks(includeOutput, NoteOutputLength, int.MaxValue));
            var omitted = _omittedBefore > 0 ? string.Format(CultureInfo.InvariantCulture, "_" + _t.Omitted + "_\n\n", _omittedBefore) : "";
            return Head(_instruction, _goal, Files(int.MaxValue)) +
                   $"## {_t.Conversation}\n\n{omitted}{conversation}\n\n" +
                   $"## {_t.LastState}\n\n{Last(int.MaxValue)}\n";
        }

        public string Prompt(bool includeOutput, string? notePath)
        {
            var pointer = notePath is null ? "" : "\n\n" + string.Format(CultureInfo.InvariantCulture, _t.FullNote, notePath);
            var head = Head(Cut(_instruction, PromptInstructionLength), Cut(_goal, PromptGoalLength), Files(PromptFilesLength));
            var tail = $"## {_t.LastState}\n\n{Last(PromptLastLength)}{pointer}\n";
            var conversationHeader = $"## {_t.Conversation}\n\n";

            var blocks = Blocks(includeOutput, PromptOutputLength, PromptTurnLength);
            // Reserve room for the omission line, whose number is at most the turn count.
            var omissionReserve = string.Format(CultureInfo.InvariantCulture, "_" + _t.Omitted + "_\n\n", _kept.Count + _omittedBefore).Length + 2;
            var budget = MaxPromptLength - head.Length - tail.Length - conversationHeader.Length - omissionReserve - 2;

            var selected = new List<string>();
            var used = 0;
            for (var i = blocks.Count - 1; i >= 0; i--)
            {
                var cost = blocks[i].Length + 2;
                if (used + cost > budget) break;
                selected.Add(blocks[i]);
                used += cost;
            }
            selected.Reverse();

            var omitted = _omittedBefore + (blocks.Count - selected.Count);
            var omissionLine = omitted > 0 ? string.Format(CultureInfo.InvariantCulture, "_" + _t.Omitted + "_\n\n", omitted) : "";
            var prompt = head + conversationHeader + omissionLine + Join(selected) + "\n\n" + tail;
            // Last resort for pathological input (a huge project path or title): the limit wins.
            return prompt.Length <= MaxPromptLength ? prompt : prompt[..MaxPromptLength];
        }
    }
}
