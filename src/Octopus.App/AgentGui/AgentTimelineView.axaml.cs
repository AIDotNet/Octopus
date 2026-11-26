using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StreamMark.Avalonia.Extensions;
using StreamMark.Avalonia.Rendering;
using TKCode.Arc;

namespace Octopus.App.AgentGui;

/// <summary>
/// Shared, lightweight pure-text Agent message timeline control (Scheme C: zero-card work area,
/// 5-line sliding live ticker, lazy-mounted tool details, frozen Markdown blocks, smart auto-scroll).
/// Reused 1:1 by both the main <see cref="AgentGuiTabView"/> and the right-panel Subagent inspector
/// without carrying any composer, activity tray, or workspace index overhead.
/// </summary>
public partial class AgentTimelineView : UserControl
{
    private const double BottomPinThreshold = 42.0;

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<AgentTimelineView, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<Thickness> StreamPaddingProperty =
        AvaloniaProperty.Register<AgentTimelineView, Thickness>(nameof(StreamPadding), new Thickness(14, 10, 14, 12));

    public static readonly StyledProperty<double> MaxContentWidthProperty =
        AvaloniaProperty.Register<AgentTimelineView, double>(nameof(MaxContentWidth), 860.0);

    public static readonly StyledProperty<bool> IsPinnedToBottomProperty =
        AvaloniaProperty.Register<AgentTimelineView, bool>(nameof(IsPinnedToBottom), true, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> ShowJumpToBottomProperty =
        AvaloniaProperty.Register<AgentTimelineView, bool>(nameof(ShowJumpToBottom));

    public static readonly StyledProperty<ICommand?> JumpToBottomCommandProperty =
        AvaloniaProperty.Register<AgentTimelineView, ICommand?>(nameof(JumpToBottomCommand));

    public static readonly StyledProperty<string?> WorkingDirectoryProperty =
        AvaloniaProperty.Register<AgentTimelineView, string?>(nameof(WorkingDirectory));

    private ScrollArea? _timelineScroller;
    private TopLevel? _topLevel;
    private bool _scrollQueued;
    private bool _forceScrollQueued;
    private bool _pointerPressedInTimeline;

    public AgentTimelineView()
    {
        InitializeComponent();
        TimelineList.TemplateApplied += OnTimelineTemplateApplied;
        MarkdownServices.SetServices(this, AgentMarkdownLinks.Services);
        AddHandler(TKCode.Arc.Markdown.LinkClickedEvent, OnMarkdownLinkClicked);
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public Thickness StreamPadding
    {
        get => GetValue(StreamPaddingProperty);
        set => SetValue(StreamPaddingProperty, value);
    }

    public double MaxContentWidth
    {
        get => GetValue(MaxContentWidthProperty);
        set => SetValue(MaxContentWidthProperty, value);
    }

    public bool IsPinnedToBottom
    {
        get => GetValue(IsPinnedToBottomProperty);
        set => SetValue(IsPinnedToBottomProperty, value);
    }

    public bool ShowJumpToBottom
    {
        get => GetValue(ShowJumpToBottomProperty);
        set => SetValue(ShowJumpToBottomProperty, value);
    }

    public ICommand? JumpToBottomCommand
    {
        get => GetValue(JumpToBottomCommandProperty);
        set => SetValue(JumpToBottomCommandProperty, value);
    }

    public string? WorkingDirectory
    {
        get => GetValue(WorkingDirectoryProperty);
        set => SetValue(WorkingDirectoryProperty, value);
    }

    /// <summary>Raised when a local file link inside rendered Markdown is clicked.</summary>
    public event EventHandler<string>? LinkOpenRequested;

    private void OnMarkdownLinkClicked(object? sender, LinkClickedEventArgs e)
    {
        if (AgentMarkdownLinks.ResolvePath(e.Url, WorkingDirectory) is not { } path) return;
        e.Handled = true;
        LinkOpenRequested?.Invoke(this, path);
    }

    private void OnTimelineTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (_timelineScroller is not null)
        {
            _timelineScroller.ScrollChanged -= OnTimelineScrollChanged;
            _timelineScroller.RemoveHandler(PointerPressedEvent, OnTimelinePointerPressed);
            _timelineScroller.RemoveHandler(PointerReleasedEvent, OnTimelinePointerReleased);
        }

        _timelineScroller = e.NameScope.Find<ScrollArea>("PART_ScrollViewer");
        if (_timelineScroller is not null)
        {
            _timelineScroller.ScrollChanged += OnTimelineScrollChanged;
            _timelineScroller.AddHandler(PointerPressedEvent, OnTimelinePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            _timelineScroller.AddHandler(PointerReleasedEvent, OnTimelinePointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            if (IsPinnedToBottom)
            {
                QueueScrollToBottom(force: true);
            }
        }
    }

    private void OnTimelinePointerPressed(object? sender, PointerPressedEventArgs e) => _pointerPressedInTimeline = true;

    private void OnTimelinePointerReleased(object? sender, PointerReleasedEventArgs e) => _pointerPressedInTimeline = false;

    private void OnTopLevelPointerReleased(object? sender, PointerReleasedEventArgs e) => _pointerPressedInTimeline = false;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _pointerPressedInTimeline = false;
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(PointerReleasedEvent, OnTopLevelPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _pointerPressedInTimeline = false;
        _topLevel?.RemoveHandler(PointerReleasedEvent, OnTopLevelPointerReleased);
        _topLevel = null;
    }

    private void OnTimelineScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_timelineScroller is null) return;
        var extentY = _timelineScroller.Extent.Height;
        var viewportY = _timelineScroller.Viewport.Height;
        if (extentY <= viewportY + 1)
        {
            IsPinnedToBottom = true;
            return;
        }

        var distanceFromBottom = extentY - viewportY - _timelineScroller.Offset.Y;
        var maxLayoutClampDrop = Math.Max(0.0, e.ViewportDelta.Y) + Math.Max(0.0, -e.ExtentDelta.Y);

        if (e.OffsetDelta.Y < -(maxLayoutClampDrop + 1.0) && distanceFromBottom > 1.0)
        {
            IsPinnedToBottom = false;
            return;
        }

        if (!IsPinnedToBottom && e.OffsetDelta.Y > 0.5 && distanceFromBottom <= BottomPinThreshold)
        {
            IsPinnedToBottom = true;
            return;
        }

        if (Math.Abs(e.ExtentDelta.Y) > 0.5 || Math.Abs(e.ViewportDelta.Y) > 0.5)
        {
            if (IsPinnedToBottom && !_pointerPressedInTimeline && distanceFromBottom > 0.5)
            {
                _timelineScroller.ScrollToEnd();
            }
            return;
        }

        if (Math.Abs(e.OffsetDelta.Y) > 0.5)
        {
            IsPinnedToBottom = distanceFromBottom <= BottomPinThreshold;
        }
    }

    public void QueueScrollToBottom(bool force)
    {
        if (!force && _pointerPressedInTimeline) return;
        if (force) _forceScrollQueued = true;
        if (_scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            var forced = _forceScrollQueued;
            _scrollQueued = false;
            _forceScrollQueued = false;
            if (_timelineScroller is null) return;
            if (!forced && (!IsPinnedToBottom || _pointerPressedInTimeline)) return;
            if (forced) IsPinnedToBottom = true;
            _timelineScroller.ScrollToEnd();
        }, DispatcherPriority.Background);
    }
}
