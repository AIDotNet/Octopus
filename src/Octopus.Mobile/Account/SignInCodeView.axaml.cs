using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Octopus.Mobile.Account;

/// <summary>The second step of every sign-in: the code, copy, "Open browser" and the wait. Its data context is a <see cref="SignInFlow"/>.</summary>
public partial class SignInCodeView : UserControl
{
    public SignInCodeView() => InitializeComponent();

    private async void OnCopyCode(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SignInFlow { UserCode: { Length: > 0 } code } flow) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(code);
                flow.MarkCodeCopied();
            }
        }
        catch
        {
            // Clipboard may be unavailable on headless or restricted environments.
        }
    }
}
