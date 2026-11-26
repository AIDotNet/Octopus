using Microsoft.Extensions.DependencyInjection;

namespace Octopus.Storage;

/// <summary>Registration of the storage services.</summary>
public static class StorageServiceCollectionExtensions
{
    /// <summary>Registers the file-backed <see cref="IProjectStore"/> and <see cref="ISettingsStore"/> as singletons. Needs an <c>IAppPaths</c>.</summary>
    public static IServiceCollection AddOctopusStorage(this IServiceCollection services)
    {
        services.AddSingleton<IProjectStore, JsonProjectStore>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IAccountStore, JsonAccountStore>();
        services.AddSingleton<IBotStore, JsonBotStore>();
        services.AddSingleton<IBotGroupStore, JsonBotGroupStore>();
        return services;
    }
}
