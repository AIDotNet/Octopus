using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Octopus.Mobile.Common;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Tests;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>
/// End-to-end verification of Desktop Project Groups on Mobile:
/// remote protocol sync (<c>project.groups</c>), zero-group flat degradation, ungrouped-first ordering,
/// collapsed 2×2 preview with lazy child mounting, expanded tinted capsules with MergeWorkspaces, single-group
/// focused banner, and Group Editor mutations (create, rename, color, MergeWorkspaces, membership, dissolve).
/// </summary>
public class ProjectGroupsTests
{
    [AvaloniaFact]
    public async Task Remote_protocol_syncs_desktop_project_groups_and_group_mutations()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        var coreA = studio.Workspace.AddProject("Octopus", "/home/ada/code/Octopus", isGitRepository: true);
        var coreB = studio.Workspace.AddProject("Octopus-Server", "/home/ada/code/Octopus-Server", isGitRepository: true);
        var solo = studio.Workspace.AddProject("Octopus-Plugins", "/home/ada/code/Octopus-Plugins", isGitRepository: true);
        var coreGroup = studio.Workspace.AddProjectGroup("核心产品", "#6366F1", mergeWorkspaces: true, coreA, coreB);

        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        phone.Pair(studio);

        var host = phone.Model<HostsViewModel>().Online.Single().Host;
        Assert.True(Phone.Until(() => host.Projects.Count == 3 && host.ProjectGroups.Count == 1),
            $"Expected 3 projects and 1 group, got {host.Projects.Count} projects / {host.ProjectGroups.Count} groups");

        Assert.True(phone.Remote.Workspace.SupportsProjectGroups(host));
        var syncedGroup = Assert.Single(host.ProjectGroups);
        Assert.Equal((coreGroup.Id, "核心产品", "#6366F1", true), (syncedGroup.Id, syncedGroup.Name, syncedGroup.Color, syncedGroup.MergeWorkspaces));
        Assert.Equal(coreGroup.Id, host.Projects.Single(p => p.Id == coreA.Id).GroupId);
        Assert.Equal(coreGroup.Id, host.Projects.Single(p => p.Id == coreB.Id).GroupId);
        Assert.Null(host.Projects.Single(p => p.Id == solo.Id).GroupId);

        // Update group name, color, and MergeWorkspaces via remote RPC.
        await phone.Remote.Workspace.UpdateProjectGroupAsync(syncedGroup, name: "核心工作区", color: "#1F9D74", mergeWorkspaces: false);
        Assert.Equal(("核心工作区", "#1F9D74", false), (syncedGroup.Name, syncedGroup.Color, syncedGroup.MergeWorkspaces));

        // Move ungrouped project into the group via remote RPC.
        var soloProject = host.Projects.Single(p => p.Id == solo.Id);
        await phone.Remote.Workspace.SetProjectGroupAsync(soloProject, syncedGroup.Id);
        Assert.Equal(syncedGroup.Id, soloProject.GroupId);

        // Create a brand-new group via remote RPC.
        var ossGroup = await phone.Remote.Workspace.CreateProjectGroupAsync(host, "开源生态", "#0EA5E9", mergeWorkspaces: true, [solo.Id]);
        Assert.True(Phone.Until(() => host.ProjectGroups.Count == 2));
        Assert.Equal(("开源生态", "#0EA5E9", true), (ossGroup.Name, ossGroup.Color, ossGroup.MergeWorkspaces));

