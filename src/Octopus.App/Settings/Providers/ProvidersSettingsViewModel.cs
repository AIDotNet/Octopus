using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents;
using Octopus.Agents.Providers;
using Octopus.App.Common;
using Octopus.App.Remote;
using Octopus.Core.Agents;
using Octopus.Platform;
using TKCode.Arc;
using TKCode.Arc.Localization;

namespace Octopus.App.Settings.Providers;

/// <summary>One quick-add preset chip shown at the top of Settings → Model Providers (Scheme B).</summary>
public sealed partial class ProviderPresetChipViewModel(
    ProviderPreset preset,
    Action<ProviderPreset> create) : ObservableObject
{
    public ProviderPreset Preset { get; } = preset;

    public string Label => ArcLanguage.Culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
        ? Preset.NameZh
        : Preset.Name;

    [RelayCommand]
    private void Pick() => create(Preset);
}

/// <summary>One clickable effort toggle chip inside a managed model row.</summary>
public sealed partial class ModelEffortChipViewModel(string id, bool isSelected, Action onChanged) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty] public partial bool IsSelected { get; set; } = isSelected;

    [RelayCommand]
    private void Toggle()
    {
        IsSelected = !IsSelected;
        onChanged();
    }
}

/// <summary>One editable model row inside <see cref="ProviderEditorViewModel"/>.</summary>
public sealed partial class ManagedModelRowViewModel : ObservableObject
{
    private static readonly string[] AllEfforts = ["low", "medium", "high", "xhigh", "max"];
    private readonly Action<ManagedModelRowViewModel> _remove;
    private readonly Action _changed;

    public ManagedModelRowViewModel(
        ManagedModelSpec spec,
        bool showTier,
        Action<ManagedModelRowViewModel> remove,
        Action changed)
    {
        _remove = remove;
        _changed = changed;
        Id = spec.Id;
        DisplayName = spec.DisplayName ?? "";
        IsEnabled = spec.Enabled;
        ShowTier = showTier;
        SelectedTier = spec.Tier.ToString();

        var existing = spec.SupportedEfforts is { Count: > 0 }
            ? new HashSet<string>(spec.SupportedEfforts, StringComparer.OrdinalIgnoreCase)
            : null;
        foreach (var ef in AllEfforts)
        {
            Efforts.Add(new ModelEffortChipViewModel(ef, existing?.Contains(ef) ?? false, changed));
        }

        TierOptions =
        [
            new SelectOption(nameof(ModelSlotTier.None), "—"),
            new SelectOption(nameof(ModelSlotTier.Flagship), "Flagship (Opus)"),
            new SelectOption(nameof(ModelSlotTier.Balanced), "Balanced (Sonnet)"),
            new SelectOption(nameof(ModelSlotTier.Fast), "Fast (Haiku)"),
        ];
    }

    public string Id { get; }
    public bool ShowTier { get; }
    public ObservableCollection<ModelEffortChipViewModel> Efforts { get; } = [];
    public IReadOnlyList<SelectOption> TierOptions { get; }

    [ObservableProperty] public partial string DisplayName { get; set; }
    [ObservableProperty] public partial bool IsEnabled { get; set; }
    [ObservableProperty] public partial string SelectedTier { get; set; }

    partial void OnIsEnabledChanged(bool value) => _changed();
    partial void OnDisplayNameChanged(string value) => _changed();
    partial void OnSelectedTierChanged(string value) => _changed();

    [RelayCommand]
    private void Remove() => _remove(this);

    public ManagedModelSpec ToSpec()
    {
        var chosenEfforts = Efforts.Where(e => e.IsSelected).Select(e => e.Id).ToList();
        return new ManagedModelSpec
        {
            Id = Id,
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim(),
            Enabled = IsEnabled,
            Tier = Enum.TryParse<ModelSlotTier>(SelectedTier, out var tier) ? tier : ModelSlotTier.None,
            SupportedEfforts = chosenEfforts.Count > 0 ? chosenEfforts : null,
        };
    }
}

