using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc.Motion;

namespace Octopus.Ticker;

/// <summary>
/// Model contract consumed by <see cref="WorkTickerViewport"/> on both desktop (<c>Octopus.App</c>)
/// and mobile (<c>Octopus.Mobile</c>) so the 3-line sliding live ticker has identical physics,
/// opacity curves, and clipping everywhere.
/// </summary>
internal interface IWorkTickerSource
{
    int VisibleCount { get; }
    object Line0 { get; }
    object Line1 { get; }
    object Line2 { get; }
    object Exit0 { get; }
    object Exit1 { get; }
    object Exit2 { get; }

    /// <summary>Clears exiting ghost snapshots once the upward slide has settled.</summary>
    void ClearExiting();

    /// <summary>Raised after a sync with the upward row shift (0..3) and current visible row count (0..3).</summary>
    event Action<int, int>? Updated;
}

/// <summary>
/// Shared upward-shift detector used by both desktop and mobile ticker view models so batch updates,
/// in-place updates, growth (1 -> 2 -> 3), and out-of-order completions behave identically.
/// </summary>
internal static class WorkTickerShift
{
    public static int Compute(
        int prevCount, object? p0, object? p1, object? p2,
        int nextCount, object? n0, object? n1, object? n2)
    {
        if (prevCount <= 0 || nextCount <= 0) return 0;

        // 1. Exact suffix-of-prev == prefix-of-next match (handles 0, 1, or 2 row upward shifts cleanly).
        for (var shift = 0; shift < prevCount; shift++)
        {
            var overlap = Math.Min(prevCount - shift, nextCount);
            var match = true;
            for (var i = 0; i < overlap; i++)
            {
                if (!ReferenceEquals(At(p0, p1, p2, shift + i), At(n0, n1, n2, i)))
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
            if (Contains(At(p0, p1, p2, i), nextCount, n0, n1, n2)) break;
            dropped++;
        }
        return dropped;
    }

    private static object? At(object? a, object? b, object? c, int index) => index switch
    {
        0 => a,
        1 => b,
        2 => c,
        _ => null,
    };

    private static bool Contains(object? key, int count, object? a, object? b, object? c) =>
        (count > 0 && ReferenceEquals(key, a)) ||
        (count > 1 && ReferenceEquals(key, b)) ||
        (count > 2 && ReferenceEquals(key, c));
}

/// <summary>
/// Clipped, spring-animated 3-line live ticker viewport shared between desktop and mobile.
/// Uses a monotonic <see cref="MotionValue{T}"/> scroll coordinate so consecutive upward shifts
/// preserve momentum without jumping, keeps height constant during 3-to-3 shifts (zero layout per frame),
/// fades exiting rows out at the top edge and entering rows in from below, and respects <see cref="ArcMotion"/>.
/// </summary>
public sealed class WorkTickerViewport : Panel
{
    /// <summary>Bounce-free settle spring (0.33s, bounce 0) matching Arc's row reflow motion.</summary>
    internal const ArcSpring SlideSpring = ArcSpring.Settle;

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<WorkTickerViewport, double>(nameof(Spacing), 1.0);

    private const int MaxVisibleRows = 3;
    private const int MaxExitingRows = 3;
    private const double Epsilon = 0.001;
    private const double GrowthSlideFraction = 0.35;

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
    private double _rowHeight;

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

    /// <summary>Current animated visible row count (0..3).</summary>
    internal double AnimatedVisibleRows => _visibleRowsValue;

    /// <summary>Measured single-row height in DIPs.</summary>
    internal double MeasuredRowHeight => _rowHeight;

    internal double GetSlotOffsetY(int index) => _lineTransforms[index].Y;
    internal double GetSlotOpacity(int index) => _lineSlots[index].Opacity;
    internal double GetExitOffsetY(int index) => _exitTransforms[index].Y;
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
        SubscribeSource(false);

        _scroll?.Dispose();
        _scroll = null;

        _visibleRows?.Dispose();
        _visibleRows = null;

