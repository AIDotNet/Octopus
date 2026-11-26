using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Octopus.App.Common;

/// <summary>
/// Renders a real product brand logo for a Bot connector (GitHub, Vercel, Cloudflare, Supabase, Slack, Google, etc.)
/// inside a crisp rounded tile directly into <see cref="DrawingContext"/>. Falls back to a colored letter badge for
/// custom URL connectors or unknown app ids.
/// </summary>
public sealed class ConnectorIconView : Control
{
    public static readonly StyledProperty<string?> AppIdProperty =
        AvaloniaProperty.Register<ConnectorIconView, string?>(nameof(AppId));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<ConnectorIconView, double>(nameof(Size), 32);

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<ConnectorIconView, double>(nameof(CornerRadius), 8);

    public static readonly StyledProperty<double> IconPaddingProperty =
        AvaloniaProperty.Register<ConnectorIconView, double>(nameof(IconPadding), 6);

    public static readonly StyledProperty<IBrush?> TileBackgroundProperty =
        AvaloniaProperty.Register<ConnectorIconView, IBrush?>(nameof(TileBackground));

    public static readonly StyledProperty<IBrush?> TileBorderBrushProperty =
        AvaloniaProperty.Register<ConnectorIconView, IBrush?>(nameof(TileBorderBrush));

    public static readonly StyledProperty<string?> FallbackTextProperty =
        AvaloniaProperty.Register<ConnectorIconView, string?>(nameof(FallbackText));

    public static readonly StyledProperty<IBrush?> FallbackBrushProperty =
        AvaloniaProperty.Register<ConnectorIconView, IBrush?>(nameof(FallbackBrush));

    private static readonly IBrush DefaultFallbackBrush = new ImmutableSolidColorBrush(Color.Parse("#5B5BD6"));
    private static readonly IBrush DefaultLightTile = new ImmutableSolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush DefaultDarkTile = new ImmutableSolidColorBrush(Color.Parse("#232328"));
    private static readonly IPen DefaultLightBorder = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#E4E4E7")), 1);
    private static readonly IPen DefaultDarkBorder = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#32323A")), 1);

    static ConnectorIconView()
    {
        AffectsRender<ConnectorIconView>(
            AppIdProperty,
            SizeProperty,
            CornerRadiusProperty,
            IconPaddingProperty,
            TileBackgroundProperty,
            TileBorderBrushProperty,
            FallbackTextProperty,
            FallbackBrushProperty);
        AffectsMeasure<ConnectorIconView>(SizeProperty);
    }

    public ConnectorIconView()
    {
        IsHitTestVisible = false;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public string? AppId
    {
        get => GetValue(AppIdProperty);
        set => SetValue(AppIdProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double IconPadding
    {
        get => GetValue(IconPaddingProperty);
        set => SetValue(IconPaddingProperty, value);
    }

    public IBrush? TileBackground
    {
        get => GetValue(TileBackgroundProperty);
        set => SetValue(TileBackgroundProperty, value);
    }

    public IBrush? TileBorderBrush
    {
        get => GetValue(TileBorderBrushProperty);
        set => SetValue(TileBorderBrushProperty, value);
    }

    public string? FallbackText
    {
        get => GetValue(FallbackTextProperty);
        set => SetValue(FallbackTextProperty, value);
    }

    public IBrush? FallbackBrush
    {
        get => GetValue(FallbackBrushProperty);
        set => SetValue(FallbackBrushProperty, value);
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

        var radius = Math.Clamp(CornerRadius, 0, Math.Min(width, height) / 2.0);
        var graphic = ConnectorIconStore.Instance.Get(AppId);

        if (graphic is null)
        {
            RenderFallbackBadge(context, width, height, radius);
            return;
        }

        var isDark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        var bg = TileBackground ?? (isDark ? DefaultDarkTile : DefaultLightTile);
        var borderPen = TileBorderBrush is { } customBorder
            ? new Pen(customBorder, 1)
            : (isDark ? DefaultDarkBorder : DefaultLightBorder);

        var tileRect = new Rect(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1));
        context.DrawRectangle(bg, borderPen, new RoundedRect(tileRect, radius));

        var pad = Math.Clamp(IconPadding, 0, Math.Min(width, height) * 0.4);
        var innerW = Math.Max(1, width - pad * 2);
        var innerH = Math.Max(1, height - pad * 2);

        var vb = graphic.ViewBox;
        var scale = Math.Min(innerW / vb.Width, innerH / vb.Height);
        var dx = pad + (innerW - vb.Width * scale) / 2.0 - vb.X * scale;
        var dy = pad + (innerH - vb.Height * scale) / 2.0 - vb.Y * scale;
        var baseMatrix = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy);

        using var _ = context.PushTransform(baseMatrix);
        IBrush? currentColor = null;

        foreach (var shape in graphic.Shapes)
        {
            var fill = shape.FillUsesCurrentColor
                ? (currentColor ??= TextElement.GetForeground(this) ?? (isDark ? Brushes.White : Brushes.Black))
                : shape.Fill;

            var stroke = shape.StrokeUsesCurrentColor
                ? new Pen(currentColor ??= TextElement.GetForeground(this) ?? (isDark ? Brushes.White : Brushes.Black), shape.StrokeWidth)
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

    private void RenderFallbackBadge(DrawingContext context, double width, double height, double radius)
    {
        var bg = FallbackBrush ?? DefaultFallbackBrush;
        context.DrawRectangle(bg, null, new RoundedRect(new Rect(0, 0, width, height), radius));

        var label = string.IsNullOrWhiteSpace(FallbackText) ? "URL" : FallbackText.Trim();
        var fontSize = Math.Max(9, Math.Round(Math.Min(width, height) * (label.Length > 2 ? 0.30 : 0.36)));
        var formatted = new FormattedText(
            label,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, FontWeight.Bold),
            fontSize,
            Brushes.White);

        var x = (width - formatted.Width) / 2.0;
        var y = (height - formatted.Height) / 2.0;
        context.DrawText(formatted, new Point(x, y));
    }
}
