using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Platform;
using Octopus.Ssh;
using TKCode.Arc;
using TKCode.Arc.Localization;

namespace Octopus.App.Settings.Ssh;

/// <summary>One row of the SSH page: the alias, where it connects, and what else the file says about it.</summary>
/// <param name="Address">Where it connects, as user@host:port; empty when that is only the alias again.</param>
/// <param name="Details">Where it jumps through and which key it uses, already in the app's language; null when neither is set.</param>
public sealed record SshHostRow(string Alias, string Address, string? Details)
{
    public bool HasAddress => Address.Length > 0;

    public bool HasDetails => Details is not null;
}

/// <summary>The SSH hosts page: the hosts of ~/.ssh/config, a rescan, and a way to open the file for editing.</summary>
public sealed partial class SshSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISshHostCatalog _catalog;
    private readonly IFileOpener _opener;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private string? _errorText;

    public SshSettingsViewModel(ISshHostCatalog catalog, IFileOpener opener, IDialogService dialogs)
    {
        _catalog = catalog;
        _opener = opener;
        _dialogs = dialogs;
        catalog.Changed += OnCatalogChanged;
        ArcLanguage.Changed += OnLanguageChanged;
        Rebuild();
    }

    public ObservableCollection<SshHostRow> Hosts { get; } = [];

    public bool HasHosts => Hosts.Count > 0;

    public bool ConfigExists => _catalog.Current.ConfigExists;

    public string ConfigPath => _catalog.Current.ConfigPath;

    public string Subtitle => AppLangResources.Format(AppLangResourceKind.SettingsSshSubtitle, Hosts.Count);

    /// <summary>What the empty list says: there is no file, or the file has no host.</summary>
    public string EmptyText => AppLangResources.Get(ConfigExists ? AppLangResourceKind.SettingsSshNoHosts : AppLangResourceKind.SettingsSshNoConfig);

    public string LastScanText => _catalog.Current.ScannedAt == default
        ? AppLangResources.Get(AppLangResourceKind.SettingsNeverScanned)
        : AppLangResources.Format(AppLangResourceKind.SettingsLastScan, _catalog.Current.ScannedAt.ToLocalTime().ToString("g", ArcLanguage.Culture));

    /// <summary>True when the last write failed; <see cref="ErrorText"/> says why.</summary>
    public bool HasError => ErrorText is not null;

    [RelayCommand]
    private void Rescan() => _catalog.Refresh();

    [RelayCommand]
    private async Task AddHostAsync()
    {
        var draft = await _dialogs.AskNewSshHostAsync();
        if (draft is null)
        {
            return;
        }
        try
        {
            _catalog.Add(draft);
            ErrorText = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorText = AppLangResources.Format(AppLangResourceKind.SettingsSshWriteFailed, e.Message);
        }
    }

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    private void OpenConfig()
    {
        if (ConfigExists)
        {
            _opener.Open(ConfigPath);
        }
    }

    public void Dispose()
    {
        _catalog.Changed -= OnCatalogChanged;
        ArcLanguage.Changed -= OnLanguageChanged;
    }

    private void OnCatalogChanged(object? sender, EventArgs e) => Rebuild();

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        Hosts.Clear();
        foreach (var host in _catalog.Current.Hosts)
        {
            Hosts.Add(RowOf(host));
        }
        OnPropertyChanged(nameof(HasHosts));
        OnPropertyChanged(nameof(ConfigExists));
        OnPropertyChanged(nameof(ConfigPath));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(LastScanText));
    }

    private static SshHostRow RowOf(SshHost host)
    {
        var user = host.User is null ? "" : host.User + "@";
        var port = host.Port is { } number ? ":" + number : "";
        var details = new List<string>();
        if (host.ProxyJump is not null)
        {
            details.Add(AppLangResources.Format(AppLangResourceKind.SettingsSshVia, host.ProxyJump));
        }
        if (host.IdentityFile is not null)
        {
            details.Add(AppLangResources.Format(AppLangResourceKind.SettingsSshKey, host.IdentityFile));
        }
        var address = user + host.HostName + port;
        return new SshHostRow(host.Alias, address == host.Alias ? "" : address, details.Count == 0 ? null : string.Join("  ·  ", details));
    }
}