        _exitActiveCount = 0;
        base.OnDetachedFromVisualTree(e);
    }

    private void BindSource(IWorkTickerSource? next)
    {
        if (ReferenceEquals(_source, next)) return;

        SubscribeSource(false);
        _source = next;

        if (_source is not null)
        {
            _exitSlots[0].Content = _source.Exit0;
            _exitSlots[1].Content = _source.Exit1;
            _exitSlots[2].Content = _source.Exit2;
            _lineSlots[0].Content = _source.Line0;
            _lineSlots[1].Content = _source.Line1;
            _lineSlots[2].Content = _source.Line2;
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
        _targetScroll = 0;
        _scrollValue = 0;
        _targetVisibleRows = visibleCount;
        _visibleRowsValue = visibleCount;
        _exitActiveCount = 0;
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

            // Prevent lag from exceeding the 3 exiting ghost slots during extreme bursts.
            var minScroll = _targetScroll - MaxExitingRows;
            if (_scroll.Value < minScroll)
            {
                _scroll.Jump(minScroll);
            }

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
            _source?.ClearExiting();
            ApplyVisuals();
        }
    }

    private async void StartHeightAnimation()
    {
        if (_visibleRows is not { } visibleRows) return;
        await visibleRows.AnimateTo(_targetVisibleRows, MotionSpec.Spring(SlideSpring));
    }

    private void OnScrollValueChanged(double value)
    {
        _scrollValue = value;
        ApplyVisuals();
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

            _exitTransforms[i].Y = visualRow * stepY;
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

            _lineTransforms[i].Y = (visualRow + revealOffset) * stepY;
            slot.Opacity = ComputeRowOpacity(visualRow, visibleRows);
        }
    }

    /// <summary>
    /// Continuous opacity profile across the viewport that matches the resting weights exactly
    /// (1 row: [1.0]; 2 rows: [0.65, 1.0]; 3 rows: [0.38, 0.66, 1.0]) while smoothly fading exiting
    /// rows out at the top edge and entering rows in from below.
    /// </summary>
    internal static double ComputeRowOpacity(double visualRow, double visibleRows)
    {
        var v = Math.Clamp(visibleRows, 0.0, MaxVisibleRows);
        if (v <= Epsilon) return 0.0;

        if (visualRow < 0.0)
        {
            if (visualRow <= -1.0) return 0.0;
            return TopSlotOpacity(v) * (1.0 + visualRow);
        }

        var bottomSlot = Math.Max(0.0, v - 1.0);
        if (visualRow > bottomSlot)
        {
            var below = visualRow - bottomSlot;
            return below >= 1.0 ? 0.0 : 1.0 - below;
        }

        var distFromBottom = bottomSlot - visualRow;
        if (v <= 2.0)
        {
            return Lerp(1.0, 0.65, Math.Clamp(distFromBottom, 0.0, 1.0));
        }

        var middleOpacity = Lerp(0.65, 0.66, Math.Clamp(v - 2.0, 0.0, 1.0));
        if (distFromBottom <= 1.0)
        {
            return Lerp(1.0, middleOpacity, distFromBottom);
        }

        return Lerp(middleOpacity, 0.38, Math.Clamp(distFromBottom - 1.0, 0.0, 1.0));
    }

    private static double TopSlotOpacity(double visibleRows)
    {
        if (visibleRows <= 1.0) return 1.0;
        if (visibleRows <= 2.0) return Lerp(1.0, 0.65, visibleRows - 1.0);
        return Lerp(0.65, 0.38, visibleRows - 2.0);
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
        var rowRect = new Rect(0, 0, finalSize.Width, rowHeight);

        for (var i = 0; i < MaxExitingRows; i++)
        {
            var slot = _exitSlots[i];
            if (slot.IsVisible) slot.Arrange(rowRect);
        }

        for (var i = 0; i < MaxVisibleRows; i++)
        {
            var slot = _lineSlots[i];
            if (slot.IsVisible) slot.Arrange(rowRect);
        }

        ApplyVisuals();
        return finalSize;
    }
}
