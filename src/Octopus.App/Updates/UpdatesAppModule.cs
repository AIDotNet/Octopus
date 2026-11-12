using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Octopus.Core.Updates;
using Octopus.Updates;

namespace Octopus.App.Updates;

/// <summary>
/// Registers the updater (design/10-自动更新.md) with the version of this build, its card and its settings section.
/// The platform's installer comes from the platform registration.
/// </summary>
internal static class UpdatesAppModule
{
    public static void Register(IServiceCollection services)
    {
        var version = typeof(UpdatesAppModule).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string? feed = null;
        string[]? keys = null;
        var development = false;
#if DEBUG
        // A debug build checks nothing, unless pointed at a test feed: OCTOPUS_UPDATE_FEED (a GitHub-shaped release
        // list), OCTOPUS_UPDATE_PUBKEY (the key that signed it) and OCTOPUS_UPDATE_VERSION (the version to pretend to be).
        // A release build reads none of these.
        feed = Environment.GetEnvironmentVariable("OCTOPUS_UPDATE_FEED") is { Length: > 0 } f ? f : null;
        keys = Environment.GetEnvironmentVariable("OCTOPUS_UPDATE_PUBKEY") is { Length: > 0 } k ? [k] : null;
        version = Environment.GetEnvironmentVariable("OCTOPUS_UPDATE_VERSION") is { Length: > 0 } v ? v : version;
        development = feed is null;
#endif
        var options = new UpdateOptions
        {
            CurrentVersion = AppVersion.TryParse(version, out var current) ? current : null,
            IsDevelopment = development,
        };
        services.AddOctopusUpdates(options, feed, keys);
        services.AddSingleton<UpdateCardViewModel>();
        services.AddTransient<UpdateSettingsViewModel>();
    }
}
