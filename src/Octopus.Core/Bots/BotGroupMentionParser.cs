using System.Text;
using System.Text.RegularExpressions;

namespace Octopus.Core.Bots;

public enum BotGroupMentionMatch
{
    Member,
    Everyone,
    Ambiguous,
    None
}

public sealed record BotGroupNamedMember(string Id, string Name);

public sealed record BotGroupMention(
    int Start,
    int End,
    bool Leading,
    BotGroupMentionMatch Match,
    IReadOnlyList<string> Ids);

public sealed record BotGroupMentionSegment(string Text, bool IsMention);

public enum BotGroupAudienceError
{
    None,
    AmbiguousMention,
    UnknownLeadingMention,
    DefaultReplierNotMember
}

public sealed record BotGroupAudienceResult(
    IReadOnlyList<string> BotIds,
    bool AddressedByMention,
    BotGroupAudienceError Error = BotGroupAudienceError.None)
{
    public bool IsValid => Error == BotGroupAudienceError.None;
}

public sealed record BotGroupReplyMention(string BotId, int Start);

/// <summary>
/// Parses <c>@Name</c> and <c>@everyone</c> mentions for Bot Group messages and bot-to-bot handoffs,
/// following Orca's <c>bot-group-mentions.ts</c> semantics: code blocks, inline code, block quotes,
/// quoted strings, and file references are blanked in place so offsets stay intact.
/// </summary>
public static partial class BotGroupMentionParser
{
    private const string EveryoneToken = "@everyone";

    [GeneratedRegex(@"(^|[^\p{L}\p{N}_.@])@", RegexOptions.Compiled)]
    private static partial Regex MentionStartRegex();

    [GeneratedRegex(@"^(?:$|[\s,:;!?)\]}>，。、：；！？）]|(?:\.(?:\s|$)))", RegexOptions.Compiled)]
    private static partial Regex MentionEndRegex();

    [GeneratedRegex(@"^(?![\p{L}\p{N}_])", RegexOptions.Compiled)]
    private static partial Regex ReplyMentionEndRegex();

    [GeneratedRegex(@"^[\s,:!?，、：]*$", RegexOptions.Compiled)]
    private static partial Regex LeadingGapRegex();

    [GeneratedRegex(@"^@[^\s@]*", RegexOptions.Compiled)]
    private static partial Regex UnknownTokenRegex();

    [GeneratedRegex(@"<quoted_room_history>[\s\S]*?(?:</quoted_room_history>|$)", RegexOptions.Compiled)]
    private static partial Regex QuotedRoomHistoryRegex();

