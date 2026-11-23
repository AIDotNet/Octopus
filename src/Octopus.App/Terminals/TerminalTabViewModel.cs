using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.Core.Projects;
using Octopus.Core.Terminals;
using Octopus.Platform;
using Octopus.Ssh;
using Octopus.Storage;
using Octopus.Terminal;

namespace Octopus.App.Terminals;

/// <summary>A terminal or agent tab: what to run, and the terminal settings that apply to it.</summary>
public sealed partial class TerminalTabViewModel : ObservableObject, IDisposable
{
    private readonly Project _project;
    private readonly IItemLaunchService _launcher;
    private readonly ISettingsStore _settings;
    private readonly IFileOpener? _fileOpener;

    public TerminalTabViewModel(
        Project project,
        ProjectItem item,
        IItemLaunchService launcher,
        ITerminalConnectionFactory factory,
        ISettingsStore settings,
        Octopus.Terminal.Avalonia.TerminalSessionRegistry? sessions = null,
        IFileOpener? fileOpener = null,
        string? homeDirectory = null)
    {
        Sessions = sessions;
        _project = project;
        Item = item;
        _launcher = launcher;
        Factory = factory;
        _settings = settings;
        _fileOpener = fileOpener;
        // A terminal on an SSH host shows how its connection is made, from the log ssh writes (see RemoteFiles). Only ssh
        // writes it: a host that connects with mosh or telnet shows its terminal at once.
        Connect = project.SshHost is { } host && ProtocolOf(host) == SshHostProtocol.Ssh
            ? new SshConnectProgress(host, AppHost.Get<RemoteFiles>().ConnectLogPath(item.Id))
            : null;
        Workspace = !string.IsNullOrEmpty(item.WorkspaceId)
            ? project.Workspaces.FirstOrDefault(w => w.Id == item.WorkspaceId)
            : null;
        WorkspaceShortPath = Workspace is not null ? WorkspacePaths.FormatHomePath(Workspace.Path, homeDirectory) : "";
        ReadSettings();
        _settings.Changed += OnSettingsChanged;
    }

    public ProjectWorkspace? Workspace { get; }
    public bool HasWorkspaceContext => Workspace is not null;
    public string WorkspaceBranch => Workspace?.DisplayName ?? "";
    public string WorkspaceShortPath { get; }
    public bool HasTask => Workspace?.Task is not null && (!string.IsNullOrEmpty(Workspace.Task.Key) || !string.IsNullOrEmpty(Workspace.Task.Title));
    public string TaskKey => Workspace?.Task?.Key ?? "";
    public string TaskTitle => Workspace?.Task?.Title ?? "";
    public string? TaskUrl => Workspace?.Task?.Url;
    public bool HasTaskUrl => !string.IsNullOrWhiteSpace(TaskUrl);

    [RelayCommand]
    private void OpenTaskUrl()
    {
        if (!string.IsNullOrWhiteSpace(TaskUrl))
        {
            _fileOpener?.OpenUrl(TaskUrl);
        }
    }

    [RelayCommand]
    private void RevealWorkspace()
    {
        if (Workspace is not null)
        {
            _fileOpener?.Reveal(Workspace.Path);
        }
    }

    private static SshHostProtocol ProtocolOf(string alias) =>
        AppHost.Get<ISshHostCatalog>().Current.Hosts.FirstOrDefault(h => h.Alias == alias)?.Protocol ?? SshHostProtocol.Ssh;

    public ProjectItem Item { get; }

    /// <summary>The connection of a terminal on an SSH host; null for a terminal on this computer.</summary>
    public SshConnectProgress? Connect { get; }
    public ITerminalConnectionFactory Factory { get; }

    /// <summary>The tab shows a terminal that another computer runs (a project on a remote Octopus host).</summary>
    public bool IsMirror { get; init; }

    /// <summary>
    /// Where the tab's session is kept, under the item's id: the session then belongs to the item, not to the tab's
    /// control, and one that already runs is shown instead of being started again. Null: the control keeps its own.
    /// </summary>
    public Octopus.Terminal.Avalonia.TerminalSessionRegistry? Sessions { get; }

    /// <summary>The process starts when the tab is shown for the first time, not when it is restored.</summary>
    public bool Started { get; set; }

    [ObservableProperty] public partial string? TerminalFontFamily { get; set; }
    [ObservableProperty] public partial double TerminalFontSize { get; set; }
    [ObservableProperty] public partial int ScrollbackLines { get; set; }
    [ObservableProperty] public partial bool ConfirmMultilinePaste { get; set; }
    /// <summary>Set when the command could not be built (for example the agent is unknown).</summary>
    [ObservableProperty] public partial string? Error { get; set; }

    /// <summary>True when the last spec from <see cref="TryCreateSpec"/> continues the agent's previous session.</summary>
    public bool Resumed { get; private set; }

    public LaunchSpec? TryCreateSpec(bool allowResume = true)
    {
        try
        {
            Error = null;
            Resumed = allowResume && _launcher.Resumes(Item);
            return _launcher.Plan(_project, Item, allowResume);
        }
        catch (Exception e)
        {
            Error = e.Message;
            return null;
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        Connect?.Dispose();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(ReadSettings);

    private void ReadSettings()
    {
        var terminal = _settings.Current.Terminal;
        TerminalFontFamily = string.IsNullOrWhiteSpace(terminal.FontFamily) ? null : terminal.FontFamily;
        TerminalFontSize = terminal.FontSize;
        ScrollbackLines = terminal.ScrollbackLines;
        ConfirmMultilinePaste = terminal.ConfirmMultilinePaste;
    }
}
