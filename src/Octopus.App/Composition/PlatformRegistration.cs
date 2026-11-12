using Microsoft.Extensions.DependencyInjection;
using Octopus.App.Common;
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
        if (OperatingSystem.IsMacOS())
        {
            return services.AddMacOSPlatform();
        }
        return OperatingSystem.IsWindows() ? services.AddWindowsPlatform() : services.AddLinuxPlatform();
    }
}