        // Dissolve a group: group is removed while all member projects stay intact and ungrouped.
        await phone.Remote.Workspace.DeleteProjectGroupAsync(syncedGroup);
        Assert.True(Phone.Until(() => host.ProjectGroups.Count == 1));
        Assert.Equal(3, host.Projects.Count);
        Assert.Null(host.Projects.Single(p => p.Id == coreA.Id).GroupId);
        Assert.Null(host.Projects.Single(p => p.Id == coreB.Id).GroupId);
    }

    [AvaloniaFact]
    public void Host_page_shows_ungrouped_first_collapsed_2x2_idle_groups_without_mounting_children_and_single_group_banner()
    {
        using var phone = new Phone();
        var mac = phone.Workspace.Hosts[0];

        // Add an idle Desktop group with 2 projects to test collapsed 2x2 preview + lazy child mounting.
        var ossGroup = new RemoteProjectGroup(mac, "g-oss", "开源生态") { Color = "#1F9D74", MergeWorkspaces = false };
        mac.ProjectGroups.Add(ossGroup);
        var sandbox = new RemoteProject(mac, "mac/opensandbox", "OpenSandbox", "~/code/OpenSandbox")
        {
            Color = "#D9822B",
            GroupId = ossGroup.Id,
            IsGitRepository = true,
            DefaultBranch = "dev",
        };
        var relayProj = new RemoteProject(mac, "mac/relay", "Octopus-Relay", "~/code/Octopus-Relay")
        {
            Color = "#0EA5E9",
            GroupId = ossGroup.Id,
            IsGitRepository = true,
            DefaultBranch = "main",
        };
        mac.Projects.Add(sandbox);
        mac.Projects.Add(relayProj);

        phone.Shell.ShowHost(mac);
        Phone.Settle();

        var page = phone.Top<HostPage>();
        var vm = (HostViewModel)page.DataContext!;

        // Desktop groups exist -> grouped layout active, flat layout empty (zero duplicate visual trees).
        Assert.True(vm.HasProjectGroups);
        Assert.True(vm.ShowGroupedLayout);
        Assert.Empty(vm.FlatDisplayedSections);

        // Ungrouped project (TKCode.Arc) appears first in UngroupedSections.
        var ungrouped = Assert.Single(vm.UngroupedSections);
        Assert.Equal("TKCode.Arc", ungrouped.Name);

        // Two Desktop groups: active "核心产品" auto-expanded; idle "开源生态" default-collapsed with 0 mounted child controls.
        Assert.Equal(2, vm.VisibleProjectGroups.Count);
        var coreCard = vm.VisibleProjectGroups[0];
        var ossCard = vm.VisibleProjectGroups[1];
        Assert.Equal("核心产品", coreCard.Name);
        Assert.True(coreCard.IsMerged);
        Assert.True(coreCard.IsExpanded);
        Assert.Equal(2, coreCard.MountedSections.Count);

        Assert.Equal("开源生态", ossCard.Name);
        Assert.False(ossCard.IsExpanded);
        Assert.Empty(ossCard.MountedSections);
        Assert.Equal(2, ossCard.MemberProjects.Count);

        // Collapsed group renders ProjectGroup2x2View in the visual tree.
        Assert.Contains(page.GetVisualDescendants().OfType<ProjectGroup2x2View>(), v => v.IsEffectivelyVisible);

        // Expanding the idle group lazily mounts its 2 member project cards.
        ossCard.ToggleExpandedCommand.Execute(null);
        Phone.Pump();
        Assert.True(ossCard.IsExpanded);
        Assert.Equal(2, ossCard.MountedSections.Count);

        // Tapping a group filter chip switches to single-group focused banner + flat member cards.
        vm.SelectProjectGroup(coreCard.Group.Id);
        Phone.Pump();
        Assert.True(vm.ShowSingleGroupBanner);
        Assert.True(vm.ShowFlatSections);
        Assert.Equal("核心产品", vm.ActiveGroupName);
        Assert.True(vm.ActiveGroupIsMerged);
        Assert.Equal(["Octopus", "OctopusServer"], vm.FlatDisplayedSections.Select(s => s.Name));
        Assert.Empty(vm.VisibleProjectGroups);
        Assert.Empty(vm.UngroupedSections);

        // Selecting Ungrouped chip shows only ungrouped projects.
        vm.SelectProjectGroup(HostViewModel.UngroupedGroupKey);
        Phone.Pump();
        Assert.False(vm.ShowSingleGroupBanner);
        Assert.Equal(["TKCode.Arc"], vm.FlatDisplayedSections.Select(s => s.Name));

        // Zero-group degradation: removing all groups hides group chrome and returns to flat project list.
        vm.SelectProjectGroup(null);
        mac.ProjectGroups.Clear();
        Phone.Pump();
        Assert.False(vm.HasProjectGroups);
        Assert.False(vm.ShowGroupedLayout);
        Assert.True(vm.ShowFlatSections);
        Assert.Equal(5, vm.FlatDisplayedSections.Count);
    }

    [AvaloniaFact]
    public async Task Group_editor_manages_rename_color_merge_workspaces_members_and_dissolve()
    {
        using var phone = new Phone();
        var mac = phone.Workspace.Hosts[0];
        phone.Shell.ShowHost(mac);
        Phone.Settle();

        var vm = (HostViewModel)phone.Top<HostPage>().DataContext!;
        var coreGroup = Assert.Single(mac.ProjectGroups);

        // Open Manage Group sheet, rename, switch color (Desktop Sky #0EA5E9), toggle MergeWorkspaces off, and add TKCode.Arc into the group.
        vm.GroupEditor.OpenManage(coreGroup);
        Assert.True(vm.GroupEditor.IsManageOpen);
        vm.GroupEditor.EditName = "主研发组";
        vm.GroupEditor.EditSwatches[1].SelectCommand.Execute(null); // Desktop ProjectService.Palette[1] = #0EA5E9
        vm.GroupEditor.EditMergeWorkspaces = false;

        var arcToggle = vm.GroupEditor.MemberToggles.Single(t => t.Name == "TKCode.Arc");
        Assert.False(arcToggle.IsInGroup);
        await arcToggle.ToggleCommand.ExecuteAsync(null);
        Assert.Equal(coreGroup.Id, mac.Projects.Single(p => p.Name == "TKCode.Arc").GroupId);

        await vm.GroupEditor.SaveCommand.ExecuteAsync(null);
        Phone.Pump();
        Assert.False(vm.GroupEditor.IsManageOpen);
        Assert.Equal(("主研发组", "#0EA5E9", false), (coreGroup.Name, coreGroup.Color, coreGroup.MergeWorkspaces));
        Assert.Empty(vm.UngroupedSections);

        // Session status ticks must reuse ProjectGroupCardViewModel and chip instances in-place (zero visual-tree tear-down).
        var cardBeforeTick = Assert.Single(vm.VisibleProjectGroups);
        var chipBeforeTick = vm.GroupFilterChips[1];
        var firstSession = mac.Projects[0].Sessions[0];
        firstSession.Activity = firstSession.Activity == SessionActivity.Working ? SessionActivity.Idle : SessionActivity.Working;
        Phone.Pump();
        Assert.Same(cardBeforeTick, Assert.Single(vm.VisibleProjectGroups));
        Assert.Same(chipBeforeTick, vm.GroupFilterChips[1]);

        // Direct per-project Move-to-Group sheet removes TKCode.Arc back to Ungrouped in 1 tap.
        var arcSection = vm.Sections.Single(s => s.Name == "TKCode.Arc");
        Assert.True(arcSection.CanMoveToGroup);
        arcSection.MoveToGroupCommand.Execute(null);
        Phone.Pump();
        Assert.True(vm.GroupEditor.IsMoveSheetOpen);
        await vm.GroupEditor.MoveProjectToGroupCommand.ExecuteAsync(null);
        Phone.Pump();
        Assert.Null(arcSection.Project.GroupId);
        Assert.Single(vm.UngroupedSections);

        // Dissolve group confirmation flow: cancel keeps the group intact; confirm dissolves while keeping all projects.
        vm.GroupEditor.OpenManage(coreGroup);
        vm.GroupEditor.RequestDissolveCommand.Execute(null);
        Assert.True(vm.GroupEditor.IsDissolveConfirmOpen);
        vm.GroupEditor.CancelDissolveCommand.Execute(null);
        Assert.False(vm.GroupEditor.IsDissolveConfirmOpen);
        Assert.Single(mac.ProjectGroups);

        vm.GroupEditor.RequestDissolveCommand.Execute(null);
        await vm.GroupEditor.DissolveCommand.ExecuteAsync(null);
        Phone.Pump();
        Assert.False(vm.GroupEditor.IsDissolveConfirmOpen);
        Assert.Empty(mac.ProjectGroups);
        Assert.Equal(3, mac.Projects.Count);
        Assert.All(mac.Projects, p => Assert.Null(p.GroupId));
        Assert.False(vm.HasProjectGroups);

        // Create a brand new group with an initial member pre-selected in the New Group sheet.
        vm.OpenNewProjectGroupCommand.Execute(null);
        Assert.True(vm.GroupEditor.IsNewOpen);
        vm.GroupEditor.NewName = "服务端集群";
        vm.GroupEditor.NewMergeWorkspaces = true;
        var serverPick = vm.GroupEditor.NewMemberToggles.Single(t => t.Name == "OctopusServer");
        await serverPick.ToggleCommand.ExecuteAsync(null);
        Assert.True(serverPick.IsInGroup);
        await vm.GroupEditor.CreateCommand.ExecuteAsync(null);
        Phone.Pump();
        var created = Assert.Single(mac.ProjectGroups);
        Assert.Equal(("服务端集群", true), (created.Name, created.MergeWorkspaces));
        Assert.Equal(created.Id, mac.Projects.Single(p => p.Name == "OctopusServer").GroupId);
        Assert.True(vm.HasProjectGroups);
    }
}
