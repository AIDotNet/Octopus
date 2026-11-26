using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using TKCode.Arc.Motion;

namespace Octopus.Ticker;

/// <summary>
/// Model contract consumed by <see cref="WorkTickerViewport"/> on both desktop (<c>Octopus.App</c>)
/// and mobile (<c>Octopus.Mobile</c>) so the 5-line sliding live ticker has identical physics,
/// opacity curves, and clipping everywhere.
/// </summary>
internal interface IWorkTickerSource
{
    int VisibleCount { get; }
    object GetLine(int index);
    object GetExit(int index);

    /// <summary>Clears exiting ghost snapshots once the upward slide has settled.</summary>
    void ClearExiting();

    /// <summary>Raised after a sync with the upward row shift (0..5) and current visible row count (0..5).</summary>
    event Action<int, int>? Updated;
}

/// <summary>
/// Shared upward-shift detector used by both desktop and mobile ticker view models so batch updates,
/// in-place updates, growth (1 -> 2 -> 3 -> 4 -> 5), and out-of-order completions behave identically.
/// </summary>
internal static class WorkTickerShift
{
    public static int Compute(ReadOnlySpan<object?> prev, int prevCount, ReadOnlySpan<object?> next, int nextCount)
    {
        if (prevCount <= 0 || nextCount <= 0) return 0;

        // 1. Exact suffix-of-prev == prefix-of-next match (handles 0..prevCount-1 row upward shifts cleanly).
        for (var shift = 0; shift < prevCount; shift++)
        {
            var overlap = Math.Min(prevCount - shift, nextCount);
            var match = true;
            for (var i = 0; i < overlap; i++)
            {
                if (!ReferenceEquals(prev[shift + i], next[i]))
                {
                    match = false;
                    break;
                }
            }
            if (match) return shift;
        }

        // 2. Fallback when steps reorder while older top steps drop off (or every slot is replaced at once):
        // count leading items from prev that are no longer present in next.
        var dropped = 0;
        for (var i = 0; i < prevCount; i++)
        {
            if (Contains(prev[i], next, nextCount)) break;
            dropped++;
        }
        return dropped;
    }

    private static bool Contains(object? key, ReadOnlySpan<object?> items, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (ReferenceEquals(key, items[i])) return true;
        }
        return false;
    }
}

/// <summary>
/// Clipped, spring-animated 5-line live ticker viewport shared between desktop and mobile.
/// Uses a monotonic <see cref="MotionValue{T}"/> scroll coordinate so consecutive upward shifts
/// preserve momentum without jumping, keeps height constant during full-window shifts,
/// fades exiting rows out at the top edge and entering rows in from below, refreshes child
/// effective viewports when newly entered rows slide into view so live spinners keep turning,
/// and respects <see cref="ArcMotion"/>.
/// </summary>
public sealed class WorkTickerViewport : Panel
{
    /// <summary>Bounce-free settle spring (0.33s, bounce 0) matching Arc's row reflow motion.</summary>
    internal const ArcSpring SlideSpring = ArcSpring.Settle;

    /// <summary>Maximum number of visible rows in the sliding live ticker.</summary>
    public const int MaxVisibleRows = 5;

    internal const int MaxExitingRows = MaxVisibleRows;

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<WorkTickerViewport, double>(nameof(Spacing), 1.0);

    private const double Epsilon = 0.001;
    private const double GrowthSlideFraction = 0.35;
    private const double ViewportRefreshLead = 0.15;

    // Continuous resting opacities indexed by distance above the bottom-most visible row (0 = newest/bottom).
    // 1 row: [1.00]
    // 2 rows: [0.76, 1.00]
    // 3 rows: [0.56, 0.76, 1.00]
    // 4 rows: [0.40, 0.56, 0.76, 1.00]
    // 5 rows: [0.28, 0.40, 0.56, 0.76, 1.00]
    private static readonly double[] DistanceOpacities = [1.00, 0.76, 0.56, 0.40, 0.28];

    private readonly ContentControl[] _exitSlots = new ContentControl[MaxExitingRows];
    private readonly TranslateTransform[] _exitTransforms = new TranslateTransform[MaxExitingRows];
    private readonly ContentControl[] _lineSlots = new ContentControl[MaxVisibleRows];
    private readonly TranslateTransform[] _lineTransforms = new TranslateTransform[MaxVisibleRows];

    private IWorkTickerSource? _source;
    private bool _subscribed;
    private MotionValue<double>? _scroll;
    private MotionValue<double>? _visibleRows;
    private double _targetScroll;
    private double _scrollValue;
    private double _targetVisibleRows;
    private double _visibleRowsValue;
    private int _exitActiveCount;
    private int _scrollGeneration;
    private int _heightGeneration;
    private double _rowHeight;
    private bool _viewportRefreshPending;
    private double _nextViewportRefreshAt;

