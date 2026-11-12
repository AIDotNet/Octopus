using Octopus.App.Common;
using Octopus.App.Services;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>An ssh process that the test drives: it says lines, ends when told to, and ends when killed.</summary>
public sealed class FakeSshProcess : IRunningProcess
{
    private readonly Action<string> _onLine;
    private readonly Action<int> _onExited;
    private int _ended;

    public FakeSshProcess(int id, IReadOnlyList<string> arguments, Action<string> onLine, Action<int> onExited, bool keepsInputOpen = false)
    {
        Id = id;
        Arguments = arguments;
        KeepsInputOpen = keepsInputOpen;
        _onLine = onLine;
        _onExited = onExited;
    }

    public int Id { get; }
    public IReadOnlyList<string> Arguments { get; }
    public bool KeepsInputOpen { get; }
    /// <summary>The ssh that holds a session on the shared connection (the anchor of a host).</summary>
    public bool IsAnchor => Arguments.Count > 0 && Arguments[^1] == "cat";
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public bool HasExited => Volatile.Read(ref _ended) == 1;
    public bool Killed { get; private set; }

    public string Line => string.Join(' ', Arguments);

    /// <summary>What ssh logs when it has logged in and set the forwards up: the tunnel is running.</summary>
    public void Up()
    {
        Say("Authenticated to 10.0.0.5 ([10.0.0.5]:22) using \"publickey\".");
        Say("debug1: Entering interactive session.");
    }

    public void Say(string line) => _onLine(line);

    public void Exit(int code)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0) _onExited(code);
    }

    public void Kill()
    {
        Killed = true;
        Exit(-1);
    }

    public void Dispose()
    {
    }
}

public sealed class FakeProcessHost : IProcessHost
{
    private readonly object _gate = new();
    private readonly List<FakeSshProcess> _started = [];
    private int _nextId = 1000;

    /// <summary>What the next start does instead of starting, when set.</summary>
    public Exception? StartFails { get; set; }

    /// <summary>The anchors (ssh with <c>cat</c>) end at once, as they do on a host that has no cat.</summary>
    public bool AnchorsExitAtOnce { get; set; }

    public List<(int Id, DateTimeOffset StartedAt, string Name)> KillRequests { get; } = [];

    public IReadOnlyList<FakeSshProcess> Started
    {
        get
        {
            lock (_gate) return [.. _started];
        }
    }

    public IRunningProcess Start(string fileName, IEnumerable<string> arguments, Action<string> onErrorLine, Action<int> onExited, bool keepInputOpen = false)
    {
        if (StartFails is { } failure) throw failure;
        FakeSshProcess process;
        lock (_gate)
        {
            process = new FakeSshProcess(_nextId++, [.. arguments], onErrorLine, onExited, keepInputOpen);
            _started.Add(process);
        }
        if (AnchorsExitAtOnce && process.IsAnchor) _ = Task.Run(() => process.Exit(127));
        return process;
    }

    public bool KillIfSame(int processId, DateTimeOffset startedAt, string processName)
    {
        KillRequests.Add((processId, startedAt, processName));
        return true;
    }
}

/// <summary>
/// The shared connection of the terminals as a test sees it: whether a master is there, which forwards it holds, and every
/// <c>-O</c> command that was run, in order. Also answers the folder reads of a host (as <see cref="FakeSshRunner"/>).
/// </summary>
public sealed class FakeSharedConnection : IProcessRunner, ISharedConnection
{
    private readonly object _gate = new();
    private readonly List<string> _commands = [];
    private readonly HashSet<string> _forwards = [];

    public IReadOnlyList<string> ClientOptions { get; set; } = ["-o", "ControlMaster=no", "-o", "ControlPath=/ctl/%C"];

    /// <summary>A terminal has signed in: there is a connection to ride on.</summary>
    public bool MasterUp { get; set; }

    /// <summary>What <c>-O forward</c> says instead of success, when set (the error output; the exit code is 255).</summary>
    public string? ForwardError { get; set; }

    /// <summary>How long <c>-O forward</c> takes.</summary>
    public TimeSpan ForwardDelay { get; set; }

