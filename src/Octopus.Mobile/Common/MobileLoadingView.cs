using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using TKCode.Arc.Motion;

namespace Octopus.Mobile.Common;

/// <summary>Visual layout of <see cref="MobileLoadingView"/> tailored to the destination page.</summary>
public enum MobileLoadingVariant
{
    /// <summary>Centered orbital pulse with caption (compact or general purpose).</summary>
    Center,
    /// <summary>Orbital pulse header + staggered chat-bubble skeleton rows for GUI conversations.</summary>
    Conversation,
    /// <summary>Orbital pulse header + staggered icon/text skeleton rows for file, change, and folder lists.</summary>
    List,
    /// <summary>Orbital pulse header + staggered line-number &amp; code-bar skeleton rows for file/diff viewers.</summary>
    Code,
}

/// <summary>
/// Polished, theme-aware mobile loading view combining a dual-ring orbital pulse and wave-shimmer skeleton rows.
/// Drawn in a single custom control (zero extra visual-tree nodes), pauses automatically when hidden, detached, or
/// backgrounded, and degrades cleanly to a static frame when <see cref="ArcMotionLevel.Off"/> is active.
/// </summary>
public sealed class MobileLoadingView : Control
{
    public static readonly StyledProperty<MobileLoadingVariant> VariantProperty =
        AvaloniaProperty.Register<MobileLoadingView, MobileLoadingVariant>(nameof(Variant), MobileLoadingVariant.List);

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<MobileLoadingView, string?>(nameof(Label));

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(24);
    private static readonly double[] ListTitleWidths = [0.54, 0.40, 0.62, 0.46, 0.58, 0.36];
    private static readonly double[] ListSubWidths = [0.34, 0.28, 0.42, 0.30, 0.38, 0.24];
    private static readonly double[] CodeBarWidths = [0.38, 0.68, 0.52, 0.78, 0.30, 0.64, 0.48, 0.72, 0.42, 0.58];

    private DispatcherTimer? _timer;
    private double _phase;
    private DateTime _lastTickUtc;
    private bool _attached;

    static MobileLoadingView()
    {
        AffectsRender<MobileLoadingView>(VariantProperty, LabelProperty);
        IsVisibleProperty.Changed.AddClassHandler<MobileLoadingView>((view, _) => view.SyncAnimation());
    }

    public MobileLoadingVariant Variant
    {
        get => GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>True while the animation timer is actively ticking (for tests).</summary>
    internal bool IsTimerRunning => _timer is { IsEnabled: true };

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        MobileHost.Backgrounded += OnBackgrounded;
        MobileHost.Foregrounded += OnForegrounded;
        SyncAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        MobileHost.Backgrounded -= OnBackgrounded;
        MobileHost.Foregrounded -= OnForegrounded;
        StopTimer();
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width;
        var minHeight = Variant switch
        {
            MobileLoadingVariant.Center => 120,
            MobileLoadingVariant.Conversation => 320,
            MobileLoadingVariant.Code => 360,
            _ => 340,
        };
        var height = double.IsInfinity(availableSize.Height) ? minHeight : Math.Max(minHeight, Math.Min(availableSize.Height, 520));
        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 4 || bounds.Height <= 4) return;

        var accent = ResolveColor("Accent", Color.FromRgb(0x3B, 0x82, 0xF6));
        var surfaceMuted = ResolveColor("SurfaceMuted", Color.FromArgb(0x22, 0x88, 0x88, 0x88));
        var textMuted = ResolveColor("TextMuted", Color.FromRgb(0x88, 0x88, 0x94));
        var border = ResolveColor("BorderSubtle", Color.FromArgb(0x30, 0x88, 0x88, 0x88));

        var reduced = ArcMotion.GetLevel(this) == ArcMotionLevel.Off;
        var phase = reduced ? 0.25 : _phase;

        if (Variant == MobileLoadingVariant.Center)
        {
            var centerY = bounds.Height * 0.45;
            DrawOrbitalBadge(context, new Point(bounds.Width / 2, centerY), accent, phase, radius: 18);
            DrawCaption(context, bounds.Width, centerY + 34, textMuted);
            return;
        }

        // Top orbital badge + caption strip.
        var headerY = 34.0;
        DrawOrbitalBadge(context, new Point(bounds.Width / 2, headerY), accent, phase, radius: 14);
        var captionBottom = DrawCaption(context, bounds.Width, headerY + 26, textMuted);
        var bodyTop = captionBottom + 18;

