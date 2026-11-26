using System.Text.Json.Nodes;

namespace Octopus.App.Collab.Tools;

/// <summary>MCP tool <c>cancel_task</c> of the collaboration bus: its contract (name, description, schema, arguments).</summary>
public sealed class CancelTaskTool : ICollabTool
{
    public string Name => "cancel_task";

    public string Description => "Cancel a task you delegated: stops the delegate's running turn and also cancels anything that delegate delegated further. Use it when the user changes their mind or the result is no longer needed.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("task_id"),
        ["properties"] = new JsonObject
        {
            ["task_id"] = new JsonObject { ["type"] = "string", ["description"] = "The task_id returned by delegate_task." },
            ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "Optional short reason, recorded as the task result." },
        },
    };

    public Task<JsonObject> CallAsync(ICollabOperations ops, string callerItemId, CollabToolArgs args, CancellationToken ct)
    {
        return ops.CancelDelegatedTaskAsync(callerItemId, args.Str("task_id") ?? "", args.Str("reason")?.Trim(), ct);
    }
}
