using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Octopus.Mobile.Common;

/// <summary>
/// Tells when a finger (or a mouse button, in the desktop preview) rests on something inside <paramref name="root"/>
/// without moving: a long press. It listens on the way down, so a button under the finger that takes the press does
/// not hide it. Avalonia's own Holding gesture is only raised for a mouse on elements that ask for it one by one, and
/// a row's press lands on the parts of its template.
/// </summary>
/// <param name="pressed">Called with what was pressed; true when it opened something (the press is then used up).</param>
public sealed class LongPress
{
    /// <summary>How long the press has to rest.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);
    /// <summary>How far it may move and still count, in device-independent pixels; more is a scroll.</summary>
    private const double Slop = 10;

    private readonly Control _root;
    private readonly Func<StyledElement, bool> _pressed;
    private readonly DispatcherTimer _timer;
    private StyledElement? _source;
    private Point _start;

    public LongPress(Control root, Func<StyledElement, bool> pressed)
    {
        _root = root;
        _pressed = pressed;
        _timer = new DispatcherTimer { Interval = Delay };
        _timer.Tick += OnTick;
        root.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerReleasedEvent, (_, _) => Cancel(), RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => Cancel(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed || e.Source is not StyledElement source)
        {
            Cancel();
            return;
        }
        _source = source;
        _start = e.GetPosition(_root);
        _timer.Stop();
        _timer.Start();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_source is null) return;
        var moved = e.GetPosition(_root) - _start;
        if (Math.Abs(moved.X) > Slop || Math.Abs(moved.Y) > Slop) Cancel();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_source is { } source)
        {
            _source = null;
            _pressed(source);
        }
    }

    private void Cancel()
    {
        _timer.Stop();
        _source = null;
    }
}
