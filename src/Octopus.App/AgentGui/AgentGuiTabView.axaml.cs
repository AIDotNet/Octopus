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
    private AgentGuiTabViewModel? _model;
    private bool _isAttached;

    public AgentGuiTabView()
    {
        InitializeComponent();
        // Expose the shared timeline DataTemplates on AgentGuiTabView too (zero extra controls; preserves template discovery).
        DataTemplates.AddRange(TimelineView.DataTemplates);
        ComposerInput.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        ModelSearchBox.AddHandler(KeyDownEvent, OnModelSearchKeyDown, RoutingStrategies.Tunnel);
        ModelPickerPopover.PropertyChanged += OnModelPopoverPropertyChanged;
        TimelineView.LinkOpenRequested += OnTimelineLinkOpenRequested;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDropAsync);
    }

    private void OnTimelineLinkOpenRequested(object? sender, string path) => _model?.OpenToolFile(path);

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
        // First time on screen: only now does the ACP process start (a restored tab that is never shown never runs).
        TryStartIfVisible();
        // Every time the user enters this GUI tab, keep the timeline pinned to the very bottom.
        PinAndScrollToBottomOnEntry();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
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
        TimelineView.QueueScrollToBottom(force: true);
    }

    private void TryStartIfVisible()
    {
        if (_isAttached)
        {
            _model?.EnsureStarted();
        }
    }

    /// <summary>
    /// Starts the ACP session if it has not been started yet, so a remote device attaching before this tab is ever
    /// shown on screen still brings the conversation up without double-connecting when the tab is later opened here.
    /// </summary>
    internal void EnsureStarted() => _model?.EnsureStarted();

    private void OnFocusComposerRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ComposerInput.Focus();
            ComposerInput.CaretIndex = ComposerInput.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }

    private void OnScrollToBottom(object? sender, bool force) => TimelineView.QueueScrollToBottom(force);

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
