using TKCode.Terminal.Core;

namespace Octopus.Terminal.Avalonia;

/// <summary>Shared matching: one modifier combination plus the C/V/K/A/F keys.</summary>
public abstract class KeymapBase : ITerminalKeymap
{
    private const KeyMods Relevant = KeyMods.Shift | KeyMods.Ctrl | KeyMods.Alt | KeyMods.Super;

    /// <summary>The exact modifiers that turn C, V, K and A into shortcuts.</summary>
    protected abstract KeyMods Modifier { get; }

    /// <inheritdoc />
    public abstract KeyMods LinkModifier { get; }

    /// <inheritdoc />
    public TerminalShortcut Match(in KeyInput key)
    {
        if (key.Action == KeyAction.Release || (key.Mods & Relevant) != Modifier) return TerminalShortcut.None;
        return key.Key switch
        {
            TerminalKey.C => TerminalShortcut.Copy,
            TerminalKey.V => TerminalShortcut.Paste,
            TerminalKey.K => TerminalShortcut.Clear,
            TerminalKey.A => TerminalShortcut.SelectAll,
            TerminalKey.F => TerminalShortcut.Find,
            _ => TerminalShortcut.None,
        };
    }
}
