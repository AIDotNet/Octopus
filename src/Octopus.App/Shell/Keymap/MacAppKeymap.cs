using Avalonia.Input;

namespace Octopus.App.Shell.Keymap;

/// <summary>macOS: the command key (Meta in Avalonia).</summary>
public sealed class MacAppKeymap : AppKeymapBase
{
    public MacAppKeymap()
    {
        const KeyModifiers cmd = KeyModifiers.Meta;
        Add(AppCommand.ToggleSidebar, Key.B, cmd);
        Add(AppCommand.ToggleRightPanel, Key.B, cmd | KeyModifiers.Alt);
        AddHint(AppCommand.FindInTerminal, Key.F, cmd);
        Add(AppCommand.NewTerminal, Key.T, cmd);
        Add(AppCommand.NewDefaultAgent, Key.T, cmd | KeyModifiers.Alt);
        Add(AppCommand.CloseTab, Key.W, cmd);
        Add(AppCommand.SplitRight, Key.OemBackslash, cmd);
        Add(AppCommand.SplitRight, Key.OemPipe, cmd);
        Add(AppCommand.OpenSettings, Key.OemComma, cmd);
        Add(AppCommand.PreviousTab, Key.OemOpenBrackets, cmd | KeyModifiers.Shift);
        Add(AppCommand.NextTab, Key.OemCloseBrackets, cmd | KeyModifiers.Shift);
        for (var i = 1; i <= 9; i++)
        {
            Add(AppCommand.Tab1 + i - 1, Key.D0 + i, cmd);
        }
    }

    protected override string Format(KeyGesture gesture)
    {
        var text = "";
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) text += "⌃";
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt)) text += "⌥";
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift)) text += "⇧";
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta)) text += "⌘";
        return text + KeyName(gesture.Key).ToUpperInvariant();
    }

    protected override IReadOnlyList<string> Parts(KeyGesture gesture)
    {
        var parts = new List<string>();
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("⌃");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("⌘");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("⌥");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("⇧");
        parts.Add(KeyName(gesture.Key).ToUpperInvariant());
        return parts;
    }
}
