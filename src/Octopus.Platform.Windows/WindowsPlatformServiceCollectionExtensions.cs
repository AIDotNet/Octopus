using Microsoft.Extensions.DependencyInjection;

namespace Octopus.Platform.Windows;

public static class WindowsPlatformServiceCollectionExtensions
{
    /// <summary>Registers every platform service for Windows as a singleton.</summary>
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IAppPaths, WindowsAppPaths>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IShellCommandBuilder, ShellCommandBuilder>();
        services.AddSingleton<IExecutableLocator>(_ => new WindowsExecutableLocator());
        services.AddSingleton<IShellResolver, WindowsShellResolver>();
        services.AddSingleton<ILoginEnvironment, WindowsLoginEnvironment>();
        services.AddSingleton<ITerminalEnvironment>(_ => new WindowsTerminalEnvironment());
        services.AddSingleton<IFileOpener, WindowsFileOpener>();
        services.AddSingleton<IAppIcon, NoAppIcon>();
        return services;
    }
}
