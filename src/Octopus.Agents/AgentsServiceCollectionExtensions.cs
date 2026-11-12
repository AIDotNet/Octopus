using Microsoft.Extensions.DependencyInjection;
using Octopus.Agents.Probes;
using Octopus.Agents.Signals;

namespace Octopus.Agents;

/// <summary>Registers the agent layer.</summary>
public static class AgentsServiceCollectionExtensions
{
    /// <summary>
    /// Adds the catalog, scanner, compiler, model discovery, signal hub, signal attach planner and probe runner as
    /// singletons. Expects the Platform services (<c>IAppPaths</c>, <c>IExecutableLocator</c>, <c>IProcessRunner</c>,
    /// <c>ILoginEnvironment</c>, <c>IShellCommandBuilder</c>) to be registered by the caller. The hub does nothing
    /// until <see cref="ISignalHub.Start"/> is called.
    /// </summary>
    public static IServiceCollection AddOctopusAgents(this IServiceCollection services)
    {
        services.AddSingleton<IAgentCatalog, AgentCatalog>();
        services.AddSingleton<IAgentDefinitionStore, AgentDefinitionStore>();
        services.AddSingleton<IAgentScanner, AgentScanner>();
        services.AddSingleton<IAgentCommandCompiler, AgentCommandCompiler>();
        services.AddSingleton<IModelDiscovery, ModelDiscovery>();
        services.AddSingleton<ISignalHub, SignalHub>();
        services.AddSingleton<ISignalAttachPlanner, SignalAttachPlanner>();
        services.AddSingleton<IProbeRunner, ProbeRunner>();
        services.AddSingleton<IAgentAccountDiscovery, AgentAccountDiscovery>();
        services.AddSingleton<ClaudeStatusLineBridge>();
        services.AddSingleton<ClaudeStatusLineWatcher>();
        return services;
    }
}
