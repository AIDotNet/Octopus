using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Octopus.Terminal;

public static class TerminalServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ITerminalLaunchPlanner"/>. Needs the platform services to be registered as well.</summary>
    public static IServiceCollection AddOctopusTerminal(this IServiceCollection services)
    {
        services.TryAddSingleton<ITerminalLaunchPlanner, TerminalLaunchPlanner>();
        return services;
    }
}
