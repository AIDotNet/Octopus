using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Octopus.Mobile.Voice;

/// <summary>
/// The loudness of the last moments as rounded bars, newest on the right: each new <see cref="Level"/> pushes the
/// others one bar to the left.
/// </summary>
public sealed class VoiceWaveform : Control
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<VoiceWaveform, double>(nameof(Level));

    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<VoiceWaveform, IBrush?>(nameof(BarBrush), Brushes.White);

    private const double BarWidth = 3;
    private const double Gap = 2.5;
    private const double MinBar = 3;
    private readonly double[] _history = new double[64];
    private int _newest;

    static VoiceWaveform()
    {
        AffectsRender<VoiceWaveform>(BarBrushProperty);
    }

    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public IBrush? BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != LevelProperty) return;
        _newest = (_newest + 1) % _history.Length;
        // A little smoothing against the bars jumping on every chunk.
        var previous = _history[(_newest + _history.Length - 1) % _history.Length];
        _history[_newest] = Math.Clamp((0.65 * Level) + (0.35 * previous), 0, 1);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var brush = BarBrush;
        if (brush is null) return;
        var height = Bounds.Height;
        var count = Math.Min(_history.Length, (int)((Bounds.Width + Gap) / (BarWidth + Gap)));
        if (count <= 0 || height <= 0) return;
        // Centred: the bars fill from the middle when there are fewer than the width holds.
        var x = (Bounds.Width - ((count * (BarWidth + Gap)) - Gap)) / 2;
        for (var i = count - 1; i >= 0; i--)
        {
            var level = _history[(_newest - i + _history.Length) % _history.Length];
            var bar = Math.Max(MinBar, level * height);
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(x, (height - bar) / 2, BarWidth, bar), BarWidth / 2));
            x += BarWidth + Gap;
        }
    }
}
