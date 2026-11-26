using Avalonia.Headless.XUnit;
using Octopus.Mobile.Bots;
using Octopus.Mobile.Hosts;
using Xunit;

namespace Octopus.Mobile.Tests;

public sealed class MobileBotGroupsTests
{
    [AvaloniaFact]
    public async Task BotsTab_ListsAndCreatesBotGroups_AndNavigatesToDetailPage()
    {
        using var phone = new Phone();
        var hostsPage = phone.Top<HostsPage>();
        var hostsVm = Assert.IsType<HostsViewModel>(hostsPage.DataContext);

        hostsVm.SelectedTab = HostsViewModel.TabBots;
        Assert.True(hostsVm.IsBotsTab);

        // Seeded demo group appears in Groups section above single Bots
        Assert.Equal(1, hostsVm.Bots.TotalGroupCount);
        Assert.True(hostsVm.Bots.HasGroups);
        Assert.True(hostsVm.Bots.HasVisibleGroups);
        var seededCard = Assert.Single(hostsVm.Bots.VisibleGroups);
        Assert.Equal("发布协作组", seededCard.Name);
        Assert.True(seededCard.HasPendingApprovals);
        Assert.Equal(1, seededCard.PendingApprovalCount);
        Assert.Equal(2, seededCard.MemberAvatars.Count);

        // Search filters groups
        hostsVm.Bots.SearchQuery = "发布";
        Assert.Single(hostsVm.Bots.VisibleGroups);
        hostsVm.Bots.SearchQuery = "不存在的群组";
        Assert.Empty(hostsVm.Bots.VisibleGroups);
        hostsVm.Bots.SearchQuery = "";
        Assert.Single(hostsVm.Bots.VisibleGroups);

        // Open New Group sheet, fill name, choose All reply mode, and create -> navigates to BotGroupDetailPage
        hostsVm.Bots.OpenNewGroupCommand.Execute(null);
        Assert.True(hostsVm.Bots.IsNewGroupOpen);
        Assert.NotEmpty(hostsVm.Bots.NewGroupMemberChoices);
        hostsVm.Bots.NewGroupName = "架构评审组";
        hostsVm.Bots.SelectedGroupReplyModeIndex = 1; // All
        await hostsVm.Bots.ConfirmCreateGroupCommand.ExecuteAsync(null);

        Assert.False(hostsVm.Bots.IsNewGroupOpen);
        Assert.Equal(2, hostsVm.Bots.TotalGroupCount);
        var detailPage = Assert.IsType<BotGroupDetailPage>(phone.Shell.Top);
        var detailVm = Assert.IsType<BotGroupDetailViewModel>(detailPage.DataContext);
        Assert.Equal("架构评审组", detailVm.GroupName);
        Assert.True(detailVm.IsReplyModeAll);
    }

    [AvaloniaFact]
    public async Task BotGroupDetailPage_SendStopThreadsApprovalsHandoffAndMembersWork()
    {
        using var phone = new Phone();
        var group = phone.Workspace.Hosts.SelectMany(h => h.BotGroups).Single(g => g.Id == "bgroup-release");

        phone.Shell.ShowBotGroup(group);
        var page = phone.Top<BotGroupDetailPage>();
        var vm = Assert.IsType<BotGroupDetailViewModel>(page.DataContext);
        await vm.RefreshAsync();

        // Main Room loaded with owner entry, handoff pill, hop-limit pause card, and pending approval
        Assert.Equal("发布协作组", vm.GroupName);
        Assert.Equal(3, vm.Entries.Count);
        Assert.True(vm.Entries[0].IsOwner);
        Assert.Equal(2, vm.Entries[0].Turns.Count);
        Assert.True(vm.Entries[1].IsMentionHandoff);
        Assert.True(vm.Entries[2].IsHopLimitPause);
        Assert.True(vm.HasPendingApprovals);
        Assert.Single(vm.PendingApprovals);

        // @Reply button prefills composer with @BotName
        vm.Entries[0].Turns[0].ReplyAtCommand.Execute(null);
        Assert.Equal("@西瓜 ", vm.ComposerText);

        // Send a message -> appended to transcript with bot turn reply
        vm.ComposerText = "@西瓜 确认发布分支状态";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal("", vm.ComposerText);
        Assert.Equal(4, vm.Entries.Count);
        Assert.Equal("@西瓜 确认发布分支状态", vm.Entries[^1].Text);
        Assert.Single(vm.Entries[^1].Turns);

        // Stop clears working state cleanly
        group.IsWorking = true;
        await vm.StopCommand.ExecuteAsync(null);
        Assert.False(group.IsWorking);

        // Continue hop-limit handoff resumes paused entry
        var pausedEntry = vm.Entries.Single(e => e.IsHopLimitPause);
        await pausedEntry.ContinueHandoffCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.Entries, e => e.IsHopLimitPause);

        // Respond to pending approval removes it from dock
        var approval = Assert.Single(vm.PendingApprovals);
        await approval.Options[0].ChooseCommand.ExecuteAsync(null);
        Assert.False(vm.HasPendingApprovals);
        Assert.Equal(0, group.PendingApprovalCount);

        // ReplyMode switching (Bot / All / Mentions)
        await vm.SetReplyModeBotCommand.ExecuteAsync(null);
        Assert.True(vm.IsReplyModeBot);
        Assert.True(vm.ShowTargetBotBar);
        await vm.SetReplyModeAllCommand.ExecuteAsync(null);
        Assert.True(vm.IsReplyModeAll);
        await vm.SetReplyModeMentionsCommand.ExecuteAsync(null);
        Assert.True(vm.IsReplyModeMentions);

        // Create a new thread and switch to it
        var initialThreadCount = vm.ThreadTabs.Count;
        await vm.CreateThreadCommand.ExecuteAsync(null);
        Assert.Equal(initialThreadCount + 1, vm.ThreadTabs.Count);
        Assert.False(string.IsNullOrWhiteSpace(vm.CurrentThreadId));
        Assert.Empty(vm.Entries);

        // Send inside the new thread
        vm.ComposerText = "@西瓜 排查新话题问题";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Single(vm.Entries);

        // Switch back to Main Room
        await vm.ThreadTabs[0].SelectCommand.ExecuteAsync(null);
        Assert.Null(vm.CurrentThreadId);
        Assert.Equal(4, vm.Entries.Count);

        // Member management sheet updates group name and members
        vm.OpenMembersSheetCommand.Execute(null);
        Assert.True(vm.IsMembersSheetOpen);
        vm.EditGroupName = "核心发布组";
        await vm.SaveMembersCommand.ExecuteAsync(null);
        Assert.False(vm.IsMembersSheetOpen);
        Assert.Equal("核心发布组", vm.GroupName);

        // Delete group navigates back
        vm.AskDeleteGroupCommand.Execute(null);
        Assert.True(vm.IsDeleteConfirmOpen);
        await vm.ConfirmDeleteGroupCommand.ExecuteAsync(null);
        Assert.DoesNotContain(phone.Workspace.Hosts.SelectMany(h => h.BotGroups), g => g.Id == group.Id);
        Assert.IsType<HostsPage>(phone.Shell.Top);
    }
}
