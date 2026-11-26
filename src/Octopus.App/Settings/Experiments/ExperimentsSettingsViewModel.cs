using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents;
using Octopus.App.Common;
using Octopus.App.Files.Lsp;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Storage;
using Octopus.Terminal;

namespace Octopus.App.Settings.Experiments;

/// <summary>One row in the detected local language servers list, with a one-click install when a tool for it exists.</summary>
public sealed partial class LanguageServerRow : ObservableObject
{
    private readonly Func<LanguageServerRow, Task>? _install;

    public LanguageServerRow(DetectedLanguageServer detected, Func<LanguageServerRow, Task>? install = null)
    {
        Detected = detected;
        _install = install;
    }

    public DetectedLanguageServer Detected { get; }

    public string Id => Detected.Spec.Id;

    public string Name => Detected.Spec.DisplayName;

    public string Executable => Detected.Spec.Executable;

    public string? Path => Detected.ExecutablePath;

    public bool IsInstalled => Detected.IsInstalled;

    public string StatusText => IsInstalled
        ? AppLangResources.Get(AppLangResourceKind.SettingsExpLspDetected)
        : AppLangResources.Get(AppLangResourceKind.SettingsExpLspNotInstalled);

    public string DetailText => Path ?? Executable;

    /// <summary>How Octopus would install it here; found after the row is shown, null while unknown or when nothing fits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallCommandLine), nameof(InstallTooltip), nameof(CanInstall), nameof(ShowInstallButton),
        nameof(ShowManualHint), nameof(InstallButtonText))]
    public partial AgentInstallPlan? InstallPlan { get; set; }

    /// <summary>Whether <see cref="InstallPlan"/> has been looked for; the manual hint waits for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowManualHint))]
    public partial bool IsPlanResolved { get; set; }

    /// <summary>The command the install button runs, e.g. <c>npm install -g pyright</c>.</summary>
    public string? InstallCommandLine => InstallPlan?.CommandLine;

    public bool CanInstall => !IsInstalled && InstallPlan is not null && _install is not null;

    /// <summary>Not installed and nothing on this machine can install it: show what to install by hand instead.</summary>
    public bool ShowManualHint => !IsInstalled && IsPlanResolved && InstallPlan is null && ManualHint is not null;

    public string? ManualHint => Detected.ApplicableRecipes.FirstOrDefault() is { } recipe
        ? AppLangResources.Format(AppLangResourceKind.SettingsExpLspManualHint, recipe.Tool, recipe.CommandLine)
        : null;

    public string InstallTooltip => InstallCommandLine ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstallButton))]
    public partial bool IsInstalling { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>The last install failed for lack of rights: the button installs as administrator (sudo / UAC).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    public partial bool NeedsAdmin { get; set; }

    public string InstallButtonText => AppLangResources.Get(
        NeedsAdmin || InstallPlan?.Elevation == InstallElevation.Required
            ? AppLangResourceKind.SettingsInstallAsAdmin
            : AppLangResourceKind.SettingsExpLspInstall);

    public bool ShowInstallButton => CanInstall && !IsInstalling;

    [RelayCommand]
    private Task InstallAsync() => CanInstall && !IsInstalling ? _install!(this) : Task.CompletedTask;
}

