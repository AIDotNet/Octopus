using Microsoft.Extensions.DependencyInjection;
using Octopus.App.Appearance;
using Octopus.App.Explorer;
using Octopus.App.Plugins;
using Octopus.App.Settings;
using Octopus.App.Shell;
using Octopus.App.Updates;

namespace Octopus.App.Composition;

internal static class AppRegistration
{
    /// <summary>The app's own services and view models, one module per feature area.</summary>
    public static IServiceCollection AddOctopusApp(this IServiceCollection services)
    {
        services.AddSingleton<ThemeService>();
        ShellModule.Register(services);       // shell, sidebar, workspace, launcher, terminals, files, dialogs
        SettingsModule.Register(services);    // settings pages
        ExplorerModule.Register(services);    // right panel: files and source control
        PluginsAppModule.Register(services);  // plugins: catalog, runtime, host modules, views
        UpdatesAppModule.Register(services);  // automatic updates: the updater, its card and settings
        return services;
    }
}
