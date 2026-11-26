using Microsoft.Extensions.DependencyInjection;
using Octopus.Agents;
using Octopus.Agents.Signals;
using Octopus.App.Activity;
using Octopus.App.Services;
using Octopus.App.Services.Workspaces;
using Octopus.App.Workspace;
using Octopus.Storage;
using Octopus.Terminal.Avalonia;

namespace Octopus.App.Collab;

internal static class CollabModule
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<CollabMcpServer>();
        services.AddSingleton<CollabCliMcpInjector>();
        services.AddSingleton<CollabStateStore>();
        services.AddSingleton<ICollabPolicy, DefaultCollabPolicy>();
        // The MCP tools agents see. Add a tool to CollabToolRegistry.BuiltIn, or register more ICollabTool services here.
        foreach (var tool in Tools.CollabToolRegistry.BuiltIn) services.AddSingleton<Tools.ICollabTool>(tool);
        services.AddSingleton<ICollabRemoteHosts>(sp => new RuntimeCollabRemoteHosts(
            sp.GetRequiredService<Octopus.App.Remote.RemoteClientRuntime>(),
            sp.GetService<Octopus.App.Remote.RemoteProjects>()));
        services.AddSingleton<CollabCoordinator>(sp => new CollabCoordinator(
            sp.GetRequiredService<IProjectService>(),
            sp.GetRequiredService<IAgentCatalog>(),
            sp.GetRequiredService<IAgentScanner>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<CollabMcpServer>(),
            sp.GetService<CollabCliMcpInjector>(),
            sp.GetService<IItemActivityService>(),
            sp.GetService<ISignalHub>(),
            sp.GetService<TerminalSessionRegistry>(),
            () => sp.GetService<ITabContentRegistry>(),
            () => sp.GetService<IWorkspaceManager>(),
            sp.GetService<CollabStateStore>(),
            () => sp.GetService<ICollabRemoteHosts>(),
            sp.GetServices<Tools.ICollabTool>(),
            sp.GetService<ICollabPolicy>()));
        services.AddSingleton<ICollabCoordinator>(sp => sp.GetRequiredService<CollabCoordinator>());
    }
}