    static WorkTickerViewport()
    {
        ClipToBoundsProperty.OverrideDefaultValue<WorkTickerViewport>(true);
        AffectsMeasure<WorkTickerViewport>(SpacingProperty);
    }

    public WorkTickerViewport()
    {
        for (var i = 0; i < MaxExitingRows; i++)
        {
            var transform = new TranslateTransform();
            var slot = new ContentControl
            {
                IsVisible = false,
                ClipToBounds = false,
                VerticalAlignment = VerticalAlignment.Top,
                RenderTransform = transform,
            };
            _exitTransforms[i] = transform;
            _exitSlots[i] = slot;
            Children.Add(slot);
        }

        for (var i = 0; i < MaxVisibleRows; i++)
        {
            var transform = new TranslateTransform();
            var slot = new ContentControl
            {
                IsVisible = false,
                ClipToBounds = false,
                VerticalAlignment = VerticalAlignment.Top,
                RenderTransform = transform,
            };
            _lineTransforms[i] = transform;
            _lineSlots[i] = slot;
            Children.Add(slot);
        }
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>True while either the upward slide or height growth spring is still in flight.</summary>
    internal bool IsRunning => (_scroll?.IsAnimating == true) || (_visibleRows?.IsAnimating == true);

    /// <summary>Remaining upward travel in row units (0 when settled).</summary>
    internal double RemainingShift => Math.Max(0.0, _targetScroll - _scrollValue);

    /// <summary>Current animated visible row count (0..5).</summary>
    internal double AnimatedVisibleRows => _visibleRowsValue;

    /// <summary>Measured single-row height in DIPs.</summary>
    internal double MeasuredRowHeight => _rowHeight;

    internal double GetSlotOffsetY(int index) => _lineSlots[index].Bounds.Y + _lineTransforms[index].Y;
    internal double GetSlotOpacity(int index) => _lineSlots[index].Opacity;
    internal double GetExitOffsetY(int index) => _exitSlots[index].Bounds.Y + _exitTransforms[index].Y;
    internal double GetExitOpacity(int index) => _exitSlots[index].Opacity;
    internal bool IsExitVisible(int index) => _exitSlots[index].IsVisible;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        BindSource(DataContext as IWorkTickerSource);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BindSource(DataContext as IWorkTickerSource);

        var count = Math.Clamp(_source?.VisibleCount ?? 0, 0, MaxVisibleRows);
        _targetScroll = 0;
        _scrollValue = 0;
        _targetVisibleRows = count;
        _visibleRowsValue = count;
        _exitActiveCount = 0;
        _viewportRefreshPending = false;
        _source?.ClearExiting();

        _scroll?.Dispose();
        _scroll = new MotionValue<double>(this, 0.0, MotionVectors.Double);
        _scroll.Changed += OnScrollValueChanged;

        _visibleRows?.Dispose();
        _visibleRows = new MotionValue<double>(this, count, MotionVectors.Double);
        _visibleRows.Changed += OnVisibleRowsValueChanged;

        SubscribeSource(true);
        ApplyVisuals();
        InvalidateMeasure();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _scrollGeneration++;
        _heightGeneration++;
        SubscribeSource(false);

        _scroll?.Dispose();
        _scroll = null;

        _visibleRows?.Dispose();
        _visibleRows = null;

        _exitActiveCount = 0;
        _viewportRefreshPending = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void BindSource(IWorkTickerSource? next)
    {
        if (ReferenceEquals(_source, next)) return;

        SubscribeSource(false);
        _source = next;

        if (_source is not null)
        {
            for (var i = 0; i < MaxExitingRows; i++) _exitSlots[i].Content = _source.GetExit(i);
            for (var i = 0; i < MaxVisibleRows; i++) _lineSlots[i].Content = _source.GetLine(i);
        }
        else
        {
            for (var i = 0; i < MaxExitingRows; i++) _exitSlots[i].Content = null;
            for (var i = 0; i < MaxVisibleRows; i++) _lineSlots[i].Content = null;
        }

        SnapTo(Math.Clamp(_source?.VisibleCount ?? 0, 0, MaxVisibleRows));
        if (_scroll is not null)
        {
            SubscribeSource(true);
        }
    }

    private void SubscribeSource(bool subscribe)
    {
        if (_source is null)
        {
            _subscribed = false;
            return;
        }
        if (subscribe && !_subscribed)
        {
            _source.Updated += OnSourceUpdated;
            _subscribed = true;
        }
        else if (!subscribe && _subscribed)
        {
            _source.Updated -= OnSourceUpdated;
            _subscribed = false;
        }
    }

    private void SnapTo(int visibleCount)
    {
        _scrollGeneration++;
        _heightGeneration++;
        _targetScroll = 0;
        _scrollValue = 0;
        _targetVisibleRows = visibleCount;
        _visibleRowsValue = visibleCount;
        _exitActiveCount = 0;
        _viewportRefreshPending = false;
        _source?.ClearExiting();

        _scroll?.Jump(0);
        _visibleRows?.Jump(visibleCount);

        ApplyVisuals();
        InvalidateMeasure();
    }

    private void OnSourceUpdated(int shift, int visibleCount)
    {
        if (_source is null) return;
        var clampedCount = Math.Clamp(visibleCount, 0, MaxVisibleRows);

        if (_scroll is null || _visibleRows is null || !IsLoaded || !IsEffectivelyVisible)
        {
            SnapTo(clampedCount);
            return;
        }

        if (shift > 0)
        {
            _targetScroll += shift;
            _exitActiveCount = Math.Min(MaxExitingRows, _exitActiveCount + shift);

            // Prevent lag from exceeding the exiting ghost slots during extreme bursts.
            var minScroll = _targetScroll - MaxExitingRows;
            if (_scroll.Value < minScroll)
            {
                _scroll.Jump(minScroll);
            }

            // A newly entering bottom row starts clipped below our bounds via RenderTransform.
            // Schedule a single arrange pass per entering row once it crosses into the viewport
            // so Avalonia recalculates EffectiveViewport and ArcSpinner's LiveLoop starts spinning.
            var remaining = Math.Max(0.0, _targetScroll - _scrollValue);
            _viewportRefreshPending = true;
            _nextViewportRefreshAt = Math.Max(0.0, Math.Ceiling(remaining) - ViewportRefreshLead);

            StartScrollAnimation();
        }

        if (Math.Abs(clampedCount - _targetVisibleRows) > Epsilon)
        {
            _targetVisibleRows = clampedCount;
            StartHeightAnimation();
        }

        ApplyVisuals();
    }

    private async void StartScrollAnimation()
    {
        if (_scroll is not { } scroll) return;
        var generation = ++_scrollGeneration;
        var arrived = await scroll.AnimateTo(_targetScroll, MotionSpec.Spring(SlideSpring));
        if (arrived && generation == _scrollGeneration)
        {
            _scrollValue = _targetScroll;
            _exitActiveCount = 0;
            _viewportRefreshPending = false;
            _source?.ClearExiting();
            ApplyVisuals();
            // Ensure EffectiveViewport is recalculated at resting transforms (remaining == 0)
            // so any LiveLoop-driven child (e.g. ArcSpinner, TickerTargetText shimmer) stays active.
            InvalidateArrange();
        }
    }

    private async void StartHeightAnimation()
    {
        if (_visibleRows is not { } visibleRows) return;
        var generation = ++_heightGeneration;
        var arrived = await visibleRows.AnimateTo(_targetVisibleRows, MotionSpec.Spring(SlideSpring));
        if (arrived && generation == _heightGeneration)
        {
            _visibleRowsValue = _targetVisibleRows;
            ApplyVisuals();
            InvalidateMeasure();
        }
    }

    private void OnScrollValueChanged(double value)
    {
        _scrollValue = value;
        ApplyVisuals();

        if (_viewportRefreshPending)
        {
            var remaining = Math.Max(0.0, _targetScroll - _scrollValue);
            if (remaining <= _nextViewportRefreshAt)
            {
                _nextViewportRefreshAt = Math.Floor(remaining) - ViewportRefreshLead;
                if (_nextViewportRefreshAt < 0.0)
                {
                    _viewportRefreshPending = false;
                }
                InvalidateArrange();
            }
        }
    }

    private void OnVisibleRowsValueChanged(double value)
    {
        _visibleRowsValue = value;
        InvalidateMeasure();
        ApplyVisuals();
    }

    private void ApplyVisuals()
    {
        var remaining = Math.Max(0.0, _targetScroll - _scrollValue);
        var visibleRows = Math.Clamp(_visibleRowsValue, 0.0, MaxVisibleRows);
        var activeCount = Math.Clamp(_source?.VisibleCount ?? 0, 0, MaxVisibleRows);
        var stepY = _rowHeight > 0 ? _rowHeight + Spacing : 0.0;
        var growthBlend = Math.Clamp(1.0 - remaining, 0.0, 1.0);

        for (var i = 0; i < MaxExitingRows; i++)
        {
            var slot = _exitSlots[i];
            var visualRow = -(i + 1) + remaining;
            var show = i < _exitActiveCount && remaining > Epsilon && visualRow > -1.0 + Epsilon;
            if (slot.IsVisible != show) slot.IsVisible = show;
            if (!show)
            {
                slot.Opacity = 0.0;
                continue;
            }

            // Slot is arranged at resting Y = -(i + 1) * stepY; transform carries only the transient slide delta.
            _exitTransforms[i].Y = remaining * stepY;
            slot.Opacity = ComputeRowOpacity(visualRow, visibleRows);
        }

        for (var i = 0; i < MaxVisibleRows; i++)
        {
            var slot = _lineSlots[i];
            var show = i < activeCount;
            if (slot.IsVisible != show) slot.IsVisible = show;
            if (!show)
            {
                slot.Opacity = 0.0;
                continue;
            }

            var visualRow = i + remaining;
            var revealOffset = i > 0
                ? (1.0 - Math.Clamp(visibleRows - i, 0.0, 1.0)) * GrowthSlideFraction * growthBlend
                : 0.0;

            // Slot is arranged at resting Y = i * stepY so at rest RenderTransform.Y == 0
            // and Avalonia's EffectiveViewport matches the child's exact resting bounds.
            _lineTransforms[i].Y = (remaining + revealOffset) * stepY;
            slot.Opacity = ComputeRowOpacity(visualRow, visibleRows);
        }
    }

    /// <summary>
    /// Continuous opacity profile across the viewport that matches the resting weights
    /// (1 row: [1.00]; 2 rows: [0.76, 1.00]; 3 rows: [0.56, 0.76, 1.00];
    /// 4 rows: [0.40, 0.56, 0.76, 1.00]; 5 rows: [0.28, 0.40, 0.56, 0.76, 1.00])
    /// while smoothly fading exiting rows out at the top edge and entering rows in from below.
    /// </summary>
    internal static double ComputeRowOpacity(double visualRow, double visibleRows)
    {
        var v = Math.Clamp(visibleRows, 0.0, MaxVisibleRows);
        if (v <= Epsilon) return 0.0;

        if (visualRow < 0.0)
        {
            if (visualRow <= -1.0) return 0.0;
            return OpacityAtDistance(Math.Max(0.0, v - 1.0)) * (1.0 + visualRow);
        }

        var bottomSlot = Math.Max(0.0, v - 1.0);
        if (visualRow > bottomSlot)
        {
            var below = visualRow - bottomSlot;
            return below >= 1.0 ? 0.0 : 1.0 - below;
        }

        return OpacityAtDistance(bottomSlot - visualRow);
    }

    private static double OpacityAtDistance(double distFromBottom)
    {
        var maxIdx = DistanceOpacities.Length - 1;
        var clamped = Math.Clamp(distFromBottom, 0.0, maxIdx);
        var lo = (int)Math.Floor(clamped);
        if (lo >= maxIdx) return DistanceOpacities[maxIdx];
        var frac = clamped - lo;
        return Lerp(DistanceOpacities[lo], DistanceOpacities[lo + 1], frac);
    }

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;

    protected override Size MeasureOverride(Size availableSize)
    {
        var rowConstraint = new Size(availableSize.Width, double.PositiveInfinity);
        var maxRowHeight = 0.0;
        var maxWidth = 0.0;

        for (var i = 0; i < MaxVisibleRows; i++)
        {
            var slot = _lineSlots[i];
            if (!slot.IsVisible) continue;
            slot.Measure(rowConstraint);
            if (slot.DesiredSize.Height > maxRowHeight) maxRowHeight = slot.DesiredSize.Height;
            if (slot.DesiredSize.Width > maxWidth) maxWidth = slot.DesiredSize.Width;
        }

        for (var i = 0; i < MaxExitingRows; i++)
        {
            var slot = _exitSlots[i];
            if (!slot.IsVisible) continue;
            slot.Measure(rowConstraint);
            if (slot.DesiredSize.Height > maxRowHeight) maxRowHeight = slot.DesiredSize.Height;
            if (slot.DesiredSize.Width > maxWidth) maxWidth = slot.DesiredSize.Width;
        }

        if (maxRowHeight > 0)
        {
            _rowHeight = maxRowHeight;
        }

        var v = Math.Clamp(_visibleRowsValue, 0.0, MaxVisibleRows);
        var height = v > Epsilon && _rowHeight > 0
            ? v * _rowHeight + Math.Max(0.0, v - 1.0) * Spacing
            : 0.0;

        var width = double.IsInfinity(availableSize.Width) ? maxWidth : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rowHeight = _rowHeight > 0 ? _rowHeight : finalSize.Height;
        var stepY = rowHeight > 0 ? rowHeight + Spacing : 0.0;

        for (var i = 0; i < MaxExitingRows; i++)
        {
            var slot = _exitSlots[i];
            if (slot.IsVisible) slot.Arrange(new Rect(0, -(i + 1) * stepY, finalSize.Width, rowHeight));
        }

        for (var i = 0; i < MaxVisibleRows; i++)
        {
            var slot = _lineSlots[i];
            if (slot.IsVisible) slot.Arrange(new Rect(0, i * stepY, finalSize.Width, rowHeight));
        }

        ApplyVisuals();
        return finalSize;
    }
}

/// <summary>
/// Single-line ticker target text control shared between desktop and mobile that optimizes live
/// <c>think</c> preview updates with two distinct, flicker-free animations plus a subtle active shimmer:
/// <list type="bullet">
/// <item><description><b>Streaming continuation</b> (text extends the previous preview): keeps the existing prefix steady while softly revealing the newly appended tail from left to right.</description></item>
/// <item><description><b>Line / topic switch</b> (reasoning moves to a new line): smoothly rolls the previous line upward while fading and rising the new line into place.</description></item>
/// <item><description><b>Active shimmer</b>: sweeps a gentle highlight band across actively streaming thinking text.</description></item>
/// </list>
/// Non-animated rows (completed steps, tool calls) and row shifts where <see cref="StepKey"/> changes
/// snap immediately with zero animation overhead and always respect <see cref="ArcMotion"/>.
/// </summary>
public sealed class TickerTargetText : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TickerTargetText, string?>(nameof(Text));

