using System.Buffers;
using System.Text;
using System.Text.Json;
using Octopus.Agents.Sessions;

namespace Octopus.Agents.Subagents;

/// <summary>Kind of event parsed incrementally from a subagent's local JSONL transcript.</summary>
public enum SubagentTranscriptEventKind
{
    UserPrompt,
    Thought,
    ToolCall,
    ToolResult,
    AssistantText,
}

/// <summary>
/// One structured message event read incrementally from a subagent's local transcript (Antigravity CLI or Claude Code).
/// Designed to map directly onto the shared Agent GUI timeline items without retaining raw JSON payloads.
/// </summary>
public sealed record SubagentTranscriptEvent(
    SubagentTranscriptEventKind Kind,
    string? Text = null,
    string? ToolCallId = null,
    string? ToolName = null,
    string? ToolKind = null,
    string? ToolTarget = null,
    string? Command = null,
    string? FilePath = null,
    string? InputSummary = null,
    string? DiffOld = null,
    string? DiffNew = null,
    string? Output = null,
    bool IsError = false);

/// <summary>Result of one incremental read from <see cref="FromOffset"/> to <see cref="ToOffset"/>.</summary>
public sealed record SubagentTimelineDelta(
    long FromOffset,
    long ToOffset,
    bool Reset,
    IReadOnlyList<SubagentTranscriptEvent> Events);

/// <summary>
/// On-demand, incremental JSONL transcript reader for rendering a subagent's full message timeline
/// (user prompt, thinking, tool invocations + arguments + outputs/diffs, and Markdown replies)
/// with minimal allocations and zero background cost when no subagent is being inspected.
/// Supports both Antigravity CLI (<c>transcript.jsonl</c>) and Claude Code (<c>agent-&lt;id&gt;.jsonl</c>).
/// </summary>
public static class SubagentTimelineTranscript
{
    public const int MaxReadBytes = 4 * 1024 * 1024;
    private const int MaxToolOutputChars = 16 * 1024;
    private const int MaxInputSummaryChars = 240;
    private const int MaxTargetLength = 120;

