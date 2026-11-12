using TKCode.Terminal.Core;

namespace Octopus.Terminal.Avalonia;

/// <summary>What an application shortcut asks the terminal to do.</summary>
public enum TerminalShortcut
{
    None,
    Copy,
    Paste,
    Clear,
    SelectAll,
    Find
}

/// <summary>Maps key presses to application shortcuts. Everything not matched goes to the running program.</summary>
public interface ITerminalKeymap
{
    /// <summary>The shortcut <paramref name="key"/> triggers, or <see cref="TerminalShortcut.None"/>.</summary>
    TerminalShortcut Match(in KeyInput key);

    /// <summary>The modifier that must be held to follow a link with the mouse (Command on macOS, Ctrl elsewhere).</summary>
    KeyMods LinkModifier { get; }
}