    [GeneratedRegex(@"```[\s\S]*?(?:```|\z)|~~~[\s\S]*?(?:~~~|\z)|`[^`\n]*`|^[ \t]*>.*$|\u201c[^\u201d]*\u201d|""[^""\n]*""", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex UnaddressedProseRegex();

    [GeneratedRegex(@"@""[^""\n]+""|@(?:[\\/~]|[a-zA-Z]:[\\/])\S*", RegexOptions.Compiled)]
    private static partial Regex FileReferenceRegex();

    [GeneratedRegex(@"```[\s\S]*?(?:```|\z)|~~~[\s\S]*?(?:~~~|\z)|`[^`\n]*`|^[ \t]*>.*$", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex MarkdownCodeAndQuotesRegex();

    [GeneratedRegex(@"\u201c[^\u201d]*\u201d|""[^""\n]*""", RegexOptions.Compiled)]
    private static partial Regex QuotedStringsRegex();

    /// <summary>Replaces regex matches with equal-length spaces (preserving newlines) so character offsets never shift.</summary>
    public static string BlankMatches(string text, Regex pattern)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return pattern.Replace(text, match =>
        {
            var val = match.Value;
            var sb = new StringBuilder(val.Length);
            foreach (var ch in val)
            {
                sb.Append(ch == '\n' ? '\n' : ' ');
            }
            return sb.ToString();
        });
    }

    public static IReadOnlyList<BotGroupMention> FindMentions(
        string text,
        IReadOnlyList<BotGroupNamedMember> members,
        IReadOnlyList<string> everyoneIds,
        bool isReplyContext = false)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('@')) return [];

        var nameEnd = isReplyContext ? ReplyMentionEndRegex() : MentionEndRegex();
        var mentions = new List<BotGroupMention>();
        int? leadingEnd = 0;

        foreach (Match found in MentionStartRegex().Matches(text))
        {
            var start = found.Index + found.Groups[1].Length;
            if (mentions.Count > 0 && start < mentions[^1].End) continue;

            var rest = text[start..];
            var leading = leadingEnd is { } le && LeadingGapRegex().IsMatch(text[le..start]);

            var matchedMembers = new List<BotGroupNamedMember>();
            var longest = 0;
            foreach (var member in members)
            {
                if (string.IsNullOrWhiteSpace(member.Name)) continue;
                var nameLen = member.Name.Length;
                if (rest.Length < 1 + nameLen) continue;
                if (!rest.AsSpan(1, nameLen).Equals(member.Name.AsSpan(), StringComparison.OrdinalIgnoreCase)) continue;
                if (!nameEnd.IsMatch(rest[(1 + nameLen)..])) continue;

                if (nameLen > longest)
                {
                    longest = nameLen;
                    matchedMembers.Clear();
                    matchedMembers.Add(member);
                }
                else if (nameLen == longest)
                {
                    matchedMembers.Add(member);
                }
            }

            var isBroadcast = rest.StartsWith(EveryoneToken, StringComparison.OrdinalIgnoreCase)
                && nameEnd.IsMatch(rest[EveryoneToken.Length..]);
            var clash = isBroadcast && longest == EveryoneToken.Length - 1;

            BotGroupMention mention;
            if (isBroadcast && matchedMembers.Count == 0)
            {
                mention = new BotGroupMention(start, start + EveryoneToken.Length, leading, BotGroupMentionMatch.Everyone, everyoneIds);
            }
            else if (matchedMembers.Count > 0)
            {
                var matchKind = matchedMembers.Count > 1 || clash ? BotGroupMentionMatch.Ambiguous : BotGroupMentionMatch.Member;
                mention = new BotGroupMention(
                    start,
                    start + 1 + longest,
                    leading,
                    matchKind,
                    matchedMembers.Select(m => m.Id).ToList());
            }
            else
            {
                var unknownMatch = UnknownTokenRegex().Match(rest);
                var len = unknownMatch.Success && unknownMatch.Length > 0 ? unknownMatch.Length : 1;
                mention = new BotGroupMention(start, start + len, leading, BotGroupMentionMatch.None, []);
            }

            mentions.Add(mention);
            leadingEnd = leading ? mention.End : null;
        }

        return mentions;
    }

    /// <summary>Resolves which bots should receive an owner's sent message.</summary>
    public static BotGroupAudienceResult ResolveAudience(
        string text,
        BotGroupReplyMode replyMode,
        IReadOnlyList<BotGroupNamedMember> members)
    {
        var masked = BlankMatches(BlankMatches(text, FileReferenceRegex()), UnaddressedProseRegex());
        var everyone = members.Select(m => m.Id).ToList();
        var mentions = FindMentions(masked, members, everyone, isReplyContext: false);

        if (mentions.Any(m => m.Match == BotGroupMentionMatch.Ambiguous))
        {
            return new BotGroupAudienceResult([], true, BotGroupAudienceError.AmbiguousMention);
        }
        if (mentions.Any(m => m.Leading && m.Match == BotGroupMentionMatch.None))
        {
            return new BotGroupAudienceResult([], true, BotGroupAudienceError.UnknownLeadingMention);
        }

        var targets = mentions
            .SelectMany(m => m.Ids)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (targets.Count > 0)
        {
            return new BotGroupAudienceResult(targets, AddressedByMention: true);
        }

        return ResolveReplyModeFallback(replyMode, everyone, strictDefaultBot: true);
    }

    /// <summary>Resolves who a live composer draft will reach (ignoring an unfinished <c>@word</c> at the very end).</summary>
    public static BotGroupAudienceResult ResolveDraftAudience(
        string draft,
        BotGroupReplyMode replyMode,
        IReadOnlyList<BotGroupNamedMember> members)
    {
        var masked = BlankMatches(BlankMatches(draft ?? "", FileReferenceRegex()), UnaddressedProseRegex());
        var everyone = members.Select(m => m.Id).ToList();
        var mentions = FindMentions(masked, members, everyone, isReplyContext: false)
            .Where(m => !(m.Match == BotGroupMentionMatch.None && m.End == masked.Length))
            .ToList();

        if (mentions.Any(m => m.Match == BotGroupMentionMatch.Ambiguous))
        {
            return new BotGroupAudienceResult([], true, BotGroupAudienceError.AmbiguousMention);
        }
        if (mentions.Any(m => m.Leading && m.Match == BotGroupMentionMatch.None))
        {
            return new BotGroupAudienceResult([], true, BotGroupAudienceError.UnknownLeadingMention);
        }

        var targets = mentions
            .SelectMany(m => m.Ids)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (targets.Count > 0)
        {
            return new BotGroupAudienceResult(targets, AddressedByMention: true);
        }

        return ResolveReplyModeFallback(replyMode, everyone, strictDefaultBot: false);
    }

    /// <summary>
    /// Extracts other members addressed via <c>@Name</c> inside a bot's completed reply for automatic handoffs.
    /// Ignores code blocks, block quotes, quoted strings, quoted room history, <c>@everyone</c>, ambiguous names, and the speaker itself.
    /// </summary>
    public static IReadOnlyList<BotGroupReplyMention> ExtractReplyMentions(
        string replyMarkdown,
        IReadOnlyList<BotGroupNamedMember> members,
        string speakerBotId)
    {
        if (string.IsNullOrWhiteSpace(replyMarkdown) || !replyMarkdown.Contains('@')) return [];

        var masked = BlankMatches(replyMarkdown, QuotedRoomHistoryRegex());
        masked = BlankMatches(masked, MarkdownCodeAndQuotesRegex());
        masked = BlankMatches(masked, FileReferenceRegex());
        masked = BlankMatches(masked, QuotedStringsRegex());

        var everyone = members.Select(m => m.Id).ToList();
        var mentions = FindMentions(masked, members, everyone, isReplyContext: true);

        var result = new List<BotGroupReplyMention>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mention in mentions)
        {
            if (mention.Match != BotGroupMentionMatch.Member || mention.Ids.Count != 1) continue;
            var targetId = mention.Ids[0];
            if (string.Equals(targetId, speakerBotId, StringComparison.Ordinal)) continue;
            if (seen.Add(targetId))
            {
                result.Add(new BotGroupReplyMention(targetId, mention.Start));
            }
        }
        return result;
    }

