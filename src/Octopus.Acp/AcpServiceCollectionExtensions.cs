using Microsoft.Extensions.DependencyInjection;
using Octopus.Acp.Dialects;
using Octopus.Acp.Process;

namespace Octopus.Acp;

public static class AcpServiceCollectionExtensions
{
    public static IServiceCollection AddOctopusAcp(this IServiceCollection services)
    {
        services.AddSingleton<AcpDialectRegistry>();
        services.AddSingleton<AcpRegistryClient>();
        services.AddSingleton<AcpAdapterInstaller>();
        services.AddSingleton<AcpLaunchResolver>();
        services.AddSingleton<IAcpSessionFactory, AcpProcessSessionFactory>();
        return services;
    }
}
