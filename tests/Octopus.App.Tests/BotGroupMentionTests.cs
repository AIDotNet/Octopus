using Octopus.Core.Bots;
using Xunit;

namespace Octopus.App.Tests;

public sealed class BotGroupMentionTests
{
    private static readonly BotGroupReplyMode MentionsMode = new() { Kind = BotGroupReplyModeKind.Mentions };
    private static readonly BotGroupReplyMode AllMode = new() { Kind = BotGroupReplyModeKind.All };

    [Fact]
    public void LongestMemberNameWins_OverShorterPrefix()
    {
        var members = new BotGroupNamedMember[]
        {
            new("b-short", "Sage"),
            new("b-long", "Sage Bot"),
        };

        var result = BotGroupMentionParser.ResolveAudience("@Sage Bot please review this", MentionsMode, members);

        Assert.True(result.IsValid);
        Assert.True(result.AddressedByMention);
        Assert.Equal(["b-long"], result.BotIds);

        var shortResult = BotGroupMentionParser.ResolveAudience("@sage check the logs", MentionsMode, members);
        Assert.True(shortResult.IsValid);
        Assert.Equal(["b-short"], shortResult.BotIds);
    }

    [Fact]
    public void DuplicateMemberNames_ReturnAmbiguousMention()
    {
        var members = new BotGroupNamedMember[]
        {
            new("b1", "Reviewer"),
            new("b2", "reviewer"),
            new("b3", "Tester"),
        };

        var result = BotGroupMentionParser.ResolveAudience("@Reviewer check PR", MentionsMode, members);

        Assert.False(result.IsValid);
        Assert.Equal(BotGroupAudienceError.AmbiguousMention, result.Error);
        Assert.Empty(result.BotIds);
    }

    [Fact]
    public void UnknownLeadingMention_ReturnsError_WhereasUnknownMidProseStaysPlainText()
    {
        var members = new BotGroupNamedMember[]
        {
            new("b1", "Sage"),
            new("b2", "Atlas"),
        };

        var leadingUnknown = BotGroupMentionParser.ResolveAudience("@nobody hi", MentionsMode, members);
        Assert.False(leadingUnknown.IsValid);
        Assert.Equal(BotGroupAudienceError.UnknownLeadingMention, leadingUnknown.Error);

        var secondLeadingUnknown = BotGroupMentionParser.ResolveAudience("@Sage, @nobody hi", MentionsMode, members);
        Assert.False(secondLeadingUnknown.IsValid);
        Assert.Equal(BotGroupAudienceError.UnknownLeadingMention, secondLeadingUnknown.Error);

        var midProseUnknown = BotGroupMentionParser.ResolveAudience("Please notify @nobody about the outage", AllMode, members);
        Assert.True(midProseUnknown.IsValid);
        Assert.False(midProseUnknown.AddressedByMention);
        Assert.Equal(["b1", "b2"], midProseUnknown.BotIds);

        var validPlusMidUnknown = BotGroupMentionParser.ResolveAudience("@Sage please tell @nobody later", MentionsMode, members);
        Assert.True(validPlusMidUnknown.IsValid);
        Assert.True(validPlusMidUnknown.AddressedByMention);
        Assert.Equal(["b1"], validPlusMidUnknown.BotIds);
    }

    [Fact]
    public void EveryoneTargetsAllMembers_AndMemberNamedEveryoneCausesAmbiguousMention()
    {
        var normalMembers = new BotGroupNamedMember[]
        {
            new("b1", "Sage"),
            new("b2", "Atlas"),
            new("b3", "Nova"),
        };

        var broadcast = BotGroupMentionParser.ResolveAudience("@everyone standup time!", MentionsMode, normalMembers);
        Assert.True(broadcast.IsValid);
        Assert.True(broadcast.AddressedByMention);
        Assert.Equal(["b1", "b2", "b3"], broadcast.BotIds);

        var clashingMembers = new BotGroupNamedMember[]
        {
            new("b1", "Everyone"),
            new("b2", "Atlas"),
        };

        var clash = BotGroupMentionParser.ResolveAudience("@everyone check this", MentionsMode, clashingMembers);
        Assert.False(clash.IsValid);
        Assert.Equal(BotGroupAudienceError.AmbiguousMention, clash.Error);
    }

