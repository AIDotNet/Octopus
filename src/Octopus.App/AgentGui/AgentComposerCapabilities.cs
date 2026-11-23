using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Acp.Dialects;
using Octopus.Acp.Protocol;
using Octopus.App.Common;
using Octopus.App.Launcher;
using Octopus.App.Localization;
using Octopus.App.Settings;
using Octopus.Core.Agents;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.AgentGui;

/// <summary>One selectable slash command shown in the composer's <c>/</c> popup.</summary>
public sealed partial class SlashCommandSuggestion(string name, string description, string? inputHint, bool isSkill) : ObservableObject
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string? InputHint { get; } = inputHint;
    public bool IsSkill { get; } = isSkill;
    public string DisplayName => $"/{Name}";

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>One workspace file shown in the composer's <c>@</c> mention popup.</summary>
public sealed partial class FileMentionSuggestion(string relativePath, string fullPath) : ObservableObject
{
    public string RelativePath { get; } = relativePath;
    public string FullPath { get; } = fullPath;

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>One selectable item displayed inside a composer popover picker (Mode, Permission, Model, or Effort).</summary>
public sealed partial class ComposerChoiceItem : ObservableObject
{
    private readonly Action<string> _select;

    public ComposerChoiceItem(
        string id,
        string label,
        Action<string> select,
        string? description = null,
        string? meta = null,
        LucideIconKind? icon = null,
        bool isDanger = false)
    {
        Id = id;
        Label = label;
        Description = description;
        Meta = meta;
        Icon = icon;
        IsDanger = isDanger;
        _select = select;
    }

    public string Id { get; }
    public string Label { get; }
    public string? Description { get; }
    public string? Meta { get; }
    public LucideIconKind? Icon { get; }
    public bool HasIcon => Icon.HasValue;
    public LucideIconKind DisplayIcon => Icon ?? LucideIconKind.Circle;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasMeta => !string.IsNullOrWhiteSpace(Meta);
    public bool IsDanger { get; }

    [ObservableProperty] public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Select() => _select(Id);
}

/// <summary>
/// Reactive capability model for the compact Style A composer.
/// Every button, selector, and toggle in the input box is dynamically computed from the current agent's catalog
/// definition (<see cref="AgentDefinition"/>) combined with live ACP session capabilities and config options.
/// Once an ACP session completes <c>initialize</c>, only controls genuinely backed by the live session remain shown.
/// </summary>
public sealed partial class AgentComposerCapabilities : ObservableObject
{
    private readonly AgentDefinition _definition;
    private readonly IAcpDialect _dialect;
    private readonly Func<string, Task>? _onModeChanged;
    private readonly Func<string, Task>? _onPermissionChanged;
    private readonly Func<string, Task>? _onModelChanged;
    private readonly Func<string, string, Task>? _onConfigSelectChanged;
    private readonly Func<string, bool, Task>? _onConfigBoolChanged;

    private string? _modeConfigId;
    private string? _modelConfigId;
    private string? _effortConfigId;
    private string? _fastConfigId;
    private string? _thinkingConfigId;
    private bool _sessionInitialized;
    private bool _hasLiveEffort;
    private bool _syncing;

    public AgentComposerCapabilities(
        AgentDefinition definition,
        IAcpDialect dialect,
        string initialPermissionMode,
        string? initialModelId,
        string? initialEffortId,
        bool? initialFast,
        Func<string, Task>? onModeChanged = null,
        Func<string, Task>? onPermissionChanged = null,
        Func<string, Task>? onModelChanged = null,
        Func<string, string, Task>? onConfigSelectChanged = null,
        Func<string, bool, Task>? onConfigBoolChanged = null)
    {
        _definition = definition;
        _dialect = dialect;
        _onModeChanged = onModeChanged;
        _onPermissionChanged = onPermissionChanged;
        _onModelChanged = onModelChanged;
        _onConfigSelectChanged = onConfigSelectChanged;
        _onConfigBoolChanged = onConfigBoolChanged;

        _syncing = true;
        SelectedPermissionId = initialPermissionMode;
        SelectedModelId = initialModelId;
        SelectedEffortId = initialEffortId;
        FastMode = initialFast ?? false;
        InitializeFromCatalog();
        _syncing = false;
    }

