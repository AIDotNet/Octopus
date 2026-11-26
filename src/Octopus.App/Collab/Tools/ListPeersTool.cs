using System.Text.Json.Nodes;

namespace Octopus.App.Collab.Tools;

/// <summary>MCP tool <c>list_peers</c> of the collaboration bus: its contract (name, description, schema, arguments).</summary>
public sealed class ListPeersTool : ICollabTool
{
    public string Name => "list_peers";

    public string Description => "List active agent/terminal sessions in the current Octopus project (or merged project group), sessions in projects on other paired computers (with their host), remote projects you can delegate into, plus installed coding agents (e.g. codex, agy, claude) available for delegation.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["scope"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("project", "group"),
                ["description"] = "Defaults to 'group' (current project plus merged-workspace group peers).",
            },
        },
    };

    public Task<JsonObject> CallAsync(ICollabOperations ops, string callerItemId, CollabToolArgs args, CancellationToken ct)
    {
        var scope = args.Str("scope") ?? "group";
        var peersArr = new JsonArray();
        foreach (var p in ops.ListPeers(callerItemId, scope))
        {
            var peer = new JsonObject
            {
                ["session_id"] = p.ItemId,
                ["is_self"] = p.ItemId == callerItemId,
                ["agent_id"] = p.AgentId,
                ["agent_name"] = p.AgentName,
                ["title"] = p.Title,
                ["surface"] = p.Surface,
                ["status"] = p.Status,
                ["working_directory"] = p.WorkingDirectory,
                ["branch"] = p.Branch,
                ["parent_session_id"] = p.ParentItemId,
                ["isolation"] = p.Isolation,
                ["summary"] = p.Summary,
            };
            if (p.Host is not null) peer["host"] = p.Host;
            peersArr.Add((JsonNode)peer);
        }

        var agentsArr = new JsonArray();
        foreach (var a in ops.ListAvailableAgents())
        {
            var aliasArr = new JsonArray();
            foreach (var al in a.Aliases) aliasArr.Add((JsonNode)JsonValue.Create(al));
            agentsArr.Add((JsonNode)new JsonObject
            {
                ["agent_id"] = a.AgentId,
                ["name"] = a.Name,
                ["supports_acp"] = a.SupportsAcp,
                ["default_surface"] = a.DefaultSurface,
                ["aliases"] = aliasArr,
            });
        }

        var payload = new JsonObject
        {
            ["caller_session_id"] = callerItemId,
            ["default_isolation"] = CollabCoordinator.IsolationToKey(ops.DefaultIsolation),
            ["active_sessions"] = peersArr,
            ["available_agents"] = agentsArr,
        };
        var remoteProjects = ops.ListRemoteProjects();
        if (remoteProjects.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var project in remoteProjects)
            {
                arr.Add((JsonNode)new JsonObject
                {
                    ["project_id"] = project.ProjectId,
                    ["name"] = project.Name,
                    ["host"] = project.Host,
                    ["path"] = project.Path,
                });
            }
            payload["remote_projects"] = arr;
            payload["remote_hint"] = "Pass a remote project_id to delegate_task to start an agent on that computer; send_message/read_session/cancel_task work on remote sessions too.";
        }
        return Task.FromResult(CollabToolResult.Ok(payload));
    }
}
