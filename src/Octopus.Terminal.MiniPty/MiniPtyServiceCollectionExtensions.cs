using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Octopus.Terminal.MiniPty;

public static class MiniPtyServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ITerminalConnectionFactory"/> backed by MiniPty.</summary>
    public static IServiceCollection AddMiniPtyTerminal(this IServiceCollection services)
    {
        services.TryAddSingleton<ITerminalConnectionFactory, MiniPtyConnectionFactory>();
        return services;
    }
}
