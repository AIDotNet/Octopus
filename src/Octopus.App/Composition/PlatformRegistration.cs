using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Octopus.App.Common;
using Octopus.Platform;
using Octopus.Platform.Linux;
using Octopus.Platform.MacOS;
using Octopus.Platform.Windows;

namespace Octopus.App.Composition;

internal static class PlatformRegistration
{
    /// <summary>
    /// Picks the platform implementation. This is the one place in the app that asks which operating system it runs
    /// on: everything else takes the interfaces of Octopus.Platform and the services registered here.
    /// </summary>
    public static IServiceCollection AddCurrentPlatform(this IServiceCollection services)
    {
        // The file systems of macOS and Windows ignore case by default, the ones of Linux do not.
        services.AddSingleton(new PathComparer(ignoreCase: OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()));
        if (OperatingSystem.IsMacOS()) services.AddMacOSPlatform();
        else if (OperatingSystem.IsWindows()) services.AddWindowsPlatform();
        else services.AddLinuxPlatform();
        // OCTOPUS_DATA_DIR: projects, settings and cache in a folder of its own, so a second instance can run beside
        // the person's own without sharing its files.
        if (FixedAppPaths.FromEnvironment() is { } paths)
        {
            services.Replace(ServiceDescriptor.Singleton<IAppPaths>(paths));
            // An isolated instance must not overwrite the primary install's OS login item.
            services.Replace(ServiceDescriptor.Singleton<IAutoStartService, NoAutoStartService>());
        }
#if DEBUG
        // Development runs (`dotnet run`) must not register bin/Debug apphosts into OS login items unless opted in.
        if (!string.Equals(Environment.GetEnvironmentVariable("OCTOPUS_ALLOW_DEBUG_AUTOSTART"), "1", StringComparison.Ordinal))
        {
            services.Replace(ServiceDescriptor.Singleton<IAutoStartService, NoAutoStartService>());
        }
#endif
        return services;
    }
}
