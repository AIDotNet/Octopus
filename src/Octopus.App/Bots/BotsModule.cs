using Microsoft.Extensions.DependencyInjection;

namespace Octopus.App.Bots;

/// <summary>Registers Bot services and view models (left Bot tree panel, right Bot panel, home manager).</summary>
internal static class BotsModule
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<BotHomeManager>();
        services.AddSingleton<BotConnectorRuntime>();
        services.AddSingleton<IBotService, BotService>();
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
