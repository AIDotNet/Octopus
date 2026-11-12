using System.Diagnostics;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Ssh;

namespace Octopus.App.Services;

/// <summary>The tunnels of the projects on SSH hosts, and which of them are running (design/11-SSH隧道.md).</summary>
public interface ITunnelService : IDisposable
{
    /// <summary>
    /// Ends what an earlier run of the app left behind, starts to follow the projects (a tunnel of a removed project
    /// stops) and starts the automatic tunnels of the current project. Called once, when the app starts.
    /// </summary>
    void Initialize();

    /// <summary>The running state of a tunnel; a stopped one until it is started.</summary>
    TunnelRun RunOf(string tunnelId);

    /// <summary>Starts the tunnel (again, when it is running: with the spec as it is now).</summary>
    void Start(Project project, TunnelSpec tunnel);

    /// <summary>Stops the tunnel and ends its ssh process.</summary>
    void Stop(string tunnelId);

    /// <summary>Stops every tunnel. At the end of the app, so that no ssh process outlives it.</summary>
    void StopAll();

    /// <summary>Raised with the id of the tunnel whose status changed, on a background thread.</summary>
    event EventHandler<string>? Changed;
}

/// <summary>How long the tunnels wait for things. The defaults are for use; tests make them short.</summary>
public sealed class TunnelTimings
{
    /// <summary>How often a connecting tunnel looks whether its forward is up (and whether it took too long).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    /// <summary>How long ssh has to bring the forward up before the run is ended.</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>
    /// A remote forward is taken as up when this long has passed since the login with no refusal in the log: the host
    /// answers within a round trip, and a log line that says so is not sure to be there (design/11 §10).
    /// </summary>
    public TimeSpan RemoteGrace { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>The pauses before reconnecting, one per failure in a row; the last one repeats.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30)];
    /// <summary>How many runs in a row that end without the forward having come up it takes for the tunnel to give up.</summary>
    public int MaxFailuresInARow { get; init; } = 6;
    /// <summary>How long a port that is taken is waited for before the tunnel fails: the ssh process of an earlier run may still be ending.</summary>
    public TimeSpan PortGrace { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>How often a tunnel that waits for the shared connection of a terminal looks whether there is one.</summary>
    public TimeSpan WaitPoll { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>How long a new anchor session has to stay up before it counts as one (see <c>SshSharedCommand.Anchor</c>).</summary>
    public TimeSpan AnchorSettle { get; init; } = TimeSpan.FromMilliseconds(400);
    /// <summary>How often a tunnel on the shared connection without an anchor asks whether the connection is still there.</summary>
    public TimeSpan MonitorPoll { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>How long a short ssh command on the shared connection (check, forward, cancel) may take.</summary>
    public TimeSpan SharedCommandTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>How long a warning (a connection through the tunnel failed) is shown after the last one.</summary>
    public TimeSpan WarningLifetime { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed partial class TunnelService : ITunnelService
{
    private readonly IProcessHost _host;
    private readonly ILocalPorts _ports;
    private readonly IProjectService _projects;
    private readonly TunnelProcessLedger _ledger;
    private readonly TunnelTimings _timings;
    private readonly IProcessRunner? _runner;
    private readonly ISharedConnection? _shared;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _autoStarted = new(StringComparer.Ordinal);
    private bool _initialized;
    private bool _disposed;

    /// <param name="ledgerDirectory">Where the ssh processes are written down (a folder that may be deleted at any time).</param>
    /// <param name="runner">Runs the short ssh commands that add and take away the forwards of the shared connection.</param>
    /// <param name="shared">The connection of the terminals; without it (or where there is none) a host that needs a password cannot have tunnels.</param>
    public TunnelService(
        IProcessHost host, ILocalPorts ports, IProjectService projects, string ledgerDirectory, TunnelTimings? timings = null,
        IProcessRunner? runner = null, ISharedConnection? shared = null)
        : this(host, ports, projects, ledgerDirectory, Environment.ProcessId, timings, runner, shared)
    {
    }

    internal TunnelService(
        IProcessHost host, ILocalPorts ports, IProjectService projects, string ledgerDirectory, int ownerId, TunnelTimings? timings,
        IProcessRunner? runner = null, ISharedConnection? shared = null)
    {
        _host = host;
        _ports = ports;
        _projects = projects;
        _ledger = new TunnelProcessLedger(ledgerDirectory, ownerId);
        _timings = timings ?? new TunnelTimings();
        _runner = runner;
        _shared = shared;
    }

    public event EventHandler<string>? Changed;

    public void Initialize()
    {
        lock (_gate)
        {
            if (_initialized || _disposed) return;
            _initialized = true;
        }
        _ledger.EndLeftovers(_host);
        _projects.Changed += OnProjectsChanged;
        ProjectActivated(_projects.Current);
    }

    public TunnelRun RunOf(string tunnelId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(tunnelId, out var entry))
            {
                _entries[tunnelId] = entry = new Entry(new TunnelRun(tunnelId), "", new TunnelSpec { Id = tunnelId });
            }
            return entry.Run;
        }
    }

    public void Start(Project project, TunnelSpec tunnel)
    {
        if (project.SshHost is not { } host) return;
        Entry entry;
        int generation;
        CancellationToken token;
        CancellationTokenSource? previous;
        IRunningProcess? running;
        lock (_gate)
        {
            if (_disposed) return;
            if (!_entries.TryGetValue(tunnel.Id, out entry!))
            {
                _entries[tunnel.Id] = entry = new Entry(new TunnelRun(tunnel.Id), host, tunnel);
            }
            // A forward that the run before this one put in the shared connection is still there: it goes first.
            TakeAwayInstalledForward(entry);
            entry.Host = host;
            entry.Spec = tunnel;
            // Whatever ran before is over: its supervisor sees the new generation and leaves the status alone.
            generation = ++entry.Generation;
            previous = entry.Cts;
            entry.Cts = new CancellationTokenSource();
            token = entry.Cts.Token;
            running = entry.Process;
            entry.Process = null;
            entry.Run.Assign(new TunnelStatus(TunnelState.Connecting));
        }
        previous?.Cancel();
        running?.Kill();
        entry.Run.ClearLog();
        Publish(entry);
        _ = SuperviseAsync(entry, generation, token);
    }

    public void Stop(string tunnelId)
    {
        Entry entry;
        CancellationTokenSource? cts;
        IRunningProcess? running;
        lock (_gate)
        {
            if (!_entries.TryGetValue(tunnelId, out entry!)) return;
            TakeAwayInstalledForward(entry);
            entry.Generation++;
            cts = entry.Cts;
            entry.Cts = null;
            running = entry.Process;
            entry.Process = null;
            entry.Run.Assign(TunnelStatus.Stopped);
        }
        cts?.Cancel();
        running?.Kill();
        Publish(entry);
    }

    public void StopAll()
    {
        string[] ids;
        lock (_gate) ids = [.. _entries.Keys];
        foreach (var id in ids) Stop(id);
        EndSharedSessions();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _projects.Changed -= OnProjectsChanged;
        StopAll();
    }

    // ---- following the projects ----

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (e.Kind is ProjectChangeKind.Projects or ProjectChangeKind.Tunnels) Sync();
        if (e.Kind is ProjectChangeKind.Projects or ProjectChangeKind.CurrentProject) ProjectActivated(_projects.Current);
    }

    /// <summary>
    /// A tunnel that is no longer in a project on a host stops and is forgotten; one whose spec was replaced (it was
    /// edited) runs again with the new one, if it was running.
    /// </summary>
    private void Sync()
    {
        Entry[] entries;
        lock (_gate) entries = [.. _entries.Values];
        foreach (var entry in entries)
        {
            var id = entry.Run.Id;
            var found = Find(id);
            if (found is null)
            {
                Stop(id);
                lock (_gate) _entries.Remove(id);
                continue;
            }
            bool changed;
            lock (_gate) changed = !ReferenceEquals(entry.Spec, found.Value.Tunnel);
            if (changed && entry.Run.Status.IsActive) Start(found.Value.Project, found.Value.Tunnel);
            else if (changed) lock (_gate) entry.Spec = found.Value.Tunnel;
        }
    }

    private (Project Project, TunnelSpec Tunnel)? Find(string tunnelId)
    {
        foreach (var project in _projects.Projects)
        {
            if (project.SshHost is null) continue;
            if (project.Tunnels.FirstOrDefault(t => t.Id == tunnelId) is { } tunnel) return (project, tunnel);
        }
        return null;
    }

    /// <summary>The automatic tunnels of a project start the first time it is the current one in a run of the app.</summary>
    private void ProjectActivated(Project? project)
    {
        if (project?.SshHost is null) return;
        lock (_gate)
        {
            if (_disposed || !_autoStarted.Add(project.Id)) return;
        }
        foreach (var tunnel in project.Tunnels.Where(t => t.AutoStart).ToList()) Start(project, tunnel);
    }

    // ---- one tunnel ----

    private sealed class Entry(TunnelRun run, string host, TunnelSpec spec)
    {
        public TunnelRun Run { get; } = run;
        public string Host { get; set; } = host;
        public TunnelSpec Spec { get; set; } = spec;
        /// <summary>Counts the starts and stops: work that belongs to an older number is over and must not touch the status.</summary>
        public int Generation { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public IRunningProcess? Process { get; set; }
        /// <summary>The forward this tunnel has put in the shared connection (the spec it was added with, which taking it away needs); null: none.</summary>
        public TunnelSpec? Installed { get; set; }
        /// <summary>The taking away of a forward that is under way: the next one waits for it, or the port would still be taken.</summary>
        public Task PendingCancel { get; set; } = Task.CompletedTask;
    }

    /// <summary>How one run of ssh went.</summary>
    private sealed record Outcome(bool CameUp, bool GiveUp, TunnelFailure Failure, string? Detail);

    /// <summary>What the log of one run has said so far. Written by the thread reading the log, read by the one waiting.</summary>
    private sealed class RunLog
    {
        private readonly object _gate = new();
        private long? _loggedInAt;
        private (SshTunnelEvent Event, string Line)? _ending;
        private string? _loss;
        private string? _last;

        public bool Confirmed { get; private set; }

        public bool SessionOpen { get; private set; }

        public void Open()
        {
            lock (_gate) SessionOpen = true;
        }

        public void Login()
        {
            lock (_gate) _loggedInAt ??= Stopwatch.GetTimestamp();
        }

        public void Confirm()
        {
            lock (_gate) Confirmed = true;
        }

        public void End(SshTunnelEvent value, string line)
        {
            lock (_gate) _ending ??= (value, line);
        }

        public void Lose(string line)
        {
            lock (_gate) _loss = line;
        }

        public void Say(string line)
        {
            lock (_gate) _last = line;
        }

        public TimeSpan? SinceLogin
        {
            get
            {
                lock (_gate) return _loggedInAt is { } at ? Stopwatch.GetElapsedTime(at) : null;
            }
        }

        public (SshTunnelEvent Event, string Line)? Ending
        {
            get
            {
                lock (_gate) return _ending;
            }
        }

        public string? Loss
        {
            get
            {
                lock (_gate) return _loss;
            }
        }

        public string? Last
        {
            get
            {
                lock (_gate) return _last;
            }
        }
    }

    private async Task SuperviseAsync(Entry entry, int generation, CancellationToken token)
    {
        var failures = 0;
        var attempt = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var shared = UsesSharedConnection(entry);
                var outcome = shared
                    ? await RunSharedAsync(entry, generation, token).ConfigureAwait(false)
                    : await RunOnceAsync(entry, generation, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                if (!shared && outcome.Failure == TunnelFailure.LoginDenied && CanUseSharedConnection)
                {
                    // The host wants a password or a second factor, which a tunnel cannot give: it rides on the
                    // connection of a terminal instead (and so do the next tunnels to this host, without trying first).
                    MarkNeedsSharedConnection(entry);
                    continue;
                }
                // A run that came up and was lost is the network's doing, not a reason to give up: it starts the count over.
                failures = outcome.CameUp ? 0 : failures + 1;
                if (outcome.GiveUp || !entry.Spec.AutoReconnect || failures >= _timings.MaxFailuresInARow)
                {
                    Transition(entry, generation, new TunnelStatus(TunnelState.Failed, outcome.Failure, outcome.Detail));
                    return;
                }
                attempt++;
                Transition(entry, generation, new TunnelStatus(TunnelState.Reconnecting, outcome.Failure, outcome.Detail, attempt));
                var delays = _timings.RetryDelays;
                await Task.Delay(delays.Count == 0 ? TimeSpan.Zero : delays[Math.Clamp(failures, 1, delays.Count) - 1], token).ConfigureAwait(false);
                Transition(entry, generation, new TunnelStatus(TunnelState.Connecting, Attempt: attempt));
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped while waiting to reconnect: Stop has set the status.
        }
    }

    private async Task<Outcome> RunOnceAsync(Entry entry, int generation, CancellationToken token)
    {
        TunnelSpec spec;
        string host;
        lock (_gate)
        {
            spec = entry.Spec;
            host = entry.Host;
        }
        if (SshTunnelCommand.Validate(spec).Count > 0) return new Outcome(false, true, TunnelFailure.InvalidSetup, null);
        if (UsedByAnotherTunnel(entry, spec, host)) return new Outcome(false, true, TunnelFailure.PortUsedByTunnel, null);
        if (spec.Kind == TunnelKind.Local && !await PortFreeAsync(spec.ListenPort, token).ConfigureAwait(false))
        {
            return new Outcome(false, true, TunnelFailure.LocalPortInUse, null);
        }

        var log = new RunLog();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        IRunningProcess process;
        try
        {
            process = _host.Start("ssh", SshTunnelCommand.Arguments(host, spec), line => OnLine(entry, generation, log, line), code => exited.TrySetResult(code));
        }
        catch (ProcessStartException e)
        {
            return new Outcome(false, true, TunnelFailure.StartFailed, e.Message);
        }

        bool stale;
        lock (_gate)
        {
            stale = generation != entry.Generation || _disposed;
            if (!stale) entry.Process = process;
        }
        if (stale)
        {
            process.Kill();
            process.Dispose();
            return new Outcome(false, true, TunnelFailure.None, null);
        }
        _ledger.Add(process);

        var cameUp = false;
        var timedOut = false;
        var timer = Stopwatch.StartNew();
        try
        {
            while (!exited.Task.IsCompleted && !token.IsCancellationRequested)
            {
                if (!cameUp && log.Ending is null && IsUp(spec, log))
                {
                    cameUp = true;
                    Transition(entry, generation, new TunnelStatus(TunnelState.Running));
                }
                if (!cameUp && timer.Elapsed > _timings.ReadyTimeout)
                {
                    timedOut = true;
                    process.Kill();
                    break;
                }
                await Task.WhenAny(exited.Task, Task.Delay(_timings.PollInterval, CancellationToken.None)).ConfigureAwait(false);
            }
            if (token.IsCancellationRequested) process.Kill();
            // The process is ending: its last lines (the reason) arrive before the exit is reported.
            await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            _ledger.Remove(process);
            lock (_gate)
            {
                if (ReferenceEquals(entry.Process, process)) entry.Process = null;
            }
            process.Dispose();
        }

        if (log.Ending is { } ending)
        {
            return new Outcome(cameUp, true, FailureOf(ending.Event), SshProtocol.Clean(ending.Line));
        }
        if (timedOut) return new Outcome(cameUp, false, TunnelFailure.TimedOut, log.Last);
        if (log.Loss is { } loss) return new Outcome(cameUp, false, TunnelFailure.ConnectionLost, loss);
        return new Outcome(cameUp, false, cameUp ? TunnelFailure.ConnectionLost : TunnelFailure.Exited, log.Last);
    }

    /// <summary>
    /// Whether the port can be listened on. A tunnel that is started again (it was edited) finds its own ssh process,
    /// just ended, still holding the port for a moment: the port gets a short time to come free before it counts as taken.
    /// </summary>
    private async Task<bool> PortFreeAsync(int port, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (!_ports.IsFree(port))
        {
            if (timer.Elapsed >= _timings.PortGrace || token.IsCancellationRequested) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, _timings.PortGrace.TotalMilliseconds / 4)), CancellationToken.None).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// A local forward is up when ssh has opened its session: it sets the listening port up before that, and a port that
    /// cannot be bound ends the run before it gets there (checked on real runs: design/11-SSH隧道.md §10). The port is not
    /// looked at: the system's list of listening ports does not show another process's on macOS, and connecting to it would
    /// make ssh connect to the target. A remote forward is up when the host said so, which comes after the session is
    /// open, or when the login is a while ago and nothing was refused (for a wording of ssh's that is not known).
    /// </summary>
    private bool IsUp(TunnelSpec spec, RunLog log) =>
        spec.Kind == TunnelKind.Local
            ? log.SessionOpen
            : log.Confirmed || log.SinceLogin >= _timings.RemoteGrace;

    private void OnLine(Entry entry, int generation, RunLog log, string line)
    {
        var text = SshProtocol.Clean(line);
        if (text.Length == 0) return;
        entry.Run.AppendLog(text);
        log.Say(text);
        var value = SshTunnelLog.EventOf(line);
        switch (value)
        {
            case SshTunnelEvent.Authenticated:
                log.Login();
                break;
            case SshTunnelEvent.SessionOpen:
                log.Login();
                log.Open();
                break;
            case SshTunnelEvent.ForwardConfirmed:
                log.Login();
                log.Open();
                log.Confirm();
                break;
            case SshTunnelEvent.ConnectionLost:
                log.Lose(text);
                break;
            case SshTunnelEvent.TargetUnreachable:
                Warn(entry, generation, TunnelWarning.TargetUnreachable);
                break;
            case SshTunnelEvent.ForwardingProhibited:
                Warn(entry, generation, TunnelWarning.ForwardingProhibited);
                break;
            default:
                if (SshTunnelLog.NeedsChange(value)) log.End(value, line);
                break;
        }
    }

    /// <summary>
    /// Shows that a connection through the tunnel failed, for a while after the last time. A burst of failing connections
    /// refreshes the time but raises one change.
    /// </summary>
    private void Warn(Entry entry, int generation, TunnelWarning warning)
    {
        var raised = false;
        bool updated;
        lock (_gate)
        {
            updated = generation == entry.Generation && !_disposed && entry.Run.Update(status =>
            {
                if (status.State != TunnelState.Running) return null;
                raised = status.Warning != warning;
                return status with { Warning = warning, WarningAt = Stopwatch.GetTimestamp() };
            });
        }
        if (!updated || !raised) return;
        Publish(entry);
        _ = ClearWarningLaterAsync(entry);
    }

    /// <summary>Takes the warning away once it has not been refreshed for <see cref="TunnelTimings.WarningLifetime"/>.</summary>
    private async Task ClearWarningLaterAsync(Entry entry)
    {
        while (true)
        {
            TimeSpan remaining;
            lock (_gate)
            {
                var status = entry.Run.Status;
                if (status.Warning == TunnelWarning.None) return;
                remaining = _timings.WarningLifetime - Stopwatch.GetElapsedTime(status.WarningAt);
            }
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining).ConfigureAwait(false);
                continue;
            }
            bool cleared;
            lock (_gate)
            {
                cleared = entry.Run.Update(status => status.Warning == TunnelWarning.None ? null : status with { Warning = TunnelWarning.None });
            }
            if (cleared) Publish(entry);
            return;
        }
    }

    /// <summary>Another running tunnel already listens on this port: on this computer for a local forward, on the same host for a remote one.</summary>
    private bool UsedByAnotherTunnel(Entry entry, TunnelSpec spec, string host)
    {
        lock (_gate)
        {
            return _entries.Values.Any(other =>
                !ReferenceEquals(other, entry)
                && other.Run.Status.IsActive
                && other.Spec.Kind == spec.Kind
                && other.Spec.ListenPort == spec.ListenPort
                && (spec.Kind == TunnelKind.Local || other.Host == host));
        }
    }

    private static TunnelFailure FailureOf(SshTunnelEvent value) => value switch
    {
        SshTunnelEvent.LocalPortUnavailable => TunnelFailure.LocalPortInUse,
        SshTunnelEvent.RemoteForwardRefused => TunnelFailure.RemoteForwardRefused,
        SshTunnelEvent.LoginDenied => TunnelFailure.LoginDenied,
        SshTunnelEvent.HostKeyRejected => TunnelFailure.HostKeyRejected,
        _ => TunnelFailure.Exited,
    };

    /// <summary>Sets the status, unless the tunnel was started or stopped again since this work began.</summary>
    private void Transition(Entry entry, int generation, TunnelStatus status)
    {
        bool applied;
        lock (_gate)
        {
            applied = generation == entry.Generation && !_disposed;
            if (applied) entry.Run.Assign(status);
        }
        if (applied) Publish(entry);
    }

    private void Publish(Entry entry)
    {
        entry.Run.RaiseChanged();
        Changed?.Invoke(this, entry.Run.Id);
    }
}
