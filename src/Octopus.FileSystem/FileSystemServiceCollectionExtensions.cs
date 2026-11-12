using Microsoft.Extensions.DependencyInjection;

namespace Octopus.FileSystem;

public static class FileSystemServiceCollectionExtensions
{
    /// <summary>Registers the file tree and search services as singletons.</summary>
    public static IServiceCollection AddOctopusFileSystem(this IServiceCollection services)
    {
        services.AddSingleton<IFileTreeService, FileTreeService>();
        services.AddSingleton<IFileSearchService, FileSearchService>();
        return services;
    }
}
