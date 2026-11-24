using TKCode.Terminal.Core;

namespace Octopus.Terminal.Avalonia;

/// <summary>
/// Ctrl+Shift+C/V/K/A/F and Shift+Insert, Ctrl-click follows links, for Windows and Linux. Plain Ctrl+C copies while
/// text is selected and goes to the program (an interrupt) when nothing is; a right click copies the selection or
/// pastes. With <paramref name="pasteWithoutShift"/> (Windows), plain Ctrl+V also pastes.
/// </summary>
public sealed class DefaultTerminalKeymap(bool pasteWithoutShift = false) : KeymapBase
{
    private const KeyMods Relevant = KeyMods.Shift | KeyMods.Ctrl | KeyMods.Alt | KeyMods.Super;

    /// <inheritdoc />
    protected override KeyMods Modifier => KeyMods.Ctrl | KeyMods.Shift;

    /// <inheritdoc />
    public override KeyMods LinkModifier => KeyMods.Ctrl;

    /// <inheritdoc />
    public override bool CopyWithoutShift => true;

    /// <inheritdoc />
    public override TerminalShortcut Match(in KeyInput key)
    {
        var mods = key.Mods & Relevant;
        if (key.Action == KeyAction.Press && key.Key == TerminalKey.C && mods == KeyMods.Ctrl)
            return TerminalShortcut.Copy;
        if (key.Action != KeyAction.Release && key.Key == TerminalKey.Insert && mods == KeyMods.Shift)
            return TerminalShortcut.Paste;
        if (pasteWithoutShift && key.Action != KeyAction.Release && key.Key == TerminalKey.V && mods == KeyMods.Ctrl)
            return TerminalShortcut.Paste;
        return base.Match(key);
    }
}
