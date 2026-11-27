using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Octopus.App.Bots;

public partial class BotPanelView : UserControl
{
    private BotPanelViewModel? _model;

    public BotPanelView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _model?.CopyRequested -= OnCopyRequested;
        _model = DataContext as BotPanelViewModel;
        _model?.CopyRequested += OnCopyRequested;
    }

    private void OnGroupRowDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is Control control && control.DataContext is Groups.BotGroupTreeNodeViewModel node)
        {
            BotContextMenus.AttachGroupMenu(control, node);
        }
    }

    private void OnGroupRoomRowDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is Control control && control.DataContext is Groups.BotGroupRoomRowViewModel row)
        {
            BotContextMenus.AttachGroupRoomMenu(control, row);
        }
    }

    private void OnBotRowDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is Control control && control.DataContext is BotTreeNodeViewModel node)
        {
            BotContextMenus.AttachBotMenu(control, node);
        }
    }

    private void OnChatRowDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is Control control && control.DataContext is BotChatRowViewModel row)
        {
            BotContextMenus.AttachChatMenu(control, row);
        }
    }

    private async void OnCopyRequested(object? sender, string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
