using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Octopus.Agents;
using Octopus.App.Activity;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Dialogs;
using Octopus.App.Launcher;
using Octopus.App.Remote;
using Octopus.App.Services;
using Octopus.App.Settings.Remote;
using Octopus.App.Shell;
using Octopus.App.Terminals;
using Octopus.App.Workspace;
using Octopus.Core.Projects;
using Octopus.Core.Terminals;
using Octopus.Platform;
using Octopus.Remote.Client;
using Octopus.Remote.Host;
using Octopus.Remote.Security;
using Octopus.Remote.Transport;
using Octopus.Storage;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>
/// This app as a device of another computer's Octopus (design/12-远程操作.md §7.2): pairing from the settings page, a
/// project on the host on the rail, its terminal in a tab, and what happens when the connection drops. The "other
/// computer" is a second project service and host service in the same process, reached through an in-memory transport.
/// </summary>
public class RemoteProjectTests
{
    private sealed class Programs : ITerminalConnectionFactory
    {
        private readonly List<Program> _started = [];
        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows)
        {
            var program = new Program(spec, (columns, rows));
            lock (_started) _started.Add(program);
            return program;
        }
        public int Count { get { lock (_started) return _started.Count; } }
        public Program Last { get { lock (_started) return _started[^1]; } }
    }

    private sealed class Program(LaunchSpec spec, (int Columns, int Rows) size) : ITerminalConnection
    {
        private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource<TerminalExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly StringBuilder _typed = new();
        public LaunchSpec Spec { get; } = spec;
        public (int Columns, int Rows) StartSize { get; } = size;
        public string Typed { get { lock (_typed) return _typed.ToString(); } }
        public int ProcessId => 9;
        public Task<TerminalExit> Exited => _exit.Task;
        public void Print(string text) => _output.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var chunk in _output.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return chunk;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default)
        {
            lock (_typed) _typed.Append(Encoding.UTF8.GetString(input.Span));
            return ValueTask.CompletedTask;
        }
        public ValueTask ResizeAsync(int c, int r, int pixelWidth, int pixelHeight) => ValueTask.CompletedTask;
        public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;
        public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);
        public ValueTask DisposeAsync()
        {
            _output.Writer.TryComplete();
            _exit.TrySetResult(new TerminalExit(0, null));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The other computer: its own projects and sessions, its own device key, and the door devices knock on.</summary>
    private sealed class OtherComputer : IAsyncDisposable
    {
        public DeviceIdentity Identity { get; } = DeviceIdentity.Create();
        public Programs Programs { get; } = new();
        public ProjectService Projects { get; }
        public TerminalSessionRegistry Sessions { get; } = new();
        public HostWorkspace Workspace { get; }
        public RemoteHostService Service { get; }
        /// <summary>False refuses new connections: the computer is asleep or off the network.</summary>
        public bool Reachable { get; set; } = true;
        public int Dials { get; private set; }

        public OtherComputer()
        {
            Projects = new ProjectService(new InMemoryProjectStore(), AppHost.Get<PathComparer>());
            Workspace = new HostWorkspace(Projects, AppHost.Get<IItemLaunchService>(), Programs, Sessions, AppHost.Get<ITabContentRegistry>(),
                AppHost.Get<IItemActivityService>(), AppHost.Get<IAgentCatalog>(), AppHost.Get<IAgentScanner>(), AppHost.Get<ISettingsStore>(), AppHost.Get<IShellResolver>(),
                AppHost.Get<Octopus.FileSystem.IFileTreeService>(), AppHost.Get<Octopus.Git.IGitService>(), AppHost.Get<Octopus.App.Common.PathComparer>());
            Service = new RemoteHostService(Identity, () => new DeviceDescription("Studio", DeviceKind.Desktop), new PairedPeerStore(null), Workspace, RemoteHostRuntime.Hello);
        }

        /// <summary>What this app's runtime dials instead of a socket.</summary>
        public Task<IMessageTransport> Dial(Uri address, CancellationToken cancellationToken)
        {
            Dials++;
            if (!Reachable) throw new IOException("Connection refused.");
            var (device, host) = InMemoryTransport.CreatePair();
            _ = Service.ServeAsync(host);
            return Task.FromResult<IMessageTransport>(device);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            Workspace.Dispose();
            await Sessions.CloseAllAsync();
            Identity.Dispose();
        }
    }

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

    private static Task Wait(Task task) => Wait(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));

    private static async Task Until(Func<bool> done, string what, int milliseconds = 8000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!done() && Environment.TickCount64 < end)
        {
            Frames.Pump();
            await Task.Delay(5);
        }
        Assert.True(done(), what);
    }

    /// <summary>Pairs this app with the other computer through the settings page, the person there allowing it.</summary>
    private static async Task<(RemoteHostsSettingsViewModel Page, string HostId)> Pair(OtherComputer other)
    {
        var runtime = AppHost.Get<RemoteClientRuntime>();
        runtime.Dial = other.Dial;
        var page = ActivatorUtilities.CreateInstance<RemoteHostsSettingsViewModel>(AppHost.Services);
        other.Service.OpenPairing(TimeSpan.FromMinutes(5));
        page.Target = "192.168.1.5";
        page.PairCommand.Execute(null);
        await Until(() => other.Service.PairingRequests.Count == 1 && page.HasSas, "both sides show the number");
        Assert.Equal(other.Service.PairingRequests[0].Sas, page.Sas!.Replace(" ", ""));
        other.Service.PairingRequests[0].Allow();
        await Wait(page.Pairing);
        other.Service.ClosePairing();
        return (page, other.Identity.Id);
    }

    [AvaloniaFact]
    public async Task Pairing_from_the_page_remembers_the_host_and_says_why_when_it_does_not_work()
    {
        await using var other = new OtherComputer();
        var runtime = AppHost.Get<RemoteClientRuntime>();
        runtime.Dial = other.Dial;
        using var page = ActivatorUtilities.CreateInstance<RemoteHostsSettingsViewModel>(AppHost.Services);
        Assert.False(page.HasHosts);

        // Not an address.
        page.Target = "not an address";
        page.PairCommand.Execute(null);
        await Wait(page.Pairing);
        Assert.Equal("这既不是地址，也不是配对链接。", page.ErrorText);

        // The host is there but nobody started pairing on it.
        page.Target = "192.168.1.5:47600";
        page.PairCommand.Execute(null);
        await Wait(page.Pairing);
        Assert.Equal("主机现在没有在配对。先在那边点“开始配对”。", page.ErrorText);
        Assert.False(page.HasHosts);

        // The person there refuses.
        other.Service.OpenPairing(TimeSpan.FromMinutes(5));
        page.PairCommand.Execute(null);
        await Until(() => other.Service.PairingRequests.Count == 1 && page.HasSas, "the number shows");
        other.Service.PairingRequests[0].Refuse();
        await Wait(page.Pairing);
        Assert.Equal("主机没有允许这台电脑。", page.ErrorText);
        Assert.Null(page.Sas);

        // The host cannot be reached at all.
        other.Reachable = false;
        page.PairCommand.Execute(null);
        await Wait(page.Pairing);
        Assert.StartsWith("连不上主机：", page.ErrorText);
        other.Reachable = true;

        // Allowed: remembered with its key and the address that worked, and connected.
        page.PairCommand.Execute(null);
        await Until(() => other.Service.PairingRequests.Count == 1 && page.HasSas, "the number shows again");
        other.Service.PairingRequests[0].Allow();
        await Wait(page.Pairing);
        Assert.Null(page.ErrorText);
        Assert.Equal("", page.Target);
        var row = Assert.Single(page.Hosts);
        Assert.Equal(("Studio", "ws://192.168.1.5:47600/", true), (row.Name, row.Address, row.IsConnected));
        Assert.Equal(other.Identity.PublicKey, runtime.Hosts.Find(other.Identity.Id)!.Key());
        Assert.Contains(other.Identity.Id, File.ReadAllText(Path.Combine(AppHost.Get<IAppPaths>().DataDirectory, "remote", "hosts.json")));

        // The address as people type it.
        Assert.Equal("ws://10.0.0.7:47600/", RemoteClientRuntime.ParseAddress("10.0.0.7")!.ToString());
        Assert.Equal("ws://mac.local:5000/", RemoteClientRuntime.ParseAddress(" mac.local:5000 ")!.ToString());
        Assert.Equal("wss://relay.example/", RemoteClientRuntime.ParseAddress("wss://relay.example")!.ToString());
        Assert.Null(RemoteClientRuntime.ParseAddress("http://x"));

        // Removed: forgotten, and the connection ends.
        await Wait(page.RemoveCommand.ExecuteAsync(page.Hosts[0]));
        Assert.Empty(page.Hosts);
        await Until(() => other.Service.Connections.Count == 0, "the host sees the device leave");
    }

    [AvaloniaFact]
    public async Task A_paired_host_is_connected_and_disconnected_from_its_row()
    {
        await using var other = new OtherComputer();
        var (page, hostId) = await Pair(other);
        using var _ = page;
        var runtime = AppHost.Get<RemoteClientRuntime>();
        var paired = Assert.Single(page.Hosts);
        Assert.True(paired.IsConnected);
        Assert.False(paired.CanConnect);

        // Ended from the row: the host sees the device leave, and the row offers to connect again.
        await Wait(page.DisconnectCommand.ExecuteAsync(paired));
        await Until(() => other.Service.Connections.Count == 0, "the host sees the device leave");
        Assert.Equal(RemoteHostState.Disconnected, runtime.StateOf(hostId));
        var stopped = Assert.Single(page.Hosts);
        Assert.False(stopped.IsConnected);
        Assert.True(stopped.CanConnect);

        // Asked for again from the row.
        await Wait(page.ConnectCommand.ExecuteAsync(stopped));
        await Until(() => other.Service.Connections.Count == 1, "the host sees the device come back");
        Assert.Equal(RemoteHostState.Connected, runtime.StateOf(hostId));
        Assert.True(Assert.Single(page.Hosts).IsConnected);
    }

    [AvaloniaFact]
    public async Task A_project_on_the_host_sits_on_the_rail_and_its_terminal_runs_there()
    {
        await using var other = new OtherComputer();
        var (page, hostId) = await Pair(other);
        using var _ = page;
        var projects = AppHost.Get<IProjectService>();
        var remote = AppHost.Get<RemoteProjects>();
        var window = new MainWindow();
        window.Show();
        Frames.Pump();

        // The project is made on the host, in a folder there, and appears here as the current project.
        var folder = Path.Combine(Directory.CreateTempSubdirectory("octopus-other-").FullName, "api");
        var project = await Wait(remote.AddAsync(hostId, folder, "API"));
        Assert.Equal((hostId, "API", folder), (project.RemoteHost, project.Name, project.Path));
        Assert.Same(project, projects.Current);
        var onHost = Assert.Single(other.Projects.Projects);
        Assert.Equal(onHost.Id, project.RemoteId);
        Assert.True(Directory.Exists(folder));
        Assert.Equal(ProjectLocationKind.Remote, ProjectLocation.KindOf(project));
        Assert.Single(page.Hosts);
        await Until(() => page.Hosts[0].ProjectsText == "这里有 1 个项目", "the host's row counts its projects here");
        // Its folder is the host's: the right panel is given the path together with the host to read it through.
        Assert.Equal(project.Path, AppHost.Get<ShellViewModel>().CurrentProjectPath);
        Assert.Equal(project.RemoteHost, AppHost.Get<ShellViewModel>().CurrentProjectRemoteHost);

        // A new terminal from this app's own menu is made on the host and comes to the front here.
        Assert.Null(AppHost.Get<IItemOpener>().OpenTerminal());
        await Until(() => project.Items.Count == 1, "the item arrives from the host");
        var item = project.Items[0];
        Assert.Equal(onHost.Items.Single().Id, item.Id);
        Assert.Equal(item.Id, project.ActiveItemId);

        // Its tab mirrors the terminal the host runs: started there, at this tab's size, typed into from here.
        await Until(() => other.Programs.Count == 1, "the host starts the program");
        var program = other.Programs.Last;
        Assert.True(program.StartSize.Columns > 20);
        var tab = window.GetVisualDescendants().OfType<TerminalTabView>().Single();
        var terminal = tab.GetVisualDescendants().OfType<TerminalControl>().Single();
        Assert.True(terminal.IsMirror);
        program.Print("hello from Studio\r\n$ ");
        await Until(() => terminal.ReadScreenText().Contains("hello from Studio"), "the host's output shows in the tab");
        terminal.SendText("pwd\r");
        await Until(() => program.Typed == "pwd\r", "typing reaches the host");
        // Nothing was started on this computer.
        Assert.Empty(AppHost.Get<FakeTerminalConnectionFactory>().Started);
        // Closing a tab whose program runs on the host always asks: this computer cannot see what runs there.
        Assert.True(await AppHost.Get<ITabContentRegistry>().Find(item.Id)!.HasRunningChildAsync());

        // The host's own window adds a terminal: it appears here, behind the tab in front.
        var theirs = new ProjectItem { Kind = ProjectItemKind.Terminal, Title = "zsh" };
        other.Projects.AddItem(onHost, theirs);
        await Until(() => project.Items.Count == 2, "the host's new item appears here");
        Assert.Equal(item.Id, project.ActiveItemId);
        // A name given on the host follows; a name given here is given on the host.
        other.Projects.RenameItem(theirs.Id, "logs");
        await Until(() => project.Items.Single(i => i.Id == theirs.Id).DisplayTitle == "logs", "the host's name follows");
        projects.RenameItem(item.Id, "main shell");
        await Until(() => onHost.Items.Single(i => i.Id == item.Id).CustomTitle == "main shell", "the name given here reaches the host");

        // Closing the tab here removes the item on the host and ends its program.
        projects.RemoveItem(item.Id);
        await Until(() => onHost.Items.All(i => i.Id != item.Id) && program.Exited.IsCompleted, "the host removes the item and ends the program");
        Assert.Single(project.Items);
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_agent_in_a_remote_project_comes_from_the_hosts_catalog()
    {
        await using var other = new OtherComputer();
        var (page, hostId) = await Pair(other);
        using var _ = page;
        var runtime = AppHost.Get<RemoteClientRuntime>();
        var project = await Wait(AppHost.Get<RemoteProjects>().AddAsync(hostId, Directory.CreateTempSubdirectory("octopus-other-").FullName, null));
        await Until(() => runtime.AgentsOf(hostId).Count > 0, "the host's agents are known");

        var launcher = AppHost.Get<LauncherViewModel>();
        launcher.Refresh();
        Assert.False(launcher.IsRemoteProject);
        Assert.Contains(launcher.Agents, a => a.Id == "claude");
        Assert.Equal(runtime.AgentsOf(hostId).Select(a => a.Id).Order(), launcher.Agents.Select(a => a.Id).Order());

        launcher.Agents.First(a => a.Id == "claude").LaunchCommand.Execute(null);
        await Until(() => project.Items.Count == 1, "the agent's item arrives");
        Assert.Equal((ProjectItemKind.Agent, "claude"), (project.Items[0].Kind, project.Items[0].AgentId));
        var onHost = other.Projects.Projects.Single().Items.Single();
        Assert.Equal("claude", onHost.AgentId);
        // The host's own settings for the agent apply: nothing of this computer's profile was sent.
        Assert.Null(onHost.Launch!.Model);
    }

    [AvaloniaFact]
    public async Task A_dropped_connection_is_made_again_and_the_tab_says_what_happened_meanwhile()
    {
        await using var other = new OtherComputer();
        var (page, hostId) = await Pair(other);
        using var _ = page;
        var runtime = AppHost.Get<RemoteClientRuntime>();
        var remote = AppHost.Get<RemoteProjects>();
        var project = await Wait(remote.AddAsync(hostId, Directory.CreateTempSubdirectory("octopus-other-").FullName, "API"));
        var onHost = other.Projects.Projects.Single();
        var states = new List<RemoteHostState>();
        runtime.StateChanged += id => { lock (states) states.Add(runtime.StateOf(id)); };

        // The host goes away (asleep, off the network) and the connection drops.
        other.Reachable = false;
        await Wait(runtime.ClientOf(hostId)!.DisposeAsync().AsTask());
        await Until(() => runtime.StateOf(hostId) != RemoteHostState.Connected, "the drop is noticed");
        Assert.NotNull(runtime.ErrorOf(hostId));

        // A terminal started meanwhile says why it has nothing to show, and ends, instead of hanging.
        var item = new ProjectItem { Id = "0123", Kind = ProjectItemKind.Terminal, Title = "zsh" };
        await using (var session = new TerminalSession(ItemLaunchService.RemoteSession, remote.TerminalFactory(project, item),
                         new TKCode.Terminal.Core.TerminalSize(60, 10, 0, 0), TerminalPalettes.Dark, TKCode.Terminal.Core.ColorScheme.Dark, 100, () => false)
        { IsMirror = true })
        {
            session.Start();
            await Until(() => session.ReadText().Contains("Studio:"), "the terminal says the host is not connected");
            await Until(() => session.LastExit is not null, "and reports its end");
        }

        // While it was away the host got a new terminal. Back on the network, the connection is made again without
        // anybody asking, and the project here catches up.
        var added = new ProjectItem { Kind = ProjectItemKind.Terminal, Title = "zsh" };
        other.Projects.AddItem(onHost, added);
        var dialsBefore = other.Dials;
        other.Reachable = true;
        await Until(() => runtime.StateOf(hostId) == RemoteHostState.Connected, "the connection comes back by itself", 20_000);
        Assert.True(other.Dials > dialsBefore);
        await Until(() => project.Items.Any(i => i.Id == added.Id), "the project catches up with the host");
        lock (states) Assert.Contains(RemoteHostState.Connecting, states);
        await Until(() => page.Hosts[0].IsConnected, "the page shows it connected again");
    }

    [AvaloniaFact]
    public void The_new_project_dialog_offers_paired_hosts_as_places()
    {
        var view = new NewProjectView(null, new NewProjectChoice("~/code/api", "", RemoteHost: "abc"), [], [("abc", "Studio")]);
        var window = new Window { Width = 520, Height = 360, Content = view };
        window.Show();
        Frames.Pump();

        var location = view.FindControl<Control>("LocationSelect")!;
        Assert.True(location.IsVisible);
        Assert.Equal("abc", view.RemoteHost);
        Assert.Null(view.SshHost);
        // The folder is the host's to check: nothing on this computer is looked at.
        Assert.Null(NewProjectView.Validate("/does/not/exist/here", remoteHost: "abc"));
        Assert.NotNull(NewProjectView.Validate("/does/not/exist/here"));
        Assert.True(view.FindControl<Control>("BrowseButton")!.IsVisible);
        window.Close();

        // With no host of either kind the place is not asked.
        var plain = new NewProjectView(null, null);
        Assert.Null(plain.RemoteHost);
    }
}
