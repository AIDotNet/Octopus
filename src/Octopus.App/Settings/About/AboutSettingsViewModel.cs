using System.Runtime.InteropServices;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Updates;
using Octopus.Platform;
using Octopus.Updates;
using TKCode.Arc;
using TKCode.Arc.Localization;

namespace Octopus.App.Settings.About;

/// <summary>The about settings page: Octopus overview, software updates, official links, local paths, and diagnostics.</summary>
public sealed partial class AboutSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IUpdateService _updateService;
    private readonly IAppPaths _paths;
    private readonly IFileOpener _opener;
    private CancellationTokenSource? _copiedCts;

    public AboutSettingsViewModel(
        UpdateSettingsViewModel updates,
        IUpdateService updateService,
        IAppPaths paths,
        IFileOpener opener)
    {
        Updates = updates;
        _updateService = updateService;
        _paths = paths;
        _opener = opener;
        TechStackSummary = BuildTechStackSummary();
        ArcLanguage.Changed += OnLanguageChanged;
    }

    /// <summary>The software updates model shared with the hero action and update settings rows.</summary>
    public UpdateSettingsViewModel Updates { get; }

    public string WebsiteUrl => UpdateEndpoints.ServiceUrl;

    public string RepositoryUrl => UpdateEndpoints.RepositoryUrl;

    public string DataDirectory => _paths.DataDirectory;

    public string CacheDirectory => _paths.CacheDirectory;

    [ObservableProperty] public partial string TechStackSummary { get; private set; } = "";

    [ObservableProperty] public partial bool DiagnosticsCopied { get; private set; }

    [RelayCommand]
    private void OpenWebsite() => _opener.OpenUrl(UpdateEndpoints.ServiceUrl);

    [RelayCommand]
    private void OpenRepository() => _opener.OpenUrl(UpdateEndpoints.RepositoryUrl);

    [RelayCommand]
    private void OpenChangelog() => _opener.OpenUrl($"{UpdateEndpoints.RepositoryUrl}/releases");

    [RelayCommand]
    private void OpenIssues() => _opener.OpenUrl($"{UpdateEndpoints.RepositoryUrl}/issues");

    [RelayCommand]
    private void OpenDataDirectory() => RevealDirectory(_paths.DataDirectory);

    [RelayCommand]
    private void OpenCacheDirectory() => RevealDirectory(_paths.CacheDirectory);

    /// <summary>Builds a plain-text summary of app, OS, and runtime details for issue reports.</summary>
    public string BuildDiagnosticsText()
    {
        var snapshot = _updateService.Current;
        var version = snapshot.CurrentVersion?.ToString() ?? "Development";
        var channel = snapshot.Status == UpdateStatus.Disabled
            ? "Development"
            : snapshot.CurrentVersion?.IsPreview == true ? "Preview" : "Stable";
        var avalonia = ShortVersion(typeof(Application).Assembly.GetName().Version);
        var arc = ShortVersion(typeof(ArcTheme).Assembly.GetName().Version);
        return string.Join(Environment.NewLine,
        [
            $"Octopus {version} ({channel}, {snapshot.InstallKind})",
            $"OS: {RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture}, process {RuntimeInformation.ProcessArchitecture})",
            $"Runtime: {RuntimeInformation.FrameworkDescription}",
            $"UI: Avalonia {avalonia} / TKCode.Arc {arc}",
            $"Service: {UpdateEndpoints.ServiceUrl}",
            $"Data: {_paths.DataDirectory}",
            $"Cache: {_paths.CacheDirectory}",
        ]);
    }

    /// <summary>Marks diagnostics as recently copied and resets the visual confirmation after a short pause.</summary>
    public void MarkDiagnosticsCopied()
    {
        _copiedCts?.Cancel();
        _copiedCts?.Dispose();
        var cts = _copiedCts = new CancellationTokenSource();
        DiagnosticsCopied = true;
        _ = ResetCopiedAsync(cts.Token);
    }

    public void Dispose()
    {
        ArcLanguage.Changed -= OnLanguageChanged;
        _copiedCts?.Cancel();
        _copiedCts?.Dispose();
        _copiedCts = null;
        Updates.Dispose();
    }

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e)
    {
        UiContext.PostToUi(() => TechStackSummary = BuildTechStackSummary());
    }

    private async Task ResetCopiedAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1800), ct).ConfigureAwait(false);
            UiContext.PostToUi(() =>
            {
                if (!ct.IsCancellationRequested) DiagnosticsCopied = false;
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RevealDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // Reveal still attempts to open the path or its parent if creation is not permitted.
        }
        _opener.Reveal(path);
    }

    private static string BuildTechStackSummary()
    {
        var dotnet = $"{Environment.Version.Major}.{Environment.Version.Minor}";
        var avalonia = ShortVersion(typeof(Application).Assembly.GetName().Version);
        var arc = ShortVersion(typeof(ArcTheme).Assembly.GetName().Version);
        var stack = $".NET {dotnet} · Avalonia {avalonia} · TKCode.Arc {arc} · MiniPty · Jaunt";
        return AppLangResources.Format(AppLangResourceKind.SettingsAboutBuiltWith, stack);
    }

    private static string ShortVersion(Version? version) =>
        version is null ? "?" : version.Build > 0 ? $"{version.Major}.{version.Minor}.{version.Build}" : $"{version.Major}.{version.Minor}";
}
