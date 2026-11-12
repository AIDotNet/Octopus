using Avalonia.Input;

namespace Octopus.App.Shell.Keymap;

/// <summary>The application shortcuts of this platform (design 03 section 5).</summary>
public interface IAppKeymap
{
    /// <summary>The command a key press stands for, or null when it is not an application shortcut.</summary>
    AppCommand? Match(KeyEventArgs e);

    /// <summary>The shortcut as text for a menu or a tooltip, such as "⌘T" or "Ctrl+Shift+T".</summary>
    string Hint(AppCommand command);

    /// <summary>The key combinations of a command as lists of key names, one list per distinct alternative, for a shortcuts list.</summary>
    IReadOnlyList<IReadOnlyList<string>> Combos(AppCommand command);
}
