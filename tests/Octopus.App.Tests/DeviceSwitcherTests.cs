using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Octopus.App.Activity;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Dialogs;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.App.Sidebar;
using Octopus.Core.Projects;
using SkiaSharp;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>
/// Sidebar Option B device switcher: switching devices filters the rail to only that device's projects,
/// remembers the last project per device, surfaces cross-device attention, and lets users customize each device's
/// icon using the same color / Lucide / pixel / picture appearance flow as projects.
/// </summary>
public class DeviceSwitcherTests
{
    private static string TempDir(string name) =>
        Directory.CreateTempSubdirectory("octopus-dev-" + name + "-").FullName;

    private static string WriteImage(int width, int height)
    {
        var path = Path.Combine(TempDir("img"), "pic.png");
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.MediumSlateBlue);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [AvaloniaFact, FreshApp]
    public void Switching_devices_filters_rail_projects_and_groups_and_remembers_last_project()
    {
        var projects = AppHost.Get<IProjectService>();
        var rail = AppHost.Get<ProjectRailViewModel>();
        var devices = AppHost.Get<DeviceSwitcherViewModel>();

        var localA = projects.AddProject(TempDir("local-a"), "LocalA");
        var localB = projects.AddProject(TempDir("local-b"), "LocalB");
        var localGroup = projects.AddGroup("Work", [localB.Id]);
        var sshProd = projects.AddProject("/srv/portal", "Portal", sshHost: "prod-ecs");
        var remoteApi = projects.AddRemoteProject("host-studio", "r-1", "/code/api", "StudioApi");

        // Adding a remote project activates that remote device and shows only its projects on the rail.
        Assert.Equal(DeviceKeys.Remote("host-studio"), projects.ActiveDeviceKey);
        Assert.Single(rail.Chips, c => c.Id == remoteApi.Id);
        Assert.Empty(rail.Entries.OfType<ProjectGroupViewModel>());

        // Switch to Local: only local projects (and the local group) appear; last active local project (LocalB) is restored.
        var localEntry = Assert.Single(devices.LocalDevices);
        localEntry.SelectCommand.Execute(null);
        Assert.Equal(DeviceKeys.Local, projects.ActiveDeviceKey);
        Assert.Same(localB, projects.Current);
        Assert.Equal(new[] { localA.Id, localB.Id }, rail.Chips.Select(c => c.Id).OrderBy(x => x).ToArray().Length == 2
            ? [localA.Id, localB.Id] : []);
        Assert.Equal([localA.Id, localB.Id], rail.Chips.Select(c => c.Id));
        Assert.Single(rail.Entries.OfType<ProjectGroupViewModel>(), g => g.Id == localGroup.Id);

        // Select LocalA, switch to SSH device, then switch back to Local -> LocalA is remembered.
        projects.SetCurrent(localA.Id);
        var sshEntry = Assert.Single(devices.SshDevices, d => d.TargetId == "prod-ecs");
        sshEntry.SelectCommand.Execute(null);
        Assert.Equal(DeviceKeys.Ssh("prod-ecs"), projects.ActiveDeviceKey);
        Assert.Same(sshProd, projects.Current);
        Assert.Single(rail.Chips, c => c.Id == sshProd.Id);

        devices.LocalDevices.Single().SelectCommand.Execute(null);
        Assert.Same(localA, projects.Current);
    }

