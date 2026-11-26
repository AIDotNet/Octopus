using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.Agents;
using Octopus.App.Activity;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Explorer;
using Octopus.App.Remote;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Core.Projects;
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
/// The files and git of a project on another computer (design/12-远程操作.md, R3): the right panel's tree, opening and
/// saving a file, and source control, all carried out by the host on the host's real files and its real git.
/// </summary>
public class RemoteFilesAndGitTests
{
    /// <summary>The other computer, with a project in a real temporary folder (a git repository when asked).</summary>
    private sealed class OtherComputer : IAsyncDisposable
    {
        public DeviceIdentity Identity { get; } = DeviceIdentity.Create();
        public ProjectService Projects { get; }
        public HostWorkspace Workspace { get; }
        public RemoteHostService Service { get; }
        public TerminalSessionRegistry Sessions { get; } = new();
        public string Folder { get; } = Path.TrimEndingDirectorySeparator(Directory.CreateTempSubdirectory("octopus-host-files-").FullName);
        public Project Project { get; }

        public OtherComputer()
        {
            // The real path: on macOS the temporary folder is reached through a link, and git reports the real one.
            Folder = RealPath.Resolve(Folder);
            Projects = new ProjectService(new InMemoryProjectStore(), AppHost.Get<PathComparer>());
            Workspace = new HostWorkspace(Projects, AppHost.Get<IItemLaunchService>(), AppHost.Get<ITerminalConnectionFactory>(), Sessions, AppHost.Get<ITabContentRegistry>(),
                AppHost.Get<IItemActivityService>(), AppHost.Get<IAgentCatalog>(), AppHost.Get<IAgentScanner>(), AppHost.Get<ISettingsStore>(), AppHost.Get<IShellResolver>(),
                AppHost.Get<IFileTreeService>(), AppHost.Get<IGitService>(), AppHost.Get<PathComparer>());
            Service = new RemoteHostService(Identity, () => new DeviceDescription("Studio", DeviceKind.Desktop), new PairedPeerStore(null), Workspace, RemoteHostRuntime.Hello);
            File.WriteAllText(Path.Combine(Folder, "README.md"), "# Studio project\n");
            Directory.CreateDirectory(Path.Combine(Folder, "src"));
            File.WriteAllText(Path.Combine(Folder, "src", "main.cs"), "class Program { }\n");
            Project = Projects.AddProject(Folder, "studio-project");
        }

        public void Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = Folder, RedirectStandardOutput = true, RedirectStandardError = true };
            string[] all = ["-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", .. arguments];
            foreach (var argument in all) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            process.WaitForExit(20_000);
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
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

