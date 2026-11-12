using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Common;
using Octopus.App.Shell.Keymap;
using TKCode.Arc.Localization;

namespace Octopus.App.Settings.Shortcuts;

/// <summary>The shortcuts page: a read-only list for the current platform; its titles follow the language.</summary>
public sealed partial class ShortcutsViewModel : ObservableObject, IDisposable
{
    private readonly IAppKeymap _keymap;

    [ObservableProperty]
    private IReadOnlyList<ShortcutEntry> _entries;

    public ShortcutsViewModel(IAppKeymap keymap)
    {
        _keymap = keymap;
        _entries = ShortcutCatalog.For(keymap);
        ArcLanguage.Changed += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e) =>
        UiContext.Run(() => Entries = ShortcutCatalog.For(_keymap));

    public void Dispose() => ArcLanguage.Changed -= OnLanguageChanged;
}
