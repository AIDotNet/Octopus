using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Settings;
using Octopus.Core.Agents;
using TKCode.Arc;
using TKCode.Arc.Localization;

namespace Octopus.App.Launcher;

/// <summary>
/// The second panel of the launcher for one agent (Scheme C).
/// Top source selector switches between subscription accounts, managed model providers, and (for remote host projects)
/// local providers forwarded via encrypted tunnel. Model list, search (with cross-provider jump), effort narrowing,
/// and pinned quick-launch all react to the active source.
/// </summary>
public sealed partial class AgentLaunchViewModel : ObservableObject, IDisposable
{
    private readonly AgentDefinition _definition;
    private readonly AgentProfile _profile;
    private readonly IAgentCommandCompiler _compiler;
    private readonly IModelDiscovery _discovery;
    private readonly string? _executable;
    private readonly Action<AgentLaunchOptions> _launch;
    private readonly Action<PinnedLaunchPreset>? _onPin;
    private readonly Action? _onOpenProviders;
    private readonly List<AgentOption> _known = [];
    private readonly List<AgentOption> _customModels = [];
    private readonly IReadOnlyList<AgentAccountRecord> _accounts;
    private readonly IReadOnlyList<ModelProviderRecord> _providers;
    private readonly bool _isRemoteHostProject;
    private readonly string _permissionId;
    private int _discoverGeneration;
    private bool _syncingSelection;

    public AgentLaunchViewModel(
        AgentDefinition definition,
        AgentProfile profile,
        string globalPermission,
        IAgentCommandCompiler compiler,
        IModelDiscovery discovery,
        string? executable,
        Action<AgentLaunchOptions> launch,
        IReadOnlyList<AgentAccountRecord>? accounts = null,
        string? preferredAccountId = null,
        AgentSurface defaultSurface = AgentSurface.Terminal,
        bool? allowGui = null,
        IReadOnlyList<ModelProviderRecord>? compatibleProviders = null,
        string? preferredBackendRef = null,
        bool isRemoteHostProject = false,
        Action<PinnedLaunchPreset>? onPin = null,
        Action? onOpenProviders = null)
    {
        _definition = definition;
        _profile = profile;
        _compiler = compiler;
        _discovery = discovery;
        _executable = executable;
        _launch = launch;
        _accounts = accounts ?? [];
        _providers = compatibleProviders ?? [];
        _isRemoteHostProject = isRemoteHostProject;
        _onPin = onPin;
        _onOpenProviders = onOpenProviders;

        var capabilities = definition.Capabilities;
        HasModel = capabilities.Model is not null;
        HasEffort = capabilities.Effort is not null;
        HasFast = capabilities.Fast is not null;
        HasPrompt = capabilities.Prompt is not null;
        HasAccounts = _accounts.Count >= 2;
        HasBackends = HasAccounts || _providers.Count > 0;
        AllowCustomModel = capabilities.Model?.AllowCustom ?? false;
        AgentName = definition.Name;
        SupportsAcp = allowGui ?? definition.SupportsAcp;
        DefaultSurface = SupportsAcp ? defaultSurface : AgentSurface.Terminal;

        _permissionId = compiler.ResolvePermission(definition, profile, globalPermission) ?? PermissionModes.Default;

        SeedDefaultKnownModels();
        SelectedModelId = string.IsNullOrEmpty(profile.Model) ? null : profile.Model;
        Fast = profile.Fast ?? false;
        SelectedEffortId = profile.Effort ?? "";

        _syncingSelection = true;
        SelectedAccountId = HasAccounts
            ? (_accounts.FirstOrDefault(a => a.Id == preferredAccountId)?.Id ?? _accounts[0].Id)
            : null;
        SelectedBackendRef = ResolveInitialBackendRef(preferredBackendRef, preferredAccountId);
        _syncingSelection = false;

        RebuildBackends();
        ApplyActiveBackendModels(allowDiscovery: true);
        RebuildEfforts();
        ArcLanguage.Changed += OnLanguageChanged;
    }

