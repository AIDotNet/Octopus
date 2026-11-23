using System.Buffers;
using System.Text;
using System.Text.Json;
using Octopus.Agents.Sessions;

namespace Octopus.App.Activity;

/// <summary>The naming clues extracted from an agent hook payload.</summary>
public readonly record struct SessionPayload(
    string? Prompt,
    string? TranscriptPath,
    string? SessionId = null,
    string? ArtifactDirectoryPath = null);

/// <summary>
/// A session title read from an agent's own storage: <see cref="Title"/> is an explicit or AI-generated title (takes
/// precedence over a prompt fallback); <see cref="FallbackPrompt"/> is a prompt/preview found when no title exists yet.
/// </summary>
public readonly record struct StoredSessionTitle(string? Title, string? FallbackPrompt = null);

/// <summary>
/// The session name an agent's hook payload leads to. Claude Code names a session only once a turn is about
/// something (an <c>ai-title</c> record in its transcript, mirrored in the terminal title); Antigravity CLI writes its
/// generated title to <c>conversation_summaries.db</c> without setting a terminal title. Until a name exists, the first
/// prompt stands in.
/// </summary>
public static class SessionName
{
    /// <summary>Longest prompt text shown as a title, in characters.</summary>
    public const int MaxPromptLength = 40;

    /// <summary>How much of the transcript's end is searched for the latest title.</summary>
    private const int TailBytes = 256 * 1024;

    private static readonly byte[] AiTitleMarker = "\"ai-title\""u8.ToArray();

    /// <summary>
    /// The prompt, transcript path, session/conversation id and artifact directory a hook payload carries; empty
    /// record when it is not JSON or has none. Understands both snake_case (Claude Code) and camelCase (Antigravity CLI).
    /// </summary>
    public static SessionPayload ReadPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.TrimStart()[0] != '{')
        {
            return default;
        }
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            return new SessionPayload(
                NonEmptyStringOf(root, "prompt") ?? NonEmptyStringOf(root, "lastUserInput") ?? NonEmptyStringOf(root, "last_user_input"),
                NonEmptyStringOf(root, "transcript_path") ?? NonEmptyStringOf(root, "transcriptPath"),
                NonEmptyStringOf(root, "session_id")
                    ?? NonEmptyStringOf(root, "sessionId")
                    ?? NonEmptyStringOf(root, "conversationId")
                    ?? NonEmptyStringOf(root, "conversation_id"),
                NonEmptyStringOf(root, "artifactDirectoryPath") ?? NonEmptyStringOf(root, "artifact_directory_path"));
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// Reads the stored session title for an agent dialect: Antigravity CLI keeps its generated title in
    /// <c>conversation_summaries.db</c>; Claude-compatible agents write <c>ai-title</c> records to their transcript.
    /// </summary>
    public static StoredSessionTitle ReadStoredTitle(string dialect, SessionPayload payload, string? agyDefaultDirectory = null)
    {
        if (string.Equals(dialect, AgySessionSource.Id, StringComparison.OrdinalIgnoreCase))
        {
            var (title, preview) = AgySessionSource.ReadConversationTitle(
                payload.SessionId,
                payload.TranscriptPath,
                payload.ArtifactDirectoryPath,
                agyDefaultDirectory);
            return new StoredSessionTitle(title, preview);
        }
        return payload.TranscriptPath is { Length: > 0 } path
            ? new StoredSessionTitle(ReadTranscriptTitle(path))
            : default;
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
            var length = (int)(stream.Length - start);
            if (length <= 0) return null;
            stream.Seek(start, SeekOrigin.Begin);
            var rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                stream.ReadExactly(rented.AsSpan(0, length));
                var span = rented.AsSpan(0, length);
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
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? NonEmptyStringOf(JsonElement element, string name) =>
        StringOf(element, name) is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string? StringOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
