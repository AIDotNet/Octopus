using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Octopus.App.Common;

/// <summary>
/// NativeAOT-safe collapsible container that smoothly interpolates measured height and opacity
/// when <see cref="IsExpanded"/> toggles, without relying on <see cref="Visual.IsVisible"/> toggling
/// so Avalonia's transition clock always runs reliably even during high-frequency streaming updates.
/// </summary>
public sealed class SmoothCollapse : ContentControl
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<SmoothCollapse, bool>(nameof(IsExpanded));

    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<SmoothCollapse, double>(nameof(Progress));

    /// <summary>
    /// Optional data context for lazy-template mode: when set alongside <see cref="ContentControl.ContentTemplate"/>,
    /// the child visual tree is only instantiated while expanded or animating, and detached once collapsed.
    /// </summary>
    public static readonly StyledProperty<object?> LazyDataProperty =
        AvaloniaProperty.Register<SmoothCollapse, object?>(nameof(LazyData));

    private bool _hasAttached;
    private bool _transitionsConfigured;
    private double _measuredChildHeight;

    static SmoothCollapse()
    {
        ClipToBoundsProperty.OverrideDefaultValue<SmoothCollapse>(true);
        HorizontalContentAlignmentProperty.OverrideDefaultValue<SmoothCollapse>(HorizontalAlignment.Stretch);
        VerticalContentAlignmentProperty.OverrideDefaultValue<SmoothCollapse>(VerticalAlignment.Top);
        AffectsMeasure<SmoothCollapse>(ProgressProperty);
        AffectsArrange<SmoothCollapse>(ProgressProperty);
    }

    public SmoothCollapse()
    {
        Opacity = 0.0;
        IsHitTestVisible = false;
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public object? LazyData
    {
        get => GetValue(LazyDataProperty);
        set => SetValue(LazyDataProperty, value);
    }

    private bool IsLazyMode => ContentTemplate is not null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_hasAttached)
        {
            _hasAttached = true;
            // Snap to initial state without playing an entrance animation on initial mount.
            var initial = IsExpanded ? 1.0 : 0.0;
            SetValue(ProgressProperty, initial);
            Opacity = initial;
            IsHitTestVisible = IsExpanded;
            SyncLazyContent();
            EnsureTransition();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _hasAttached = false;
        _transitionsConfigured = false;
        Transitions = null;
        if (!IsExpanded)
        {
            SetValue(ProgressProperty, 0.0);
            Opacity = 0.0;
            if (IsLazyMode) Content = null;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsExpandedProperty)
        {
            var expanded = change.GetNewValue<bool>();
            IsHitTestVisible = expanded;
            if (!_hasAttached)
            {
                var snap = expanded ? 1.0 : 0.0;
                SetValue(ProgressProperty, snap);
                Opacity = snap;
                SyncLazyContent();
            }
            else
            {
                if (expanded) SyncLazyContent();
                EnsureTransition();
                SetValue(ProgressProperty, expanded ? 1.0 : 0.0);
                if (!expanded && Progress <= 0.001) SyncLazyContent();
            }
            InvalidateMeasure();
        }
        else if (change.Property == ProgressProperty)
        {
            var p = Math.Clamp(change.GetNewValue<double>(), 0.0, 1.0);
            Opacity = p <= 0.001 ? 0.0 : Math.Clamp((p - 0.1) / 0.9, 0.0, 1.0);
            if (p <= 0.001 && !IsExpanded)
            {
                SyncLazyContent();
            }
        }
        else if (change.Property == LazyDataProperty || change.Property == ContentTemplateProperty)
        {
            SyncLazyContent();
        }
    }

    private void SyncLazyContent()
    {
        if (!IsLazyMode) return;
        var shouldMount = IsExpanded || Progress > 0.001;
        var desired = shouldMount ? LazyData : null;
        if (!ReferenceEquals(Content, desired))
        {
            Content = desired;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var p = Math.Clamp(Progress, 0.0, 1.0);
        if (p <= 0.001 && !IsExpanded)
        {
            _measuredChildHeight = 0;
            return default;
        }
        var desired = base.MeasureOverride(new Size(availableSize.Width, double.PositiveInfinity));
        _measuredChildHeight = desired.Height;
        return p >= 0.999
            ? desired
            : new Size(desired.Width, desired.Height * p);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var p = Math.Clamp(Progress, 0.0, 1.0);
        if (p <= 0.001 && !IsExpanded)
        {
            return default;
        }
        var fullHeight = p >= 0.999 ? finalSize.Height : Math.Max(finalSize.Height, _measuredChildHeight);
        base.ArrangeOverride(new Size(finalSize.Width, fullHeight));
        return finalSize;
    }

    private void EnsureTransition()
    {
        if (_transitionsConfigured) return;
        _transitionsConfigured = true;
        Transitions =
        [
            new DoubleTransition
            {
                Property = ProgressProperty,
                Duration = TimeSpan.FromMilliseconds(200),
                Easing = new CubicEaseOut(),
            },
        ];
    }
}
