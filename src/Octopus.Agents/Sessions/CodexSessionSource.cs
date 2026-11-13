using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Octopus.Agents.Sessions;

/// <summary>
/// Reads OpenAI Codex's saved sessions: <c>&lt;CODEX_HOME&gt;/sessions/YYYY/MM/DD/rollout-&lt;time&gt;-&lt;uuid&gt;.jsonl</c>, one
/// <c>{timestamp, type, payload}</c> record per line (checked against 0.167; older versions put the same payloads in
/// other record types, so unknown shapes are skipped, never fatal). Thread names live in <c>session_index.jsonl</c>;
/// <c>archived_sessions</c> is not read, so archived sessions are not listed.
/// </summary>
public sealed class CodexSessionSource : ISessionSource
{
    public const string Id = "codex";

    private const int MaxTitleLength = 80;
    private const int MaxToolInputLength = 200;
    private const int MaxToolOutputLength = 4 * 1024;

    /// <summary>How far into a record's text the kind of record is looked for, so huge lines (images) are not parsed to be dropped.</summary>
    private const int KindProbeLength = 400;

    /// <summary>Rollouts without <c>cwd</c> in their first record (older versions) get this many more lines to show a turn context.</summary>
    private const int CwdProbeLines = 200;

    private static readonly string[] InjectedPrefixes =
    [
        "<environment_context>", "<user_instructions>", "<permissions instructions>", "<permissions_instructions>",
        "# AGENTS.md instructions", "<turn_aborted>", "<user_action>",
    ];

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Files only grow while a session runs, so (length, write time) tells whether an entry is still good.
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    /// <summary>How many files were read past their first line (for tests: a cache hit does not add one).</summary>
    internal int FullScans => Volatile.Read(ref _fullScans);
    private int _fullScans;

    public string AgentId => Id;

