using Octopus.Agents;
using Octopus.Agents.Accounts;
using Octopus.App.Services;
using Octopus.Platform;
using Octopus.Storage;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;

namespace Octopus.App.Settings.Agents;

/// <summary>What an agent row needs from the layers below, passed as one object.</summary>
public sealed record AgentServices(
    ISettingsStore Settings,
    IAgentCommandCompiler Compiler,
    IModelDiscovery Discovery,
    IFileOpener Opener,
    IPathPicker PathPicker,
    IAgentAccountService? Accounts = null,
    IDialogService? Dialogs = null,
    ITerminalLaunchPlanner? LaunchPlanner = null,
    IProjectService? Projects = null,
    TerminalSessionRegistry? Sessions = null);
