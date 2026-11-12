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
        return services;
    }
}