    /// <summary>Splits message text into plain and highlighted <c>@mention</c> segments for UI rendering.</summary>
    public static IReadOnlyList<BotGroupMentionSegment> SplitDisplaySegments(
        string text,
        IReadOnlyList<string> memberNames,
        bool includeEveryone = true)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var named = memberNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => new BotGroupNamedMember(n, n))
            .ToList();
        var mentions = FindMentions(text, named, [], isReplyContext: false);
        var segments = new List<BotGroupMentionSegment>();
        var cursor = 0;
        foreach (var m in mentions)
        {
            if (m.Match == BotGroupMentionMatch.None || (m.Match == BotGroupMentionMatch.Everyone && !includeEveryone))
            {
                continue;
            }
            if (m.Start > cursor)
            {
                segments.Add(new BotGroupMentionSegment(text[cursor..m.Start], false));
            }
            segments.Add(new BotGroupMentionSegment(text[m.Start..m.End], true));
            cursor = m.End;
        }
        if (cursor < text.Length)
        {
            segments.Add(new BotGroupMentionSegment(text[cursor..], false));
        }
        return segments;
    }

    /// <summary>Prepends <c>@BotName </c> to a draft when the bot is still a current member.</summary>
    public static string PrependReplyMention(string botId, string draft, IReadOnlyList<BotGroupNamedMember> members)
    {
        var bot = members.FirstOrDefault(m => string.Equals(m.Id, botId, StringComparison.Ordinal));
        if (bot is null) return draft;
        var prefix = $"@{bot.Name} ";
        if (draft.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return draft;
        return prefix + draft;
    }

    private static BotGroupAudienceResult ResolveReplyModeFallback(
        BotGroupReplyMode replyMode,
        IReadOnlyList<string> everyone,
        bool strictDefaultBot)
    {
        return replyMode.Kind switch
        {
            BotGroupReplyModeKind.All => new BotGroupAudienceResult(everyone, AddressedByMention: false),
            BotGroupReplyModeKind.Bot => !string.IsNullOrWhiteSpace(replyMode.BotId)
                && everyone.Contains(replyMode.BotId, StringComparer.Ordinal)
                ? new BotGroupAudienceResult([replyMode.BotId!], AddressedByMention: false)
                : strictDefaultBot
                    ? new BotGroupAudienceResult([], AddressedByMention: false, BotGroupAudienceError.DefaultReplierNotMember)
                    : new BotGroupAudienceResult([], AddressedByMention: false),
            _ => new BotGroupAudienceResult([], AddressedByMention: false),
        };
    }
}
