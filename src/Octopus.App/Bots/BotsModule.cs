using Microsoft.Extensions.DependencyInjection;

namespace Octopus.App.Bots;

/// <summary>Registers Bot services and view models (left Bot tree panel, right Bot panel, home manager).</summary>
internal static class BotsModule
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<BotHomeManager>();
        services.AddSingleton<BotConnectorRuntime>();
        services.AddSingleton<IBotService>(sp => new BotService(
            sp.GetRequiredService<Octopus.Storage.IBotStore>(),
            sp.GetRequiredService<Octopus.Agents.IAgentCatalog>(),
            sp.GetRequiredService<Octopus.Agents.IAgentScanner>(),
            sp.GetRequiredService<Octopus.Storage.ISettingsStore>(),
            sp.GetRequiredService<Octopus.App.Common.PathComparer>(),
            sp.GetRequiredService<BotHomeManager>(),
            sp.GetService<Octopus.App.Activity.IItemActivityService>(),
            sp.GetService<BotConnectorRuntime>(),
            () => sp.GetService<Octopus.App.Workspace.ITabContentRegistry>()));
        services.AddSingleton<Groups.IBotGroupService>(sp => new Groups.BotGroupService(
            sp.GetRequiredService<Octopus.Storage.IBotGroupStore>(),
            sp.GetRequiredService<IBotService>(),
            () => sp.GetService<Octopus.App.Workspace.ITabContentRegistry>()));
        services.AddSingleton(sp => new BotScheduler(
            sp.GetRequiredService<IBotService>(),
            () => sp.GetService<Octopus.App.Workspace.ITabContentRegistry>()));
        services.AddSingleton(sp => new BotScheduleMcpServer(
            sp.GetRequiredService<IBotService>(),
            sp.GetRequiredService<BotScheduler>()));
        services.AddSingleton<BotPanelViewModel>();
        services.AddSingleton<BotRightPanelViewModel>();
        services.AddSingleton<Groups.BotGroupRoomViewModel>();
        services.AddSingleton<Groups.BotGroupRightPanelViewModel>();
    }
}
