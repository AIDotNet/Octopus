using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Octopus.App.Localization;
using Octopus.App.Settings.Agents;
using Octopus.App.Settings.Appearance;
using Octopus.App.Settings.General;
using Octopus.App.Settings.Shortcuts;
using Octopus.App.Settings.Terminal;
using TKCode.Arc.Localization;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Settings;

/// <summary>The settings shell: the navigation groups and the page that is open.</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private SettingsSectionViewModel _selected;

    public SettingsViewModel(IServiceProvider services)
    {
        Ai = [new("agents", LucideIconKind.Bot, AppLangResourceKind.SettingsNavAgents, () => ActivatorUtilities.CreateInstance<AgentsSettingsViewModel>(services))];
        General =
        [
            new("general", LucideIconKind.SlidersHorizontal, AppLangResourceKind.SettingsNavGeneral, () => ActivatorUtilities.CreateInstance<GeneralSettingsViewModel>(services)),
            new("terminal", LucideIconKind.SquareTerminal, AppLangResourceKind.SettingsNavTerminal, () => ActivatorUtilities.CreateInstance<TerminalSettingsViewModel>(services)),
        ];
        Interface =
        [
            new("appearance", LucideIconKind.Palette, AppLangResourceKind.SettingsNavAppearance, () => ActivatorUtilities.CreateInstance<AppearanceSettingsViewModel>(services)),
            new("shortcuts", LucideIconKind.Keyboard, AppLangResourceKind.SettingsNavShortcuts, () => ActivatorUtilities.CreateInstance<ShortcutsViewModel>(services)),
        ];
        foreach (var section in All)
        {
            section.SelectCommand = SelectCommand;
        }
        _selected = Ai[0];
        _selected.IsSelected = true;
        ArcLanguage.Changed += OnLanguageChanged;
    }

    public IReadOnlyList<SettingsSectionViewModel> Ai { get; }

    public IReadOnlyList<SettingsSectionViewModel> General { get; }

    public IReadOnlyList<SettingsSectionViewModel> Interface { get; }

    public IEnumerable<SettingsSectionViewModel> All => Ai.Concat(General).Concat(Interface);

    /// <summary>The open page's view model (the view picks the template by its type).</summary>
    public object Page => Selected.Page;

    /// <summary>Opens the section with this key; an unknown key opens the agents page.</summary>
    public void Show(string? key) => Selected = All.FirstOrDefault(s => s.Key == key) ?? Ai[0];

    [RelayCommand]
    private void Select(SettingsSectionViewModel? section)
    {
        if (section is not null)
        {
            Selected = section;
        }
    }

    partial void OnSelectedChanged(SettingsSectionViewModel? oldValue, SettingsSectionViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }
        newValue.IsSelected = true;
        OnPropertyChanged(nameof(Page));
    }

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e)
    {
        foreach (var section in All)
        {
            section.RefreshTitle();
        }
    }

    public void Dispose()
    {
        ArcLanguage.Changed -= OnLanguageChanged;
        foreach (var section in All.Where(s => s.IsPageCreated))
        {
            switch (section.Page)
            {
                case AgentsSettingsViewModel agents:
                    agents.FlushPending();
                    break;
                case TerminalSettingsViewModel terminal:
                    terminal.FlushPending();
                    break;
            }
            section.DisposePage();
        }
    }
}
