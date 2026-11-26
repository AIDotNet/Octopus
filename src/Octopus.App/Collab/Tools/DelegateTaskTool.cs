using System.Text.Json.Nodes;

namespace Octopus.App.Collab.Tools;

/// <summary>MCP tool <c>delegate_task</c> of the collaboration bus: its contract (name, description, schema, arguments).</summary>
public sealed class DelegateTaskTool : ICollabTool
{
    public string Name => "delegate_task";

    public string Description => "Assign a task asynchronously to another agent (e.g. 'codex', 'agy', 'claude') or to an existing session id. Spawning a new agent nests it visually under your session in Octopus and automatically notifies you upon completion.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("target", "goal"),
        ["properties"] = new JsonObject
        {
            ["target"] = new JsonObject { ["type"] = "string", ["description"] = "Agent id/alias (e.g. 'codex', 'agy', 'claude') to spawn, OR an existing session_id to reuse." },
            ["goal"] = new JsonObject { ["type"] = "string", ["description"] = "Clear, self-contained task specification and expected output." },
            ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Short title shown under your session in the sidebar and collab ribbon." },
            ["isolation"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("shared", "worktree", "read_only"),
                ["description"] = "Workspace isolation override. Omit to follow the user's global Octopus setting (default: shared). Use 'worktree' for isolated git branch edits or 'read_only' for audits/reviews.",
            },
            ["surface"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("gui", "terminal"),
                ["description"] = "Optional UI surface override ('gui' ACP or 'terminal' CLI). Defaults to Octopus settings.",
            },
            ["model"] = new JsonObject { ["type"] = "string", ["description"] = "Optional model id override for the target agent." },
            ["effort"] = new JsonObject { ["type"] = "string", ["description"] = "Optional reasoning effort override." },
            ["project_id"] = new JsonObject { ["type"] = "string", ["description"] = "Optional remote project id from list_peers.remote_projects: starts the agent on that other computer (isolation 'shared' only)." },
            ["context_files"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Optional list of relevant workspace file paths to highlight for the delegate.",
            },
        },
    };

    public Task<JsonObject> CallAsync(ICollabOperations ops, string callerItemId, CollabToolArgs args, CancellationToken ct)
    {
        return ops.DelegateTaskAsync(callerItemId, new CollabDelegateRequest(args.Str("target") ?? "", args.Str("goal") ?? "")
        {
            Title = args.Str("title"),
            Isolation = args.Str("isolation"),
            Surface = args.Str("surface"),
            Model = args.Str("model"),
            Effort = args.Str("effort"),
            ContextFiles = args.StringList("context_files"),
            ProjectId = args.Str("project_id"),
        }, ct);
    }
}
