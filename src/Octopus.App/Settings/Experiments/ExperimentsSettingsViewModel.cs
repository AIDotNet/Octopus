using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.App.Files.Lsp;
using Octopus.App.Localization;
using Octopus.Storage;

namespace Octopus.App.Settings.Experiments;

/// <summary>One row in the detected local language servers list.</summary>
public sealed record LanguageServerRow(string Name, string Executable, string? Path, bool IsInstalled)
{
    public string StatusText => IsInstalled
        ? AppLangResources.Get(AppLangResourceKind.SettingsExpLspDetected)
        : AppLangResources.Get(AppLangResourceKind.SettingsExpLspNotInstalled);

    public string DetailText => Path ?? Executable;
}

/// <summary>
/// The Experiments settings page: opt-in toggles for Monaco-based code editing, interactive diff editing, and local
/// Language Server Protocol (LSP) integration, plus a detector showing which language servers are installed on PATH.
/// </summary>
public sealed partial class ExperimentsSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly ILanguageServerManager _lsp;
    private CancellationTokenSource? _scanCts;
    private bool _disposed;

    public ExperimentsSettingsViewModel(ISettingsStore settings, ILanguageServerManager lsp)
    {
        _settings = settings;
        _lsp = lsp;
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
                    Servers.Add(new LanguageServerRow(item.Spec.DisplayName, item.Spec.Executable, item.ExecutablePath, item.IsInstalled));
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts)) IsScanning = false;
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
