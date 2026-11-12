using Microsoft.Extensions.DependencyInjection;

namespace Octopus.Agents;

/// <summary>Registers the agent layer.</summary>
public static class AgentsServiceCollectionExtensions
{
    /// <summary>
    /// Adds the catalog, scanner, compiler and model discovery as singletons. Expects the Platform services
    /// (<c>IAppPaths</c>, <c>IExecutableLocator</c>, <c>IProcessRunner</c>, <c>ILoginEnvironment</c>,
    /// <c>IShellCommandBuilder</c>) to be registered by the caller.
    /// </summary>
    public static IServiceCollection AddOctopusAgents(this IServiceCollection services)
    {
        services.AddSingleton<IAgentCatalog, AgentCatalog>();
        services.AddSingleton<IAgentScanner, AgentScanner>();
        services.AddSingleton<IAgentCommandCompiler, AgentCommandCompiler>();
        services.AddSingleton<IModelDiscovery, ModelDiscovery>();
        return services;
    }
}