    [Theory]
    [InlineData("```csharp\n// @Sage inside backtick fence\n```")]
    [InlineData("~~~bash\necho @Sage inside tilde fence\n~~~")]
    [InlineData("Use `@Sage` inline code.")]
    [InlineData("> @Sage inside a block quote")]
    [InlineData("She said \"@Sage is away\"")]
    [InlineData("She said \u201c@Sage in curly quotes\u201d.")]
    [InlineData("Check files @/src/foo, @~/bar, and @\"my file\".")]
    public void IgnoresMentionsInsideCodeBlocksInlineCodeBlockQuotesQuotedStringsAndFileRefs(string text)
    {
        var members = new BotGroupNamedMember[]
        {
            new("b1", "Sage"),
            new("b2", "Atlas"),
        };

        var ignored = BotGroupMentionParser.ResolveAudience(text, MentionsMode, members);
        Assert.True(ignored.IsValid);
        Assert.False(ignored.AddressedByMention);
        Assert.Empty(ignored.BotIds);

        var withRealMention = text + "\n@Atlas please verify.";
        var matched = BotGroupMentionParser.ResolveAudience(withRealMention, MentionsMode, members);
        Assert.True(matched.IsValid);
        Assert.True(matched.AddressedByMention);
        Assert.Equal(["b2"], matched.BotIds);
    }

    [Fact]
    public void ReplyModeFallback_HandlesAllBotValidBotMissingAndMentions()
    {
        var members = new BotGroupNamedMember[]
        {
            new("b1", "Sage"),
            new("b2", "Atlas"),
        };

        var allResult = BotGroupMentionParser.ResolveAudience("General status update", AllMode, members);
        Assert.True(allResult.IsValid);
        Assert.False(allResult.AddressedByMention);
        Assert.Equal(["b1", "b2"], allResult.BotIds);

        var validBotMode = new BotGroupReplyMode { Kind = BotGroupReplyModeKind.Bot, BotId = "b2" };
        var botResult = BotGroupMentionParser.ResolveAudience("General status update", validBotMode, members);
        Assert.True(botResult.IsValid);
        Assert.False(botResult.AddressedByMention);
        Assert.Equal(["b2"], botResult.BotIds);

        var missingBotMode = new BotGroupReplyMode { Kind = BotGroupReplyModeKind.Bot, BotId = "b-departed" };
        var missingResult = BotGroupMentionParser.ResolveAudience("General status update", missingBotMode, members);
        Assert.False(missingResult.IsValid);
        Assert.Equal(BotGroupAudienceError.DefaultReplierNotMember, missingResult.Error);

        var missingDraftResult = BotGroupMentionParser.ResolveDraftAudience("General status update", missingBotMode, members);
        Assert.True(missingDraftResult.IsValid);
        Assert.Empty(missingDraftResult.BotIds);

        var mentionsResult = BotGroupMentionParser.ResolveAudience("General status update", MentionsMode, members);
        Assert.True(mentionsResult.IsValid);
        Assert.False(mentionsResult.AddressedByMention);
        Assert.Empty(mentionsResult.BotIds);
    }

