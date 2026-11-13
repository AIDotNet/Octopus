using TKCode.Terminal.Core;

namespace Octopus.Terminal.Avalonia;

/// <summary>
/// Ctrl+Shift+C/V/K/A/F, Ctrl-click follows links, for Windows and Linux. Plain Ctrl+C copies while text is selected
/// and goes to the program (an interrupt) when nothing is; a right click copies the selection or pastes.
/// </summary>
public sealed class DefaultTerminalKeymap : KeymapBase
{
    /// <inheritdoc />
    protected override KeyMods Modifier => KeyMods.Ctrl | KeyMods.Shift;

    /// <inheritdoc />
    public override KeyMods LinkModifier => KeyMods.Ctrl;

    /// <inheritdoc />
    public bool CopyWithoutShift => true;

    /// <inheritdoc />
    public override TerminalShortcut Match(in KeyInput key) =>
        key.Action == KeyAction.Press && key.Key == TerminalKey.C && (key.Mods & (KeyMods.Shift | KeyMods.Ctrl | KeyMods.Alt | KeyMods.Super)) == KeyMods.Ctrl
            ? TerminalShortcut.Copy
            : base.Match(key);
}
