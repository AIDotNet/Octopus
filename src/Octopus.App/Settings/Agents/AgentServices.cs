using Octopus.Agents;
using Octopus.Platform;
using Octopus.Storage;

namespace Octopus.App.Settings.Agents;

/// <summary>What an agent row needs from the layers below, passed as one object.</summary>
public sealed record AgentServices(
    ISettingsStore Settings,
    IAgentCommandCompiler Compiler,
    IModelDiscovery Discovery,
    IFileOpener Opener,
    IPathPicker PathPicker);
