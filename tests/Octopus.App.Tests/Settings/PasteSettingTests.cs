using Avalonia.Headless.XUnit;
using Octopus.App.Composition;
using Octopus.App.Settings.Terminal;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests.Settings;

public class PasteSettingTests
{
    [AvaloniaFact]
    public void Paste_confirmation_is_on_by_default_and_can_be_switched_off()
    {
        var settings = AppHost.Get<ISettingsStore>();
        var terminal = AppHost.Get<TerminalSettingsViewModel>();
        Assert.True(settings.Current.Terminal.ConfirmMultilinePaste);
        Assert.True(terminal.ConfirmMultilinePaste);

        terminal.ConfirmMultilinePaste = false;
        Assert.False(settings.Current.Terminal.ConfirmMultilinePaste);
    }
}
