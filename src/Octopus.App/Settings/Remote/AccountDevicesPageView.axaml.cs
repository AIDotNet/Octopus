using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Octopus.App.Settings.Remote;

public partial class AccountDevicesPageView : UserControl
{
    public AccountDevicesPageView() => InitializeComponent();

    private async void OnCopyPassword(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AccountDevicesSettingsViewModel { PasswordToCopy: { } password } model && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(password);
            model.MarkCopied();
        }
    }
}
