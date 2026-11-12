namespace Octopus.App.Services;

public enum TunnelState
{
    Stopped,
    /// <summary>ssh is started and the forward is not up yet.</summary>
    Connecting,
    Running,
    /// <summary>The connection was lost; ssh is started again after a pause.</summary>
    Reconnecting,
    /// <summary>
    /// The host needs a password or a second factor, so the tunnel rides on the connection of a terminal, and there is
    /// none (yet, or any more). It starts by itself when one is made. Not a failure: the person has to sign in.
    /// </summary>
    Waiting,
    /// <summary>It ended and will not be started again by itself: see <see cref="TunnelStatus.Failure"/>.</summary>
    Failed
}

/// <summary>Why a tunnel is not running (or is being connected again).</summary>
public enum TunnelFailure
{
    None,
    /// <summary>The spec cannot be used (an address or a port is not valid).</summary>
    InvalidSetup,
    /// <summary>The port on this computer is taken by another program.</summary>
    LocalPortInUse,
    /// <summary>Another tunnel of the app listens on the same port.</summary>
    PortUsedByTunnel,
    /// <summary>The host did not give the port (it is taken, or the server does not allow forwarding).</summary>
    RemoteForwardRefused,
    /// <summary>The host refused the login: a tunnel cannot type a password, it needs a key or an agent.</summary>
    LoginDenied,
    HostKeyRejected,
    /// <summary>The connection could not be made or was lost.</summary>
    ConnectionLost,
    /// <summary>The forward was not up in time.</summary>
    TimedOut,
    /// <summary>The ssh program could not be started.</summary>
    StartFailed,
    /// <summary>ssh ended and said nothing the app knows.</summary>
    Exited,
    /// <summary>The shared connection did not take the forward (and it is not a port that is taken).</summary>
    SharedForwardFailed
}

/// <summary>A problem of a running tunnel that does not stop it: a connection through it failed.</summary>
public enum TunnelWarning
{
    None,
    /// <summary>The target does not accept connections (nothing listens there).</summary>
    TargetUnreachable,
    /// <summary>The server does not allow forwarding (<c>AllowTcpForwarding no</c>).</summary>
    ForwardingProhibited
}

/// <summary>What is known of a tunnel at a moment. Replaced as a whole, never changed.</summary>
/// <param name="Detail">The line of ssh's log behind <paramref name="Failure"/>, as ssh said it; null when there is none.</param>
/// <param name="Attempt">While reconnecting: which attempt it is, counting from 1.</param>
/// <param name="Shared">Running on the connection of a terminal rather than on one of its own.</param>
public sealed record TunnelStatus(
    TunnelState State,
    TunnelFailure Failure = TunnelFailure.None,
    string? Detail = null,
    int Attempt = 0,
    TunnelWarning Warning = TunnelWarning.None,
    bool Shared = false)
{
    /// <summary>When <see cref="Warning"/> was last seen (a <c>Stopwatch</c> timestamp).</summary>
    internal long WarningAt { get; init; }

    public bool IsActive => State is TunnelState.Connecting or TunnelState.Running or TunnelState.Reconnecting or TunnelState.Waiting;

    public static readonly TunnelStatus Stopped = new(TunnelState.Stopped);
}

/// <summary>
/// One tunnel as it runs: its status and the last lines of ssh's log. <see cref="Changed"/> is raised on a background
/// thread, so a view has to move to its own thread first.
/// </summary>
public sealed class TunnelRun(string id)
{
    private const int MaxLines = 200;

    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private TunnelStatus _status = TunnelStatus.Stopped;

    public string Id { get; } = id;

    public TunnelStatus Status
    {
        get
        {
            lock (_gate) return _status;
        }
    }

    public event EventHandler? Changed;

    /// <summary>The latest lines of ssh's log, oldest first, without the debug prefix.</summary>
    public IReadOnlyList<string> Log()
    {
        lock (_gate) return [.. _lines];
    }

    internal void Assign(TunnelStatus status)
    {
        lock (_gate) _status = status;
    }

    /// <summary>Changes the status in place; false (and no change) when <paramref name="change"/> returns null.</summary>
    internal bool Update(Func<TunnelStatus, TunnelStatus?> change)
    {
        lock (_gate)
        {
            if (change(_status) is not { } next) return false;
            _status = next;
            return true;
        }
    }

    internal void AppendLog(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > MaxLines) _lines.Dequeue();
        }
    }

    internal void ClearLog()
    {
        lock (_gate) _lines.Clear();
    }

    internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
