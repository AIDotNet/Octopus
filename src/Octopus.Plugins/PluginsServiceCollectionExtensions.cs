using Microsoft.Extensions.DependencyInjection;
using Octopus.Plugins.Catalog;
using Octopus.Plugins.Packages;
using Octopus.Plugins.Security;
using Octopus.Storage;

namespace Octopus.Plugins;

/// <summary>Registration of the plugin services.</summary>
public static class PluginsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the plugin catalog, installer and state store as singletons. Needs an <c>IAppPaths</c>. The app supplies
    /// its version and where built-in and development plugins are.
    /// </summary>
    public static IServiceCollection AddOctopusPlugins(this IServiceCollection services, SemanticVersion appVersion, PluginCatalogOptions? options = null)
    {
        services.AddSingleton<PluginPaths>();
        services.AddSingleton<IPluginStateStore, JsonPluginStateStore>();
        services.AddSingleton(PluginHostInfo.Current(appVersion));
        services.AddSingleton(options ?? new PluginCatalogOptions());
        services.AddSingleton<IPluginCatalog, PluginCatalog>();
        services.AddSingleton<PluginInstaller>();
        services.AddSingleton<IPluginAccessPolicy, PluginAccessPolicy>();
        services.AddSingleton(UserHome.Current);
        services.AddSingleton<Runtime.UserCommandRunner>();
        return services;
    }
}