    // Dynamic input-action capabilities
    [ObservableProperty] public partial bool SupportsSlashCommands { get; private set; }
    [ObservableProperty] public partial bool SupportsFileMentions { get; private set; } = true;
    [ObservableProperty] public partial bool SupportsImageAttachments { get; private set; }

    // Dynamic toolbar selectors (shown ONLY when the active agent supports them)
    [ObservableProperty] public partial bool ShowModeSelector { get; private set; }
    [ObservableProperty] public partial bool ShowPermissionSelector { get; private set; }
    [ObservableProperty] public partial bool ShowModelSelector { get; private set; }
    [ObservableProperty] public partial bool ShowEffortSelector { get; private set; }
    [ObservableProperty] public partial bool ShowFastToggle { get; private set; }
    [ObservableProperty] public partial bool ShowThinkingToggle { get; private set; }

    // Option collections (kept as SelectOption for API/test compatibility alongside rich popover choices)
    [ObservableProperty] public partial IReadOnlyList<SelectOption> ModeOptions { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<SelectOption> PermissionOptions { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<SelectOption> ModelOptions { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<SelectOption> EffortOptions { get; private set; } = [];

    // Rich popover choice lists
    public ObservableCollection<ComposerChoiceItem> ModeChoices { get; } = [];
    public ObservableCollection<ComposerChoiceItem> PermissionChoices { get; } = [];
    public ObservableCollection<ComposerChoiceItem> ModelChoices { get; } = [];
    public ObservableCollection<ComposerChoiceItem> FilteredModelChoices { get; } = [];
    public ObservableCollection<ComposerChoiceItem> EffortChoices { get; } = [];

    // Popover open states & model filter query
    [ObservableProperty] public partial bool IsModePickerOpen { get; set; }
    [ObservableProperty] public partial bool IsPermissionPickerOpen { get; set; }
    [ObservableProperty] public partial bool IsModelPickerOpen { get; set; }
    [ObservableProperty] public partial bool IsEffortPickerOpen { get; set; }
    [ObservableProperty] public partial string ModelSearchQuery { get; set; } = "";

    // Selected values
    [ObservableProperty] public partial string? SelectedModeId { get; set; }
    [ObservableProperty] public partial string? SelectedPermissionId { get; set; }
    [ObservableProperty] public partial string? SelectedModelId { get; set; }
    [ObservableProperty] public partial string? SelectedEffortId { get; set; }
    [ObservableProperty] public partial bool FastMode { get; set; }
    [ObservableProperty] public partial bool ThinkingEnabled { get; set; } = true;

    public ObservableCollection<SlashCommandSuggestion> AllCommands { get; } = [];
    public ObservableCollection<string> CapabilityBadges { get; } = [];

    public bool LinksFileMentions => _dialect.LinksFileMentions;
    public bool ShowModelSearch => ModelChoices.Count > 6;
    public bool HasFilteredModels => FilteredModelChoices.Count > 0;

    public string SelectedModeLabel =>
        ModeChoices.FirstOrDefault(c => c.Id == SelectedModeId)?.Label
        ?? AppLangResources.Get(AppLangResourceKind.AgentGuiModeLabel);

    public string SelectedPermissionLabel =>
        PermissionChoices.FirstOrDefault(c => c.Id == SelectedPermissionId)?.Label
        ?? AppLangResources.Get(AppLangResourceKind.AgentGuiPermissionLabel);

    public bool IsPermissionDanger => string.Equals(SelectedPermissionId, PermissionModes.Full, StringComparison.OrdinalIgnoreCase);

    public LucideIconKind PermissionIcon => PermissionIconFor(SelectedPermissionId);

    public string SelectedModelLabel =>
        ModelChoices.FirstOrDefault(c => c.Id == SelectedModelId)?.Label
        ?? (!string.IsNullOrWhiteSpace(SelectedModelId) ? SelectedModelId! : AppLangResources.Get(AppLangResourceKind.AgentGuiModelLabel));

    public string SelectedEffortLabel =>
        EffortChoices.FirstOrDefault(c => c.Id == SelectedEffortId)?.Label
        ?? AppLangResources.Get(AppLangResourceKind.AgentGuiEffortLabel);

    [RelayCommand]
    private void ToggleFast() => FastMode = !FastMode;

    [RelayCommand]
    private void ToggleThinking() => ThinkingEnabled = !ThinkingEnabled;

    /// <summary>Seeds composer controls from static catalog metadata before the ACP handshake completes.</summary>
    private void InitializeFromCatalog()
    {
        var permModes = new List<(string Id, string Label)>();
        if (_definition.Acp?.PermissionModes is { Count: > 0 } acpPerms)
        {
            foreach (var key in PermissionModes.All)
            {
                if (acpPerms.ContainsKey(key))
                {
                    permModes.Add((key, AgentLaunchViewModel.PermissionLabel(key)));
                }
            }
        }
        else if (_definition.Capabilities.Permission is { Modes.Count: > 0 } capPerm)
        {
            foreach (var key in PermissionModes.All)
            {
                if (capPerm.Modes.TryGetValue(key, out var spec))
                {
                    var label = spec.LabelFor(AgentLanguage.Current);
                    permModes.Add((key, string.IsNullOrWhiteSpace(label) ? AgentLaunchViewModel.PermissionLabel(key) : label));
                }
            }
        }
        else
        {
            permModes.Add((PermissionModes.Default, AgentLaunchViewModel.PermissionLabel(PermissionModes.Default)));
            permModes.Add((PermissionModes.AcceptEdits, AgentLaunchViewModel.PermissionLabel(PermissionModes.AcceptEdits)));
            permModes.Add((PermissionModes.Full, AgentLaunchViewModel.PermissionLabel(PermissionModes.Full)));
        }

        PermissionOptions = [.. permModes.Select(p => new SelectOption(p.Id, p.Label))];
        ShowPermissionSelector = permModes.Count > 1;
        if (permModes.Count > 0 && permModes.All(o => o.Id != SelectedPermissionId))
        {
            SelectedPermissionId = permModes[0].Id;
        }
        RebuildPermissionChoices(permModes);

        if (_definition.Capabilities.Model is { Options.Count: > 0 } catModel)
        {
            var lang = AgentLanguage.Current;
            ModelOptions = [.. catModel.Options.Select(o => new SelectOption(o.Id, o.LabelFor(lang)))];
            ShowModelSelector = ModelOptions.Count > 0;
            if (string.IsNullOrEmpty(SelectedModelId) && ModelOptions.Count > 0)
            {
                SelectedModelId = ModelOptions[0].Value?.ToString();
            }
            RebuildModelChoices(catModel.Options.Select(o => (o.Id, o.LabelFor(lang), (string?)null, o.Note)));
        }

        RebuildCatalogEffortsForModel(SelectedModelId);
        ShowFastToggle = _definition.Capabilities.Fast is not null;
        RebuildCapabilityBadges();
    }

    private void RebuildCatalogEffortsForModel(string? modelId)
    {
        if (_hasLiveEffort || _definition.Capabilities.Effort is not { Options.Count: > 0 } catEffort)
        {
            return;
        }
        IEnumerable<AgentOption> filtered = catEffort.Options;
        if (!string.IsNullOrWhiteSpace(modelId) && catEffort.PerModel is { Count: > 0 } perModel)
        {
            if (perModel.TryGetValue(modelId, out var allowed))
            {
                var set = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
                filtered = catEffort.Options.Where(o => set.Contains(o.Id));
            }
        }
        var items = filtered.ToList();
        EffortOptions = [.. items.Select(o => new SelectOption(o.Id, o.DisplayLabel))];
        ShowEffortSelector = EffortOptions.Count > 0;
        if (ShowEffortSelector && !string.IsNullOrEmpty(SelectedEffortId)
            && EffortOptions.All(o => o.Value?.ToString() != SelectedEffortId))
        {
            SelectedEffortId = EffortOptions[0].Value?.ToString();
        }
        RebuildEffortChoices(items.Select(o => (o.Id, o.DisplayLabel, (string?)null)));
    }

    /// <summary>Updates all dynamic composer controls from live ACP session state.</summary>
    public void UpdateFromSession(
        AcpInitializeResult? initialized,
        AcpSessionModeState? modes,
        AcpSessionModelState? models,
        IReadOnlyList<AcpSessionConfigOption> configOptions,
        IReadOnlyList<AcpAvailableCommand> commands,
        bool preserveConfiguredPermission = false)
    {
        _syncing = true;
        try
        {
            if (initialized is not null) _sessionInitialized = true;
            var configuredPerm = SelectedPermissionId;
            SupportsImageAttachments = initialized?.AgentCapabilities.PromptCapabilities.Image == true
                && !_dialect.DropsPromptImages;

            AllCommands.Clear();
            foreach (var cmd in commands)
            {
                AllCommands.Add(new SlashCommandSuggestion(cmd.Name, cmd.Description, cmd.InputHint, cmd.IsSkill));
            }
            SupportsSlashCommands = AllCommands.Count > 0;

            _modeConfigId = null;
            _modelConfigId = null;
            _effortConfigId = null;
            _fastConfigId = null;
            _thinkingConfigId = null;

            AcpSessionConfigOption? modeOpt = null;
            AcpSessionConfigOption? modelOpt = null;
            AcpSessionConfigOption? effortOpt = null;
            AcpSessionConfigOption? fastOpt = null;
            AcpSessionConfigOption? thinkingOpt = null;

            foreach (var opt in configOptions)
            {
                if (string.Equals(opt.Category, "mode", StringComparison.OrdinalIgnoreCase))
                {
                    modeOpt ??= opt;
                }
                else if (string.Equals(opt.Category, "model", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(opt.Category, "model_config", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(opt.Id, "model", StringComparison.OrdinalIgnoreCase))
                {
                    modelOpt ??= opt;
                }
                else if (IsEffortOption(opt))
                {
                    effortOpt ??= opt;
                }
                if (_dialect.FastConfigId is { } fastId && string.Equals(opt.Id, fastId, StringComparison.OrdinalIgnoreCase))
                {
                    fastOpt = opt;
                }
                if (_dialect.ThinkingConfigId is { } thinkId && string.Equals(opt.Id, thinkId, StringComparison.OrdinalIgnoreCase))
                {
                    thinkingOpt = opt;
                }
            }

            var permValues = _definition.Acp?.PermissionModes?.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (modeOpt is { Options.Count: > 1 })
            {
                var allModesArePermission = permValues is { Count: > 0 }
                    && modeOpt.Options.All(m => permValues.Contains(m.Value));
                if (allModesArePermission)
                {
                    ShowModeSelector = false;
                    ModeOptions = [];
                    ModeChoices.Clear();
                    if (!preserveConfiguredPermission || string.IsNullOrEmpty(configuredPerm))
                    {
                        var matched = _definition.Acp!.PermissionModes!
                            .FirstOrDefault(kv => string.Equals(kv.Value, modeOpt.CurrentValue, StringComparison.OrdinalIgnoreCase)).Key;
                        if (!string.IsNullOrEmpty(matched)) SelectedPermissionId = matched;
                    }
                }
                else
                {
                    _modeConfigId = modeOpt.Id;
                    ModeOptions = [.. modeOpt.Options.Select(o => new SelectOption(o.Value, o.Name))];
                    SelectedModeId = modeOpt.CurrentValue ?? modeOpt.Options[0].Value;
                    ShowModeSelector = true;
                    RebuildModeChoices(modeOpt.Options.Select(o => (o.Value, o.Name, o.Description)));
                }
            }
            else if (modes is { AvailableModes.Count: > 1 })
            {
                var allModesArePermission = permValues is { Count: > 0 }
                    && modes.AvailableModes.All(m => permValues.Contains(m.Id));
                if (allModesArePermission)
                {
                    ShowModeSelector = false;
                    ModeOptions = [];
                    ModeChoices.Clear();
                    if (!preserveConfiguredPermission || string.IsNullOrEmpty(configuredPerm))
                    {
                        var matched = _definition.Acp!.PermissionModes!
                            .FirstOrDefault(kv => string.Equals(kv.Value, modes.CurrentModeId, StringComparison.OrdinalIgnoreCase)).Key;
                        if (!string.IsNullOrEmpty(matched)) SelectedPermissionId = matched;
                    }
                }
                else
                {
                    ModeOptions = [.. modes.AvailableModes.Select(m => new SelectOption(m.Id, m.Name))];
                    SelectedModeId = string.IsNullOrEmpty(modes.CurrentModeId) ? modes.AvailableModes[0].Id : modes.CurrentModeId;
                    ShowModeSelector = true;
                    RebuildModeChoices(modes.AvailableModes.Select(m => (m.Id, m.Name, m.Description)));
                }
            }
            else
            {
                ShowModeSelector = false;
                ModeOptions = [];
                ModeChoices.Clear();
            }

            if (modelOpt is { Options.Count: > 0 })
            {
                _modelConfigId = modelOpt.Id;
                ModelOptions = [.. modelOpt.Options.Select(o => new SelectOption(o.Value, o.Name))];
                SelectedModelId = modelOpt.CurrentValue ?? modelOpt.Options[0].Value;
                ShowModelSelector = true;
                RebuildModelChoices(modelOpt.Options.Select(o => (o.Value, o.Name, o.Description, o.GroupName)));
            }
            else if (models is { AvailableModels.Count: > 0 })
            {
                ModelOptions = [.. models.AvailableModels.Select(m => new SelectOption(m.ModelId, m.Name))];
                SelectedModelId = string.IsNullOrEmpty(models.CurrentModelId) ? models.AvailableModels[0].ModelId : models.CurrentModelId;
                ShowModelSelector = true;
                RebuildModelChoices(models.AvailableModels.Select(m => (m.ModelId, m.Name, m.Description, (string?)null)));
            }
            else if (initialized is not null)
            {
                // Connected ACP server did not expose live model selection -> hide dead selector.
                ModelOptions = [];
                ModelChoices.Clear();
                FilteredModelChoices.Clear();
                ShowModelSelector = false;
            }

            if (effortOpt is { Options.Count: > 0 })
            {
                _hasLiveEffort = true;
                _effortConfigId = effortOpt.Id;
                EffortOptions = [.. effortOpt.Options.Select(o => new SelectOption(o.Value, o.Name))];
                SelectedEffortId = effortOpt.CurrentValue ?? effortOpt.Options[0].Value;
                ShowEffortSelector = true;
                RebuildEffortChoices(effortOpt.Options.Select(o => (o.Value, o.Name, o.Description)));
            }
            else if (initialized is not null)
            {
                // Connected ACP server has no live effort/thought_level option (e.g. agy encodes effort in model variants).
                _hasLiveEffort = false;
                EffortOptions = [];
                EffortChoices.Clear();
                ShowEffortSelector = false;
            }
            else
            {
                _hasLiveEffort = false;
                RebuildCatalogEffortsForModel(SelectedModelId);
            }

            if (fastOpt is not null)
            {
                _fastConfigId = fastOpt.Id;
                FastMode = fastOpt.CurrentBoolValue ?? string.Equals(fastOpt.CurrentValue, "true", StringComparison.OrdinalIgnoreCase);
                ShowFastToggle = true;
            }
            else if (initialized is not null)
            {
                ShowFastToggle = false;
            }
            else
            {
                ShowFastToggle = _definition.Capabilities.Fast is not null;
            }

            if (thinkingOpt is not null)
            {
                _thinkingConfigId = thinkingOpt.Id;
                ThinkingEnabled = thinkingOpt.CurrentBoolValue ?? !string.Equals(thinkingOpt.CurrentValue, "false", StringComparison.OrdinalIgnoreCase);
                ShowThinkingToggle = true;
            }
            else
            {
                ShowThinkingToggle = false;
            }

            SyncAllSelectionStates();
            RebuildCapabilityBadges();
        }
        finally
        {
            _syncing = false;
        }
    }

    private static bool IsEffortOption(AcpSessionConfigOption opt) =>
        string.Equals(opt.Category, "thought_level", StringComparison.OrdinalIgnoreCase)
        || string.Equals(opt.Category, "reasoning_effort", StringComparison.OrdinalIgnoreCase)
        || string.Equals(opt.Category, "effort", StringComparison.OrdinalIgnoreCase)
        || string.Equals(opt.Id, "thought_level", StringComparison.OrdinalIgnoreCase)
        || string.Equals(opt.Id, "reasoning_effort", StringComparison.OrdinalIgnoreCase)
        || string.Equals(opt.Id, "effort", StringComparison.OrdinalIgnoreCase);

    private void RebuildModeChoices(IEnumerable<(string Id, string Label, string? Description)> items)
    {
        ModeChoices.Clear();
        foreach (var (id, label, desc) in items)
        {
            ModeChoices.Add(new ComposerChoiceItem(id, label, SelectMode, desc) { IsSelected = id == SelectedModeId });
        }
        OnPropertyChanged(nameof(SelectedModeLabel));
    }

    private void RebuildPermissionChoices(IEnumerable<(string Id, string Label)> items)
    {
        PermissionChoices.Clear();
        foreach (var (id, label) in items)
        {
            var danger = string.Equals(id, PermissionModes.Full, StringComparison.OrdinalIgnoreCase);
            PermissionChoices.Add(new ComposerChoiceItem(id, label, SelectPermission, icon: PermissionIconFor(id), isDanger: danger)
            {
                IsSelected = id == SelectedPermissionId,
            });
        }
        OnPropertyChanged(nameof(SelectedPermissionLabel));
        OnPropertyChanged(nameof(PermissionIcon));
        OnPropertyChanged(nameof(IsPermissionDanger));
    }

    private void RebuildModelChoices(IEnumerable<(string Id, string Label, string? Description, string? Meta)> items)
    {
        ModelChoices.Clear();
        foreach (var (id, label, desc, meta) in items)
        {
            var cleanMeta = !string.IsNullOrWhiteSpace(meta) && !string.Equals(meta, label, StringComparison.OrdinalIgnoreCase) ? meta : null;
            ModelChoices.Add(new ComposerChoiceItem(id, label, SelectModel, desc, cleanMeta) { IsSelected = id == SelectedModelId });
        }
        ApplyModelFilter();
        OnPropertyChanged(nameof(ShowModelSearch));
        OnPropertyChanged(nameof(SelectedModelLabel));
    }

    private void RebuildEffortChoices(IEnumerable<(string Id, string Label, string? Description)> items)
    {
        EffortChoices.Clear();
        foreach (var (id, label, desc) in items)
        {
            EffortChoices.Add(new ComposerChoiceItem(id, label, SelectEffort, desc) { IsSelected = id == SelectedEffortId });
        }
        OnPropertyChanged(nameof(SelectedEffortLabel));
    }

    private void ApplyModelFilter()
    {
        var query = ModelSearchQuery.Trim();
        FilteredModelChoices.Clear();
        foreach (var item in ModelChoices)
        {
            if (query.Length == 0
                || item.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (item.Meta?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (item.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                FilteredModelChoices.Add(item);
            }
        }
        OnPropertyChanged(nameof(HasFilteredModels));
    }

    private void SyncAllSelectionStates()
    {
        foreach (var c in ModeChoices) c.IsSelected = c.Id == SelectedModeId;
        foreach (var c in PermissionChoices) c.IsSelected = c.Id == SelectedPermissionId;
        foreach (var c in ModelChoices) c.IsSelected = c.Id == SelectedModelId;
        foreach (var c in EffortChoices) c.IsSelected = c.Id == SelectedEffortId;
        OnPropertyChanged(nameof(SelectedModeLabel));
        OnPropertyChanged(nameof(SelectedPermissionLabel));
        OnPropertyChanged(nameof(PermissionIcon));
        OnPropertyChanged(nameof(IsPermissionDanger));
        OnPropertyChanged(nameof(SelectedModelLabel));
        OnPropertyChanged(nameof(SelectedEffortLabel));
    }

    private void SelectMode(string id)
    {
        SelectedModeId = id;
        IsModePickerOpen = false;
    }

    private void SelectPermission(string id)
    {
        SelectedPermissionId = id;
        IsPermissionPickerOpen = false;
    }

    private void SelectModel(string id)
    {
        SelectedModelId = id;
        IsModelPickerOpen = false;
    }

    private void SelectEffort(string id)
    {
        SelectedEffortId = id;
        IsEffortPickerOpen = false;
    }

    private static LucideIconKind PermissionIconFor(string? id) => id switch
    {
        PermissionModes.Full => LucideIconKind.ShieldAlert,
        PermissionModes.AcceptEdits or PermissionModes.Auto or PermissionModes.DontAsk => LucideIconKind.ShieldCheck,
        _ => LucideIconKind.Shield,
    };

    private void RebuildCapabilityBadges()
    {
        CapabilityBadges.Clear();
        if (SupportsSlashCommands) CapabilityBadges.Add($"/{AllCommands.Count}");
        if (SupportsImageAttachments) CapabilityBadges.Add("Images");
        if (ShowModeSelector) CapabilityBadges.Add("Modes");
        if (ShowModelSelector) CapabilityBadges.Add("Models");
        if (ShowEffortSelector) CapabilityBadges.Add("Effort");
        if (ShowFastToggle) CapabilityBadges.Add("Fast");
        if (ShowThinkingToggle) CapabilityBadges.Add("Thinking");
    }

    partial void OnModelSearchQueryChanged(string value) => ApplyModelFilter();

    partial void OnIsModelPickerOpenChanged(bool value)
    {
        if (value)
        {
            IsModePickerOpen = false;
            IsPermissionPickerOpen = false;
            IsEffortPickerOpen = false;
        }
        else if (!string.IsNullOrEmpty(ModelSearchQuery))
        {
            ModelSearchQuery = "";
        }
    }

    partial void OnIsModePickerOpenChanged(bool value)
    {
        if (!value) return;
        IsPermissionPickerOpen = false;
        IsModelPickerOpen = false;
        IsEffortPickerOpen = false;
    }

    partial void OnIsPermissionPickerOpenChanged(bool value)
    {
        if (!value) return;
        IsModePickerOpen = false;
        IsModelPickerOpen = false;
        IsEffortPickerOpen = false;
    }

    partial void OnIsEffortPickerOpenChanged(bool value)
    {
        if (!value) return;
        IsModePickerOpen = false;
        IsPermissionPickerOpen = false;
        IsModelPickerOpen = false;
    }

    partial void OnSelectedModeIdChanged(string? value)
    {
        foreach (var c in ModeChoices) c.IsSelected = c.Id == value;
        OnPropertyChanged(nameof(SelectedModeLabel));
        if (_syncing || string.IsNullOrEmpty(value)) return;
        if (_modeConfigId is not null && _onConfigSelectChanged is not null)
        {
            _ = _onConfigSelectChanged(_modeConfigId, value);
        }
        else if (_onModeChanged is not null)
        {
            _ = _onModeChanged(value);
        }
    }

    partial void OnSelectedPermissionIdChanged(string? value)
    {
        foreach (var c in PermissionChoices) c.IsSelected = c.Id == value;
        OnPropertyChanged(nameof(SelectedPermissionLabel));
        OnPropertyChanged(nameof(PermissionIcon));
        OnPropertyChanged(nameof(IsPermissionDanger));
        if (_syncing || string.IsNullOrEmpty(value)) return;
        if (_onPermissionChanged is not null)
        {
            _ = _onPermissionChanged(value);
        }
    }

    partial void OnSelectedModelIdChanged(string? value)
    {
        foreach (var c in ModelChoices) c.IsSelected = c.Id == value;
        OnPropertyChanged(nameof(SelectedModelLabel));
        if (_syncing || string.IsNullOrEmpty(value)) return;
        if (!_sessionInitialized && !_hasLiveEffort)
        {
            _syncing = true;
            RebuildCatalogEffortsForModel(value);
            _syncing = false;
        }
        if (_modelConfigId is not null && _onConfigSelectChanged is not null)
        {
            _ = _onConfigSelectChanged(_modelConfigId, value);
        }
        else if (_onModelChanged is not null)
        {
            _ = _onModelChanged(value);
        }
    }

    partial void OnSelectedEffortIdChanged(string? value)
    {
        foreach (var c in EffortChoices) c.IsSelected = c.Id == value;
        OnPropertyChanged(nameof(SelectedEffortLabel));
        if (_syncing || string.IsNullOrEmpty(value)) return;
        if (_effortConfigId is not null && _onConfigSelectChanged is not null)
        {
            _ = _onConfigSelectChanged(_effortConfigId, value);
        }
    }

    partial void OnFastModeChanged(bool value)
    {
        if (_syncing) return;
        if (_fastConfigId is not null && _onConfigBoolChanged is not null)
        {
            _ = _onConfigBoolChanged(_fastConfigId, value);
        }
    }

    partial void OnThinkingEnabledChanged(bool value)
    {
        if (_syncing) return;
        if (_thinkingConfigId is not null && _onConfigBoolChanged is not null)
        {
            _ = _onConfigBoolChanged(_thinkingConfigId, value);
        }
    }
}
