using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Octopus.App.Settings.About;

public partial class AboutPageView : UserControl
{
    public AboutPageView() => InitializeComponent();

    private void OnCopyDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AboutSettingsViewModel vm) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = clipboard.SetTextAsync(vm.BuildDiagnosticsText());
        }
        vm.MarkDiagnosticsCopied();
    }
}
