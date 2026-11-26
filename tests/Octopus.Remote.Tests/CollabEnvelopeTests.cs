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
}
