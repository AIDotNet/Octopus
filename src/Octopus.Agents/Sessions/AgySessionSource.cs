using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Octopus.Agents.Sessions;

/// <summary>
/// Reads Antigravity CLI's (<c>agy</c>) saved conversations: the index lives in
/// <c>&lt;account&gt;/conversation_summaries.db</c> (SQLite, WAL mode) and each conversation's steps live in
/// <c>&lt;account&gt;/brain/&lt;conversation_id&gt;/.system_generated/logs/transcript.jsonl</c>. Child conversations
/// (subagents, <c>nesting_depth &gt; 0</c> or non-empty <c>parent_conversation_id</c>), killed ones and empty
/// zero-step conversations are skipped.
/// </summary>
public sealed class AgySessionSource : ISessionSource
{
    public const string Id = "agy";

    /// <summary>Longest title taken from a prompt or preview, in characters.</summary>
    public const int MaxTitleLength = 80;

    private const int MaxToolInputLength = 200;
    private const int MaxToolOutputLength = 4 * 1024;
    private const int ProbeLength = 256;

    private const string UserRequestOpen = "<USER_REQUEST>";
    private const string UserRequestClose = "</USER_REQUEST>";

    private static readonly (string Open, string Close)[] MetadataBlocks =
    [
        ("<ADDITIONAL_METADATA>", "</ADDITIONAL_METADATA>"),
        ("<USER_SETTINGS_CHANGE>", "</USER_SETTINGS_CHANGE>"),
        ("<SYSTEM_MESSAGE>", "</SYSTEM_MESSAGE>"),
    ];

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly ConcurrentDictionary<string, TranscriptScan> _cache = new(StringComparer.Ordinal);

    /// <summary>How many transcript files were parsed from disk (for tests: a cache hit does not add one).</summary>
    internal int FullScans => Volatile.Read(ref _fullScans);
    private int _fullScans;

    public string AgentId => Id;

    public string? DefaultDirectory(string home) => Path.Combine(home, ".gemini", "antigravity-cli");

    public Task<IReadOnlyList<AgentSessionInfo>> ListAsync(string accountDirectory, string projectPath, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<AgentSessionInfo>>(() => List(accountDirectory, projectPath, cancellationToken), cancellationToken);

    public Task<SessionTranscript> ReadAsync(AgentSessionInfo session, CancellationToken cancellationToken = default) =>
        Task.Run(() => new SessionTranscript(session, ReadTurns(session.FilePath, cancellationToken)), cancellationToken);

    private List<AgentSessionInfo> List(string accountDirectory, string projectPath, CancellationToken ct)
    {
        var dbPath = Path.Combine(accountDirectory, "conversation_summaries.db");
        if (!File.Exists(dbPath)) return [];

        var project = NormalizePath(projectPath);
        var rows = ReadSummaryRows(dbPath, project, ct);
        if (rows.Count == 0) return [];

        var result = new List<AgentSessionInfo>(rows.Count);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var transcriptPath = Path.Combine(accountDirectory, "brain", row.Id, ".system_generated", "logs", "transcript.jsonl");
            var scan = ScanCached(transcriptPath, ct);

            var dbTitle = NormalizeTitle(row.Title, MaxTitleLength * 2);
            var dbPreview = Shorten(row.Preview);
            var messageCount = scan?.MessageCount ?? (dbTitle is not null || dbPreview is not null ? 1 : 0);
            if (messageCount == 0 && dbTitle is null && dbPreview is null) continue;

            var dbUpdated = ParseTime(row.LastModifiedTime);
            var dbUserInput = ParseTime(row.LastUserInputTime);
            var fallbackTime = scan is not null ? new DateTimeOffset(scan.WriteTicks, TimeSpan.Zero) : DateTimeOffset.UtcNow;
            var started = Earliest(scan?.Started, dbUserInput, dbUpdated) ?? fallbackTime;
            var updated = Latest(scan?.Updated, dbUpdated, dbUserInput) ?? started;
            var title = dbTitle ?? dbPreview ?? scan?.FirstPrompt ?? row.Id;

            result.Add(new AgentSessionInfo(
                Id,
                accountDirectory,
                row.Id,
                title,
                started,
                updated,
                messageCount,
                scan?.Model,
                GitBranch: null,
                transcriptPath));
        }

        result.Sort(static (a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return result;
    }

    private static List<SummaryRow> ReadSummaryRows(string dbPath, string project, CancellationToken ct)
    {
        var rows = new List<SummaryRow>();
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 2,
            }.ToString();
            using var connection = new SqliteConnection(cs);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT conversation_id, title, preview, step_count, last_modified_time, workspace_uris, last_user_input_time
                FROM conversation_summaries
                WHERE (parent_conversation_id IS NULL OR parent_conversation_id = '')
                  AND nesting_depth = 0
                  AND killed = 0
                  AND step_count > 0
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var id = reader.IsDBNull(0) ? "" : reader.GetString(0);
                if (string.IsNullOrWhiteSpace(id)) continue;
                var workspaceUris = reader.IsDBNull(5) ? "" : reader.GetString(5);
                if (!MatchesProject(workspaceUris, project)) continue;
                rows.Add(new SummaryRow(
                    id,
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    reader.IsDBNull(6) ? "" : reader.GetString(6)));
            }
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return rows;
    }

