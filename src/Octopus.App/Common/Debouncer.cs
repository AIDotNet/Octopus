using Avalonia.Threading;

namespace Octopus.App.Common;

/// <summary>Runs the last scheduled action once typing has paused (300 ms), on the UI thread.</summary>
public sealed class Debouncer
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherTimer _timer;
    private Action? _pending;

    public Debouncer(TimeSpan? delay = null)
    {
        _timer = new DispatcherTimer { Interval = delay ?? DefaultDelay };
        _timer.Tick += (_, _) => Flush();
    }

    public bool IsPending => _pending is not null;

    public void Schedule(Action action)
    {
        _pending = action;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Runs the scheduled action now, if there is one.</summary>
    public void Flush()
    {
        _timer.Stop();
        var action = _pending;
        _pending = null;
        action?.Invoke();
    }

    public void Cancel()
    {
        _timer.Stop();
        _pending = null;
    }
}
