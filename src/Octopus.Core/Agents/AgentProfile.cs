using System.Text.Json.Serialization;

namespace Octopus.Core.Agents;

/// <summary>The user's defaults for one agent (Settings → Agents). Null means "the agent's own default".</summary>
public sealed class AgentProfile
{
    public bool Enabled { get; set; } = true;
    /// <summary>Replaces the detected executable; may carry arguments, e.g. <c>rec --profile work</c>.</summary>
    public string? Command { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public bool? Fast { get; set; }
    /// <summary>A <see cref="PermissionModes"/> id; null follows the global default.</summary>
    public string? Permission { get; set; }
    /// <summary>Per-agent surface override (GUI vs Terminal); null follows <c>AgentSettings.DefaultSurface</c>. Ignored when the agent does not support ACP.</summary>
    public AgentSurface? Surface { get; set; }
    /// <summary>Global default account id for this agent; null uses the implicit <c>default</c> account.</summary>
    public string? DefaultAccountId { get; set; }
    /// <summary>Unified global default backend (<c>account:&lt;id&gt;</c> or <c>provider:&lt;id&gt;</c>); falls back to <see cref="DefaultAccountId"/> when null.</summary>
    public string? DefaultBackend { get; set; }
    public List<string> ExtraArgs { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = [];
    /// <summary>Feature id to value. bool: "true"; list: items separated by newlines.</summary>
    public Dictionary<string, string> Features { get; set; } = [];
}

/// <summary>What was chosen for one launch (the "+" menu). Overrides the profile where set.</summary>
public sealed class AgentLaunchOptions
{
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public bool? Fast { get; set; }
    /// <summary>Optional per-launch permission mode override (e.g. <c>plan</c> for read-only delegated agents).</summary>
    public string? Permission { get; set; }
    /// <summary>Account chosen for this tab in the "+" launcher; overrides project and profile defaults when set.</summary>
    public string? AccountId { get; set; }
    /// <summary>Unified backend chosen for this tab (<c>account:&lt;id&gt;</c>, <c>provider:&lt;id&gt;</c>, or <c>tunnel:&lt;id&gt;</c>); overrides project and profile defaults when set.</summary>
    public string? BackendRef { get; set; }
    /// <summary>Surface explicitly chosen for this launch (GUI or Terminal); null follows the profile and global default.</summary>
    public AgentSurface? Surface { get; set; }

    /// <summary>The initial prompt. Used for the first start only and never saved.</summary>
    [JsonIgnore]
    public string? Prompt { get; set; }

    /// <summary>Collaboration thread id for the initial delegated prompt. Never saved.</summary>
    [JsonIgnore]
    public string? CollabThreadId { get; set; }

    /// <summary>Collaboration task id for the initial delegated prompt. Never saved.</summary>
    [JsonIgnore]
    public string? CollabTaskId { get; set; }

    /// <summary>Collaboration per-delivery id for the initial delegated prompt. Never saved.</summary>
    [JsonIgnore]
    public string? CollabDeliveryId { get; set; }

    /// <summary>Continue the most recent session (used when a tab is restored after a restart).</summary>
    [JsonIgnore]
    public bool Resume { get; set; }

    /// <summary>
    /// The saved session the tab continues (chosen in the session list, or made by a migration). Kept with the tab, so a
    /// restored tab continues this one rather than whichever session is the latest.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Variables for this tab only, over the profile's: the account the session belongs to (<c>CLAUDE_CONFIG_DIR</c>,
    /// <c>CODEX_HOME</c>). Null when the tab runs with the profile's account.
    /// </summary>
    public Dictionary<string, string>? Env { get; set; }

    /// <summary>
    /// Runtime-only remote host tunnel provider spec (populated on the remote host when a controller launches a tunneled
    /// session). Never persisted to disk.
    /// </summary>
    [JsonIgnore]
    public RemoteTunnelProviderSpec? TunnelProvider { get; set; }

    /// <summary>Runtime-only loopback proxy base URL on the remote host (e.g. <c>http://127.0.0.1:54321/t/&lt;grant&gt;</c>). Never persisted.</summary>
    [JsonIgnore]
    public string? TunnelLoopbackBaseUrl { get; set; }
}

/// <summary>Where an agent was found on this machine.</summary>
public sealed class AgentInstallation
{
    public string AgentId { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Version { get; set; }
}

/// <summary>The content of agent-scan.json.</summary>
public sealed class AgentScanResult
{
    public DateTimeOffset ScannedAt { get; set; }
    public List<AgentInstallation> Installations { get; set; } = [];
}

/// <summary>The compiled command of an agent launch: an executable with an argument array, never a joined string.</summary>
public sealed record AgentCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string>? UnsetEnvironment = null);
