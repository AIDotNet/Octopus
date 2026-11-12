using Octopus.App.Common;
using Octopus.App.Services;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>
/// A host that wants a password or a second factor: the tunnel's own ssh is refused, so it rides on the connection of a
/// terminal (design/11-SSH隧道.md §4). The ssh runs are fakes that behave as the real ones were seen to.
/// </summary>
public class TunnelSharedTests : IDisposable
{
    private static readonly TunnelTimings Quick = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(5),
        ReadyTimeout = TimeSpan.FromSeconds(3),
        RetryDelays = [TimeSpan.FromMilliseconds(20)],
        PortGrace = TimeSpan.FromMilliseconds(40),
        WaitPoll = TimeSpan.FromMilliseconds(15),
        AnchorSettle = TimeSpan.FromMilliseconds(40),
        MonitorPoll = TimeSpan.FromMilliseconds(30),
    };

    private const string Denied = "root@prod: Permission denied (publickey,password).";

    private readonly FakeProcessHost _host = new();
    private readonly FakeLocalPorts _ports = new();
    private readonly FakeSharedConnection _shared = new();
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

    private TunnelService Service(FakeSharedConnection? shared = null)
    {
        var connection = shared ?? _shared;
        var service = new TunnelService(_host, _ports, _projects, _ledger, Quick, connection, connection);
        service.Initialize();
        _services.Add(service);
        return service;
    }

    private Project Remote(string host = "prod") => _projects.AddProject("/srv/app", null, host);

    private static TunnelSpec Local(int port = 5173) => new() { Kind = TunnelKind.Local, ListenPort = port, TargetPort = port };

    private static TunnelSpec Remote(int port) => new() { Kind = TunnelKind.Remote, ListenPort = port, TargetPort = 3000 };

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private static Task InState(TunnelRun run, TunnelState state) =>
        Eventually(() => run.Status.State == state, $"{state} (it is {run.Status.State}, {run.Status.Failure})");

    private static List<TunnelStatus> Record(TunnelRun run)
    {
        var history = new List<TunnelStatus> { run.Status };
        run.Changed += (_, _) =>
        {
            lock (history) history.Add(run.Status);
        };
        return history;
    }

    private IReadOnlyList<FakeSshProcess> Anchors => [.. _host.Started.Where(p => p.IsAnchor)];

    /// <summary>Starts the tunnel and has its own ssh refused, as a host with a password does: it is now on the shared connection.</summary>
    private async Task<TunnelRun> StartRefused(TunnelService service, Project project, TunnelSpec spec)
    {
        service.Start(project, spec);
        await Eventually(() => _host.Started.Count > 0, "the tunnel's own ssh");
        var own = _host.Started.First(p => !p.IsAnchor);
        own.Say(Denied);
        own.Exit(255);
        return service.RunOf(spec.Id);
    }

    [Fact]
    public async Task A_host_that_refuses_the_login_makes_the_tunnel_wait_for_a_terminal_that_has_signed_in()
    {
        var project = Remote();
        var spec = Local();
        var service = Service();
        var history = Record(service.RunOf(spec.Id));

        var run = await StartRefused(service, project, spec);

        await InState(run, TunnelState.Waiting);
        // Not a failure, and nothing more is started: it is the person's turn.
        Assert.Equal(TunnelFailure.None, run.Status.Failure);
        Assert.True(run.Status.IsActive);
        Assert.DoesNotContain(history, s => s.State == TunnelState.Failed);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Single(_host.Started);
        Assert.Contains("check", _shared.Commands);
        Assert.Empty(_shared.Forwards);
    }

    [Fact]
    public async Task When_a_terminal_has_signed_in_the_tunnel_rides_on_its_connection_with_an_anchor_to_hold_it()
    {
        var project = Remote();
        var spec = Local();
        var service = Service();
        var run = await StartRefused(service, project, spec);
        await InState(run, TunnelState.Waiting);

        _shared.MasterUp = true;

        await InState(run, TunnelState.Running);
        Assert.True(run.Status.Shared);
        Assert.Equal(["-L 127.0.0.1:5173:localhost:5173"], _shared.Forwards);
        var anchor = Assert.Single(Anchors);
        Assert.True(anchor.KeepsInputOpen);
        Assert.Contains("-o ControlPath=/ctl/%C", anchor.Line);
        Assert.Contains("-T -- prod cat", anchor.Line);
        // An identical forward is no error but a leftover is, so it is taken away before it is put in.
        var commands = _shared.Commands.ToList();
        Assert.True(commands.IndexOf("cancel -L 127.0.0.1:5173:localhost:5173") < commands.IndexOf("forward -L 127.0.0.1:5173:localhost:5173"));
    }

    [Fact]
    public async Task A_remote_tunnel_rides_on_the_shared_connection_too()
    {
        var project = Remote();
        var spec = Remote(9000);
        var service = Service();
        _shared.MasterUp = true;
        var run = await StartRefused(service, project, spec);

        await InState(run, TunnelState.Running);

        Assert.Equal(["-R 127.0.0.1:9000:localhost:3000"], _shared.Forwards);
        Assert.True(run.Status.Shared);
    }

    [Fact]
    public async Task When_the_connection_ends_the_tunnel_waits_again_and_goes_on_with_the_next_one()
    {
        var project = Remote();
        var spec = Local();
        var service = Service();
        _shared.MasterUp = true;
        var run = await StartRefused(service, project, spec);
        await InState(run, TunnelState.Running);
        var anchor = Assert.Single(Anchors);

        // The master ends and takes its forwards; the anchor, a session on it, ends too.
        _shared.EndMaster();
        anchor.Exit(255);
        await InState(run, TunnelState.Waiting);
        Assert.Empty(_shared.Forwards);

        _shared.MasterUp = true;

        await InState(run, TunnelState.Running);
        Assert.Equal(2, Anchors.Count);
        Assert.Equal(["-L 127.0.0.1:5173:localhost:5173"], _shared.Forwards);
    }

    [Fact]
    public async Task Stopping_takes_the_forward_out_of_the_connection_and_ends_the_anchor()
    {
        var project = Remote();
        var spec = Local();
        var service = Service();
        _shared.MasterUp = true;
        var run = await StartRefused(service, project, spec);
        await InState(run, TunnelState.Running);
        var anchor = Assert.Single(Anchors);

        service.Stop(spec.Id);

        Assert.Equal(TunnelState.Stopped, run.Status.State);
        await Eventually(() => _shared.Forwards.Count == 0, "the forward to be taken away");
        await Eventually(() => anchor.Killed, "the anchor to end");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(TunnelState.Stopped, run.Status.State);
    }

    [Fact]
    public async Task The_tunnels_of_a_host_share_one_anchor_that_ends_with_the_last_of_them()
    {
        var project = Remote();
        var first = Local(5173);
        var second = Local(5174);
        var service = Service();
        _shared.MasterUp = true;
        var run1 = await StartRefused(service, project, first);
        await InState(run1, TunnelState.Running);
        // The host is known to need the shared connection: no login of its own is tried for the next tunnel.
        service.Start(project, second);
        var run2 = service.RunOf(second.Id);
        await InState(run2, TunnelState.Running);

        Assert.Single(_host.Started, p => !p.IsAnchor);
        var anchor = Assert.Single(Anchors);
        Assert.Equal(2, _shared.Forwards.Count);

        service.Stop(first.Id);
        await Eventually(() => _shared.Forwards.Count == 1, "the first forward to go");
        await Task.Delay(60, TestContext.Current.CancellationToken);
        Assert.False(anchor.Killed);
        Assert.Equal(TunnelState.Running, run2.Status.State);

        service.Stop(second.Id);
        await Eventually(() => anchor.Killed, "the anchor to end with the last tunnel");
        Assert.Empty(_shared.Forwards);
    }

    [Fact]
    public async Task A_port_that_the_connection_cannot_bind_fails_the_tunnel_and_lets_go_of_the_anchor()
    {
        var project = Remote();
        var spec = Local();
        var service = Service();
        _shared.MasterUp = true;
        _shared.ForwardError = "mux_client_forward: forwarding request failed: Port forwarding failed";

        var run = await StartRefused(service, project, spec);

        await InState(run, TunnelState.Failed);
        Assert.Equal(TunnelFailure.LocalPortInUse, run.Status.Failure);
        Assert.Equal("mux_client_forward: forwarding request failed: Port forwarding failed", run.Status.Detail);
        await Eventually(() => Anchors.All(a => a.Killed), "the anchor to end");
    }

    [Fact]
    public async Task A_remote_port_that_the_host_refuses_fails_the_tunnel_in_the_words_of_the_remote_forward()
    {
        var project = Remote();
        var spec = Remote(9000);
        var service = Service();
        _shared.MasterUp = true;
        _shared.ForwardError = "mux_client_forward: forwarding request failed: remote port forwarding failed for listen port 9000";

        var run = await StartRefused(service, project, spec);

        await InState(run, TunnelState.Failed);
        Assert.Equal(TunnelFailure.RemoteForwardRefused, run.Status.Failure);
    }

    [Fact]
    public async Task A_forward_that_the_connection_refuses_for_another_reason_says_so_with_the_words_of_ssh()
    {
        var project = Remote();
        var spec = Local();
        var service = Service();
        _shared.MasterUp = true;
        _shared.ForwardError = "mux_client_forward: forwarding request failed: something unexpected";

        var run = await StartRefused(service, project, spec);

        await InState(run, TunnelState.Failed);
        Assert.Equal(TunnelFailure.SharedForwardFailed, run.Status.Failure);
        Assert.Contains("something unexpected", run.Status.Detail);
    }

    [Fact]
    public async Task Without_cat_on_the_host_the_tunnel_runs_without_an_anchor_and_asks_after_the_connection()
    {
        var project = Remote();
        var spec = Local();
        _host.AnchorsExitAtOnce = true;
        var service = Service();
        _shared.MasterUp = true;
        var run = await StartRefused(service, project, spec);

        await InState(run, TunnelState.Running);
        Assert.True(run.Status.Shared);

        // No anchor to end when the connection does: the tunnel finds out by asking.
        _shared.EndMaster();
        await InState(run, TunnelState.Waiting);
    }

    [Fact]
    public async Task Where_there_is_no_shared_connection_a_refused_login_fails_the_tunnel_as_before()
    {
        var project = Remote();
        var spec = Local();
        var none = new FakeSharedConnection { ClientOptions = [] };
        var service = Service(none);

        var run = await StartRefused(service, project, spec);

        await InState(run, TunnelState.Failed);
        Assert.Equal(TunnelFailure.LoginDenied, run.Status.Failure);
        Assert.Empty(none.Commands);
    }

    [Fact]
    public async Task Editing_a_tunnel_that_rides_on_the_connection_takes_its_old_forward_away_first()
    {
        var project = Remote();
        var spec = Local(5173);
        _projects.AddTunnel(project, spec);
        var service = Service();
        _shared.MasterUp = true;
        var run = await StartRefused(service, project, spec);
        await InState(run, TunnelState.Running);

        var edited = spec.Clone();
        edited.ListenPort = 7000;
        edited.TargetPort = 7000;
        _projects.UpdateTunnel(project, edited);

        await Eventually(() => _shared.Forwards.SequenceEqual(["-L 127.0.0.1:7000:localhost:7000"]), "only the new forward to be there");
        await InState(run, TunnelState.Running);
        var commands = _shared.Commands.ToList();
        Assert.True(commands.LastIndexOf("cancel -L 127.0.0.1:5173:localhost:5173") < commands.LastIndexOf("forward -L 127.0.0.1:7000:localhost:7000"));
    }

    [Fact]
    public async Task Ending_the_app_takes_every_forward_out_of_the_connection_before_it_returns()
    {
        var project = Remote();
        var service = Service();
        _shared.MasterUp = true;
        var a = Local(5173);
        var b = Remote(9000);
        var run = await StartRefused(service, project, a);
        service.Start(project, b);
        await InState(run, TunnelState.Running);
        await InState(service.RunOf(b.Id), TunnelState.Running);

        service.StopAll();

        // Not "soon": when StopAll is back, it is done.
        Assert.Empty(_shared.Forwards);
        Assert.All(Anchors, anchor => Assert.True(anchor.Killed));
    }

    [Fact]
    public async Task A_tunnel_that_is_stopped_while_it_waits_leaves_nothing_behind()
    {
        var project = Remote();
        var spec = Local();
        var service = Service();
        var run = await StartRefused(service, project, spec);
        await InState(run, TunnelState.Waiting);

        service.Stop(spec.Id);
        var commands = _shared.Commands.Count;
        _shared.MasterUp = true;
        await Task.Delay(150, TestContext.Current.CancellationToken);

        Assert.Equal(TunnelState.Stopped, run.Status.State);
        Assert.Empty(_shared.Forwards);
        Assert.Empty(Anchors);
        Assert.True(_shared.Commands.Count <= commands + 1, "it kept asking after it was stopped");
    }

    [Fact]
    public async Task A_tunnel_that_is_stopped_while_its_forward_is_being_put_in_does_not_leave_the_forward()
    {
        var project = Remote();
        var spec = Local();
        var service = Service();
        _shared.MasterUp = true;
        _shared.ForwardDelay = TimeSpan.FromMilliseconds(120);
        var run = await StartRefused(service, project, spec);
        await Eventually(() => _shared.Commands.Any(c => c.StartsWith("forward", StringComparison.Ordinal)), "the forward to be asked for");

        service.Stop(spec.Id);

        await Eventually(() => _shared.Commands.Count(c => c.StartsWith("cancel", StringComparison.Ordinal)) >= 2 && _shared.Forwards.Count == 0, "the forward to be taken away");
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(_shared.Forwards);
        Assert.Equal(TunnelState.Stopped, run.Status.State);
    }
}