    public static readonly StyledProperty<bool> AnimateUpdatesProperty =
        AvaloniaProperty.Register<TickerTargetText, bool>(nameof(AnimateUpdates));

    public static readonly StyledProperty<object?> StepKeyProperty =
        AvaloniaProperty.Register<TickerTargetText, object?>(nameof(StepKey));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<TickerTargetText>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<TickerTargetText>();

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        TextElement.FontWeightProperty.AddOwner<TickerTargetText>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<TickerTargetText>();

    private const double RollDistancePx = 5.5;
    private const double RevealFeatherPx = 28.0;
    private const double ShimmerSweepSeconds = 2.0;
    private const double ShimmerPauseSeconds = 0.7;
    private const double ShimmerBandEm = 2.4;

    private readonly LiveLoop _shimmerLoop;

    private string _currentText = "";
    private string? _exitingText;
    private object? _displayedStepKey;

    private TextLayout? _currentLayout;
    private TextLayout? _currentHighlightLayout;
    private TextLayout? _exitingLayout;
    private double _layoutWidth = double.NaN;
    private bool _layoutDirty = true;

    private MotionValue<double>? _swapProgress;
    private int _swapGeneration;
    private bool _swapVertical;

    private MotionValue<double>? _revealProgress;
    private int _revealGeneration;
    private double _revealFromX;
    private double _revealToX;

