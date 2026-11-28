using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Octopus.App.Plugins;
using Octopus.Plugins.Themes;

namespace Octopus.App.Appearance;

/// <summary>
/// Renders a subtle declarative background grid or dot matrix when the active plugin theme enables
/// <see cref="Octopus.Core.Plugins.AmbientGridEffect"/>. Zero cost when disabled.
/// </summary>
public sealed class ThemeAmbientGridOverlay : Control
{
    public ThemeAmbientGridOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        PluginThemes.ActiveEffectsChanged += OnEffectsChanged;
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        PluginThemes.ActiveEffectsChanged -= OnEffectsChanged;
    }

    private void OnEffectsChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var grid = PluginThemes.CurrentEffects?.AmbientGrid;
        if (grid is null || grid.Opacity <= 0.001) return;

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 8 || h < 8) return;

        var tint = Color.FromRgb(0x38, 0xBD, 0xF8);
        if (ThemeColor.TryParse(PluginThemes.CurrentAccentHex, out var acc))
        {
            tint = Color.FromRgb(acc.R, acc.G, acc.B);
        }

        var alpha = (byte)Math.Clamp((int)Math.Round(255 * grid.Opacity), 1, 80);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, tint.R, tint.G, tint.B));

        if (string.Equals(grid.Pattern, "dots", StringComparison.OrdinalIgnoreCase))
        {
            const double step = 24.0;
            for (var y = step * 0.5; y < h; y += step)
            {
                for (var x = step * 0.5; x < w; x += step)
                {
                    context.DrawEllipse(brush, null, new Point(x, y), 1.0, 1.0);
                }
            }
            return;
        }

        const double cell = 28.0;
        var pen = new Pen(brush, 1.0);
        for (var x = cell; x < w; x += cell)
        {
            context.DrawLine(pen, new Point(x, 0), new Point(x, h));
        }
        for (var y = cell; y < h; y += cell)
        {
            context.DrawLine(pen, new Point(0, y), new Point(w, y));
        }
    }
}
