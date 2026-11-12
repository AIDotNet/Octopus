using Octopus.Agents.Signals;
using Octopus.Core.Agents;

namespace Octopus.Agents;

/// <summary>Every known agent: the built-in definitions merged with the user's own (same id replaces).</summary>
public interface IAgentCatalog
{
    IReadOnlyList<AgentDefinition> Definitions { get; }
    AgentDefinition? Find(string agentId);
    /// <summary>Reads the user's definitions again.</summary>
    void Reload();
    event EventHandler? Changed;
}

/// <summary>An agent definition file that does not live in the user's agents folder: one a plugin ships.</summary>
/// <param name="Path">The JSON file (same schema as the agents folder).</param>
/// <param name="Origin">Who provides it (a plugin id).</param>
/// <param name="OriginName">Its name as people read it.</param>
public sealed record AgentDefinitionFile(string Path, string Origin, string OriginName);

/// <summary>
/// More agent definitions for <see cref="IAgentCatalog"/> (design/plugins/08 §3: user folder &gt; plugins &gt; built-in).
/// A definition from here may add an agent but never replace a built-in or a user's one.
/// </summary>
public interface IAgentDefinitionSource
{
    IReadOnlyList<AgentDefinitionFile> Files { get; }

    /// <summary>The files changed (a plugin was enabled, disabled, installed or removed).</summary>
    event EventHandler? Changed;
}

/// <summary>Finds which agents are installed on this machine. See design/05-智能体设计.md §5.</summary>
public interface IAgentScanner
{
    /// <summary>The last result: from the cache file right after start-up, then from the latest scan. Empty before either.</summary>
    AgentScanResult Current { get; }
    bool IsScanning { get; }
    /// <summary>Raised (on any thread) when <see cref="Current"/> or <see cref="IsScanning"/> changes.</summary>
    event EventHandler? Changed;

    /// <summary>Reads the cached result from disk, if there is one.</summary>
    Task LoadCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>Scans every definition of the catalog, updates <see cref="Current"/> and writes the cache.</summary>
    Task<AgentScanResult> ScanAsync(CancellationToken cancellationToken = default);

    AgentInstallation? Find(string agentId);
}

/// <summary>Inputs of one compilation.</summary>
/// <param name="GlobalPermission">The permission level used when the profile has none.</param>
/// <param name="InstalledPath">The detected executable; null when not detected (the first executable name is used).</param>
/// <param name="Signals">
/// From <see cref="ISignalAttachPlanner"/>: its arguments go right after the base arguments (before anything that
/// could end option parsing, such as a prompt after <c>--</c>); its variables go last, because an overlay variable
/// the profile also sets was already merged with the profile's value by the planner.
/// </param>
public sealed record AgentCompileRequest(
    AgentDefinition Definition,
    AgentProfile Profile,
    AgentLaunchOptions? Launch,
    string GlobalPermission,
    string? InstalledPath,
    SignalAttachment? Signals = null);

/// <summary>
/// Pure function from a definition, the user's profile and one launch's options to a command. No IO.
/// Order: command → base args → signals → model → effort → fast → permission → features → extra args → resume → prompt.
/// </summary>
public interface IAgentCommandCompiler
{
    AgentCommand Compile(AgentCompileRequest request);

    /// <summary>A one-line preview for the settings page, quoted for reading (POSIX style).</summary>
    string Preview(AgentCompileRequest request);

    /// <summary>The permission mode that will really be used: the profile's, else the global one, else the agent's default.</summary>
    string? ResolvePermission(AgentDefinition definition, AgentProfile profile, string globalPermission);

    /// <summary>The effort options offered for a model (applies <c>perModel</c>).</summary>
    IReadOnlyList<AgentOption> EffortOptionsFor(AgentDefinition definition, string? model);
}

/// <summary>Asks an agent CLI for its models, for the agents that declare <c>model.discover</c>.</summary>
public interface IModelDiscovery
{
    /// <summary>The discovered models; empty when the agent has no discovery or the command fails. Cached per agent.</summary>
    Task<IReadOnlyList<AgentOption>> DiscoverAsync(
        AgentDefinition definition,
        string executable,
        bool refresh = false,
        CancellationToken cancellationToken = default);
}
