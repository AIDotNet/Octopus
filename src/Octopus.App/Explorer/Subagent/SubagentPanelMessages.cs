using Octopus.App.AgentGui;

namespace Octopus.App.Explorer.Subagent;

/// <summary>
/// Broadcast when the user clicks any subagent (from the left sidebar, the Agent GUI activity tray,
/// or a workflow task owner chip) so the shell expands the right panel and opens the singleton subagent timeline.
/// </summary>
public sealed record OpenSubagentPanelRequested(
    string ParentItemId,
    string SubagentId,
    string? WorkingDirectory = null,
    SubagentRunViewModel? LiveRun = null,
    IReadOnlyList<SubagentRunViewModel>? SiblingRuns = null);