    private static Task Wait(Task task) => Wait(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));

    private static async Task Until(Func<bool> done, string what, int milliseconds = 15_000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!done() && Environment.TickCount64 < end)
        {
            Frames.Pump();
            await Task.Delay(5);
        }
        Assert.True(done(), what);
    }

    /// <summary>This app paired with the other computer (already known on both sides), and connected.</summary>
    private static async Task<string> Pair(OtherComputer other)
    {
        var runtime = AppHost.Get<RemoteClientRuntime>();
        runtime.Dial = other.Dial;
        var mine = await Wait(AppHost.Get<RemoteIdentityProvider>().GetAsync());
        other.Service.Devices.Add(mine.PublicKey, runtime.Description);
        runtime.Hosts.Add(other.Identity.PublicKey, new DeviceDescription("Studio", DeviceKind.Desktop), ["ws://studio.test:47600"]);
        await Wait(runtime.ConnectAsync(other.Identity.Id));
        return other.Identity.Id;
    }

    [AvaloniaFact]
    public async Task The_hosts_files_are_listed_read_written_and_kept_inside_its_projects()
    {
        await using var other = new OtherComputer();
        var hostId = await Pair(other);
        var tree = new HostFileTree(AppHost.Get<RemoteClientRuntime>(), hostId);

        Assert.Equal(other.Folder, await Wait(tree.ResolveAsync(other.Folder)));
        var root = await Wait(tree.ReadFolderAsync(other.Folder));
        // Folders first, as the host's own tree shows them.
        Assert.Equal(["src", "README.md"], root.Select(entry => entry.Name));
        Assert.True(root[0].IsDirectory);
        Assert.Equal(Path.Combine(other.Folder, "src", "main.cs"), (await Wait(tree.ReadFolderAsync(root[0].FullPath))).Single().FullPath);

        // A file larger than one piece goes over whole, both ways.
        var big = new byte[RemoteMethods.FilePieceBytes * 2 + 12345];
        Random.Shared.NextBytes(big);
        var path = Path.Combine(other.Folder, "src", "data.bin");
        await Wait(tree.WriteAsync(path, big));
        Assert.Equal(big, File.ReadAllBytes(path));
        Assert.Equal(big, await Wait(tree.ReadAsync(path, 5 * 1024 * 1024)));
        Assert.Empty(Directory.GetFiles(Path.Combine(other.Folder, "src"), "*.tmp"));
        var tooLarge = await Assert.ThrowsAsync<RemoteFileTooLargeException>(() => Wait(tree.ReadAsync(path, 1000)));
        Assert.Equal(big.Length, tooLarge.Size);

        // Overwriting keeps the file's rights; new, rename, delete.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await Wait(tree.WriteAsync(path, "short"u8.ToArray()));
            Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute));
        }
        await Wait(tree.CreateDirectoryAsync(Path.Combine(other.Folder, "docs")));
        await Wait(tree.CreateFileAsync(Path.Combine(other.Folder, "docs", "notes.md")));
        await Wait(tree.RenameAsync(Path.Combine(other.Folder, "docs", "notes.md"), Path.Combine(other.Folder, "docs", "plan.md")));
        Assert.True(File.Exists(Path.Combine(other.Folder, "docs", "plan.md")));
        Assert.True(await Wait(tree.ExistsAsync(Path.Combine(other.Folder, "docs", "plan.md"))));
        await Wait(tree.DeleteAsync(Path.Combine(other.Folder, "docs", "plan.md"), isDirectory: false));
        Assert.False(await Wait(tree.ExistsAsync(Path.Combine(other.Folder, "docs", "plan.md"))));

        // Outside every project of the host nothing is reachable, by any spelling; nor is a project's own folder deleted.
        var outside = Path.GetDirectoryName(other.Folder)!;
        foreach (var attempt in new[] { outside, Path.Combine(other.Folder, "..", "elsewhere.txt"), "/etc/passwd", "~" })
        {
            await Assert.ThrowsAnyAsync<IOException>(() => Wait(tree.ReadAsync(attempt, 100)));
            await Assert.ThrowsAnyAsync<IOException>(() => Wait(tree.WriteAsync(attempt, "x"u8.ToArray())));
        }
        Assert.Empty(await Wait(tree.ReadFolderAsync(outside)));
        Assert.NotNull(tree.Status);
        // The tree itself never waits: a folder it has not read is empty at first and it is told when the answer is there.
        var told = new List<string>();
        using (tree.Watch(other.Folder, directory => { lock (told) told.Add(directory); }))
        {
            var docs = Path.Combine(other.Folder, "docs");
            Assert.Empty(tree.List(docs));
            await Until(() => { lock (told) return told.Contains(docs); }, "the tree is told when the folder was read");
            Assert.Empty(tree.List(docs)); // read, and really empty now
            File.WriteAllText(Path.Combine(docs, "later.md"), "x");
            await Until(() => tree.List(docs).Count == 1, "a change on the host shows after it says so");
        }
        await Assert.ThrowsAnyAsync<IOException>(() => Wait(tree.DeleteAsync(other.Folder, isDirectory: true)));
        Assert.True(Directory.Exists(other.Folder));
    }

    [AvaloniaFact]
    public async Task The_host_says_when_a_watched_folder_changed()
    {
        await using var other = new OtherComputer();
        var hostId = await Pair(other);
        var tree = new HostFileTree(AppHost.Get<RemoteClientRuntime>(), hostId);
        var changes = 0;
        using var watch = tree.WatchChanges(other.Folder, () => Interlocked.Increment(ref changes), TimeSpan.Zero);
        Assert.Equal(2, (await Wait(tree.ReadFolderAsync(other.Folder))).Count);
        await Task.Delay(300); // the host starts watching

        File.WriteAllText(Path.Combine(other.Folder, "added-on-the-host.txt"), "x");

        await Until(() => Volatile.Read(ref changes) > 0, "the device is told about the change");
        // And what it had listed is no longer trusted.
        Assert.Contains("added-on-the-host.txt", (await Wait(tree.ReadFolderAsync(other.Folder))).Select(e => e.Name));
    }

    [AvaloniaFact]
    public async Task Source_control_of_a_remote_project_is_the_hosts_git()
    {
        await using var other = new OtherComputer();
        other.Git("init", "-q", "-b", "main");
        // The commit below is made by the host's own git, which a CI machine has no identity for.
        other.Git("config", "user.name", "Test");
        other.Git("config", "user.email", "test@example.com");
        other.Git("config", "commit.gpgsign", "false");
        other.Git("add", "-A");
        other.Git("commit", "-q", "-m", "first");
        var hostId = await Pair(other);
        IGitService git = new RemoteGitService(AppHost.Get<RemoteClientRuntime>(), hostId);

        Assert.Equal(other.Folder, Path.TrimEndingDirectorySeparator((await Wait(git.FindRepositoryRootAsync(Path.Combine(other.Folder, "src"))))!));
        var clean = (await Wait(git.GetStatusAsync(other.Folder)))!;
        Assert.Equal("main", clean.Branch);
        Assert.Empty(clean.Changes);
        Assert.Equal("first", (await Wait(git.GetLogAsync(other.Folder, 10))).Single().Subject);

        // A change made on the host shows up, with its diff; staged and committed from here by the host's git.
        File.WriteAllText(Path.Combine(other.Folder, "src", "main.cs"), "class Program { static void Main() { } }\n");
        File.WriteAllText(Path.Combine(other.Folder, "new.txt"), "new\n");
        var dirty = (await Wait(git.GetStatusAsync(other.Folder)))!;
        Assert.Equal(["new.txt", "src/main.cs"], dirty.Changes.Select(c => c.Path).Order());
        var diff = (await Wait(git.GetFileDiffAsync(other.Folder, "src/main.cs", staged: false)))!;
        Assert.Equal((1, 1), (diff.Added, diff.Removed));
        Assert.Contains(diff.Lines, line => line.Text.Contains("static void Main"));

        Assert.True((await Wait(git.StageAsync(other.Folder, ["src/main.cs"]))).Succeeded);
        Assert.Contains((await Wait(git.GetStatusAsync(other.Folder)))!.Changes, c => c is { Path: "src/main.cs", IsStaged: true });
        Assert.True((await Wait(git.CommitAsync(other.Folder, "add Main"))).Succeeded);
        Assert.Equal(["add Main", "first"], (await Wait(git.GetLogAsync(other.Folder, 10))).Select(c => c.Subject));
        var files = await Wait(git.GetCommitFilesAsync(other.Folder, (await Wait(git.GetLogAsync(other.Folder, 1)))[0].Hash));
        Assert.Equal("src/main.cs", files.Single().Path);

        Assert.True((await Wait(git.CreateBranchAsync(other.Folder, "feature"))).Succeeded);
        Assert.Contains(await Wait(git.GetBranchesAsync(other.Folder)), b => b is { Name: "feature", IsCurrent: true });
        Assert.True(await Wait(git.IsValidBranchNameAsync(other.Folder, "ok-name")));
        Assert.False(await Wait(git.IsValidBranchNameAsync(other.Folder, "bad name")));

        // A failing command comes back as a failure with git's own words; a folder that is no project of the host is refused.
        var failed = await Wait(git.SwitchBranchAsync(other.Folder, "no-such-branch", isRemote: false));
        Assert.False(failed.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(failed.Error));
        Assert.Null(await Wait(git.GetStatusAsync(Path.GetDirectoryName(other.Folder)!)));
        Assert.False((await Wait(git.CommitAsync("/", "nope"))).Succeeded);
    }

    [AvaloniaFact]
    public async Task The_right_panel_shows_a_remote_projects_files_and_changes_and_a_file_is_edited_in_place()
    {
        await using var other = new OtherComputer();
        other.Git("init", "-q", "-b", "main");
        other.Git("add", "-A");
        other.Git("commit", "-q", "-m", "first");
        var hostId = await Pair(other);
        var project = AppHost.Get<IProjectService>().AddRemoteProject(hostId, other.Project.Id, other.Folder, "studio-project");
        var panel = AppHost.Get<ExplorerPanelViewModel>();
        panel.IsActive = true;

        await Wait(panel.SetProjectAsync(project.Path, project.Name, null, hostId));
        await Until(() => panel.Files.Roots.Count == 2, "the tree shows the host's folder");
        Assert.Equal(["src", "README.md"], panel.Files.Roots.Select(node => node.Name));
        Assert.True(panel.IsGitRepository);
        Assert.True(panel.IsHostProject);
        panel.ShowSessionsCommand.Execute(null);
        Assert.False(panel.IsSessionsTab); // sessions are read from this computer: none for the host's project
        Assert.False(panel.IsSshProject);
        Assert.Null(panel.RemoteStatus);
        Assert.Equal(0, panel.ChangeCount);

        // Opening a file copies it here and shows it as the host's file; saving writes it back to the host.
        var item = (await Wait(AppHost.Get<IItemOpener>().OpenRemoteFileAsync(Path.Combine(other.Folder, "README.md"))))!;
        Assert.Equal(Path.Combine(other.Folder, "README.md"), item.RemotePath);
        Assert.Equal("# Studio project\n", File.ReadAllText(item.FilePath!));
        var copy = new RemoteCopy(AppHost.Get<RemoteProjectAccess>().TreeOf(project)!, item.RemotePath!, File.ReadAllBytes(item.FilePath!), AppHost.Get<IDialogService>());
        Assert.Equal(RemoteWriteOutcome.Written, (await Wait(copy.WriteAsync("# Edited from the laptop\n"u8.ToArray()))).Outcome);
        Assert.Equal("# Edited from the laptop\n", File.ReadAllText(Path.Combine(other.Folder, "README.md")));

        // The host tells that its folder changed: the panel counts the change without being asked.
        await Until(() => panel.ChangeCount == 1, "the change shows in source control");
        // The host changed the file meanwhile: saving again asks before overwriting (the test's dialogs say no).
        File.WriteAllText(Path.Combine(other.Folder, "README.md"), "# Changed on the host\n");
        AppHost.Get<FakeDialogService>().Answer = false;
        Assert.Equal(RemoteWriteOutcome.Declined, (await Wait(copy.WriteAsync("# Mine\n"u8.ToArray()))).Outcome);
        Assert.Equal("# Changed on the host\n", File.ReadAllText(Path.Combine(other.Folder, "README.md")));
    }
}