    [AvaloniaFact, FreshApp]
    public async Task Switching_to_empty_device_shows_empty_state_and_preselects_device_for_new_project()
    {
        var projects = AppHost.Get<IProjectService>();
        var rail = AppHost.Get<ProjectRailViewModel>();
        var panel = AppHost.Get<ProjectPanelViewModel>();
        var opener = AppHost.Get<IItemOpener>();
        var dialogs = AppHost.Get<FakeDialogService>();

        projects.AddProject(TempDir("local"), "LocalOnly");

        // Switch to an SSH host that has no projects yet.
        projects.SetActiveDevice(DeviceKeys.Ssh("staging-box"));

        Assert.Null(projects.Current);
        Assert.Empty(rail.Chips);
        Assert.True(panel.HasNoProject);
        Assert.Contains("staging-box", panel.EmptyTitle);

        // Clicking "+" pre-selects staging-box in the New Project dialog.
        dialogs.NewProject = null;
        await opener.PickAndAddProjectAsync();
        Assert.NotNull(dialogs.LastNewProjectInitial);
        Assert.Equal("staging-box", dialogs.LastNewProjectInitial!.SshHost);
        Assert.Null(dialogs.LastNewProjectInitial.RemoteHost);
    }

    [AvaloniaFact, FreshApp]
    public async Task Device_icon_can_be_customized_with_lucide_pixel_initial_or_picture()
    {
        var projects = AppHost.Get<IProjectService>();
        var devices = AppHost.Get<DeviceSwitcherViewModel>();
        var dialogs = AppHost.Get<FakeDialogService>();
        var store = (ProjectIconStore)AppHost.Get<IProjectIconStore>();

        // Default uncustomized local device shows Laptop icon.
        Assert.True(devices.CurrentDevice.ShowIcon);
        Assert.Equal(LucideIconKind.Laptop, devices.CurrentDevice.IconKind);

        // 1. Custom color + Lucide icon.
        dialogs.DeviceAppearance = new ProjectAppearanceChoice("#10B981", "Rocket", null);
        await devices.CurrentDevice.ChangeIconCommand.ExecuteAsync(null);
        Assert.Equal(DeviceKeys.Local, dialogs.LastDeviceAppearanceKey);
        Assert.True(devices.CurrentDevice.ShowIcon);
        Assert.Equal(LucideIconKind.Rocket, devices.CurrentDevice.IconKind);
        Assert.Equal(Color.Parse("#10B981"), Assert.IsAssignableFrom<ISolidColorBrush>(devices.CurrentDevice.Background).Color);

        // 2. Built-in pixel icon.
        var pixel = PixelIcons.All[0];
        dialogs.DeviceAppearance = new ProjectAppearanceChoice("#F59E0B", PixelIcons.Reference(pixel), null);
        await devices.CurrentDevice.ChangeIconCommand.ExecuteAsync(null);
        Assert.True(devices.CurrentDevice.ShowPixel);
        Assert.Equal(pixel.Name, devices.CurrentDevice.Pixel!.Name);

        // 3. Initial ("Aa", Icon == null).
        dialogs.DeviceAppearance = new ProjectAppearanceChoice("#0EA5E9", null, null);
        await devices.CurrentDevice.ChangeIconCommand.ExecuteAsync(null);
        Assert.True(devices.CurrentDevice.ShowInitial);

        // 4. Custom picture thumbnail, and replacing it deletes the old thumbnail.
        var firstThumb = await store.ImportAsync("dev-local", WriteImage(300, 300));
        Assert.NotNull(firstThumb);
        dialogs.DeviceAppearance = new ProjectAppearanceChoice("#0EA5E9", null, firstThumb);
        await devices.CurrentDevice.ChangeIconCommand.ExecuteAsync(null);
        Assert.True(devices.CurrentDevice.HasImage);
        Assert.Same(Brushes.Transparent, devices.CurrentDevice.Background);

        dialogs.DeviceAppearance = new ProjectAppearanceChoice("#6366F1", "Server", null);
        await devices.CurrentDevice.ChangeIconCommand.ExecuteAsync(null);
        Assert.False(devices.CurrentDevice.HasImage);
        Assert.False(File.Exists(Path.Combine(store.Directory, firstThumb!)));

        // Appearance dialog with circularPreview renders a circular preview and returns the choice.
        var form = new ProjectAppearanceView(
            "dev-local", "MyMac", "Local", "#10B981", "Rocket", null,
            AppHost.Get<FakePickerService>(), store, circularPreview: true);
        Assert.Equal(new CornerRadius(28), form.FindControl<Border>("Preview")!.CornerRadius);
        Assert.Equal(new ProjectAppearanceChoice("#10B981", "Rocket", null), form.Choice);
    }

