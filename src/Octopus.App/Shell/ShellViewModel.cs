using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Launcher;
using Octopus.App.Services;
using Octopus.App.Shell.Chrome;
using Octopus.App.Shell.Keymap;
using Octopus.App.Sidebar;
using Octopus.App.Workspace;
using Octopus.Storage;

namespace Octopus.App.Shell;

/// <summary>The main window: sidebar state, the settings overlay, and the keyboard shortcuts.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public const double RailWidth = 54;
    public const double MinPanelWidth = 200;
    public const double MaxPanelWidth = 420;
    public const double MinRightPanelWidth = 220;
    public const double MaxRightPanelWidth = 520;

    private readonly ISettingsStore _settings;
    private readonly IAppKeymap _keymap;
    private readonly IItemOpener _opener;
    private readonly IWorkspaceCommands _workspaceCommands;
    private readonly ISettingsNavigator _navigator;
    private readonly IProjectService _projects;

    public ShellViewModel(
        ISettingsStore settings,
        IWindowChrome chrome,
        IAppKeymap keymap,
        IItemOpener opener,
        IWorkspaceCommands workspaceCommands,
        IProjectService projects,
        SettingsNavigator navigator,
        ProjectRailViewModel rail,
        ProjectPanelViewModel panel,
        WorkspaceViewModel workspace,
        LauncherViewModel launcher,
        Octopus.App.Updates.UpdateCardViewModel updateCard)
    {
        _settings = settings;
        _keymap = keymap;
        _opener = opener;
        _workspaceCommands = workspaceCommands;
        _navigator = navigator;
        _projects = projects;
        projects.Changed += (_, e) =>
        {
            if (e.Kind is ProjectChangeKind.Projects or ProjectChangeKind.CurrentProject)
            {
                ReadProject();
            }
        };
        ReadProject();
        IsRightPanelOpen = settings.Current.RightPanel.IsOpen;
        RightPanelWidth = Math.Clamp(settings.Current.RightPanel.Width, MinRightPanelWidth, MaxRightPanelWidth);
        navigator.Requested += (_, section) => SettingsRequested?.Invoke(this, section);
        Rail = rail;
        Panel = panel;
        Workspace = workspace;
        Launcher = launcher;
        UpdateCard = updateCard;
        LeftInset = chrome.LeftInset;
        ShowsCaptionButtons = chrome.ShowsCaptionButtons;
        IsSidebarCollapsed = settings.Current.Sidebar.IsCollapsed;
        PanelWidth = Math.Clamp(settings.Current.Sidebar.Width, MinPanelWidth, MaxPanelWidth);
    }

    public ProjectRailViewModel Rail { get; }
    public ProjectPanelViewModel Panel { get; }
    public WorkspaceViewModel Workspace { get; }
    public LauncherViewModel Launcher { get; }
    /// <summary>The update card at the bottom right (design/10-自动更新.md §10).</summary>
    public Octopus.App.Updates.UpdateCardViewModel UpdateCard { get; }

    /// <summary>Room kept free at the top left for the system's window buttons.</summary>
    public double LeftInset { get; }
    public bool ShowsCaptionButtons { get; }

    [ObservableProperty] public partial bool IsSidebarCollapsed { get; set; }
    [ObservableProperty] public partial double PanelWidth { get; set; }

    [ObservableProperty] public partial bool IsRightPanelOpen { get; set; }
    [ObservableProperty] public partial double RightPanelWidth { get; set; }
    /// <summary>Folder and name of the current project, for the right panel; null without a project.</summary>
    [ObservableProperty] public partial string? CurrentProjectPath { get; private set; }

    /// <summary>The SSH host of the current project; null when its folder is on this computer.</summary>
    [ObservableProperty] public partial string? CurrentProjectSshHost { get; private set; }
    [ObservableProperty] public partial string? CurrentProjectName { get; private set; }

    public GridLength RightColumnWidth => IsRightPanelOpen ? new GridLength(RightPanelWidth) : new GridLength(0);
    public string ToggleRightPanelHint => _keymap.Hint(AppCommand.ToggleRightPanel);

    partial void OnIsRightPanelOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(RightColumnWidth));
        _settings.Update(s => s.RightPanel.IsOpen = value);
    }

    partial void OnRightPanelWidthChanged(double value) => OnPropertyChanged(nameof(RightColumnWidth));

    public void CommitRightPanelWidth(double width)
    {
        RightPanelWidth = Math.Clamp(width, MinRightPanelWidth, MaxRightPanelWidth);
        _settings.Update(s => s.RightPanel.Width = RightPanelWidth);
    }

    /// <summary>The right panel asked for a file: open it as a tab (an open one is activated).</summary>
    /// <summary>On an SSH host the file is copied here first (read-only); otherwise it is opened where it is.</summary>
    public void OpenFileFromPanel(string path)
    {
        if (CurrentProjectSshHost is not null) _ = _opener.OpenRemoteFileAsync(path);
        else _opener.OpenFile(path);
    }

    public void OpenDiffFromPanel(Octopus.App.Files.DiffTabRequest request) => _workspaceCommands.OpenDiff(request);

    private void ReadProject()
    {
        CurrentProjectPath = _projects.Current?.Path;
        CurrentProjectName = _projects.Current?.Name;
        CurrentProjectSshHost = _projects.Current?.SshHost;
    }

    /// <summary>
    /// The window is closing: asks once when programs still run (design 06 section 5.6), then saves the projects and ends
    /// every terminal. False when the person decided to keep the app open.
    /// </summary>
    public async Task<bool> ShutDownAsync()
    {
        if (!await Workspace.ConfirmQuitAsync())
        {
            return false;
        }
        try
        {
            await _projects.FlushAsync();
        }
        catch (Exception)
        {
            // A failed save must not keep the app open: the next start reads the last good file.
        }
        await Workspace.CloseAllAsync();
        return true;
    }

    [RelayCommand]
    private void ToggleRightPanel() => IsRightPanelOpen = !IsRightPanelOpen;

    public bool IsSidebarVisible => !IsSidebarCollapsed;

    /// <summary>Width of the whole left column; zero when the sidebar is collapsed, so it takes no room at all.</summary>
    public GridLength LeftColumnWidth => IsSidebarCollapsed ? new GridLength(0) : new GridLength(RailWidth + PanelWidth);

    public string ToggleSidebarHint => _keymap.Hint(AppCommand.ToggleSidebar);
    public string NewTerminalHint => _keymap.Hint(AppCommand.NewTerminal);
    public string SplitRightHint => _keymap.Hint(AppCommand.SplitRight);

    /// <summary>Raised when the settings page should open; the argument is the section key or null.</summary>
    public event EventHandler<string?>? SettingsRequested;

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSidebarVisible));
        OnPropertyChanged(nameof(LeftColumnWidth));
        _settings.Update(s => s.Sidebar.IsCollapsed = value);
    }

    partial void OnPanelWidthChanged(double value) => OnPropertyChanged(nameof(LeftColumnWidth));

    /// <summary>Stores the width the user dragged the panel to.</summary>
    public void CommitPanelWidth(double width)
    {
        PanelWidth = Math.Clamp(width, MinPanelWidth, MaxPanelWidth);
        _settings.Update(s => s.Sidebar.Width = PanelWidth);
    }

    public void OpenSettings(string? section = null) => _navigator.OpenSettings(section);

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    [RelayCommand]
    private void OpenSettingsPage() => OpenSettings();

    [RelayCommand]
    private void SplitRight() => _workspaceCommands.SplitRight();

    /// <summary>Runs the command of a shortcut.</summary>
    public void Execute(AppCommand command)
    {
        switch (command)
        {
            case AppCommand.ToggleSidebar: ToggleSidebar(); break;
            case AppCommand.ToggleRightPanel: ToggleRightPanel(); break;
            case AppCommand.NewTerminal: _opener.OpenTerminal(); break;
            case AppCommand.NewDefaultAgent: _opener.OpenDefaultAgent(); break;
            case AppCommand.CloseTab: _workspaceCommands.CloseActiveTab(); break;
            case AppCommand.PreviousTab: _workspaceCommands.CycleTab(-1); break;
            case AppCommand.NextTab: _workspaceCommands.CycleTab(1); break;
            case AppCommand.SplitRight: _workspaceCommands.SplitRight(); break;
            case AppCommand.OpenSettings: OpenSettings(); break;
            default:
                if (command >= AppCommand.Tab1 && command <= AppCommand.Tab9)
                {
                    _workspaceCommands.SelectTab(command - AppCommand.Tab1 + 1);
                }
                break;
        }
    }
}
