using System.Buffers;
using System.Text.Json;

namespace Octopus.Agents.Subagents;

/// <summary>One subagent created by an Antigravity CLI parent conversation.</summary>
public sealed record AgySubagentSpawn(string ConversationId, string TranscriptPath);

/// <summary>One message or error delivered back to an Antigravity CLI parent conversation for a subagent.</summary>
public sealed record AgySubagentMessage(string ConversationId, string Content, bool IsError);

/// <summary>Incremental update read from an Antigravity CLI parent conversation's <c>transcript.jsonl</c>.</summary>
public sealed record AgyParentTranscriptDelta(
    long FromOffset,
    long ToOffset,
    IReadOnlyList<AgySubagentSpawn> Spawns,
    IReadOnlyList<AgySubagentMessage> Messages);

/// <summary>One tool call made inside an Antigravity CLI subagent's own <c>transcript.jsonl</c>.</summary>
public sealed record AgySubagentToolCall(string Name, string Kind, string? Target);

/// <summary>Incremental update read from an Antigravity CLI subagent's own <c>transcript.jsonl</c>.</summary>
public sealed record AgyChildTranscriptDelta(
    long FromOffset,
    long ToOffset,
    IReadOnlyList<AgySubagentToolCall> ToolCalls,
    int CompletedResults,
    int FailedResults,
    string? FinalContent);

/// <summary>
/// Incrementally tails Antigravity CLI (<c>agy</c>) conversation transcripts at
/// <c>&lt;appDataDir&gt;/brain/&lt;conversationId&gt;/.system_generated/logs/transcript.jsonl</c>.
/// Because <c>agy acp</c> completes <c>invoke_subagent</c> as soon as the child is spawned without streaming the
/// subagent's conversation id, nested tool calls, or completion over ACP, reading these local transcripts recovers
/// live subagent steps, summaries, and completion/error status with minimal I/O (a single <see cref="FileInfo.Length"/>
/// stat per tick when unchanged).
/// </summary>
public static class AgySubagentTranscript
{
    public const int MaxReadBytes = 4 * 1024 * 1024;
    private const int MaxTargetLength = 100;
    private const string SpawnMarker = "Created the following subagent:";
    private const string MessageMarker = "[Message]";
    private const string SystemMessageClose = "</SYSTEM_MESSAGE>";

    /// <summary>Returns the transcript path for an Antigravity CLI conversation id.</summary>
    public static string TranscriptPathFor(string conversationId, string? appDataDir = null)
    {
        var root = string.IsNullOrWhiteSpace(appDataDir)
            ? DefaultAppDataDir()
            : appDataDir!;
        return Path.Combine(root, "brain", conversationId, ".system_generated", "logs", "transcript.jsonl");
    }

