using Microsoft.Extensions.DependencyInjection;

namespace Octopus.FileSystem;

public static class FileSystemServiceCollectionExtensions
{
    /// <summary>Registers the file tree, search and access services as singletons.</summary>
    public static IServiceCollection AddOctopusFileSystem(this IServiceCollection services)
    {
        services.AddSingleton<IFileTreeService, FileTreeService>();
        services.AddSingleton<IFileSearchService, FileSearchService>();
        services.AddSingleton<IFileAccessService>(_ => new FileAccessService());
        return services;
    }
}
