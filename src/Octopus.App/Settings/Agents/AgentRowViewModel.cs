using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.Core.Agents;
using TKCode.Arc;

namespace Octopus.App.Settings.Agents;

/// <summary>
/// One installed agent in the list: the collapsed header (name, version, command preview, switches) and the details,
/// whose <see cref="Fields"/> are generated from the capability blocks of its definition. Every change goes through
/// <see cref="Mutate"/> into settings.json and then refreshes <see cref="CommandPreview"/>.
/// </summary>
public sealed partial class AgentRowViewModel : ObservableObject, IDisposable
{
    private readonly AgentServices _services;
    private CancellationTokenSource? _discoveryCts;
    private bool _discoveryStarted;
    private bool _disposed;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _commandPreview = "";

    [ObservableProperty]
    private bool _isCommandOverridden;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultButtonText))]
    private bool _isDefault;

    public AgentRowViewModel(AgentDefinition definition, AgentInstallation? installation, AgentServices services)
    {
        Definition = definition;
        Installation = installation;
        _services = services;
        AccountsSection = definition.Accounts is not null && services.Accounts is not null
            ? new AgentAccountsSectionViewModel(this, services, services.Accounts)
            : null;
        Fields = AgentFieldFactory.Create(this);
        RebuildBackendOptions();
        Refresh();
        services.Settings.Changed += OnSettingsChanged;
        services.Providers?.Changed += OnProvidersChanged;
        services.Accounts?.Changed += OnProvidersChanged;
    }

    public AgentDefinition Definition { get; }

    public AgentAccountsSectionViewModel? AccountsSection { get; }

    public bool HasAccountsSection => AccountsSection is not null;

    public bool HasProviderSupport => Definition.SupportsManagedProviders && _services.Providers is not null;

    public bool CanOpenProviders => _services.Navigator is not null;

    public ObservableCollection<SelectOption> BackendOptions { get; } = [];

    /// <summary>Selected default backend reference for this agent (empty = native default, account:&lt;id&gt;, or provider:&lt;id&gt;).</summary>
    public string SelectedBackendRef
    {
        get
        {
            var profile = Profile;
            if (!string.IsNullOrWhiteSpace(profile.DefaultBackend))
            {
                return profile.DefaultBackend!;
            }
            if (!string.IsNullOrWhiteSpace(profile.DefaultAccountId))
            {
                return AgentBackendRefs.Account(profile.DefaultAccountId!);
            }
            return "";
        }
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(normalized, SelectedBackendRef, StringComparison.Ordinal))
            {
                return;
            }
            Mutate(p =>
            {
                p.DefaultBackend = normalized;
                if (normalized is null)
                {
                    p.DefaultAccountId = null;
                }
                else if (AgentBackendRefs.TryParseAccount(normalized, out var accountId))
                {
                    p.DefaultAccountId = accountId;
                }
            });
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void OpenProvidersSettings() => _services.Navigator?.OpenSettings("providers");

    private void RebuildBackendOptions()
    {
        BackendOptions.Clear();
        if (!HasProviderSupport)
        {
            return;
        }
        BackendOptions.Add(new SelectOption("", AppLangResources.Get(AppLangResourceKind.BackendFollowGlobal)));
        if (Definition.Accounts is not null && _services.Accounts is { } accounts)
        {
            foreach (var acc in accounts.GetAccounts(Definition))
            {
                var label = acc.IsDefaultOrigin && acc.Label == "Default"
                    ? AppLangResources.Get(AppLangResourceKind.AccountDefaultName)
                    : acc.Label;
                if (!string.IsNullOrWhiteSpace(acc.Identity))
                {
                    label = $"{label} ({acc.Identity})";
                }
                BackendOptions.Add(new SelectOption(AgentBackendRefs.Account(acc.Id), label));
            }
        }
        if (_services.Providers is { } providers)
        {
            foreach (var provider in providers.ListCompatibleProviders(Definition))
            {
                var tag = ProviderProtocolNames.DisplayLabel(provider.Protocol);
                BackendOptions.Add(new SelectOption(
                    AgentBackendRefs.Provider(provider.Id),
                    $"{provider.Name} ({tag})"));
            }
        }
        OnPropertyChanged(nameof(SelectedBackendRef));
    }

    private void OnProvidersChanged(object? sender, EventArgs e) => UiContext.PostToUi(() =>
    {
        if (!_disposed)
        {
            RebuildBackendOptions();
            Refresh();
        }
    });

    /// <summary>"Custom" or "Modified" when the user has a definition file of this agent; null for a built-in one as shipped.</summary>
    public string? OriginBadge { get; init; }

    public bool HasOriginBadge => OriginBadge is not null;

    /// <summary>The user's file can be removed: the agent is deleted, or the built-in one restored (<see cref="RemoveText"/> says which).</summary>
    public bool CanRemove { get; init; }

    public string RemoveText { get; init; } = "";

    public bool CanEdit { get; init; }

    /// <summary>Where the agent was found; null when it was not (or the row is built without a scan).</summary>
    public AgentInstallation? Installation { get; }

    public IReadOnlyList<AgentFieldViewModel> Fields { get; }

    public string Id => Definition.Id;

    public string Name => Definition.Name;

    public string? Version => Installation?.Version;

    public bool HasVersion => !string.IsNullOrEmpty(Version);

    public bool IsUnverified => !Definition.Verified;

    public bool SupportsAcp => Definition.SupportsAcp;

    public string AcpBadgeText => Definition.Acp switch
    {
        null => AppLangResources.Get(AppLangResourceKind.SettingsAcpBadgeTerminalOnly),
        { Mode: "adapter" } => AppLangResources.Get(AppLangResourceKind.SettingsAcpBadgeAdapter),
        _ => AppLangResources.Get(AppLangResourceKind.SettingsAcpBadgeNative),
    };

    public string AcpCommandPreview
    {
        get
        {
            if (Definition.Acp is not { } acp) return "";
            var command = string.IsNullOrWhiteSpace(Profile.Command) ? DefaultCommandName : Profile.Command.Trim();
            if (string.Equals(acp.Mode, "adapter", StringComparison.OrdinalIgnoreCase) && acp.Adapter is { } adapter)
            {
                var bin = adapter.Executables.FirstOrDefault() ?? adapter.NpxPackage ?? adapter.UvxPackage ?? adapter.RegistryId ?? Definition.Id;
                var fallback = !string.IsNullOrWhiteSpace(adapter.NpxPackage) ? $" / npx {adapter.NpxPackage}" : "";
                var nativeHint = !string.IsNullOrWhiteSpace(adapter.NativeExecutableEnvVar) && !string.IsNullOrWhiteSpace(Profile.Command)
                    ? $" ({adapter.NativeExecutableEnvVar}={Profile.Command.Trim()})"
                    : "";
                return $"{bin}{fallback}{nativeHint} (dialect: {acp.Dialect})";
            }
            return $"{command} {string.Join(' ', acp.Args)} (dialect: {acp.Dialect})".Trim();
        }
    }

    /// <summary><c>follow</c>, <c>gui</c>, or <c>terminal</c>; per-agent surface override for ACP-capable agents.</summary>
    public string SurfaceKey
    {
        get => !SupportsAcp
            ? "terminal"
            : Profile.Surface switch
            {
                AgentSurface.Gui => "gui",
                AgentSurface.Terminal => "terminal",
                _ => "follow",
            };
        set
        {
            if (!SupportsAcp) return;
            AgentSurface? next = value switch
            {
                "gui" => AgentSurface.Gui,
                "terminal" => AgentSurface.Terminal,
                _ => null,
            };
            if (next != Profile.Surface)
            {
                Mutate(p => p.Surface = next);
            }
        }
    }

    public bool HasHomepage => !string.IsNullOrWhiteSpace(Definition.Homepage);

    public string DefaultButtonText => AppLangResources.Get(IsDefault ? AppLangResourceKind.SettingsClearDefault : AppLangResourceKind.SettingsSetDefault);

    internal IPathPicker PathPicker => _services.PathPicker;

    internal ChoiceFieldViewModel? ModelField { get; set; }

    internal ChoiceFieldViewModel? EffortField { get; set; }

    /// <summary>The profile as saved now; a new unsaved one when the agent was never configured.</summary>
    public AgentProfile Profile => _services.Settings.Current.Agents.ProfileOf(Id);

    /// <summary>The executable name the command field shows as its placeholder.</summary>
    public string DefaultCommandName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Installation?.Path))
            {
                return Path.GetFileName(Installation!.Path);
            }
            return Definition.Detect.Executables.FirstOrDefault() ?? Definition.Id;
        }
    }

    /// <summary><c>on</c> or <c>off</c>; the value of the enable switch (kept for API & test compatibility).</summary>
    public string EnabledKey
    {
        get => Profile.Enabled ? "on" : "off";
        set
        {
            var enabled = value == "on";
            if (enabled != Profile.Enabled)
            {
                Mutate(p => p.Enabled = enabled);
            }
        }
    }

    /// <summary>Whether this agent is enabled in the launcher and workspace.</summary>
    public bool IsEnabled
    {
        get => Profile.Enabled;
        set
        {
            if (value != Profile.Enabled)
            {
                Mutate(p => p.Enabled = value);
            }
        }
    }

    /// <summary>Changes the saved profile (creating it when this agent was never configured) and refreshes the preview.</summary>
    public void Mutate(Action<AgentProfile> change)
    {
        _services.Settings.Update(s =>
        {
            var profile = s.Agents.ProfileOf(Id);
            s.Agents.Profiles[Id] = profile;
            change(profile);
        });
        Refresh();
    }

    /// <summary>The permission mode a profile without its own would really use now (null when the agent has none).</summary>
    public string? EffectiveGlobalPermission() =>
        _services.Compiler.ResolvePermission(Definition, new AgentProfile(), _services.Settings.Current.Agents.DefaultPermission);

    public string EffectiveGlobalPermissionName()
    {
        var id = EffectiveGlobalPermission();
        if (id is null || Definition.Capabilities.Permission is not { } permission)
        {
            return "";
        }
        return AgentFieldFactory.ModeLabel(permission, id);
    }

    public IEnumerable<(string Id, string Label)> EffortItems() =>
        AgentFieldFactory.EffortItems(_services.Compiler.EffortOptionsFor(Definition, Profile.Model));

    /// <summary>The model changed: the effort levels it allows may be fewer, and a level it dropped goes back to default.</summary>
    internal void OnModelChanged() => EffortField?.SetItems(EffortItems());

    /// <summary>Recomputes the command preview and the flags that come from the settings.</summary>
    public void Refresh()
    {
        var agents = _services.Settings.Current.Agents;
        CommandPreview = _services.Compiler.Preview(
            new AgentCompileRequest(Definition, agents.ProfileOf(Id), null, agents.DefaultPermission, Installation?.Path));
        IsDefault = agents.DefaultAgentId == Id;
        IsCommandOverridden = !string.IsNullOrWhiteSpace(agents.ProfileOf(Id).Command);
        OnPropertyChanged(nameof(EnabledKey));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(SurfaceKey));
        OnPropertyChanged(nameof(AcpCommandPreview));
        OnPropertyChanged(nameof(SelectedBackendRef));
    }

    /// <summary>Writes every edit that is still waiting for typing to pause.</summary>
    public void FlushPending()
    {
        foreach (var field in Fields)
        {
            field.Flush();
        }
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ToggleDefault() =>
        _services.Settings.Update(s => s.Agents.DefaultAgentId = s.Agents.DefaultAgentId == Id ? null : Id);

    [RelayCommand]
    private void OpenHomepage()
    {
        if (HasHomepage)
        {
            _services.Opener.Open(Definition.Homepage!);
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        var states = _services.Settings.Current.Agents.ExpansionStates;
        if (!states.TryGetValue(Id, out var saved) || saved != value)
            _services.Settings.Update(s => s.Agents.ExpansionStates[Id] = value);
        if (value)
        {
            _ = DiscoverModelsAsync();
            if (Definition.Accounts is not null && _services.Accounts is { } accounts)
            {
                _ = accounts.RefreshAllIdentitiesAsync(Definition);
            }
        }
    }

    private async Task DiscoverModelsAsync()
    {
        var discover = Definition.Capabilities.Model?.Discover;
        if (_discoveryStarted || ModelField is null || discover is null)
        {
            return;
        }
        var isCommand = string.Equals(discover.Kind, "command", StringComparison.OrdinalIgnoreCase);
        if (isCommand && Installation is null)
        {
            return;
        }
        _discoveryStarted = true;
        _discoveryCts = new CancellationTokenSource();
        var envOverrides = BuildDiscoveryEnvironmentOverrides();
        try
        {
            var found = await Task.Run(
                () => _services.Discovery.DiscoverAsync(
                    Definition,
                    Installation?.Path ?? "",
                    false,
                    _discoveryCts.Token,
                    envOverrides),
                _discoveryCts.Token);
            if (found.Count == 0)
            {
                return;
            }
            UiContext.PostToUi(() =>
                ModelField.SetItems(AgentFieldFactory.ModelItems(Definition.Capabilities.Model!.Options, found)));
        }
        catch (Exception)
        {
            // Discovery is a convenience: when it fails the suggested models and the custom field are still there.
        }
    }

    private Dictionary<string, string> BuildDiscoveryEnvironmentOverrides()
    {
        var env = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var profile = Profile;
        foreach (var feature in Definition.Capabilities.Features)
        {
            if (string.IsNullOrEmpty(feature.Env)) continue;
            if (!profile.Features.TryGetValue(feature.Id, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;
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
        foreach (var (k, v) in profile.Env)
        {
            env[k] = v;
        }
        return env;
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => UiContext.Run(OnSettingsChangedOnUi);

    private void OnSettingsChangedOnUi()
    {
        Refresh();
        AccountsSection?.Rebuild();
        foreach (var field in Fields)
        {
            field.OnSettingsChanged();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _services.Settings.Changed -= OnSettingsChanged;
        _services.Providers?.Changed -= OnProvidersChanged;
        _services.Accounts?.Changed -= OnProvidersChanged;
        AccountsSection?.Dispose();
        _discoveryCts?.Cancel();
        _discoveryCts?.Dispose();
    }
}
