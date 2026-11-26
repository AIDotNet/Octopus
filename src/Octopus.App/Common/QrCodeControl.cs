using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Octopus.Remote.Security;

namespace Octopus.App.Common;

/// <summary>
/// Renders a <see cref="QrCodeMatrix"/> as crisp, high-contrast vector modules with a standard quiet zone so phones
/// scan reliably in both light and dark desktop themes.
/// </summary>
public sealed class QrCodeControl : Control
{
    public static readonly StyledProperty<QrCodeMatrix?> MatrixProperty =
        AvaloniaProperty.Register<QrCodeControl, QrCodeMatrix?>(nameof(Matrix));

    public static readonly StyledProperty<int> QuietZoneProperty =
        AvaloniaProperty.Register<QrCodeControl, int>(nameof(QuietZone), defaultValue: 3);

    public static readonly StyledProperty<IBrush> ForegroundProperty =
        AvaloniaProperty.Register<QrCodeControl, IBrush>(nameof(Foreground), defaultValue: Brushes.Black);

    public static readonly StyledProperty<IBrush> BackgroundProperty =
        AvaloniaProperty.Register<QrCodeControl, IBrush>(nameof(Background), defaultValue: Brushes.White);

    static QrCodeControl()
    {
        AffectsRender<QrCodeControl>(MatrixProperty, QuietZoneProperty, ForegroundProperty, BackgroundProperty);
    }

    public QrCodeMatrix? Matrix
    {
        get => GetValue(MatrixProperty);
        set => SetValue(MatrixProperty, value);
    }

    public int QuietZone
    {
        get => GetValue(QuietZoneProperty);
        set => SetValue(QuietZoneProperty, value);
    }

    public IBrush Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var background = Background ?? Brushes.White;
        context.FillRectangle(background, bounds);

        var matrix = Matrix;
        if (matrix is null || matrix.Size <= 0) return;

        var quiet = Math.Max(0, QuietZone);
        var totalModules = matrix.Size + quiet * 2;
        var side = Math.Min(bounds.Width, bounds.Height);
        var moduleSize = side / totalModules;
        if (moduleSize <= 0) return;

        var offsetX = (bounds.Width - side) / 2.0 + quiet * moduleSize;
        var offsetY = (bounds.Height - side) / 2.0 + quiet * moduleSize;
        var foreground = Foreground ?? Brushes.Black;

        // Slight overlap (0.25 DIP) prevents hairline anti-aliasing seams between adjacent dark modules on fractional DPI.
        var overlap = moduleSize >= 1.5 ? 0.25 : 0.0;
        for (var y = 0; y < matrix.Size; y++)
        {
            var py = offsetY + y * moduleSize;
            var x = 0;
            while (x < matrix.Size)
            {
                if (!matrix[x, y])
                {
                    x++;
                    continue;
                }
                var runStart = x;
                while (x < matrix.Size && matrix[x, y]) x++;
                var px = offsetX + runStart * moduleSize;
                var width = (x - runStart) * moduleSize + overlap;
                context.FillRectangle(foreground, new Rect(px, py, width, moduleSize + overlap));
            }
        }
    }
}
