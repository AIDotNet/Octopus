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
/// The second panel of the launcher for one agent. Which controls it has follows the agent's capability blocks: no
/// model block, no model list; no effort block, no effort control; and so on.
/// </summary>
public sealed partial class AgentLaunchViewModel : ObservableObject, IDisposable
{
    private readonly AgentDefinition _definition;
    private readonly IAgentCommandCompiler _compiler;
    private readonly Action<AgentLaunchOptions> _launch;
    private readonly List<AgentOption> _known = [];
    private readonly IReadOnlyList<AgentAccountRecord> _accounts;
    private readonly string _permissionId;

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
        bool? allowGui = null)
    {
        _definition = definition;
        _compiler = compiler;
        _launch = launch;
        _accounts = accounts ?? [];

        var capabilities = definition.Capabilities;
        HasModel = capabilities.Model is not null;
        HasEffort = capabilities.Effort is not null;
        HasFast = capabilities.Fast is not null;
        HasPrompt = capabilities.Prompt is not null;
        HasAccounts = _accounts.Count >= 2;
        AllowCustomModel = capabilities.Model?.AllowCustom ?? false;
        AgentName = definition.Name;
        SupportsAcp = allowGui ?? definition.SupportsAcp;
        DefaultSurface = SupportsAcp ? defaultSurface : AgentSurface.Terminal;

        _permissionId = compiler.ResolvePermission(definition, profile, globalPermission) ?? PermissionModes.Default;

        if (capabilities.Model is { } model)
        {
            _known.AddRange(model.Options);
            if (!string.IsNullOrEmpty(profile.Model) && _known.All(o => o.Id != profile.Model))
            {
                _known.Add(new AgentOption { Id = profile.Model });
            }
        }
        SelectedModelId = string.IsNullOrEmpty(profile.Model) ? null : profile.Model;
        Fast = profile.Fast ?? false;
        SelectedEffortId = profile.Effort ?? "";
        SelectedAccountId = HasAccounts
            ? (_accounts.FirstOrDefault(a => a.Id == preferredAccountId)?.Id ?? _accounts[0].Id)
            : null;
        RebuildAccounts();
        RebuildModels();
        RebuildEfforts();
        ArcLanguage.Changed += OnLanguageChanged;

        if (capabilities.Model?.Discover is not null && executable is not null)
        {
            _ = DiscoverAsync(discovery, executable);
        }
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
    public bool AllowCustomModel { get; }
    public string EffortCaption => EffortCaptionOf(_definition.Capabilities.Effort?.Label);

    public string PermissionName =>
        _definition.Capabilities.Permission?.Modes.GetValueOrDefault(_permissionId)?.LabelFor(AgentLanguage.Current)
        ?? PermissionLabel(_permissionId);

    public ObservableCollection<ModelOptionViewModel> Models { get; } = [];
    public ObservableCollection<EffortChoiceViewModel> Efforts { get; } = [];
    public ObservableCollection<SelectOption> AccountOptions { get; } = [];

    [ObservableProperty] public partial string ModelQuery { get; set; } = "";
    [ObservableProperty] public partial string? SelectedModelId { get; set; }
    [ObservableProperty] public partial string SelectedEffortId { get; set; } = "";
    [ObservableProperty] public partial string? SelectedAccountId { get; set; }
    [ObservableProperty] public partial bool Fast { get; set; }
    [ObservableProperty] public partial string Prompt { get; set; } = "";
    [ObservableProperty] public partial bool IsDiscovering { get; set; }

    partial void OnModelQueryChanged(string value) => RebuildModels();

    partial void OnSelectedModelIdChanged(string? value)
    {
        RebuildModels();
        RebuildEfforts();
    }

    private void SelectModel(ModelOptionViewModel? option)
    {
        if (option is null)
        {
            return;
        }
        if (option.IsCustom && option.Id is { } customId && _known.All(o => o.Id != customId))
        {
            // Keep a typed model id in the list so it stays visible (and checked) once the search is cleared.
            _known.Add(new AgentOption { Id = customId });
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

    /// <summary>
    /// The choice made in the panel. The panel always shows a selection, so an explicit "agent default" is the empty
    /// string (it overrides the profile's value and produces no argument); null is only for blocks the agent lacks.
    /// </summary>
    public AgentLaunchOptions BuildOptions(AgentSurface? surface = null) => new()
    {
        Model = HasModel ? SelectedModelId ?? "" : null,
        Effort = HasEffort ? SelectedEffortId : null,
        Fast = HasFast ? Fast : null,
        AccountId = HasAccounts && !string.IsNullOrWhiteSpace(SelectedAccountId) ? SelectedAccountId : null,
        Prompt = HasPrompt && !string.IsNullOrWhiteSpace(Prompt) ? Prompt.Trim() : null,
        Surface = surface is { } chosen ? (SupportsAcp ? chosen : AgentSurface.Terminal) : null,
    };

    private void RebuildAccounts()
    {
        AccountOptions.Clear();
        if (!HasAccounts) return;
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

    private void RebuildModels()
    {
        Models.Clear();
        var query = ModelQuery.Trim();
        var language = AgentLanguage.Current;
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
            Models.Add(new ModelOptionViewModel(option.Id, option.LabelFor(language), option.NoteFor(language), select: SelectModel)
            {
                IsSelected = option.Id == SelectedModelId,
            });
        }
        if (AllowCustomModel && query.Length > 0 && _known.All(o => !string.Equals(o.Id, query, StringComparison.OrdinalIgnoreCase)))
        {
            Models.Add(new ModelOptionViewModel(
                query, AppLangResources.Format(AppLangResourceKind.UseCustomModel, query), null, isCustom: true, select: SelectModel)
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
        foreach (var option in options)
        {
            Efforts.Add(new EffortChoiceViewModel(option.Id, EffortName(option)));
        }
        if (SelectedEffortId.Length > 0 && Efforts.All(e => e.Id != SelectedEffortId))
        {
            SelectedEffortId = "";
        }
    }

    private async Task DiscoverAsync(IModelDiscovery discovery, string executable)
    {
        IsDiscovering = true;
        try
        {
            var found = await Task.Run(() => discovery.DiscoverAsync(_definition, executable));
            UiContext.PostToUi(() =>
            {
                foreach (var option in found)
                {
                    if (_known.All(o => o.Id != option.Id))
                    {
                        _known.Add(option);
                    }
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
            UiContext.PostToUi(() => IsDiscovering = false);
        }
    }

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e) => UiContext.PostToUi(() =>
    {
        RebuildAccounts();
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
