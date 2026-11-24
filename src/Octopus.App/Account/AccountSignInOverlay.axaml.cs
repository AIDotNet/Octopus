using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Octopus.App.Account;

public partial class AccountSignInOverlay : UserControl
{
    public AccountSignInOverlay() => InitializeComponent();

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is AccountViewModel vm)
        {
            vm.CloseSignInDialogCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private async void OnCopyCode(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AccountViewModel { UserCode: { Length: > 0 } code } vm
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(code);
            vm.MarkCodeCopied();
        }
    }
}
