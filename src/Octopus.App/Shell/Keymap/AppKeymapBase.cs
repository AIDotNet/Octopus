using Avalonia.Input;

namespace Octopus.App.Shell.Keymap;

/// <summary>A keymap is a table of gestures; the two platforms only differ in the table and in how they print it.</summary>
public abstract class AppKeymapBase : IAppKeymap
{
    private readonly List<(KeyGesture Gesture, AppCommand Command)> _entries = [];

    private readonly List<(KeyGesture Gesture, AppCommand Command)> _hints = [];

    /// <summary>
    /// A shortcut that something other than the window handles (the terminal's own Find): shown by <see cref="Hint"/>
    /// and <see cref="Combos"/>, never matched, so the window does not swallow the key before the terminal sees it.
    /// </summary>
    protected void AddHint(AppCommand command, Key key, KeyModifiers modifiers) =>
        _hints.Add((new KeyGesture(key, modifiers), command));

    protected void Add(AppCommand command, Key key, KeyModifiers modifiers) =>
        _entries.Add((new KeyGesture(key, modifiers), command));

    public AppCommand? Match(KeyEventArgs e)
    {
        foreach (var (gesture, command) in _entries)
        {
            if (gesture.Matches(e))
            {
                return command;
            }
        }
        return null;
    }

    public string Hint(AppCommand command)
    {
        foreach (var (gesture, entry) in _entries.Concat(_hints))
        {
            if (entry == command)
            {
                return Format(gesture);
            }
        }
        return "";
    }

    public IReadOnlyList<IReadOnlyList<string>> Combos(AppCommand command)
    {
        var combos = new List<IReadOnlyList<string>>();
        var seen = new HashSet<string>();
        foreach (var (gesture, entry) in _entries.Concat(_hints))
        {
            if (entry != command)
            {
                continue;
            }
            var parts = Parts(gesture);
            if (seen.Add(string.Join('\0', parts)))
            {
                combos.Add(parts);
            }
        }
        return combos;
    }

    protected abstract string Format(KeyGesture gesture);

    /// <summary>The gesture as separate key names, in the order the shortcuts list shows them.</summary>
    protected abstract IReadOnlyList<string> Parts(KeyGesture gesture);

    protected static string KeyName(Key key) => key switch
    {
        Key.OemComma => ",",
        Key.OemBackslash or Key.OemPipe => "\\",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        _ => key.ToString(),
    };
}
