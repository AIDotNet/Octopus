using System.Text.Json;

namespace Octopus.Agents.Signals;

/// <summary>One in-flight background task reported by a hook payload (Claude Code <c>Stop.background_tasks</c>).</summary>
public sealed record SignalBackgroundTask(string? Id, string Type, string Status, string? Description, string? Name);

/// <summary>
/// Active background work reported in a turn-end signal's <c>background_tasks</c> array. Claude Code includes this on
/// <c>Stop</c> so consumers can tell "session is done" from "session is paused waiting for background work to wake it".
/// </summary>
public readonly record struct SignalBackgroundTaskSummary(
    bool HasActiveWorkflow,
    bool HasActiveSubagent,
    bool HasActiveDelegatedWork,
    IReadOnlyList<SignalBackgroundTask> ActiveSubagents)
{
    public static SignalBackgroundTaskSummary Empty { get; } = new(false, false, false, []);

    /// <summary>
    /// True when local delegated work (a dynamic workflow or background subagent) is running or pending: Claude Code
    /// keeps the session busy until it completes and wakes a follow-up turn.
    /// </summary>
    public bool HasActiveLocalWork => HasActiveWorkflow || HasActiveSubagent;
}

/// <summary>
/// Parses Claude Code's <c>background_tasks</c> field from <c>Stop</c> hook payloads. Only delegated agentic work
/// (workflows, background subagents, and — when the session title stays active — cloud sessions / teammates) counts as
/// keeping the session busy; background shells, monitors and scheduled crons leave the session idle.
/// </summary>
public static class SignalBackgroundTasks
{
    public static SignalBackgroundTaskSummary Read(string? payload)
    {
        if (string.IsNullOrEmpty(payload) || !payload.Contains("background_tasks", StringComparison.Ordinal))
        {
            return SignalBackgroundTaskSummary.Empty;
        }
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("background_tasks", out var tasks)
                || tasks.ValueKind != JsonValueKind.Array)
            {
                return SignalBackgroundTaskSummary.Empty;
            }

            var hasWorkflow = false;
            var hasSubagent = false;
            var hasDelegated = false;
            List<SignalBackgroundTask>? subagents = null;

            foreach (var item in tasks.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var status = String(item, "status");
                if (!IsActiveStatus(status))
                {
                    continue;
                }
                var type = String(item, "type");
                if (string.IsNullOrEmpty(type))
                {
                    continue;
                }
                if (IsWorkflowType(type))
                {
                    hasWorkflow = true;
                    hasDelegated = true;
                }
                else if (IsSubagentType(type))
                {
                    hasSubagent = true;
                    hasDelegated = true;
                    subagents ??= [];
                    subagents.Add(new SignalBackgroundTask(
                        String(item, "id"),
                        type,
                        status!,
                        String(item, "description"),
                        String(item, "name")));
                }
                else if (IsOtherDelegatedType(type))
                {
                    hasDelegated = true;
                }
            }

            return hasDelegated
                ? new SignalBackgroundTaskSummary(hasWorkflow, hasSubagent, true, subagents ?? (IReadOnlyList<SignalBackgroundTask>)[])
                : SignalBackgroundTaskSummary.Empty;
        }
        catch (JsonException)
        {
            return SignalBackgroundTaskSummary.Empty;
        }
    }

    private static bool IsActiveStatus(string? status) =>
        string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase);

    private static bool IsWorkflowType(string type) =>
        string.Equals(type, "workflow", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "local_workflow", StringComparison.OrdinalIgnoreCase);

    private static bool IsSubagentType(string type) =>
        string.Equals(type, "subagent", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "local_agent", StringComparison.OrdinalIgnoreCase);

    private static bool IsOtherDelegatedType(string type) =>
        string.Equals(type, "cloud session", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "remote_agent", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "teammate", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "in_process_teammate", StringComparison.OrdinalIgnoreCase);

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
