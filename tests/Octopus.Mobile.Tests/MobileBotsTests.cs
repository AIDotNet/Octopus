using Avalonia.Headless.XUnit;
using Octopus.Mobile.Bots;
using Octopus.Mobile.Hosts;
using Xunit;

namespace Octopus.Mobile.Tests;

public sealed class MobileBotsTests
{
    [AvaloniaFact]
    public async Task BotsTab_AggregatesCrossHostBots_ResolvesApprovals_AndHandlesDeepLinkImport()
    {
        using var phone = new Phone();
        var hostsPage = phone.Top<HostsPage>();
        var hostsVm = Assert.IsType<HostsViewModel>(hostsPage.DataContext);

        // Switch to the dedicated Bots tab (Scheme A 4-tab bar)
        hostsVm.SelectedTab = HostsViewModel.TabBots;
        Assert.True(hostsVm.IsBotsTab);
        Assert.Equal(2, hostsVm.Bots.TotalBotCount);
        Assert.True(hostsVm.Bots.TotalNeedsCount >= 1);
        Assert.Contains("(", hostsVm.BotsTabLabel);

        // 西瓜 card is expanded with pending work item & embedded active chats
        var xiguaCard = hostsVm.Bots.VisibleBots.Single(c => c.Name == "西瓜");
        Assert.True(xiguaCard.IsExpanded);
        Assert.True(xiguaCard.HasPendingWorkItems);
        Assert.Equal(3, xiguaCard.ActiveChats.Count);

        // 1-tap inline approval resolves the pending work item
        var itemVm = xiguaCard.PendingWorkItems[0];
        await itemVm.Options[0].ChooseCommand.ExecuteAsync(null);
        Assert.Equal(0, hostsVm.Bots.TotalNeedsCount);

        // Filter by working bots
        hostsVm.Bots.SelectFilterWorkingCommand.Execute(null);
        Assert.NotEmpty(hostsVm.Bots.VisibleBots);
        hostsVm.Bots.SelectFilterAllCommand.Execute(null);

        // Create a new Bot via sheet command -> navigates to BotDetailPage
        hostsVm.Bots.OpenNewBotCommand.Execute(null);
        hostsVm.Bots.NewBotName = "测试助手";
        await hostsVm.Bots.ConfirmCreateBotCommand.ExecuteAsync(null);
        Assert.Equal(3, hostsVm.Bots.TotalBotCount);
        Assert.IsType<BotDetailPage>(phone.Shell.Top);

        // Deep-link octopus://bot/import opens Bots tab & pre-fills Import Sheet
        phone.Shell.HandleDeepLink(new Uri("octopus://bot/import?data=demo-test"));
        Assert.IsType<HostsPage>(phone.Shell.Top);
        Assert.Equal(HostsViewModel.TabBots, hostsVm.SelectedTab);
        Assert.True(hostsVm.Bots.IsImportOpen);
        Assert.Equal("octopus://bot/import?data=demo-test", hostsVm.Bots.ImportLink);

        await hostsVm.Bots.ConfirmImportBotCommand.ExecuteAsync(null);
        Assert.Equal(4, hostsVm.Bots.TotalBotCount);
        Assert.IsType<BotDetailPage>(phone.Shell.Top);
    }

    [AvaloniaFact]
    public async Task BotDetailPage_FullSessionLifecycle_Connectors_AndSettingsWork()
    {
        using var phone = new Phone();
        var xigua = phone.Workspace.Hosts.SelectMany(h => h.Bots).Single(b => b.Id == "bot-xigua");

        phone.Shell.ShowBot(xigua, BotDetailTab.Sessions);
        var page = phone.Top<BotDetailPage>();
        var vm = Assert.IsType<BotDetailViewModel>(page.DataContext);

        // Initial sessions: 3 active + 1 closed history
        Assert.True(vm.IsSessionsTab);
        Assert.Equal(3, vm.ActiveChats.Count);
        Assert.Single(vm.ClosedChats);

        // Create a new sub-chat -> opens session page
        vm.OpenNewChatCommand.Execute(null);
        vm.NewChatTitle = "排查移动重连";
        await vm.ConfirmCreateChatCommand.ExecuteAsync(null);
        Assert.Equal(4, xigua.Chats.Count(c => c.IsOpen));
        phone.Shell.Back();

        // Rename the created chat
        var created = xigua.Chats.Single(c => c.Title == "排查移动重连");
        vm.ActiveChats.Single(c => c.Chat.Id == created.Id).OpenMenuCommand.Execute(null);
        Assert.True(vm.IsChatMenuOpen);
        Assert.True(vm.MenuChatCanDelete);
        vm.StartRenameMenuChatCommand.Execute(null);
        vm.RenameTitle = "重连修复记录";
        await vm.ConfirmRenameChatCommand.ExecuteAsync(null);
        Assert.Equal("重连修复记录", created.Title);

        // Close chat -> moves from ActiveChats to ClosedChats (preserving history)
        vm.ActiveChats.Single(c => c.Chat.Id == created.Id).OpenMenuCommand.Execute(null);
        await vm.CloseMenuChatCommand.ExecuteAsync(null);
        Assert.False(created.IsOpen);
        Assert.Equal(2, vm.ClosedChats.Count);

        // Reopen from ClosedChats -> moves back to ActiveChats
        await vm.ClosedChats.Single(c => c.Chat.Id == created.Id).ReopenCommand.ExecuteAsync(null);
        Assert.True(created.IsOpen);
        phone.Shell.Back();

        // Delete non-Home chat
        vm.ActiveChats.Single(c => c.Chat.Id == created.Id).OpenMenuCommand.Execute(null);
        await vm.DeleteMenuChatCommand.ExecuteAsync(null);
        Assert.DoesNotContain(xigua.Chats, c => c.Id == created.Id);

        // Connectors tab: toggle Remote MCP switch
        vm.SelectedTab = (int)BotDetailTab.Connectors;
        Assert.True(vm.IsConnectorsTab);
        var gh = vm.Connectors.Single(c => c.Connector.AppId == "github");
        gh.IsEnabled = false;
        await Task.Delay(20);
        Assert.False(gh.Connector.Enabled);

        // Settings tab: toggle look-around & memory maintenance
        vm.SelectedTab = (int)BotDetailTab.Settings;
        Assert.True(vm.IsSettingsTab);
        vm.LookAroundEnabled = false;
        vm.TidyUpMemoryEnabled = false;
        await Task.Delay(20);
        Assert.False(xigua.LookAroundEnabled);
        Assert.False(xigua.TidyUpMemoryEnabled);
    }
}
