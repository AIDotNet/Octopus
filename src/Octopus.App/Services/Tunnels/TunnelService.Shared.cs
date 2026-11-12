using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Ssh;

namespace Octopus.App.Services;

/// <summary>
/// Tunnels that ride on the connection of a terminal (design/11-SSH隧道.md §4). A host that wants a password or a second
/// factor refuses the login of a tunnel's own ssh (it cannot ask anyone); the terminal's ssh has signed in and shares its
/// connection (<see cref="ISharedConnection"/>), so the tunnel adds its forward to that one. Differences from a tunnel
/// with a process of its own, all checked on real ssh:
/// <list type="bullet">
/// <item>The forward belongs to the master connection: no process stands for the tunnel, it is added with
/// <c>-O forward</c> and has to be taken away with <c>-O cancel</c> (it stays when the one who asked for it ends).</item>
/// <item>The master ends a few minutes after its last session even if forwards are listening (<c>ControlPersist</c> does not
/// count them): an <see cref="Anchor"/> session per host keeps it up, and when the anchor ends by itself the connection is gone.</item>
/// <item>There is no connection until the person signs in to the host in a terminal: the tunnel waits
/// (<see cref="TunnelState.Waiting"/>) and goes on by itself when there is.</item>
/// </list>
/// </summary>
public sealed partial class TunnelService
{
    private readonly HashSet<string> _sharedHosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Anchor> _anchors = new(StringComparer.Ordinal);
    private readonly List<Task> _pendingCancels = [];

    private bool CanUseSharedConnection => _runner is not null && _shared is { ClientOptions.Count: > 0 };

    private bool UsesSharedConnection(Entry entry)
    {
        lock (_gate) return CanUseSharedConnection && _sharedHosts.Contains(entry.Host);
    }

    private void MarkNeedsSharedConnection(Entry entry)
    {
        lock (_gate) _sharedHosts.Add(entry.Host);
    }

    /// <summary>
    /// A session on the shared connection of a host that does nothing but stay: while it runs the connection is not ended
    /// for being idle. Shared by the tunnels of the host; ended when the last of them lets go.
    /// </summary>
    private sealed class Anchor(IRunningProcess process, TaskCompletionSource lost)
    {
        public IRunningProcess Process { get; } = process;
        /// <summary>Completes when the anchor's ssh has ended: by itself that means the connection is gone.</summary>
        public TaskCompletionSource Lost { get; } = lost;
        public int Users { get; set; }
    }

    /// <summary>Called with the lock held (from <c>Start</c> and <c>Stop</c>): the forward this tunnel has in the shared connection is taken away.</summary>
    private void TakeAwayInstalledForward(Entry entry)
    {
        if (entry.Installed is not { } installed) return;
        entry.Installed = null;
        var task = CancelForwardAsync(entry.PendingCancel, entry.Host, installed);
        entry.PendingCancel = task;
        _pendingCancels.RemoveAll(t => t.IsCompleted);
        _pendingCancels.Add(task);
    }