    [AvaloniaFact, FreshApp]
    public void Cross_device_waiting_attention_surfaces_on_device_switcher_badge()
    {
        var projects = AppHost.Get<IProjectService>();
        var activity = AppHost.Get<IItemActivityService>();
        var devices = AppHost.Get<DeviceSwitcherViewModel>();

        var local = projects.AddProject(TempDir("local"), "LocalApp");
        var localItem = new ProjectItem { Kind = ProjectItemKind.Agent, Title = "Claude" };
        projects.AddItem(local, localItem);

        projects.AddProject("/srv/app", "ProdApp", sshHost: "prod");
        // Now active device is ssh:prod. Mark local agent as Waiting.
        activity.ReportRemote(localItem.Id, ItemActivity.Waiting);

        Assert.Equal(1, devices.OtherWaitingCount);
        Assert.True(devices.HasOtherWaiting);
        Assert.Equal("1", devices.OtherWaitingBadgeText);

        // Switching back to Local clears "other devices" waiting count.
        projects.SetActiveDevice(DeviceKeys.Local);
        Assert.Equal(0, devices.OtherWaitingCount);
        Assert.False(devices.HasOtherWaiting);
    }

    [Fact]
    public async Task Active_device_last_projects_and_device_appearances_persist_across_reload()
    {
        var store = new RecordingProjectStore();
        var comparer = new PathComparer(ignoreCase: false);
        var service = new ProjectService(store, comparer);
        var local = service.AddProject(TempDir("persist-local"), "Local");
        var ssh = service.AddProject("/srv/web", "Web", sshHost: "edge");
        service.SetDeviceAppearance(DeviceKeys.Local, "#EC4899", "Sparkles", null);
        service.SetActiveDevice(DeviceKeys.Local);
        await service.FlushAsync();

        var reloaded = new ProjectService(store, comparer);
        await reloaded.LoadAsync();

        Assert.Equal(DeviceKeys.Local, reloaded.ActiveDeviceKey);
        Assert.Equal(local.Id, reloaded.Current?.Id);
        var saved = reloaded.GetDeviceAppearance(DeviceKeys.Local);
        Assert.NotNull(saved);
        Assert.Equal("#EC4899", saved!.Color);
        Assert.Equal("Sparkles", saved.Icon);

        reloaded.SetActiveDevice(DeviceKeys.Ssh("edge"));
        Assert.Equal(ssh.Id, reloaded.Current?.Id);
    }

    [AvaloniaFact, FreshApp]
    public void Clicking_rail_device_badge_opens_device_switcher_popover()
    {
        var projects = AppHost.Get<IProjectService>();
        projects.AddProject(TempDir("ui-local"), "UiLocal");
        projects.AddProject("/srv/api", "UiSsh", sshHost: "srv-1");

        var window = new MainWindow();
        window.Show();
        Frames.Pump();

        var railView = window.GetVisualDescendants().OfType<ProjectRailView>().Single();
        var badge = railView.FindControl<Avalonia.Controls.Button>("DeviceBadgeButton")!;
        Assert.True(badge.IsVisible);

        DeviceSwitcherRequest.Raise(badge);
        Frames.Pump();

        var shellView = window.GetVisualDescendants().OfType<ShellView>().Single();
        var popover = shellView.FindControl<Popover>("DevicePopover")!;
        Assert.True(popover.IsOpen);

        AppHost.Get<DeviceSwitcherViewModel>().LocalDevices.Single().SelectCommand.Execute(null);
        Frames.Pump();
        Assert.False(popover.IsOpen);
        Assert.Equal(DeviceKeys.Local, projects.ActiveDeviceKey);

        window.Close();
    }
}