    [Fact]
    public void ResolveDraftAudience_IgnoresIncompleteTrailingMentionWhileTyping()
    {
        var members = new BotGroupNamedMember[]
        {
            new("b1", "Sage"),
            new("b2", "Atlas"),
        };

        var typing = BotGroupMentionParser.ResolveDraftAudience("@sa", MentionsMode, members);
        Assert.True(typing.IsValid);
        Assert.Empty(typing.BotIds);

        var typingAfterValid = BotGroupMentionParser.ResolveDraftAudience("@Atlas @sa", MentionsMode, members);
        Assert.True(typingAfterValid.IsValid);
        Assert.True(typingAfterValid.AddressedByMention);
        Assert.Equal(["b2"], typingAfterValid.BotIds);

        // Once the user finishes the unknown token with trailing whitespace, it is flagged.
        var finishedUnknown = BotGroupMentionParser.ResolveDraftAudience("@sa ", MentionsMode, members);
        Assert.False(finishedUnknown.IsValid);
        Assert.Equal(BotGroupAudienceError.UnknownLeadingMention, finishedUnknown.Error);
    }

    [Fact]
    public void ExtractReplyMentions_ExtractsOtherMembersInOrder_AndFiltersSelfEveryoneHistoryCodeAndQuotes()
    {
        var members = new BotGroupNamedMember[]
        {
            new("b1", "Sage"),
            new("b2", "Atlas"),
            new("b3", "Nova"),
            new("b4", "Twin"),
            new("b5", "Twin"),
        };

        var reply = """
            <quoted_room_history>
            [{"authorKind":"owner","authorName":"Owner","text":"@Nova should be ignored here"}]
            </quoted_room_history>
            ```
            @Nova in fenced code
            ```
            `@Nova` in inline code
            > @Nova in block quote
            "@Nova in double quotes" and “@Nova in curly quotes”
            @/src/Nova
            Hey @Sage (self) and @everyone and @Twin (ambiguous):
            First @Atlas please check build, then @Nova deploy, and @Atlas confirm again.
            """;

        var mentions = BotGroupMentionParser.ExtractReplyMentions(reply, members, speakerBotId: "b1");

        Assert.Equal(2, mentions.Count);
        Assert.Equal("b2", mentions[0].BotId);
        Assert.Equal("b3", mentions[1].BotId);
        Assert.True(mentions[0].Start < mentions[1].Start);
        Assert.Equal("@Atlas", reply.Substring(mentions[0].Start, "@Atlas".Length));
        Assert.Equal("@Nova", reply.Substring(mentions[1].Start, "@Nova".Length));
    }

    [Fact]
    public void SplitDisplaySegments_AndPrependReplyMention_WorkAsExpected()
    {
        var names = new[] { "Sage", "Sage Bot", "Atlas" };

        var withEveryone = BotGroupMentionParser.SplitDisplaySegments(
            "Hi @Sage Bot, ping @everyone and @unknown plus @Atlas!",
            names,
            includeEveryone: true);

        Assert.Equal(
            [
                new BotGroupMentionSegment("Hi ", false),
                new BotGroupMentionSegment("@Sage Bot", true),
                new BotGroupMentionSegment(", ping ", false),
                new BotGroupMentionSegment("@everyone", true),
                new BotGroupMentionSegment(" and @unknown plus ", false),
                new BotGroupMentionSegment("@Atlas", true),
                new BotGroupMentionSegment("!", false),
            ],
            withEveryone);

        var withoutEveryone = BotGroupMentionParser.SplitDisplaySegments(
            "Notify @everyone and @Sage",
            names,
            includeEveryone: false);

        Assert.Equal(
            [
                new BotGroupMentionSegment("Notify @everyone and ", false),
                new BotGroupMentionSegment("@Sage", true),
            ],
            withoutEveryone);

        Assert.Empty(BotGroupMentionParser.SplitDisplaySegments("", names));

        var namedMembers = new BotGroupNamedMember[]
        {
            new("b1", "Sage"),
            new("b2", "Atlas"),
        };

        Assert.Equal("@Sage check CI", BotGroupMentionParser.PrependReplyMention("b1", "check CI", namedMembers));
        Assert.Equal("@sage check CI", BotGroupMentionParser.PrependReplyMention("b1", "@sage check CI", namedMembers));
        Assert.Equal("check CI", BotGroupMentionParser.PrependReplyMention("missing-bot", "check CI", namedMembers));
    }
}
