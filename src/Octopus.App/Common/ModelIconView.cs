using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Octopus.App.Common;

/// <summary>
/// Renders a LobeHub AI model/provider icon directly into <see cref="DrawingContext"/> with zero visual child
/// controls. When <see cref="IconKey"/> is null or unknown, renders a clean Lucide-style sparkles glyph in the
/// inherited foreground color so every model row and trigger chip stays visually aligned.
/// </summary>
public sealed class ModelIconView : Control
{
    public static readonly StyledProperty<string?> IconKeyProperty =
        AvaloniaProperty.Register<ModelIconView, string?>(nameof(IconKey));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<ModelIconView, double>(nameof(Size), 14);

    // Lucide "sparkles" icon (24x24 viewBox, strokeWidth=2) used as the neutral fallback for unknown/default models.
    private static readonly Geometry FallbackSparkleMain = StreamGeometry.Parse(
        "M 11.017 2.814 A 1 1 0 0 1 12.983 2.814 L 14.034 6.071 A 2 2 0 0 0 15.929 7.966 L 19.186 9.017 A 1 1 0 0 1 19.186 10.983 L 15.929 12.034 A 2 2 0 0 0 14.034 13.929 L 12.983 17.186 A 1 1 0 0 1 11.017 17.186 L 9.966 13.929 A 2 2 0 0 0 8.071 12.034 L 4.814 10.983 A 1 1 0 0 1 4.814 9.017 L 8.071 7.966 A 2 2 0 0 0 9.966 6.071 Z");

    private static readonly Geometry FallbackSparkleTopRight = StreamGeometry.Parse(
        "M 20 2 V 6 M 22 4 H 18");

    private static readonly Geometry FallbackSparkleBottomLeft = StreamGeometry.Parse(
        "M 4 17 A 1 1 0 1 0 4 19 A 1 1 0 1 0 4 17 Z");

    static ModelIconView()
    {
        AffectsRender<ModelIconView>(IconKeyProperty, SizeProperty);
        AffectsMeasure<ModelIconView>(SizeProperty);
    }

    public ModelIconView()
    {
        IsHitTestVisible = false;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public string? IconKey
    {
        get => GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = Math.Max(0, Size);
        return new Size(s, s);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var graphic = ModelIconStore.Instance.Get(IconKey);
        if (graphic is null)
        {
            RenderFallbackSparkles(context, width, height);
            return;
        }

        var vb = graphic.ViewBox;
        var scale = Math.Min(width / vb.Width, height / vb.Height);
        var dx = (width - vb.Width * scale) / 2.0 - vb.X * scale;
        var dy = (height - vb.Height * scale) / 2.0 - vb.Y * scale;
        var baseMatrix = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy);

        using var _ = context.PushTransform(baseMatrix);
        IBrush? currentColor = null;

        foreach (var shape in graphic.Shapes)
        {
            var fill = shape.FillUsesCurrentColor
                ? (currentColor ??= TextElement.GetForeground(this) ?? Brushes.Gray)
                : shape.Fill;

            var stroke = shape.StrokeUsesCurrentColor
                ? new Pen(currentColor ??= TextElement.GetForeground(this) ?? Brushes.Gray, shape.StrokeWidth)
                : shape.Stroke;

            var hasOpacity = shape.Opacity < 0.999;
            var hasTransform = shape.Transform.HasValue;

            if (hasOpacity && hasTransform)
            {
                using var op = context.PushOpacity(shape.Opacity);
                using var tr = context.PushTransform(shape.Transform!.Value);
                context.DrawGeometry(fill, stroke, shape.Geometry);
            }
            else if (hasOpacity)
            {
                using var op = context.PushOpacity(shape.Opacity);
                context.DrawGeometry(fill, stroke, shape.Geometry);
            }
            else if (hasTransform)
            {
                using var tr = context.PushTransform(shape.Transform!.Value);
                context.DrawGeometry(fill, stroke, shape.Geometry);
            }
            else
            {
                context.DrawGeometry(fill, stroke, shape.Geometry);
            }
        }
    }

    private void RenderFallbackSparkles(DrawingContext context, double width, double height)
    {
        var scale = Math.Min(width, height) / 24.0;
        var dx = (width - 24.0 * scale) / 2.0;
        var dy = (height - 24.0 * scale) / 2.0;
        var matrix = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy);
        var fg = TextElement.GetForeground(this) ?? Brushes.Gray;
        var pen = new Pen(fg, 2.0, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        using var _ = context.PushTransform(matrix);
        context.DrawGeometry(null, pen, FallbackSparkleMain);
        context.DrawGeometry(null, pen, FallbackSparkleTopRight);
        context.DrawGeometry(fg, null, FallbackSparkleBottomLeft);
    }
}
