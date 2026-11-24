using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Octopus.App.Localization;
using Octopus.App.Settings.About;
using Octopus.App.Settings.Agents;
using Octopus.App.Settings.Appearance;
using Octopus.App.Settings.General;
using Octopus.App.Settings.Shortcuts;
using Octopus.App.Settings.Ssh;
using Octopus.App.Settings.Terminal;
using Octopus.App.Settings.Workspaces;
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
            new("workspace-cleanup", LucideIconKind.FolderGit2, AppLangResourceKind.SettingsNavWorkspaces, () => ActivatorUtilities.CreateInstance<WorkspaceCleanupSettingsViewModel>(services)),
            new("terminal", LucideIconKind.SquareTerminal, AppLangResourceKind.SettingsNavTerminal, () => ActivatorUtilities.CreateInstance<TerminalSettingsViewModel>(services)),
            new("ssh", LucideIconKind.Server, AppLangResourceKind.SettingsNavSsh, () => ActivatorUtilities.CreateInstance<SshSettingsViewModel>(services)),
            new("account-devices", LucideIconKind.User, AppLangResourceKind.SettingsNavAccountDevices, () => ActivatorUtilities.CreateInstance<Remote.AccountDevicesSettingsViewModel>(services)),
            new("remote", LucideIconKind.MonitorSmartphone, AppLangResourceKind.SettingsNavRemote, () => ActivatorUtilities.CreateInstance<Remote.RemoteAccessSettingsViewModel>(services)),
            new("remote-hosts", LucideIconKind.Monitor, AppLangResourceKind.SettingsNavRemoteHosts, () => ActivatorUtilities.CreateInstance<Remote.RemoteHostsSettingsViewModel>(services)),
        ];
        Interface =
        [
            new("appearance", LucideIconKind.Palette, AppLangResourceKind.SettingsNavAppearance, () => ActivatorUtilities.CreateInstance<AppearanceSettingsViewModel>(services)),
            new("shortcuts", LucideIconKind.Keyboard, AppLangResourceKind.SettingsNavShortcuts, () => ActivatorUtilities.CreateInstance<ShortcutsViewModel>(services)),
            new("plugins", LucideIconKind.Puzzle, () => Plugins.PluginText.Get("插件", "Plugins"), () => ActivatorUtilities.CreateInstance<Plugins.Settings.PluginsSettingsViewModel>(services)),
        ];
        About =
        [
            new("about", LucideIconKind.Info, AppLangResourceKind.SettingsNavAbout, () => ActivatorUtilities.CreateInstance<AboutSettingsViewModel>(services)),
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

    public IReadOnlyList<SettingsSectionViewModel> About { get; }

    public IEnumerable<SettingsSectionViewModel> All => Ai.Concat(General).Concat(Interface).Concat(About);

    /// <summary>The navigation groups in order with their headings: the sections of the settings sidebar.</summary>
    public IReadOnlyList<(AppLangResourceKind Heading, IReadOnlyList<SettingsSectionViewModel> Sections)> Groups =>
    [
        (AppLangResourceKind.SettingsGroupAi, Ai),
        (AppLangResourceKind.SettingsGroupGeneral, General),
        (AppLangResourceKind.SettingsGroupInterface, Interface),
        (AppLangResourceKind.SettingsGroupAbout, About),
    ];

    /// <summary>The open section's key, for the sidebar's selection; setting it opens that section.</summary>
    public string SelectedKey
    {
        get => Selected.Key;
        set
        {
            if (value != Selected.Key) Show(value);
        }
    }

    /// <summary>The open page's view model (the view picks the template by its type).</summary>
    public object Page => Selected.Page;

    /// <summary>Opens the section with this key; an unknown key opens the agents page.</summary>
    public void Show(string? key)
    {
        var resolved = key == "workspaces" ? "workspace-cleanup" : key;
        Selected = All.FirstOrDefault(s => s.Key == resolved) ?? Ai[0];
    }

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
        OnPropertyChanged(nameof(SelectedKey));
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
                case WorkspaceCleanupSettingsViewModel workspaces:
                    workspaces.FlushPending();
                    break;
                case TerminalSettingsViewModel terminal:
                    terminal.FlushPending();
                    break;
            }
            section.DisposePage();
        }
    }
}
