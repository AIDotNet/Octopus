using Microsoft.Extensions.DependencyInjection;
using Octopus.App.Explorer.Files;
using Octopus.App.Explorer.Git;
using Octopus.App.Explorer.Search;
using Octopus.FileSystem;
using Octopus.Git;

namespace Octopus.App.Explorer;

/// <summary>Registers the services and view models of the right panel (files and source control).</summary>
internal static class ExplorerModule
{
    public static void Register(IServiceCollection services)
    {
        services.AddOctopusFileSystem();
        services.AddOctopusGit();
        services.AddTransient<FileTreeViewModel>();
        services.AddTransient<FileSearchViewModel>();
        services.AddTransient<GitPanelViewModel>();
        services.AddTransient<ExplorerPanelViewModel>();
    }
}
