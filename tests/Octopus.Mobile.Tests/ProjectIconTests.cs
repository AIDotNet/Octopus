using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Octopus.Mobile.Common;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Remote;
using Octopus.Remote.Tests;
using Xunit;

namespace Octopus.Mobile.Tests;

public sealed class ProjectIconTests : IDisposable
{
    // Valid 1x1 red PNG and 1x1 blue PNG for fast, deterministic bitmap decoding tests.
    private static readonly byte[] RedPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGO4Y2MDAANMAVV5GmOeAAAAAElFTkSuQmCC");

    private static readonly byte[] BluePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGOwqbgDAAKEAZE7aWvyAAAAAElFTkSuQmCC");

    private readonly string _folder = Directory.CreateTempSubdirectory("octopus-mobile-icons-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [AvaloniaFact]
    public void Project_badge_renders_picture_pixel_lucide_and_initial_in_desktop_priority_order()
    {
        using var phone = new Phone();
        var host = phone.Workspace.Hosts[0];
        var project = host.Projects[0];
        project.Color = "#3B82F6";
        project.Icon = null;
        project.ImageKey = null;
        project.ImageBitmap = null;

        phone.Shell.ShowHost(host);
        Phone.Settle();

        var badge = phone.Top<HostPage>().GetVisualDescendants()
            .OfType<ProjectBadgeView>()
            .First(b => ReferenceEquals(b.Project, project));

        // 1. Default fallback: initial letter.
        Assert.True(badge.ShowsInitial);
        Assert.False(badge.ShowsLucide);
        Assert.False(badge.ShowsPixel);
        Assert.False(badge.ShowsImage);

        // 2. Lucide icon name from desktop.
        project.Icon = "Rocket";
        Phone.Pump();
        Assert.True(badge.ShowsLucide);
        Assert.False(badge.ShowsInitial);

        // 3. Built-in pixel icon ("pixel:<id>") from desktop.
        var anyPixel = Octopus.App.Common.PixelIcons.All[0];
        project.Icon = Octopus.App.Common.PixelIcons.Reference(anyPixel);
        Phone.Pump();
        Assert.True(badge.ShowsPixel);
        Assert.False(badge.ShowsLucide);

        // 4. Custom picture bitmap wins over everything else and updates live in place.
        using var ms = new MemoryStream(RedPng);
        project.ImageKey = "p1-aaaa1111.png";
        project.ImageBitmap = new Bitmap(ms);
        Phone.Pump();
        Assert.True(badge.ShowsImage);
        Assert.False(badge.ShowsPixel);
        Assert.False(badge.ShowsLucide);
        Assert.False(badge.ShowsInitial);

        // 5. Clearing the picture falls back to the pixel icon again.
        project.ImageKey = null;
        project.ImageBitmap = null;
        Phone.Pump();
        Assert.False(badge.ShowsImage);
        Assert.True(badge.ShowsPixel);
    }

    [AvaloniaFact]
    public void Disk_icon_cache_persists_across_restarts_and_prunes_stale_files()
    {
        var cache1 = new MobileProjectIconCache(_folder);
        Assert.Null(cache1.TryLoad("host-1", "p1-v1.png"));

        var saved = cache1.SaveAndLoad("host-1", "p1-v1.png", RedPng);
        Assert.NotNull(saved);

        // A brand-new cache instance (simulating app restart) loads directly from disk.
        var cache2 = new MobileProjectIconCache(_folder);
        var loaded = cache2.TryLoad("host-1", "p1-v1.png");
        Assert.NotNull(loaded);

        // Save v2 and prune so v1 is removed from disk while v2 stays.
        Assert.NotNull(cache2.SaveAndLoad("host-1", "p1-v2.png", BluePng));
        cache2.Prune("host-1", new HashSet<string>(StringComparer.Ordinal) { "p1-v2.png" });

        var hostDir = Path.Combine(cache2.DirectoryPath!, "host-1");
        Assert.False(File.Exists(Path.Combine(hostDir, "p1-v1.png")));
        Assert.True(File.Exists(Path.Combine(hostDir, "p1-v2.png")));
    }

    [AvaloniaFact]
    public async Task Remote_project_icons_sync_cache_to_disk_skip_unchanged_and_update_when_changed()
    {
        await using var relay = new FakeRelay();
        await using var computer = await Computer.StartAsync(relay, "Studio");
        var p1 = computer.Workspace.AddProject("Octopus", "/code/Octopus");
        computer.Workspace.SetProjectAppearance(p1, color: "#D97757", icon: "Rocket", image: "p1-aaaa1111.png", imageBytes: RedPng);

        // First phone session: pairs and downloads the icon exactly once.
        using (var phone = new RealPhone(relay, _folder))
        {
            phone.SignIn(relay);
            phone.Pair(computer);

            Assert.True(Phone.Until(() =>
                phone.Remote.Workspace.Hosts.FirstOrDefault()?.Projects.FirstOrDefault() is { ImageBitmap: not null, Icon: "Rocket", Color: "#D97757" }));
            Assert.Equal(1, computer.Workspace.IconRequestCount);

            // Workspace changes without changing the project's image key -> must NOT re-fetch project/icon.
            computer.Workspace.AddItem(p1, "Shell");
            Assert.True(Phone.Until(() => phone.Remote.Workspace.Hosts[0].Projects[0].Sessions.Count == 1));
            Phone.Settle(150);
            Assert.Equal(1, computer.Workspace.IconRequestCount);
        }

        // Second phone session sharing the same DataDirectory: loads directly from disk cache with ZERO icon RPCs!
        var requestsBeforeRestart = computer.Workspace.IconRequestCount;
        using (var phone2 = new RealPhone(relay, _folder))
        {
            Assert.True(Phone.Until(() =>
                phone2.Remote.Workspace.Hosts.FirstOrDefault()?.Projects.FirstOrDefault() is { ImageBitmap: not null, ImageKey: "p1-aaaa1111.png" }));
            Phone.Settle(150);
            Assert.Equal(requestsBeforeRestart, computer.Workspace.IconRequestCount);

            // Now the user changes the project's picture on desktop -> new image key triggers exactly one fetch & prunes old cache file.
            computer.Workspace.SetProjectAppearance(p1, color: "#D97757", icon: "Rocket", image: "p1-bbbb2222.png", imageBytes: BluePng);
            Assert.True(Phone.Until(() =>
                phone2.Remote.Workspace.Hosts[0].Projects[0] is { ImageKey: "p1-bbbb2222.png", ImageBitmap: not null }));
            Assert.Equal(requestsBeforeRestart + 1, computer.Workspace.IconRequestCount);

            var hostCacheDir = Path.Combine(_folder, "project-icons");
            Assert.True(Phone.Until(() =>
                Directory.Exists(hostCacheDir)
                && Directory.GetFiles(hostCacheDir, "p1-bbbb2222.png", SearchOption.AllDirectories).Length == 1
                && Directory.GetFiles(hostCacheDir, "p1-aaaa1111.png", SearchOption.AllDirectories).Length == 0));
        }
    }
}
