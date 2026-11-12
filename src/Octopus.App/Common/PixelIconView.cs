using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Octopus.App.Common;

/// <summary>
/// Draws a <see cref="PixelIcon"/> in one brush. Its pixels are drawn a whole number of device-independent pixels wide
/// when the control is at least 24 px (24 px: one each, 48 px: two each), centred, with aliased edges, so the art stays
/// crisp; below 24 px they are scaled down to fit.
/// </summary>
public sealed class PixelIconView : Control
{
    public static readonly StyledProperty<PixelIcon?> IconProperty =
        AvaloniaProperty.Register<PixelIconView, PixelIcon?>(nameof(Icon));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<PixelIconView, IBrush?>(nameof(Foreground), Brushes.White);

    static PixelIconView()
    {
        AffectsRender<PixelIconView>(IconProperty, ForegroundProperty);
    }

    public PixelIconView()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public PixelIcon? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Icon is not { } icon || Foreground is not { } brush)
        {
            return;
        }
        var side = Math.Min(Bounds.Width, Bounds.Height);
        var unit = side / PixelIcon.Size;
        if (unit >= 1)
        {
            unit = Math.Floor(unit);
        }
        var x0 = Math.Round((Bounds.Width - unit * PixelIcon.Size) / 2);
        var y0 = Math.Round((Bounds.Height - unit * PixelIcon.Size) / 2);
        foreach (var r in icon.Rects)
        {
            context.FillRectangle(brush, new Rect(x0 + r.X * unit, y0 + r.Y * unit, r.Width * unit, r.Height * unit));
        }
    }
}