    public string AgentName { get; }
    public bool SupportsAcp { get; }
    public AgentSurface DefaultSurface { get; }
    public bool ShowDualLaunchButtons => SupportsAcp;
    public bool ShowTerminalOnlyHint => !SupportsAcp;
    public string PrimaryLaunchLabel => AppLangResources.Get(
        DefaultSurface == AgentSurface.Gui ? AppLangResourceKind.LaunchGui : AppLangResourceKind.LaunchTerminal);
    public string SecondaryLaunchLabel => AppLangResources.Get(
        DefaultSurface == AgentSurface.Gui ? AppLangResourceKind.LaunchTerminal : AppLangResourceKind.LaunchGui);
    public bool HasModel { get; }
    public bool HasEffort { get; }
    public bool HasFast { get; }
    public bool HasPrompt { get; }
    public bool HasAccounts { get; }
    public bool HasBackends { get; }
    public bool CanPin => _onPin is not null;
    public bool CanManageProviders => _onOpenProviders is not null && _definition.SupportsManagedProviders;
    public bool AllowCustomModel { get; }
    public string EffortCaption => EffortCaptionOf(_definition.Capabilities.Effort?.Label);

    public string PermissionName =>
        _definition.Capabilities.Permission?.Modes.GetValueOrDefault(_permissionId)?.LabelFor(AgentLanguage.Current)
        ?? PermissionLabel(_permissionId);

    public ObservableCollection<ModelOptionViewModel> Models { get; } = [];
    public ObservableCollection<EffortChoiceViewModel> Efforts { get; } = [];
    public ObservableCollection<SelectOption> AccountOptions { get; } = [];
    public ObservableCollection<SelectOption> BackendOptions { get; } = [];

    [ObservableProperty] public partial string ModelQuery { get; set; } = "";
    [ObservableProperty] public partial string? SelectedModelId { get; set; }
    [ObservableProperty] public partial string SelectedEffortId { get; set; } = "";
    [ObservableProperty] public partial string? SelectedAccountId { get; set; }
    [ObservableProperty] public partial string? SelectedBackendRef { get; set; }
    [ObservableProperty] public partial bool Fast { get; set; }
    [ObservableProperty] public partial string Prompt { get; set; } = "";
    [ObservableProperty] public partial bool IsDiscovering { get; set; }
    [ObservableProperty] public partial bool IsPinnedFeedback { get; set; }

    public bool IsTunnelSelected => SelectedBackendRef is not null && AgentBackendRefs.TryParseTunnel(SelectedBackendRef, out _);

    partial void OnModelQueryChanged(string value) => RebuildModels();

    partial void OnSelectedModelIdChanged(string? value)
    {
        RebuildModels();
        RebuildEfforts();
    }

