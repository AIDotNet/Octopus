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
        services.AddSingleton<Octopus.App.Files.Lsp.ILanguageServerManager, Octopus.App.Files.Lsp.LanguageServerManager>();
        services.AddSingleton<Octopus.App.Services.Workspaces.IGitHubTaskProvider, Octopus.App.Services.Workspaces.GhCliTaskProvider>();
        services.AddSingleton<Octopus.App.Services.Workspaces.IWorkspaceManager, Octopus.App.Services.Workspaces.WorkspaceManager>();
        services.AddSingleton<Octopus.App.Services.Workspaces.IWorkspaceCleanupService, Octopus.App.Services.Workspaces.WorkspaceCleanupService>();
        // Terminal sessions by item id: they live apart from the tabs that show them (design/12-远程操作.md §4.1).
        services.AddSingleton<Octopus.Terminal.Avalonia.TerminalSessionRegistry>();
        services.AddSingleton<ITabContentRegistry, TabContentRegistry>();
        // Remote operation, host side: this computer's workspace for paired devices, and the service that lets them in.
        services.AddSingleton<Octopus.App.Remote.RemoteIdentityProvider>();
        services.AddSingleton<Octopus.App.Remote.HostWorkspace>();
        // Device side: the hosts this computer is paired with and the connections to them.
        services.AddSingleton<Octopus.App.Remote.RelayAccess>();
        services.AddSingleton<Octopus.App.Remote.RemoteClientRuntime>();
        services.AddSingleton<Octopus.App.Remote.RemoteProjects>();
        services.AddSingleton<Octopus.App.Remote.RemoteProjectAccess>();
        services.AddSingleton<Octopus.App.Remote.RemoteHostRuntime>();
        services.AddSingleton<CloseConfirmation>();

        // View models
        services.AddSingleton<EmptyStateViewModel>();
        services.AddSingleton<WorkspaceViewModel>();
        services.AddSingleton<IWorkspaceCommands>(p => p.GetRequiredService<WorkspaceViewModel>());
        services.AddSingleton<DeviceSwitcherViewModel>();
        services.AddSingleton<Account.AccountViewModel>();
        services.AddSingleton<ProjectRailViewModel>();
        services.AddSingleton<ProjectPanelViewModel>();
        services.AddSingleton<LauncherViewModel>();
        services.AddSingleton<Octopus.App.StarPrompt.StarPromptOptions>();
        services.AddSingleton<Octopus.App.StarPrompt.StarPromptViewModel>();
        services.AddSingleton<ShellViewModel>();
    }
}
