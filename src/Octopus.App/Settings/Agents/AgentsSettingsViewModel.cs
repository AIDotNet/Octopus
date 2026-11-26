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
    private readonly IAgentInstaller? _installer;
    /// <summary>Install state by agent id, kept here because <see cref="Rebuild"/> replaces the rows.</summary>
    private readonly HashSet<string> _installing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _installErrors = new(StringComparer.Ordinal);
    /// <summary>Agents whose install failed for lack of rights: their button now installs as administrator.</summary>
    private readonly HashSet<string> _needsAdmin = new(StringComparer.Ordinal);
    private int _planGeneration;
    private string _installationSignature = "";
    private bool _disposed;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isNotInstalledExpanded;

    [ObservableProperty]
    private IReadOnlyList<SelectOption> _permissionOptions = [];

    [ObservableProperty]
    private IReadOnlyList<SelectOption> _acpAdapterPolicyOptions = [];

    [ObservableProperty]
    private IReadOnlyList<SelectOption> _collabIsolationOptions = [];

    [ObservableProperty]
    private IReadOnlyList<SelectOption> _collabMaxHopsOptions = [];

    public AgentsSettingsViewModel(
        ISettingsStore settings,
        IAgentCatalog catalog,
        IAgentScanner scanner,
        IAgentCommandCompiler compiler,
        IModelDiscovery discovery,
        IFileOpener opener,
        IPathPicker pathPicker,
        IAgentDefinitionStore? store = null,
        Services.IDialogService? dialogs = null,
        Octopus.Agents.Accounts.IAgentAccountService? accounts = null,
        Octopus.Terminal.ITerminalLaunchPlanner? launchPlanner = null,
        Services.IProjectService? projects = null,
        Octopus.Terminal.Avalonia.TerminalSessionRegistry? sessions = null,
        IAgentInstaller? installer = null)
    {
        _installer = installer;
        _settings = settings;
        _catalog = catalog;
        _scanner = scanner;
        _opener = opener;
        _store = store;
        _dialogs = dialogs;
        _services = new AgentServices(settings, compiler, discovery, opener, pathPicker, accounts, dialogs, launchPlanner, projects, sessions);
        _isScanning = scanner.IsScanning;
        BuildPermissionOptions();
        BuildAcpAdapterPolicyOptions();
        BuildCollabOptions();
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

    public string NotInstalledTitle => AppLangResources.Format(AppLangResourceKind.SettingsNotInstalledCount, NotInstalled.Count);

    public string Subtitle => AppLangResources.Format(AppLangResourceKind.SettingsAgentsSubtitle, Installed.Count);

    /// <summary>Default launch surface for ACP-capable agents (non-ACP agents always open in Terminal).</summary>
    public AgentSurface DefaultSurface
    {
        get => _settings.Current.Agents.DefaultSurface;
        set
        {
            if (value == DefaultSurface)
            {
                return;
            }
            _settings.Update(s => s.Agents.DefaultSurface = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGuiDefault));
            OnPropertyChanged(nameof(IsTerminalDefault));
        }
    }

    public bool IsGuiDefault => DefaultSurface == AgentSurface.Gui;

    public bool IsTerminalDefault => DefaultSurface == AgentSurface.Terminal;

    [RelayCommand]
    private void SelectGuiSurface() => DefaultSurface = AgentSurface.Gui;

    [RelayCommand]
    private void SelectTerminalSurface() => DefaultSurface = AgentSurface.Terminal;

    [RelayCommand]
    private void ToggleNotInstalled() => IsNotInstalledExpanded = !IsNotInstalledExpanded;

    /// <summary>Selected ACP adapter resolution policy key.</summary>
    public string AcpAdapterPolicyKey
    {
        get => _settings.Current.Agents.AcpAdapterPolicy.ToString();
        set
        {
            if (string.IsNullOrEmpty(value)
                || !Enum.TryParse<AcpAdapterPolicy>(value, ignoreCase: true, out var parsed)
                || parsed == _settings.Current.Agents.AcpAdapterPolicy)
            {
                return;
            }
            _settings.Update(s => s.Agents.AcpAdapterPolicy = parsed);
            OnPropertyChanged();
        }
    }

    /// <summary>Whether ACP agents may render structured form elicitation prompts inside the GUI.</summary>
    public bool AcpEnableFormElicitation
    {
        get => _settings.Current.Agents.AcpEnableFormElicitation;
        set
        {
            if (value == AcpEnableFormElicitation)
            {
                return;
            }
            _settings.Update(s => s.Agents.AcpEnableFormElicitation = value);
            OnPropertyChanged();
        }
    }

    /// <summary>Whether ACP agents may trigger out-of-band URL authorization prompts inside the GUI.</summary>
    public bool AcpEnableUrlElicitation
    {
        get => _settings.Current.Agents.AcpEnableUrlElicitation;
        set
        {
            if (value == AcpEnableUrlElicitation)
            {
                return;
            }
            _settings.Update(s => s.Agents.AcpEnableUrlElicitation = value);
            OnPropertyChanged();
        }
    }

    /// <summary>Whether Octopus advertises client-hosted <c>fs/read_text_file</c> and <c>fs/write_text_file</c> callbacks (default off).</summary>
    public bool AcpEnableClientFileSystem
    {
        get => _settings.Current.Agents.AcpEnableClientFileSystem;
        set
        {
            if (value == AcpEnableClientFileSystem)
            {
                return;
            }
            _settings.Update(s => s.Agents.AcpEnableClientFileSystem = value);
            OnPropertyChanged();
        }
    }

    /// <summary>Whether Octopus advertises client-hosted <c>terminal/*</c> callbacks (default off).</summary>
    public bool AcpEnableClientTerminal
    {
        get => _settings.Current.Agents.AcpEnableClientTerminal;
        set
        {
            if (value == AcpEnableClientTerminal)
            {
                return;
            }
            _settings.Update(s => s.Agents.AcpEnableClientTerminal = value);
            OnPropertyChanged();
        }
    }

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

    /// <summary>Agents started here use the system proxy unless their environment already names one.</summary>
    public bool UseSystemProxy
    {
        get => _settings.Current.Agents.UseSystemProxy;
        set
        {
            if (value == UseSystemProxy)
            {
                return;
            }
            _settings.Update(s => s.Agents.UseSystemProxy = value);
            OnPropertyChanged();
        }
    }

    public bool CollabEnabled
    {
        get => _settings.Current.Agents.Collab.Enabled;
        set
        {
            if (value == CollabEnabled) return;
            _settings.Update(s => s.Agents.Collab.Enabled = value);
            OnPropertyChanged();
        }
    }

    public string CollabDefaultIsolationKey
    {
        get => _settings.Current.Agents.Collab.DefaultIsolation.ToString();
        set
        {
            if (string.IsNullOrEmpty(value)
                || !Enum.TryParse<CollabIsolationMode>(value, ignoreCase: true, out var parsed)
                || parsed == _settings.Current.Agents.Collab.DefaultIsolation)
            {
                return;
            }
            _settings.Update(s => s.Agents.Collab.DefaultIsolation = parsed);
            OnPropertyChanged();
        }
    }

    public bool CollabEnableTerminalBridge
    {
        get => _settings.Current.Agents.Collab.EnableTerminalBridge;
        set
        {
            if (value == CollabEnableTerminalBridge) return;
            _settings.Update(s => s.Agents.Collab.EnableTerminalBridge = value);
            OnPropertyChanged();
        }
    }

    public string CollabMaxHopsKey
    {
        get => _settings.Current.Agents.Collab.MaxNegotiationHops.ToString();
        set
        {
            if (!int.TryParse(value, out var parsed)) return;
            parsed = Math.Clamp(parsed, CollabSettings.MinNegotiationHops, CollabSettings.MaxAllowedNegotiationHops);
            if (parsed == _settings.Current.Agents.Collab.MaxNegotiationHops) return;
            _settings.Update(s => s.Agents.Collab.MaxNegotiationHops = parsed);
            OnPropertyChanged();
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
                    OriginBadge = badge,
                    CanRemove = canRemove,
                    RemoveText = removeText,
                    CanEdit = CanEditAgents,
                    Installer = _installer is null ? null : InstallAgentAsync,
                    IsInstalling = _installing.Contains(definition.Id),
                    InstallError = _installErrors.GetValueOrDefault(definition.Id),
                    NeedsAdmin = _needsAdmin.Contains(definition.Id),
                });
                continue;
            }
            Installed.Add(new AgentRowViewModel(definition, installation, _services)
            {
                IsExpanded = _settings.Current.Agents.ExpansionStates.GetValueOrDefault(definition.Id),
                OriginBadge = badge,
                CanRemove = canRemove,
                RemoveText = removeText,
                CanEdit = CanEditAgents,
            });
        }
        if (expandFirst && Installed.Count > 0 && _settings.Current.Agents.ExpansionStates.Count == 0)
        {
            var preferred = Installed.FirstOrDefault(r => r.IsDefault) ?? Installed[0];
            preferred.IsExpanded = true;
        }
        _installationSignature = Signature();
        _ = LoadInstallPlansAsync([.. NotInstalled]);
        OnPropertyChanged(nameof(HasInstalled));
        OnPropertyChanged(nameof(HasNotInstalled));
        OnPropertyChanged(nameof(NotInstalledTitle));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(LastScanText));
    }

    /// <summary>Finds, off the UI thread, which uninstalled agents Octopus can install here (npm, uv, brew, … on PATH).</summary>
    private async Task LoadInstallPlansAsync(IReadOnlyList<UninstalledAgentViewModel> rows)
    {
        if (_installer is null || rows.Count == 0) return;
        var generation = ++_planGeneration;
        foreach (var row in rows)
        {
            AgentInstallPlan? plan;
            try
            {
                plan = await Task.Run(() => _installer.PlanAsync(row.Definition));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                plan = null;
            }
            if (_disposed || generation != _planGeneration) return;
            UiContext.Run(() => row.InstallPlan = plan);
        }
    }

    /// <summary>
    /// Asks, runs the agent's install command (elevated when it needs administrator rights, see <see cref="InstallFlow"/>),
    /// then rescans so the agent moves to the installed group.
    /// </summary>
    internal async Task InstallAgentAsync(UninstalledAgentViewModel row)
    {
        if (_installer is null || row.InstallPlan is not { } plan || _installing.Contains(row.Id)) return;
        var flow = new InstallFlow(_installer, _dialogs, _services.LaunchPlanner, _settings);

        _installing.Add(row.Id);
        _installErrors.Remove(row.Id);
        row.InstallError = null;
        row.IsInstalling = true;
        string? error = null;
        var needsAdmin = false;
        try
        {
            var outcome = await flow.RunAsync(row.Name, plan, asAdmin: row.NeedsAdmin);
            if (_disposed || outcome.Cancelled) return;
            if (!outcome.Succeeded)
            {
                error = outcome.Error;
                needsAdmin = outcome.NeedsAdmin;
                return;
            }
            await _scanner.ScanAsync();
            if (_disposed) return;
            if (_scanner.Find(row.Id) is null) error = AppLangResources.Get(AppLangResourceKind.SettingsInstallAgentNotFound);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = AppLangResources.Format(AppLangResourceKind.SettingsInstallAgentFailed, ex.Message);
        }
        finally
        {
            _installing.Remove(row.Id);
            if (error is not null) _installErrors[row.Id] = error;
            if (needsAdmin) _needsAdmin.Add(row.Id);
            if (!_disposed)
            {
                UiContext.Run(() =>
                {
                    // The scan may have rebuilt the rows: update whichever row shows this agent now.
                    foreach (var current in NotInstalled.Where(r => r.Id == row.Id).Append(row).Distinct())
                    {
                        current.IsInstalling = false;
                        current.InstallError = error ?? current.InstallError;
                        current.NeedsAdmin = _needsAdmin.Contains(row.Id);
                    }
                    if (_scanner.Find(row.Id) is not null && NotInstalled.Any(r => r.Id == row.Id)) Rebuild();
                });
            }
        }
    }

    private string Signature() =>
        string.Join('|', _scanner.Current.Installations.OrderBy(i => i.AgentId).Select(i => $"{i.AgentId}={i.Path}={i.Version}"));

    private void BuildPermissionOptions() =>
        PermissionOptions = [.. PermissionModes.All.Select(id => new SelectOption(id, PermissionNames.Of(id)))];

    private void BuildAcpAdapterPolicyOptions() =>
        AcpAdapterPolicyOptions =
        [
            new SelectOption(nameof(AcpAdapterPolicy.UseLocalRunners), AppLangResources.Get(AppLangResourceKind.SettingsAcpPolicyLocalRunners)),
            new SelectOption(nameof(AcpAdapterPolicy.NativeOnly), AppLangResources.Get(AppLangResourceKind.SettingsAcpPolicyNativeOnly)),
            new SelectOption(nameof(AcpAdapterPolicy.ManagedRegistry), AppLangResources.Get(AppLangResourceKind.SettingsAcpPolicyManagedRegistry)),
        ];

    private void BuildCollabOptions()
    {
        CollabIsolationOptions =
        [
            new SelectOption(nameof(CollabIsolationMode.Shared), AppLangResources.Get(AppLangResourceKind.SettingsCollabIsolationShared)),
            new SelectOption(nameof(CollabIsolationMode.Worktree), AppLangResources.Get(AppLangResourceKind.SettingsCollabIsolationWorktree)),
            new SelectOption(nameof(CollabIsolationMode.ReadOnly), AppLangResources.Get(AppLangResourceKind.SettingsCollabIsolationReadOnly)),
        ];
        CollabMaxHopsOptions =
        [
            new SelectOption("4", "4"),
            new SelectOption("6", "6"),
            new SelectOption("10", "10"),
            new SelectOption("16", "16"),
        ];
    }

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
        OnPropertyChanged(nameof(UseSystemProxy));
        OnPropertyChanged(nameof(DefaultSurface));
        OnPropertyChanged(nameof(IsGuiDefault));
        OnPropertyChanged(nameof(IsTerminalDefault));
        OnPropertyChanged(nameof(AcpAdapterPolicyKey));
        OnPropertyChanged(nameof(AcpEnableFormElicitation));
        OnPropertyChanged(nameof(AcpEnableUrlElicitation));
        OnPropertyChanged(nameof(AcpEnableClientFileSystem));
        OnPropertyChanged(nameof(AcpEnableClientTerminal));
        OnPropertyChanged(nameof(CollabEnabled));
        OnPropertyChanged(nameof(CollabDefaultIsolationKey));
        OnPropertyChanged(nameof(CollabEnableTerminalBridge));
        OnPropertyChanged(nameof(CollabMaxHopsKey));
    });

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e) => UiContext.PostToUi(() =>
    {
        BuildPermissionOptions();
        BuildAcpAdapterPolicyOptions();
        BuildCollabOptions();
        Rebuild();
        OnPropertyChanged(nameof(GlobalPermission));
        OnPropertyChanged(nameof(AcpAdapterPolicyKey));
        OnPropertyChanged(nameof(CollabDefaultIsolationKey));
        OnPropertyChanged(nameof(CollabMaxHopsKey));
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
