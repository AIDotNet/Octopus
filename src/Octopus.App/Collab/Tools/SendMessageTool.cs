using System.Text.Json.Nodes;

namespace Octopus.App.Collab.Tools;

/// <summary>MCP tool <c>send_message</c> of the collaboration bus: its contract (name, description, schema, arguments).</summary>
public sealed class SendMessageTool : ICollabTool
{
    public string Name => "send_message";

    public string Description => "Send a message to an existing Octopus session to negotiate, align interfaces, review work, or ask follow-up questions. Enforces automatic hop limits to prevent loops.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("target_session_id", "message"),
        ["properties"] = new JsonObject
        {
            ["target_session_id"] = new JsonObject { ["type"] = "string", ["description"] = "Existing target session id." },
            ["message"] = new JsonObject { ["type"] = "string", ["description"] = "Message body sent to the target session." },
            ["thread_id"] = new JsonObject { ["type"] = "string", ["description"] = "Optional existing negotiation thread id to continue." },
            ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Optional short topic title when starting a new thread." },
            ["expect_reply"] = new JsonObject { ["type"] = "boolean", ["description"] = "Whether the target should reply back to this thread (default true)." },
            ["max_turns"] = new JsonObject { ["type"] = "integer", ["description"] = "Optional hop limit override for this thread." },
        },
    };

    public Task<JsonObject> CallAsync(ICollabOperations ops, string callerItemId, CollabToolArgs args, CancellationToken ct)
    {
        return ops.SendMessageAsync(callerItemId, new CollabSendRequest(args.Str("target_session_id") ?? "", args.Str("message") ?? "")
        {
            ThreadId = args.Str("thread_id"),
            Title = args.Str("title"),
            ExpectReply = args.Bool("expect_reply") ?? true,
            MaxTurns = args.Int("max_turns"),
        }, ct);
    }
}
