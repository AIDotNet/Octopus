using Avalonia;
using TKCode.Arc.Motion;

namespace Octopus.App.Shell;

/// <summary>
/// The width of a side panel as an animated value. Arc's own motion value does the work, so it follows the motion level
/// of the window (under Reduced and Off the width jumps), keeps its velocity when the target changes in mid-flight (a
/// second toggle turns the panel around without a jump) and runs on Arc's shared frame scheduler.
/// The spring is Arc's <see cref="ArcSpring.Smooth"/> (0.4 s, no bounce): the Motion page names it for "panels", and a
/// spring without bounce never overshoots the target width.
/// </summary>
internal sealed class PanelSlide : IDisposable
{
    private readonly MotionValue<double> _value;
    private int _generation;

    public PanelSlide(Visual owner, double initial)
    {
        _value = new MotionValue<double>(owner, initial, MotionVectors.Double);
        _value.Changed += width => Frame?.Invoke(width);
    }

    /// <summary>The current width.</summary>
    public double Width => _value.Value;

    /// <summary>True from the start of a slide until it has arrived (or was replaced by the next one that arrives).</summary>
    public bool IsSliding { get; private set; }

    /// <summary>Raised with the width on every step.</summary>
    public event Action<double>? Frame;

    /// <summary>Raised once when a slide has arrived at its target (also for a jump).</summary>
    public event Action? Arrived;

    /// <summary>Sets the width at once and stops any slide.</summary>
    public void Jump(double width)
    {
        _generation++;
        IsSliding = false;
        _value.Jump(width);
        Arrived?.Invoke();
    }

    /// <summary>Slides to the width; from the current width and velocity when a slide is already running.</summary>
    public async void Slide(double width)
    {
        var generation = ++_generation;
        IsSliding = true;
        // Under Reduced and Off the task is already complete and the width has jumped.
        var arrived = await _value.AnimateTo(width, MotionSpec.Spring(ArcSpring.Smooth));
        if (arrived && generation == _generation)
        {
            IsSliding = false;
            Arrived?.Invoke();
        }
    }

    public void Dispose() => _value.Dispose();
}
