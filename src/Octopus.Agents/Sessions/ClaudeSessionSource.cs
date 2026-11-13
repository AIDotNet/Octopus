using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Octopus.Agents.Sessions;

/// <summary>
/// Claude Code's saved sessions: <c>&lt;account&gt;/projects/&lt;encoded path&gt;/&lt;session id&gt;.jsonl</c>, one JSON record per
/// line. Only <c>user</c> and <c>assistant</c> records and the two title records matter here; the rest (and the huge
/// <c>api-request-blob</c> lines, which are most of a big file) are skipped by their <c>"type"</c> prefix without
/// being copied or parsed.
/// </summary>
public sealed class ClaudeSessionSource : ISessionSource
{
    public const string Id = "claude";

    /// <summary>Longest title taken from a prompt, in characters.</summary>
    public const int MaxTitleLength = 80;

    private const int MaxSummaryLength = 200;
    private const int MaxOutputLength = 4096;

    /// <summary>User text starting with one of these is Claude Code's own bookkeeping, not something the person typed.</summary>
    private static readonly string[] WrapperPrefixes =
    [
        "<local-command-caveat>", "<command-name>", "<command-message>", "<command-args>", "<local-command-stdout>",
        "<local-command-stderr>", "<bash-input>", "<bash-stdout>", "<bash-stderr>", "<user-prompt-submit-hook>",
    ];

    private const string ReminderOpen = "<system-reminder>";
    private const string ReminderClose = "</system-reminder>";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>What a scan of one file found; kept until the file changes so that listing again costs a stat per file.</summary>
    private sealed record Scan(
        long Length,
        long WriteTicks,
        string? Cwd,
        string? Title,
        DateTimeOffset? Started,
        DateTimeOffset? Updated,
        int MessageCount,
        string? Model,
        string? GitBranch);

    private readonly ConcurrentDictionary<string, Scan> _cache = new(StringComparer.Ordinal);

    public string AgentId => Id;

