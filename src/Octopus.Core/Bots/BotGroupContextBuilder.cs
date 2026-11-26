using System.Text;
using System.Text.Json;

namespace Octopus.Core.Bots;

/// <summary>
/// Builds incremental quoted room history for a Bot Group member turn, following Orca's <c>botGroupContext</c>
/// and <c>fitBotGroupContext</c> budget strategy (24 KB cap, oldest dropped first, mandatory tail preserved).
/// </summary>
public static class BotGroupContextBuilder
{
    public const int MaxContextChars = 24_000;
    public const int MaxSavedReplyChars = 16_000;
    public const int HandoffExcerptBefore = 1_000;
    public const int HandoffExcerptAfter = 3_000;

    public sealed record ContextEntry(string AuthorKind, string? AuthorId, string AuthorName, string Text);

    /// <summary>Clips a completed assistant reply to the maximum length persisted for room history.</summary>
    public static string? ClipReplyForHistory(string? fullReply)
    {
        if (string.IsNullOrWhiteSpace(fullReply)) return null;
        var trimmed = fullReply.Trim();
        return trimmed.Length <= MaxSavedReplyChars
            ? trimmed
            : trimmed[^MaxSavedReplyChars..];
    }

    /// <summary>
    /// Collects room posts and other members' completed replies since <paramref name="member"/> last ran
    /// a non-error turn in this room (or all recent history if the member has not spoken yet).
    /// </summary>
    public static string BuildIncrementalContextJson(
        IReadOnlyList<BotGroupMessage> roomMessages,
        BotGroupMember member)
    {
        var seenIndex = -1;
        for (var i = roomMessages.Count - 1; i >= 0; i--)
        {
            if (roomMessages[i].Turns.Any(t =>
                string.Equals(t.ChatId, member.ChatId, StringComparison.Ordinal)
                && string.IsNullOrEmpty(t.Error)
                && t.Status != BotGroupTurnStatus.Failed))
            {
                seenIndex = i;
                break;
            }
        }

        var entries = new List<ContextEntry>();
        var startIndex = Math.Max(seenIndex, 0);
        for (var i = startIndex; i < roomMessages.Count; i++)
        {
            var message = roomMessages[i];
            if (!(seenIndex != -1 && i == startIndex))
            {
                if (!string.IsNullOrWhiteSpace(message.Text))
                {
                    entries.Add(new ContextEntry(
                        message.IsOwner ? "owner" : "bot",
                        message.AuthorBotId,
                        message.IsOwner ? "Owner" : (message.AuthorBotName ?? "Bot"),
                        message.Text));
                }
            }

            foreach (var turn in message.Turns)
            {
                if (string.Equals(turn.BotId, member.BotId, StringComparison.Ordinal)) continue;
                if (string.IsNullOrWhiteSpace(turn.Reply)) continue;
                entries.Add(new ContextEntry("bot", turn.BotId, turn.BotName, turn.Reply!));
            }
        }

        return FitEntriesJson(entries, keptTailCount: 0);
    }

    /// <summary>
    /// Ensures the handoff speaker's reply excerpt around <paramref name="mentionStartIndex"/> is included
    /// and never dropped by the 24 KB context budget.
    /// </summary>
    public static string WithNamingReply(
        string baseContextJson,
        BotGroupTurn speaker,
        string fullReply,
        int mentionStartIndex)
    {
        var entries = ParseEntriesJson(baseContextJson);
        var before = HandoffExcerptBefore;
        var after = HandoffExcerptAfter;
        var text = Excerpt(fullReply, mentionStartIndex, before, after);
        while (SerializeEntries([new ContextEntry("bot", speaker.BotId, speaker.BotName, text)]).Length > MaxContextChars
            && (before > 64 || after > 64))
        {
            before /= 2;
            after /= 2;
            text = Excerpt(fullReply, mentionStartIndex, before, after);
        }

        var existingIdx = entries.FindIndex(e =>
            e.AuthorKind == "bot"
            && string.Equals(e.AuthorId, speaker.BotId, StringComparison.Ordinal)
            && string.Equals(e.Text, text, StringComparison.Ordinal));
        if (existingIdx >= 0)
        {
            return FitEntriesJson(entries, keptTailCount: entries.Count - existingIdx);
        }

        entries.Add(new ContextEntry("bot", speaker.BotId, speaker.BotName, text));
        return FitEntriesJson(entries, keptTailCount: 1);
    }

