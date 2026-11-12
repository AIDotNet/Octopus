using Microsoft.Extensions.DependencyInjection;
using Octopus.Platform.Unix;

namespace Octopus.Platform.Linux;

public static class LinuxPlatformServiceCollectionExtensions
{
    /// <summary>Registers every platform service for Linux as a singleton.</summary>
    public static IServiceCollection AddLinuxPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IAppPaths>(_ => new LinuxAppPaths());
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IShellCommandBuilder, ShellCommandBuilder>();
        services.AddSingleton<IExecutableLocator, UnixExecutableLocator>();
        services.AddSingleton<IShellResolver, LinuxShellResolver>();
        services.AddSingleton<ILoginEnvironment, UnixLoginEnvironment>();
        services.AddSingleton<ITerminalEnvironment>(_ => new LinuxTerminalEnvironment());
        services.AddSingleton<IFileOpener, LinuxFileOpener>();
        services.AddSingleton<IAppIcon, NoAppIcon>();
        services.AddSingleton<ISecretStore>(sp => new LinuxSecretStore(sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<IExecutableLocator>()));
        services.AddSingleton<ITrashService>(_ => new LinuxTrashService());
        services.AddSingleton<Updates.IUpdateInstaller>(_ => new LinuxUpdateInstaller());
        return services;
    }
}
