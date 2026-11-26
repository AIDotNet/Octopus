using System.Text.Json.Serialization;

namespace Octopus.App.Collab.Ledger;

// The persisted form of the ledger (collab/state.json). Source-generated (NativeAOT safe). Names match the first,
// hand-written version of the file so ledgers written by it still load. To add a field: add it here and map it in
// CollabLedger.ToDocument / FromDocument; bump Version only when an old file needs converting.

internal sealed class CollabLedgerDocument
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("id_seq")] public int IdSeq { get; set; }
    [JsonPropertyName("event_seq")] public long EventSeq { get; set; }
    [JsonPropertyName("tasks")] public List<CollabTaskDocument> Tasks { get; set; } = [];
    [JsonPropertyName("threads")] public List<CollabThreadDocument> Threads { get; set; } = [];
    /// <summary>Per caller: task/thread id → the sequence number await_events last reported.</summary>
    [JsonPropertyName("seen")] public Dictionary<string, Dictionary<string, long>> Seen { get; set; } = [];
}

internal sealed class CollabTaskDocument
{
    [JsonPropertyName("task_id")] public string TaskId { get; set; } = "";
    [JsonPropertyName("caller")] public string Caller { get; set; } = "";
    [JsonPropertyName("target")] public string Target { get; set; } = "";
    [JsonPropertyName("agent")] public string Agent { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("goal")] public string Goal { get; set; } = "";
    [JsonPropertyName("isolation")] public string? Isolation { get; set; }
    [JsonPropertyName("worktree_branch")] public string? WorktreeBranch { get; set; }
    [JsonPropertyName("worktree_path")] public string? WorktreePath { get; set; }
    [JsonPropertyName("thread_id")] public string? ThreadId { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("activity")] public string? Activity { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("created")] public DateTimeOffset? Created { get; set; }
    [JsonPropertyName("completed")] public DateTimeOffset? Completed { get; set; }
    [JsonPropertyName("seq")] public long Seq { get; set; }
}

internal sealed class CollabThreadDocument
{
    [JsonPropertyName("thread_id")] public string ThreadId { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("initiator")] public string Initiator { get; set; } = "";
    [JsonPropertyName("participants")] public List<string> Participants { get; set; } = [];
    [JsonPropertyName("hops")] public int Hops { get; set; }
    [JsonPropertyName("max_hops")] public int? MaxHops { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("updated")] public DateTimeOffset? Updated { get; set; }
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("messages")] public List<CollabMessageDocument> Messages { get; set; } = [];
}

internal sealed class CollabMessageDocument
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("from_label")] public string FromLabel { get; set; } = "";
    [JsonPropertyName("from_agent")] public string? FromAgent { get; set; }
    [JsonPropertyName("to")] public string? To { get; set; }
    [JsonPropertyName("to_label")] public string? ToLabel { get; set; }
    [JsonPropertyName("round")] public int Round { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("user")] public bool User { get; set; }
    [JsonPropertyName("at")] public DateTimeOffset? At { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CollabLedgerDocument))]
internal sealed partial class CollabLedgerJsonContext : JsonSerializerContext;