    private double _shimmerSweep;
    private double _shimmerPause = 0.2;
    private bool _shimmerStarted;
    private IBrush? _highlightBrush;
    private IBrush? _highlightSourceBrush;

    static TickerTargetText()
    {
        ClipToBoundsProperty.OverrideDefaultValue<TickerTargetText>(true);
        IsHitTestVisibleProperty.OverrideDefaultValue<TickerTargetText>(false);
        VerticalAlignmentProperty.OverrideDefaultValue<TickerTargetText>(VerticalAlignment.Center);
        AffectsMeasure<TickerTargetText>(FontFamilyProperty, FontSizeProperty, FontWeightProperty);
        AffectsRender<TickerTargetText>(ForegroundProperty);
        AutomationProperties.ControlTypeOverrideProperty.OverrideDefaultValue<TickerTargetText>(
            Avalonia.Automation.Peers.AutomationControlType.Text);
    }

    public TickerTargetText()
    {
        _shimmerLoop = new LiveLoop(this, StepShimmer)
        {
            MinimumLevel = ArcMotionLevel.Full,
            ViewportMargin = 64.0,
        };
        _shimmerLoop.RunningChanged += OnShimmerRunningChanged;
        ResourcesChanged += (_, _) => UpdateShimmerLoop();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool AnimateUpdates
    {
        get => GetValue(AnimateUpdatesProperty);
        set => SetValue(AnimateUpdatesProperty, value);
    }

    public object? StepKey
    {
        get => GetValue(StepKeyProperty);
        set => SetValue(StepKeyProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>True while a vertical line-switch roll animation is in flight.</summary>
    internal bool IsSwapAnimating => _swapProgress?.IsAnimating == true;

    /// <summary>True while a streaming tail-reveal animation is in flight.</summary>
    internal bool IsRevealAnimating => _revealProgress?.IsAnimating == true;

    /// <summary>True while any text update transition (roll or reveal) is running.</summary>
    internal bool IsTransitioning => IsSwapAnimating || IsRevealAnimating;

    /// <summary>True while the subtle active-thinking shimmer sweep is running.</summary>
    internal bool IsShimmering => _shimmerLoop.IsRunning;

    internal string CurrentDisplayText => _currentText;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _displayedStepKey = StepKey;
        _currentText = Text ?? "";
        _layoutDirty = true;
        UpdateShimmerLoop();
        InvalidateMeasure();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelTransitions();
        _shimmerLoop.Stop();
        DisposeLayouts();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == StepKeyProperty)
        {
            if (!ReferenceEquals(_displayedStepKey, StepKey))
            {
                CancelTransitions();
                InvalidateVisual();
            }
        }
        else if (change.Property == TextProperty)
        {
            var next = Text ?? "";
            AutomationProperties.SetName(this, next);
            HandleTextChanged(next);
        }
        else if (change.Property == AnimateUpdatesProperty)
        {
            if (!AnimateUpdates)
            {
                CancelTransitions();
            }
            UpdateShimmerLoop();
            InvalidateVisual();
        }
        else if (change.Property == ForegroundProperty)
        {
            _highlightBrush = null;
            _highlightSourceBrush = null;
            _layoutDirty = true;
            InvalidateVisual();
        }
        else if (change.Property == FontFamilyProperty
                 || change.Property == FontSizeProperty
                 || change.Property == FontWeightProperty)
        {
            _layoutDirty = true;
            InvalidateMeasure();
            InvalidateVisual();
        }
        else if (change.Property == IsVisibleProperty
                 || change.Property == ArcMotion.LevelProperty)
        {
            if (ArcMotion.GetEffectiveLevel(this) == ArcMotionLevel.Off)
            {
                CancelTransitions();
            }
            UpdateShimmerLoop();
            InvalidateVisual();
        }
    }

    private void HandleTextChanged(string next)
    {
        var previous = _currentText;
        var currentKey = StepKey;
        var sameStep = _displayedStepKey is not null && ReferenceEquals(_displayedStepKey, currentKey);
        _displayedStepKey = currentKey;

        if (string.Equals(previous, next, StringComparison.Ordinal))
        {
            return;
        }

        var level = ArcMotion.GetEffectiveLevel(this);
        var canAnimate = IsLoaded
            && AnimateUpdates
            && sameStep
            && level != ArcMotionLevel.Off
            && !string.IsNullOrEmpty(previous)
            && !string.IsNullOrEmpty(next);

        if (!canAnimate)
        {
            CancelTransitions();
            _currentText = next;
            _layoutDirty = true;
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        if (IsPrefixContinuation(previous, next))
        {
            // Streaming continuation: keep existing prefix steady and softly reveal the newly appended suffix.
            var constraintWidth = ResolveConstraintWidth();
            var prevWidth = MeasureSingleLineWidth(TrimTrailingEllipsis(previous), constraintWidth);
            _currentText = next;
            _exitingText = null;
            DisposeLayout(ref _exitingLayout);
            _swapGeneration++;
            _swapProgress?.Stop();

            _layoutDirty = true;
            EnsureLayouts(constraintWidth);
            var nextWidth = _currentLayout?.Width ?? prevWidth;

            if (level == ArcMotionLevel.Full && nextWidth > prevWidth + 2.0)
            {
                StartRevealAnimation(prevWidth, nextWidth);
            }
            else
            {
                _revealGeneration++;
                _revealProgress?.Stop();
            }

            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        // Topic / line switch (e.g. reasoning advanced to a new line): smooth vertical roll + crossfade.
        _revealGeneration++;
        _revealProgress?.Stop();

        _exitingText = previous;
        DisposeLayout(ref _exitingLayout);
        _exitingLayout = _currentLayout;
        _currentLayout = null;
        DisposeLayout(ref _currentHighlightLayout);

        _currentText = next;
        _layoutDirty = true;
        StartSwapAnimation(fullMotion: level == ArcMotionLevel.Full);
        InvalidateMeasure();
        InvalidateVisual();
    }

    private async void StartSwapAnimation(bool fullMotion)
    {
        _swapVertical = fullMotion;
        if (_swapProgress is null)
        {
            _swapProgress = new MotionValue<double>(this, 0.0, MotionVectors.Double);
            _swapProgress.Changed += _ => InvalidateVisual();
        }
        _swapProgress.Jump(0.0);
        var generation = ++_swapGeneration;
        var duration = fullMotion ? TimeSpan.FromMilliseconds(210) : TimeSpan.FromMilliseconds(120);
        var arrived = await _swapProgress.AnimateTo(1.0, MotionSpec.Tween(duration, ArcEasings.Enter));
        if (arrived && generation == _swapGeneration)
        {
            _exitingText = null;
            DisposeLayout(ref _exitingLayout);
            InvalidateVisual();
        }
    }

    private async void StartRevealAnimation(double fromX, double toX)
    {
        _revealFromX = fromX;
        _revealToX = toX;
        if (_revealProgress is null)
        {
            _revealProgress = new MotionValue<double>(this, 0.0, MotionVectors.Double);
            _revealProgress.Changed += _ => InvalidateVisual();
        }
        _revealProgress.Jump(0.0);
        var generation = ++_revealGeneration;
        var arrived = await _revealProgress.AnimateTo(1.0, MotionSpec.Tween(TimeSpan.FromMilliseconds(150), ArcEasings.Enter));
        if (arrived && generation == _revealGeneration)
        {
            InvalidateVisual();
        }
    }

    private void CancelTransitions()
    {
        _swapGeneration++;
        _revealGeneration++;
        _swapProgress?.Dispose();
        _swapProgress = null;
        _revealProgress?.Dispose();
        _revealProgress = null;
        _exitingText = null;
        DisposeLayout(ref _exitingLayout);
    }

    private void UpdateShimmerLoop()
    {
        if (AnimateUpdates && IsVisible && ArcMotion.GetEffectiveLevel(this) == ArcMotionLevel.Full)
        {
            _shimmerLoop.Start();
        }
        else
        {
            _shimmerLoop.Stop();
            _shimmerSweep = 0.0;
        }
    }

    private void OnShimmerRunningChanged(bool running)
    {
        if (running && !_shimmerStarted)
        {
            _shimmerPause = 0.15;
            _shimmerStarted = true;
        }
        InvalidateVisual();
    }

    private bool StepShimmer(double dt)
    {
        if (_shimmerPause > 0.0)
        {
            _shimmerPause -= dt;
            if (_shimmerPause > 0.0) return true;
            dt = -_shimmerPause;
            _shimmerPause = 0.0;
            if (_shimmerSweep >= 1.0) _shimmerSweep = 0.0;
        }

        _shimmerSweep = Math.Min(1.0, _shimmerSweep + dt / ShimmerSweepSeconds);
        if (_shimmerSweep >= 1.0)
        {
            _shimmerPause = ShimmerPauseSeconds;
        }
        InvalidateVisual();
        return true;
    }

    private static string TrimTrailingEllipsis(string s) =>
        s.EndsWith('…') ? s[..^1] : s;

    internal static bool IsPrefixContinuation(string previous, string next)
    {
        var prevBase = TrimTrailingEllipsis(previous);
        if (prevBase.Length < 2) return false;
        if (next.StartsWith(prevBase, StringComparison.Ordinal)) return true;

        // Also treat near-identical prefix growth (e.g. trailing punctuation adjusted while streaming) as continuation.
        var minPrefix = Math.Min(prevBase.Length, 14);
        var common = 0;
        var limit = Math.Min(prevBase.Length, next.Length);
        while (common < limit && prevBase[common] == next[common]) common++;
        return common >= minPrefix && next.Length >= prevBase.Length;
    }

    private double ResolveConstraintWidth()
    {
        if (Bounds.Width > 0) return Bounds.Width;
        if (!double.IsNaN(_layoutWidth) && _layoutWidth > 0) return _layoutWidth;
        return double.PositiveInfinity;
    }

    private double MeasureSingleLineWidth(string text, double maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return 0.0;
        var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight);
        using var layout = new TextLayout(
            text,
            typeface,
            FontSize,
            Foreground ?? Brushes.Gray,
            TextAlignment.Left,
            TextWrapping.NoWrap,
            TextTrimming.CharacterEllipsis,
            maxWidth: maxWidth,
            maxLines: 1);
        return layout.Width;
    }

    private void EnsureLayouts(double maxWidth)
    {
        var widthChanged = double.IsNaN(_layoutWidth) || Math.Abs(_layoutWidth - maxWidth) > 0.5;
        if (!_layoutDirty && !widthChanged && _currentLayout is not null)
        {
            return;
        }

        _layoutDirty = false;
        _layoutWidth = maxWidth;
        var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight);
        var fg = Foreground ?? Brushes.Gray;

        DisposeLayout(ref _currentLayout);
        DisposeLayout(ref _currentHighlightLayout);

        _currentLayout = new TextLayout(
            _currentText,
            typeface,
            FontSize,
            fg,
            TextAlignment.Left,
            TextWrapping.NoWrap,
            TextTrimming.CharacterEllipsis,
            maxWidth: maxWidth,
            maxLines: 1);

        if (_exitingText is not null && (widthChanged || _exitingLayout is null))
        {
            DisposeLayout(ref _exitingLayout);
            _exitingLayout = new TextLayout(
                _exitingText,
                typeface,
                FontSize,
                fg,
                TextAlignment.Left,
                TextWrapping.NoWrap,
                TextTrimming.CharacterEllipsis,
                maxWidth: maxWidth,
                maxLines: 1);
        }
    }

    private void EnsureHighlightLayout(double maxWidth)
    {
        if (_currentHighlightLayout is not null) return;
        var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight);
        _currentHighlightLayout = new TextLayout(
            _currentText,
            typeface,
            FontSize,
            ResolveHighlightBrush(),
            TextAlignment.Left,
            TextWrapping.NoWrap,
            TextTrimming.CharacterEllipsis,
            maxWidth: maxWidth,
            maxLines: 1);
    }

