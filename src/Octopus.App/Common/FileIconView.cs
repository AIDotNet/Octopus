using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;

namespace Octopus.App.Common;

/// <summary>
/// Renders a VS Code style colored file or folder icon directly into <see cref="DrawingContext"/> with zero visual
/// child controls, so virtualized tree and list rows recycle without creating or tearing down visual trees.
/// </summary>
public sealed class FileIconView : Control
{
    public static readonly StyledProperty<string?> IconKeyProperty =
        AvaloniaProperty.Register<FileIconView, string?>(nameof(IconKey));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<FileIconView, double>(nameof(Size), 16);

    static FileIconView()
    {
        AffectsRender<FileIconView>(IconKeyProperty, SizeProperty);
        AffectsMeasure<FileIconView>(SizeProperty);
    }

    public FileIconView()
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
        var isLight = ActualThemeVariant == ThemeVariant.Light;
        var graphic = FileIconStore.Instance.Get(IconKey, isLight);
        if (graphic is null) return;

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

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
}