    /// <summary>Builds the full prompt envelope delivered into a member bot's ACP session.</summary>
    public static string BuildTurnPromptEnvelope(
        string groupName,
        string selfBotName,
        IReadOnlyList<string> memberNames,
        string incrementalContextJson,
        BotGroupMessage message,
        BotGroupMessage? originalOwnerMessage = null)
    {
        var sb = new StringBuilder();
        sb.Append("[Octopus Bot Group: ").Append(groupName)
            .Append(" | You are @").Append(selfBotName)
            .Append(" | Members: ").Append(string.Join(", ", memberNames.Select(n => "@" + n)))
            .AppendLine("]");
        sb.AppendLine("Rules: Answer concisely as your role in this group. To hand off a follow-up to another member, address them with @MemberName outside code blocks. Do not address yourself or @everyone.");

        if (!string.IsNullOrWhiteSpace(incrementalContextJson) && incrementalContextJson != "[]")
        {
            sb.AppendLine();
            sb.AppendLine("<quoted_room_history>");
            sb.AppendLine(incrementalContextJson);
            sb.AppendLine("</quoted_room_history>");
        }

        sb.AppendLine();
        if (message.IsMentionHandoff && !string.IsNullOrWhiteSpace(message.RequesterBotName))
        {
            sb.Append("[Handoff from @").Append(message.RequesterBotName).AppendLine("]");
            if (originalOwnerMessage is not null && !string.IsNullOrWhiteSpace(originalOwnerMessage.Text))
            {
                sb.Append("Original owner request: ").AppendLine(originalOwnerMessage.Text);
            }
            sb.AppendLine("Read the quoted room history above (especially the message mentioning you) and respond directly.");
        }
        else
        {
            sb.AppendLine("[Message from Owner]");
            sb.AppendLine(message.Text);
        }

        return sb.ToString().TrimEnd();
    }

    public static string FitEntriesJson(IReadOnlyList<ContextEntry> entries, int keptTailCount = 0)
    {
        if (entries.Count == 0) return "[]";
        var kept = Math.Clamp(keptTailCount, 0, entries.Count);
        var start = 0;
        var json = SerializeEntries(entries);
        while (start < entries.Count - kept && json.Length > MaxContextChars)
        {
            start++;
            json = SerializeEntries(entries.Skip(start).ToList());
        }
        return json;
    }

    private static string Excerpt(string source, int at, int before, int after)
    {
        if (string.IsNullOrEmpty(source)) return "";
        var safeAt = Math.Clamp(at, 0, source.Length);
        var from = Math.Max(0, safeAt - before);
        var to = Math.Min(source.Length, safeAt + after);
        var prefix = from > 0 ? "…" : "";
        var suffix = to < source.Length ? "…" : "";
        return $"{prefix}{source[from..to]}{suffix}";
    }

    private static string SerializeEntries(IReadOnlyList<ContextEntry> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var e in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("authorKind", e.AuthorKind);
                if (!string.IsNullOrWhiteSpace(e.AuthorId)) writer.WriteString("authorId", e.AuthorId);
                writer.WriteString("authorName", e.AuthorName);
                writer.WriteString("text", e.Text);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static List<ContextEntry> ParseEntriesJson(string json)
    {
        var result = new List<ContextEntry>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var kind = el.TryGetProperty("authorKind", out var k) ? k.GetString() ?? "bot" : "bot";
                var id = el.TryGetProperty("authorId", out var i) ? i.GetString() : null;
                var name = el.TryGetProperty("authorName", out var n) ? n.GetString() ?? "Bot" : "Bot";
                var text = el.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                result.Add(new ContextEntry(kind, id, name, text));
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }
}