    private async Task CancelForwardAsync(Task before, string host, TunnelSpec spec)
    {
        try
        {
            await before.ConfigureAwait(false);
            if (_runner is null || _shared is null) return;
            await _runner.RunAsync("ssh", SshSharedCommand.Cancel(host, spec, _shared.ClientOptions), timeout: _timings.SharedCommandTimeout).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
        {
            // Nothing more can be done: the forward goes with the connection.
        }
    }

    /// <summary>
    /// At the end of the app: the forwards that were being taken away are given a moment to be, and the anchors are ended
    /// (they would end by themselves with the app, which closes their input, but a person should not have to count on that).
    /// </summary>
    private void EndSharedSessions()
    {
        Task[] pending;
        Anchor[] anchors;
        lock (_gate)
        {
            pending = [.. _pendingCancels];
            anchors = [.. _anchors.Values];
            _anchors.Clear();
        }
        try
        {
            Task.WaitAll(pending, TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }
        foreach (var anchor in anchors) EndAnchor(anchor);
    }

    private static void EndAnchor(Anchor anchor)
    {
        anchor.Process.Kill();
        anchor.Process.Dispose();
    }

    /// <summary>
    /// The anchor of the host: the one that runs for another tunnel, or a new one. Null when there is none to have: ssh did
    /// not stay (the host has no <c>cat</c>, or there is no connection after all).
    /// </summary>
    private async Task<Anchor?> AcquireAnchorAsync(string host, IReadOnlyList<string> client, CancellationToken token)
    {
        lock (_gate)
        {
            if (_anchors.TryGetValue(host, out var existing) && !existing.Lost.Task.IsCompleted)
            {
                existing.Users++;
                return existing;
            }
        }

        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Its input stays open: that is what keeps cat waiting.
        var process = _host.Start("ssh", SshSharedCommand.Anchor(host, client), _ => { }, _ => lost.TrySetResult(), keepInputOpen: true);
        var anchor = new Anchor(process, lost) { Users = 1 };
        Anchor? other = null;
        lock (_gate)
        {
            if (_anchors.TryGetValue(host, out var existing) && !existing.Lost.Task.IsCompleted)
            {
                // Another tunnel was quicker.
                existing.Users++;
                other = existing;
            }
            else
            {
                _anchors[host] = anchor;
            }
        }
        if (other is not null)
        {
            EndAnchor(anchor);
            return other;
        }

        // An anchor that is not a session on a connection (there is none, or the host has no cat) ends within a moment.
        await Task.WhenAny(lost.Task, Task.Delay(_timings.AnchorSettle, token)).ConfigureAwait(false);
        if (!lost.Task.IsCompleted) return anchor;
        ReleaseAnchor(host, anchor);
        return null;
    }

    private void ReleaseAnchor(string host, Anchor? anchor)
    {
        if (anchor is null) return;
        var end = false;
        lock (_gate)
        {
            if (--anchor.Users > 0) return;
            end = true;
            if (_anchors.TryGetValue(host, out var current) && ReferenceEquals(current, anchor)) _anchors.Remove(host);
        }
        if (end) EndAnchor(anchor);
    }

    private Task<ProcessResult> RunSharedCommandAsync(IReadOnlyList<string> arguments, CancellationToken token) =>
        _runner!.RunAsync("ssh", arguments, timeout: _timings.SharedCommandTimeout, cancellationToken: token);

    private void SetWaiting(Entry entry, int generation)
    {
        var applied = false;
        var changed = false;
        lock (_gate)
        {
            applied = generation == entry.Generation && !_disposed;
            if (applied) changed = entry.Run.Update(status => status.State == TunnelState.Waiting ? null : new TunnelStatus(TunnelState.Waiting));
        }
        if (applied && changed) Publish(entry);
    }

    /// <summary>
    /// The line of a failed command's error output that says what went wrong: the first that ssh's wording is known for,
    /// otherwise the first that is not the fixed "muxclient: master forward request failed" that follows the reason.
    /// </summary>
    private static string ProblemLine(ProcessResult result)
    {
        var lines = result.StandardError.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.FirstOrDefault(l => SshTunnelLog.EventOf(l) != SshTunnelEvent.None)
            ?? lines.FirstOrDefault(l => !l.StartsWith("muxclient:", StringComparison.Ordinal))
            ?? lines.LastOrDefault()
            ?? "";
    }

    /// <summary>
    /// One tunnel on the shared connection. Goes round: no connection, so wait for one; a connection, so hold it with the
    /// anchor, put the forward in and run; the connection ends, so wait again. It only returns when the tunnel is stopped
    /// or cannot be had (a port that is taken, a forward that is refused).
    /// </summary>
    private async Task<Outcome> RunSharedAsync(Entry entry, int generation, CancellationToken token)
    {
        TunnelSpec spec;
        string host;
        Task pending;
        lock (_gate)
        {
            spec = entry.Spec;
            host = entry.Host;
            pending = entry.PendingCancel;
        }
        if (SshTunnelCommand.Validate(spec).Count > 0) return new Outcome(false, true, TunnelFailure.InvalidSetup, null);
        if (UsedByAnotherTunnel(entry, spec, host)) return new Outcome(false, true, TunnelFailure.PortUsedByTunnel, null);
        var client = _shared!.ClientOptions;
        // The forward that an earlier run put in the connection goes first, or its port would still be taken.
        await pending.ConfigureAwait(false);

        var cameUp = false;
        while (!token.IsCancellationRequested)
        {
            var check = await RunSharedCommandAsync(SshSharedCommand.Check(host, client), token).ConfigureAwait(false);
            if (!check.Succeeded)
            {
                SetWaiting(entry, generation);
                await Task.Delay(_timings.WaitPoll, token).ConfigureAwait(false);
                continue;
            }

            Anchor? anchor;
            try
            {
                anchor = await AcquireAnchorAsync(host, client, token).ConfigureAwait(false);
            }
            catch (ProcessStartException e)
            {
                return new Outcome(cameUp, true, TunnelFailure.StartFailed, e.Message);
            }

            try
            {
                // An identical forward is no error, but one with the same port and another target would be: away first.
                await RunSharedCommandAsync(SshSharedCommand.Cancel(host, spec, client), token).ConfigureAwait(false);
                var forward = await RunSharedCommandAsync(SshSharedCommand.Forward(host, spec, client), token).ConfigureAwait(false);
                if (!forward.Succeeded)
                {
                    var line = ProblemLine(forward);
                    var detail = SshProtocol.Clean(line);
                    switch (SshTunnelLog.EventOf(line))
                    {
                        case SshTunnelEvent.LocalPortUnavailable:
                            return new Outcome(cameUp, true, TunnelFailure.LocalPortInUse, detail);
                        case SshTunnelEvent.RemoteForwardRefused:
                            return new Outcome(cameUp, true, TunnelFailure.RemoteForwardRefused, detail);
                        case SshTunnelEvent.NoSharedConnection:
                            break;
                        default:
                            if (!forward.TimedOut) return new Outcome(cameUp, true, TunnelFailure.SharedForwardFailed, detail);
                            break;
                    }
                    // The connection went away between the check and now, or did not answer: back to waiting.
                    SetWaiting(entry, generation);
                    await Task.Delay(_timings.WaitPoll, token).ConfigureAwait(false);
                    continue;
                }

                bool stale;
                lock (_gate)
                {
                    stale = generation != entry.Generation || _disposed;
                    if (!stale) entry.Installed = spec;
                }
                if (stale)
                {
                    // Stopped while the forward was being put in: nobody else knows it is there.
                    await RunSharedCommandAsync(SshSharedCommand.Cancel(host, spec, client), CancellationToken.None).ConfigureAwait(false);
                    return new Outcome(cameUp, true, TunnelFailure.None, null);
                }

                cameUp = true;
                Transition(entry, generation, new TunnelStatus(TunnelState.Running, Shared: true));
                await HoldAsync(anchor, host, client, token).ConfigureAwait(false);
                lock (_gate)
                {
                    // It went with the connection (a forward taken away by a stop is already off the entry).
                    if (ReferenceEquals(entry.Installed, spec)) entry.Installed = null;
                }
                if (token.IsCancellationRequested) break;
                SetWaiting(entry, generation);
            }
            finally
            {
                ReleaseAnchor(host, anchor);
            }
        }
        return new Outcome(cameUp, true, TunnelFailure.None, null);
    }

    /// <summary>
    /// Returns when the connection is gone or the tunnel is stopped. The anchor ending says the first; without an anchor
    /// (a host that cannot run <c>cat</c>) the connection is asked after from time to time, and then it is also what ends
    /// it a few minutes after the terminal's last session.
    /// </summary>
    private async Task HoldAsync(Anchor? anchor, string host, IReadOnlyList<string> client, CancellationToken token)
    {
        if (anchor is not null)
        {
            var stopped = new TaskCompletionSource();
            using (token.Register(() => stopped.TrySetResult()))
            {
                await Task.WhenAny(anchor.Lost.Task, stopped.Task).ConfigureAwait(false);
            }
            return;
        }
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_timings.MonitorPoll, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            var check = await RunSharedCommandAsync(SshSharedCommand.Check(host, client), token).ConfigureAwait(false);
            if (!check.Succeeded) return;
        }
    }
}
