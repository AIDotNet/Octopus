using Microsoft.Extensions.DependencyInjection;
using Octopus.Agents;
using Octopus.Ssh;
using Octopus.Storage;
using Octopus.Terminal;
using Octopus.Terminal.MiniPty;

namespace Octopus.App.Composition;

/// <summary>
/// The composition root: every layer is registered here and nowhere else. Built before Avalonia starts, because the
/// theme and language of the first frame come from the saved settings.
/// </summary>
public static class AppHost
{
    private static IServiceProvider? s_services;

    /// <summary>Lets a test host replace services (in-memory stores, a temporary data folder) before the first build.</summary>
    public static Action<IServiceCollection>? ConfigureOverrides { get; set; }

    public static IServiceProvider Services => s_services ??= Build();

    public static T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>
    /// Disposes the container and drops it, so the next use builds a fresh one (tests, and the end of the app).
    /// Safe to call more than once.
    /// </summary>
    public static void Reset() => (Interlocked.Exchange(ref s_services, null) as IDisposable)?.Dispose();

    private static IServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddCurrentPlatform();     // shell, environment, paths: the only OS switch in the app
        services.AddOctopusStorage();      // projects.json, settings.json
        services.AddOctopusAgents();       // catalog, scanner, command compiler
        services.AddOctopusTerminal();     // launch planner
        services.AddSingleton<ISshHostCatalog>(_ => new SshHostCatalog(SshHostCatalog.UserHome)); // hosts of ~/.ssh/config
        services.AddSingleton<ISshHostTester, SshHostTester>(); // the test button of the host form
        services.AddSingleton<Octopus.App.Services.RemoteFiles>(); // folders and copies of files on SSH hosts
        services.AddMiniPtyTerminal();     // pty
        services.AddOctopusApp();          // app services and view models
        ConfigureOverrides?.Invoke(services);
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISettingsStore>().Load();
        return provider;
    }
}
