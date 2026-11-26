using Octopus.Remote.Protocol;
using Xunit;

namespace Octopus.Remote.Tests;

public sealed class CollabEnvelopeTests
{
    [Fact]
    public void A_task_delegated_from_another_computer_reads_as_who_where_and_what()
    {
        var text = "[Octopus Collab Task #task_3_ab12 · Thread #t_4_cd34 · Delegated by @Claude Code · Lead · From device MacBook Pro]\n"
            + "Run the integration suite and report failures.\n\n"
            + "When finished (or if you need clarification from the delegating agent), conclude with a concise summary or your question.";

        var envelope = CollabEnvelope.TryParse(text);

        Assert.NotNull(envelope);
        Assert.Equal("Collab Task · Delegated by @Claude Code · Lead", envelope.Header);
        Assert.Equal("MacBook Pro", envelope.FromDevice);
        Assert.Equal("Run the integration suite and report failures.", envelope.Body);
    }

    [Fact]
    public void A_reply_drops_ids_rounds_and_instructions_and_an_ordinary_prompt_is_not_an_envelope()
    {
        var reply = CollabEnvelope.TryParse(
            "[Octopus Collab · From @Codex · Login @ Build Server · Thread #t_2_a0e3 · Round 2/6]\nSQLite or Postgres?\n\n"
            + "(This message comes from another computer: conclude this turn with your answer and Octopus relays it back automatically; reply_thread cannot reach this thread.)");
        Assert.NotNull(reply);
        Assert.Equal("Collab · From @Codex · Login @ Build Server", reply.Header);
        Assert.Null(reply.FromDevice);
        Assert.Equal("SQLite or Postgres?", reply.Body);

        Assert.Null(CollabEnvelope.TryParse("计算一下 1+1"));
        Assert.Null(CollabEnvelope.TryParse("[Octopus Collab without a closing bracket\nbody"));
    }

    [Fact]
    public void Every_envelope_the_bus_builds_parses_back_to_its_body_without_the_footer()
    {
        var built = new (string Text, string Body, string HeaderStart)[]
        {
            (CollabEnvelope.Delegation("task_1_ab", "t_2_cd", "Claude Code · Lead", "Write tests", ["a.cs"]), "Write tests\n\nRelevant files:\n- a.cs", "Collab Task · Delegated by @Claude Code · Lead"),
            (CollabEnvelope.Delegation("task_1_ab", "t_2_cd", "Lead", "Run CI", [], fromDevice: "MacBook"), "Run CI", "Collab Task · Delegated by @Lead"),
            (CollabEnvelope.Delegation("task_1_ab", "t_2_cd", "Lead", "Audit", [], readOnly: true), "Audit", "Collab Task · Delegated by @Lead · Read-Only Mode"),
            (CollabEnvelope.Message("t_2_cd", "Codex", 1, 6, "camelCase?", expectReply: true), "camelCase?", "Collab · From @Codex"),
            (CollabEnvelope.Message("t_2_cd", "Codex", 1, 6, "camelCase?", expectReply: true, toRemote: true), "camelCase?", "Collab · From @Codex"),
            (CollabEnvelope.Reply("t_2_cd", "API", "Codex", 2, 6, "agreed", "Yes."), "Yes.", "Collab Reply · From @Codex"),
            (CollabEnvelope.Reply("t_2_cd", "API", "Codex", 2, 6, "agreed", "Which DB?", autoReportedFromSessionId: "s1"), "Which DB?", "Collab Reply · From @Codex"),
            (CollabEnvelope.Interjection("t_2_cd", "Ship it."), "Ship it.", "Collab · User Interjection"),
        };
        foreach (var (text, body, headerStart) in built)
        {
            var parsed = CollabEnvelope.TryParse(text);
            Assert.NotNull(parsed);
            Assert.Equal(body.Replace("\n", Environment.NewLine, StringComparison.Ordinal).ReplaceLineEndings("\n"), parsed.Body.ReplaceLineEndings("\n"));
            Assert.StartsWith(headerStart, parsed.Header, StringComparison.Ordinal);
        }
        Assert.Equal("MacBook", CollabEnvelope.TryParse(built[1].Text)!.FromDevice);
    }
}