    private IBrush ResolveHighlightBrush()
    {
        var fg = Foreground;
        if (_highlightBrush is not null && ReferenceEquals(_highlightSourceBrush, fg))
        {
            return _highlightBrush;
        }
        _highlightSourceBrush = fg;
        if (fg is ISolidColorBrush solid)
        {
            var c = solid.Color;
            // Brighten gently in dark mode (or deepen in light mode) so the shimmer band reads cleanly on any theme.
            var lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            byte Mix(byte ch, byte target) => (byte)Math.Clamp((int)Math.Round(ch + (target - ch) * 0.58), 0, 255);
            var target = lum >= 128 ? (byte)255 : (byte)20;
            _highlightBrush = new ImmutableSolidColorBrush(Color.FromArgb(c.A, Mix(c.R, target), Mix(c.G, target), Mix(c.B, target)));
        }
        else
        {
            _highlightBrush = Brushes.White;
        }
        return _highlightBrush;
    }

    private void DisposeLayouts()
    {
        DisposeLayout(ref _currentLayout);
        DisposeLayout(ref _currentHighlightLayout);
        DisposeLayout(ref _exitingLayout);
    }

    private static void DisposeLayout(ref TextLayout? layout)
    {
        layout?.Dispose();
        layout = null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var maxWidth = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0.0, availableSize.Width);
        EnsureLayouts(maxWidth);
        var h = Math.Max(_currentLayout?.Height ?? 0.0, FontSize * 1.25);
        var w = Math.Min(_currentLayout?.Width ?? 0.0, availableSize.Width);
        return new Size(double.IsInfinity(w) ? 0.0 : w, h);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        EnsureLayouts(bounds.Width);
        if (_currentLayout is null) return;