/// <summary>
/// The Experiments settings page: opt-in toggles for Monaco-based code editing, interactive diff editing, and local
/// Language Server Protocol (LSP) integration, plus a detector showing which language servers are installed on PATH.
/// </summary>
public sealed partial class ExperimentsSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly ILanguageServerManager _lsp;
    private readonly IAgentInstaller? _installer;
    private readonly IDialogService? _dialogs;
    private readonly ITerminalLaunchPlanner? _launchPlanner;
    /// <summary>Install state by server id, kept here because a rescan replaces the rows.</summary>
    private readonly HashSet<string> _installing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _installErrors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _needsAdmin = new(StringComparer.Ordinal);
    private CancellationTokenSource? _scanCts;
    private bool _disposed;

    public ExperimentsSettingsViewModel(
        ISettingsStore settings,
        ILanguageServerManager lsp,
        IAgentInstaller? installer = null,
        IDialogService? dialogs = null,
        ITerminalLaunchPlanner? launchPlanner = null)
    {
        _settings = settings;
        _lsp = lsp;
        _installer = installer;
        _dialogs = dialogs;
        _launchPlanner = launchPlanner;
        _ = RescanServersAsync();
    }

    public ObservableCollection<LanguageServerRow> Servers { get; } = [];

    [ObservableProperty] public partial bool IsScanning { get; private set; }

    public bool CodeEditor
    {
        get => _settings.Current.Experiments.CodeEditor;
        set
        {
            if (value == CodeEditor) return;
            _settings.Update(s => s.Experiments.CodeEditor = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConfigureLanguageServer));
        }
    }

    public bool DiffEditor
    {
        get => _settings.Current.Experiments.DiffEditor;
        set
        {
            if (value == DiffEditor) return;
            _settings.Update(s => s.Experiments.DiffEditor = value);
            OnPropertyChanged();
        }
    }

    public bool LanguageServer
    {
        get => _settings.Current.Experiments.LanguageServer;
        set
        {
            if (value == LanguageServer) return;
            _settings.Update(s => s.Experiments.LanguageServer = value);
            OnPropertyChanged();
        }
    }

    public bool CanConfigureLanguageServer => CodeEditor;

    [RelayCommand]
    public async Task RescanServersAsync()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        IsScanning = true;
        try
        {
            var detected = await _lsp.DetectInstalledAsync(cts.Token);
            if (_disposed || cts.IsCancellationRequested) return;
            UiContext.Run(() =>
            {
                if (_disposed || cts.IsCancellationRequested) return;
                Servers.Clear();
                foreach (var item in detected.OrderByDescending(d => d.IsInstalled).ThenBy(d => d.Spec.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    Servers.Add(new LanguageServerRow(item, _installer is null ? null : InstallServerAsync)
                    {
                        IsInstalling = _installing.Contains(item.Spec.Id),
                        Error = _installErrors.GetValueOrDefault(item.Spec.Id),
                        NeedsAdmin = _needsAdmin.Contains(item.Spec.Id),
                    });
                }
            });
            // Not awaited: the list and the Rescan button do not wait for npm to report its prefix.
            _ = LoadInstallPlansAsync([.. Servers.Where(r => !r.IsInstalled)], cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts)) IsScanning = false;
        }
    }

    /// <summary>Finds, off the UI thread, which missing servers can be installed here (npm, brew, go, … on PATH).</summary>
    private async Task LoadInstallPlansAsync(IReadOnlyList<LanguageServerRow> rows, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            AgentInstallPlan? plan = null;
            if (_installer is not null)
            {
                var hints = row.Detected.ApplicableRecipes.Select(r => r.ToHint()).ToList();
                try
                {
                    plan = await Task.Run(() => _installer.PlanAsync(row.Id, hints, cancellationToken), cancellationToken);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    plan = null;
                }
            }
            if (_disposed || cancellationToken.IsCancellationRequested) return;
            UiContext.Run(() =>
            {
                row.InstallPlan = plan;
                row.IsPlanResolved = true;
            });
        }
    }

    /// <summary>Asks, runs the row's install command (elevated when needed, see <see cref="InstallFlow"/>), then rescans.</summary>
    internal async Task InstallServerAsync(LanguageServerRow row)
    {
        if (_disposed || _installer is null || row.InstallPlan is not { } plan || _installing.Contains(row.Id)) return;
        var flow = new InstallFlow(_installer, _dialogs, _launchPlanner, _settings);
        _installing.Add(row.Id);
        _installErrors.Remove(row.Id);
        row.Error = null;
        row.IsInstalling = true;
        string? error = null;
        try
        {
            var outcome = await flow.RunAsync(row.Name, plan, asAdmin: row.NeedsAdmin);
            if (_disposed || outcome.Cancelled) return;
            if (!outcome.Succeeded)
            {
                error = outcome.Error;
                if (outcome.NeedsAdmin) _needsAdmin.Add(row.Id);
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = AppLangResources.Format(AppLangResourceKind.SettingsExpLspInstallFailed, ex.Message);
        }
        finally
        {
            _installing.Remove(row.Id);
            if (error is not null) _installErrors[row.Id] = error;
            if (!_disposed)
            {
                UiContext.Run(() =>
                {
                    row.IsInstalling = false;
                    row.Error = error;
                    row.NeedsAdmin = _needsAdmin.Contains(row.Id);
                });
            }
        }
        if (_disposed || error is not null) return;
        await RescanServersAsync();
        var now = Servers.FirstOrDefault(r => r.Id == row.Id);
        if (now is { IsInstalled: false })
        {
            var notFound = AppLangResources.Get(AppLangResourceKind.SettingsExpLspInstallNotFound);
            _installErrors[row.Id] = notFound;
            UiContext.Run(() => now.Error = notFound);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
    }
}
