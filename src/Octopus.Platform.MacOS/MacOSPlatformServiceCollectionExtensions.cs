using Microsoft.Extensions.DependencyInjection;
using Octopus.Platform.Unix;

namespace Octopus.Platform.MacOS;

public static class MacOSPlatformServiceCollectionExtensions
{
    /// <summary>Registers every platform service for macOS as a singleton.</summary>
    public static IServiceCollection AddMacOSPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IAppPaths>(_ => new MacAppPaths());
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IShellCommandBuilder, ShellCommandBuilder>();
        services.AddSingleton<IExecutableLocator, UnixExecutableLocator>();
        services.AddSingleton<IShellResolver, MacShellResolver>();
        services.AddSingleton<ILoginEnvironment, UnixLoginEnvironment>();
        services.AddSingleton<ITerminalEnvironment>(sp => new MacTerminalEnvironment(sp.GetRequiredService<IProcessRunner>()));
        services.AddSingleton<IFileOpener, MacFileOpener>();
        services.AddSingleton<IAppIcon, MacAppIcon>();
        services.AddSingleton<ISecretStore>(_ => new MacSecretStore());
        services.AddSingleton<ITrashService>(_ => new MacTrashService());
        services.AddSingleton<Updates.IUpdateInstaller>(sp => new MacUpdateInstaller(sp.GetRequiredService<IProcessRunner>()));
        return services;
    }
}
