using System.Text;
using System.Text.Json;

namespace Octopus.App.Activity;

/// <summary>
/// The session name an agent's hook payload leads to. Claude Code names a session only once a turn is about
/// something (an <c>ai-title</c> record in its transcript, mirrored in the terminal title); a greeting such as
/// "你好" never gets one, and the title stays "Claude Code". Until a name exists, the first prompt stands in.
/// </summary>
public static class SessionName
{
    /// <summary>Longest prompt text shown as a title, in characters.</summary>
    public const int MaxPromptLength = 40;

    /// <summary>How much of the transcript's end is searched for the latest title.</summary>
    private const int TailBytes = 256 * 1024;

    private static readonly byte[] AiTitleMarker = "\"ai-title\""u8.ToArray();

    /// <summary>The prompt and transcript path a hook payload carries; nulls when it is not JSON or has none.</summary>
    public static (string? Prompt, string? TranscriptPath) ReadPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.TrimStart()[0] != '{')
        {
            return (null, null);
        }
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            return (StringOf(root, "prompt"), StringOf(root, "transcript_path"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// A title from a prompt: its first non-empty line, whitespace collapsed, cut at <see cref="MaxPromptLength"/>.
    /// Empty for slash commands and for prompts with no text.
    /// </summary>
    public static string FromPrompt(string? prompt)
    {
        var line = (prompt ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        if (line.Length == 0 || line[0] == '/')
        {
            return "";
        }
        var text = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (char.IsWhiteSpace(c))
            {
                if (text.Length > 0 && text[^1] != ' ') text.Append(' ');
            }
            else if (!char.IsControl(c))
            {
                text.Append(c);
            }
        }
        var result = text.ToString().Trim();
        if (result.Length <= MaxPromptLength)
        {
            return result;
        }
        var cut = MaxPromptLength;
        if (char.IsHighSurrogate(result[cut - 1])) cut--;
        return result[..cut].TrimEnd() + "…";
    }

    /// <summary>The latest <c>ai-title</c> in the end of a Claude Code transcript; null when there is none or it cannot be read.</summary>
    public static string? ReadTranscriptTitle(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            stream.ReadExactly(buffer);
            var span = buffer.AsSpan();
            // The first line may be cut in half when the tail starts mid-file.
            if (start > 0 && span.IndexOf((byte)'\n') is var first and >= 0) span = span[(first + 1)..];
            string? title = null;
            while (span.Length > 0)
            {
                var end = span.IndexOf((byte)'\n');
                var line = end < 0 ? span : span[..end];
                span = end < 0 ? default : span[(end + 1)..];
                if (line.IndexOf(AiTitleMarker) < 0) continue;
                try
                {
                    var reader = new Utf8JsonReader(line);
                    using var document = JsonDocument.ParseValue(ref reader);
                    var root = document.RootElement;
                    if (StringOf(root, "type") == "ai-title" && StringOf(root, "aiTitle") is { Length: > 0 } found)
                    {
                        title = found.Trim();
                    }
                }
                catch (JsonException)
                {
                }
            }
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
