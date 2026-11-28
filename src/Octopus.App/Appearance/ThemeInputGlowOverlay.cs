using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Octopus.App.Plugins;
using Octopus.Core.Plugins;
using Octopus.Plugins.Themes;

namespace Octopus.App.Appearance;

/// <summary>
/// Renders optional declarative sci-fi theme effects around a composer input card: an animated/static border glow
/// (<see cref="InputGlowEffect"/>) and optional HUD corner brackets (<see cref="ThemeEffects.HudCorners"/>).
/// Does zero work and allocates no timer when the active theme defines no effects.
/// </summary>
public sealed class ThemeInputGlowOverlay : Control
{
    public static readonly StyledProperty<bool> IsAgentWorkingProperty =
        AvaloniaProperty.Register<ThemeInputGlowOverlay, bool>(nameof(IsAgentWorking));

    public static readonly StyledProperty<bool> IsTargetFocusedProperty =
        AvaloniaProperty.Register<ThemeInputGlowOverlay, bool>(nameof(IsTargetFocused));

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<ThemeInputGlowOverlay, double>(nameof(CornerRadius), defaultValue: 10.0);

    private DispatcherTimer? _timer;
    private double _angleDeg;
    private double _pulsePhase;
    private DateTime _lastTickUtc = DateTime.UtcNow;
    private bool _attached;

    static ThemeInputGlowOverlay()
    {
        AffectsRender<ThemeInputGlowOverlay>(IsAgentWorkingProperty, IsTargetFocusedProperty, CornerRadiusProperty);
    }

