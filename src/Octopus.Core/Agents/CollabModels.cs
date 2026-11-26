namespace Octopus.Core.Agents;

/// <summary>Default workspace isolation strategy when one agent delegates a task to another agent.</summary>
public enum CollabIsolationMode
{
    /// <summary>Share the caller's current working directory (default).</summary>
    Shared,
    /// <summary>Create a dedicated git worktree branch workspace when the project is a local git repository.</summary>
    Worktree,
    /// <summary>Launch the delegated agent in read-only / plan mode without modifying files.</summary>
    ReadOnly,
}

/// <summary>Global settings governing inter-agent task delegation, @-mentions, and cross-session negotiation.</summary>
public sealed class CollabSettings
{
    public const int DefaultMaxNegotiationHops = 6;
    public const int MinNegotiationHops = 2;
    public const int MaxAllowedNegotiationHops = 24;
    public const int DefaultMaxDelegationDepth = 2;

    /// <summary>Enables the local Octopus MCP collaboration bus for agent sessions.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default workspace isolation used when an agent delegates a task without specifying an explicit override.
    /// Defaults to <see cref="CollabIsolationMode.Shared"/>; users can switch to <see cref="CollabIsolationMode.Worktree"/>
    /// or <see cref="CollabIsolationMode.ReadOnly"/> globally and still override per delegation.
    /// </summary>
    public CollabIsolationMode DefaultIsolation { get; set; } = CollabIsolationMode.Shared;

    /// <summary>Maximum back-and-forth rounds permitted on a single negotiation thread before pausing for user approval.</summary>
    public int MaxNegotiationHops { get; set; } = DefaultMaxNegotiationHops;

    /// <summary>Maximum nested delegation depth (e.g. 2 allows Parent → Child → Grandchild and blocks deeper loops).</summary>
    public int MaxDelegationDepth { get; set; } = DefaultMaxDelegationDepth;

    /// <summary>Automatically queues a wakeup prompt back to the calling session when a delegated task or peer reply arrives.</summary>
    public bool AutoWakeupCaller { get; set; } = true;

    /// <summary>Injects the local collaboration MCP server into native terminal agent tabs and bridges PTY input/output via signals.</summary>
    public bool EnableTerminalBridge { get; set; } = true;

    /// <summary>
    /// Lets agents see and delegate to sessions in projects on paired Octopus hosts (projects you added from another
    /// computer). The remote agent runs on that host with that host's permissions; its permission requests surface here.
    /// </summary>
    public bool AllowRemoteHosts { get; set; } = true;
}
