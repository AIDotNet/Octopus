using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.Agents;
using Octopus.App.Activity;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Remote;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.FileSystem;
using Octopus.Git;
using Octopus.Platform;
using Octopus.Remote.Host;
using Octopus.Remote.Protocol;
using Octopus.Remote.Security;
using Octopus.Remote.Transport;
using Octopus.Storage;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>
/// End-to-end tests for browsing and creating folders on a paired Octopus host through <see cref="HostFolderBrowser"/>
/// and <see cref="RemoteMethods.FsBrowse"/>, while keeping <see cref="RemoteMethods.FsList"/> restricted to projects.
/// </summary>
public class RemoteFolderBrowseTests
{
    private sealed class OtherComputer : IAsyncDisposable
    {
        public DeviceIdentity Identity { get; } = DeviceIdentity.Create();
        public ProjectService Projects { get; }
        public HostWorkspace Workspace { get; }
        public RemoteHostService Service { get; }
        public TerminalSessionRegistry Sessions { get; } = new();
        public string Root { get; }
        public string ProjectFolder { get; }
        public string OutsideFolder { get; }

        public OtherComputer(bool advertiseBrowse = true)
        {
            Root = RealPath.Resolve(Path.TrimEndingDirectorySeparator(Directory.CreateTempSubdirectory("octopus-browse-host-").FullName));
            ProjectFolder = Path.Combine(Root, "existing-project");
            OutsideFolder = Path.Combine(Root, "other-folder");
            Directory.CreateDirectory(ProjectFolder);
            Directory.CreateDirectory(OutsideFolder);
            File.WriteAllText(Path.Combine(Root, "secret.txt"), "should not appear in browse");

            Projects = new ProjectService(new InMemoryProjectStore(), AppHost.Get<PathComparer>(), new InMemorySettingsStore());
            Workspace = new HostWorkspace(Projects, AppHost.Get<IItemLaunchService>(), AppHost.Get<ITerminalConnectionFactory>(), Sessions, AppHost.Get<ITabContentRegistry>(),
                AppHost.Get<IItemActivityService>(), AppHost.Get<IAgentCatalog>(), AppHost.Get<IAgentScanner>(), AppHost.Get<ISettingsStore>(), AppHost.Get<IShellResolver>(),
                AppHost.Get<IFileTreeService>(), AppHost.Get<IGitService>(), AppHost.Get<PathComparer>());
            var hello = new HelloMessage
            {
                AppVersion = RemoteHostRuntime.Hello.AppVersion,
                Capabilities = advertiseBrowse
                    ? [.. RemoteHostRuntime.Hello.Capabilities]
                    : [.. RemoteHostRuntime.Hello.Capabilities.Where(c => c != RemoteMethods.CapabilityBrowse)],
                TerminalBuild = RemoteHostRuntime.Hello.TerminalBuild,
            };
            Service = new RemoteHostService(Identity, () => new DeviceDescription("Win", DeviceKind.Desktop), new PairedPeerStore(null), Workspace, hello);
            Projects.AddProject(ProjectFolder, "Existing");
        }

        public Task<IMessageTransport> Dial(Uri address, CancellationToken cancellationToken)
        {
            var (device, host) = InMemoryTransport.CreatePair();
            _ = Service.ServeAsync(host);
            return Task.FromResult<IMessageTransport>(device);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            Workspace.Dispose();
            Identity.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private static async Task<T> Wait<T>(Task<T> task, int milliseconds = 15_000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!task.IsCompleted && Environment.TickCount64 < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(task.IsCompleted, "The operation did not finish in time.");
        return await task;
    }

    private static async Task<string> Pair(OtherComputer other)
    {
        var runtime = AppHost.Get<RemoteClientRuntime>();
        runtime.Dial = other.Dial;
        var mine = await Wait(AppHost.Get<RemoteIdentityProvider>().GetAsync());
        other.Service.Devices.Add(mine.PublicKey, runtime.Description);
        runtime.Hosts.Add(other.Identity.PublicKey, new DeviceDescription("Win", DeviceKind.Desktop), ["ws://win.test:47600"]);
        await Wait(runtime.ConnectAsync(other.Identity.Id));
        return other.Identity.Id;
    }

    [AvaloniaFact]
    public async Task Host_folder_browser_lists_only_directories_marks_existing_projects_creates_folders_and_keeps_fs_list_restricted()
    {
        await using var other = new OtherComputer();
        var hostId = await Pair(other);
        var runtime = AppHost.Get<RemoteClientRuntime>();
        var browser = new HostFolderBrowser(runtime, hostId, "Win", "Unsupported host.");

        var page = await Wait(browser.BrowseAsync(other.Root));
        Assert.Equal(other.Root, page.CurrentPath);
        Assert.Equal(["existing-project", "other-folder"], page.Entries.Select(e => e.Name).ToArray());
        Assert.True(page.Entries.Single(e => e.Name == "existing-project").IsExistingProject);
        Assert.False(page.Entries.Single(e => e.Name == "other-folder").IsExistingProject);
        Assert.NotNull(page.ExistingProjects);
        Assert.Contains(page.ExistingProjects!, p => p.Name == "Existing");

        // Creating a new folder on the host works and shows up on the next browse.
        var created = await Wait(browser.CreateFolderAsync(other.Root, "fresh-repo"));
        Assert.True(Directory.Exists(created));

        var after = await Wait(browser.BrowseAsync(other.Root));
        Assert.Contains(after.Entries, e => e.Name == "fresh-repo");

        // Existing fs/list security boundary remains intact: outside project folders are still refused.
        var client = runtime.ClientOf(hostId)!;
        var refused = await Assert.ThrowsAsync<RemoteException>(() => Wait(client.ListFolderAsync(other.OutsideFolder)));
        Assert.Equal(RemoteMethods.NotAllowed, refused.Code);
    }

    [AvaloniaFact]
    public async Task Older_host_without_browse_capability_reports_a_clear_message()
    {
        await using var older = new OtherComputer(advertiseBrowse: false);
        var hostId = await Pair(older);
        var runtime = AppHost.Get<RemoteClientRuntime>();
        var browser = new HostFolderBrowser(runtime, hostId, "Win", "Update host to browse.");

        var error = await Assert.ThrowsAsync<IOException>(() => Wait(browser.BrowseAsync()));
        Assert.Equal("Update host to browse.", error.Message);
    }
}