    public ThemeInputGlowOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;
    }

    public bool IsAgentWorking
    {
        get => GetValue(IsAgentWorkingProperty);
        set => SetValue(IsAgentWorkingProperty, value);
    }

    public bool IsTargetFocused
    {
        get => GetValue(IsTargetFocusedProperty);
        set => SetValue(IsTargetFocusedProperty, value);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        PluginThemes.ActiveEffectsChanged += OnEffectsChanged;
        UpdateTimerState();
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        PluginThemes.ActiveEffectsChanged -= OnEffectsChanged;
        StopTimer();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property == IsTargetFocusedProperty || change.Property == IsAgentWorkingProperty)
        {
            UpdateTimerState();
        }
    }

    private void OnEffectsChanged(object? sender, EventArgs e)
    {
        UpdateTimerState();
        InvalidateVisual();
    }

    private bool NeedsAnimation(ThemeEffects? effects)
    {
        if (!_attached || !IsVisible) return false;
        var glow = effects?.InputGlow;
        if (glow is null || glow.Intensity <= 0.001) return false;
        return glow.Style switch
        {
            InputGlowStyle.Orbit => true,
            InputGlowStyle.Pulse => true,
            InputGlowStyle.Focus => IsTargetFocused || (IsAgentWorking && glow.AccelerateOnAgentWorking),
            _ => false,
        };
    }

    private void UpdateTimerState()
    {
        if (NeedsAnimation(PluginThemes.CurrentEffects))
        {
            if (_timer is null)
            {
                _lastTickUtc = DateTime.UtcNow;
                _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, OnTick);
                _timer.Start();
            }
        }
        else
        {
            StopTimer();
        }
    }

    private void StopTimer()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var fx = PluginThemes.CurrentEffects;
        if (!NeedsAnimation(fx))
        {
            StopTimer();
            InvalidateVisual();
            return;
        }
        var now = DateTime.UtcNow;
        var dt = Math.Clamp((now - _lastTickUtc).TotalSeconds, 0.005, 0.15);
        _lastTickUtc = now;

        var glow = fx!.InputGlow!;
        var fast = IsAgentWorking && glow.AccelerateOnAgentWorking;
        var degPerSec = fast ? 220.0 : 95.0;
        _angleDeg = (_angleDeg + degPerSec * dt) % 360.0;
        _pulsePhase = (_pulsePhase + (fast ? 4.2 : 2.1) * dt) % (Math.PI * 2.0);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var fx = PluginThemes.CurrentEffects;
        if (fx is null) return;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 8 || h < 8) return;

        var rect = new Rect(0, 0, w, h);
        var radius = Math.Max(0, CornerRadius);
        var palette = ResolveColors(fx.InputGlow?.Colors);
        var primary = palette[0];

        if (fx.InputGlow is { Intensity: > 0.001 } glow)
        {
            RenderGlowBorder(context, rect, radius, glow, palette);
        }

        if (fx.HudCorners)
        {
            RenderHudCorners(context, rect, primary);
        }
    }

    private void RenderGlowBorder(DrawingContext context, Rect rect, double radius, InputGlowEffect glow, Color[] palette)
    {
        var focusActive = IsTargetFocused;
        var workingBoost = IsAgentWorking && glow.AccelerateOnAgentWorking;
        var baseIntensity = Math.Clamp(glow.Intensity, 0.0, 1.2);

        var factor = glow.Style switch
        {
            InputGlowStyle.Focus => (focusActive || workingBoost) ? 1.0 : 0.18,
            InputGlowStyle.Pulse => 0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(_pulsePhase)),
            _ => 1.0,
        };
        if (focusActive) factor *= 1.35;
        if (workingBoost) factor *= 1.28;

        var alphaScale = Math.Clamp(baseIntensity * factor, 0.0, 1.0);
        if (alphaScale <= 0.01) return;

        var brush = BuildBorderBrush(glow.Style, palette, alphaScale);

        // Multi-pass soft outer halo around the rounded border (GPU-fast, no offscreen blur target).
        var halo1 = new Pen(BuildBorderBrush(glow.Style, palette, alphaScale * 0.16), 9.0);
        var halo2 = new Pen(BuildBorderBrush(glow.Style, palette, alphaScale * 0.28), 5.0);
        var halo3 = new Pen(BuildBorderBrush(glow.Style, palette, alphaScale * 0.48), 2.5);
        var rim = new Pen(brush, focusActive || workingBoost ? 1.65 : 1.35);

        context.DrawRectangle(null, halo1, new RoundedRect(rect.Inflate(1.5), radius + 1.5));
        context.DrawRectangle(null, halo2, new RoundedRect(rect.Inflate(0.8), radius + 0.8));
        context.DrawRectangle(null, halo3, new RoundedRect(rect.Inflate(0.25), radius + 0.25));
        context.DrawRectangle(null, rim, new RoundedRect(rect, radius));
    }

    private IBrush BuildBorderBrush(InputGlowStyle style, Color[] palette, double opacity)
    {
        var c0 = palette[0];
        var c1 = palette.Length > 1 ? palette[1] : palette[0];
        var c2 = palette.Length > 2 ? palette[2] : c1;
        var aFull = (byte)Math.Clamp((int)Math.Round(255 * opacity), 0, 255);
        var aMid = (byte)Math.Clamp((int)Math.Round(180 * opacity), 0, 255);
        var aDim = (byte)Math.Clamp((int)Math.Round(42 * opacity), 0, 255);
        var aPeak = (byte)Math.Clamp((int)Math.Round(255 * Math.Min(1.0, opacity * 1.15)), 0, 255);

        if (style is InputGlowStyle.Static or InputGlowStyle.Pulse)
        {
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                [
                    new GradientStop(Color.FromArgb(aFull, c0.R, c0.G, c0.B), 0.0),
                    new GradientStop(Color.FromArgb(aFull, c1.R, c1.G, c1.B), 0.52),
                    new GradientStop(Color.FromArgb(aMid, c2.R, c2.G, c2.B), 1.0),
                ],
            };
        }

        // Orbit / Focus animated sweep around the perimeter.
        var peak = Color.FromArgb(aPeak,
            (byte)Math.Min(255, c0.R + 70),
            (byte)Math.Min(255, c0.G + 70),
            (byte)Math.Min(255, c0.B + 70));

        return new ConicGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            Angle = _angleDeg,
            GradientStops =
            [
                new GradientStop(Color.FromArgb(aDim, c0.R, c0.G, c0.B), 0.00),
                new GradientStop(Color.FromArgb(aFull, c0.R, c0.G, c0.B), 0.16),
                new GradientStop(peak, 0.25),
                new GradientStop(Color.FromArgb(aFull, c1.R, c1.G, c1.B), 0.36),
                new GradientStop(Color.FromArgb(aDim, c1.R, c1.G, c1.B), 0.56),
                new GradientStop(Color.FromArgb(aMid, c2.R, c2.G, c2.B), 0.78),
                new GradientStop(Color.FromArgb(aFull, c0.R, c0.G, c0.B), 0.92),
                new GradientStop(Color.FromArgb(aDim, c0.R, c0.G, c0.B), 1.00),
            ],
        };
    }

    private void RenderHudCorners(DrawingContext context, Rect rect, Color primary)
    {
        var alpha = (byte)(IsTargetFocused || IsAgentWorking ? 205 : 120);
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, primary.R, primary.G, primary.B)), 1.5, lineCap: PenLineCap.Round);
        const double pad = 4.0;
        const double len = 10.0;

        var left = rect.Left - pad;
        var top = rect.Top - pad;
        var right = rect.Right + pad;
        var bottom = rect.Bottom + pad;

        // Top-left
        context.DrawLine(pen, new Point(left, top + len), new Point(left, top));
        context.DrawLine(pen, new Point(left, top), new Point(left + len, top));
        // Top-right
        context.DrawLine(pen, new Point(right - len, top), new Point(right, top));
        context.DrawLine(pen, new Point(right, top), new Point(right, top + len));
        // Bottom-left
        context.DrawLine(pen, new Point(left, bottom - len), new Point(left, bottom));
        context.DrawLine(pen, new Point(left, bottom), new Point(left + len, bottom));
        // Bottom-right
        context.DrawLine(pen, new Point(right - len, bottom), new Point(right, bottom));
        context.DrawLine(pen, new Point(right, bottom), new Point(right, bottom - len));
    }

    private static Color[] ResolveColors(IReadOnlyList<string>? hexes)
    {
        var list = new List<Color>();
        if (hexes is not null)
        {
            foreach (var h in hexes)
            {
                if (ThemeColor.TryParse(h, out var tc))
                {
                    list.Add(Color.FromRgb(tc.R, tc.G, tc.B));
                }
            }
        }
        if (list.Count == 0 && ThemeColor.TryParse(PluginThemes.CurrentAccentHex, out var acc))
        {
            list.Add(Color.FromRgb(acc.R, acc.G, acc.B));
        }
        if (list.Count == 0)
        {
            list.Add(Color.FromRgb(0x22, 0xD3, 0xEE));
            list.Add(Color.FromRgb(0x38, 0xBD, 0xF8));
            list.Add(Color.FromRgb(0x81, 0x8C, 0xF8));
        }
        return [.. list];
    }
}
