using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using TKCode.Arc;
using TKCode.Arc.Motion;
using TKCode.Arc.Primitives;

namespace Octopus.App.Explorer;

/// <summary>
/// A single-select <see cref="ToggleGroup"/> whose options expand their label only while selected.
/// Coordinates the selection pill (<see cref="GlidingIndicator"/>) with each option's <see cref="TabLabelReveal"/>
/// on the same bounce-free spring (<see cref="TabSpring"/>), predicting the destination tab's settled rectangle so
/// intermediate layout frames of the collapsing/expanding labels never cancel the pill's glide.
/// </summary>
public sealed class ExplorerTabGroup : ToggleGroup
{
    /// <summary>
    /// Shared bounce-free spring (Arc's Quick: visualDuration 0.25s, bounce 0) for both the gliding pill and the
    /// expanding/collapsing tab labels so they accelerate, travel and settle together without jittering the group border.
    /// </summary>
    internal const ArcSpring TabSpring = ArcSpring.Quick;

    private const double RectEpsilon = 0.5;

    private GlidingIndicator? _indicator;
    private Control? _watchedContainer;
    private Rect _targetRect;
    private bool _placed;
    private bool _gliding;
    private bool _rebuilding;
    private bool _layoutScheduled;

    public ExplorerTabGroup()
    {
        SelectionChanged += OnTabSelectionChanged;
        EffectiveViewportChanged += OnEffectiveViewportChanged;
        PropertyChanged += OnGroupPropertyChanged;
    }

