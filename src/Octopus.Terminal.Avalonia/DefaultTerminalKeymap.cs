using TKCode.Terminal.Core;

namespace Octopus.Terminal.Avalonia;

/// <summary>Ctrl+Shift+C/V/K/A/F, Ctrl-click follows links, for Windows and Linux. Plain Ctrl+C stays with the program.</summary>
public sealed class DefaultTerminalKeymap : KeymapBase
{
    /// <inheritdoc />
    protected override KeyMods Modifier => KeyMods.Ctrl | KeyMods.Shift;

    /// <inheritdoc />
    public override KeyMods LinkModifier => KeyMods.Ctrl;
}
