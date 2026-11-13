using Microsoft.Extensions.DependencyInjection;
using Octopus.Agents.Probes;
using Octopus.Agents.Sessions;
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
        // Session management (design/ui/会话管理方案.html): one reader per agent whose files are understood, and the converters.
        services.AddSingleton<ISessionSource, ClaudeSessionSource>();
        services.AddSingleton<ISessionSource, CodexSessionSource>();
        services.AddSingleton<ISessionConverter, ClaudeAccountCopyConverter>(_ => new ClaudeAccountCopyConverter());
        services.AddSingleton<ISessionConverter, HandoffSessionConverter>(_ => new HandoffSessionConverter());
        services.AddSingleton<IAgentSessionService>(sp => new AgentSessionService(
            sp.GetRequiredService<IAgentAccountDiscovery>(),
            sp.GetServices<ISessionSource>(),
            sp.GetServices<ISessionConverter>()));
        return services;
    }
}