        var yCenter = Math.Max(0.0, (bounds.Height - _currentLayout.Height) * 0.5);

        // 1. Outgoing line during vertical line-switch roll.
        var swapActive = _exitingLayout is not null && _swapProgress is { IsAnimating: true };
        if (swapActive)
        {
            var p = Math.Clamp(_swapProgress!.Value, 0.0, 1.0);
            var exitOpacity = 1.0 - p;
            if (exitOpacity > 0.01)
            {
                var exitY = yCenter - (_swapVertical ? RollDistancePx * p : 0.0);
                using (context.PushOpacity(exitOpacity))
                {
                    _exitingLayout!.Draw(context, new Point(0, exitY));
                }
            }

            var enterY = yCenter + (_swapVertical ? RollDistancePx * (1.0 - p) : 0.0);
            using (context.PushOpacity(p))
            {
                DrawCurrentLine(context, bounds, enterY);
            }
            return;
        }

        // 2. Streaming continuation: soft left-to-right feathered reveal on the newly appended tail.
        if (_revealProgress is { IsAnimating: true })
        {
            var p = Math.Clamp(_revealProgress.Value, 0.0, 1.0);
            var textWidth = Math.Max(1.0, Math.Min(bounds.Width, _currentLayout.Width));
            var edgeX = _revealFromX + (_revealToX + RevealFeatherPx - _revealFromX) * p;
            var stopSolid = Math.Clamp((edgeX - RevealFeatherPx) / textWidth, 0.0, 1.0);
            var stopFade = Math.Clamp(edgeX / textWidth, stopSolid, 1.0);

            if (stopSolid < 0.999)
            {
                var mask = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                    GradientStops =
                    [
                        new GradientStop(Colors.White, 0.0),
                        new GradientStop(Colors.White, stopSolid),
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), stopFade),
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0),
                    ],
                };
                using (context.PushOpacityMask(mask, new Rect(0, 0, textWidth, bounds.Height)))
                {
                    DrawCurrentLine(context, bounds, yCenter);
                }
                return;
            }
        }

        // 3. Resting line (plus optional subtle shimmer while active thinking).
        DrawCurrentLine(context, bounds, yCenter);
    }

    private void DrawCurrentLine(DrawingContext context, Rect bounds, double y)
    {
        _currentLayout!.Draw(context, new Point(0, y));

        if (!AnimateUpdates || !_shimmerLoop.IsRunning || _shimmerPause > 0.0 || _currentText.Length == 0)
        {
            return;
        }

        var textWidth = Math.Min(bounds.Width, _currentLayout.Width);
        if (textWidth <= 4.0) return;

        EnsureHighlightLayout(bounds.Width);
        if (_currentHighlightLayout is null) return;

        var bandHalf = Math.Max(18.0, FontSize * ShimmerBandEm);
        var center = _shimmerSweep * (textWidth + 2.0 * bandHalf) - bandHalf;
        var left = Math.Clamp((center - bandHalf) / textWidth, 0.0, 1.0);
        var mid = Math.Clamp(center / textWidth, 0.0, 1.0);
        var right = Math.Clamp((center + bandHalf) / textWidth, 0.0, 1.0);
        if (right <= left + 0.001) return;

        var shimmerMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.FromArgb(0, 255, 255, 255), left),
                new GradientStop(Color.FromArgb(215, 255, 255, 255), mid),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), right),
            ],
        };

        using (context.PushOpacityMask(shimmerMask, new Rect(0, 0, textWidth, bounds.Height)))
        {
            _currentHighlightLayout.Draw(context, new Point(0, y));
        }
    }
}
