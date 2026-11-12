using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.Core.Agents;
using Octopus.Platform;
using Octopus.Storage;
using TKCode.Arc;
using TKCode.Arc.Localization;

namespace Octopus.App.Settings.Agents;

/// <summary>The agents page: the default permission, the scan, and the installed and not installed agents.</summary>
public sealed partial class AgentsSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly IAgentCatalog _catalog;
    private readonly IAgentScanner _scanner;
    private readonly IFileOpener _opener;
    private readonly AgentServices _services;
    private readonly IAgentDefinitionStore? _store;
    private readonly Services.IDialogService? _dialogs;
    private string _installationSignature = "";
    private bool _disposed;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private IReadOnlyList<SelectOption> _permissionOptions = [];

    public AgentsSettingsViewModel(
        ISettingsStore settings,
        IAgentCatalog catalog,
        IAgentScanner scanner,
        IAgentCommandCompiler compiler,
        IModelDiscovery discovery,
        IFileOpener opener,
        IPathPicker pathPicker,
        IAgentDefinitionStore? store = null,
        Services.IDialogService? dialogs = null)
    {
        _settings = settings;
        _catalog = catalog;
        _scanner = scanner;
        _opener = opener;
        _store = store;
        _dialogs = dialogs;
        _services = new AgentServices(settings, compiler, discovery, opener, pathPicker);
        _isScanning = scanner.IsScanning;
        BuildPermissionOptions();
        Rebuild(expandFirst: true);
        scanner.Changed += OnScannerChanged;
        catalog.Changed += OnCatalogChanged;
        settings.Changed += OnSettingsChanged;
        ArcLanguage.Changed += OnLanguageChanged;
    }

    public ObservableCollection<AgentRowViewModel> Installed { get; } = [];

    public ObservableCollection<UninstalledAgentViewModel> NotInstalled { get; } = [];

    public bool HasInstalled => Installed.Count > 0;

    public bool HasNotInstalled => NotInstalled.Count > 0;

    public string Subtitle => AppLangResources.Format(AppLangResourceKind.SettingsAgentsSubtitle, Installed.Count);

    /// <summary>The unified permission level of agents without their own; the id of one of <see cref="PermissionOptions"/>.</summary>
    public string GlobalPermission
    {
        get => _settings.Current.Agents.DefaultPermission;
        set
        {
            if (string.IsNullOrEmpty(value) || value == GlobalPermission)
            {
                return;
            }
            _settings.Update(s => s.Agents.DefaultPermission = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowFullWarning));
        }
    }

    public bool ShowFullWarning => GlobalPermission == PermissionModes.Full;

    public string RescanText => AppLangResources.Get(IsScanning ? AppLangResourceKind.SettingsScanning : AppLangResourceKind.SettingsRescan);

    public string LastScanText => _scanner.Current.ScannedAt == default
        ? AppLangResources.Get(AppLangResourceKind.SettingsNeverScanned)
        : AppLangResources.Format(AppLangResourceKind.SettingsLastScan, _scanner.Current.ScannedAt.ToLocalTime().ToString("g", ArcLanguage.Culture));

    public bool CanEditAgents => _store is not null && _dialogs is not null;

    /// <summary>Opens the editor for a new agent; saving it writes its file and looks for it on this machine.</summary>
    [RelayCommand]
    private async Task AddAgent()
    {
        if (_store is null || _dialogs is null) return;
        var model = new AgentEditorModel(_store, null, _catalog.Definitions.Select(d => d.Id).ToList());
        await SaveFromAsync(model);
    }

    /// <summary>Opens the editor on an agent: a custom one is changed, a built-in one gets the user's own version.</summary>
    [RelayCommand]
    private async Task EditAgent(AgentDefinition? definition)
    {
        if (definition is null || _store is null || _dialogs is null) return;
        await SaveFromAsync(new AgentEditorModel(_store, definition, []));
    }

    private async Task SaveFromAsync(AgentEditorModel model)
    {
        var definition = await _dialogs!.AskAgentDefinitionAsync(model);
        if (definition is null) return;
        _store!.Save(definition);
        Rebuild();
        // A new executable, or a new way to find it: look again.
        await RescanAsync();
    }

    /// <summary>Deletes a custom agent, or the user's version of a built-in one (which then comes back), after asking.</summary>
    [RelayCommand]
    private async Task RemoveAgent(AgentDefinition? definition)
    {
        if (definition is null || _store is null || _dialogs is null || !_store.HasUserFile(definition.Id)) return;
        var builtIn = _store.IsBuiltIn(definition.Id);
        var confirmed = await _dialogs.ConfirmAsync(
            AppLangResources.Format(builtIn ? AppLangResourceKind.SettingsRestoreAgentTitle : AppLangResourceKind.SettingsDeleteAgentTitle, definition.Name),
            AppLangResources.Get(builtIn ? AppLangResourceKind.SettingsRestoreAgentDescription : AppLangResourceKind.SettingsDeleteAgentDescription),
            AppLangResources.Get(builtIn ? AppLangResourceKind.SettingsRestoreAgent : AppLangResourceKind.SettingsDeleteAgent),
            isDanger: !builtIn);
        if (!confirmed) return;
        _store.Delete(definition.Id);
        Rebuild();
        await RescanAsync();
    }

    /// <summary>"Custom" for an agent only the user defines, "Modified" for a built-in one they changed; null otherwise.</summary>
    private (string? Badge, bool CanRemove, string RemoveText) OriginOf(AgentDefinition definition)
    {
        if (_store is null) return (null, false, "");
        if (!_store.HasUserFile(definition.Id))
        {
            // Shipped by a plugin: said so; it goes away with the plugin.
            return definition.Origin is not null
                ? (AppLangResources.Format(AppLangResourceKind.SettingsPluginAgent, definition.OriginName ?? definition.Origin), false, "")
                : (null, false, "");
        }
        return _store.IsBuiltIn(definition.Id)
            ? (AppLangResources.Get(AppLangResourceKind.SettingsModifiedAgent), true, AppLangResources.Get(AppLangResourceKind.SettingsRestoreAgent))
            : (AppLangResources.Get(AppLangResourceKind.SettingsCustomAgent), true, AppLangResources.Get(AppLangResourceKind.SettingsDeleteAgent));
    }

    private void OnCatalogChanged(object? sender, EventArgs e) => UiContext.PostToUi(() =>
    {
        if (!_disposed) Rebuild();
    });

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private async Task RescanAsync()
    {
        IsScanning = true;
        try
        {
            await _scanner.ScanAsync();
        }
        finally
        {
            IsScanning = _scanner.IsScanning;
            OnScanFinished();
        }
    }

    private bool CanRescan() => !IsScanning;

    partial void OnIsScanningChanged(bool value)
    {
        RescanCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RescanText));
    }

    /// <summary>Writes the edits of every open row that are still waiting for typing to pause.</summary>
    public void FlushPending()
    {
        foreach (var row in Installed)
        {
            row.FlushPending();
        }
    }

    /// <summary>Builds the two groups from the catalog and the scan result. Rows keep their open state.</summary>
    public void Rebuild(bool expandFirst = false)
    {
        var open = Installed.Where(r => r.IsExpanded).Select(r => r.Id).ToHashSet();
        foreach (var row in Installed)
        {
            row.FlushPending();
            row.Dispose();
        }
        Installed.Clear();
        NotInstalled.Clear();
        foreach (var definition in _catalog.Definitions)
        {
            var installation = _scanner.Find(definition.Id);
            var (badge, canRemove, removeText) = OriginOf(definition);
            if (installation is null)
            {
                NotInstalled.Add(new UninstalledAgentViewModel(definition, _opener)
                {
                    OriginBadge = badge, CanRemove = canRemove, RemoveText = removeText, CanEdit = CanEditAgents,
                });
                continue;
            }
            Installed.Add(new AgentRowViewModel(definition, installation, _services)
            {
                IsExpanded = open.Contains(definition.Id),
                OriginBadge = badge,
                CanRemove = canRemove,
                RemoveText = removeText,
                CanEdit = CanEditAgents,
            });
        }
        if (expandFirst && Installed.Count > 0)
        {
            var preferred = Installed.FirstOrDefault(r => r.IsDefault) ?? Installed[0];
            preferred.IsExpanded = true;
        }
        _installationSignature = Signature();
        OnPropertyChanged(nameof(HasInstalled));
        OnPropertyChanged(nameof(HasNotInstalled));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(LastScanText));
    }

    private string Signature() =>
        string.Join('|', _scanner.Current.Installations.OrderBy(i => i.AgentId).Select(i => $"{i.AgentId}={i.Path}={i.Version}"));

    private void BuildPermissionOptions() =>
        PermissionOptions = [.. PermissionModes.All.Select(id => new SelectOption(id, PermissionNames.Of(id)))];

    private void OnScannerChanged(object? sender, EventArgs e) => UiContext.PostToUi(() =>
    {
        IsScanning = _scanner.IsScanning;
        if (Signature() != _installationSignature)
        {
            Rebuild();
        }
        OnScanFinished();
    });

    private void OnScanFinished() => OnPropertyChanged(nameof(LastScanText));

    private void OnSettingsChanged(object? sender, EventArgs e) => UiContext.PostToUi(() =>
    {
        OnPropertyChanged(nameof(GlobalPermission));
        OnPropertyChanged(nameof(ShowFullWarning));
    });

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e) => UiContext.PostToUi(() =>
    {
        BuildPermissionOptions();
        Rebuild();
        OnPropertyChanged(nameof(GlobalPermission));
        OnPropertyChanged(nameof(RescanText));
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _scanner.Changed -= OnScannerChanged;
        _catalog.Changed -= OnCatalogChanged;
        _settings.Changed -= OnSettingsChanged;
        ArcLanguage.Changed -= OnLanguageChanged;
        foreach (var row in Installed)
        {
            row.FlushPending();
            row.Dispose();
        }
    }
}
