namespace Octopus.App.Shell.Keymap;

public enum AppCommand
{
    ToggleSidebar,
    NewTerminal,
    NewDefaultAgent,
    CloseTab,
    PreviousTab,
    NextTab,
    SplitRight,
    OpenSettings,
    ToggleRightPanel,
    /// <summary>Handled by the terminal itself; listed so the shortcuts page can show it.</summary>
    FindInTerminal,
    Tab1, Tab2, Tab3, Tab4, Tab5, Tab6, Tab7, Tab8, Tab9,
}