        switch (Variant)
        {
            case MobileLoadingVariant.Conversation:
                DrawConversationSkeleton(context, bounds, bodyTop, surfaceMuted, accent, phase);
                break;
            case MobileLoadingVariant.Code:
                DrawCodeSkeleton(context, bounds, bodyTop, surfaceMuted, border, phase);
                break;
            default:
                DrawListSkeleton(context, bounds, bodyTop, surfaceMuted, border, phase);
                break;
        }
    }

    private static void DrawOrbitalBadge(DrawingContext context, Point center, Color accent, double phase, double radius)
    {
        // Soft breathing outer halo.
        var breathe = 0.5 + 0.5 * Math.Sin(phase * Math.Tau);
        var haloRadius = radius + 4 + breathe * 4;
        var haloAlpha = (byte)(22 + (int)(26 * (1.0 - breathe)));
        context.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(haloAlpha, accent.R, accent.G, accent.B)),
            null,
            center,
            haloRadius,
            haloRadius);

        // Subtle track ring.
        var trackPen = new Pen(new SolidColorBrush(Color.FromArgb(46, accent.R, accent.G, accent.B)), 2.2);
        context.DrawEllipse(null, trackPen, center, radius, radius);

        // Smooth rotating primary arc (110° sweep) + counter-orbiting accent dot.
        var startAngle = phase * Math.Tau;
        var sweepAngle = Math.PI * 0.62;
        var arcGeo = BuildArcGeometry(center, radius, startAngle, sweepAngle);
        var arcPen = new Pen(new SolidColorBrush(accent), 2.4, lineCap: PenLineCap.Round);
        context.DrawGeometry(null, arcPen, arcGeo);

        // Inner breathing core dot.
        var coreRadius = 2.6 + breathe * 1.1;
        var coreAlpha = (byte)(170 + (int)(85 * breathe));
        context.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(coreAlpha, accent.R, accent.G, accent.B)),
            null,
            center,
            coreRadius,
            coreRadius);
    }

    private double DrawCaption(DrawingContext context, double width, double y, Color textColor)
    {
        if (string.IsNullOrWhiteSpace(Label)) return y;
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Medium);
        var formatted = new FormattedText(
            Label,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            12.5,
            new SolidColorBrush(textColor))
        {
            MaxTextWidth = Math.Max(40, width - 32),
            TextAlignment = TextAlignment.Center,
        };
        var x = Math.Max(16, (width - formatted.Width) / 2);
        context.DrawText(formatted, new Point(x, y));
        return y + formatted.Height;
    }

    private static void DrawListSkeleton(DrawingContext context, Rect bounds, double top, Color baseColor, Color dividerColor, double phase)
    {
        const double padX = 16;
        const double rowHeight = 56;
        var usableWidth = Math.Max(80, bounds.Width - padX * 2);
        var dividerPen = new Pen(new SolidColorBrush(dividerColor), 1);

        var y = top;
        for (var i = 0; i < ListTitleWidths.Length && y + rowHeight <= bounds.Height + 12; i++)
        {
            var alpha = ShimmerAlpha(phase, i * 0.14);
            var barBrush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
            var subBrush = new SolidColorBrush(Color.FromArgb((byte)(alpha * 0.72), baseColor.R, baseColor.G, baseColor.B));

            // Leading icon square.
            context.DrawRectangle(barBrush, null, new RoundedRect(new Rect(padX, y + 13, 28, 28), 8));

            // Title & subtitle bars.
            var textX = padX + 40;
            var textSpace = Math.Max(40, usableWidth - 56);
            context.DrawRectangle(barBrush, null, new RoundedRect(new Rect(textX, y + 14, textSpace * ListTitleWidths[i], 11), 5.5));
            context.DrawRectangle(subBrush, null, new RoundedRect(new Rect(textX, y + 32, textSpace * ListSubWidths[i], 8.5), 4));

            // Row hairline divider.
            context.DrawLine(dividerPen, new Point(textX, y + rowHeight - 1), new Point(bounds.Width - padX, y + rowHeight - 1));
            y += rowHeight;
        }
    }

    private static void DrawCodeSkeleton(DrawingContext context, Rect bounds, double top, Color baseColor, Color dividerColor, double phase)
    {
        const double padX = 14;
        const double rowHeight = 24;
        var usableWidth = Math.Max(80, bounds.Width - padX * 2 - 40);
        var gutterPen = new Pen(new SolidColorBrush(dividerColor), 1);
        context.DrawLine(gutterPen, new Point(padX + 30, top), new Point(padX + 30, Math.Min(bounds.Height - 12, top + CodeBarWidths.Length * rowHeight)));

        var y = top + 4;
        for (var i = 0; i < CodeBarWidths.Length && y + rowHeight <= bounds.Height + 6; i++)
        {
            var alpha = ShimmerAlpha(phase, i * 0.10);
            var numBrush = new SolidColorBrush(Color.FromArgb((byte)(alpha * 0.65), baseColor.R, baseColor.G, baseColor.B));
            var lineBrush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));

            context.DrawRectangle(numBrush, null, new RoundedRect(new Rect(padX + 4, y + 6, 18, 8), 4));
            var indent = (i % 3 == 1) ? 14 : (i % 3 == 2) ? 28 : 0;
            var barW = Math.Max(28, (usableWidth - indent) * CodeBarWidths[i]);
            context.DrawRectangle(lineBrush, null, new RoundedRect(new Rect(padX + 40 + indent, y + 5, barW, 10), 5));
            y += rowHeight;
        }
    }

    private static void DrawConversationSkeleton(DrawingContext context, Rect bounds, double top, Color baseColor, Color accent, double phase)
    {
        const double padX = 16;
        var usableWidth = Math.Max(120, bounds.Width - padX * 2);

        // Right-aligned user bubble skeleton.
        var userW = Math.Min(220, usableWidth * 0.56);
        var userAlpha = ShimmerAlpha(phase, 0.0);
        var userBrush = new SolidColorBrush(Color.FromArgb((byte)(userAlpha * 0.35), accent.R, accent.G, accent.B));
        context.DrawRectangle(userBrush, null, new RoundedRect(new Rect(bounds.Width - padX - userW, top, userW, 38), new CornerRadius(16, 16, 4, 16)));

        // Left-aligned assistant work + text lines skeleton.
        var y = top + 56;
        double[] widths = [0.46, 0.86, 0.78, 0.62, 0.40];
        for (var i = 0; i < widths.Length && y + 18 <= bounds.Height; i++)
        {
            var alpha = ShimmerAlpha(phase, 0.18 + i * 0.12);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
            var barHeight = i == 0 ? 10 : 12;
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(padX, y, usableWidth * widths[i], barHeight), 5.5));
            y += i == 0 ? 22 : 20;
        }
    }

    private static byte ShimmerAlpha(double phase, double offset)
    {
        var wave = 0.5 + 0.5 * Math.Sin((phase - offset) * Math.Tau);
        return (byte)(95 + (int)(110 * wave));
    }

    private static StreamGeometry BuildArcGeometry(Point center, double radius, double startRadians, double sweepRadians)
    {
        var start = new Point(center.X + radius * Math.Cos(startRadians), center.Y + radius * Math.Sin(startRadians));
        var end = new Point(center.X + radius * Math.Cos(startRadians + sweepRadians), center.Y + radius * Math.Sin(startRadians + sweepRadians));
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(start, isFilled: false);
        ctx.ArcTo(end, new Size(radius, radius), 0, isLargeArc: sweepRadians > Math.PI, SweepDirection.Clockwise);
        ctx.EndFigure(isClosed: false);
        return geometry;
    }

    private Color ResolveColor(string tokenName, Color fallback)
    {
        if (this.TryFindResource(tokenName, ActualThemeVariant, out var value))
        {
            if (value is ISolidColorBrush brush) return brush.Color;
            if (value is Color color) return color;
        }
        return fallback;
    }

    private void SyncAnimation()
    {
        var shouldRun = _attached && IsVisible && !MobileHost.IsBackgrounded && ArcMotion.GetLevel(this) != ArcMotionLevel.Off;
        if (shouldRun) StartTimer();
        else StopTimer();
    }

    private void StartTimer()
    {
        if (_timer is { IsEnabled: true }) return;
        _lastTickUtc = DateTime.UtcNow;
        _timer ??= new DispatcherTimer(FrameInterval, DispatcherPriority.Render, OnTick);
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Clamp((now - _lastTickUtc).TotalSeconds, 0.0, 0.1);
        _lastTickUtc = now;
        // One full cycle every 1.15s: smooth, calm, and legible on mobile OLED screens.
        _phase = (_phase + dt / 1.15) % 1.0;
        InvalidateVisual();
    }

    private void OnBackgrounded() => Dispatcher.UIThread.Post(StopTimer);

    private void OnForegrounded() => Dispatcher.UIThread.Post(SyncAnimation);
}