    private TranscriptScan? ScanCached(string path, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var length = info.Length;
            var ticks = info.LastWriteTimeUtc.Ticks;
            if (_cache.TryGetValue(path, out var cached) && cached.Length == length && cached.WriteTicks == ticks)
            {
                return cached;
            }
            Interlocked.Increment(ref _fullScans);
            var scan = ScanFile(path, length, ticks, ct);
            _cache[path] = scan;
            return scan;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static TranscriptScan ScanFile(string path, long length, long ticks, CancellationToken ct)
    {
        DateTimeOffset? started = null, updated = null;
        string? model = null, firstPrompt = null;
        var messages = 0;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);
        var lines = 0;
        while (reader.ReadLine() is { } line)
        {
            if ((++lines & 63) == 0) ct.ThrowIfCancellationRequested();
            if (line.Length == 0 || line[0] != '{') continue;
            var probe = line.Length <= ProbeLength ? line : line[..ProbeLength];
            if (CreatedAtOf(probe) is { } at)
            {
                started ??= at;
                updated = at;
            }
            var isUser = probe.Contains("\"USER_INPUT\"", StringComparison.Ordinal);
            var isPlanner = !isUser && probe.Contains("\"PLANNER_RESPONSE\"", StringComparison.Ordinal);
            if (!isUser && !isPlanner) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var type = Str(root, "type");
                if (type == "USER_INPUT")
                {
                    if (ExtractUserRequest(Str(root, "content")) is { } text)
                    {
                        messages++;
                        firstPrompt ??= Shorten(text);
                    }
                }
                else if (type == "PLANNER_RESPONSE")
                {
                    if (Str(root, "model") is { Length: > 0 } m && !m.StartsWith("MODEL_", StringComparison.Ordinal))
                    {
                        model = m;
                    }
                    if (!string.IsNullOrWhiteSpace(Str(root, "content")))
                    {
                        messages++;
                    }
                }
            }
            catch (JsonException)
            {
            }
        }
        return new TranscriptScan(length, ticks, started, updated, messages, model, firstPrompt);
    }

    private static List<SessionTurn> ReadTurns(string path, CancellationToken ct)
    {
        var turns = new List<TurnBuilder>();
        var pendingTools = new Queue<TurnBuilder>();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);
        var lines = 0;
        while (reader.ReadLine() is { } line)
        {
            if ((++lines & 63) == 0) ct.ThrowIfCancellationRequested();
            if (line.Length == 0 || line[0] != '{') continue;
            var probe = line.Length <= ProbeLength ? line : line[..ProbeLength];
            if (!probe.Contains("\"USER_INPUT\"", StringComparison.Ordinal)
                && !probe.Contains("\"PLANNER_RESPONSE\"", StringComparison.Ordinal)
                && !probe.Contains("\"GENERIC\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var type = Str(root, "type");
                var at = ParseTime(Str(root, "created_at"));

                switch (type)
                {
                    case "USER_INPUT":
                        pendingTools.Clear();
                        if (ExtractUserRequest(Str(root, "content")) is { } userText)
                        {
                            turns.Add(new TurnBuilder(SessionTurnRole.User, at) { Text = userText });
                        }
                        break;

                    case "PLANNER_RESPONSE":
                        if (Str(root, "content") is { } reply && !string.IsNullOrWhiteSpace(reply))
                        {
                            turns.Add(new TurnBuilder(SessionTurnRole.Assistant, at) { Text = reply.Trim() });
                        }
                        if (root.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                        {
                            pendingTools.Clear();
                            foreach (var call in calls.EnumerateArray())
                            {
                                if (call.ValueKind != JsonValueKind.Object) continue;
                                var name = Str(call, "name") ?? "tool";
                                var tool = new TurnBuilder(SessionTurnRole.Tool, at) { ToolName = name };
                                if (call.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object)
                                {
                                    tool.Text = SummarizeArgs(args);
                                    tool.Files = FilesOf(name, args);
                                }
                                turns.Add(tool);
                                pendingTools.Enqueue(tool);
                            }
                        }
                        break;

                    case "GENERIC" when Str(root, "source") == "MODEL":
                        if (pendingTools.TryDequeue(out var target))
                        {
                            var body = CleanToolOutput(Str(root, "content"));
                            if (string.IsNullOrWhiteSpace(body)) body = Str(root, "error")?.Trim();
                            if (!string.IsNullOrWhiteSpace(body)) target.Output = Cut(body, MaxToolOutputLength);
                        }
                        break;
                }
            }
            catch (JsonException)
            {
            }
        }
        return turns.ConvertAll(static t => t.Build());
    }

    private sealed class TurnBuilder(SessionTurnRole role, DateTimeOffset? time)
    {
        public string Text { get; set; } = "";
        public string? ToolName { get; set; }
        public string? Output { get; set; }
        public IReadOnlyList<string>? Files { get; set; }

        public SessionTurn Build() => new(role, Text, time, ToolName, Output, Files);
    }

    internal static string? ExtractUserRequest(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var open = content.IndexOf(UserRequestOpen, StringComparison.Ordinal);
        if (open >= 0)
        {
            var start = open + UserRequestOpen.Length;
            var close = content.IndexOf(UserRequestClose, start, StringComparison.Ordinal);
            var inner = (close >= 0 ? content[start..close] : content[start..]).Trim();
            return inner.Length > 0 ? inner : null;
        }
        var stripped = content;
        foreach (var (tagOpen, tagClose) in MetadataBlocks)
        {
            while (stripped.IndexOf(tagOpen, StringComparison.Ordinal) is var idx and >= 0)
            {
                var end = stripped.IndexOf(tagClose, idx, StringComparison.Ordinal);
                stripped = end < 0 ? stripped[..idx] : stripped[..idx] + stripped[(end + tagClose.Length)..];
            }
        }
        stripped = stripped.Trim();
        return stripped.Length > 0 ? stripped : null;
    }

    private static string? CleanToolOutput(string? content)
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

    private static string SummarizeArgs(JsonElement args)
    {
        foreach (var key in (ReadOnlySpan<string>)
            ["Path", "TargetFile", "CommandLine", "file_path", "command", "query", "Query", "Url", "DirectoryPath", "SearchPath", "Pattern", "Prompt", "Description"])
        {
            if (ArgString(args, key) is { Length: > 0 } value) return OneLine(value, MaxToolInputLength);
        }
        return OneLine(args.GetRawText(), MaxToolInputLength);
    }

    private static IReadOnlyList<string>? FilesOf(string tool, JsonElement args)
    {
        if (tool is not ("edit_file" or "write_file" or "replace_file_content" or "multi_replace_file_content" or "write_to_file"))
        {
            return null;
        }
        var path = ArgString(args, "Path") ?? ArgString(args, "TargetFile");
        return string.IsNullOrWhiteSpace(path) ? null : [path];
    }

    /// <summary>
    /// <c>agy</c> serializes each tool argument value as a JSON string inside <c>args</c> (e.g. <c>"\"/a/b\""</c>);
    /// unquote one layer of JSON string encoding when present.
    /// </summary>
    private static string? ArgString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return value.GetRawText();
        var raw = value.GetString();
        if (string.IsNullOrEmpty(raw)) return raw;
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == JsonValueKind.String) return doc.RootElement.GetString();
            }
            catch (JsonException)
            {
            }
        }
        return raw;
    }

    internal static bool MatchesProject(string workspaceUrisJson, string normalizedProject)
    {
        if (string.IsNullOrWhiteSpace(workspaceUrisJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(workspaceUrisJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var raw = item.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var candidate = UriToPath(raw);
                if (candidate is not null && string.Equals(NormalizePath(candidate), normalizedProject, PathComparison))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }
        return false;
    }

    internal static string? UriToPath(string raw)
    {
        if (!raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
        var path = uri.LocalPath;
        // Percent-encoded Windows drive URIs (file:///c%3A/...) decode to "/c:/..." instead of "c:\...".
        if (path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':')
        {
            path = path[1..].Replace('/', '\\');
        }
        return path;
    }

    private static string NormalizePath(string path)
    {
        var normalized = OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;
        return normalized.Length > 1 ? normalized.TrimEnd('/', '\\') is { Length: > 0 } t ? t : normalized[..1] : normalized;
    }

    private static DateTimeOffset? CreatedAtOf(string probe)
    {
        const string key = "\"created_at\":\"";
        var at = probe.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;
        var start = at + key.Length;
        var end = probe.IndexOf('"', start);
        return end < 0 ? null : ParseTime(probe[start..end]);
    }

    private static DateTimeOffset? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.RoundtripKind, out var time))
        {
            return null;
        }
        // Go's zero time ("0001-01-01 00:00:00+00:00") is written when no user input has been recorded yet.
        return time.Year <= 1 ? null : time;
    }

    private static DateTimeOffset? Earliest(params DateTimeOffset?[] times)
    {
        DateTimeOffset? result = null;
        foreach (var t in times)
        {
            if (t is { } v && (result is null || v < result.Value)) result = v;
        }
        return result;
    }

    private static DateTimeOffset? Latest(params DateTimeOffset?[] times)
    {
        DateTimeOffset? result = null;
        foreach (var t in times)
        {
            if (t is { } v && (result is null || v > result.Value)) result = v;
        }
        return result;
    }

    private static string? NormalizeTitle(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = OneLine(text, max);
        return line.Length > 0 ? line : null;
    }

    private static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var line in text.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                var one = OneLine(line, MaxTitleLength);
                if (one.Length > 0) return one;
            }
        }
        return null;
    }

    private static string OneLine(string text, int max)
    {
        var sb = new StringBuilder();
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = sb.Length > 0;
                continue;
            }
            if (char.IsControl(ch)) continue;
            if (space) sb.Append(' ');
            space = false;
            sb.Append(ch);
            if (sb.Length > max) break;
        }
        return Cut(sb.ToString(), max);
    }

    private static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut].TrimEnd() + "…";
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record SummaryRow(string Id, string Title, string Preview, string LastModifiedTime, string LastUserInputTime);

    private sealed record TranscriptScan(
        long Length,
        long WriteTicks,
        DateTimeOffset? Started,
        DateTimeOffset? Updated,
        int MessageCount,
        string? Model,
        string? FirstPrompt);
}
