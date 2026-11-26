using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.Agents;
using Octopus.App.Activity;
using Octopus.App.Composition;
using Octopus.App.Remote;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Core.Projects;
using Octopus.Core.Terminals;
using Octopus.Platform;
using Octopus.Remote.Client;
using Octopus.Remote.Host;
using Octopus.Remote.Protocol;
using Octopus.Remote.Security;
using Octopus.Remote.Transport;
using Octopus.Storage;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using TKCode.Terminal.Core;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>
/// A device and this app as its host, joined in memory: the real project service, launch planning and session
/// registry on the host side, the real client and a mirror terminal on the device side. Only the programs are made up
/// (design/12-远程操作.md, R2).
/// </summary>
public class RemoteEndToEndTests
{
    /// <summary>The programs the host "runs": the test writes their output and reads what was typed.</summary>
    private sealed class Programs : ITerminalConnectionFactory
    {
        public List<Program> Started { get; } = [];
        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows)
        {
            var program = new Program(spec, columns, rows);
            lock (Started) Started.Add(program);
            return program;
        }
        public Program Last { get { lock (Started) return Started[^1]; } }
        public int Count { get { lock (Started) return Started.Count; } }
    }

    private sealed class Program(LaunchSpec spec, int columns, int rows) : ITerminalConnection
    {
        private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource<TerminalExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly StringBuilder _typed = new();
        public LaunchSpec Spec { get; } = spec;
        public (int Columns, int Rows) StartSize { get; } = (columns, rows);
        public (int Columns, int Rows) Size { get; private set; } = (columns, rows);
        public string Typed { get { lock (_typed) return _typed.ToString(); } }
        public int ProcessId => 7;
        public Task<TerminalExit> Exited => _exit.Task;
        public void Print(string text) => _output.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public void Print(byte[] bytes) => _output.Writer.TryWrite(bytes);
        public void Exit(int code)
        {
            _output.Writer.TryComplete();
            _exit.TrySetResult(new TerminalExit(code, null));
        }
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var chunk in _output.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return chunk;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default)
        {
            lock (_typed) _typed.Append(Encoding.UTF8.GetString(input.Span));
            return ValueTask.CompletedTask;
        }
        public ValueTask ResizeAsync(int c, int r, int pixelWidth, int pixelHeight)
        {
            Size = (c, r);
            return ValueTask.CompletedTask;
        }
        public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;
        public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);
        public ValueTask DisposeAsync()
        {
            Exit(0);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The host side of the test: this app's services behind a host service, and the made-up programs.</summary>
    private sealed class HostSide : IAsyncDisposable
    {
        public DeviceIdentity Identity { get; } = DeviceIdentity.Create();
        public Programs Programs { get; } = new();
        public PairedPeerStore Devices { get; } = new(null);
        public HostWorkspace Workspace { get; }
        public RemoteHostService Service { get; }
        public IProjectService Projects { get; } = AppHost.Get<IProjectService>();
        public TerminalSessionRegistry Sessions { get; } = AppHost.Get<TerminalSessionRegistry>();

        public HostSide(string terminalBuild = "build-1", HelloMessage? hello = null)
        {
            Workspace = new HostWorkspace(Projects, AppHost.Get<IItemLaunchService>(), Programs, Sessions, AppHost.Get<ITabContentRegistry>(),
                AppHost.Get<IItemActivityService>(), AppHost.Get<IAgentCatalog>(), AppHost.Get<IAgentScanner>(), AppHost.Get<ISettingsStore>(), AppHost.Get<IShellResolver>(),
                AppHost.Get<Octopus.FileSystem.IFileTreeService>(), AppHost.Get<Octopus.Git.IGitService>(), AppHost.Get<Octopus.App.Common.PathComparer>());
            Service = new RemoteHostService(Identity, () => new DeviceDescription("Studio", DeviceKind.Desktop), Devices, Workspace, hello ?? Hello(terminalBuild));
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            Workspace.Dispose();
            await Sessions.CloseAllAsync();
            Identity.Dispose();
        }
    }

    private static HelloMessage Hello(string terminalBuild) => new()
    {
        AppVersion = "test",
        Capabilities = [RemoteMethods.CapabilityWorkspace, RemoteMethods.CapabilityNativeSnapshot],
        TerminalBuild = terminalBuild,
    };

    private static readonly DeviceDescription Laptop = new("Laptop", DeviceKind.Desktop);
    private static readonly LaunchSpec Remote = new("remote", [], null, new Dictionary<string, string>());

    /// <summary>Connects a paired device to the host.</summary>
    private static async Task<RemoteHostClient> Connect(HostSide host, DeviceIdentity device, string terminalBuild = "build-1", ConnectOptions? options = null, PeerExpectation? expected = null)
    {
        var (a, b) = InMemoryTransport.CreatePair();
        _ = host.Service.ServeAsync(b);
        return await Wait(RemoteHostClient.ConnectAsync(a, device, Laptop, expected ?? PeerExpectation.Paired(host.Identity.PublicKey), Hello(terminalBuild), options));
    }

    /// <summary>Waits while the UI thread keeps running what the host posts to it (the test itself is on that thread).</summary>
    private static async Task<T> Wait<T>(Task<T> task, int milliseconds = 8000)
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

    private static async Task Wait(Task task, int milliseconds = 8000) => await Wait(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }), milliseconds);

    private static async Task Until(Func<bool> done, string what, int milliseconds = 8000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!done() && Environment.TickCount64 < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(done(), what);
    }

    private static Project AddHostProject(string name = "octopus-remote-") =>
        AppHost.Get<IProjectService>().AddProject(Directory.CreateTempSubdirectory(name).FullName);

    private static TerminalSession Mirror(RemoteHostClient client, string itemId, int columns = 50, int rows = 15)
    {
        var mirror = new TerminalSession(Remote, client.OpenTerminal(itemId), new TerminalSize(columns, rows, 0, 0),
            TerminalPalettes.Dark, ColorScheme.Dark, 20_000, () => false)
        { IsMirror = true };
        mirror.Start();
        return mirror;
    }

    [AvaloniaFact]
    public async Task A_device_pairs_by_comparing_numbers_and_a_revoked_one_is_cut_off()
    {
        await using var host = new HostSide();
        using var device = DeviceIdentity.Create();

        // Not paired, and the host is not pairing: turned away.
        var (a, b) = InMemoryTransport.CreatePair();
        _ = host.Service.ServeAsync(b);
        var refused = await Assert.ThrowsAsync<HandshakeException>(() => Wait(RemoteHostClient.ConnectAsync(a, device, Laptop,
            PeerExpectation.AnyForPairing, Hello("build-1"), new ConnectOptions { RequestPairing = true })));
        Assert.Equal(HandshakeStatus.PairingClosed, refused.Status);

        // The person opens the pairing window; the device asks, both show the same number, the person allows.
        host.Service.OpenPairing(TimeSpan.FromMinutes(5));
        var shown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        (a, b) = InMemoryTransport.CreatePair();
        _ = host.Service.ServeAsync(b);
        var connecting = RemoteHostClient.ConnectAsync(a, device, Laptop, PeerExpectation.AnyForPairing, Hello("build-1"),
            new ConnectOptions { RequestPairing = true, OnPending = shown.SetResult });
        var digits = await Wait(shown.Task);
        await Until(() => host.Service.PairingRequests.Count == 1, "the request reaches the host");
        var request = host.Service.PairingRequests[0];
        Assert.Equal(digits, request.Sas);
        Assert.Equal("Laptop", request.Device.Name);
        Assert.False(connecting.IsCompleted);
        request.Allow();
        var client = await Wait(connecting);

        Assert.True(client.WasPaired);
        Assert.Equal(host.Identity.PublicKey, client.Host);
        Assert.Equal("Studio", client.HostDescription.Name);
        Assert.Equal(Environment.MachineName, client.Info.Name);
        Assert.Equal(device.Id, host.Devices.Peers.Single().Id);
        Assert.Empty(host.Service.PairingRequests);
        await Until(() => host.Service.Connections.Count == 1, "the device shows as connected");
        Assert.Equal("Laptop", host.Service.Connections[0].Device.Name);

        // Paired now: it comes in again without anyone being asked, also with the window closed.
        host.Service.ClosePairing();
        await using var again = await Connect(host, device);
        Assert.False(again.WasPaired);

        // Revoked: both connections end at once, and it is not let in again.
        await host.Service.RevokeAsync(device.Id);
        await Wait(client.Completion);
        await Wait(again.Completion);
        Assert.Empty(host.Service.Connections);
        (a, b) = InMemoryTransport.CreatePair();
        _ = host.Service.ServeAsync(b);
        var unknown = await Assert.ThrowsAsync<HandshakeException>(() => Wait(RemoteHostClient.ConnectAsync(a, device, Laptop, PeerExpectation.Paired(host.Identity.PublicKey), Hello("build-1"))));
        Assert.Equal(HandshakeStatus.UnknownDevice, unknown.Status);
    }

    [AvaloniaFact]
    public async Task Only_three_devices_may_ask_in_one_pairing_window_and_a_link_pairs_without_asking()
    {
        await using var host = new HostSide();
        var window = host.Service.OpenPairing(TimeSpan.FromMinutes(5));
        var link = host.Service.CreatePairingLink(["ws://192.168.1.5:47600"])!;
        Assert.Equal(host.Identity.Id, link.DeviceId);

        // The link: the host's fingerprint and the one-time token. No person is asked.
        using var phone = DeviceIdentity.Create();
        await using (var viaLink = await Connect(host, phone, options: new ConnectOptions { RequestPairing = true, Token = link.Token },
                         expected: PeerExpectation.WithFingerprint(link.Fingerprint)))
        {
            Assert.True(viaLink.WasPaired);
            Assert.Empty(host.Service.PairingRequests);
            Assert.Null(host.Service.CreatePairingLink([])); // spent
        }

        // Three strangers may wait for the person; the fourth is turned away at once.
        var waiting = new List<Task<RemoteHostClient>>();
        var identities = new List<DeviceIdentity>();
        for (var i = 0; i < PairingWindow.MaxRequests; i++)
        {
            identities.Add(DeviceIdentity.Create());
            var (a, b) = InMemoryTransport.CreatePair();
            _ = host.Service.ServeAsync(b);
            waiting.Add(RemoteHostClient.ConnectAsync(a, identities[i], Laptop, PeerExpectation.AnyForPairing, Hello("build-1"), new ConnectOptions { RequestPairing = true }));
        }
        await Until(() => host.Service.PairingRequests.Count == PairingWindow.MaxRequests, "three requests wait");
        using var fourth = DeviceIdentity.Create();
        var (x, y) = InMemoryTransport.CreatePair();
        _ = host.Service.ServeAsync(y);
        var closed = await Assert.ThrowsAsync<HandshakeException>(() => Wait(RemoteHostClient.ConnectAsync(x, fourth, Laptop, PeerExpectation.AnyForPairing, Hello("build-1"), new ConnectOptions { RequestPairing = true })));
        Assert.Equal(HandshakeStatus.PairingClosed, closed.Status);

        // Closing the window refuses those who waited.
        host.Service.ClosePairing();
        foreach (var task in waiting) Assert.Equal(HandshakeStatus.PairingRefused, (await Assert.ThrowsAsync<HandshakeException>(() => Wait(task))).Status);
        Assert.Equal(phone.Id, host.Devices.Peers.Single().Id);
        foreach (var identity in identities) identity.Dispose();
    }

    [AvaloniaFact]
    public async Task A_device_sees_the_hosts_projects_and_adds_to_them_without_taking_the_hosts_screen()
    {
        await using var host = new HostSide();
        using var device = DeviceIdentity.Create();
        host.Devices.Add(device.PublicKey, Laptop);
        var mine = AddHostProject();
        var front = AppHost.Get<IItemOpener>().OpenTerminal()!;
        host.Projects.AddProject("~/code/app", "on-ssh", sshHost: "build-box", activate: false);
        await using var client = await Connect(host, device);
        var changes = 0;
        client.WorkspaceChanged += () => Interlocked.Increment(ref changes);

        // The host's own folders, not what it reaches through ssh.
        var projects = await Wait(client.ListProjectsAsync());
        Assert.Equal([mine.Id], projects.Select(p => p.Id));
        Assert.Equal(mine.Path, projects[0].Path);
        var items = await Wait(client.ListItemsAsync(mine.Id));
        Assert.Equal([front.Id], items.Select(i => i.Id));
        Assert.Equal(ItemKinds.Terminal, items[0].Kind);
        Assert.False(items[0].IsRunning);

        // A new project in a folder of the host: there afterwards, and the host still looks at its own.
        var folder = Path.Combine(Directory.CreateTempSubdirectory("octopus-remote-new-").FullName, "made", "by-device");
        var missing = await Assert.ThrowsAsync<RemoteException>(() => Wait(client.CreateProjectAsync(folder)));
        Assert.Equal(RemoteMethods.NotFound, missing.Code);
        var created = await Wait(client.CreateProjectAsync(folder, "From the laptop", createFolder: true));
        Assert.True(Directory.Exists(folder));
        Assert.Equal("From the laptop", created.Name);
        Assert.Contains(host.Projects.Projects, p => p.Id == created.Id && p.Path == folder);
        Assert.Same(mine, host.Projects.Current);
        Assert.Equal(RemoteProtocol.InvalidParams, (await Assert.ThrowsAsync<RemoteException>(() => Wait(client.CreateProjectAsync("relative/path")))).Code);

        // A terminal in the host's current project: added behind the tab the person there has in front.
        var terminal = await Wait(client.CreateItemAsync(new CreateItemMessage { ProjectId = mine.Id, Kind = ItemKinds.Terminal }));
        Assert.Contains(mine.Items, i => i.Id == terminal.Id && i.Kind == ProjectItemKind.Terminal);
        Assert.Equal(front.Id, mine.ActiveItemId);
        await Wait(client.RenameItemAsync(terminal.Id, "build watcher"));
        Assert.Equal("build watcher", mine.Items.Single(i => i.Id == terminal.Id).DisplayTitle);
        await Until(() => changes >= 2, "the device is told that the workspace changed");

        // Removed again; ids that do not exist are told apart from failures.
        await Wait(client.RemoveItemAsync(terminal.Id));
        Assert.DoesNotContain(mine.Items, i => i.Id == terminal.Id);
        Assert.Equal(RemoteMethods.NotFound, (await Assert.ThrowsAsync<RemoteException>(() => Wait(client.RemoveItemAsync("nope")))).Code);
        Assert.Equal(RemoteMethods.NotFound, (await Assert.ThrowsAsync<RemoteException>(() => Wait(client.ListItemsAsync("nope")))).Code);
    }

    [AvaloniaFact]
    public async Task An_agent_that_runs_as_a_gui_conversation_says_so_and_has_no_terminal_to_attach_to()
    {
        await using var host = new HostSide();
        using var device = DeviceIdentity.Create();
        host.Devices.Add(device.PublicKey, Laptop);
        var mine = AddHostProject();
        var gui = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "接口文档", Surface = Octopus.Core.Agents.AgentSurface.Gui };
        var cli = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "重构" };
        host.Projects.AddItem(mine, gui, activate: false);
        host.Projects.AddItem(mine, cli, activate: false);
        await using var client = await Connect(host, device);

        var items = await Wait(client.ListItemsAsync(mine.Id));
        Assert.Equal(ItemSurfaces.Gui, items.Single(i => i.Id == gui.Id).Surface);
        Assert.Equal(ItemSurfaces.Terminal, items.Single(i => i.Id == cli.Id).Surface);

        // Planning it as a terminal would start a second copy of the agent beside the GUI tab's own.
        var refused = await Assert.ThrowsAsync<RemoteException>(() => Wait(client.RestartItemAsync(gui.Id)));
        Assert.Equal(RemoteMethods.NotAllowed, refused.Code);
        Assert.Null(host.Sessions.Find(gui.Id));
    }

    [AvaloniaFact]
    public async Task A_picture_for_a_terminal_is_kept_in_the_hosts_temporary_folder_and_its_path_given_back()
    {
        await using var host = new HostSide();
        using var device = DeviceIdentity.Create();
        host.Devices.Add(device.PublicKey, Laptop);
        var mine = AddHostProject();
        var shell = new ProjectItem { Kind = ProjectItemKind.Terminal, Title = "shell" };
        var gui = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "接口文档", Surface = Octopus.Core.Agents.AgentSurface.Gui };
        host.Projects.AddItem(mine, shell, activate: false);
        host.Projects.AddItem(mine, gui, activate: false);
        await using var client = await Connect(host, device);
        Assert.Contains(RemoteMethods.CapabilityUpload, RemoteHostRuntime.Hello.Capabilities);

        // More than one piece: the pieces collect on the host and the last one is answered with the whole picture's path.
        var picture = new byte[RemoteMethods.FilePieceBytes * 2 + 99];
        Random.Shared.NextBytes(picture);
        var shares = new List<double>();
        var path = await Wait(client.UploadAsync(shell.Id, "IMG_0001.JPG", picture, new SyncProgress(shares.Add)));
        try
        {
            Assert.Equal(Path.GetFullPath(ClipboardImages.DefaultFolder), Path.GetDirectoryName(path));
            Assert.Matches(@"^paste-\d{8}-\d{6}-\d{3}(-\d+)?\.jpg$", Path.GetFileName(path));
            Assert.Equal(picture, File.ReadAllBytes(path));
            // One report a piece, rising to the whole.
            Assert.Equal(3, shares.Count);
            Assert.Equal((double)RemoteMethods.FilePieceBytes / picture.Length, shares[0], 6);
            Assert.Equal(1.0, shares[^1]);
            // Nothing of it is left half-done beside it.
            Assert.DoesNotContain(Directory.EnumerateFiles(ClipboardImages.DefaultFolder, "upload-*.part"), f => File.GetLastWriteTimeUtc(f) > DateTime.UtcNow.AddMinutes(-1));
        }
        finally { File.Delete(path); }

        // Only pictures, only for a terminal of the host, and never larger than a picture may be.
        Assert.Equal(RemoteProtocol.InvalidParams, (await Assert.ThrowsAsync<RemoteException>(() => Wait(client.UploadAsync(shell.Id, "notes.sh", [1, 2, 3])))).Code);
        Assert.Equal(RemoteMethods.NotAllowed, (await Assert.ThrowsAsync<RemoteException>(() => Wait(client.UploadAsync(gui.Id, "a.png", [1, 2, 3])))).Code);
        Assert.Equal(RemoteMethods.NotFound, (await Assert.ThrowsAsync<RemoteException>(() => Wait(client.UploadAsync("nope", "a.png", [1, 2, 3])))).Code);
        await Assert.ThrowsAsync<InvalidDataException>(() => Wait(client.UploadAsync(shell.Id, "big.png", new byte[RemoteMethods.UploadMaxBytes + 1])));
    }

    /// <summary>Reports at once, on the reporting thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [AvaloniaFact]
    public async Task An_agent_is_started_on_the_host_from_the_hosts_own_catalog()
    {
        await using var host = new HostSide();
        using var device = DeviceIdentity.Create();
        host.Devices.Add(device.PublicKey, Laptop);
        var project = AddHostProject();
        await using var client = await Connect(host, device);

        var agents = await Wait(client.ListAgentsAsync());
        var claude = agents.Single(a => a.Id == "claude");
        Assert.NotEmpty(claude.Models);
        Assert.True(claude.TakesPrompt);
        Assert.DoesNotContain(agents, a => a.Id == "gemini"); // not installed on the host

        var item = await Wait(client.CreateItemAsync(new CreateItemMessage
        {
            ProjectId = project.Id,
            Kind = ItemKinds.Agent,
            AgentId = "claude",
            Model = claude.Models[0].Id,
            Prompt = "fix the resize bug",
        }));
        Assert.Equal(ItemKinds.Agent, item.Kind);
        var onHost = project.Items.Single(i => i.Id == item.Id);
        Assert.Equal("fix the resize bug", onHost.Launch!.Prompt);
        Assert.Equal(RemoteMethods.NotFound, (await Assert.ThrowsAsync<RemoteException>(() => Wait(client.CreateItemAsync(
            new CreateItemMessage { ProjectId = project.Id, Kind = ItemKinds.Agent, AgentId = "gemini" })))).Code);

        // Showing it starts it on the host, with the command line the host itself builds for that agent.
        await using var mirror = Mirror(client, item.Id);
        await Until(() => host.Programs.Count == 1, "the host starts the agent");
        var command = string.Join(' ', host.Programs.Last.Spec.Arguments);
        Assert.Contains("claude", command);
        Assert.Contains("fix the resize bug", command);
        Assert.Equal(project.Path, host.Programs.Last.Spec.WorkingDirectory);

        // What the agent reports about itself on the host reaches the device.
        var seen = new List<ItemMessage>();
        client.ItemChanged += changed => { lock (seen) seen.Add(changed); };
        AppHost.Get<IItemActivityService>().ReportTitle(item.Id, "⠋ Fixing the resize bug");
        await Until(() => { lock (seen) return seen.Any(i => i.Id == item.Id && i.Activity == ItemActivities.Working); }, "the activity reaches the device");
    }

    [AvaloniaTheory]
    [InlineData("build-1")] // the same terminal library on both sides: its own snapshot
    [InlineData("build-2")] // another one: VT text
    public async Task A_terminal_on_the_host_is_shown_typed_into_and_resized_from_the_device(string deviceBuild)
    {
        await using var host = new HostSide();
        using var device = DeviceIdentity.Create();
        host.Devices.Add(device.PublicKey, Laptop);
        var project = AddHostProject();
        await using var client = await Connect(host, device, deviceBuild);
        Assert.Equal(deviceBuild == "build-1", client.NativeSnapshots);
        var item = await Wait(client.CreateItemAsync(new CreateItemMessage { ProjectId = project.Id, Kind = ItemKinds.Terminal }));

        // No tab ever showed it on the host: the program starts now, in a terminal of the device's size.
        await using var mirror = Mirror(client, item.Id, 50, 15);
        await Until(() => host.Programs.Count == 1, "the host starts the program");
        var program = host.Programs.Last;
        Assert.Equal((50, 15), program.StartSize);
        var session = host.Sessions.Find(item.Id)!;
        Assert.NotNull(session.SizeOwner);

        program.Print("hello from the host\r\n$ ");
        await Until(() => mirror.ReadText().Contains("hello from the host"), "output reaches the device");
        mirror.Send("ls -la\r");
        await Until(() => program.Typed == "ls -la\r", "typing reaches the program");

        // A second view (another tab, another device) joins later and sees what is already there.
        await using (var second = Mirror(client, item.Id, 50, 15))
        {
            await Until(() => second.ReadText().Contains("hello from the host"), "a later view gets the screen so far");
            program.Print("both see this");
            await Until(() => mirror.ReadText().Contains("both see this") && second.ReadText().Contains("both see this"), "both views follow");
            // The view that came last has the say over the size.
            second.Resize(new TerminalSize(44, 12, 0, 0));
            await Until(() => session.Size is { Columns: 44, Rows: 12 } && mirror.Size is { Columns: 44, Rows: 12 }, "the later view decides the size");
        }
        // It left: the say and the size go back to the view that had them before.
        await Until(() => session.Size is { Columns: 50, Rows: 15 } && mirror.Size is { Columns: 50, Rows: 15 }, "the first view's size comes back");

        // The device's view changes size: it has the say, so the terminal follows, and the device's own grid with it.
        mirror.Resize(new TerminalSize(64, 20, 0, 0));
        await Until(() => session.Size is { Columns: 64, Rows: 20 } && mirror.Size is { Columns: 64, Rows: 20 }, "the device's size is applied");
        Assert.Equal((64, 20), program.Size);

        // The person at the host uses the terminal there: its size is the window's now, and the device follows that.
        session.ClaimSize(null, new TerminalSize(120, 40, 0, 0));
        await Until(() => mirror.Size is { Columns: 120, Rows: 40 }, "the device follows the host's size");
        mirror.Resize(new TerminalSize(70, 22, 0, 0));
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(120, session.Size.Columns); // asking is not enough while the host's view has the say

        // Typing on the device is using it: the say comes back to the device's view.
        mirror.Send("x");
        await Until(() => session.Size is { Columns: 70, Rows: 22 } && mirror.Size is { Columns: 70, Rows: 22 }, "typing takes the size back");
        await Until(() => program.Typed.EndsWith('x'), "and the key still arrives");

        // The device stops showing it: the program keeps running, and the window's view decides the size again.
        await mirror.DisposeAsync();
        await Until(() => session.SizeOwner is null, "the say goes back to the host");
        Assert.True(session.IsRunning);
        Assert.False(program.Exited.IsCompleted);
    }

    [AvaloniaFact]
    public async Task A_view_that_only_looks_leaves_the_size_alone_until_it_claims_it_and_can_give_it_back()
    {
        await using var host = new HostSide();
        using var device = DeviceIdentity.Create();
        host.Devices.Add(device.PublicKey, Laptop);
        var project = AddHostProject();
        await using var client = await Connect(host, device, "build-1");
        var item = await Wait(client.CreateItemAsync(new CreateItemMessage { ProjectId = project.Id, Kind = ItemKinds.Terminal }));
        // The terminal runs at the size the host's own window gave it.
        await using (Mirror(client, item.Id, 100, 30)) await Until(() => host.Programs.Count == 1, "the host starts the program");
        var session = host.Sessions.Find(item.Id)!;
        await Until(() => session.SizeOwner is null, "the say is the host's own again");
        session.ClaimSize(null, new TerminalSize(100, 30, 0, 0));

        // A phone looks: at the terminal's size, whatever its own view would hold.
        var view = client.ViewTerminal(item.Id);
        var sizes = new System.Collections.Concurrent.ConcurrentQueue<(int Columns, int Rows, bool Owns)>();
        view.SizeChanged += (columns, rows, owns) => sizes.Enqueue((columns, rows, owns));
        await using var mirror = new TerminalSession(Remote, view.Factory, new TerminalSize(40, 20, 0, 0), TerminalPalettes.Dark, ColorScheme.Dark, 20_000, () => false) { IsMirror = true };
        mirror.Start();
        await Until(() => mirror.Size is { Columns: 100, Rows: 30 }, "the view shows the terminal at the host's size");
        Assert.False(view.OwnsSize);
        Assert.Null(session.SizeOwner);
        Assert.Contains((100, 30, false), sizes);
        // Its own view changing size asks for nothing that is applied.
        mirror.Resize(new TerminalSize(42, 20, 0, 0));
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(100, session.Size.Columns);

        // "Fit to this phone".
        await Wait(view.ClaimSizeAsync(42, 20));
        await Until(() => session.Size is { Columns: 42, Rows: 20 } && mirror.Size is { Columns: 42, Rows: 20 }, "the phone's size is applied");
        Assert.True(view.OwnsSize);
        Assert.NotNull(session.SizeOwner);

        // Given back while still looking: the host's window decides again, and the view follows what that makes of it.
        await Wait(view.ReleaseSizeAsync());
        await Until(() => session.SizeOwner is null && !view.OwnsSize, "the say went back to the host");
        session.ClaimSize(null, new TerminalSize(100, 30, 0, 0));
        await Until(() => mirror.Size is { Columns: 100, Rows: 30 }, "the view follows the host's size again");
        Assert.True(session.IsRunning);
        // The app says it can do this, so a phone need not find out by trying.
        Assert.Contains(RemoteMethods.CapabilitySizeRelease, RemoteHostRuntime.Hello.Capabilities);
    }

    [AvaloniaFact]
    public async Task A_full_screen_program_and_the_shell_under_it_arrive_whole_and_its_end_is_reported()
    {
        await using var host = new HostSide();
        using var device = DeviceIdentity.Create();
        host.Devices.Add(device.PublicKey, Laptop);
        var project = AddHostProject();
        await using var client = await Connect(host, device);
        var item = await Wait(client.CreateItemAsync(new CreateItemMessage { ProjectId = project.Id, Kind = ItemKinds.Terminal }));
        await using var first = Mirror(client, item.Id);
        await Until(() => host.Programs.Count == 1, "the program starts");
        var program = host.Programs.Last;
        program.Print("shell line before\r\n\u001b[?1049h\u001b[2J\u001b[1;1Heditor screen");
        await Until(() => first.ReadText().Contains("editor screen"), "the editor shows");

        // Joining while the editor runs, then the editor leaves: the shell's screen is there (the native snapshot carried it).
        await using var late = Mirror(client, item.Id);
        await Until(() => late.ReadText().Contains("editor screen"), "the later view shows the editor");
        Assert.True(late.ReadInputModes().AlternateScreen);
        program.Print("\u001b[?1049l");
        await Until(() => !late.ReadInputModes().AlternateScreen, "the editor leaves");
        Assert.Contains("shell line before", late.ReadText());

        // More than one frame's worth of output in one go arrives complete and in order.
        var big = new StringBuilder();
        for (var i = 0; i < 4000; i++) big.Append("line ").Append(i.ToString("0000")).Append(" of a long build log\r\n");
        program.Print(big.ToString() + "END-OF-LOG");
        await Until(() => first.ReadText().Contains("END-OF-LOG"), "a long output arrives");
        Assert.Equal(host.Sessions.Find(item.Id)!.ReadText(), first.ReadText());

        // The program ends: the device's terminal reports the exit code; starting it again runs the program again on the host.
        var ended = new TaskCompletionSource<TerminalExit>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Exited += ended.SetResult;
        program.Exit(3);
        Assert.Equal(3, (await Wait(ended.Task)).ExitCode);
        await Wait(first.RestartAsync());
        await Until(() => host.Programs.Count == 2, "the host runs the program again");
        host.Programs.Last.Print("second run");
        await Until(() => first.ReadText().Contains("second run"), "the new run shows");
        Assert.DoesNotContain("END-OF-LOG", first.ReadText());
    }

    [AvaloniaFact]
    public async Task Removing_an_item_from_the_device_ends_its_program_on_the_host()
    {
        await using var host = new HostSide();
        using var device = DeviceIdentity.Create();
        host.Devices.Add(device.PublicKey, Laptop);
        var project = AddHostProject();
        await using var client = await Connect(host, device);
        var item = await Wait(client.CreateItemAsync(new CreateItemMessage { ProjectId = project.Id, Kind = ItemKinds.Terminal }));
        await using var mirror = Mirror(client, item.Id);
        await Until(() => host.Programs.Count == 1, "the program starts");
        Assert.True((await Wait(client.ListItemsAsync(project.Id))).Single().IsRunning);

        await Wait(client.RemoveItemAsync(item.Id));

        await Until(() => host.Programs.Last.Exited.IsCompleted, "the program is ended");
        Assert.Null(host.Sessions.Find(item.Id));
        Assert.Empty(project.Items);
    }

    [AvaloniaFact]
    public async Task A_device_that_does_not_keep_up_is_cut_off_from_that_terminal_and_gets_it_back_whole()
    {
        await using var host = new HostSide();
        using var device = DeviceIdentity.Create();
        host.Devices.Add(device.PublicKey, Laptop);
        var project = AddHostProject();
        var item = AppHost.Get<IItemOpener>().OpenTerminal()!;

        // A device that attaches and then never says how far it got, at the level of the protocol.
        var (a, b) = InMemoryTransport.CreatePair();
        _ = host.Service.ServeAsync(b);
        var channel = await Wait(SecureChannel.ConnectAsync(a, device, Laptop, PeerExpectation.Paired(host.Identity.PublicKey)));
        await using var raw = new RemoteConnection(channel);
        var resync = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        long received = 0;
        raw.On(RemoteMethods.SessionResync, RemoteJsonContext.Default.ChannelMessage, message => resync.TrySetResult(message.Channel));
        raw.DataReceived = (header, payload) => { if (header.Type == FrameType.Output) Interlocked.Add(ref received, payload.Length - 8); };
        raw.Start();
        await Wait(RemoteHello.SendAsync(raw, Hello("build-1")));
        var attached = await Wait(raw.RequestAsync(RemoteMethods.SessionAttach,
            new AttachMessage { ItemId = item.Id, Channel = 5, Columns = 80, Rows = 24, Snapshot = SnapshotFormats.Vt },
            RemoteJsonContext.Default.AttachMessage, RemoteJsonContext.Default.AttachedMessage));
        Assert.Equal(5u, attached.Channel);
        await Until(() => host.Programs.Count == 1, "the program starts");
        var program = host.Programs.Last;

        // Three megabytes with no acknowledgement: the host stops at about one, tells the device, and the program is not held up.
        var chunk = Encoding.ASCII.GetBytes(new string('x', 64 * 1024 - 2) + "\r\n");
        for (var i = 0; i < 48; i++) program.Print(chunk);
        program.Print("AFTER-THE-FLOOD");
        Assert.Equal(5u, await Wait(resync.Task));
        var session = host.Sessions.Find(item.Id)!;
        await Until(() => session.ReadText().Contains("AFTER-THE-FLOOD"), "the program's output was all taken on the host");
        Assert.InRange(Interlocked.Read(ref received), 1, 1_200_000);

        // The channel is free again, and a fresh attachment has the whole screen.
        await using var client = await Connect(host, device);
        await using var mirror = Mirror(client, item.Id, 80, 24);
        await Until(() => mirror.ReadText().Contains("AFTER-THE-FLOOD"), "a fresh view has everything");
    }

    [AvaloniaFact]
    public async Task Protocol_v2_streams_files_resumes_terminals_from_ring_buffer_and_upgrades_live_to_webrtc_p2p()
    {
        await using var host = new HostSide(hello: RemoteHostRuntime.Hello);
        using var device = DeviceIdentity.Create();
        host.Devices.Add(device.PublicKey, Laptop);
        var project = AddHostProject();

        // Connect with full v2 capabilities (compression, binary file streams, session resume, WebRTC P2P upgrade).
        var (a, b) = InMemoryTransport.CreatePair();
        _ = host.Service.ServeAsync(b);
        await using var client = await Wait(RemoteHostClient.ConnectAsync(a, device, Laptop, PeerExpectation.Paired(host.Identity.PublicKey), RemoteHostRuntime.Hello));

        Assert.True(client.SupportsFileStream);
        Assert.True(client.SupportsSessionResume);
        Assert.True(client.SupportsTransportP2p);

        // 1. Live WebRTC P2P UDP upgrade: migrates the active connection from InMemoryTransport to a real UDP WebRtcP2pTransport!
        await Wait(client.UpgradeToP2pAsync(device, Laptop, loopbackOnly: true));
        Assert.Equal(1, client.Connection.MigrationCount);

        // 2. Binary file streaming over the migrated P2P connection (multi-chunk, zero Base64).
        var fileBytes = new byte[RemoteMethods.StreamChunkBytes * 3 + 777];
        Random.Shared.NextBytes(fileBytes);
        var filePath = Path.Combine(project.Path, "streamed.bin");
        await Wait(client.WriteFileAsync(filePath, fileBytes));
        Assert.Equal(fileBytes, File.ReadAllBytes(filePath));
        var readBack = await Wait(client.ReadFileAsync(filePath, 5 * 1024 * 1024));
        Assert.Equal(fileBytes, readBack);

        // 3. Binary image upload streaming with progress reporting.
        var shell = await Wait(client.CreateItemAsync(new CreateItemMessage { ProjectId = project.Id, Kind = ItemKinds.Terminal }));
        var picBytes = new byte[RemoteMethods.FilePieceBytes * 2 + 123];
        Random.Shared.NextBytes(picBytes);
        var progressReports = new List<double>();
        var uploadedPath = await Wait(client.UploadAsync(shell.Id, "p2p.png", picBytes, new SyncProgress(progressReports.Add)));
        try
        {
            Assert.Equal(picBytes, File.ReadAllBytes(uploadedPath));
            Assert.Equal(3, progressReports.Count);
            Assert.Equal(1.0, progressReports[^1]);
        }
        finally { File.Delete(uploadedPath); }

        // 4. Terminal ring-buffer delta resume via ResumeFrom.
        await using var mirror = Mirror(client, shell.Id, 60, 15);
        await Until(() => host.Programs.Count == 1, "program starts");
        var program = host.Programs.Last;
        program.Print("line before gap\r\n");
        await Until(() => mirror.ReadText().Contains("line before gap"), "initial output arrives");

        // Direct protocol check of ResumeFrom on the same session:
        var session = host.Sessions.Find(shell.Id)!;
        program.Print("line during gap\r\n");
        await Until(() => session.ReadText().Contains("line during gap"), "host processes gap output");
        var resumedAttach = await Wait(client.Connection.RequestAsync(RemoteMethods.SessionAttach,
            new AttachMessage { ItemId = shell.Id, Channel = 99, Columns = 60, Rows = 15, Snapshot = SnapshotFormats.Native, ResumeFrom = 0 },
            RemoteJsonContext.Default.AttachMessage, RemoteJsonContext.Default.AttachedMessage));
        Assert.True(resumedAttach.Resumed);
        Assert.Equal(0, resumedAttach.SnapshotLength);
    }
}
