using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Octopus.App.Workspace;

namespace Octopus.App.AgentGui;

public partial class AgentGuiTabView : UserControl, ITabContent
{
    private const double BottomPinThreshold = 42.0;

    private AgentGuiTabViewModel? _model;
    private bool _started;
    private bool _programmaticScroll;
    private bool _scrollQueued;
    private bool _pointerPressedInTimeline;

    public AgentGuiTabView()
    {
        InitializeComponent();
        ComposerInput.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        TimelineScroller.ScrollChanged += OnTimelineScrollChanged;
        TimelineScroller.AddHandler(PointerPressedEvent, OnTimelinePointerPressed, RoutingStrategies.Tunnel);
        TimelineScroller.AddHandler(PointerReleasedEvent, OnTimelinePointerReleased, RoutingStrategies.Tunnel);
        ModelPickerPopover.PropertyChanged += OnModelPopoverPropertyChanged;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDropAsync);
    }

    private void OnTimelinePointerPressed(object? sender, PointerPressedEventArgs e) => _pointerPressedInTimeline = true;

    private void OnTimelinePointerReleased(object? sender, PointerReleasedEventArgs e) => _pointerPressedInTimeline = false;

    private void OnModelPopoverPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TKCode.Arc.Popover.IsOpenProperty && e.GetNewValue<bool>() && ModelSearchBox.IsVisible)
        {
            Dispatcher.UIThread.Post(() => ModelSearchBox.Focus(), DispatcherPriority.Input);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.ScrollToBottomRequested -= OnScrollToBottom;
        }
        _model = DataContext as AgentGuiTabViewModel;
        if (_model is not null)
        {
            _model.ScrollToBottomRequested += OnScrollToBottom;
            if (!_started)
            {
                _started = true;
                _ = _model.ConnectAsync();
            }
        }
    }

    private void OnTimelineScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_model is null || _programmaticScroll) return;
        var extentY = TimelineScroller.Extent.Height;
        var viewportY = TimelineScroller.Viewport.Height;
        if (extentY <= viewportY + 1)
        {
            _model.IsPinnedToBottom = true;
            return;
        }
        var distanceFromBottom = extentY - viewportY - TimelineScroller.Offset.Y;
        _model.IsPinnedToBottom = distanceFromBottom <= BottomPinThreshold;
    }

    private void OnScrollToBottom(object? sender, bool force)
    {
        if (!force && (_pointerPressedInTimeline || _scrollQueued)) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (_model is null || (!force && (!_model.IsPinnedToBottom || _pointerPressedInTimeline))) return;
            _programmaticScroll = true;
            try { TimelineScroller.ScrollToEnd(); }
            finally { _programmaticScroll = false; }
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
            if (_model.IsAnyHudOpen)
            {
                e.Handled = true;
                _model.ActiveHudPanel = HudPanelKind.None;
                return;
            }
            if (pickerOpen)
            {
                e.Handled = true;
                _model.IsCommandPopupOpen = false;
                _model.IsFilePopupOpen = false;
                return;
            }
            if (_model.IsWorking)
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
            if (_model.CanSend)
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