    /// <summary>
    /// Replaces the tab options (for example when switching projects or when git detection finishes) and snaps
    /// both the labels and the selection pill into place without playing a spurious transition.
    /// </summary>
    public void ReplaceOptions(IReadOnlyList<ToggleGroupOption> wanted, string? selectedValue)
    {
        _rebuilding = true;
        try
        {
            Items.Clear();
            foreach (var option in wanted) Items.Add(option);
            SelectedValue = selectedValue;
            SnapAllLabels(selectedValue);
        }
        finally
        {
            _rebuilding = false;
        }

        _placed = false;
        _gliding = false;
        WatchContainer(null);
        ScheduleLayoutPlace();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        DetachIndicator();
        _indicator = e.NameScope.Find<GlidingIndicator>(SelectionPart);

        // Hide PART_Selection from ToggleGroup.OnApplyTemplate so Arc's internal TogglesIndicatorTracker is not attached
        // to it: that tracker snaps on every container Bounds change, which would cancel the glide as labels animate.
        var forwarded = new NameScope();
        if (e.NameScope.Find<Control>("PART_Frame") is { } frame) forwarded.Register("PART_Frame", frame);
        if (e.NameScope.Find<ItemsPresenter>("PART_ItemsPresenter") is { } presenter) forwarded.Register("PART_ItemsPresenter", presenter);
        forwarded.Complete();
        base.OnApplyTemplate(new TemplateAppliedEventArgs(forwarded));

        if (_indicator is not null)
        {
            _indicator.Spring = TabSpring;
            _indicator.PropertyChanged += OnWatchedBoundsChanged;
        }

        _placed = false;
        _gliding = false;
        ScheduleLayoutPlace();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SubscribeAllLabels(subscribe: true);
        ScheduleLayoutPlace();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelLayoutPlace();
        WatchContainer(null);
        SubscribeAllLabels(subscribe: false);
        _placed = false;
        _gliding = false;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void ContainerForItemPreparedOverride(Control container, object? item, int index)
    {
        base.ContainerForItemPreparedOverride(container, item, index);
        if (FindLabel(item) is { } label)
        {
            label.Settled -= OnLabelSettled;
            label.Settled += OnLabelSettled;
        }
        ScheduleLayoutPlace();
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        if (ReferenceEquals(_watchedContainer, container)) WatchContainer(null);
        base.ClearContainerForItemOverride(container);
    }

    private void DetachIndicator()
    {
        if (_indicator is not null)
        {
            _indicator.PropertyChanged -= OnWatchedBoundsChanged;
            _indicator = null;
        }
    }

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding) return;
        if (!_placed || !IsEffectivelyVisible || !IsLoaded)
        {
            _placed = false;
            _gliding = false;
            ScheduleLayoutPlace();
            return;
        }
        StartGlide();
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (IsEffectivelyVisible)
        {
            if (!_placed) PlaceImmediate();
            else ScheduleLayoutPlace();
        }
        else
        {
            _placed = false;
            _gliding = false;
        }
    }

    private void OnGroupPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty) ScheduleLayoutPlace();
    }

    private void OnWatchedBoundsChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty) ScheduleLayoutPlace();
    }

    private void OnLabelSettled() => ScheduleLayoutPlace();

    private void ScheduleLayoutPlace()
    {
        if (_layoutScheduled) return;
        _layoutScheduled = true;
        LayoutUpdated += OnLayoutSettled;
    }

    private void CancelLayoutPlace()
    {
        if (!_layoutScheduled) return;
        _layoutScheduled = false;
        LayoutUpdated -= OnLayoutSettled;
    }

    private void WatchContainer(Control? container)
    {
        if (ReferenceEquals(_watchedContainer, container)) return;
        if (_watchedContainer is not null) _watchedContainer.PropertyChanged -= OnWatchedBoundsChanged;
        _watchedContainer = container;
        if (_watchedContainer is not null) _watchedContainer.PropertyChanged += OnWatchedBoundsChanged;
    }

    private void StartGlide()
    {
        if (_indicator is null) return;
        var selectedIndex = SelectedIndex;
        var container = selectedIndex >= 0 ? ContainerFromIndex(selectedIndex) : null;
        WatchContainer(container);
        if (container is null || !container.IsVisible)
        {
            _indicator.Hide(animate: false);
            _placed = false;
            _gliding = false;
            return;
        }

        if (!TryComputeTargetRect(selectedIndex, predicted: true, out var target))
        {
            ScheduleLayoutPlace();
            return;
        }

        _gliding = true;
        _targetRect = target;
        _placed = true;
        _indicator.MoveTo(target, animate: true, TabSpring);
    }

    private void OnLayoutSettled(object? sender, EventArgs e)
    {
        CancelLayoutPlace();
        if (!IsEffectivelyVisible) return;
        PlaceAfterLayout();
    }

    private void PlaceImmediate()
    {
        if (_indicator is null) return;
        var selectedIndex = SelectedIndex;
        var container = selectedIndex >= 0 ? ContainerFromIndex(selectedIndex) : null;
        WatchContainer(container);
        if (container is null || !container.IsVisible)
        {
            if (_placed) _indicator.Hide(animate: false);
            _placed = false;
            _gliding = false;
            return;
        }

        if (!TryComputeTargetRect(selectedIndex, predicted: false, out var actual)) return;
        _targetRect = actual;
        _placed = true;
        _gliding = false;
        _indicator.MoveTo(actual, animate: false, TabSpring);
    }

    private void PlaceAfterLayout()
    {
        if (_indicator is null) return;
        var selectedIndex = SelectedIndex;
        var container = selectedIndex >= 0 ? ContainerFromIndex(selectedIndex) : null;
        WatchContainer(container);
        if (container is null || !container.IsVisible)
        {
            if (_placed) _indicator.Hide(animate: false);
            _placed = false;
            _gliding = false;
            return;
        }

        if (!_placed)
        {
            if (!TryComputeTargetRect(selectedIndex, predicted: false, out var initial)) return;
            _targetRect = initial;
            _placed = true;
            _gliding = false;
            _indicator.MoveTo(initial, animate: false, TabSpring);
            return;
        }

        var labelsAnimating = AnyLabelAnimating();
        if (_gliding || labelsAnimating)
        {
            if (labelsAnimating)
            {
                // Mid-transition: only retarget if the predicted final destination itself moved (e.g. a count badge appeared).
                if (TryComputeTargetRect(selectedIndex, predicted: true, out var predicted) && !RectsClose(predicted, _targetRect))
                {
                    _targetRect = predicted;
                    _indicator.MoveTo(predicted, animate: true, TabSpring);
                }
                return;
            }

            // Labels have settled: reconcile against the real arranged bounds without snapping away a running glide.
            _gliding = false;
            if (TryComputeTargetRect(selectedIndex, predicted: false, out var finalRect) && !RectsClose(finalRect, _targetRect))
            {
                _targetRect = finalRect;
                _indicator.MoveTo(finalRect, animate: true, TabSpring);
            }
            return;
        }

        // Settled passive layout change (count badge change, density switch, font change): stay locked to the container.
        if (TryComputeTargetRect(selectedIndex, predicted: false, out var actual) && !RectsClose(actual, _targetRect))
        {
            _targetRect = actual;
            _indicator.MoveTo(actual, animate: false, TabSpring);
        }
    }

    private bool TryComputeTargetRect(int selectedIndex, bool predicted, out Rect rect)
    {
        rect = default;
        if (_indicator is null || selectedIndex < 0 || selectedIndex >= ItemCount) return false;
        var selectedContainer = ContainerFromIndex(selectedIndex);
        if (selectedContainer is null || !selectedContainer.IsVisible) return false;
        if (selectedContainer.Bounds.Width <= 0 || selectedContainer.Bounds.Height <= 0) return false;

        var selectedOrigin = selectedContainer.TranslatePoint(default, _indicator);
        if (selectedOrigin is null) return false;

        if (!predicted)
        {
            rect = new Rect(selectedOrigin.Value, selectedContainer.Bounds.Size);
            return true;
        }

        var gap = (ItemsPanelRoot as StackPanel)?.Spacing ?? 0;
        var hasOrigin = false;
        var x = 0.0;

        for (var i = 0; i < ItemCount; i++)
        {
            var container = ContainerFromIndex(i);
            if (container is null || !container.IsVisible) continue;
            if (container.Bounds.Width <= 0) return false;

            if (!hasOrigin)
            {
                var firstOrigin = container.TranslatePoint(default, _indicator);
                if (firstOrigin is null) return false;
                x = firstOrigin.Value.X;
                hasOrigin = true;
            }

            var label = FindLabel(Items[i]);
            var baseWidth = container.Bounds.Width - (label?.Bounds.Width ?? 0);
            var targetLabelWidth = i == selectedIndex ? (label?.EnsureExpandedWidth() ?? 0) : 0;
            var finalWidth = Math.Max(container.MinWidth, baseWidth + targetLabelWidth);

            if (i == selectedIndex)
            {
                rect = new Rect(x, selectedOrigin.Value.Y, finalWidth, selectedContainer.Bounds.Height);
                return true;
            }

            x += finalWidth + gap;
        }

        return false;
    }

    private void SubscribeAllLabels(bool subscribe)
    {
        for (var i = 0; i < ItemCount; i++)
        {
            if (FindLabel(Items[i]) is { } label)
            {
                label.Settled -= OnLabelSettled;
                if (subscribe) label.Settled += OnLabelSettled;
            }
        }
    }

    private void SnapAllLabels(string? selectedValue)
    {
        for (var i = 0; i < ItemCount; i++)
        {
            if (Items[i] is ToggleGroupOption option && FindLabel(option) is { } label)
            {
                label.Snap(Equals(option.Value, selectedValue));
            }
        }
    }

    private bool AnyLabelAnimating()
    {
        for (var i = 0; i < ItemCount; i++)
        {
            if (FindLabel(Items[i]) is { IsRunning: true }) return true;
        }
        return false;
    }

    private static TabLabelReveal? FindLabel(object? item)
    {
        if (item is ToggleGroupOption { Content: Panel panel })
        {
            foreach (var child in panel.Children)
            {
                if (child is TabLabelReveal reveal) return reveal;
            }
        }
        return null;
    }

    private static bool RectsClose(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) <= RectEpsilon &&
        Math.Abs(a.Y - b.Y) <= RectEpsilon &&
        Math.Abs(a.Width - b.Width) <= RectEpsilon &&
        Math.Abs(a.Height - b.Height) <= RectEpsilon;
}

