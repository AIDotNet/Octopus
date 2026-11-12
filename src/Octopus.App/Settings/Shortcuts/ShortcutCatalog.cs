using Octopus.App.Localization;
using Octopus.App.Shell.Keymap;

namespace Octopus.App.Settings.Shortcuts;

/// <summary>One shortcut: what it does and the key combinations that trigger it (alternatives), each as a list of keys.</summary>
public sealed record ShortcutEntry(string Title, IReadOnlyList<IReadOnlyList<string>> Combos);

/// <summary>The shortcuts of the app (design/03 §5), read from the platform's keymap.</summary>
public static class ShortcutCatalog
{
    public static IReadOnlyList<ShortcutEntry> For(IAppKeymap keymap) =>
    [
        Entry(AppLangResourceKind.SettingsKeyToggleSidebar, keymap.Combos(AppCommand.ToggleSidebar)),
        Entry(AppLangResourceKind.SettingsKeyToggleRightPanel, keymap.Combos(AppCommand.ToggleRightPanel)),
        Entry(AppLangResourceKind.SettingsKeySplitRight, keymap.Combos(AppCommand.SplitRight)),
        Entry(AppLangResourceKind.SettingsKeyNewTerminal, keymap.Combos(AppCommand.NewTerminal)),
        Entry(AppLangResourceKind.SettingsKeyNewDefaultAgent, keymap.Combos(AppCommand.NewDefaultAgent)),
        Entry(AppLangResourceKind.SettingsKeyCloseTab, keymap.Combos(AppCommand.CloseTab)),
        Entry(AppLangResourceKind.SettingsKeyGotoTab, GotoTab(keymap)),
        Entry(AppLangResourceKind.SettingsKeyPrevNextTab, [.. keymap.Combos(AppCommand.PreviousTab), .. keymap.Combos(AppCommand.NextTab)]),
        Entry(AppLangResourceKind.SettingsKeyFindInTerminal, keymap.Combos(AppCommand.FindInTerminal)),
        Entry(AppLangResourceKind.SettingsKeyOpenSettings, keymap.Combos(AppCommand.OpenSettings)),
    ];

    /// <summary>Tab 1 to 9 share modifiers: shown once, as the first tab's keys with the last one widened to "1–9".</summary>
    private static IReadOnlyList<IReadOnlyList<string>> GotoTab(IAppKeymap keymap) =>
        [.. keymap.Combos(AppCommand.Tab1).Select(c => (IReadOnlyList<string>)[.. c.SkipLast(1), "1–9"])];

    private static ShortcutEntry Entry(AppLangResourceKind title, IReadOnlyList<IReadOnlyList<string>> combos) =>
        new(AppLangResources.Get(title), combos);
}
