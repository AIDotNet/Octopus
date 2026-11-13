using Microsoft.Extensions.DependencyInjection;
using Octopus.App.Explorer.Files;
using Octopus.App.Explorer.Git;
using Octopus.App.Explorer.Search;
using Octopus.App.Explorer.Sessions;
using Octopus.App.Explorer.Tunnels;
using Octopus.App.Services;
using Octopus.Platform;
using Octopus.FileSystem;
using Octopus.Git;

namespace Octopus.App.Explorer;

/// <summary>Registers the services and view models of the right panel (files, source control and tunnels).</summary>
internal static class ExplorerModule
{
    public static void Register(IServiceCollection services)
    {
        services.AddOctopusFileSystem();
        services.AddOctopusGit();
        services.AddTransient<FileTreeViewModel>();
        services.AddTransient<FileSearchViewModel>();
        services.AddTransient<GitPanelViewModel>();
        // Tunnels of projects on SSH hosts (design/11-SSH隧道.md): one service for the app, so that what runs outlives the panel.
        services.AddSingleton<IProcessHost, ProcessHost>();
        services.AddSingleton<ILocalPorts, LocalPorts>();
        services.AddSingleton<ISharedConnection>(sp => sp.GetRequiredService<RemoteFiles>());
        services.AddSingleton<ITunnelService>(sp => new TunnelService(
            sp.GetRequiredService<IProcessHost>(),
            sp.GetRequiredService<ILocalPorts>(),
            sp.GetRequiredService<IProjectService>(),
            Path.Combine(sp.GetRequiredService<IAppPaths>().CacheDirectory, "tunnel-processes"),
            runner: sp.GetRequiredService<IProcessRunner>(),
            shared: sp.GetRequiredService<ISharedConnection>()));
        services.AddTransient<TunnelsPanelViewModel>();
        services.AddSingleton<ISessionMigrationDialog, SessionMigrationDialog>();
        services.AddTransient<SessionsPanelViewModel>();
        services.AddTransient<ExplorerPanelViewModel>();
    }
}
