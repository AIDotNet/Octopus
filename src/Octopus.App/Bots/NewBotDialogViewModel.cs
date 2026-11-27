using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.App.Settings;
using Octopus.Core.Agents;
using Octopus.Core.Bots;
using Octopus.Remote.Client;
using Octopus.Storage;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.Localization;

namespace Octopus.App.Bots;

public sealed partial class BotColorOptionViewModel(string hex, bool isSelected, Action<string> select) : ObservableObject
{
    public string Hex { get; } = hex;
    public IBrush Brush { get; } = SolidColorBrush.Parse(hex);
    [ObservableProperty] public partial bool IsSelected { get; set; } = isSelected;

    [RelayCommand]
    private void Pick() => select(Hex);
}

public sealed partial class BotTemplateOptionViewModel(
    BotTemplateDefinition template,
    string label,
    LucideIconKind icon,
    bool isSelected,
    Action<BotTemplateDefinition> select) : ObservableObject
{
    public BotTemplateDefinition Template { get; } = template;
    public string Id => Template.Id;
    public string Label { get; } = label;
    public LucideIconKind Icon { get; } = icon;
    [ObservableProperty] public partial bool IsSelected { get; set; } = isSelected;

    [RelayCommand]
    private void Pick() => select(Template);
}

/// <summary>ViewModel for the "New Bot" creation dialog: identity, SOUL.md summary, host computer, agent, default model, and starter template.</summary>
public sealed partial class NewBotDialogViewModel : ObservableObject, IDisposable
{
    private readonly IAgentCatalog _catalog;
    private readonly IAgentScanner _scanner;
    private readonly ISettingsStore _settings;
    private readonly RemoteHostConnections? _remoteHosts;
    private readonly IPickerService? _pickers;
    private readonly IProjectIconStore? _icons;
    private readonly IModelDiscovery? _modelDiscovery;
    private readonly Dictionary<string, IReadOnlyList<AgentOption>> _discoveredModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _draftId = Guid.NewGuid().ToString("N");
    private readonly List<string> _createdImages = [];
    private readonly bool _initialized;
    private bool _rebuildingAgents;
    private bool _disposed;
    private CancellationTokenSource? _modelDiscoveryCts;
    private BotTemplateDefinition _selectedTemplate;

    public NewBotDialogViewModel(
        IAgentCatalog catalog,
        IAgentScanner scanner,
        ISettingsStore settings,
        string? initialTemplateId = null,
        IReadOnlyList<SelectOption>? hostOptions = null,
        string? initialHostKey = null,
        RemoteHostConnections? remoteHosts = null,
        IPickerService? pickers = null,
        IProjectIconStore? icons = null,
        IModelDiscovery? modelDiscovery = null)
    {
        _catalog = catalog;
        _scanner = scanner;
        _settings = settings;
        _remoteHosts = remoteHosts;
        _pickers = pickers;
        _icons = icons;
        _modelDiscovery = modelDiscovery;

        _selectedTemplate = BotCatalog.FindTemplate(initialTemplateId);
        SelectedColor = _selectedTemplate.Color;
        var isZh = IsChinese();
        Name = _selectedTemplate.Id == "blank" ? "" : (isZh ? _selectedTemplate.DefaultName : _selectedTemplate.EnglishName);
        Soul = isZh ? _selectedTemplate.DefaultSoulZh : _selectedTemplate.DefaultSoulEn;

        foreach (var hex in BotCatalog.AvatarPalette)
        {
            Colors.Add(new BotColorOptionViewModel(hex, string.Equals(hex, SelectedColor, StringComparison.OrdinalIgnoreCase), SelectColor));
        }

        // Host options (local computer + paired Octopus hosts + SSH hosts)
        if (hostOptions is { Count: > 0 })
        {
            HostOptions = hostOptions;
        }
        else
        {
            HostOptions = [new SelectOption("local", AppLangResources.Get(AppLangResourceKind.NewProjectLocalMachine))];
        }
        SelectedHostKey = !string.IsNullOrWhiteSpace(initialHostKey)
            && HostOptions.Any(h => string.Equals(h.Value?.ToString(), initialHostKey, StringComparison.Ordinal))
                ? initialHostKey
                : HostOptions[0].Value?.ToString() ?? "local";

        RebuildAgentOptions();
        _initialized = true;

        foreach (var tpl in BotCatalog.Templates)
        {
            Templates.Add(new BotTemplateOptionViewModel(
                tpl,
                TemplateLabel(tpl.Id),
                ParseIcon(tpl.IconName, LucideIconKind.Sparkles),
                tpl.Id == _selectedTemplate.Id,
                SelectTemplate));
        }
    }