/// <summary>One visual card in the 2-column provider grid (Settings Scheme B).</summary>
public sealed partial class ProviderCardViewModel : ObservableObject
{
    private readonly Action<ProviderCardViewModel> _configure;
    private readonly Func<ProviderCardViewModel, Task> _test;
    private readonly Action<ProviderCardViewModel, bool> _setEnabled;

    public ProviderCardViewModel(
        ModelProviderRecord record,
        IReadOnlyList<string> compatibleAgentNames,
        bool hasSecret,
        Action<ProviderCardViewModel> configure,
        Func<ProviderCardViewModel, Task> test,
        Action<ProviderCardViewModel, bool> setEnabled)
    {
        Record = record;
        _configure = configure;
        _test = test;
        _setEnabled = setEnabled;
        HasSecret = hasSecret;
        CompatibleAgentsSummary = compatibleAgentNames.Count > 0
            ? string.Join(" · ", compatibleAgentNames)
            : "—";

        var enabledModels = record.Models.Where(m => m.Enabled).ToList();
        EnabledModelChips = enabledModels.Take(4).Select(m => m.Id).ToList();
        ExtraModelCountText = enabledModels.Count > 4 ? $"+{enabledModels.Count - 4}" : null;
        _isEnabled = record.Enabled;
    }

    public ModelProviderRecord Record { get; }
    public string Id => Record.Id;
    public string Name => Record.Name;
    public string ProtocolLabel => ProviderProtocolNames.DisplayLabel(Record.Protocol);
    public string BaseUrlDisplay => string.IsNullOrWhiteSpace(Record.BaseUrl) ? "Official Endpoint" : Record.BaseUrl;
    public IReadOnlyList<string> EnabledModelChips { get; }
    public string? ExtraModelCountText { get; }
    public bool HasExtraModels => !string.IsNullOrEmpty(ExtraModelCountText);
    public string CompatibleAgentsSummary { get; }
    public bool HasSecret { get; }
    public bool AllowRemoteTunnel => Record.AllowRemoteTunnel;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] public partial string? TestBadgeText { get; set; }
    [ObservableProperty] public partial bool HasTestBadge { get; set; }
    [ObservableProperty] public partial bool TestSucceeded { get; set; }
    [ObservableProperty] public partial bool IsTesting { get; set; }

    partial void OnIsEnabledChanged(bool value) => _setEnabled(this, value);

    [RelayCommand]
    private void Configure() => _configure(this);

    [RelayCommand]
    private Task TestAsync() => _test(this);
}

/// <summary>Modal editor for one managed model provider (connection, keychain secret, models, remote tunnel, materialize preview).</summary>
public sealed partial class ProviderEditorViewModel : ObservableObject
{
    private readonly IModelProviderService _providers;
    private readonly IAgentCatalog _catalog;
    private readonly Action _onClosed;

    public ProviderEditorViewModel(
        ModelProviderRecord record,
        bool isNew,
        bool hasSavedSecret,
        IModelProviderService providers,
        IAgentCatalog catalog,
        Action onClosed)
    {
        _providers = providers;
        _catalog = catalog;
        _onClosed = onClosed;
        Id = record.Id;
        IsNew = isNew;
        HasSavedSecret = hasSavedSecret;

        Name = record.Name;
        SelectedProtocol = record.Protocol.ToString();
        BaseUrl = record.BaseUrl;
        AuthEnvPreference = string.IsNullOrWhiteSpace(record.AuthEnvPreference) ? "ANTHROPIC_AUTH_TOKEN" : record.AuthEnvPreference;
        IsEnabled = record.Enabled;
        IsolateConfigDir = record.IsolateConfigDir;
        AllowRemoteTunnel = record.AllowRemoteTunnel;

        ProtocolOptions =
        [
            new SelectOption(nameof(ProviderProtocol.Anthropic), "Anthropic (/v1/messages)"),
            new SelectOption(nameof(ProviderProtocol.OpenAIResponses), "OpenAI Responses (/v1/responses)"),
            new SelectOption(nameof(ProviderProtocol.OpenAIChat), "OpenAI Chat (/v1/chat/completions)"),
            new SelectOption(nameof(ProviderProtocol.Gemini), "Gemini (Custom Base URL)"),
            new SelectOption(nameof(ProviderProtocol.GeminiOfficialKey), "Gemini Official Key (GEMINI_API_KEY)"),
        ];

        AuthEnvOptions =
        [
            new SelectOption("ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_AUTH_TOKEN (Default)"),
            new SelectOption("ANTHROPIC_API_KEY", "ANTHROPIC_API_KEY"),
        ];

        var isAnthropic = record.Protocol == ProviderProtocol.Anthropic;
        foreach (var model in record.Models)
        {
            Models.Add(new ManagedModelRowViewModel(model, isAnthropic, RemoveModelRow, RefreshDerived));
        }

        RefreshDerived();
    }

