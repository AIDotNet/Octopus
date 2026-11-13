using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.Core.Projects;
using Octopus.Core.Terminals;
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

    public TerminalTabViewModel(
        Project project,
        ProjectItem item,
        IItemLaunchService launcher,
        ITerminalConnectionFactory factory,
        ISettingsStore settings)
    {
        _project = project;
        Item = item;
        _launcher = launcher;
        Factory = factory;
        _settings = settings;
        // A terminal on an SSH host shows how its connection is made, from the log ssh writes (see RemoteFiles). Only ssh
        // writes it: a host that connects with mosh or telnet shows its terminal at once.
        Connect = project.SshHost is { } host && ProtocolOf(host) == SshHostProtocol.Ssh
            ? new SshConnectProgress(host, AppHost.Get<RemoteFiles>().ConnectLogPath(item.Id))
            : null;
        ReadSettings();
        _settings.Changed += OnSettingsChanged;
    }

    private static SshHostProtocol ProtocolOf(string alias) =>
        AppHost.Get<ISshHostCatalog>().Current.Hosts.FirstOrDefault(h => h.Alias == alias)?.Protocol ?? SshHostProtocol.Ssh;

    public ProjectItem Item { get; }

    /// <summary>The connection of a terminal on an SSH host; null for a terminal on this computer.</summary>
    public SshConnectProgress? Connect { get; }
    public ITerminalConnectionFactory Factory { get; }

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
