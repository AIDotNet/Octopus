namespace Octopus.Terminal.Avalonia;

/// <summary>Picks the keymap that matches the platform's conventions.</summary>
public static class TerminalKeymaps
{
    /// <summary>The only operating-system check in this library.</summary>
    public static ITerminalKeymap ForCurrentPlatform() =>
        OperatingSystem.IsMacOS() ? new MacTerminalKeymap() : new DefaultTerminalKeymap();
}