    partial void OnSelectedAccountIdChanged(string? value)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        SelectedBackendRef = string.IsNullOrWhiteSpace(value) ? "" : AgentBackendRefs.Account(value);
        _syncingSelection = false;
        OnPropertyChanged(nameof(IsTunnelSelected));
        ApplyActiveBackendModels(allowDiscovery: true);
        RebuildEfforts();
    }

    partial void OnSelectedBackendRefChanged(string? value)
    {
        OnPropertyChanged(nameof(IsTunnelSelected));
        if (_syncingSelection) return;
        _syncingSelection = true;
        if (value is not null && AgentBackendRefs.TryParseAccount(value, out var accId))
        {
            SelectedAccountId = accId;
        }
        else if (HasAccounts && string.IsNullOrEmpty(value))
        {
            SelectedAccountId = _accounts[0].Id;
        }
        _syncingSelection = false;
        ApplyActiveBackendModels(allowDiscovery: true);
        RebuildEfforts();
    }

    private string? ResolveInitialBackendRef(string? preferredBackendRef, string? preferredAccountId)
    {
        if (!HasBackends)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(preferredBackendRef))
        {
            if (AgentBackendRefs.TryParseProvider(preferredBackendRef, out var pId)
                && !_isRemoteHostProject
                && _providers.Any(p => p.Id == pId))
            {
                return preferredBackendRef;
            }
            if (AgentBackendRefs.TryParseTunnel(preferredBackendRef, out var tId)
                && _isRemoteHostProject
                && _providers.Any(p => p.Id == tId))
            {
                return preferredBackendRef;
            }
            if (AgentBackendRefs.TryParseAccount(preferredBackendRef, out var aId)
                && !_isRemoteHostProject
                && _accounts.Any(a => a.Id == aId))
            {
                return preferredBackendRef;
            }
        }
        if (!_isRemoteHostProject && _accounts.Count > 0)
        {
            var acc = _accounts.FirstOrDefault(a => a.Id == preferredAccountId) ?? _accounts[0];
            return AgentBackendRefs.Account(acc.Id);
        }
        return "";
    }

    private ModelProviderRecord? ActiveProvider()
    {
        if (string.IsNullOrWhiteSpace(SelectedBackendRef))
        {
            return null;
        }
        if (AgentBackendRefs.TryParseProvider(SelectedBackendRef, out var pid)
            || AgentBackendRefs.TryParseTunnel(SelectedBackendRef, out pid))
        {
            return _providers.FirstOrDefault(p => p.Id == pid);
        }
        return null;
    }

    private string BackendRefFor(ModelProviderRecord provider) =>
        _isRemoteHostProject
            ? AgentBackendRefs.Tunnel(provider.Id)
            : AgentBackendRefs.Provider(provider.Id);

    private void SeedDefaultKnownModels()
    {
        _known.Clear();
        if (_definition.Capabilities.Model is { } model)
        {
            _known.AddRange(model.Options);
            if (!string.IsNullOrEmpty(_profile.Model) && _known.All(o => o.Id != _profile.Model))
            {
                _known.Add(new AgentOption { Id = _profile.Model });
            }
        }
        foreach (var custom in _customModels)
        {
            if (_known.All(o => o.Id != custom.Id))
            {
                _known.Add(custom);
            }
        }
    }

    private void ApplyActiveBackendModels(bool allowDiscovery)
    {
        var provider = ActiveProvider();
        if (provider is not null)
        {
            // Cancel any in-flight native discovery so it doesn't overwrite provider models.
            Interlocked.Increment(ref _discoverGeneration);
            IsDiscovering = false;
            _known.Clear();
            var enabledModels = provider.Models.Where(m => m.Enabled).ToList();
            foreach (var spec in enabledModels)
            {
                var note = spec.Tier != ModelSlotTier.None ? spec.Tier.ToString() : null;
                _known.Add(new AgentOption
                {
                    Id = spec.Id,
                    Label = string.IsNullOrWhiteSpace(spec.DisplayName) ? spec.Id : spec.DisplayName,
                    Note = note,
                });
            }
            if (SelectedModelId is not null && enabledModels.All(m => !string.Equals(m.Id, SelectedModelId, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedModelId = enabledModels.FirstOrDefault()?.Id;
            }
            RebuildModels();
            return;
        }

        SeedDefaultKnownModels();
        RebuildModels();
        if (allowDiscovery && CanDiscover())
        {
            _ = DiscoverAsync();
        }
    }

    private void SelectModel(ModelOptionViewModel? option)
    {
        if (option is null)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(option.SwitchBackendRef)
            && !string.Equals(option.SwitchBackendRef, SelectedBackendRef, StringComparison.Ordinal))
        {
            SelectedBackendRef = option.SwitchBackendRef;
            SelectedModelId = option.Id;
            ModelQuery = "";
            return;
        }
        if (option.IsCustom && option.Id is { } customId)
        {
            if (_customModels.All(o => o.Id != customId))
            {
                _customModels.Add(new AgentOption { Id = customId });
            }
            if (_known.All(o => o.Id != customId))
            {
                _known.Add(new AgentOption { Id = customId });
            }
        }
        SelectedModelId = option.Id;
        if (option.IsCustom)
        {
            ModelQuery = "";
        }
    }

    /// <summary>
    /// Enter in the model search: picks the first match for the text (the exact or custom id when there is one) and
    /// launches. Without text it launches with the current selection.
    /// </summary>
    public void CommitModelQuery()
    {
        if (ModelQuery.Trim().Length > 0)
        {
            var match = Models.FirstOrDefault(m => m.Id is not null && string.Equals(m.Id, ModelQuery.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? Models.FirstOrDefault(m => m.Id is not null);
            if (match is null)
            {
                return;
            }
            SelectModel(match);
        }
        Launch();
    }

    [RelayCommand]
    private void Launch() => _launch(BuildOptions(DefaultSurface));

    [RelayCommand]
    private void LaunchSecondary() => _launch(BuildOptions(DefaultSurface == AgentSurface.Gui ? AgentSurface.Terminal : AgentSurface.Gui));

    [RelayCommand]
    private void LaunchGui() => _launch(BuildOptions(SupportsAcp ? AgentSurface.Gui : AgentSurface.Terminal));

    [RelayCommand]
    private void LaunchTerminal() => _launch(BuildOptions(AgentSurface.Terminal));

    [RelayCommand]
    private void PinCurrent()
    {
        if (_onPin is null) return;
        var provider = ActiveProvider();
        var modelLabel = SelectedModelId is null
            ? null
            : Models.FirstOrDefault(m => m.Id == SelectedModelId)?.Label ?? SelectedModelId;
        var parts = new List<string> { _definition.Name };
        if (provider is not null) parts.Add(provider.Name);
        if (!string.IsNullOrWhiteSpace(modelLabel)) parts.Add(modelLabel);
        var preset = new PinnedLaunchPreset
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            CustomLabel = string.Join(" · ", parts),
            AgentId = _definition.Id,
            BackendRef = string.IsNullOrWhiteSpace(SelectedBackendRef) ? "" : SelectedBackendRef,
            ModelId = HasModel && !string.IsNullOrWhiteSpace(SelectedModelId) ? SelectedModelId : null,
            Effort = HasEffort && !string.IsNullOrWhiteSpace(SelectedEffortId) ? SelectedEffortId : null,
        };
        _onPin(preset);
        IsPinnedFeedback = true;
    }

    [RelayCommand]
    private void OpenProviders() => _onOpenProviders?.Invoke();

    /// <summary>
    /// The choice made in the panel. The panel always shows a selection, so an explicit "agent default" is the empty
    /// string (it overrides the profile's value and produces no argument); null is only for blocks the agent lacks.
    /// </summary>
    public AgentLaunchOptions BuildOptions(AgentSurface? surface = null)
    {
        var backendRef = HasBackends && !string.IsNullOrWhiteSpace(SelectedBackendRef) ? SelectedBackendRef : null;
        string? accountId = null;
        if (backendRef is not null && AgentBackendRefs.TryParseAccount(backendRef, out var parsedAcc))
        {
            accountId = HasAccounts ? parsedAcc : null;
        }
        else if (backendRef is null && HasAccounts && !string.IsNullOrWhiteSpace(SelectedAccountId))
        {
            accountId = SelectedAccountId;
        }

        return new AgentLaunchOptions
        {
            Model = HasModel ? SelectedModelId ?? "" : null,
            Effort = HasEffort ? SelectedEffortId : null,
            Fast = HasFast ? Fast : null,
            AccountId = accountId,
            BackendRef = backendRef,
            Prompt = HasPrompt && !string.IsNullOrWhiteSpace(Prompt) ? Prompt.Trim() : null,
            Surface = surface is { } chosen ? (SupportsAcp ? chosen : AgentSurface.Terminal) : null,
        };
    }

    private void RebuildBackends()
    {
        AccountOptions.Clear();
        BackendOptions.Clear();

        if (HasAccounts)
        {
            foreach (var account in _accounts)
            {
                var label = account.IsDefaultOrigin && account.Label == "Default"
                    ? AppLangResources.Get(AppLangResourceKind.AccountDefaultName)
                    : account.Label;
                if (!string.IsNullOrWhiteSpace(account.Identity))
                {
                    label = $"{label} ({account.Identity})";
                }
                AccountOptions.Add(new SelectOption(account.Id, label));
            }
        }

        if (!HasBackends)
        {
            return;
        }

        if (_isRemoteHostProject)
        {
            BackendOptions.Add(new SelectOption("", AppLangResources.Get(AppLangResourceKind.BackendFollowGlobal)));
            foreach (var provider in _providers)
            {
                var tag = ProviderProtocolNames.DisplayLabel(provider.Protocol);
                BackendOptions.Add(new SelectOption(
                    AgentBackendRefs.Tunnel(provider.Id),
                    $"⚡ {provider.Name} ({tag})"));
            }
            return;
        }

        if (_accounts.Count > 0)
        {
            foreach (var account in _accounts)
            {
                var label = account.IsDefaultOrigin && account.Label == "Default"
                    ? AppLangResources.Get(AppLangResourceKind.AccountDefaultName)
                    : account.Label;
                if (!string.IsNullOrWhiteSpace(account.Identity))
                {
                    label = $"{label} ({account.Identity})";
                }
                BackendOptions.Add(new SelectOption(AgentBackendRefs.Account(account.Id), label));
            }
        }
        else
        {
            BackendOptions.Add(new SelectOption("", AppLangResources.Get(AppLangResourceKind.AgentDefault)));
        }

        foreach (var provider in _providers)
        {
            var tag = ProviderProtocolNames.DisplayLabel(provider.Protocol);
            BackendOptions.Add(new SelectOption(
                AgentBackendRefs.Provider(provider.Id),
                $"{provider.Name} ({tag})"));
        }
    }

    private void RebuildModels()
    {
        Models.Clear();
        var query = ModelQuery.Trim();
        var language = AgentLanguage.Current;
        var activeProvider = ActiveProvider();

        Models.Add(new ModelOptionViewModel(null, AppLangResources.Get(AppLangResourceKind.AgentDefault), select: SelectModel)
        {
            IsSelected = SelectedModelId is null,
        });
        foreach (var option in _known)
        {
            if (query.Length > 0
                && !option.LabelFor(language).Contains(query, StringComparison.OrdinalIgnoreCase)
                && !option.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var label = option.LabelFor(language);
            var note = option.NoteFor(language);
            var iconKey = ModelIcons.KeyFor(option.Id, label, note, _definition.Id);
            Models.Add(new ModelOptionViewModel(option.Id, label, note, select: SelectModel, iconKey: iconKey)
            {
                IsSelected = option.Id == SelectedModelId,
            });
        }

        // Cross-provider model search: when user types a query, also match models from other compatible providers.
        if (query.Length > 0 && _providers.Count > 0)
        {
            foreach (var other in _providers)
            {
                if (activeProvider is not null && other.Id == activeProvider.Id) continue;
                var targetRef = BackendRefFor(other);
                var badge = _isRemoteHostProject ? $"⚡ {other.Name}" : other.Name;
                foreach (var spec in other.Models)
                {
                    if (!spec.Enabled) continue;
                    var display = string.IsNullOrWhiteSpace(spec.DisplayName) ? spec.Id : spec.DisplayName;
                    if (!display.Contains(query, StringComparison.OrdinalIgnoreCase)
                        && !spec.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                        && !other.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var iconKey = ModelIcons.KeyFor(spec.Id, display, null, _definition.Id);
                    Models.Add(new ModelOptionViewModel(
                        spec.Id,
                        display,
                        note: null,
                        select: SelectModel,
                        iconKey: iconKey,
                        switchBackendRef: targetRef,
                        sourceBadge: badge));
                }
            }
        }

        if (AllowCustomModel && query.Length > 0 && _known.All(o => !string.Equals(o.Id, query, StringComparison.OrdinalIgnoreCase)))
        {
            Models.Add(new ModelOptionViewModel(
                query,
                AppLangResources.Format(AppLangResourceKind.UseCustomModel, query),
                null,
                isCustom: true,
                select: SelectModel,
                iconKey: ModelIcons.KeyFor(query, fallbackAgentId: _definition.Id))
            {
                IsSelected = query == SelectedModelId,
            });
        }
    }

    private void RebuildEfforts()
    {
        Efforts.Clear();
        if (!HasEffort)
        {
            return;
        }
        Efforts.Add(new EffortChoiceViewModel("", AppLangResources.Get(AppLangResourceKind.EffortDefault)));
        var options = _compiler.EffortOptionsFor(_definition, SelectedModelId);

        // If a managed provider model explicitly restricts SupportedEfforts, narrow the effort chips accordingly.
        var activeProvider = ActiveProvider();
        var modelSpec = activeProvider?.Models.FirstOrDefault(m =>
            string.Equals(m.Id, SelectedModelId, StringComparison.OrdinalIgnoreCase));
        if (modelSpec is { SupportedEfforts.Count: > 0 })
        {
            var allowed = new HashSet<string>(modelSpec.SupportedEfforts, StringComparer.OrdinalIgnoreCase);
            options = options.Where(o => allowed.Contains(o.Id)).ToList();
        }

        foreach (var option in options)
        {
            Efforts.Add(new EffortChoiceViewModel(option.Id, EffortName(option)));
        }
        if (SelectedEffortId.Length > 0 && Efforts.All(e => e.Id != SelectedEffortId))
        {
            SelectedEffortId = "";
        }
    }

    private bool CanDiscover()
    {
        if (ActiveProvider() is not null || _isRemoteHostProject) return false;
        var discover = _definition.Capabilities.Model?.Discover;
        if (discover is null) return false;
        if (!string.Equals(discover.Kind, "command", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return _executable is not null;
    }

    private Dictionary<string, string> BuildDiscoveryEnvironmentOverrides()
    {
        var env = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (_definition.Accounts is { Env.Length: > 0 } accCap
            && !string.IsNullOrWhiteSpace(SelectedAccountId)
            && _accounts.FirstOrDefault(a => a.Id == SelectedAccountId) is { IsDefaultOrigin: false, Directory.Length: > 0 } chosen)
        {
            env[accCap.Env] = chosen.Directory;
        }
        foreach (var feature in _definition.Capabilities.Features)
        {
            if (string.IsNullOrEmpty(feature.Env)) continue;
            if (!_profile.Features.TryGetValue(feature.Id, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;
            if (feature.Type == "bool")
            {
                if (string.Equals(raw.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                {
                    env[feature.Env] = string.IsNullOrEmpty(feature.EnvValue) ? "true" : feature.EnvValue;
                }
            }
            else if (feature.Type == "list")
            {
                var items = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (items.Length > 0) env[feature.Env] = string.Join(',', items);
            }
            else
            {
                env[feature.Env] = raw;
            }
        }
        foreach (var (k, v) in _profile.Env)
        {
            env[k] = v;
        }
        return env;
    }

    private async Task DiscoverAsync()
    {
        var gen = Interlocked.Increment(ref _discoverGeneration);
        var envOverrides = BuildDiscoveryEnvironmentOverrides();
        IsDiscovering = true;
        try
        {
            var found = await Task.Run(() => _discovery.DiscoverAsync(
                _definition,
                _executable ?? "",
                environmentOverrides: envOverrides));
            UiContext.PostToUi(() =>
            {
                if (gen != _discoverGeneration || ActiveProvider() is not null) return;
                _known.Clear();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var option in found)
                {
                    if (seen.Add(option.Id)) _known.Add(option);
                }
                foreach (var option in _customModels)
                {
                    if (seen.Add(option.Id)) _known.Add(option);
                }
                if (_definition.Capabilities.Model is { } modelCap)
                {
                    foreach (var option in modelCap.Options)
                    {
                        if (seen.Add(option.Id)) _known.Add(option);
                    }
                }
                if (!string.IsNullOrEmpty(_profile.Model) && seen.Add(_profile.Model))
                {
                    _known.Add(new AgentOption { Id = _profile.Model });
                }
                RebuildModels();
            });
        }
        catch
        {
            // Discovery is a convenience; the built-in list stays.
        }
        finally
        {
            UiContext.PostToUi(() =>
            {
                if (gen == _discoverGeneration) IsDiscovering = false;
            });
        }
    }

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e) => UiContext.PostToUi(() =>
    {
        RebuildBackends();
        RebuildModels();
        RebuildEfforts();
        OnPropertyChanged(nameof(EffortCaption));
        OnPropertyChanged(nameof(PermissionName));
        OnPropertyChanged(nameof(PrimaryLaunchLabel));
        OnPropertyChanged(nameof(SecondaryLaunchLabel));
    });

    public void Dispose() => ArcLanguage.Changed -= OnLanguageChanged;

    private static string EffortName(AgentOption option) => option.Id switch
    {
        "low" => AppLangResources.Get(AppLangResourceKind.EffortLow),
        "medium" => AppLangResources.Get(AppLangResourceKind.EffortMedium),
        "high" => AppLangResources.Get(AppLangResourceKind.EffortHigh),
        "xhigh" => AppLangResources.Get(AppLangResourceKind.EffortXHigh),
        "max" => AppLangResources.Get(AppLangResourceKind.EffortMax),
        _ => option.Id,
    };

    private static string EffortCaptionOf(string? label) => label switch
    {
        null or "" or "Effort" => AppLangResources.Get(AppLangResourceKind.EffortLabel),
        "Reasoning effort" => AppLangResources.Get(AppLangResourceKind.ReasoningEffortLabel),
        _ => label,
    };

    public static string PermissionLabel(string id) => id switch
    {
        PermissionModes.Default => AppLangResources.Get(AppLangResourceKind.PermissionDefault),
        PermissionModes.AcceptEdits => AppLangResources.Get(AppLangResourceKind.PermissionAcceptEdits),
        PermissionModes.Plan => AppLangResources.Get(AppLangResourceKind.PermissionPlan),
        PermissionModes.Auto => AppLangResources.Get(AppLangResourceKind.PermissionAuto),
        PermissionModes.DontAsk => AppLangResources.Get(AppLangResourceKind.PermissionDontAsk),
        PermissionModes.Full => AppLangResources.Get(AppLangResourceKind.PermissionFull),
        _ => id,
    };
}
