using System.Text.Json.Nodes;

namespace Octopus.App.Collab.Tools;

/// <summary>MCP tool <c>reply_thread</c> of the collaboration bus: its contract (name, description, schema, arguments).</summary>
public sealed class ReplyThreadTool : ICollabTool
{
    public string Name => "reply_thread";

    public string Description =>
        "Reply explicitly to an active cross-session collaboration thread. Records the reply ('recorded': true) and reports wakeup delivery separately in 'delivery' "
        + "('delivered', 'await_events_signaled', 'await_events_consumed', 'wakeup_disabled', 'no_recipient', or 'failed'). 'delivered_to' is included only when a wakeup prompt is accepted; "
        + "otherwise 'recipient_session_id' (when applicable), 'delivery_error' (on failure), and 'note' report status (do not resend a duplicate).";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("thread_id", "message"),
        ["properties"] = new JsonObject
        {
            ["thread_id"] = new JsonObject { ["type"] = "string", ["description"] = "The collaboration thread id from the incoming message header." },
            ["message"] = new JsonObject { ["type"] = "string", ["description"] = "Concise reply, decision, or task deliverable." },
            ["status"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("continue", "agreed", "blocked"),
                ["description"] = "Use 'agreed' once both sides have reached consensus so no unnecessary follow-up turn is triggered.",
            },
        },
    };

    public Task<JsonObject> CallAsync(ICollabOperations ops, string callerItemId, CollabToolArgs args, CancellationToken ct)
    {
        var status = (args.Str("status") ?? "continue").Trim().ToLowerInvariant();
        if (status is not ("continue" or "agreed" or "blocked")) status = "continue";
        return ops.ReplyThreadAsync(callerItemId, args.Str("thread_id") ?? "", args.Str("message") ?? "", status, ct);
    }
}