    public Task<IReadOnlyList<AgentSessionInfo>> ListAsync(string accountDirectory, string projectPath, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<AgentSessionInfo>>(() => List(accountDirectory, projectPath, cancellationToken), cancellationToken);

    public Task<SessionTranscript> ReadAsync(AgentSessionInfo session, CancellationToken cancellationToken = default) =>
        Task.Run(() => new SessionTranscript(session, ReadTurns(session.FilePath, cancellationToken)), cancellationToken);

    private IReadOnlyList<AgentSessionInfo> List(string accountDirectory, string projectPath, CancellationToken ct)
    {
        var root = Path.Combine(accountDirectory, "sessions");
        if (!Directory.Exists(root)) return [];
        var project = TrimSeparators(projectPath);
        var names = ReadThreadNames(Path.Combine(accountDirectory, "session_index.jsonl"));
        var result = new List<AgentSessionInfo>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var summary = GetSummary(path, project);
                if (summary is null) continue;
                var title = names.TryGetValue(summary.SessionId, out var named) && named.Length > 0 ? named : summary.Title;
                result.Add(new AgentSessionInfo(Id, accountDirectory, summary.SessionId, title, summary.StartedAt, summary.UpdatedAt,
                    summary.MessageCount, summary.Model, summary.GitBranch, path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // Unreadable or being replaced: the next listing sees it.
            }
        }
        result.Sort(static (a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return result;
    }

    private Summary? GetSummary(string path, string project)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        var length = info.Length;
        var ticks = info.LastWriteTimeUtc.Ticks;
        if (_cache.TryGetValue(path, out var cached) && cached.Length == length && cached.Ticks == ticks)
        {
            if (cached.Summary is not null) return cached.Summary.Cwd is { } c && SamePath(c, project) ? cached.Summary : null;
            if (cached.Cwd is null || !SamePath(cached.Cwd, project)) return null;
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);
        var meta = ReadHeader(reader, out var pending);
        if (meta.Cwd is null || !SamePath(meta.Cwd, project))
        {
            _cache[path] = new Entry(length, ticks, meta.Cwd, null);
            return null;
        }
        Interlocked.Increment(ref _fullScans);
        var summary = Scan(reader, pending, meta, path, info);
        _cache[path] = new Entry(length, ticks, meta.Cwd, summary);
        return summary;
    }

    /// <summary>The session_meta record, and (for rollouts that did not put <c>cwd</c> in it) the first turn context's folder.</summary>
    private static Meta ReadHeader(StreamReader reader, out List<string> consumed)
    {
        consumed = [];
        var meta = new Meta();
        var first = reader.ReadLine();
        if (first is null) return meta;
        consumed.Add(first);
        try
        {
            using var doc = JsonDocument.Parse(first);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return meta;
            var payload = root;
            if (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "session_meta"
                && root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                payload = p;
            }
            meta.Id = Str(payload, "id");
            meta.Cwd = Str(payload, "cwd");
            meta.Started = ParseTime(Str(payload, "timestamp")) ?? ParseTime(Str(root, "timestamp"));
            if (payload.TryGetProperty("git", out var git) && git.ValueKind == JsonValueKind.Object) meta.Branch = Str(git, "branch");
        }
        catch (JsonException)
        {
            return meta;
        }
        for (var i = 0; meta.Cwd is null && i < CwdProbeLines; i++)
        {
            var line = reader.ReadLine();
            if (line is null) break;
            consumed.Add(line);
            if (!Probe(line).Contains("\"turn_context\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object) meta.Cwd = Str(p, "cwd");
            }
            catch (JsonException)
            {
            }
        }
        return meta;
    }

    private static Summary? Scan(StreamReader reader, List<string> pending, Meta meta, string path, FileInfo info)
    {
        var response = new Counts();
        var events = new Counts();
        string? model = null;
        string? firstAssistant = null;
        DateTimeOffset? first = null, last = null;

        void Handle(string line)
        {
            if (line.Length == 0) return;
            var probe = Probe(line);
            if (TopLevelTimestamp(probe) is { } at)
            {
                first ??= at;
                last = at;
            }
            var isResponse = probe.Contains("\"response_item\"", StringComparison.Ordinal);
            var isEvent = !isResponse && probe.Contains("\"event_msg\"", StringComparison.Ordinal);
            var isContext = !isResponse && !isEvent && probe.Contains("\"turn_context\"", StringComparison.Ordinal);
            if (!(isContext
                  || (isResponse && probe.Contains("\"message\"", StringComparison.Ordinal))
                  || (isEvent && (probe.Contains("\"user_message\"", StringComparison.Ordinal)
                                  || probe.Contains("\"agent_message\"", StringComparison.Ordinal)
                                  || probe.Contains("\"thread_settings_applied\"", StringComparison.Ordinal)))))
            {
                return;
            }
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return;
                if (isContext)
                {
                    model = Str(payload, "model") ?? model;
                }
                else if (isEvent)
                {
                    switch (Str(payload, "type"))
                    {
                        case "thread_settings_applied":
                            if (payload.TryGetProperty("thread_settings", out var s) && s.ValueKind == JsonValueKind.Object) model = Str(s, "model") ?? model;
                            break;
                        case "user_message":
                            events.AddUser(RealUserText(Str(payload, "message")));
                            break;
                        case "agent_message":
                            events.AddAssistant(Str(payload, "message"));
                            break;
                    }
                }
                else if (Str(payload, "type") == "message")
                {
                    var role = Str(payload, "role");
                    var text = MessageText(payload);
                    if (role == "user") response.AddUser(RealUserText(text));
                    else if (role == "assistant") response.AddAssistant(text);
                }
            }
            catch (JsonException)
            {
            }
        }

        foreach (var line in pending) Handle(line);
        while (reader.ReadLine() is { } line) Handle(line);

        var counts = response.Total > 0 ? response : events;
        if (counts.Total == 0) return null;
        firstAssistant = counts.FirstAssistant;
        var id = meta.Id;
        if (string.IsNullOrEmpty(id)) id = IdFromFileName(path);
        var title = counts.FirstPrompt is { } prompt ? Shorten(prompt) : firstAssistant is { } a ? Shorten(a) : id;
        var started = meta.Started ?? first ?? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var updated = last ?? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        return new Summary(meta.Cwd!, id, title, started, updated, counts.Total, model, meta.Branch);
    }

    private static List<SessionTurn> ReadTurns(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);
        // Newer rollouts carry every message twice (response_item and event_msg): the response items win.
        var turns = new List<(SessionTurn Turn, bool FromEvent)>();
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;
            var probe = Probe(line);
            var isResponse = probe.Contains("\"response_item\"", StringComparison.Ordinal);
            var isEvent = !isResponse && probe.Contains("\"event_msg\"", StringComparison.Ordinal);
            if (!isResponse && !isEvent) continue;
            if (isResponse && (probe.Contains("\"reasoning\"", StringComparison.Ordinal) || probe.Contains("\"web_search_call\"", StringComparison.Ordinal))) continue;
            if (isEvent && !probe.Contains("\"user_message\"", StringComparison.Ordinal) && !probe.Contains("\"agent_message\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                var at = ParseTime(Str(doc.RootElement, "timestamp"));
                var type = Str(payload, "type");
                if (isEvent)
                {
                    if (type == "user_message" && RealUserText(Str(payload, "message")) is { } u)
                        turns.Add((new SessionTurn(SessionTurnRole.User, u, at), true));
                    else if (type == "agent_message" && Str(payload, "message") is { Length: > 0 } a)
                        turns.Add((new SessionTurn(SessionTurnRole.Assistant, a.Trim(), at), true));
                    continue;
                }
                switch (type)
                {
                    case "message":
                        var role = Str(payload, "role");
                        var text = MessageText(payload);
                        if (role == "user" && RealUserText(text) is { } user)
                            turns.Add((new SessionTurn(SessionTurnRole.User, user, at), false));
                        else if (role == "assistant" && !string.IsNullOrWhiteSpace(text))
                            turns.Add((new SessionTurn(SessionTurnRole.Assistant, text.Trim(), at), false));
                        break;
                    case "function_call" or "custom_tool_call" or "local_shell_call":
                        var call = ToolCall(payload, type, at);
                        if (Str(payload, "call_id") is { } callId) calls[callId] = turns.Count;
                        turns.Add((call, false));
                        break;
                    case "function_call_output" or "custom_tool_call_output" or "local_shell_call_output":
                        if (Str(payload, "call_id") is { } id && calls.TryGetValue(id, out var index)
                            && payload.TryGetProperty("output", out var output))
                        {
                            turns[index] = (turns[index].Turn with { Output = Cut(OutputText(output), MaxToolOutputLength) }, false);
                        }
                        break;
                }
            }
            catch (JsonException)
            {
            }
        }
        var hasResponseMessages = turns.Any(static t => !t.FromEvent);
        return [.. turns.Where(t => !(hasResponseMessages && t.FromEvent)).Select(static t => t.Turn)];
    }

    private static SessionTurn ToolCall(JsonElement payload, string type, DateTimeOffset? at)
    {
        var name = Str(payload, "name") ?? (type == "local_shell_call" ? "local_shell" : "tool");
        // function_call: arguments is a JSON string. custom_tool_call: input is raw text (a patch). local_shell_call: action.command.
        JsonElement args = default;
        string? raw = Str(payload, "arguments") ?? Str(payload, "input");
        var parsed = false;
        JsonDocument? argsDoc = null;
        try
        {
            if (type == "local_shell_call" && payload.TryGetProperty("action", out var action))
            {
                args = action;
                parsed = true;
            }
            else if (raw is not null && raw.TrimStart().StartsWith('{'))
            {
                argsDoc = JsonDocument.Parse(raw);
                args = argsDoc.RootElement;
                parsed = true;
            }
            var files = new List<string>();
            if (raw is not null) CollectPatchFiles(raw, files);
            if (parsed) CollectPatchFilesIn(args, files);
            var distinct = files.Distinct(StringComparer.Ordinal).ToList();
            string summary;
            if (distinct.Count > 0 && (name == "apply_patch" || !parsed)) summary = Cut(string.Join(", ", distinct), MaxToolInputLength);
            else if (parsed) summary = Summarize(args);
            else summary = OneLine(raw ?? "", MaxToolInputLength);
            return new SessionTurn(SessionTurnRole.Tool, summary, at, name, null, distinct.Count > 0 ? distinct : null);
        }
        catch (JsonException)
        {
            return new SessionTurn(SessionTurnRole.Tool, OneLine(raw ?? "", MaxToolInputLength), at, name);
        }
        finally
        {
            argsDoc?.Dispose();
        }
    }

    private static string Summarize(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "command", "cmd", "path", "file_path" })
            {
                if (!args.TryGetProperty(key, out var v)) continue;
                var text = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Array => string.Join(' ', v.EnumerateArray().Select(static e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(text)) return OneLine(text, MaxToolInputLength);
            }
        }
        return OneLine(args.GetRawText(), MaxToolInputLength);
    }

    private static void CollectPatchFilesIn(JsonElement element, List<string> files)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                if (element.GetString() is { } s) CollectPatchFiles(s, files);
                break;
            case JsonValueKind.Array:
                foreach (var e in element.EnumerateArray()) CollectPatchFilesIn(e, files);
                break;
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject()) CollectPatchFilesIn(p.Value, files);
                break;
        }
    }

    /// <summary>The files an <c>apply_patch</c> envelope names, from its <c>*** Update File:</c>, <c>Add File</c>, <c>Delete File</c> and <c>Move to</c> lines.</summary>
    private static void CollectPatchFiles(string text, List<string> files)
    {
        if (!text.Contains("*** ", StringComparison.Ordinal)) return;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            foreach (var marker in new[] { "*** Update File: ", "*** Add File: ", "*** Delete File: ", "*** Move to: " })
            {
                if (!line.StartsWith(marker, StringComparison.Ordinal)) continue;
                var file = line[marker.Length..].Trim();
                if (file.Length > 0) files.Add(file);
                break;
            }
        }
    }

    private static string OutputText(JsonElement output)
    {
        switch (output.ValueKind)
        {
            case JsonValueKind.String:
                return output.GetString() ?? "";
            case JsonValueKind.Object:
                if (output.TryGetProperty("output", out var inner) && inner.ValueKind == JsonValueKind.String) return inner.GetString() ?? "";
                if (output.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
                break;
            case JsonValueKind.Array:
                var parts = output.EnumerateArray()
                    .Select(static e => e.ValueKind == JsonValueKind.Object ? Str(e, "text") : null)
                    .Where(static t => t is not null);
                var joined = string.Join("\n", parts);
                if (joined.Length > 0) return joined;
                break;
        }
        return output.GetRawText();
    }

    private static string MessageText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            if (Str(part, "type") is "input_text" or "output_text" or "text" && Str(part, "text") is { Length: > 0 } text) parts.Add(text);
        }
        return string.Join("\n", parts);
    }

    /// <summary>The text of a user message a person typed; null for context Codex injected (as a user message) and for empty ones.</summary>
    private static string? RealUserText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        foreach (var prefix in InjectedPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal)) return null;
        }
        return trimmed;
    }

    private static Dictionary<string, string> ReadThreadNames(string indexPath)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (Str(doc.RootElement, "id") is { } id && Str(doc.RootElement, "thread_name") is { } name && !string.IsNullOrWhiteSpace(name))
                    {
                        // Renames append: the last line of an id wins.
                        names[id] = OneLine(name, MaxTitleLength * 2);
                    }
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return names;
    }

    private static string Probe(string line) => line.Length <= KindProbeLength ? line : line[..KindProbeLength];

    /// <summary>The record's own time, found in its first characters so that a huge line need not be parsed for it.</summary>
    private static DateTimeOffset? TopLevelTimestamp(string probe)
    {
        const string key = "\"timestamp\":\"";
        var at = probe.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;
        var start = at + key.Length;
        var end = probe.IndexOf('"', start);
        return end < 0 ? null : ParseTime(probe[start..end]);
    }

    private static DateTimeOffset? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string IdFromFileName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem.Length > 36 ? stem[^36..] : stem;
    }

    private static string TrimSeparators(string path) =>
        path.Length > 1 ? path.TrimEnd('/', '\\') is { Length: > 0 } t ? t : path[..1] : path;

    private static bool SamePath(string a, string b) => string.Equals(TrimSeparators(a), b, PathComparison);

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
            if (space) sb.Append(' ');
            space = false;
            sb.Append(ch);
            if (sb.Length > max) break;
        }
        return Cut(sb.ToString(), max);
    }

    /// <summary>The first non-empty line, whitespace collapsed.</summary>
    private static string Shorten(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line)) return OneLine(line, MaxTitleLength);
        }
        return "";
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private sealed class Meta
    {
        public string? Id, Cwd, Branch;
        public DateTimeOffset? Started;
    }

    private sealed class Counts
    {
        public int Users, Assistants;
        public string? FirstPrompt, FirstAssistant;
        public int Total => Users + Assistants;

        public void AddUser(string? text)
        {
            if (text is null) return;
            Users++;
            FirstPrompt ??= text;
        }

        public void AddAssistant(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Assistants++;
            FirstAssistant ??= text.Trim();
        }
    }

    private sealed record Summary(string Cwd, string SessionId, string Title, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, int MessageCount, string? Model, string? GitBranch);

    private sealed record Entry(long Length, long Ticks, string? Cwd, Summary? Summary);
}
