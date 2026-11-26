using System.Text.Json.Nodes;

namespace Octopus.App.Collab.Tools;

/// <summary>MCP tool <c>await_events</c> of the collaboration bus: its contract (name, description, schema, arguments).</summary>
public sealed class AwaitEventsTool : ICollabTool
{
    public string Name => "await_events";

    public string Description => "Wait briefly (up to 45s) for delegated tasks or negotiation threads to report updates or finish. Each change is reported once: finished items you have already seen are omitted unless you name them in ids. Returns immediately when there is news or when nothing is pending, otherwise returns live progress on timeout.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["ids"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Optional list of task_id and/or thread_id values to watch (empty watches all active items for caller).",
            },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Seconds to wait before returning current progress (1-45, default 25)." },
        },
    };

    public Task<JsonObject> CallAsync(ICollabOperations ops, string callerItemId, CollabToolArgs args, CancellationToken ct)
    {
        return ops.AwaitEventsAsync(callerItemId, args.StringList("ids"), Math.Clamp(args.Int("timeout_seconds") ?? 25, 1, 45), ct);
    }
}