    public Task<IReadOnlyList<AgentSessionInfo>> ListAsync(string accountDirectory, string projectPath, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<AgentSessionInfo>>(() => List(accountDirectory, projectPath, cancellationToken), cancellationToken);

    public Task<SessionTranscript> ReadAsync(AgentSessionInfo session, CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(session, cancellationToken), cancellationToken);

    private List<AgentSessionInfo> List(string accountDirectory, string projectPath, CancellationToken cancellationToken)
    {
        var result = new List<AgentSessionInfo>();
        foreach (var folder in ClaudeProjectFolder.Candidates(accountDirectory, projectPath))
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*.jsonl", SearchOption.TopDirectoryOnly);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scan = ScanCached(file, cancellationToken);
                if (scan is null || !SamePath(scan.Cwd, projectPath)) continue;
                var sessionId = Path.GetFileNameWithoutExtension(file);
                var fallback = new DateTimeOffset(scan.WriteTicks, TimeSpan.Zero);
                result.Add(new AgentSessionInfo(
                    Id,
                    accountDirectory,
                    sessionId,
                    scan.Title ?? sessionId,
                    scan.Started ?? scan.Updated ?? fallback,
                    scan.Updated ?? fallback,
                    scan.MessageCount,
                    scan.Model,
                    scan.GitBranch,
                    file));
            }
        }
        result.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return result;
    }

    /// <summary>The scan of a file, from the cache while its size and write time are unchanged; null for files that cannot be read or have no messages.</summary>
    private Scan? ScanCached(string path, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var length = info.Length;
            var ticks = info.LastWriteTimeUtc.Ticks;
            if (_cache.TryGetValue(path, out var cached) && cached.Length == length && cached.WriteTicks == ticks) return cached.Cwd is null ? null : cached;
            var scan = ScanFile(path, length, ticks, cancellationToken);
            _cache[path] = scan;
            return scan.Cwd is null ? null : scan;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Scan ScanFile(string path, long length, long ticks, CancellationToken cancellationToken)
    {
        string? cwd = null, custom = null, ai = null, firstPrompt = null, model = null, branch = null;
        DateTimeOffset? started = null, updated = null;
        var prompts = 0;
        var assistantIds = new HashSet<string>(StringComparer.Ordinal);
        var anonymousAssistant = 0;
        var any = false;
        ReadRecords(path, cancellationToken, root =>
        {
            switch (StringOf(root, "type"))
            {
                case "custom-title":
                    if (StringOf(root, "customTitle")?.Trim() is { Length: > 0 } c) custom = c;
                    return;
                case "ai-title":
                    if (StringOf(root, "aiTitle")?.Trim() is { Length: > 0 } a) ai = a;
                    return;
                case "user" or "assistant":
                    break;
                default:
                    return;
            }
            if (BoolOf(root, "isSidechain") || BoolOf(root, "isMeta")) return;
            any = true;
            cwd ??= StringOf(root, "cwd");
            if (StringOf(root, "gitBranch") is { Length: > 0 } b) branch = b;
            if (TimeOf(root) is { } time)
            {
                started ??= time;
                updated = time;
            }
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
            if (StringOf(root, "type") == "user")
            {
                if (UserText(message) is { } text)
                {
                    prompts++;
                    firstPrompt ??= TitleFromPrompt(text) is { Length: > 0 } t ? t : null;
                }
                return;
            }
            if (StringOf(message, "model") is { Length: > 0 } m && m != "<synthetic>") model = m;
            if (AssistantText(message) is null) return;
            if (StringOf(message, "id") is { Length: > 0 } id) assistantIds.Add(id);
            else anonymousAssistant++;
        });
        var title = custom ?? ai ?? firstPrompt;
        return new Scan(length, ticks, any ? cwd ?? "" : null, title, started, updated, prompts + assistantIds.Count + anonymousAssistant, model, branch);
    }

    private static SessionTranscript Read(AgentSessionInfo session, CancellationToken cancellationToken)
    {
        var turns = new List<TurnBuilder>();
        var assistantByMessage = new Dictionary<string, TurnBuilder>(StringComparer.Ordinal);
        var toolByUse = new Dictionary<string, TurnBuilder>(StringComparer.Ordinal);
        ReadRecords(session.FilePath, cancellationToken, root =>
        {
            var type = StringOf(root, "type");
            if (type is not ("user" or "assistant")) return;
            if (BoolOf(root, "isSidechain") || BoolOf(root, "isMeta")) return;
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
            var time = TimeOf(root);
            if (type == "user")
            {
                if (UserText(message) is { } text)
                {
                    turns.Add(new TurnBuilder(SessionTurnRole.User, time) { Text = text });
                }
                AttachToolResults(message, toolByUse);
                return;
            }
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
            var messageId = StringOf(message, "id");
            foreach (var block in content.EnumerateArray())
            {
                switch (StringOf(block, "type"))
                {
                    case "text" when StringOf(block, "text") is { } t && !string.IsNullOrWhiteSpace(t):
                        if (messageId is not null && assistantByMessage.TryGetValue(messageId, out var existing))
                        {
                            existing.Text = existing.Text.Length == 0 ? t : existing.Text + "\n" + t;
                        }
                        else
                        {
                            var turn = new TurnBuilder(SessionTurnRole.Assistant, time) { Text = t };
                            turns.Add(turn);
                            if (messageId is not null) assistantByMessage[messageId] = turn;
                        }
                        break;
                    case "tool_use":
                        var tool = new TurnBuilder(SessionTurnRole.Tool, time) { ToolName = StringOf(block, "name") ?? "" };
                        if (block.TryGetProperty("input", out var input))
                        {
                            tool.Text = Summarize(input);
                            tool.Files = FilesOf(tool.ToolName, input);
                        }
                        turns.Add(tool);
                        if (StringOf(block, "id") is { Length: > 0 } useId) toolByUse[useId] = tool;
                        break;
                }
            }
        });
        return new SessionTranscript(session, turns.ConvertAll(t => t.Build()));
    }

    private sealed class TurnBuilder(SessionTurnRole role, DateTimeOffset? time)
    {
        public string Text { get; set; } = "";
        public string? ToolName { get; set; }
        public string? Output { get; set; }
        public IReadOnlyList<string>? Files { get; set; }

        public SessionTurn Build() => new(role, Text, time, ToolName, Output, Files);
    }

    private static void AttachToolResults(JsonElement message, Dictionary<string, TurnBuilder> toolByUse)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var block in content.EnumerateArray())
        {
            if (StringOf(block, "type") != "tool_result" || StringOf(block, "tool_use_id") is not { } id) continue;
            if (!toolByUse.TryGetValue(id, out var tool)) continue;
            if (!block.TryGetProperty("content", out var body)) continue;
            var text = body.ValueKind == JsonValueKind.String ? body.GetString() : JoinText(body);
            if (!string.IsNullOrEmpty(text)) tool.Output = Cut(text, MaxOutputLength);
        }
    }

    private static string? JoinText(JsonElement blocks)
    {
        if (blocks.ValueKind != JsonValueKind.Array) return null;
        StringBuilder? text = null;
        foreach (var block in blocks.EnumerateArray())
        {
            if (StringOf(block, "type") == "text" && StringOf(block, "text") is { } t)
            {
                (text ??= new StringBuilder()).Append(text.Length > 0 ? "\n" : "").Append(t);
            }
        }
        return text?.ToString();
    }

    /// <summary>What the person typed in a user record; null for tool results, wrappers, reminders and interruption markers.</summary>
    private static string? UserText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return null;
        var parts = new List<string>();
        if (content.ValueKind == JsonValueKind.String)
        {
            parts.Add(content.GetString() ?? "");
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (StringOf(block, "type") == "text") parts.Add(StringOf(block, "text") ?? "");
            }
        }
        var kept = new List<string>();
        foreach (var part in parts)
        {
            var text = StripReminders(part).Trim();
            if (text.Length == 0 || text.StartsWith("[Request interrupted by user", StringComparison.Ordinal)) continue;
            if (WrapperPrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal))) continue;
            kept.Add(text);
        }
        return kept.Count == 0 ? null : string.Join("\n", kept);
    }

    private static string? AssistantText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return string.IsNullOrWhiteSpace(content.GetString()) ? null : content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        foreach (var block in content.EnumerateArray())
        {
            if (StringOf(block, "type") == "text" && StringOf(block, "text") is { } t && !string.IsNullOrWhiteSpace(t)) return t;
        }
        return null;
    }

    private static string StripReminders(string text)
    {
        while (text.IndexOf(ReminderOpen, StringComparison.Ordinal) is var open and >= 0)
        {
            var close = text.IndexOf(ReminderClose, open, StringComparison.Ordinal);
            text = close < 0 ? text[..open] : text[..open] + text[(close + ReminderClose.Length)..];
        }
        return text;
    }

    /// <summary>A title from a prompt: its first non-empty line, whitespace collapsed, cut at <see cref="MaxTitleLength"/>.</summary>
    private static string TitleFromPrompt(string prompt)
    {
        var line = prompt.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return Cut(Collapse(line), MaxTitleLength);
    }

    private static string Summarize(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in (ReadOnlySpan<string>)["file_path", "command", "pattern", "url", "description", "prompt"])
            {
                if (StringOf(input, key) is { Length: > 0 } value) return Cut(Collapse(value), MaxSummaryLength);
            }
        }
        return Cut(input.GetRawText(), MaxSummaryLength);
    }

    private static IReadOnlyList<string>? FilesOf(string tool, JsonElement input)
    {
        if (tool is not ("Write" or "Edit" or "MultiEdit" or "NotebookEdit")) return null;
        var path = StringOf(input, "file_path") ?? StringOf(input, "notebook_path");
        return string.IsNullOrEmpty(path) ? null : [path];
    }

    private static string Collapse(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (result.Length > 0 && result[^1] != ' ') result.Append(' ');
            }
            else if (!char.IsControl(c))
            {
                result.Append(c);
            }
        }
        return result.ToString().Trim();
    }

    private static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = max;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut].TrimEnd() + "…";
    }

    private static bool SamePath(string? recorded, string projectPath) =>
        recorded is not null && string.Equals(recorded.TrimEnd('/', '\\'), projectPath.TrimEnd('/', '\\'), PathComparison);

    private static string? StringOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool BoolOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? TimeOf(JsonElement element) =>
        StringOf(element, "timestamp") is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            ? time
            : null;

    // ---- line reading ----

    private static readonly byte[] TypePrefix = "{\"type\":\""u8.ToArray();
    private const int PrefixProbe = 48;

    /// <summary>
    /// Whether a line is a record of a kind that never matters (judged from its start, before the whole line is in memory).
    /// Only lines that begin with the type are judged: Claude Code writes bookkeeping records that way, and conversation
    /// records with other keys first.
    /// </summary>
    private static bool IsIgnorable(ReadOnlySpan<byte> start)
    {
        if (!start.StartsWith(TypePrefix)) return false;
        var rest = start[TypePrefix.Length..];
        var end = rest.IndexOf((byte)'"');
        if (end < 0) return false;
        var name = rest[..end];
        return !(name.SequenceEqual("ai-title"u8) || name.SequenceEqual("custom-title"u8));
    }

    /// <summary>Streams a transcript's lines, parsing those not ignorable; unparsable lines are dropped.</summary>
    private static void ReadRecords(string path, CancellationToken cancellationToken, Action<JsonElement> handle)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        var buffer = new byte[64 * 1024];
        int start = 0, end = 0;
        var eof = false;
        var skipping = false;
        var lines = 0;
        while (true)
        {
            if ((++lines & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (skipping)
            {
                // Inside an ignorable line: drop bytes until its newline.
                var newline = buffer.AsSpan(start, end - start).IndexOf((byte)'\n');
                if (newline >= 0)
                {
                    start += newline + 1;
                    skipping = false;
                    continue;
                }
                start = end = 0;
                if (!Fill(stream, buffer, ref start, ref end, ref eof)) return;
                continue;
            }
            var span = buffer.AsSpan(start, end - start);
            var nl = span.IndexOf((byte)'\n');
            if (nl < 0)
            {
                if (!eof && span.Length < PrefixProbe)
                {
                    if (!Fill(stream, buffer, ref start, ref end, ref eof)) { }
                    continue;
                }
                if (IsIgnorable(span))
                {
                    skipping = true;
                    if (eof) return;
                    continue;
                }
                if (eof)
                {
                    if (span.Length > 0) Handle(span, handle);
                    return;
                }
                // A long line that matters: make room for the rest of it.
                if (start > 0)
                {
                    Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                    end -= start;
                    start = 0;
                }
                if (end == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
                var read = stream.Read(buffer, end, buffer.Length - end);
                if (read == 0) eof = true;
                end += read;
                continue;
            }
            var line = span[..nl];
            if (!IsIgnorable(line)) Handle(line, handle);
            start += nl + 1;
        }
    }

    /// <summary>Moves what is left to the front and reads more; false at the end of the file with nothing left.</summary>
    private static bool Fill(FileStream stream, byte[] buffer, ref int start, ref int end, ref bool eof)
    {
        if (start > 0)
        {
            Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
            end -= start;
            start = 0;
        }
        var read = stream.Read(buffer, end, buffer.Length - end);
        if (read == 0) eof = true;
        end += read;
        return !(eof && end == 0);
    }

    private static void Handle(ReadOnlySpan<byte> line, Action<JsonElement> handle)
    {
        line = line.Trim((byte)'\r');
        if (line.IsEmpty || line[0] != (byte)'{') return;
        try
        {
            var reader = new Utf8JsonReader(line);
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind == JsonValueKind.Object) handle(document.RootElement);
        }
        catch (JsonException)
        {
        }
    }
}