    /// <summary>The master ends: it takes its forwards with it.</summary>
    public void EndMaster()
    {
        lock (_gate)
        {
            MasterUp = false;
            _forwards.Clear();
        }
    }

    /// <summary>Every <c>-O</c> command, as "check", "forward -L 127.0.0.1:5173:localhost:5173", "cancel -L …".</summary>
    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_gate) return [.. _commands];
        }
    }

    /// <summary>The forwards the master holds, as "-L 127.0.0.1:5173:localhost:5173".</summary>
    public IReadOnlyCollection<string> Forwards
    {
        get
        {
            lock (_gate) return [.. _forwards];
        }
    }

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var args = arguments.ToList();
        var at = args.IndexOf("-O");
        if (at < 0)
        {
            return new ProcessResult(0, args.Last().StartsWith("cd ", StringComparison.Ordinal) ? "/srv/app\n" : "", "", false);
        }
        var operation = args[at + 1];
        var detail = operation == "check" ? "" : $" {args[at + 2]} {args[at + 3]}";
        lock (_gate) _commands.Add(operation + detail);
        if (operation == "forward" && ForwardDelay > TimeSpan.Zero) await Task.Delay(ForwardDelay, CancellationToken.None);
        lock (_gate)
        {
            if (!MasterUp)
            {
                return new ProcessResult(255, "", "Control socket connect(/ctl/8d6691c9): No such file or directory", false);
            }
            switch (operation)
            {
                case "check":
                    return new ProcessResult(0, "", "Master running (pid=4242)", false);
                case "forward" when ForwardError is { } error:
                    return new ProcessResult(255, "", error + "\nmuxclient: master forward request failed", false);
                case "forward":
                    _forwards.Add($"{args[at + 2]} {args[at + 3]}");
                    return new ProcessResult(0, "", "", false);
                case "cancel":
                    // The exit code is 0 either way, as ssh's is.
                    return new ProcessResult(0, "", _forwards.Remove($"{args[at + 2]} {args[at + 3]}") ? "" : "mux_client_forward: forwarding request failed: port not forwarded", false);
                default:
                    return new ProcessResult(0, "", "", false);
            }
        }
    }
}

public sealed class FakeLocalPorts : ILocalPorts
{
    private readonly object _gate = new();

    /// <summary>Ports that other programs hold.</summary>
    public HashSet<int> Taken { get; } = [];

    public bool IsFree(int port)
    {
        lock (_gate) return !Taken.Contains(port);
    }

    /// <summary>A port that comes free after a while, as the one of an ssh that was just ended does.</summary>
    public void ReleaseLater(int port, TimeSpan delay) => _ = Task.Run(async () =>
    {
        await Task.Delay(delay);
        lock (_gate) Taken.Remove(port);
    });

    public int FindFree() => 54321;
}

/// <summary>Answers every ssh run of the file tree as a host with one folder, so that no real ssh is started.</summary>
public sealed class FakeSshRunner : IProcessRunner
{
    public Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProcessResult(0, arguments.Last().StartsWith("cd ", StringComparison.Ordinal) ? "/srv/app\n" : "", "", false));
}

public static class FakeSshContainer
{
    /// <summary>
    /// A new container for the test that runs, in which ssh is fake: the host's folders are answered, and tunnels start the
    /// given fake processes on the given fake ports. Returns what puts the overrides back (the container is disposed after the test).
    /// </summary>
    public static Action Use(FakeProcessHost host, FakeLocalPorts ports, FakeSharedConnection? shared = null)
    {
        var previous = Octopus.App.Composition.AppHost.ConfigureOverrides;
        Octopus.App.Composition.AppHost.Reset();
        Octopus.App.Composition.AppHost.ConfigureOverrides = services =>
        {
            previous?.Invoke(services);
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.Replace(services,
                Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<IProcessRunner>(shared is null ? new FakeSshRunner() : shared));
            if (shared is not null)
            {
                Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.Replace(services,
                    Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<ISharedConnection>(shared));
            }
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.Replace(services,
                Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<IProcessHost>(host));
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.Replace(services,
                Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<ILocalPorts>(ports));
        };
        return () => Octopus.App.Composition.AppHost.ConfigureOverrides = previous;
    }
}