    /// <summary>
    /// Reads newly appended lines of <paramref name="transcriptPath"/> starting at <paramref name="offset"/>.
    /// Returns immediately without opening the file when the file does not exist or its length equals <paramref name="offset"/>.
    /// </summary>
    public static SubagentTimelineDelta ReadDelta(string transcriptPath, long offset)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath))
        {
            return new SubagentTimelineDelta(offset, offset, Reset: false, []);
        }

        var requested = offset;
        var end = offset;
        var reset = false;
        var events = new List<SubagentTranscriptEvent>();
        byte[]? buffer = null;

        try
        {
            var info = new FileInfo(transcriptPath);
            if (!info.Exists || info.Length == offset)
            {
                return new SubagentTimelineDelta(requested, end, Reset: false, events);
            }

            using var stream = new FileStream(
                transcriptPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1);

            if (stream.Length < offset)
            {
                offset = 0;
                end = 0;
                reset = true;
            }

            var length = (int)Math.Min(stream.Length - offset, MaxReadBytes);
            if (length > 0)
            {
                stream.Seek(offset, SeekOrigin.Begin);
                buffer = ArrayPool<byte>.Shared.Rent(length);
                var data = buffer.AsSpan(0, length);
                stream.ReadExactly(data);

                var lineStart = 0;
                while (data[lineStart..].IndexOf((byte)'\n') is var newline and >= 0)
                {
                    var slice = data.Slice(lineStart, newline);
                    if (slice.Length > 0 && slice[^1] == (byte)'\r')
                    {
                        slice = slice[..^1];
                    }
                    if (slice.Length > 0)
                    {
                        ParseLine(slice, events);
                    }
                    lineStart += newline + 1;
                }

                end = offset + lineStart;
                if (lineStart == 0 && length == MaxReadBytes)
                {
                    // Skip a single oversized line rather than stalling forever.
                    end = offset + length;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return new SubagentTimelineDelta(requested, end, reset, events);
    }

    private static void ParseLine(ReadOnlySpan<byte> line, List<SubagentTranscriptEvent> events)
    {
        if (line[0] != (byte)'{') return;
        try
        {
            var reader = new Utf8JsonReader(line);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            var type = TryString(root, "type");
            if (string.IsNullOrEmpty(type)) return;

            switch (type)
            {
                // Antigravity CLI transcript format
                case "USER_INPUT":
                    ParseAgyUserInput(root, events);
                    break;
                case "PLANNER_RESPONSE":
                    ParseAgyPlannerResponse(root, events);
                    break;
                case "GENERIC":
                    ParseAgyGeneric(root, events);
                    break;

                // Claude Code subagent transcript format
                case "user":
                    ParseClaudeUser(root, events);
                    break;
                case "assistant":
                    ParseClaudeAssistant(root, events);
                    break;
            }
        }
        catch (JsonException)
        {
        }
    }

    // ---- Antigravity CLI format -----------------------------------------------------------------

    private static void ParseAgyUserInput(JsonElement root, List<SubagentTranscriptEvent> events)
    {
        var raw = TryString(root, "content");
        var text = AgySessionSource.ExtractUserRequest(raw);
        if (!string.IsNullOrWhiteSpace(text))
        {
            events.Add(new SubagentTranscriptEvent(SubagentTranscriptEventKind.UserPrompt, Text: text));
        }
    }

    private static void ParseAgyPlannerResponse(JsonElement root, List<SubagentTranscriptEvent> events)
    {
        var thought = TryString(root, "thought") ?? TryString(root, "thinking");
        if (!string.IsNullOrWhiteSpace(thought))
        {
            events.Add(new SubagentTranscriptEvent(SubagentTranscriptEventKind.Thought, Text: thought.Trim()));
        }

        if (root.TryGetProperty("tool_calls", out var tcArr)
            && tcArr.ValueKind == JsonValueKind.Array
            && tcArr.GetArrayLength() > 0)
        {
            foreach (var tc in tcArr.EnumerateArray())
            {
                if (tc.ValueKind != JsonValueKind.Object) continue;
                var name = TryString(tc, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var id = TryString(tc, "id");
                var args = tc.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
                events.Add(BuildAgyToolCall(id, name!, args));
            }
        }

        var content = TryString(root, "content");
        if (!string.IsNullOrWhiteSpace(content))
        {
            events.Add(new SubagentTranscriptEvent(SubagentTranscriptEventKind.AssistantText, Text: content.Trim()));
        }
    }

    private static void ParseAgyGeneric(JsonElement root, List<SubagentTranscriptEvent> events)
    {
        var status = TryString(root, "status");
        var isError = string.Equals(status, "ERROR", StringComparison.Ordinal)
            || string.Equals(status, "FAILED", StringComparison.Ordinal);
        var isDone = string.Equals(status, "DONE", StringComparison.Ordinal);
        if (!isDone && !isError) return;

        var body = CleanAgyToolOutput(TryString(root, "content"));
        if (string.IsNullOrWhiteSpace(body))
        {
            body = TryString(root, "error")?.Trim();
        }
        var toolCallId = TryString(root, "tool_call_id");
        events.Add(new SubagentTranscriptEvent(
            SubagentTranscriptEventKind.ToolResult,
            ToolCallId: toolCallId,
            Output: Truncate(body, MaxToolOutputChars),
            IsError: isError));
    }

    private static SubagentTranscriptEvent BuildAgyToolCall(string? id, string name, JsonElement args)
    {
        var kind = MapAgyToolKind(name);
        string? command = null;
        string? filePath = null;
        string? diffOld = null;
        string? diffNew = null;
        string? target = null;
        string? inputSummary = null;

        if (args.ValueKind == JsonValueKind.Object)
        {
            filePath = UnquoteArg(args, "Path")
                ?? UnquoteArg(args, "TargetFile")
                ?? UnquoteArg(args, "file_path");
            command = UnquoteArg(args, "CommandLine")
                ?? UnquoteArg(args, "command");

            if (name is "edit_file" or "replace_file_content")
            {
                diffOld = UnquoteArg(args, "Target") ?? UnquoteArg(args, "TargetContent");
                diffNew = UnquoteArg(args, "Replacement") ?? UnquoteArg(args, "ReplacementContent");
            }

            target = name switch
            {
                "run_command" => UnquoteArg(args, "toolSummary") ?? FirstLine(command),
                "read_file" or "view_file" or "edit_file" or "write_file" or "replace_file_content" => FileTail(filePath),
                "search_web" => UnquoteArg(args, "query"),
                "grep_search" or "find_by_name" => UnquoteArg(args, "Query") ?? UnquoteArg(args, "Pattern") ?? UnquoteArg(args, "SearchPath"),
                "invoke_subagent" => UnquoteArg(args, "Role") ?? UnquoteArg(args, "TypeName"),
                "send_message" => "Report to parent",
                "manage_task" => UnquoteArg(args, "Action"),
                _ => UnquoteArg(args, "toolSummary") ?? FileTail(filePath) ?? UnquoteArg(args, "Action"),
            };

            if (command is null && diffOld is null && diffNew is null)
            {
                var summaryVal = UnquoteArg(args, "query")
                    ?? UnquoteArg(args, "Query")
                    ?? UnquoteArg(args, "Pattern")
                    ?? UnquoteArg(args, "Message")
                    ?? UnquoteArg(args, "Prompt");
                if (!string.IsNullOrWhiteSpace(summaryVal))
                {
                    inputSummary = Truncate(summaryVal, MaxInputSummaryChars);
                }
            }
        }

        return new SubagentTranscriptEvent(
            SubagentTranscriptEventKind.ToolCall,
            ToolCallId: id,
            ToolName: name,
            ToolKind: kind,
            ToolTarget: Truncate(target, MaxTargetLength),
            Command: command,
            FilePath: filePath,
            InputSummary: inputSummary,
            DiffOld: diffOld,
            DiffNew: diffNew);
    }

    private static string MapAgyToolKind(string name) => name switch
    {
        "read_file" or "view_file" or "list_dir" => "read",
        "edit_file" or "write_file" or "replace_file_content" or "multi_replace_file_content" => "edit",
        "run_command" or "manage_task" => "execute",
        "search_web" or "read_url_content" => "fetch",
        "grep_search" or "find_by_name" => "search",
        _ => "other",
    };

    // ---- Claude Code format ---------------------------------------------------------------------

    private static void ParseClaudeUser(JsonElement root, List<SubagentTranscriptEvent> events)
    {
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return;
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
        if (!message.TryGetProperty("content", out var content)) return;

        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                events.Add(new SubagentTranscriptEvent(SubagentTranscriptEventKind.UserPrompt, Text: text));
            }
            return;
        }

        if (content.ValueKind != JsonValueKind.Array) return;

        StringBuilder? promptBuilder = null;
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            var partType = TryString(part, "type");
            if (partType == "tool_result")
            {
                var toolUseId = TryString(part, "tool_use_id");
                var isErr = part.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                var output = ExtractClaudeContentText(part);
                events.Add(new SubagentTranscriptEvent(
                    SubagentTranscriptEventKind.ToolResult,
                    ToolCallId: toolUseId,
                    Output: Truncate(output, MaxToolOutputChars),
                    IsError: isErr));
            }
            else if (partType == "text" && TryString(part, "text") is { Length: > 0 } txt)
            {
                promptBuilder ??= new StringBuilder();
                if (promptBuilder.Length > 0) promptBuilder.Append('\n');
                promptBuilder.Append(txt);
            }
        }

        if (promptBuilder is { Length: > 0 })
        {
            var prompt = promptBuilder.ToString().Trim();
            if (prompt.Length > 0)
            {
                events.Add(new SubagentTranscriptEvent(SubagentTranscriptEventKind.UserPrompt, Text: prompt));
            }
        }
    }

    private static void ParseClaudeAssistant(JsonElement root, List<SubagentTranscriptEvent> events)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            var partType = TryString(part, "type");
            switch (partType)
            {
                case "thinking":
                {
                    var thinking = TryString(part, "thinking") ?? TryString(part, "text");
                    if (!string.IsNullOrWhiteSpace(thinking))
                    {
                        events.Add(new SubagentTranscriptEvent(SubagentTranscriptEventKind.Thought, Text: thinking.Trim()));
                    }
                    break;
                }
                case "tool_use":
                {
                    var name = TryString(part, "name");
                    if (string.IsNullOrWhiteSpace(name)) break;
                    var id = TryString(part, "id");
                    var input = part.TryGetProperty("input", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
                    events.Add(BuildClaudeToolCall(id, name!, input));
                    break;
                }
                case "text":
                {
                    var text = TryString(part, "text");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        events.Add(new SubagentTranscriptEvent(SubagentTranscriptEventKind.AssistantText, Text: text.Trim()));
                    }
                    break;
                }
            }
        }
    }

    private static SubagentTranscriptEvent BuildClaudeToolCall(string? id, string name, JsonElement input)
    {
        var kind = name switch
        {
            "Read" or "NotebookRead" => "read",
            "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => "edit",
            "Grep" or "Glob" => "search",
            "Bash" => "execute",
            "WebFetch" or "WebSearch" => "fetch",
            _ => "other",
        };

        string? command = null;
        string? filePath = null;
        string? diffOld = null;
        string? diffNew = null;
        string? target = null;
        string? inputSummary = null;

        if (input.ValueKind == JsonValueKind.Object)
        {
            filePath = TryString(input, "file_path") ?? TryString(input, "notebook_path") ?? TryString(input, "path");
            command = TryString(input, "command");
            if (name is "Edit" or "MultiEdit")
            {
                diffOld = TryString(input, "old_string");
                diffNew = TryString(input, "new_string");
            }
            target = name switch
            {
                "Read" or "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => FileTail(filePath),
                "Grep" or "Glob" => TryString(input, "pattern") is { Length: > 0 } p ? $"\"{p}\"" : null,
                "Bash" => TryString(input, "description") ?? FirstLine(command),
                "WebFetch" => TryString(input, "url"),
                "WebSearch" => TryString(input, "query"),
                _ => TryString(input, "description") ?? FileTail(filePath),
            };
            if (command is null && diffOld is null && diffNew is null)
            {
                var summaryVal = TryString(input, "pattern") ?? TryString(input, "query") ?? TryString(input, "url");
                if (!string.IsNullOrWhiteSpace(summaryVal))
                {
                    inputSummary = Truncate(summaryVal, MaxInputSummaryChars);
                }
            }
        }

        return new SubagentTranscriptEvent(
            SubagentTranscriptEventKind.ToolCall,
            ToolCallId: id,
            ToolName: name,
            ToolKind: kind,
            ToolTarget: Truncate(target, MaxTargetLength),
            Command: command,
            FilePath: filePath,
            InputSummary: inputSummary,
            DiffOld: diffOld,
            DiffNew: diffNew);
    }

    private static string? ExtractClaudeContentText(JsonElement part)
    {
        if (!part.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString()?.Trim();
        if (content.ValueKind != JsonValueKind.Array) return null;

        var sb = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && TryString(item, "text") is { Length: > 0 } txt)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(txt);
                if (sb.Length >= MaxToolOutputChars) break;
            }
            else if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(s);
                if (sb.Length >= MaxToolOutputChars) break;
            }
        }
        return sb.Length > 0 ? sb.ToString().Trim() : null;
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static string? CleanAgyToolOutput(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var span = content.AsSpan();
        while (true)
        {
            var trimmed = span.TrimStart("\r\n");
            if (!trimmed.StartsWith("Created At:", StringComparison.Ordinal)
                && !trimmed.StartsWith("Completed At:", StringComparison.Ordinal))
            {
                return trimmed.ToString().Trim();
            }
            var nl = trimmed.IndexOf('\n');
            if (nl < 0) return null;
            span = trimmed[(nl + 1)..];
        }
    }

    private static string? UnquoteArg(JsonElement args, string prop)
    {
        if (!args.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var raw = v.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            try
            {
                using var parsed = JsonDocument.Parse(raw);
                if (parsed.RootElement.ValueKind == JsonValueKind.String)
                {
                    return parsed.RootElement.GetString();
                }
            }
            catch (JsonException)
            {
            }
        }
        return raw;
    }

    private static string? FileTail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.TrimEnd('/', '\\');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var nl = text.IndexOfAny(['\r', '\n']);
        var line = (nl >= 0 ? text[..nl] : text).Trim();
        return line.Length > 0 ? line : null;
    }

    private static string? Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length > maxChars ? text[..maxChars] + "…" : text;
    }

    private static string? TryString(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