/// <summary>
/// Horizontal spring-driven reveal for an explorer tab's label: smoothly expands/collapses its layout width
/// (including its leading gap from the icon) while sliding and fading the text in/out, matching
/// <see cref="ExplorerTabGroup.TabSpring"/> and respecting <see cref="ArcMotion"/>.
/// </summary>
internal sealed class TabLabelReveal : Decorator
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<TabLabelReveal, bool>(nameof(IsExpanded));

    /// <summary>Leading horizontal space between the tab icon and the label text when expanded.</summary>
    public double LeadGap { get; set; } = 5;

    private const double SlideOffset = 4.0;

    private MotionValue<double>? _openness;
    private double _opennessValue;
    private double _expandedWidth;
    private int _generation;

    static TabLabelReveal()
    {
        ClipToBoundsProperty.OverrideDefaultValue<TabLabelReveal>(true);
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>True while the horizontal reveal spring is still moving.</summary>
    public bool IsRunning => _openness?.IsAnimating == true;

    /// <summary>Raised when a spring animation reaches its target without being superseded.</summary>
    public event Action? Settled;

    /// <summary>Measures and returns the full expanded width (including <see cref="LeadGap"/>).</summary>
    public double EnsureExpandedWidth()
    {
        if (_expandedWidth <= 0 && Child is not null)
        {
            Child.Measure(Size.Infinity);
            _expandedWidth = LeadGap + Child.DesiredSize.Width;
        }
        return _expandedWidth;
    }

    /// <summary>Immediately snaps the reveal state without running a spring.</summary>
    public void Snap(bool expanded)
    {
        _generation++;
        var value = expanded ? 1.0 : 0.0;
        if (_openness is not null) _openness.Jump(value);
        else Apply(value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var initial = IsExpanded ? 1.0 : 0.0;
        _openness?.Dispose();
        _openness = new MotionValue<double>(this, initial, MotionVectors.Double);
        _openness.Changed += Apply;
        Apply(initial);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _generation++;
        _openness?.Dispose();
        _openness = null;
        Apply(IsExpanded ? 1.0 : 0.0);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsExpandedProperty)
        {
            var expanded = change.GetNewValue<bool>();
            var target = expanded ? 1.0 : 0.0;
            if (_openness is null || !IsLoaded || !IsEffectivelyVisible)
            {
                Snap(expanded);
            }
            else
            {
                Animate(target);
            }
        }
    }

    private async void Animate(double target)
    {
        if (_openness is not { } openness) return;
        var generation = ++_generation;
        var arrived = await openness.AnimateTo(target, MotionSpec.Spring(ExplorerTabGroup.TabSpring));
        if (arrived && generation == _generation)
        {
            Settled?.Invoke();
        }
    }

    private void Apply(double value)
    {
        _opennessValue = value;
        if (Child is not null)
        {
            var p = Math.Clamp(value, 0.0, 1.0);
            Child.Opacity = p <= 0.001 ? 0.0 : Math.Clamp((p - 0.10) / 0.75, 0.0, 1.0);
        }
        InvalidateMeasure();
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null)
        {
            _expandedWidth = 0;
            return default;
        }
        // Always measure the label at its natural width so ExplorerTabGroup can predict settled rects even while collapsed.
        Child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        _expandedWidth = LeadGap + Child.DesiredSize.Width;

        var p = Math.Clamp(_opennessValue, 0.0, 1.0);
        var width = p <= 0.001 ? 0.0 : p >= 0.999 ? _expandedWidth : _expandedWidth * p;
        return new Size(width, Child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not null)
        {
            var p = Math.Clamp(_opennessValue, 0.0, 1.0);
            var slide = -SlideOffset * (1.0 - p);
            Child.Arrange(new Rect(LeadGap + slide, 0, Child.DesiredSize.Width, finalSize.Height));
        }
        return finalSize;
    }
}