    public ObservableCollection<BotColorOptionViewModel> Colors { get; } = [];
    public ObservableCollection<BotTemplateOptionViewModel> Templates { get; } = [];
    public IReadOnlyList<SelectOption> HostOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAgents))]
    public partial IReadOnlyList<SelectOption> AgentOptions { get; private set; } = [];

    public bool HasAgents => AgentOptions.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelOptions))]
    public partial IReadOnlyList<SelectOption> ModelOptions { get; private set; } = [];

    public bool HasModelOptions => ModelOptions.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvatarInitial))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvatarBrush))]
    public partial string SelectedColor { get; set; } = "#5b5bd6";

    /// <summary>Thumbnail file name in the project icon store; null when the avatar uses the color + initial.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvatarImage), nameof(HasAvatarImage))]
    public partial string? SelectedImage { get; set; }

    public Bitmap? AvatarImage => _icons?.Load(SelectedImage);
    public bool HasAvatarImage => AvatarImage is not null;
    public bool CanPickImage => _pickers is not null && _icons is not null;

    [ObservableProperty] public partial string SelectedHostKey { get; set; } = "local";
    [ObservableProperty] public partial string SelectedAgentId { get; set; } = "";
    [ObservableProperty] public partial string SelectedModelId { get; set; } = "";
    [ObservableProperty] public partial string Soul { get; set; } = "";

    partial void OnSelectedHostKeyChanged(string value)
    {
        if (_initialized)
        {
            RebuildAgentOptions();
        }
    }

    partial void OnSelectedAgentIdChanged(string value)
    {
        if (_initialized && !_rebuildingAgents)
        {
            RebuildModelOptions(resetSelection: true, triggerDiscovery: true);
        }
    }

    private void RebuildAgentOptions()
    {
        var prevAgentId = SelectedAgentId;
        var options = new List<SelectOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? preferredDefault = null;

        if (SelectedHostKey.StartsWith("remote:", StringComparison.Ordinal) && _remoteHosts is not null)
        {
            var peerId = SelectedHostKey["remote:".Length..];
            foreach (var agent in _remoteHosts.AgentsOf(peerId))
            {
                if (!seen.Add(agent.Id))
                {
                    continue;
                }
                options.Add(new SelectOption(agent.Id, agent.Name));
                if (agent.IsDefault && preferredDefault is null)
                {
                    preferredDefault = agent.Id;
                }
            }
        }

        if (options.Count == 0)
        {
            var agentSettings = _settings.Current.Agents;
            preferredDefault = agentSettings.DefaultAgentId;
            foreach (var inst in _scanner.Current.Installations)
            {
                if (_catalog.Find(inst.AgentId) is { } def
                    && agentSettings.ProfileOf(def.Id).Enabled
                    && seen.Add(def.Id))
                {
                    options.Add(new SelectOption(def.Id, def.Name));
                }
            }

            // SSH hosts are not scanned locally, so fall back to enabled catalog agents for them.
            if (SelectedHostKey.StartsWith("ssh:", StringComparison.Ordinal))
            {
                foreach (var def in _catalog.Definitions)
                {
                    if (agentSettings.ProfileOf(def.Id).Enabled && seen.Add(def.Id))
                    {
                        options.Add(new SelectOption(def.Id, def.Name));
                    }
                }
            }
        }

        _rebuildingAgents = true;
        try
        {
            AgentOptions = options;
            SelectedAgentId = PickSelectedAgentId(options, SelectedAgentId, preferredDefault);
        }
        finally
        {
            _rebuildingAgents = false;
        }

        var agentChanged = !string.Equals(prevAgentId, SelectedAgentId, StringComparison.OrdinalIgnoreCase);
        RebuildModelOptions(resetSelection: agentChanged, triggerDiscovery: true);
    }

    private void RebuildModelOptions(bool resetSelection, bool triggerDiscovery)
    {
        var agentId = SelectedAgentId;
        var definition = string.IsNullOrWhiteSpace(agentId) ? null : _catalog.Find(agentId);
        var profile = definition is null ? null : _settings.Current.Agents.ProfileOf(definition.Id);
        var discovered = definition is not null && _discoveredModels.TryGetValue(definition.Id, out var cached) ? cached : null;
        var currentModel = resetSelection ? null : SelectedModelId;

        var options = BuildModelSelectOptions(definition, profile, discovered, currentModel);
        ModelOptions = options;
        if (options.Count == 0 || resetSelection)
        {
            SelectedModelId = "";
        }
        else if (!string.IsNullOrWhiteSpace(currentModel)
            && options.FirstOrDefault(o => string.Equals(o.Value?.ToString(), currentModel, StringComparison.OrdinalIgnoreCase)) is { Value: not null } existing)
        {
            SelectedModelId = existing.Value.ToString()!;
        }
        else
        {
            SelectedModelId = "";
        }

        if (triggerDiscovery)
        {
            StartModelDiscovery(definition, profile);
        }
    }

    private void StartModelDiscovery(AgentDefinition? definition, AgentProfile? profile)
    {
        _modelDiscoveryCts?.Cancel();
        _modelDiscoveryCts?.Dispose();
        _modelDiscoveryCts = null;

        if (_disposed || _modelDiscovery is null
            || !CanDiscoverModels(definition, _scanner, SelectedHostKey, out var executable))
        {
            return;
        }

        var def = definition!;
        var hostKey = SelectedHostKey;
        var cts = new CancellationTokenSource();
        _modelDiscoveryCts = cts;
        var ct = cts.Token;
        var envOverrides = BuildDiscoveryEnvironmentOverrides(def, profile);

        _ = DiscoverModelsCoreAsync(def, executable, hostKey, envOverrides, ct);
    }

    private async Task DiscoverModelsCoreAsync(
        AgentDefinition definition,
        string executable,
        string hostKey,
        IReadOnlyDictionary<string, string> envOverrides,
        CancellationToken ct)
    {
        try
        {
            var found = await Task.Run(
                () => _modelDiscovery!.DiscoverAsync(definition, executable, false, ct, envOverrides),
                ct);
            if (found.Count == 0 || ct.IsCancellationRequested || _disposed)
            {
                return;
            }
            UiContext.Run(() =>
            {
                if (ct.IsCancellationRequested || _disposed
                    || !string.Equals(SelectedHostKey, hostKey, StringComparison.Ordinal)
                    || !string.Equals(SelectedAgentId, definition.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                _discoveredModels[definition.Id] = found;
                RebuildModelOptions(resetSelection: false, triggerDiscovery: false);
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch
        {
            // Model discovery is best-effort; built-in catalog options remain available.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _modelDiscoveryCts?.Cancel();
        _modelDiscoveryCts?.Dispose();
        _modelDiscoveryCts = null;
    }

    internal static IReadOnlyList<SelectOption> BuildModelSelectOptions(
        AgentDefinition? definition,
        AgentProfile? profile,
        IReadOnlyList<AgentOption>? discovered,
        string? currentModel)
    {
        if (definition?.Capabilities.Model is not { } modelCap)
        {
            return [];
        }

        var language = AgentLanguage.Current;
        var options = new List<SelectOption>
        {
            new("", AppLangResources.Get(AppLangResourceKind.BotSettingsModelFollowAgent)),
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (discovered is not null)
        {
            foreach (var opt in discovered)
            {
                if (!string.IsNullOrWhiteSpace(opt.Id) && seen.Add(opt.Id))
                {
                    options.Add(new SelectOption(opt.Id, opt.LabelFor(language)));
                }
            }
        }

        foreach (var opt in modelCap.Options)
        {
            if (!string.IsNullOrWhiteSpace(opt.Id) && seen.Add(opt.Id))
            {
                options.Add(new SelectOption(opt.Id, opt.LabelFor(language)));
            }
        }

        if (!string.IsNullOrWhiteSpace(profile?.Model))
        {
            var profileModel = profile.Model.Trim();
            if (seen.Add(profileModel))
            {
                options.Add(new SelectOption(profileModel, profileModel));
            }
        }

        if (!string.IsNullOrWhiteSpace(currentModel))
        {
            var savedModel = currentModel.Trim();
            if (seen.Add(savedModel))
            {
                options.Add(new SelectOption(savedModel, savedModel));
            }
        }

        return options;
    }

    internal static bool CanDiscoverModels(
        AgentDefinition? definition,
        IAgentScanner? scanner,
        string? hostDeviceKey,
        out string executable)
    {
        executable = "";
        if (!string.IsNullOrWhiteSpace(hostDeviceKey)
            && !string.Equals(hostDeviceKey, "local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var discover = definition?.Capabilities.Model?.Discover;
        if (definition is null || discover is null)
        {
            return false;
        }
        executable = scanner?.Find(definition.Id)?.Path ?? "";
        if (string.Equals(discover.Kind, "command", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }
        return true;
    }

    internal static Dictionary<string, string> BuildDiscoveryEnvironmentOverrides(AgentDefinition definition, AgentProfile? profile)
    {
        var env = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (profile is null)
        {
            return env;
        }
        foreach (var feature in definition.Capabilities.Features)
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

    private static string PickSelectedAgentId(IReadOnlyList<SelectOption> options, string? currentId, string? preferredDefault)
    {
        if (options.Count == 0)
        {
            return "";
        }
        if (!string.IsNullOrWhiteSpace(currentId)
            && options.FirstOrDefault(o => string.Equals(o.Value?.ToString(), currentId, StringComparison.OrdinalIgnoreCase)) is { Value: not null } existing)
        {
            return existing.Value.ToString()!;
        }
        if (!string.IsNullOrWhiteSpace(preferredDefault)
            && options.FirstOrDefault(o => string.Equals(o.Value?.ToString(), preferredDefault, StringComparison.OrdinalIgnoreCase)) is { Value: not null } preferred)
        {
            return preferred.Value.ToString()!;
        }
        return options[0].Value?.ToString() ?? "";
    }

    public IBrush AvatarBrush => SolidColorBrush.Parse(SelectedColor);

    public string AvatarInitial
    {
        get
        {
            var source = string.IsNullOrWhiteSpace(Name)
                ? AppLangResources.Get(AppLangResourceKind.BotDefaultName)
                : Name.Trim();
            return source.Length > 0 ? source[..1].ToUpperInvariant() : "B";
        }
    }

    public NewBotRequest BuildRequest() => new(
        string.IsNullOrWhiteSpace(Name) ? AppLangResources.Get(AppLangResourceKind.BotDefaultName) : Name.Trim(),
        SelectedAgentId,
        SelectedColor,
        _selectedTemplate.Id,
        string.IsNullOrWhiteSpace(Soul) ? null : Soul.Trim(),
        SelectedHostKey,
        HasAvatarImage ? SelectedImage : null,
        HasModelOptions && !string.IsNullOrWhiteSpace(SelectedModelId) ? SelectedModelId.Trim() : null);

    [RelayCommand]
    private async Task PickImageAsync()
    {
        if (_pickers is null || _icons is null) return;
        var path = await _pickers.PickImageAsync();
        if (string.IsNullOrWhiteSpace(path)) return;
        var fileName = await _icons.ImportAsync("bot-" + _draftId, path);
        if (string.IsNullOrWhiteSpace(fileName)) return;
        _createdImages.Add(fileName);
        SelectedImage = fileName;
    }

    [RelayCommand]
    private void ClearImage() => SelectedImage = null;

    /// <summary>Deletes thumbnails imported while the dialog was open, except the one the new Bot keeps.</summary>
    public void DiscardUnusedImages(string? keptImage)
    {
        if (_icons is null) return;
        foreach (var file in _createdImages)
        {
            if (!string.Equals(file, keptImage, StringComparison.Ordinal))
            {
                _icons.Delete(file);
            }
        }
    }

    private void SelectColor(string hex)
    {
        SelectedColor = hex;
        foreach (var c in Colors) c.IsSelected = string.Equals(c.Hex, hex, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectTemplate(BotTemplateDefinition next)
    {
        var isZh = IsChinese();
        var prevName = _selectedTemplate.Id == "blank" ? "" : (isZh ? _selectedTemplate.DefaultName : _selectedTemplate.EnglishName);
        var prevSoul = isZh ? _selectedTemplate.DefaultSoulZh : _selectedTemplate.DefaultSoulEn;
        var nextName = next.Id == "blank" ? "" : (isZh ? next.DefaultName : next.EnglishName);
        var nextSoul = isZh ? next.DefaultSoulZh : next.DefaultSoulEn;

        if (string.IsNullOrWhiteSpace(Name) || string.Equals(Name.Trim(), prevName, StringComparison.Ordinal))
        {
            Name = nextName;
        }
        if (string.IsNullOrWhiteSpace(Soul) || string.Equals(Soul.Trim(), prevSoul, StringComparison.Ordinal))
        {
            Soul = nextSoul;
        }
        SelectColor(next.Color);
        _selectedTemplate = next;
        foreach (var t in Templates) t.IsSelected = t.Id == next.Id;
    }

    private static bool IsChinese() =>
        ArcLanguage.Current.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public static string TemplateLabel(string templateId) => templateId switch
    {
        "pr" => AppLangResources.Get(AppLangResourceKind.BotTemplatePr),
        "ci" => AppLangResources.Get(AppLangResourceKind.BotTemplateCi),
        "daily" => AppLangResources.Get(AppLangResourceKind.BotTemplateDaily),
        _ => AppLangResources.Get(AppLangResourceKind.BotTemplateBlank),
    };

    public static IBrush AgentAccent(string? agentId) => (agentId ?? "").ToLowerInvariant() switch
    {
        "claude" => SolidColorBrush.Parse("#d97757"),
        "codex" => SolidColorBrush.Parse("#1f9d74"),
        "gemini" or "antigravity" or "agy" => SolidColorBrush.Parse("#3b7de9"),
        "opencode" => SolidColorBrush.Parse("#8e4ec6"),
        _ => SolidColorBrush.Parse("#5b5bd6"),
    };

    public static LucideIconKind ParseIcon(string? name, LucideIconKind fallback) =>
        !string.IsNullOrWhiteSpace(name) && Enum.TryParse<LucideIconKind>(name, ignoreCase: true, out var kind)
            ? kind
            : fallback;
}