public class TunnelServiceTests : IDisposable
{
    private static readonly TunnelTimings Quick = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(5),
        ReadyTimeout = TimeSpan.FromSeconds(3),
        RemoteGrace = TimeSpan.FromMilliseconds(60),
        RetryDelays = [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40)],
        MaxFailuresInARow = 3,
        PortGrace = TimeSpan.FromMilliseconds(40),
        WarningLifetime = TimeSpan.FromMilliseconds(150),
        WaitPoll = TimeSpan.FromMilliseconds(20),
        AnchorSettle = TimeSpan.FromMilliseconds(40),
        MonitorPoll = TimeSpan.FromMilliseconds(30),
    };

    private readonly FakeProcessHost _host = new();
    private readonly FakeLocalPorts _ports = new();
    private readonly ProjectService _projects = new(new InMemoryProjectStore(), new PathComparer(ignoreCase: false), new InMemorySettingsStore());
    private readonly string _ledger = Directory.CreateTempSubdirectory("octopus-ledger-").FullName;
    private readonly List<TunnelService> _services = [];

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        // Windows refuses to delete a file that is still being released; a leftover temp folder costs nothing.
        try { Directory.Delete(_ledger, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private TunnelService Service(TunnelTimings? timings = null, FakeSharedConnection? shared = null)
    {
        var service = new TunnelService(_host, _ports, _projects, _ledger, timings ?? Quick, shared, shared);
        _services.Add(service);
        return service;
    }

    private Project RemoteProject(string path = "/srv/app", string host = "prod") => _projects.AddProject(path, null, host);

    private static TunnelSpec Local(int port = 5173, bool reconnect = true, bool auto = false) => new()
    {
        Kind = TunnelKind.Local, ListenPort = port, TargetPort = port, AutoReconnect = reconnect, AutoStart = auto,
    };

    private static TunnelSpec Remote(int port = 9000) => new() { Kind = TunnelKind.Remote, ListenPort = port, TargetPort = 3000 };

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Every status the run has had from now on: a state that lasts a few milliseconds cannot be caught by looking.</summary>
    private static List<TunnelStatus> Record(TunnelRun run)
    {
        var history = new List<TunnelStatus> { run.Status };
        run.Changed += (_, _) =>
        {
            lock (history) history.Add(run.Status);
        };
        return history;
    }

    private static bool Had(List<TunnelStatus> history, TunnelState state, TunnelFailure failure)
    {
        lock (history) return history.Any(s => s.State == state && s.Failure == failure);
    }

    private static Task InState(TunnelRun run, TunnelState state) =>
        Eventually(() => run.Status.State == state, $"{state} (it is {run.Status.State}, {run.Status.Failure})");

    private async Task<FakeSshProcess> NthStarted(int count)
    {
        await Eventually(() => _host.Started.Count >= count, $"ssh start number {count}");
        return _host.Started[count - 1];
    }

    // ---- local tunnels ----

    [Fact]
    public async Task A_local_tunnel_runs_ssh_for_the_forward_and_is_running_once_the_port_listens()
    {
        var project = RemoteProject();
        var spec = Local();
        _projects.AddTunnel(project, spec);
        var service = Service();

        service.Start(project, spec);
        var run = service.RunOf(spec.Id);
        Assert.Equal(TunnelState.Connecting, run.Status.State);
        var process = await NthStarted(1);
        Assert.Contains("-L 127.0.0.1:5173:localhost:5173 -- prod", process.Line);
        Assert.Equal(TunnelState.Connecting, run.Status.State);

        // ssh sets the forward up once it has logged in.
        process.Up();

        await InState(run, TunnelState.Running);
    }

    [Fact]
    public async Task A_local_port_that_is_taken_fails_without_starting_ssh()
    {
        var project = RemoteProject();
        var spec = Local();
        _ports.Taken.Add(5173);
        var service = Service();

        service.Start(project, spec);

        var run = service.RunOf(spec.Id);
        await InState(run, TunnelState.Failed);
        Assert.Equal(TunnelFailure.LocalPortInUse, run.Status.Failure);
        Assert.Empty(_host.Started);
    }

    [Fact]
    public async Task A_spec_that_is_not_valid_fails_without_starting_ssh()
    {
        var project = RemoteProject();
        var spec = Local();
        spec.TargetHost = "db internal";
        var service = Service();

        service.Start(project, spec);

        var run = service.RunOf(spec.Id);
        await InState(run, TunnelState.Failed);
        Assert.Equal(TunnelFailure.InvalidSetup, run.Status.Failure);
        Assert.Empty(_host.Started);
    }

    [Fact]
    public async Task A_port_that_ssh_cannot_bind_fails_and_is_not_tried_again()
    {
        var project = RemoteProject();
        var spec = Local();
        var service = Service();
        service.Start(project, spec);
        var process = await NthStarted(1);

        process.Say("bind [127.0.0.1]:5173: Address already in use");
        process.Say("Could not request local forwarding.");
        process.Exit(255);

        var run = service.RunOf(spec.Id);
        await InState(run, TunnelState.Failed);
        Assert.Equal(TunnelFailure.LocalPortInUse, run.Status.Failure);
        Assert.Equal("bind [127.0.0.1]:5173: Address already in use", run.Status.Detail);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Single(_host.Started);
    }

    [Fact]
    public async Task A_refused_login_fails_and_says_why()
    {
        var project = RemoteProject();
        var spec = Local();
        var service = Service();
        service.Start(project, spec);
        var process = await NthStarted(1);

        process.Say("root@prod: Permission denied (publickey,password).");
        process.Exit(255);

        var run = service.RunOf(spec.Id);
        await InState(run, TunnelState.Failed);
        Assert.Equal(TunnelFailure.LoginDenied, run.Status.Failure);
        Assert.Single(_host.Started);
    }

    [Fact]
    public async Task Two_local_tunnels_cannot_listen_on_the_same_port()
    {
        var project = RemoteProject();
        var first = Local(5173);
        var second = Local(5173);
        var service = Service();
        service.Start(project, first);
        await NthStarted(1);

        service.Start(project, second);

        var run = service.RunOf(second.Id);
        await InState(run, TunnelState.Failed);
        Assert.Equal(TunnelFailure.PortUsedByTunnel, run.Status.Failure);
        Assert.Single(_host.Started);
    }

    // ---- remote tunnels ----

    [Fact]
    public async Task A_remote_tunnel_is_running_when_the_host_confirms_the_forward()
    {
        var project = RemoteProject();
        var spec = Remote();
        var service = Service();
        service.Start(project, spec);
        var process = await NthStarted(1);
        Assert.Contains("-R 127.0.0.1:9000:localhost:3000 -- prod", process.Line);

        process.Say("debug1: remote forward success for: listen 9000, connect localhost:3000");

        await InState(service.RunOf(spec.Id), TunnelState.Running);
    }

    [Fact]
    public async Task A_remote_tunnel_is_running_after_the_login_when_nothing_was_refused_for_a_while()
    {
        var project = RemoteProject();
        var spec = Remote();
        var service = Service();
        service.Start(project, spec);
        var process = await NthStarted(1);

        process.Say("Authenticated to 10.0.0.5 ([10.0.0.5]:22) using \"publickey\".");

        await InState(service.RunOf(spec.Id), TunnelState.Running);
    }

    [Fact]
    public async Task A_remote_forward_that_the_host_refuses_is_not_taken_as_running()
    {
        var project = RemoteProject();
        var spec = Remote();
        var service = Service();
        service.Start(project, spec);
        var process = await NthStarted(1);

        process.Say("Authenticated to 10.0.0.5 ([10.0.0.5]:22) using \"publickey\".");
        process.Say("Error: remote port forwarding failed for listen port 9000");
        process.Exit(255);

        var run = service.RunOf(spec.Id);
        await InState(run, TunnelState.Failed);
        Assert.Equal(TunnelFailure.RemoteForwardRefused, run.Status.Failure);
    }

    [Fact]
    public async Task Remote_tunnels_on_different_hosts_may_use_the_same_port()
    {
        var first = RemoteProject("/srv/a", "prod");
        var second = RemoteProject("/srv/b", "staging");
        var service = Service();

        service.Start(first, Remote(9000));
        service.Start(second, Remote(9000));

        await NthStarted(2);
    }

    // ---- losing the connection ----

    [Fact]
    public async Task A_connection_that_is_lost_is_made_again_and_the_tunnel_runs_again()
    {
        var project = RemoteProject();
        var spec = Local();
        var service = Service();
        service.Start(project, spec);
        var first = await NthStarted(1);
        first.Up();
        var run = service.RunOf(spec.Id);
        await InState(run, TunnelState.Running);

        first.Say("Connection reset by 10.0.0.5 port 22");
        first.Exit(255);

        await InState(run, TunnelState.Reconnecting);
        Assert.Equal(TunnelFailure.ConnectionLost, run.Status.Failure);
        var second = await NthStarted(2);
        second.Up();
        await InState(run, TunnelState.Running);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Without_automatic_reconnecting_a_lost_connection_fails_the_tunnel()
    {
        var project = RemoteProject();
        var spec = Local(reconnect: false);
        var service = Service();
        service.Start(project, spec);
        var first = await NthStarted(1);
        first.Up();
        var run = service.RunOf(spec.Id);
        await InState(run, TunnelState.Running);

        first.Say("Timeout, server 10.0.0.5 not responding.");
        first.Exit(255);

        await InState(run, TunnelState.Failed);
        Assert.Equal(TunnelFailure.ConnectionLost, run.Status.Failure);
        Assert.Single(_host.Started);
    }

    [Fact]
    public async Task A_tunnel_that_never_comes_up_gives_up_after_some_tries()
    {
        var project = RemoteProject();
        var spec = Local();
        var service = Service();
        service.Start(project, spec);

        for (var i = 1; i <= Quick.MaxFailuresInARow; i++)
        {
            var process = await NthStarted(i);
            process.Say("ssh: connect to host prod port 22: Connection refused");
            process.Exit(255);
        }

        var run = service.RunOf(spec.Id);
        await InState(run, TunnelState.Failed);
        Assert.Equal(TunnelFailure.ConnectionLost, run.Status.Failure);
        Assert.Equal(Quick.MaxFailuresInARow, _host.Started.Count);
    }

    [Fact]
    public async Task A_tunnel_that_came_up_before_starts_counting_its_failures_again()
    {
        var project = RemoteProject();
        var spec = Local();
        var service = Service();
        service.Start(project, spec);

        // Two failed runs, then one that comes up and is lost, then two failed runs: five in all, never three in a row.
        for (var i = 1; i <= 2; i++)
        {
            var failing = await NthStarted(i);
            failing.Say("Connection reset by 10.0.0.5 port 22");
            failing.Exit(255);
        }
        var good = await NthStarted(3);
        good.Up();
        var run = service.RunOf(spec.Id);
        await InState(run, TunnelState.Running);
        good.Say("Connection reset by 10.0.0.5 port 22");
        good.Exit(255);
        for (var i = 4; i <= 5; i++)
        {
            var failing = await NthStarted(i);
            failing.Say("Connection reset by 10.0.0.5 port 22");
            failing.Exit(255);
        }

        await NthStarted(6);
        Assert.NotEqual(TunnelState.Failed, run.Status.State);
    }

    [Fact]
    public async Task A_run_that_ends_without_a_word_is_tried_again()
    {
        var project = RemoteProject();
        var spec = Local();
        var service = Service();
        var history = Record(service.RunOf(spec.Id));
        service.Start(project, spec);
        var first = await NthStarted(1);

        first.Exit(1);

        await NthStarted(2);
        Assert.True(Had(history, TunnelState.Reconnecting, TunnelFailure.Exited));
    }

    [Fact]
    public async Task A_forward_that_is_not_up_in_time_is_ended_and_tried_again()
    {
        var project = RemoteProject();
        var spec = Local();
        var service = Service(new TunnelTimings
        {
            PollInterval = TimeSpan.FromMilliseconds(5),
            ReadyTimeout = TimeSpan.FromMilliseconds(80),
            RetryDelays = [TimeSpan.FromMilliseconds(20)],
            MaxFailuresInARow = 3,
            PortGrace = TimeSpan.FromMilliseconds(40),
        });

        var history = Record(service.RunOf(spec.Id));
        service.Start(project, spec);

        var first = await NthStarted(1);
        await Eventually(() => first.Killed, "the first run to be ended");
        await NthStarted(2);
        Assert.True(Had(history, TunnelState.Reconnecting, TunnelFailure.TimedOut));
    }

    [Fact]
    public async Task Ssh_that_cannot_be_started_fails_the_tunnel()
    {
        var project = RemoteProject();
        var spec = Local();
        _host.StartFails = new ProcessStartException("No such file or directory");
        var service = Service();

        service.Start(project, spec);

        var run = service.RunOf(spec.Id);
        await InState(run, TunnelState.Failed);
        Assert.Equal(TunnelFailure.StartFailed, run.Status.Failure);
        Assert.Equal("No such file or directory", run.Status.Detail);
    }

    // ---- stopping and starting again ----

    [Fact]
    public async Task Stopping_ends_ssh_and_nothing_starts_it_again()
    {
        var project = RemoteProject();
        var spec = Local();
        var service = Service();
        service.Start(project, spec);
        var process = await NthStarted(1);
        process.Up();
        var run = service.RunOf(spec.Id);
        await InState(run, TunnelState.Running);

        service.Stop(spec.Id);

        Assert.Equal(TunnelState.Stopped, run.Status.State);
        Assert.True(process.Killed);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(TunnelState.Stopped, run.Status.State);
        Assert.Single(_host.Started);
    }

    [Fact]
    public async Task Stopping_while_waiting_to_reconnect_cancels_the_reconnect()
    {
        var project = RemoteProject();
        var spec = Local();
        var service = Service(new TunnelTimings
        {
            PollInterval = TimeSpan.FromMilliseconds(5),
            RetryDelays = [TimeSpan.FromMilliseconds(300)],
            PortGrace = TimeSpan.FromMilliseconds(40),
        });
        service.Start(project, spec);
        var first = await NthStarted(1);
        first.Say("Connection reset by 10.0.0.5 port 22");
        first.Exit(255);
        var run = service.RunOf(spec.Id);
        await InState(run, TunnelState.Reconnecting);

        service.Stop(spec.Id);
        await Task.Delay(450, TestContext.Current.CancellationToken);

        Assert.Equal(TunnelState.Stopped, run.Status.State);
        Assert.Single(_host.Started);
    }

    [Fact]
    public async Task Starting_a_running_tunnel_again_ends_the_old_run_and_uses_the_new_spec()
    {
        var project = RemoteProject();
        var spec = Local(5173);
        var service = Service();
        service.Start(project, spec);
        var first = await NthStarted(1);
        first.Up();
        var run = service.RunOf(spec.Id);
        await InState(run, TunnelState.Running);

        var edited = spec.Clone();
        edited.ListenPort = 6000;
        edited.TargetPort = 6000;
        service.Start(project, edited);

        Assert.True(first.Killed);
        var second = await NthStarted(2);
        Assert.Contains("-L 127.0.0.1:6000:localhost:6000", second.Line);
        Assert.Equal(TunnelState.Connecting, run.Status.State);
        // The ended run does not push its end into the new run's status.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.NotEqual(TunnelState.Failed, run.Status.State);
        Assert.NotEqual(TunnelState.Stopped, run.Status.State);
    }

    [Fact]
    public async Task A_tunnel_that_is_started_again_waits_for_the_ssh_that_it_ended_to_let_go_of_its_port()
    {
        var project = RemoteProject();
        var spec = Local(5173);
        var service = Service();
        service.Start(project, spec);
        var first = await NthStarted(1);
        first.Up();
        var run = service.RunOf(spec.Id);
        await InState(run, TunnelState.Running);

        // The old ssh is ended, and for a moment it still holds the port, as a real one does.
        _ports.Taken.Add(5173);
        _ports.ReleaseLater(5173, TimeSpan.FromMilliseconds(15));
        service.Start(project, spec.Clone());

        var second = await NthStarted(2);
        Assert.NotSame(first, second);
        Assert.NotEqual(TunnelState.Failed, run.Status.State);
    }

    [Fact]
    public async Task A_port_that_stays_taken_after_that_time_is_taken_by_someone_else()
    {
        var project = RemoteProject();
        var spec = Local(5173);
        _ports.Taken.Add(5173);
        var service = Service();

        service.Start(project, spec);

        var run = service.RunOf(spec.Id);
        await InState(run, TunnelState.Failed);
        Assert.Equal(TunnelFailure.LocalPortInUse, run.Status.Failure);
    }

    [Fact]
    public async Task A_local_tunnel_is_not_running_before_ssh_has_set_it_up()
    {
        var project = RemoteProject();
        var spec = Local();
        var service = Service();
        service.Start(project, spec);
        var process = await NthStarted(1);

        // Logged in, but the forwards are not up yet: not running.
        process.Say("Authenticated to 10.0.0.5 ([10.0.0.5]:22) using \"publickey\".");
        process.Say("debug1: Local connections to 127.0.0.1:5173 forwarded to remote address localhost:5173");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(TunnelState.Connecting, service.RunOf(spec.Id).Status.State);

        process.Say("debug1: Entering interactive session.");
        await InState(service.RunOf(spec.Id), TunnelState.Running);
    }

    [Fact]
    public async Task Stop_all_ends_every_process()
    {
        var project = RemoteProject();
        var a = Local(5173);
        var b = Remote(9000);
        var service = Service();
        service.Start(project, a);
        service.Start(project, b);
        await NthStarted(2);

        service.StopAll();

        Assert.All(_host.Started, p => Assert.True(p.Killed));
        Assert.Equal(TunnelState.Stopped, service.RunOf(a.Id).Status.State);
        Assert.Equal(TunnelState.Stopped, service.RunOf(b.Id).Status.State);
    }

    [Fact]
    public async Task Disposing_the_service_ends_every_process()
    {
        var project = RemoteProject();
        var service = Service();
        service.Start(project, Local());
        var process = await NthStarted(1);

        service.Dispose();

        Assert.True(process.Killed);
    }

    // ---- warnings ----

    [Fact]
    public async Task A_connection_through_a_running_tunnel_that_fails_is_a_warning_that_goes_away()
    {
        var project = RemoteProject();
        var spec = Local();
        var service = Service();
        service.Start(project, spec);
        var process = await NthStarted(1);
        process.Up();
        var run = service.RunOf(spec.Id);
        await InState(run, TunnelState.Running);

        process.Say("channel 2: open failed: connect failed: Connection refused");

        Assert.Equal(TunnelState.Running, run.Status.State);
        Assert.Equal(TunnelWarning.TargetUnreachable, run.Status.Warning);
        await Eventually(() => run.Status.Warning == TunnelWarning.None, "the warning to go");
        Assert.Equal(TunnelState.Running, run.Status.State);
    }

    // ---- following the projects ----

    [Fact]
    public async Task A_tunnel_that_is_removed_from_its_project_is_stopped()
    {
        var project = RemoteProject();
        var spec = Local();
        _projects.AddTunnel(project, spec);
        var service = Service();
        service.Initialize();
        service.Start(project, spec);
        var process = await NthStarted(1);

        _projects.RemoveTunnel(project, spec.Id);

        Assert.True(process.Killed);
    }

    [Fact]
    public async Task Removing_a_project_stops_its_tunnels()
    {
        var project = RemoteProject();
        var spec = Local();
        _projects.AddTunnel(project, spec);
        var service = Service();
        service.Initialize();
        service.Start(project, spec);
        var process = await NthStarted(1);

        _projects.RemoveProject(project.Id);

        Assert.True(process.Killed);
    }

    [Fact]
    public async Task A_running_tunnel_that_is_edited_runs_again_with_the_edit()
    {
        var project = RemoteProject();
        var spec = Local(5173);
        _projects.AddTunnel(project, spec);
        var service = Service();
        service.Initialize();
        service.Start(project, spec);
        var first = await NthStarted(1);
        first.Up();
        await InState(service.RunOf(spec.Id), TunnelState.Running);

        var edited = spec.Clone();
        edited.ListenPort = 7000;
        edited.TargetPort = 7000;
        _projects.UpdateTunnel(project, edited);

        Assert.True(first.Killed);
        var second = await NthStarted(2);
        Assert.Contains("-L 127.0.0.1:7000:localhost:7000", second.Line);
    }

    [Fact]
    public async Task A_stopped_tunnel_that_is_edited_stays_stopped()
    {
        var project = RemoteProject();
        var spec = Local(5173);
        _projects.AddTunnel(project, spec);
        var service = Service();
        service.Initialize();
        var run = service.RunOf(spec.Id);

        var edited = spec.Clone();
        edited.ListenPort = 7000;
        _projects.UpdateTunnel(project, edited);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(TunnelState.Stopped, run.Status.State);
        Assert.Empty(_host.Started);
        // And it starts with the edit.
        service.Start(project, edited);
        Assert.Contains("-L 127.0.0.1:7000:", (await NthStarted(1)).Line);
    }

    // ---- automatic start ----

    [Fact]
    public async Task Automatic_tunnels_start_with_the_first_activation_of_their_project_and_only_then()
    {
        var project = RemoteProject();
        var auto = Local(5173, auto: true);
        var manual = Local(5174);
        _projects.AddTunnel(project, auto);
        _projects.AddTunnel(project, manual);
        var service = Service();

        service.Initialize();

        var process = await NthStarted(1);
        Assert.Contains("127.0.0.1:5173:", process.Line);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(_host.Started);

        // Stopped by the person, and the project is opened again: it stays stopped.
        service.Stop(auto.Id);
        var other = RemoteProject("/srv/other");
        _projects.SetCurrent(other.Id);
        _projects.SetCurrent(project.Id);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(_host.Started);
    }

    [Fact]
    public async Task A_project_that_is_opened_later_starts_its_automatic_tunnels_then()
    {
        var first = RemoteProject("/srv/a");
        var second = RemoteProject("/srv/b");
        var auto = Local(5173, auto: true);
        _projects.AddTunnel(second, auto);
        _projects.SetCurrent(first.Id);
        var service = Service();
        service.Initialize();
        Assert.Empty(_host.Started);

        _projects.SetCurrent(second.Id);

        await NthStarted(1);
    }

    [Fact]
    public void A_project_on_this_computer_has_no_tunnels()
    {
        var local = _projects.AddProject(Directory.CreateTempSubdirectory("octopus-local-").FullName);
        var spec = Local();

        _projects.AddTunnel(local, spec);
        Service().Start(local, spec);

        Assert.Empty(local.Tunnels);
        Assert.Empty(_host.Started);
    }

    // ---- what a crash leaves behind ----

    [Fact]
    public void Processes_that_a_dead_app_left_are_ended_at_start_and_those_of_a_live_one_are_not()
    {
        var dead = int.MaxValue - 7;
        var started = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        File.WriteAllText(Path.Combine(_ledger, dead + ".txt"), "4242|" + started.ToUnixTimeMilliseconds() + "\n");
        File.WriteAllText(Path.Combine(_ledger, Environment.ProcessId + ".txt"), "5151|" + started.ToUnixTimeMilliseconds() + "\n");

        // The service believes it is another app process, so that the file of this (live) test process is a stranger's.
        var service = new TunnelService(_host, _ports, _projects, _ledger, ownerId: 1, Quick);
        _services.Add(service);
        service.Initialize();

        Assert.Equal([(4242, started, "ssh")], _host.KillRequests);
        Assert.False(File.Exists(Path.Combine(_ledger, dead + ".txt")));
        Assert.True(File.Exists(Path.Combine(_ledger, Environment.ProcessId + ".txt")));
    }

    [Fact]
    public async Task The_processes_of_running_tunnels_are_written_down_and_taken_off_when_they_end()
    {
        var project = RemoteProject();
        var spec = Local();
        var service = Service();
        service.Start(project, spec);
        var process = await NthStarted(1);
        var file = Path.Combine(_ledger, Environment.ProcessId + ".txt");

        await Eventually(() => File.Exists(file) && File.ReadAllText(file).StartsWith(process.Id + "|"), "the process to be written down");

        service.Stop(spec.Id);
        await Eventually(() => !File.Exists(file), "the record to go");
    }
}
