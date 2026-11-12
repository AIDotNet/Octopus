using Microsoft.Extensions.DependencyInjection;

namespace Octopus.Git;

public static class GitServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IGitService"/>. Needs the platform's process runner, executable locator and login environment.</summary>
    public static IServiceCollection AddOctopusGit(this IServiceCollection services)
    {
        services.AddSingleton<IGitService, GitService>();
        return services;
    }
}