    public static string DefaultAppDataDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".gemini", "antigravity-cli");
    }

    /// <summary>Returns the current file length (0 if missing), used to baseline a parent transcript before live launches.</summary>
    public static long CurrentLength(string transcriptPath)
    {
        try
        {
            var info = new FileInfo(transcriptPath);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Reads newly appended lines of a parent conversation's transcript for subagent spawns and replies.</summary>
    public static AgyParentTranscriptDelta ReadParentDelta(string transcriptPath, long offset, string? appDataDir = null)
    {
        var spawns = new List<AgySubagentSpawn>();
        var messages = new List<AgySubagentMessage>();
        var end = ReadLines(transcriptPath, offset, line => ScanParentLine(line, appDataDir, spawns, messages));
        return new AgyParentTranscriptDelta(offset, end, spawns, messages);
    }

    /// <summary>Reads newly appended lines of a subagent's own transcript for its tool calls and results.</summary>
    public static AgyChildTranscriptDelta ReadChildDelta(string transcriptPath, long offset)
    {
        var calls = new List<AgySubagentToolCall>();
        var completed = 0;
        var failed = 0;
        string? finalContent = null;
        var end = ReadLines(transcriptPath, offset, line => ScanChildLine(line, calls, ref completed, ref failed, ref finalContent));
        return new AgyChildTranscriptDelta(offset, end, calls, completed, failed, finalContent);
    }

    private delegate void LineVisitor(ReadOnlySpan<byte> line);

    private static long ReadLines(string transcriptPath, long offset, LineVisitor visitor)
    {
        var end = offset;
        byte[]? buffer = null;
        try
        {
            var info = new FileInfo(transcriptPath);
            if (!info.Exists || info.Length == offset)
            {
                return end;
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
                        visitor(slice);
                    }
                    lineStart += newline + 1;
                }
                end = offset + lineStart;
                if (lineStart == 0 && length == MaxReadBytes)
                {
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
        return end;
    }

    private static void ScanParentLine(
        ReadOnlySpan<byte> line,
        string? appDataDir,
        List<AgySubagentSpawn> spawns,
        List<AgySubagentMessage> messages)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            var type = TryString(root, "type");
            var content = TryString(root, "content");
            if (string.IsNullOrEmpty(content)) return;

            if (string.Equals(type, "GENERIC", StringComparison.Ordinal))
            {
                var markerIdx = content.IndexOf(SpawnMarker, StringComparison.Ordinal);
                if (markerIdx < 0) return;

                var braceStart = content.IndexOf('{', markerIdx + SpawnMarker.Length);
                if (braceStart < 0) return;
                var braceEnd = FindMatchingBrace(content, braceStart);
                if (braceEnd <= braceStart) return;

                var jsonBlock = content.Substring(braceStart, braceEnd - braceStart + 1);
                using var spawnDoc = JsonDocument.Parse(jsonBlock);
                var convId = TryString(spawnDoc.RootElement, "conversationId");
                if (string.IsNullOrWhiteSpace(convId)) return;

                var logUri = TryString(spawnDoc.RootElement, "logAbsoluteUri");
                var logPath = ResolveFileUri(logUri) ?? TranscriptPathFor(convId!, appDataDir);
                spawns.Add(new AgySubagentSpawn(convId!, logPath));
            }
            else if (string.Equals(type, "SYSTEM_MESSAGE", StringComparison.Ordinal))
            {
                var msgIdx = content.IndexOf(MessageMarker, StringComparison.Ordinal);
                if (msgIdx < 0) return;

                var senderIdx = content.IndexOf("sender=", msgIdx, StringComparison.Ordinal);
                if (senderIdx < 0) return;
                senderIdx += "sender=".Length;
                var senderEnd = senderIdx;
                while (senderEnd < content.Length && !char.IsWhiteSpace(content[senderEnd])) senderEnd++;
                if (senderEnd <= senderIdx) return;
                var sender = content[senderIdx..senderEnd];

                var contentKeyIdx = content.IndexOf("content=", senderEnd, StringComparison.Ordinal);
                if (contentKeyIdx < 0) return;
                var bodyStart = contentKeyIdx + "content=".Length;
                var closeIdx = content.IndexOf(SystemMessageClose, bodyStart, StringComparison.Ordinal);
                var body = (closeIdx >= bodyStart ? content[bodyStart..closeIdx] : content[bodyStart..]).Trim();

                if (string.Equals(sender, "system", StringComparison.OrdinalIgnoreCase))
                {
                    // Example: The subagent <role> (<type>) with id "<uuid>" encountered an error ...
                    var idMarker = "with id \"";
                    var idIdx = body.IndexOf(idMarker, StringComparison.Ordinal);
                    if (idIdx >= 0)
                    {
                        var idStart = idIdx + idMarker.Length;
                        var idEnd = body.IndexOf('"', idStart);
                        if (idEnd > idStart)
                        {
                            var subId = body[idStart..idEnd];
                            var isError = body.Contains("error", StringComparison.OrdinalIgnoreCase)
                                || body.Contains("failed", StringComparison.OrdinalIgnoreCase);
                            if (isError)
                            {
                                messages.Add(new AgySubagentMessage(subId, body, IsError: true));
                            }
                        }
                    }
                }
                else if (!sender.Contains('/'))
                {
                    // Background shell task messages use "<parentId>/task-<n>"; subagent replies use the bare conversationId.
                    messages.Add(new AgySubagentMessage(sender, body, IsError: false));
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    private static void ScanChildLine(
        ReadOnlySpan<byte> line,
        List<AgySubagentToolCall> calls,
        ref int completed,
        ref int failed,
        ref string? finalContent)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            var type = TryString(root, "type");
            var status = TryString(root, "status");

            if (string.Equals(type, "PLANNER_RESPONSE", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("tool_calls", out var tcArr)
                    && tcArr.ValueKind == JsonValueKind.Array
                    && tcArr.GetArrayLength() > 0)
                {
                    foreach (var tc in tcArr.EnumerateArray())
                    {
                        if (tc.ValueKind != JsonValueKind.Object) continue;
                        var name = TryString(tc, "name");
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var args = tc.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object
                            ? a
                            : default;
                        calls.Add(DescribeToolCall(name!, args));
                    }
                }
                else if (string.Equals(status, "DONE", StringComparison.Ordinal)
                    && TryString(root, "content") is { Length: > 0 } text
                    && !string.IsNullOrWhiteSpace(text))
                {
                    finalContent = text.Trim();
                }
            }
            else if (string.Equals(type, "GENERIC", StringComparison.Ordinal))
            {
                if (string.Equals(status, "DONE", StringComparison.Ordinal))
                {
                    completed++;
                }
                else if (string.Equals(status, "ERROR", StringComparison.Ordinal)
                    || string.Equals(status, "FAILED", StringComparison.Ordinal))
                {
                    failed++;
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    private static AgySubagentToolCall DescribeToolCall(string name, JsonElement args)
    {
        var kind = name switch
        {
            "read_file" or "view_file" or "list_dir" => "read",
            "edit_file" or "write_file" or "multi_replace_file_content" => "edit",
            "run_command" or "manage_task" => "execute",
            "search_web" or "read_url_content" => "fetch",
            "grep_search" or "find_by_name" => "search",
            _ => "other",
        };

        string? target = null;
        if (args.ValueKind == JsonValueKind.Object)
        {
            target = name switch
            {
                "run_command" => UnquoteArg(args, "toolSummary") ?? FirstLine(UnquoteArg(args, "CommandLine")),
                "read_file" or "edit_file" or "write_file" => FileTail(UnquoteArg(args, "Path")),
                "search_web" => UnquoteArg(args, "query"),
                "invoke_subagent" => UnquoteArg(args, "Role") ?? UnquoteArg(args, "TypeName"),
                "send_message" => "Report to parent",
                "manage_task" => UnquoteArg(args, "Action"),
                _ => UnquoteArg(args, "toolSummary") ?? FileTail(UnquoteArg(args, "Path")) ?? UnquoteArg(args, "Action"),
            };
        }

        if (target is { Length: > MaxTargetLength })
        {
            target = target[..MaxTargetLength] + "…";
        }
        return new AgySubagentToolCall(name, kind, target);
    }

    /// <summary>
    /// In Antigravity <c>transcript.jsonl</c>, <c>tool_calls[].args</c> values are JSON-encoded strings
    /// (e.g. <c>"\"/path/to/file.cs\""</c>). Unwrap one layer of JSON string quoting when present.
    /// </summary>
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

    private static string? ResolveFileUri(string? uriText)
    {
        if (string.IsNullOrWhiteSpace(uriText)) return null;
        if (Uri.TryCreate(uriText, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }
        return null;
    }

    private static int FindMatchingBrace(string text, int openIndex)
    {
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static string? TryString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
