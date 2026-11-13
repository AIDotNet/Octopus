using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Octopus.Agents.Subagents;

/// <summary>What Claude Code wrote next to a subagent's transcript: <c>agent-&lt;id&gt;.meta.json</c>.</summary>
public sealed record SubagentMeta(string? Description, string? ToolUseId, bool Background);

/// <summary>What one read of a subagent's transcript found, from <see cref="FromOffset"/> to <see cref="ToOffset"/>.</summary>
public sealed record SubagentProgress(long FromOffset, long ToOffset, int Tools, string? LastAction, SubagentMeta? Meta);

/// <summary>
/// Reads a subagent's own transcript as it grows, for the tool calls it makes, and its <c>.meta.json</c>. Claude Code
/// (2.1.323) keeps them at <c>&lt;session transcript without .jsonl&gt;/subagents/agent-&lt;id&gt;.jsonl</c> and
/// <c>….meta.json</c>. Reading the file instead of a PreToolUse hook per tool keeps a forwarder process out of every
/// tool call of every session. Files that are not there (a remote session, an older version) read as no progress.
/// </summary>
public static class SubagentTranscript
{
    /// <summary>At most this much of a transcript per read; the rest waits for the next one.</summary>
    public const int MaxReadBytes = 4 * 1024 * 1024;

    private const int MaxActionLength = 80;

    /// <summary>The subagent's transcript, derived from its session's.</summary>
    public static string? PathFor(string? sessionTranscript, string agentId)
    {
        if (string.IsNullOrEmpty(sessionTranscript) || !sessionTranscript.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var sessionDir = sessionTranscript[..^".jsonl".Length];
        return Path.Combine(sessionDir, "subagents", $"agent-{agentId}.jsonl");
    }

    /// <summary>Reads what was appended since <paramref name="offset"/>, and the meta file when <paramref name="withMeta"/>.</summary>
    public static SubagentProgress Read(string transcriptPath, long offset, bool withMeta)
    {
        var meta = withMeta ? ReadMeta(Path.ChangeExtension(transcriptPath, ".meta.json")) : null;
        var requested = offset;
        var tools = 0;
        string? last = null;
        var end = offset;
        byte[]? buffer = null;
        try
        {
            // Polled every second while the subagent runs: a stat is enough to see that nothing was added, so the
            // file is opened only when it grew (and never when it is not there, as for a remote session).
            var info = new FileInfo(transcriptPath);
            if (!info.Exists || info.Length == offset)
            {
                return new SubagentProgress(requested, end, 0, null, meta);
            }
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            if (stream.Length < offset)
            {
                // Rewritten from the start: read it again.
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
                // Only whole lines: the last one may still be being written.
                var lineStart = 0;
                while (data[lineStart..].IndexOf((byte)'\n') is var newline and >= 0)
                {
                    ScanLine(data.Slice(lineStart, newline), ref tools, ref last);
                    lineStart += newline + 1;
                }
                end = offset + lineStart;
                if (lineStart == 0 && length == MaxReadBytes)
                {
                    // One line longer than a read (a huge tool result): skip it rather than stall here.
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
        return new SubagentProgress(requested, end, tools, last, meta);
    }

    public static SubagentMeta? ReadMeta(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return new SubagentMeta(
                String(root, "description"),
                String(root, "toolUseId"),
                String(root, "requestShape")?.Contains("background", StringComparison.OrdinalIgnoreCase) == true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static readonly byte[] s_toolUseMark = Encoding.UTF8.GetBytes("\"tool_use\"");

    private static void ScanLine(ReadOnlySpan<byte> line, ref int tools, ref string? last)
    {
        // Most lines are not assistant tool calls; a byte search spares parsing them.
        if (line.IndexOf(s_toolUseMark) < 0)
        {
            return;
        }
        try
        {
            var reader = new Utf8JsonReader(line);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (String(root, "type") != "assistant"
                || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object && String(part, "type") == "tool_use" && String(part, "name") is { Length: > 0 } name)
                {
                    tools++;
                    last = Describe(name, part.TryGetProperty("input", out var input) ? input : default);
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>A tool call in a few words: <c>Read SignalHub.cs</c>, <c>Grep "TemplateEngine"</c>, <c>Bash dotnet build</c>.</summary>
    public static string Describe(string tool, JsonElement input)
    {
        string? detail = null;
        if (input.ValueKind == JsonValueKind.Object)
        {
            detail = tool switch
            {
                "Read" or "Edit" or "Write" or "MultiEdit" => FileName(String(input, "file_path")),
                "NotebookEdit" => FileName(String(input, "notebook_path")),
                "Grep" or "Glob" => String(input, "pattern") is { } pattern ? $"\"{pattern}\"" : null,
                "Bash" => FirstLine(String(input, "command")),
                "WebFetch" => Host(String(input, "url")),
                "WebSearch" => String(input, "query"),
                "Agent" or "Task" => String(input, "description"),
                _ => String(input, "description"),
            };
        }
        var text = string.IsNullOrWhiteSpace(detail) ? tool : $"{tool} {detail.Trim()}";
        return text.Length > MaxActionLength ? text[..(MaxActionLength - 1)] + "…" : text;
    }

    private static string? FileName(string? path) =>
        string.IsNullOrEmpty(path) ? null : path.TrimEnd('/', '\\').Split('/', '\\')[^1];

    private static string? FirstLine(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static string? Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
