using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace Octopus.Updates;

/// <summary>Registration of the updater. The platform's <see cref="Octopus.Platform.Updates.IUpdateInstaller"/> comes from the platform registration.</summary>
public static class UpdatesServiceCollectionExtensions
{
    public static IServiceCollection AddOctopusUpdates(
        this IServiceCollection services,
        UpdateOptions options,
        string? releasesUrl = null,
        IReadOnlyList<string>? publicKeys = null)
    {
        services.AddSingleton(options);
        services.AddKeyedSingleton(UpdateHttp.Key, (_, _) => UpdateHttp.Create());
        services.AddSingleton(_ => new ManifestVerifier(publicKeys ?? UpdateEndpoints.PublicKeys));
        services.AddSingleton<IUpdateFeed>(p => new GitHubReleasesFeed(
            p.GetRequiredKeyedService<HttpClient>(UpdateHttp.Key),
            p.GetRequiredService<ManifestVerifier>(),
            releasesUrl ?? UpdateEndpoints.ReleasesUrl));
        services.AddSingleton(p => new UpdateDownloader(p.GetRequiredKeyedService<HttpClient>(UpdateHttp.Key)));
        services.AddSingleton<UpdateService>();
        services.AddSingleton<IUpdateService>(p => p.GetRequiredService<UpdateService>());
        return services;
    }
}

/// <summary>The HTTP client of the updater: the system proxy, no overall timeout (downloads are long; reads have their own).</summary>
internal static class UpdateHttp
{
    public const string Key = "octopus-updates";

    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        // GitHub's API requires a User-Agent; it names the program only.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Octopus-Updater");
        return client;
    }
}
