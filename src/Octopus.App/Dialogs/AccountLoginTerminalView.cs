using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Octopus.App.Composition;
using Octopus.App.Localization;
using Octopus.Core.Terminals;
using Octopus.Platform;
using Octopus.Storage;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;

namespace Octopus.App.Dialogs;

/// <summary>
/// Hosts a real <see cref="TerminalControl"/> inside a modal dialog so the user can complete interactive OAuth / device
/// code sign-in for an isolated account without opening a permanent workspace tab.
/// </summary>
public sealed class AccountLoginTerminalView : Border
{
    private readonly TerminalControl _terminal;
    private readonly LaunchSpec _spec;
    private readonly ITerminalConnectionFactory _factory;
    private bool _started;

    public AccountLoginTerminalView(LaunchSpec spec, ITerminalConnectionFactory factory, ISettingsStore settings)
    {
        _spec = spec;
        _factory = factory;

        var terminalSettings = settings.Current.Terminal;
        _terminal = new TerminalControl
        {
            FontSize = terminalSettings.FontSize,
            ScrollbackLines = Math.Max(500, terminalSettings.ScrollbackLines),
            ConfirmMultilinePaste = false,
            PathLinksEnabled = false,
            ExitedMessageFormat = AppLangResources.Get(AppLangResourceKind.TerminalExited),
        };
        if (!string.IsNullOrWhiteSpace(terminalSettings.FontFamily))
        {
            _terminal.FontFamily = new FontFamily(terminalSettings.FontFamily);
        }
        _terminal.LinkActivated += (_, url) => AppHost.Get<IFileOpener>().OpenUrl(url);
        _terminal.Exited += (_, code) => Exited?.Invoke(this, code);

        Width = 640;
        Height = 360;
        CornerRadius = new CornerRadius(6);
        ClipToBounds = true;
        BorderThickness = new Thickness(1);
        Child = _terminal;
    }

    public event EventHandler<int?>? Exited;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_started)
        {
            _started = true;
            _terminal.Start(_spec, _factory);
            Avalonia.Threading.Dispatcher.UIThread.Post(_terminal.FocusTerminal);
        }
    }

    public Task StopAsync() => _terminal.CloseAsync();
}
