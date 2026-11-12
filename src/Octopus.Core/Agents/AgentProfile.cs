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

    /// <summary>The initial prompt. Used for the first start only and never saved.</summary>
    [JsonIgnore]
    public string? Prompt { get; set; }

    /// <summary>Continue the most recent session (used when a tab is restored after a restart).</summary>
    [JsonIgnore]
    public bool Resume { get; set; }
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
    IReadOnlyDictionary<string, string> Environment);
