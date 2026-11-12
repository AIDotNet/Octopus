using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.Core.Agents;

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
        Fields = AgentFieldFactory.Create(this);
        Refresh();
        services.Settings.Changed += OnSettingsChanged;
    }

    public AgentDefinition Definition { get; }

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

    /// <summary><c>on</c> or <c>off</c>; the value of the enable switch.</summary>
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
        if (value)
        {
            _ = DiscoverModelsAsync();
        }
    }

    private async Task DiscoverModelsAsync()
    {
        if (_discoveryStarted || ModelField is null || Definition.Capabilities.Model?.Discover is null || Installation is null)
        {
            return;
        }
        _discoveryStarted = true;
        _discoveryCts = new CancellationTokenSource();
        try
        {
            var found = await Task.Run(
                () => _services.Discovery.DiscoverAsync(Definition, Installation.Path, false, _discoveryCts.Token),
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

    private void OnSettingsChanged(object? sender, EventArgs e) => UiContext.Run(OnSettingsChangedOnUi);

    private void OnSettingsChangedOnUi()
    {
        Refresh();
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
        _discoveryCts?.Cancel();
        _discoveryCts?.Dispose();
    }
}
