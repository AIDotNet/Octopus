using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents.Signals;
using Octopus.App.Common;
using Octopus.Platform;

namespace Octopus.App.Plugins.Settings;

/// <summary>
/// "Claude 接入" on the agent usage plugin's row: the Claude Code configurations on this machine and, for each, whether
/// the status line bridge (<see cref="ClaudeStatusLineBridge"/>) is installed. Installing is the user's choice, one
/// configuration at a time or all at once, and can be undone exactly.
/// </summary>
public sealed partial class ClaudeConnectionsViewModel : ObservableObject
{
    private readonly ClaudeStatusLineBridge _bridge;
    private readonly ClaudeStatusLineWatcher _watcher;
    private readonly ILoginEnvironment _login;
    private readonly string _home;

    public ClaudeConnectionsViewModel(ClaudeStatusLineBridge bridge, ClaudeStatusLineWatcher watcher, ILoginEnvironment login, Octopus.Plugins.Security.UserHome home)
    {
        _bridge = bridge;
        _watcher = watcher;
        _login = login;
        _home = home.Path;
        _ = RefreshAsync();
    }

    public ObservableCollection<ClaudeConnectionRow> Directories { get; } = [];

    [ObservableProperty] public partial bool IsScanning { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }
    public bool HasMessage => Message is not null;

    public bool HasNone => !IsScanning && Directories.Count == 0;
    public bool CanInstallAll => Directories.Any(d => !d.IsInstalled);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsScanning = true;
        OnPropertyChanged(nameof(HasNone));
        try
        {
            var environment = await _login.GetAsync();
            var home = _home;
            // The login environment's CLAUDE_CONFIG_DIR counts only for the real home (tests point the home elsewhere).
            var variable = home == Octopus.Plugins.Security.UserHome.Current.Path ? environment.GetValueOrDefault("CLAUDE_CONFIG_DIR") : null;
            var found = await Task.Run(() =>
            {
                var dirs = ClaudeConfigDirectories.Find(home, variable)
                    .Concat(_bridge.InstalledDirectories())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                return dirs.Select(d => (Dir: d, Account: ClaudeConfigDirectories.AccountOf(d, home), Installed: _bridge.IsInstalled(d), Report: _bridge.Read(d)?.At)).ToList();
            });
            Directories.Clear();
            foreach (var (dir, account, installed, report) in found.OrderByDescending(f => f.Installed).ThenBy(f => f.Dir, StringComparer.Ordinal))
            {
                Directories.Add(new ClaudeConnectionRow(this, dir, account, installed, report, home));
            }
        }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(HasNone));
            OnPropertyChanged(nameof(CanInstallAll));
        }
    }

    [RelayCommand]
    private async Task InstallAllAsync()
    {
        foreach (var row in Directories.Where(d => !d.IsInstalled).ToList()) await InstallAsync(row);
    }

    internal async Task InstallAsync(ClaudeConnectionRow row)
    {
        try
        {
            await Task.Run(() => _bridge.Install(row.Directory));
            _watcher.Start();
            _watcher.Publish(row.Directory);
            row.Update(installed: true, _bridge.Read(row.Directory)?.At);
            Message = PluginText.Get(
                "已接入。在这个配置的 Claude Code 里发一条消息后，用量就会出现。",
                "Connected. Usage appears after the next message in Claude Code with this configuration.");
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Message = e.Message;
        }
        OnPropertyChanged(nameof(CanInstallAll));
    }

    internal async Task RestoreAsync(ClaudeConnectionRow row)
    {
        try
        {
            await Task.Run(() => _bridge.Restore(row.Directory));
            row.Update(installed: false, null);
            Message = PluginText.Get("已恢复原来的状态行。", "The previous status line is back.");
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Message = e.Message;
        }
        OnPropertyChanged(nameof(CanInstallAll));
    }
}

/// <summary>One Claude Code configuration directory.</summary>
public sealed partial class ClaudeConnectionRow : ObservableObject
{
    private readonly ClaudeConnectionsViewModel _owner;

    public ClaudeConnectionRow(ClaudeConnectionsViewModel owner, string directory, string? account, bool installed, DateTimeOffset? reported, string home)
    {
        _owner = owner;
        Directory = directory;
        DisplayPath = directory.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~" + directory[home.Length..] : directory;
        Title = account ?? PluginText.Get("未登录的配置", "Not signed in");
        Update(installed, reported);
    }

    public string Directory { get; }
    /// <summary>The directory with the home folder written as <c>~</c>.</summary>
    public string DisplayPath { get; }
    public string Title { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotInstalled))]
    public partial bool IsInstalled { get; private set; }
    public bool IsNotInstalled => !IsInstalled;

    [ObservableProperty] public partial string State { get; private set; } = "";
    [ObservableProperty] public partial TKCode.Arc.BadgeTone StateTone { get; private set; }

    internal void Update(bool installed, DateTimeOffset? reported)
    {
        IsInstalled = installed;
        (State, StateTone) = (installed, reported) switch
        {
            (false, _) => (PluginText.Get("未接入", "Not connected"), TKCode.Arc.BadgeTone.Neutral),
            (true, null) => (PluginText.Get("已接入，等待数据", "Connected, waiting"), TKCode.Arc.BadgeTone.Info),
            (true, { } at) => (PluginText.Get("已接入 · ", "Connected · ") + RelativeTime.Format(at.ToLocalTime(), DateTimeOffset.Now), TKCode.Arc.BadgeTone.Success),
        };
    }

    [RelayCommand] private Task InstallAsync() => _owner.InstallAsync(this);
    [RelayCommand] private Task RestoreAsync() => _owner.RestoreAsync(this);
}