    public string Id { get; }
    public bool IsNew { get; }
    public bool CanDelete => !IsNew && !string.IsNullOrWhiteSpace(Id);
    public IReadOnlyList<SelectOption> ProtocolOptions { get; }
    public IReadOnlyList<SelectOption> AuthEnvOptions { get; }
    public ObservableCollection<ManagedModelRowViewModel> Models { get; } = [];

    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial string SelectedProtocol { get; set; }
    [ObservableProperty] public partial string BaseUrl { get; set; }
    [ObservableProperty] public partial string AuthEnvPreference { get; set; }
    [ObservableProperty] public partial string ApiKeyInput { get; set; } = "";
    [ObservableProperty] public partial bool HasSavedSecret { get; set; }
    [ObservableProperty] public partial bool IsEnabled { get; set; }
    [ObservableProperty] public partial bool IsolateConfigDir { get; set; }
    [ObservableProperty] public partial bool AllowRemoteTunnel { get; set; }
    [ObservableProperty] public partial string NewModelId { get; set; } = "";
    [ObservableProperty] public partial bool ShowPreview { get; set; }
    [ObservableProperty] public partial string PreviewText { get; set; } = "";
    [ObservableProperty] public partial string CompatibleSummary { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string? StatusMessage { get; set; }
    [ObservableProperty] public partial bool StatusIsError { get; set; }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool IsAnthropicProtocol => SelectedProtocol == nameof(ProviderProtocol.Anthropic);
    public bool IsBaseUrlEditable => SelectedProtocol != nameof(ProviderProtocol.GeminiOfficialKey);

    partial void OnNameChanged(string value) => RefreshDerived();
    partial void OnBaseUrlChanged(string value) => RefreshDerived();
    partial void OnAuthEnvPreferenceChanged(string value) => RefreshDerived();
    partial void OnIsolateConfigDirChanged(bool value) => RefreshDerived();
    partial void OnAllowRemoteTunnelChanged(bool value) => RefreshDerived();
    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnSelectedProtocolChanged(string value)
    {
        OnPropertyChanged(nameof(IsAnthropicProtocol));
        OnPropertyChanged(nameof(IsBaseUrlEditable));
        var specs = Models.Select(m => m.ToSpec()).ToList();
        Models.Clear();
        foreach (var spec in specs)
        {
            Models.Add(new ManagedModelRowViewModel(spec, IsAnthropicProtocol, RemoveModelRow, RefreshDerived));
        }
        RefreshDerived();
    }

    private void RemoveModelRow(ManagedModelRowViewModel row)
    {
        Models.Remove(row);
        RefreshDerived();
    }

    [RelayCommand]
    private void TogglePreview()
    {
        ShowPreview = !ShowPreview;
        if (ShowPreview) RefreshDerived();
    }

    [RelayCommand]
    private void AddModel()
    {
        var id = NewModelId.Trim();
        if (id.Length == 0) return;
        if (Models.Any(m => string.Equals(m.Id, id, StringComparison.Ordinal)))
        {
            StatusIsError = true;
            StatusMessage = $"Model '{id}' is already in the list.";
            return;
        }
        Models.Add(new ManagedModelRowViewModel(
            new ManagedModelSpec { Id = id, Enabled = true, SupportedEfforts = ["low", "medium", "high"] },
            IsAnthropicProtocol,
            RemoveModelRow,
            RefreshDerived));
        NewModelId = "";
        StatusMessage = null;
        RefreshDerived();
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsBusy = true;
        StatusIsError = false;
        StatusMessage = "Testing connection...";
        try
        {
            var draft = BuildRecord();
            var overrideKey = string.IsNullOrWhiteSpace(ApiKeyInput) ? null : ApiKeyInput.Trim();
            var result = await _providers.TestConnectionAsync(draft, overrideKey);
            StatusIsError = !result.Succeeded;
            StatusMessage = result.Succeeded
                ? $"Connected ({result.LatencyMs} ms{(result.DiscoveredModelCount is > 0 ? $" · {result.DiscoveredModelCount} models" : "")})"
                : $"Failed: {result.ErrorMessage}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task FetchModelsAsync()
    {
        IsBusy = true;
        StatusIsError = false;
        StatusMessage = "Fetching remote models...";
        try
        {
            var draft = BuildRecord();
            var overrideKey = string.IsNullOrWhiteSpace(ApiKeyInput) ? null : ApiKeyInput.Trim();
            var remote = await _providers.FetchRemoteModelsAsync(draft, overrideKey);
            var added = 0;
            foreach (var spec in remote)
            {
                if (Models.Any(m => string.Equals(m.Id, spec.Id, StringComparison.Ordinal))) continue;
                Models.Add(new ManagedModelRowViewModel(spec, IsAnthropicProtocol, RemoveModelRow, RefreshDerived));
                added++;
            }
            StatusIsError = remote.Count == 0;
            StatusMessage = remote.Count == 0
                ? "No models returned by endpoint."
                : $"Fetched {remote.Count} models ({added} added).";
            RefreshDerived();
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusIsError = true;
            StatusMessage = "Provider name is required.";
            return;
        }
        IsBusy = true;
        try
        {
            var record = BuildRecord();
            var keyToWrite = string.IsNullOrWhiteSpace(ApiKeyInput) ? null : ApiKeyInput.Trim();
            await _providers.SaveAsync(record, keyToWrite);
            _onClosed();
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!CanDelete) return;
        IsBusy = true;
        try
        {
            await _providers.RemoveAsync(Id);
            _onClosed();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _onClosed();

    private ModelProviderRecord BuildRecord()
    {
        var protocol = Enum.TryParse<ProviderProtocol>(SelectedProtocol, out var p) ? p : ProviderProtocol.Anthropic;
        return new ModelProviderRecord
        {
            Id = Id,
            Name = Name.Trim(),
            Protocol = protocol,
            BaseUrl = BaseUrl.Trim().TrimEnd('/'),
            AuthEnvPreference = protocol == ProviderProtocol.Anthropic ? AuthEnvPreference : null,
            Enabled = IsEnabled,
            IsolateConfigDir = IsolateConfigDir,
            AllowRemoteTunnel = AllowRemoteTunnel,
            Models = Models.Select(m => m.ToSpec()).ToList(),
        };
    }

    private void RefreshDerived()
    {
        var draft = BuildRecord();
        var compat = _catalog.Definitions
            .Where(d => d.ProviderSupport?.SupportsProtocol(draft.Protocol) == true)
            .Select(d => d.Name)
            .ToList();
        CompatibleSummary = compat.Count > 0 ? string.Join(" · ", compat) : "—";
        PreviewText = ProviderMaterializer.BuildPreviewText(draft);
    }
}

/// <summary>Settings → AI Capabilities → Model Providers page (Scheme B: quick preset bar + 2-column card grid + modal editor).</summary>
public sealed partial class ProvidersSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IModelProviderService _providers;
    private readonly IAgentCatalog _catalog;
    private readonly IAgentScanner _scanner;
    private readonly CoWorkIntegrationService? _cowork;
    private readonly IFileOpener? _opener;
    private bool _suppressTeamSelect;

    public ProvidersSettingsViewModel(
        IModelProviderService providers,
        IAgentCatalog catalog,
        IAgentScanner scanner,
        CoWorkIntegrationService? cowork = null,
        IFileOpener? opener = null)
    {
        _providers = providers;
        _catalog = catalog;
        _scanner = scanner;
        _cowork = cowork;
        _opener = opener;

        foreach (var preset in ProviderPresets.All)
        {
            Presets.Add(new ProviderPresetChipViewModel(preset, CreateFromPreset));
        }

        _providers.Changed += OnProvidersChanged;
        _scanner.Changed += OnScannerChanged;
        _cowork?.Changed += OnCoWorkChanged;

        ArcLanguage.Changed += OnLanguageChanged;
        UpdateCoWorkUi();
        _ = ReloadCardsAsync();
        if (_cowork is not null)
        {
            _ = RefreshCoWorkAsync();
        }
    }

    public ObservableCollection<ProviderPresetChipViewModel> Presets { get; } = [];
    public ObservableCollection<ProviderCardViewModel> Cards { get; } = [];
    public ObservableCollection<SelectOption> CoWorkTeamOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    public partial ProviderEditorViewModel? Editor { get; set; }

    [ObservableProperty] public partial bool ShowCoWorkCard { get; private set; }
    [ObservableProperty] public partial bool IsCoWorkBound { get; private set; }
    [ObservableProperty] public partial bool IsCoWorkBusy { get; private set; }
    [ObservableProperty] public partial bool HasCoWorkTeams { get; private set; }
    [ObservableProperty] public partial string CoWorkBadgeText { get; private set; } = "";
    [ObservableProperty] public partial string CoWorkTitleText { get; private set; } = "";
    [ObservableProperty] public partial string CoWorkSummaryText { get; private set; } = "";
    [ObservableProperty] public partial string? CoWorkMessage { get; private set; }
    [ObservableProperty] public partial bool HasCoWorkMessage { get; private set; }
    [ObservableProperty] public partial string? SelectedCoWorkTeamId { get; set; }

    partial void OnSelectedCoWorkTeamIdChanged(string? value)
    {
        if (_suppressTeamSelect || string.IsNullOrWhiteSpace(value) || _cowork is null) return;
        _ = SwitchCoWorkTeamAsync(value);
    }

    [RelayCommand]
    private void OpenCoWorkBindPage()
    {
        if (_cowork?.GetBindPageUrl() is { } url)
        {
            _opener?.OpenUrl(url);
        }
    }

    [RelayCommand]
    private async Task RefreshCoWorkAsync()
    {
        if (_cowork is null || IsCoWorkBusy) return;
        IsCoWorkBusy = true;
        try
        {
            await _cowork.RefreshStatusAsync(autoSyncProviders: true);
        }
        finally
        {
            UiContext.PostToUi(() =>
            {
                IsCoWorkBusy = false;
                UpdateCoWorkUi();
            });
        }
    }

    [RelayCommand]
    private async Task SyncCoWorkModelsAsync()
    {
        if (_cowork is null || IsCoWorkBusy) return;
        IsCoWorkBusy = true;
        CoWorkMessage = null;
        HasCoWorkMessage = false;
        try
        {
            var isZh = IsZh();
            var (ok, count, err) = await _cowork.SyncProvidersAsync();
            UiContext.PostToUi(() =>
            {
                CoWorkMessage = ok
                    ? (isZh ? $"已同步 CoWork 模型提供商（共发现 {count} 个模型）" : $"Synced CoWork model providers ({count} models discovered).")
                    : (isZh ? $"同步失败：{err ?? "请检查 CoWork 绑定状态"}" : $"Sync failed: {err ?? "check CoWork binding"}");
                HasCoWorkMessage = true;
            });
        }
        finally
        {
            UiContext.PostToUi(() =>
            {
                IsCoWorkBusy = false;
                UpdateCoWorkUi();
            });
        }
    }

    private async Task SwitchCoWorkTeamAsync(string teamId)
    {
        if (_cowork is null) return;
        IsCoWorkBusy = true;
        try
        {
            var isZh = IsZh();
            var (ok, err) = await _cowork.SelectTeamAsync(teamId);
            UiContext.PostToUi(() =>
            {
                if (!ok)
                {
                    CoWorkMessage = isZh ? $"切换 Team 失败：{err}" : $"Failed to switch Team: {err}";
                    HasCoWorkMessage = true;
                }
            });
        }
        finally
        {
            UiContext.PostToUi(() =>
            {
                IsCoWorkBusy = false;
                UpdateCoWorkUi();
            });
        }
    }

    private void OnCoWorkChanged(object? sender, EventArgs e) => UiContext.PostToUi(UpdateCoWorkUi);

    private void UpdateCoWorkUi()
    {
        if (_cowork is null)
        {
            ShowCoWorkCard = false;
            return;
        }

        var isZh = IsZh();
        var status = _cowork.Status;
        if (!_cowork.IsSignedIn)
        {
            ShowCoWorkCard = true;
            IsCoWorkBound = false;
            HasCoWorkTeams = false;
            CoWorkTitleText = isZh ? "CoWork 模型额度" : "CoWork Model Quota";
            CoWorkBadgeText = isZh ? "未登录 Octopus" : "Sign in required";
            CoWorkSummaryText = isZh
                ? "请先点击右上角登录 Octopus 账号，再在账号设置中一对一绑定 CoWork 使用模型余额。"
                : "Sign in to your Octopus account first, then link CoWork 1:1 to spend your CoWork model balance.";
            return;
        }

        if (status is { Enabled: false })
        {
            ShowCoWorkCard = false;
            return;
        }

        ShowCoWorkCard = true;
        CoWorkTitleText = isZh ? "CoWork 模型额度" : "CoWork Model Quota";
        if (status is not { Bound: true })
        {
            IsCoWorkBound = false;
            HasCoWorkTeams = false;
            CoWorkBadgeText = isZh ? "未绑定" : "Not linked";
            CoWorkSummaryText = isZh
                ? "绑定 CoWork 后，桌面端自动启用本机代理与短命委托令牌，所有编码智能体可直接消耗所选 Team 的 CoWork 余额。"
                : "Link your CoWork account to use its balance across all coding agents via a local auto-refreshing proxy.";
            return;
        }

        IsCoWorkBound = true;
        var reauth = status.Status == "revoked" || status.Error == "cowork_reauth_required";
        CoWorkBadgeText = reauth
            ? (isZh ? "需重新授权" : "Re-link required")
            : status.Wallet is { } w
                ? $"${w.AvailableBalance:F2}"
                : (isZh ? "已绑定" : "Linked");

        var identity = status.CoWorkDisplayName ?? status.CoWorkEmail ?? status.CoWorkUserId ?? "CoWork";
        var teamPart = string.IsNullOrWhiteSpace(status.SelectedTeamName)
            ? ""
            : (isZh ? $" · Team: {status.SelectedTeamName}" : $" · Team: {status.SelectedTeamName}");
        var walletPart = status.Wallet is { } wallet
            ? (isZh
                ? $" · 可用 ${wallet.AvailableBalance:F2}（现金 ${wallet.CashBalance:F2} / 赠送 ${wallet.GiftBalance:F2}）"
                : $" · Balance ${wallet.AvailableBalance:F2} (Cash ${wallet.CashBalance:F2} / Gift ${wallet.GiftBalance:F2})")
            : "";
        CoWorkSummaryText = reauth
            ? (isZh ? $"{identity} 的 CoWork 授权已失效，请前往网页重新绑定。" : $"CoWork authorization for {identity} expired. Please re-link.")
            : $"{identity}{teamPart}{walletPart}";

        _suppressTeamSelect = true;
        try
        {
            CoWorkTeamOptions.Clear();
            foreach (var t in _cowork.Teams)
            {
                CoWorkTeamOptions.Add(new SelectOption(t.Id, $"{t.Name} ({t.Role})"));
            }

            HasCoWorkTeams = CoWorkTeamOptions.Count > 0;
            SelectedCoWorkTeamId = status.SelectedTeamId;
        }
        finally
        {
            _suppressTeamSelect = false;
        }
    }

    private static bool IsZh() => ArcLanguage.Culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public bool IsEditing => Editor is not null;
    public bool IsEmpty => Cards.Count == 0;

    [RelayCommand]
    private void AddCustom()
    {
        var blank = new ModelProviderRecord
        {
            Name = "Custom Provider",
            Protocol = ProviderProtocol.Anthropic,
            BaseUrl = "https://api.anthropic.com",
            AuthEnvPreference = "ANTHROPIC_AUTH_TOKEN",
            Enabled = true,
            IsolateConfigDir = true,
            AllowRemoteTunnel = true,
        };
        Editor = new ProviderEditorViewModel(blank, isNew: true, hasSavedSecret: false, _providers, _catalog, CloseEditor);
    }

    private void CreateFromPreset(ProviderPreset preset)
    {
        var isZh = ArcLanguage.Culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        var record = ProviderPresets.CreateRecord(preset, preferChinese: isZh);
        Editor = new ProviderEditorViewModel(record, isNew: true, hasSavedSecret: false, _providers, _catalog, CloseEditor);
    }

    private async void OpenEditor(ProviderCardViewModel card)
    {
        var fresh = _providers.Find(card.Id) ?? card.Record;
        var hasSecret = await _providers.HasSecretAsync(fresh.Id);
        UiContext.PostToUi(() =>
        {
            Editor = new ProviderEditorViewModel(fresh, isNew: false, hasSavedSecret: hasSecret, _providers, _catalog, CloseEditor);
        });
    }

    private void CloseEditor() => Editor = null;

    private async Task TestCardAsync(ProviderCardViewModel card)
    {
        card.IsTesting = true;
        card.HasTestBadge = false;
        try
        {
            var result = await _providers.TestConnectionAsync(card.Record);
            UiContext.PostToUi(() =>
            {
                card.TestSucceeded = result.Succeeded;
                card.TestBadgeText = result.Succeeded ? $"{result.LatencyMs} ms" : (result.ErrorMessage ?? "Failed");
                card.HasTestBadge = true;
            });
        }
        finally
        {
            UiContext.PostToUi(() => card.IsTesting = false);
        }
    }

    private void SetCardEnabled(ProviderCardViewModel card, bool enabled)
    {
        if (card.Record.Enabled != enabled)
        {
            _providers.SetEnabled(card.Id, enabled);
        }
    }

    private async Task ReloadCardsAsync()
    {
        var list = _providers.ListProviders();
        var secretFlags = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var p in list)
        {
            secretFlags[p.Id] = await _providers.HasSecretAsync(p.Id);
        }

        var installedIds = _scanner.Current.Installations.Select(i => i.AgentId).ToHashSet(StringComparer.Ordinal);
        UiContext.PostToUi(() =>
        {
            Cards.Clear();
            foreach (var p in list)
            {
                var compat = _catalog.Definitions
                    .Where(d => d.ProviderSupport?.SupportsProtocol(p.Protocol) == true
                        && (installedIds.Count == 0 || installedIds.Contains(d.Id)))
                    .Select(d => d.Name)
                    .ToList();
                Cards.Add(new ProviderCardViewModel(
                    p,
                    compat,
                    secretFlags.GetValueOrDefault(p.Id),
                    OpenEditor,
                    TestCardAsync,
                    SetCardEnabled));
            }
            OnPropertyChanged(nameof(IsEmpty));
        });
    }

    private void OnProvidersChanged(object? sender, EventArgs e) => _ = ReloadCardsAsync();
    private void OnScannerChanged(object? sender, EventArgs e) => _ = ReloadCardsAsync();

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e) => UiContext.PostToUi(() =>
    {
        Presets.Clear();
        foreach (var preset in ProviderPresets.All)
        {
            Presets.Add(new ProviderPresetChipViewModel(preset, CreateFromPreset));
        }
        UpdateCoWorkUi();
    });

    public void Dispose()
    {
        _providers.Changed -= OnProvidersChanged;
        _scanner.Changed -= OnScannerChanged;
        _cowork?.Changed -= OnCoWorkChanged;
        ArcLanguage.Changed -= OnLanguageChanged;
    }
}
