using Avalonia.Input;

namespace Octopus.App.Shell.Keymap;

/// <summary>Windows and Linux: Ctrl, with Shift where a plain Ctrl letter belongs to the terminal (Ctrl+T, Ctrl+W).</summary>
public sealed class DefaultAppKeymap : AppKeymapBase
{
    public DefaultAppKeymap()
    {
        const KeyModifiers ctrl = KeyModifiers.Control;
        Add(AppCommand.ToggleSidebar, Key.B, ctrl);
        Add(AppCommand.ToggleRightPanel, Key.B, ctrl | KeyModifiers.Alt);
        AddHint(AppCommand.FindInTerminal, Key.F, ctrl | KeyModifiers.Shift);
        Add(AppCommand.NewTerminal, Key.T, ctrl | KeyModifiers.Shift);
        Add(AppCommand.NewDefaultAgent, Key.T, ctrl | KeyModifiers.Alt);
        Add(AppCommand.CloseTab, Key.W, ctrl | KeyModifiers.Shift);
        Add(AppCommand.SplitRight, Key.OemBackslash, ctrl);
        Add(AppCommand.SplitRight, Key.OemPipe, ctrl);
        Add(AppCommand.OpenSettings, Key.OemComma, ctrl);
        Add(AppCommand.PreviousTab, Key.PageUp, ctrl);
        Add(AppCommand.NextTab, Key.PageDown, ctrl);
        for (var i = 1; i <= 9; i++)
        {
            Add(AppCommand.Tab1 + i - 1, Key.D0 + i, KeyModifiers.Alt);
        }
    }

    protected override string Format(KeyGesture gesture)
    {
        var parts = new List<string>();
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(KeyName(gesture.Key));
        return string.Join("+", parts);
    }

    protected override IReadOnlyList<string> Parts(KeyGesture gesture) => Format(gesture).Split('+');
}
