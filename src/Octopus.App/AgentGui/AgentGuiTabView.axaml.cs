using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Octopus.App.Workspace;
using TKCode.Arc;

namespace Octopus.App.AgentGui;

public partial class AgentGuiTabView : UserControl, ITabContent
{
    private const double BottomPinThreshold = 42.0;

    private AgentGuiTabViewModel? _model;
    private ScrollArea? _timelineScroller;
    private TopLevel? _topLevel;
    private bool _started;
    private bool _isAttached;
    private bool _scrollQueued;
    private bool _forceScrollQueued;
    private bool _pointerPressedInTimeline;

    public AgentGuiTabView()
    {
        InitializeComponent();
        ComposerInput.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        ModelSearchBox.AddHandler(KeyDownEvent, OnModelSearchKeyDown, RoutingStrategies.Tunnel);
        TimelineList.TemplateApplied += OnTimelineTemplateApplied;
        ModelPickerPopover.PropertyChanged += OnModelPopoverPropertyChanged;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDropAsync);
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
            if (_model?.IsPinnedToBottom == true)
            {
                QueueScrollToBottom(force: true);
            }
        }
    }

    private void OnTimelinePointerPressed(object? sender, PointerPressedEventArgs e) => _pointerPressedInTimeline = true;

    private void OnTimelinePointerReleased(object? sender, PointerReleasedEventArgs e) => _pointerPressedInTimeline = false;

    private void OnTopLevelPointerReleased(object? sender, PointerReleasedEventArgs e) => _pointerPressedInTimeline = false;

    private void OnModelPopoverPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TKCode.Arc.Popover.IsOpenProperty && e.GetNewValue<bool>() && ModelSearchBox.IsVisible)
        {
            Dispatcher.UIThread.Post(() => ModelSearchBox.Focus(), DispatcherPriority.Input);
        }
    }

    private void OnModelSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_model is null) return;
        if (e.Key == Key.Enter && _model.Composer.CommitModelSearch())
        {
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _model.Composer.IsModelPickerOpen)
        {
            _model.Composer.IsModelPickerOpen = false;
            e.Handled = true;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.ScrollToBottomRequested -= OnScrollToBottom;
            _model.FocusComposerRequested -= OnFocusComposerRequested;
        }
        _model = DataContext as AgentGuiTabViewModel;
        if (_model is not null)
        {
            _model.ScrollToBottomRequested += OnScrollToBottom;
            _model.FocusComposerRequested += OnFocusComposerRequested;
            TryStartIfVisible();
            if (_isAttached)
            {
                PinAndScrollToBottomOnEntry();
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        _pointerPressedInTimeline = false;
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(PointerReleasedEvent, OnTopLevelPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        // First time on screen: only now does the ACP process start (a restored tab that is never shown never runs).
        TryStartIfVisible();
        // Every time the user enters this GUI tab, keep the timeline pinned to the very bottom.
        PinAndScrollToBottomOnEntry();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        _pointerPressedInTimeline = false;
        if (_topLevel is not null)
        {
            _topLevel.RemoveHandler(PointerReleasedEvent, OnTopLevelPointerReleased);
            _topLevel = null;
        }
        // Release transient overlays when switching away from this tab.
        if (_model is not null)
        {
            _model.IsCommandPopupOpen = false;
            _model.IsFilePopupOpen = false;
            if (_model.ScriptPreview.IsOpen) _model.ScriptPreview.Close();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && _isAttached && change.GetNewValue<bool>())
        {
            PinAndScrollToBottomOnEntry();
        }
    }

    private void PinAndScrollToBottomOnEntry()
    {
        if (_model is null) return;
        _model.IsPinnedToBottom = true;
        QueueScrollToBottom(force: true);
    }

    private void TryStartIfVisible()
    {
        if (!_started && _isAttached && _model is not null)
        {
            _started = true;
            _ = _model.ConnectAsync();
        }
    }

    /// <summary>
    /// Starts the ACP session if it has not been started yet, so a remote device attaching before this tab is ever
    /// shown on screen still brings the conversation up without double-connecting when the tab is later opened here.
    /// </summary>
    internal void EnsureStarted()
    {
        if (!_started && _model is not null)
        {
            _started = true;
            _ = _model.ConnectAsync();
        }
    }

    private void OnFocusComposerRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ComposerInput.Focus();
            ComposerInput.CaretIndex = ComposerInput.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }

    private void OnTimelineScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_model is null || _timelineScroller is null) return;
        var extentY = _timelineScroller.Extent.Height;
        var viewportY = _timelineScroller.Viewport.Height;
        if (extentY <= viewportY + 1)
        {
            _model.IsPinnedToBottom = true;
            return;
        }

        var distanceFromBottom = extentY - viewportY - _timelineScroller.Offset.Y;
        // When Viewport grows (e.g. multi-line composer clears on submit) or Extent shrinks (e.g. ticker collapses),
        // Avalonia's CoerceOffset can clamp Offset.Y down before the new Extent is applied. Only an upward offset
        // delta strictly larger than that layout clamp represents a genuine user scroll upward.
        var maxLayoutClampDrop = Math.Max(0.0, e.ViewportDelta.Y) + Math.Max(0.0, -e.ExtentDelta.Y);

        if (e.OffsetDelta.Y < -(maxLayoutClampDrop + 1.0) && distanceFromBottom > 1.0)
        {
            _model.IsPinnedToBottom = false;
            return;
        }

        // User scrolled back down to the bottom (even if content grew in the same layout pass).
        if (!_model.IsPinnedToBottom && e.OffsetDelta.Y > 0.5 && distanceFromBottom <= BottomPinThreshold)
        {
            _model.IsPinnedToBottom = true;
            return;
        }

        // Content or viewport size changed (new prompt, streaming output, composer resize, tab re-attach):
        // preserve pinned state and immediately follow the bottom so large growth deltas never falsely unpin.
        if (Math.Abs(e.ExtentDelta.Y) > 0.5 || Math.Abs(e.ViewportDelta.Y) > 0.5)
        {
            if (_model.IsPinnedToBottom && !_pointerPressedInTimeline && distanceFromBottom > 0.5)
            {
                _timelineScroller.ScrollToEnd();
            }
            return;
        }

        if (Math.Abs(e.OffsetDelta.Y) > 0.5)
        {
            _model.IsPinnedToBottom = distanceFromBottom <= BottomPinThreshold;
        }
    }

    private void OnScrollToBottom(object? sender, bool force) => QueueScrollToBottom(force);

    private void QueueScrollToBottom(bool force)
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
            if (_model is null || _timelineScroller is null) return;
            if (!forced && (!_model.IsPinnedToBottom || _pointerPressedInTimeline)) return;
            if (forced) _model.IsPinnedToBottom = true;
            _timelineScroller.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (_model is null) return;

        // Cmd/Ctrl+V clipboard image paste when the active agent supports images.
        var pasteModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(pasteModifier) && _model.Composer.SupportsImageAttachments)
        {
            _ = TryPasteClipboardImageAsync();
            return;
        }

        var pickerOpen = _model.IsCommandPopupOpen || _model.IsFilePopupOpen;

        if (e.Key == Key.Escape)
        {
            if (_model.ScriptPreview.IsOpen)
            {
                e.Handled = true;
                _model.ScriptPreview.Close();
                return;
            }
            if (_model.HasSelectedSubagent)
            {
                e.Handled = true;
                _model.SelectedSubagent = null;
                return;
            }
            if (_model.HasExpandedBackgroundShellLogs)
            {
                e.Handled = true;
                _model.CollapseExpandedShellLogs();
                return;
            }
            if (pickerOpen)
            {
                e.Handled = true;
                _model.IsCommandPopupOpen = false;
                _model.IsFilePopupOpen = false;
                return;
            }
            if (_model.IsTurnActive)
            {
                e.Handled = true;
                _ = _model.CancelTurnAsync();
                return;
            }
        }

        if (pickerOpen)
        {
            if (e.Key == Key.Up && _model.MovePickerSelection(-1))
            {
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Down && _model.MovePickerSelection(1))
            {
                e.Handled = true;
                return;
            }
            if ((e.Key == Key.Tab || (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
                && _model.CommitSelectedPickerItem())
            {
                e.Handled = true;
                return;
            }
        }
        else if (e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Key == Key.Up && _model.NavigateHistoryUp())
            {
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Down && _model.NavigateHistoryDown())
            {
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var sendNowModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if (e.KeyModifiers.HasFlag(sendNowModifier))
            {
                if (_model.CanSend)
                {
                    e.Handled = true;
                    _ = _model.SendPromptNowAsync();
                }
            }
            else if (e.KeyModifiers == KeyModifiers.None && _model.CanSend)
            {
                e.Handled = true;
                _ = _model.SendPromptAsync();
            }
        }
    }

    private async Task TryPasteClipboardImageAsync()
    {
        if (_model is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try
        {
            var formats = await clipboard.GetDataFormatsAsync();
            if (!formats.Contains(DataFormat.Bitmap)) return;
            using var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap is null) return;
            using var ms = new MemoryStream();
            bitmap.Save(ms, PngBitmapEncoderOptions.Default);
            _model.AttachImageBytes(ms.ToArray(), $"paste-{DateTime.Now:HHmmss}.png");
        }
        catch
        {
            // Ignore clipboard errors; text paste proceeds normally.
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.TryGetFiles() is { Length: > 0 };
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDropAsync(object? sender, DragEventArgs e)
    {
        if (_model is null) return;
        var files = e.DataTransfer.TryGetFiles();
        if (files is null || files.Length == 0) return;
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
        if (paths.Count > 0)
        {
            await _model.AttachFilePathsAsync(paths);
        }
    }

    public Task<bool> HasRunningChildAsync() => _model?.HasRunningChildAsync() ?? Task.FromResult(false);

    public Task CloseAsync() => _model?.CloseAsync() ?? Task.CompletedTask;

    public Task RestartAsync() => _model?.RestartAsync() ?? Task.CompletedTask;

    public void FocusContent() => ComposerInput.Focus();
}
