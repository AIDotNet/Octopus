using System.Collections.ObjectModel;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Core.Projects;
using Octopus.Platform;
using TKCode.Arc.Localization;

namespace Octopus.App.Explorer.Tunnels;

/// <summary>
/// The tunnels tab of the right panel (design/11-SSH隧道.md §7): the tunnels of the current project when it is on an SSH
/// host, each with its switch. It follows the current project by itself; the panel only decides whether to show it.
/// </summary>
public sealed partial class TunnelsPanelViewModel : ObservableObject, IDisposable
{
    private readonly IProjectService _projects;
    private readonly ITunnelService _tunnels;
    private readonly IDialogService _dialogs;
    private readonly IFileOpener _opener;
    private readonly IWindowAccessor _windows;
    private readonly UiContext _ui = new();
    private Project? _project;

    public TunnelsPanelViewModel(IProjectService projects, ITunnelService tunnels, IDialogService dialogs, IFileOpener opener, IWindowAccessor windows)
    {
        _projects = projects;
        _tunnels = tunnels;
        _dialogs = dialogs;
        _opener = opener;
        _windows = windows;
        projects.Changed += OnProjectsChanged;
        tunnels.Changed += OnTunnelChanged;
        ArcLanguage.Changed += OnLanguageChanged;
        Reload();
    }

    public ObservableCollection<TunnelRowViewModel> Rows { get; } = [];

    /// <summary>The SSH host of the current project; null when the project is on this computer (the panel has no tab then).</summary>
    [ObservableProperty] public partial string? Host { get; private set; }

    /// <summary>How many tunnels of the current project run, for the badge on the tab.</summary>
    [ObservableProperty] public partial int RunningCount { get; private set; }

    public bool HasRows => Rows.Count > 0;
    public bool IsEmpty => Rows.Count == 0;
    public string EmptyHint => AppLangResources.Format(AppLangResourceKind.TunnelsEmptyHint, Host ?? "");

    /// <summary>The Plus button, and the one in the empty state.</summary>
    [RelayCommand]
    public async Task AddAsync()
    {
        if (_project is not { SshHost: { } host } project) return;
        var spec = await _dialogs.AskTunnelAsync(null, host);
        if (spec is null) return;
        _projects.AddTunnel(project, spec);
        // A tunnel that is made is wanted now.
        _tunnels.Start(project, spec);
    }

    [RelayCommand]
    private void StartAll()
    {
        if (_project is not { } project) return;
        foreach (var row in Rows) _tunnels.Start(project, row.Spec);
    }

    [RelayCommand]
    private void StopAll()
    {
        foreach (var row in Rows) _tunnels.Stop(row.Id);
    }

    internal void SetRunning(TunnelRowViewModel row, bool on)
    {
        if (_project is not { } project) return;
        if (on) _tunnels.Start(project, row.Spec);
        else _tunnels.Stop(row.Id);
    }

    internal void Open(TunnelRowViewModel row) => _opener.OpenUrl("http://" + row.Address);

    internal async Task CopyAddressAsync(TunnelRowViewModel row)
    {
        if (_windows.Window?.Clipboard is { } clipboard) await clipboard.SetTextAsync(row.Address);
    }

    internal async Task EditAsync(TunnelRowViewModel row)
    {
        if (_project is not { SshHost: { } host } project) return;
        // The dialog works on a copy: the tunnel that runs is replaced only when the edit is saved, and a running one then starts again.
        var edited = await _dialogs.AskTunnelAsync(row.Spec.Clone(), host);
        if (edited is not null) _projects.UpdateTunnel(project, edited);
    }

    internal async Task DeleteAsync(TunnelRowViewModel row)
    {
        if (_project is not { } project) return;
        var confirmed = await _dialogs.ConfirmAsync(
            AppLangResources.Get(AppLangResourceKind.TunnelsDeleteTitle),
            AppLangResources.Format(AppLangResourceKind.TunnelsDeleteDescription, row.Title),
            AppLangResources.Get(AppLangResourceKind.TunnelsDelete),
            isDanger: true);
        if (!confirmed) return;
        _tunnels.Stop(row.Id);
        _projects.RemoveTunnel(project, row.Id);
    }

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (e.Kind is ProjectChangeKind.CurrentProject or ProjectChangeKind.Projects or ProjectChangeKind.Tunnels) Reload();
    }

    private void OnTunnelChanged(object? sender, string id)
    {
        // Called on a background thread by the service.
        _ui.Post(UpdateCount);
    }

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e) => Reload();

    private void Reload()
    {
        foreach (var row in Rows) row.Dispose();
        Rows.Clear();
        _project = _projects.Current is { SshHost: not null } current ? current : null;
        Host = _project?.SshHost;
        if (_project is not null)
        {
            foreach (var spec in _project.Tunnels) Rows.Add(new TunnelRowViewModel(spec, _tunnels.RunOf(spec.Id), this));
        }
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyHint));
        UpdateCount();
    }

    private void UpdateCount() => RunningCount = Rows.Count(r => r.IsRunning);

    public void Dispose()
    {
        _projects.Changed -= OnProjectsChanged;
        _tunnels.Changed -= OnTunnelChanged;
        ArcLanguage.Changed -= OnLanguageChanged;
        foreach (var row in Rows) row.Dispose();
    }
}
