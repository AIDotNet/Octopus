using TKCode.Terminal.Core;

namespace Octopus.Terminal.Avalonia;

/// <summary>Command+C/V/K/A/F; Command-click follows links. Ctrl+letter stays with the program.</summary>
public sealed class MacTerminalKeymap : KeymapBase
{
    /// <inheritdoc />
    protected override KeyMods Modifier => KeyMods.Super;

    /// <inheritdoc />
    public override KeyMods LinkModifier => KeyMods.Super;
}
