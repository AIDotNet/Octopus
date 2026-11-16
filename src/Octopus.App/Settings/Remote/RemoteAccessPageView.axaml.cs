using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Octopus.App.Settings.Remote;

public partial class RemoteAccessPageView : UserControl
{
    public RemoteAccessPageView() => InitializeComponent();

    private void OnCopyLink(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RemoteAccessSettingsViewModel { PairingLink: { } link } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = clipboard.SetTextAsync(link);
        }
    }
}
