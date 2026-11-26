using Octopus.Core.Settings;

namespace Octopus.Core.Agents;

/// <summary>How an agent tab opens in the workspace.</summary>
public enum AgentSurface
{
    /// <summary>The agent's own CLI inside a terminal emulator.</summary>
    Terminal,
    /// <summary>Structured graphical conversation driven by the Agent Client Protocol (ACP).</summary>
    Gui,
}

/// <summary>How Octopus resolves ACP adapters for agents whose CLI does not embed ACP natively.</summary>
public enum AcpAdapterPolicy
{
    /// <summary>Only use locally installed native CLIs or adapter binaries already on PATH.</summary>
    NativeOnly,
    /// <summary>Prefer local binaries; fall back to local <c>npx</c>/<c>uvx</c> runners for official adapters.</summary>
    UseLocalRunners,
    /// <summary>Allow downloading and verifying managed binaries from the official ACP Registry.</summary>
    ManagedRegistry,
}

/// <summary>Who enforces tool-approval policy during an ACP session.</summary>
public enum AcpPermissionStrategy
{
    /// <summary>The agent calls <c>session/request_permission</c> and Octopus enforces the selected mode.</summary>
    ClientApproval,
    /// <summary>The agent enforces its own session mode via <c>session/set_mode</c> or <c>session/set_config_option</c>.</summary>
    AgentMode,
}

/// <summary>
/// How an agent speaks the Agent Client Protocol (ACP). Null on <see cref="AgentDefinition.Acp"/> means the agent
/// does not support ACP and always launches in a terminal.
/// </summary>
public sealed class AgentAcpDefinition
{
    /// <summary>Transport kind: <c>stdio</c> (local subprocess), <c>ws</c>/<c>websocket</c>, or <c>http</c>/<c>streamable-http</c>.</summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>Remote ACP endpoint URL when <see cref="Transport"/> is <c>ws</c>/<c>websocket</c> or <c>http</c>/<c>streamable-http</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Optional HTTP/WebSocket headers sent when connecting to a remote ACP <see cref="Endpoint"/> (e.g. Authorization).</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary><c>native</c> (the agent's own CLI executable) or <c>adapter</c> (a companion ACP bridge/package).</summary>
    public string Mode { get; set; } = "native";

    /// <summary>Arguments passed to the native CLI to start its ACP server (e.g. <c>["acp"]</c> or <c>["--acp"]</c>).</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>Extra arguments added only when starting in full-access permission mode (e.g. <c>--always-approve</c>).</summary>
    public List<string> FullAccessArgs { get; set; } = [];

    /// <summary>Environment variables applied when launching the ACP process.</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>Dialect identifier in <c>AcpDialectRegistry</c> (<c>generic</c> when omitted).</summary>
    public string Dialect { get; set; } = "generic";

    /// <summary>Optional minimum CLI version known to support ACP.</summary>
    public string? MinVersion { get; set; }

    /// <summary>Whether permission modes are handled client-side or via native session modes.</summary>
    public AcpPermissionStrategy PermissionStrategy { get; set; } = AcpPermissionStrategy.ClientApproval;

    /// <summary>Maps Octopus unified permission mode ids (<see cref="PermissionModes"/>) to the agent's native ACP mode id.</summary>
    public Dictionary<string, string>? PermissionModes { get; set; }

    /// <summary>Optional companion adapter / official ACP Registry distribution spec.</summary>
    public AgentAcpAdapterSpec? Adapter { get; set; }
}

/// <summary>Companion adapter executable or runner package (e.g. official <c>@agentclientprotocol/*</c> adapters).</summary>
public sealed class AgentAcpAdapterSpec
{
    /// <summary>Agent id in the official ACP Registry (<c>registry.json</c>).</summary>
    public string? RegistryId { get; set; }

    /// <summary>Local executable names to look for on PATH before using runners (e.g. <c>claude-agent-acp</c>).</summary>
    public List<string> Executables { get; set; } = [];

    /// <summary>Arguments passed to a locally installed adapter executable.</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>npm package invoked via <c>npx -y</c> when no local adapter binary is found.</summary>
    public string? NpxPackage { get; set; }

    /// <summary>Extra arguments passed after <see cref="NpxPackage"/>.</summary>
    public List<string> NpxArgs { get; set; } = [];

    /// <summary>
    /// When true and the native agent CLI is already installed locally, pass <c>--omit=optional</c> during
    /// managed npm adapter installation to skip redundant platform-bundled CLI binaries (e.g. Claude SDK's 364MB binary).
    /// </summary>
    public bool NpxOmitOptionalWhenInstalled { get; set; }

    /// <summary>Environment variable populated with the locally installed native CLI path when launching the adapter.</summary>
    public string? NativeExecutableEnvVar { get; set; }

    /// <summary>
    /// On Windows, resolves npm <c>.cmd</c>/<c>.bat</c> shims in <see cref="NativeExecutableEnvVar"/> to their underlying
    /// target (needed when a Node adapter spawns the path without a shell, e.g. <c>CLAUDE_CODE_EXECUTABLE</c>).
    /// Disable when the adapter uses <c>cross-spawn</c> to run batch shims directly (e.g. <c>CODEX_PATH</c>).
    /// </summary>
    public bool ResolveWindowsBatchShim { get; set; } = true;

    /// <summary>Python package invoked via <c>uvx</c> when no local adapter binary is found.</summary>
    public string? UvxPackage { get; set; }

    /// <summary>Extra arguments passed after <see cref="UvxPackage"/>.</summary>
    public List<string> UvxArgs { get; set; } = [];
}

/// <summary>Model Context Protocol (MCP) server definition forwarded via ACP <c>session/new|load|resume|fork</c> (<c>mcpServers</c>).</summary>
public sealed class AcpMcpServerConfig
{
    /// <summary>Unique display name of the MCP server.</summary>
    public string Name { get; set; } = "";

    /// <summary>Transport type: <c>stdio</c>, <c>http</c>, or <c>sse</c>.</summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>Executable command (for <c>stdio</c> transport).</summary>
    public string? Command { get; set; }

    /// <summary>Command arguments (for <c>stdio</c> transport).</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>Environment variables (for <c>stdio</c> transport).</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>Endpoint URL (for <c>http</c> or <c>sse</c> transport).</summary>
    public string? Url { get; set; }

    /// <summary>HTTP headers (for <c>http</c> or <c>sse</c> transport).</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>Whether this MCP server is forwarded to ACP sessions.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Single source of truth for deciding whether an agent opens as GUI (ACP) or Terminal.
/// Enforces the hard rule: any agent without ACP support always opens in a terminal.
/// </summary>
public static class AgentSurfaceResolver
{
    public static AgentSurface Resolve(
        AgentDefinition? definition,
        AgentSettings settings,
        AgentSurface? explicitOverride = null,
        bool acpAvailable = true)
    {
        if (definition is null || !definition.SupportsAcp || !acpAvailable)
        {
            return AgentSurface.Terminal;
        }
        if (explicitOverride is { } chosen)
        {
            return chosen;
        }
        var profile = settings.ProfileOf(definition.Id);
        return profile.Surface ?? settings.DefaultSurface;
    }
}
