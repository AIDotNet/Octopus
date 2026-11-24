using Microsoft.Extensions.DependencyInjection;
using Octopus.App.Settings.About;
using Octopus.App.Settings.Agents;
using Octopus.App.Settings.Appearance;
using Octopus.App.Settings.General;
using Octopus.App.Settings.Shortcuts;
using Octopus.App.Settings.Ssh;
using Octopus.App.Settings.Terminal;
using Octopus.App.Settings.Workspaces;

namespace Octopus.App.Settings;

/// <summary>Registers the view models of the settings pages.</summary>
internal static class SettingsModule
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IPathPicker, StoragePathPicker>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<AgentsSettingsViewModel>();
        services.AddTransient<GeneralSettingsViewModel>();
        services.AddTransient<WorkspaceCleanupSettingsViewModel>();
        services.AddTransient<TerminalSettingsViewModel>();
        services.AddTransient<SshSettingsViewModel>();
        services.AddTransient<Remote.RemoteAccessSettingsViewModel>();
        services.AddTransient<Remote.RemoteHostsSettingsViewModel>();
        services.AddTransient<AppearanceSettingsViewModel>();
        services.AddTransient<ShortcutsViewModel>();
        services.AddTransient<AboutSettingsViewModel>();
    }
}
