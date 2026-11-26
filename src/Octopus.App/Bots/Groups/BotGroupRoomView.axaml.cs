using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Octopus.App.Bots.Groups;

public partial class BotGroupRoomView : UserControl
{
    private BotGroupRoomViewModel? _model;

    public BotGroupRoomView()
    {
        InitializeComponent();
        ComposerInput.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.ScrollToBottomRequested -= OnScrollToBottomRequested;
            _model.FocusComposerRequested -= OnFocusComposerRequested;
        }
        _model = DataContext as BotGroupRoomViewModel;
        if (_model is not null)
        {
            _model.ScrollToBottomRequested += OnScrollToBottomRequested;
            _model.FocusComposerRequested += OnFocusComposerRequested;
        }
    }

    private void OnScrollToBottomRequested(object? sender, bool force)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var remaining = TranscriptScroller.Extent.Height - TranscriptScroller.Viewport.Height - TranscriptScroller.Offset.Y;
            if (force || remaining < 120)
            {
                TranscriptScroller.ScrollToEnd();
            }
        }, DispatcherPriority.Background);
    }

    private void OnFocusComposerRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ComposerInput.Focus();
            ComposerInput.CaretIndex = ComposerInput.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (_model is null)
        {
            return;
        }
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (e.KeyModifiers == KeyModifiers.None && _model.CanSend)
            {
                e.Handled = true;
                _ = _model.SendAsync();
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (_model.IsReplyPickerOpen || _model.IsMentionPickerOpen)
            {
                _model.IsReplyPickerOpen = false;
                _model.IsMentionPickerOpen = false;
                e.Handled = true;
            }
        }
    }
}
