using System.Text.Json.Nodes;

namespace Octopus.App.Collab.Tools;

/// <summary>MCP tool <c>read_session</c> of the collaboration bus: its contract (name, description, schema, arguments).</summary>
public sealed class ReadSessionTool : ICollabTool
{
    public string Name => "read_session";

    public string Description => "Read a concise summary of recent turns or terminal output from another active Octopus session without waking it up.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("target_session_id"),
        ["properties"] = new JsonObject
        {
            ["target_session_id"] = new JsonObject { ["type"] = "string", ["description"] = "Target session's Octopus item id (from list_peers or @-mention metadata)." },
            ["max_turns"] = new JsonObject { ["type"] = "integer", ["description"] = "Maximum recent turns to summarize (default 5)." },
        },
    };

    public Task<JsonObject> CallAsync(ICollabOperations ops, string callerItemId, CollabToolArgs args, CancellationToken ct)
    {
        return ops.ReadSessionAsync(callerItemId, args.Str("target_session_id") ?? "", Math.Clamp(args.Int("max_turns") ?? 5, 1, 20), ct);
    }
}
