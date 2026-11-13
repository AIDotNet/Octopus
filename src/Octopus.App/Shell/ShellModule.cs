using Microsoft.Extensions.DependencyInjection;
using Octopus.App.Dialogs;
using Octopus.App.Launcher;
using Octopus.App.Services;
using Octopus.App.Shell.Chrome;
using Octopus.App.Shell.Keymap;
using Octopus.App.Sidebar;
using Octopus.App.Workspace;

namespace Octopus.App.Shell;

/// <summary>Registers the services and view models of the main window: shell, sidebar, workspace, launcher, tabs, dialogs.</summary>
internal static class ShellModule
{
    public static void Register(IServiceCollection services)
    {
        // Platform differences
        services.AddSingleton(_ => WindowChromeFactory.Create());
        services.AddSingleton(_ => AppKeymapFactory.Create());

        // Services
        services.AddSingleton<SettingsNavigator>();
        services.AddSingleton<ISettingsNavigator>(p => p.GetRequiredService<SettingsNavigator>());
        services.AddSingleton<IWindowAccessor, WindowAccessor>();
        services.AddSingleton<TrayService>();
        services.AddSingleton<Activity.AgentAttentionNotifier>();
        services.AddSingleton<IPickerService, PickerService>();
        services.AddSingleton<IProjectIconStore, ProjectIconStore>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IProjectService, ProjectService>();
        services.AddSingleton<IItemLaunchService, ItemLaunchService>();
        services.AddSingleton<Octopus.App.Activity.IItemActivityService, Octopus.App.Activity.ItemActivityService>();
        services.AddSingleton<Octopus.App.Activity.ISubagentService, Octopus.App.Activity.SubagentService>();
        services.AddSingleton<IItemOpener, ItemOpener>();
        services.AddSingleton<ITabContentRegistry, TabContentRegistry>();
        services.AddSingleton<CloseConfirmation>();

        // View models
        services.AddSingleton<EmptyStateViewModel>();
        services.AddSingleton<WorkspaceViewModel>();
        services.AddSingleton<IWorkspaceCommands>(p => p.GetRequiredService<WorkspaceViewModel>());
        services.AddSingleton<ProjectRailViewModel>();
        services.AddSingleton<ProjectPanelViewModel>();
        services.AddSingleton<LauncherViewModel>();
        services.AddSingleton<ShellViewModel>();
    }
}
