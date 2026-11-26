using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Core.Agents;

namespace Octopus.App.Collab.Tools;

/// <summary>
/// One MCP tool of the <c>octopus-collab</c> server: its contract (name, description, input schema) and the mapping
/// from JSON arguments to a typed operation on <see cref="ICollabOperations"/>.
/// <para>
/// To add a tool: write a class implementing this interface and add it to <see cref="CollabToolRegistry.BuiltIn"/> (keep the
/// business logic behind <see cref="ICollabOperations"/>). Tools are stateless. Extra tools can also be registered as
/// <see cref="ICollabTool"/> services; they are offered after the built-in ones and may replace one by name.
/// </para>
/// </summary>
public interface ICollabTool
{
    string Name { get; }
    string Description { get; }
    JsonObject InputSchema { get; }

    /// <summary>Runs the tool for <paramref name="callerItemId"/> (the session the MCP token belongs to); returns an MCP tool result.</summary>
    Task<JsonObject> CallAsync(ICollabOperations ops, string callerItemId, CollabToolArgs args, CancellationToken ct);
}

/// <summary>The operations collaboration tools are built from (implemented by <see cref="CollabCoordinator"/>).</summary>
/// <remarks>The heavier ones still return MCP results (<see cref="CollabToolResult"/>) rather than typed values.</remarks>
public interface ICollabOperations
{
    IReadOnlyList<CollabPeerInfo> ListPeers(string callerItemId, string scope = "group");
    IReadOnlyList<CollabAvailableAgentInfo> ListAvailableAgents();
    /// <summary>Projects on paired computers that agents may delegate into (empty when remote hosts are off).</summary>
    IReadOnlyList<CollabRemoteProjectInfo> ListRemoteProjects();
    CollabIsolationMode DefaultIsolation { get; }

    Task<JsonObject> ReadSessionAsync(string callerItemId, string targetSessionId, int maxTurns, CancellationToken ct);
    Task<JsonObject> DelegateTaskAsync(string callerItemId, CollabDelegateRequest request, CancellationToken ct);
    Task<JsonObject> SendMessageAsync(string callerItemId, CollabSendRequest request, CancellationToken ct);
    Task<JsonObject> ReplyThreadAsync(string callerItemId, string threadId, string message, string status, CancellationToken ct);
    Task<JsonObject> AwaitEventsAsync(string callerItemId, IReadOnlyCollection<string> ids, int timeoutSeconds, CancellationToken ct);
    Task<JsonObject> CancelDelegatedTaskAsync(string callerItemId, string taskId, string? reason, CancellationToken ct);
}

/// <summary>What <c>delegate_task</c> asks for. Empty <see cref="Target"/> / <see cref="Goal"/> are rejected by the operation.</summary>
public sealed record CollabDelegateRequest(string Target, string Goal)
{
    public string? Title { get; init; }
    /// <summary><c>shared</c>, <c>worktree</c>, <c>read_only</c>; null follows the user's default.</summary>
    public string? Isolation { get; init; }
    /// <summary><c>gui</c> or <c>terminal</c>; null follows the settings.</summary>
    public string? Surface { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public IReadOnlyList<string> ContextFiles { get; init; } = [];
    /// <summary>A remote project to start the agent in (another computer); null for the caller's own project.</summary>
    public string? ProjectId { get; init; }
}

/// <summary>What <c>send_message</c> asks for.</summary>
public sealed record CollabSendRequest(string TargetSessionId, string Message)
{
    public string? ThreadId { get; init; }
    public string? Title { get; init; }
    public bool ExpectReply { get; init; } = true;
    public int? MaxTurns { get; init; }
}

/// <summary>A project of a paired computer, as <c>list_peers</c> reports it.</summary>
public sealed record CollabRemoteProjectInfo(string ProjectId, string Name, string Host, string Path);

/// <summary>Typed access to a tool call's JSON arguments.</summary>
public readonly struct CollabToolArgs(JsonObject? args)
{
    public string? Str(string key) =>
        args is not null && args.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public int? Int(string key) =>
        args is not null && args.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;

    public bool? Bool(string key) =>
        args is not null && args.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>The non-blank strings of an array argument, trimmed.</summary>
    public IReadOnlyList<string> StringList(string key)
    {
        var list = new List<string>();
        if (args is not null && args.TryGetPropertyValue(key, out var node) && node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
            }
        }
        return list;
    }
}

/// <summary>MCP tool results: a JSON payload as text, or an error message.</summary>
public static class CollabToolResult
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        // Keep CJK and symbols readable for the model (and 6x cheaper) instead of \uXXXX escapes.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonObject Ok(JsonObject payload) => new()
    {
        ["content"] = new JsonArray
        {
            (JsonNode)new JsonObject { ["type"] = "text", ["text"] = payload.ToJsonString(Options) },
        },
    };

    public static JsonObject Error(string message) => new()
    {
        ["isError"] = true,
        ["content"] = new JsonArray
        {
            (JsonNode)new JsonObject { ["type"] = "text", ["text"] = message },
        },
    };
}

/// <summary>The collaboration tools by name, and their <c>tools/list</c> schemas.</summary>
public sealed class CollabToolRegistry
{
    private readonly Dictionary<string, ICollabTool> _byName = new(StringComparer.Ordinal);
    private readonly List<ICollabTool> _ordered = [];

    /// <param name="tools">Later registrations of the same name replace earlier ones (a build can override a built-in).</param>
    public CollabToolRegistry(IEnumerable<ICollabTool> tools)
    {
        foreach (var tool in tools)
        {
            if (_byName.TryGetValue(tool.Name, out var existing)) _ordered.Remove(existing);
            _byName[tool.Name] = tool;
            _ordered.Add(tool);
        }
    }

    /// <summary>The tools Octopus ships, in the order agents see them.</summary>
    public static IReadOnlyList<ICollabTool> BuiltIn { get; } =
    [
        new ListPeersTool(),
        new ReadSessionTool(),
        new DelegateTaskTool(),
        new SendMessageTool(),
        new ReplyThreadTool(),
        new AwaitEventsTool(),
        new CancelTaskTool(),
    ];

    public IReadOnlyList<ICollabTool> Tools => _ordered;

    public ICollabTool? Find(string name) => _byName.GetValueOrDefault(name);

    public JsonArray Schemas()
    {
        var arr = new JsonArray();
        foreach (var tool in _ordered)
        {
            arr.Add((JsonNode)new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema,
            });
        }
        return arr;
    }
}
