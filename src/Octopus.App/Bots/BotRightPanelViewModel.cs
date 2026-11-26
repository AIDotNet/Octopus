using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents;
using Octopus.App.Activity;
using Octopus.App.Common;
using Octopus.App.Explorer.Files;
using Octopus.App.Explorer.Search;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Core.Bots;
using Octopus.Platform;
using Octopus.Storage;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.Localization;

namespace Octopus.App.Bots;

public enum BotRightTab
{
    Home,
    Connectors,
    Chats,
    Files
}

public sealed partial class BotConnectorItemViewModel : ObservableObject
{
    private readonly string _botId;
    private readonly IBotService _bots;
    private readonly Action<string, bool>? _setEnabled;
    private readonly Action<string> _remove;

    public BotConnectorItemViewModel(
        string botId,
        BotConnectorBinding binding,
        IBotService bots,
        Action<string, bool> setEnabled,
        Action<string> remove)
    {
        _botId = botId;
        _bots = bots;
        Id = binding.Id;
        AppId = binding.AppId;
        Name = binding.DisplayName;
        var app = BotCatalog.FindConnectorApp(binding.AppId);
        var isZh = ArcLanguage.Current.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        Subtitle = !string.IsNullOrWhiteSpace(binding.Url)
            ? binding.Url!
            : (app is not null ? (isZh ? app.DescriptionZh : app.DescriptionEn) : binding.Description ?? "");
        BadgeBrush = SolidColorBrush.Parse(app?.AccentColor ?? "#5b5bd6");
        BadgeText = app?.BadgeText ?? (Name.Length > 0 ? Name[..Math.Min(2, Name.Length)].ToUpperInvariant() : "URL");
        TransportBadge = string.Equals(binding.Transport, "sse", StringComparison.OrdinalIgnoreCase) ? "SSE" : "HTTP";
        HasEndpoint = !string.IsNullOrWhiteSpace(binding.Url);
        HasSecret = binding.HasSecret;
        AuthBadge = binding.HasSecret
            ? AppLangResources.Get(AppLangResourceKind.BotConnectorAuthTokenSet)
            : binding.AuthMode switch
            {
                "none" => AppLangResources.Get(AppLangResourceKind.BotConnectorAuthNone),
                "bearer" => AppLangResources.Get(AppLangResourceKind.BotConnectorAuthTokenNeeded),
                _ => AppLangResources.Get(AppLangResourceKind.BotConnectorAuthOAuth),
            };
        HasUrlVariable = app?.UrlVariable is not null;
        UrlVariableLabel = app?.UrlVariable is { } v ? (isZh ? v.LabelZh : v.LabelEn) : "";
        UrlVariablePlaceholder = app?.UrlVariable?.Placeholder ?? "";
        EditVariableValue = binding.VariableValue ?? app?.UrlVariable?.DefaultValue ?? "";
        EditUrl = binding.Url ?? "";
        EditTransport = string.Equals(binding.Transport, "sse", StringComparison.OrdinalIgnoreCase) ? "sse" : "http";

        IsEnabled = binding.Enabled;
        _setEnabled = setEnabled;
        _remove = remove;
    }

    public string Id { get; }
    public string AppId { get; }
    public string Name { get; }
    public string Subtitle { get; }
    public IBrush BadgeBrush { get; }
    public string BadgeText { get; }
    public string TransportBadge { get; }
    public bool HasEndpoint { get; }
    public bool HasSecret { get; }
    public string AuthBadge { get; }
    public bool HasUrlVariable { get; }
    public string UrlVariableLabel { get; }
    public string UrlVariablePlaceholder { get; }

    public IReadOnlyList<SelectOption> TransportOptions { get; } =
    [
        new("http", "HTTP"),
        new("sse", "SSE"),
    ];

    [ObservableProperty] public partial bool IsEnabled { get; set; }
    [ObservableProperty] public partial bool IsEditing { get; set; }
    [ObservableProperty] public partial string EditUrl { get; set; } = "";
    [ObservableProperty] public partial string EditVariableValue { get; set; } = "";
    [ObservableProperty] public partial string EditTransport { get; set; } = "http";
    [ObservableProperty] public partial string EditBearerToken { get; set; } = "";
    [ObservableProperty] public partial bool IsProbing { get; private set; }
    [ObservableProperty] public partial string ProbeStatusText { get; private set; } = "";
    [ObservableProperty] public partial bool HasProbeStatus { get; private set; }

    partial void OnIsEnabledChanged(bool value) => _setEnabled?.Invoke(Id, value);

    [RelayCommand]
    private void ToggleEdit() => IsEditing = !IsEditing;

    [RelayCommand]
    private async Task TestAsync()
    {
        if (IsProbing) return;
        var targetUrl = !string.IsNullOrWhiteSpace(EditUrl) ? EditUrl : Subtitle;
        IsProbing = true;
        ProbeStatusText = AppLangResources.Get(AppLangResourceKind.BotConnectorTesting);
        HasProbeStatus = true;
        try
        {
            var token = !string.IsNullOrWhiteSpace(EditBearerToken)
                ? EditBearerToken
                : await _bots.ConnectorRuntime.GetTokenAsync(_botId, Id);
            var result = await _bots.ConnectorRuntime.ProbeAsync(targetUrl, EditTransport, token);
            ProbeStatusText = result.Status switch
            {
                ConnectorProbeStatus.Reachable => AppLangResources.Format(AppLangResourceKind.BotConnectorTestOk, result.Message),
                ConnectorProbeStatus.AuthRequired => AppLangResources.Format(AppLangResourceKind.BotConnectorTestAuthRequired, result.Message),
                _ => AppLangResources.Format(AppLangResourceKind.BotConnectorTestFailed, result.Message),
            };
        }
        finally
        {
            IsProbing = false;
        }
    }

    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        var app = BotCatalog.FindConnectorApp(AppId);
        var urlToSave = HasUrlVariable && app is not null
            ? BotCatalog.ResolveMcpUrl(app, EditVariableValue)
            : EditUrl;
        await _bots.ConfigureConnectorAsync(
            _botId,
            Id,
            urlToSave,
            EditTransport,
            HasUrlVariable ? EditVariableValue : null,
            EditBearerToken,
            clearToken: false);
        EditBearerToken = "";
        IsEditing = false;
    }

    [RelayCommand]
    private async Task ClearTokenAsync()
    {
        await _bots.ConfigureConnectorAsync(
            _botId,
            Id,
            EditUrl,
            EditTransport,
            HasUrlVariable ? EditVariableValue : null,
            bearerToken: null,
            clearToken: true);
        EditBearerToken = "";
    }

    [RelayCommand]
    private void Remove() => _remove(Id);
}

public sealed partial class BotWorkItemOptionViewModel(string label, Action<string> choose)
{
    public string Label { get; } = label;

    [RelayCommand]
    private void Choose() => choose(Label);
}

public sealed partial class BotWorkItemCardViewModel
{
    public BotWorkItemCardViewModel(BotWorkItem item, Action<string, string?> resolve, Action<string>? openChat)
    {
        Id = item.Id;
        NumberText = $"#{item.Number}";
        Title = item.Title;
        Status = item.Status;
        IsNeedsYou = item.Status == BotWorkItemStatus.NeedsYou;
        Question = item.Question;
        HasQuestion = !string.IsNullOrWhiteSpace(item.Question);
        Meta = item.Meta;
        HasMeta = !string.IsNullOrWhiteSpace(item.Meta);
        ChatId = item.ChatId;
        HasChat = !string.IsNullOrWhiteSpace(item.ChatId);
        StatusLabel = item.Status switch
        {
            BotWorkItemStatus.NeedsYou => AppLangResources.Get(AppLangResourceKind.BotStatusWorking),
            BotWorkItemStatus.Working => AppLangResources.Get(AppLangResourceKind.BotStatusWorking),
            _ => item.Status.ToString(),
        };
        Options = [.. item.Options.Select(o => new BotWorkItemOptionViewModel(o, opt => resolve(item.Id, opt)))];
        _openChat = openChat;
    }

    private readonly Action<string>? _openChat;

    public string Id { get; }
    public string NumberText { get; }
    public string Title { get; }
    public BotWorkItemStatus Status { get; }
    public bool IsNeedsYou { get; }
    public string? Question { get; }
    public bool HasQuestion { get; }
    public string? Meta { get; }
    public bool HasMeta { get; }
    public string? ChatId { get; }
    public bool HasChat { get; }
    public string StatusLabel { get; }
    public IReadOnlyList<BotWorkItemOptionViewModel> Options { get; }

    [RelayCommand]
    private void OpenChat()
    {
        if (ChatId is not null) _openChat?.Invoke(ChatId);
    }
}

/// <summary>
/// Right panel in Bot mode with 4 tabs matching the reference design:
/// <c>主页</c> (Overview + in-panel Settings sub-page) | <c>连接器</c> | <c>聊天</c> | <c>文件</c>.
/// </summary>
public sealed partial class BotRightPanelViewModel : ObservableObject
{
    private static readonly string[] PermissionModes = ["same_as_chat", "ask", "read_only", "auto"];
    private static readonly string[] WhoCanAskModes = ["only_you", "members", "anyone"];

    private readonly IBotService _bots;
    private readonly IDialogService _dialogs;
    private readonly IAgentCatalog _catalog;
    private readonly IFileOpener _fileOpener;
    private readonly IProjectIconStore? _icons;
    private readonly IItemActivityService? _activity;
    private readonly IAgentScanner? _scanner;
    private readonly ISettingsStore? _settings;
    private string? _loadedFilesRoot;
    private bool _hydrating;
    private readonly BotScheduler? _scheduler;

    public BotRightPanelViewModel(
        IBotService bots,
        IDialogService dialogs,
        IAgentCatalog catalog,
        IFileOpener fileOpener,
        FileTreeViewModel files,
        FileSearchViewModel search,
        IProjectIconStore? icons = null,
        IItemActivityService? activity = null,
        IAgentScanner? scanner = null,
        ISettingsStore? settings = null,
        BotScheduler? scheduler = null)
    {
        _bots = bots;
        _scheduler = scheduler;
        _dialogs = dialogs;
        _catalog = catalog;
        _fileOpener = fileOpener;
        _icons = icons;
        Files = files;
        Search = search;
        _activity = activity;
        _scanner = scanner;
        _settings = settings;

        Files.OpenRequested += (_, path) => OpenBotFile(path);
        Search.OpenRequested += (_, path) => OpenBotFile(path);

        _bots.Changed += (_, _) => UiContext.Run(Refresh);
        _activity?.Changed += (_, _) => UiContext.Run(RefreshStatusAndChats);
        ArcLanguage.Changed += (_, _) => UiContext.Run(() =>
        {
            BuildStaleDayOptions();
            Refresh();
        });

        BuildStaleDayOptions();
        Refresh();
    }

    public FileTreeViewModel Files { get; }
    public FileSearchViewModel Search { get; }

    public ObservableCollection<BotWorkItemCardViewModel> WorkItems { get; } = [];
    public ObservableCollection<string> MemoryLines { get; } = [];
    public ObservableCollection<BotConnectorItemViewModel> Connectors { get; } = [];
    public ObservableCollection<BotScheduleRowViewModel> ScheduledTasks { get; } = [];
    public ObservableCollection<BotChatRowViewModel> FilteredChats { get; } = [];
    public ObservableCollection<SelectOption> StaleDayOptions { get; } = [];
    public ObservableCollection<SelectOption> AgentOptions { get; } = [];
    public ObservableCollection<SelectOption> PermissionOptions { get; } = [];
    public ObservableCollection<SelectOption> WhoCanAskOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabKey), nameof(IsHomeTab), nameof(IsHomeOverview), nameof(IsHomeSettings), nameof(IsConnectorsTab), nameof(IsChatsTab), nameof(IsFilesTab))]
    public partial BotRightTab Tab { get; set; } = BotRightTab.Home;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeOverview), nameof(IsHomeSettings))]
    public partial bool IsSettingsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoBot), nameof(IsHomeTab), nameof(IsHomeOverview), nameof(IsHomeSettings), nameof(IsConnectorsTab), nameof(IsChatsTab), nameof(IsFilesTab))]
    public partial bool HasBot { get; private set; }

    [ObservableProperty] public partial string BotId { get; private set; } = "";
    [ObservableProperty] public partial string BotName { get; private set; } = "";
    [ObservableProperty] public partial string BotSlug { get; private set; } = "bot";
    [ObservableProperty] public partial string BotHomePath { get; private set; } = "";
    [ObservableProperty] public partial string BotInitial { get; private set; } = "B";
    [ObservableProperty] public partial IBrush BotAvatarBrush { get; private set; } = SolidColorBrush.Parse("#5b5bd6");
    [ObservableProperty] public partial Bitmap? BotAvatarImage { get; private set; }
    [ObservableProperty] public partial bool HasBotAvatarImage { get; private set; }
    [ObservableProperty] public partial bool HasBotAvatarIcon { get; private set; }
    [ObservableProperty] public partial bool ShowBotInitial { get; private set; } = true;
    [ObservableProperty] public partial LucideIconKind BotAvatarIcon { get; private set; } = LucideIconKind.Bot;
    [ObservableProperty] public partial IBrush BotAgentAccent { get; private set; } = SolidColorBrush.Parse("#d97757");
    [ObservableProperty] public partial string BotAgentName { get; private set; } = "";
    [ObservableProperty] public partial string SelectedAgentId { get; set; } = "";
    [ObservableProperty] public partial string BotHostDisplayName { get; private set; } = "";
    [ObservableProperty] public partial string PermissionModeLabel { get; private set; } = "";
    [ObservableProperty] public partial string SelectedPermissionMode { get; set; } = "same_as_chat";
    [ObservableProperty] public partial string WhoCanAskLabel { get; private set; } = "";
    [ObservableProperty] public partial string SelectedWhoCanAsk { get; set; } = "only_you";
    [ObservableProperty] public partial bool IsBotPaused { get; private set; }
    [ObservableProperty] public partial bool IsBotEnabled { get; set; } = true;
    [ObservableProperty] public partial bool IsBotWorking { get; private set; }
    [ObservableProperty] public partial string BotStatusText { get; private set; } = "";
    [ObservableProperty] public partial string BotStatusLine { get; private set; } = "";
    [ObservableProperty] public partial string HomeEmptyItemsDescription { get; private set; } = "";
    [ObservableProperty] public partial string HomeAutomationsHint { get; private set; } = "";
    [ObservableProperty] public partial bool LookAroundEnabled { get; set; } = true;
    [ObservableProperty] public partial string LookAroundIntervalText { get; private set; } = "";
    [ObservableProperty] public partial bool TidyUpMemoryEnabled { get; set; } = true;
    [ObservableProperty] public partial string StaleAfterDaysKey { get; set; } = "5";
    [ObservableProperty] public partial string MemoryFilesSummary { get; private set; } = "";
    [ObservableProperty] public partial string ConnectorsCountSummary { get; private set; } = "";
    [ObservableProperty] public partial string ToolsCountSummary { get; private set; } = "4";
    [ObservableProperty] public partial string SkillsCountSummary { get; private set; } = "";
    [ObservableProperty] public partial bool NotifyNeedsPermission { get; set; } = true;
    [ObservableProperty] public partial bool NotifyTaskCompleted { get; set; } = true;
    [ObservableProperty] public partial bool NotifyUnableToComplete { get; set; } = true;
    [ObservableProperty] public partial string SoulSummary { get; private set; } = "";
    [ObservableProperty] public partial bool HasSoul { get; private set; }
    [ObservableProperty] public partial bool HasWorkItems { get; private set; }
    [ObservableProperty] public partial bool HasScheduledTasks { get; private set; }
    [ObservableProperty] public partial string ScheduledTasksHint { get; private set; } = "";
    [ObservableProperty] public partial bool HasMemory { get; private set; }
    [ObservableProperty] public partial bool HasConnectors { get; private set; }
    [ObservableProperty] public partial string ConnectorsEmptyDescription { get; private set; } = "";
    [ObservableProperty] public partial string ChatSearchQuery { get; set; } = "";
    [ObservableProperty] public partial bool HasNoFilteredChats { get; private set; }
    [ObservableProperty] public partial int NeedsCount { get; private set; }

    public bool HasNoBot => !HasBot;
    public bool HasNeedsCount => NeedsCount > 0;
    public string NeedsCountText => NeedsCount > 99 ? "99+" : NeedsCount.ToString();
    public bool HasNoWorkItems => !HasWorkItems;
    public bool HasNoMemory => !HasMemory;
    public bool HasNoConnectors => !HasConnectors;
    public string PauseButtonTooltip => AppLangResources.Get(IsBotPaused ? AppLangResourceKind.BotMenuResume : AppLangResourceKind.BotMenuPause);

    public string TabKey
    {
        get => Tab switch
        {
            BotRightTab.Connectors => "connectors",
            BotRightTab.Chats => "chats",
            BotRightTab.Files => "files",
            _ => "home",
        };
        set => Tab = value switch
        {
            "connectors" => BotRightTab.Connectors,
            "chats" => BotRightTab.Chats,
            "files" => BotRightTab.Files,
            _ => BotRightTab.Home,
        };
    }

    public bool IsHomeTab => HasBot && Tab == BotRightTab.Home;
    public bool IsHomeOverview => IsHomeTab && !IsSettingsOpen;
    public bool IsHomeSettings => IsHomeTab && IsSettingsOpen;
    public bool IsConnectorsTab => HasBot && Tab == BotRightTab.Connectors;
    public bool IsChatsTab => HasBot && Tab == BotRightTab.Chats;
    public bool IsFilesTab => HasBot && Tab == BotRightTab.Files;

    partial void OnTabChanged(BotRightTab value)
    {
        if (value != BotRightTab.Home && IsSettingsOpen)
        {
            IsSettingsOpen = false;
        }
    }

    partial void OnChatSearchQueryChanged(string value) => RebuildChats();
    partial void OnNeedsCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasNeedsCount));
        OnPropertyChanged(nameof(NeedsCountText));
    }
    partial void OnIsBotPausedChanged(bool value) => OnPropertyChanged(nameof(PauseButtonTooltip));
    partial void OnHasWorkItemsChanged(bool value) => OnPropertyChanged(nameof(HasNoWorkItems));
    partial void OnHasMemoryChanged(bool value) => OnPropertyChanged(nameof(HasNoMemory));
    partial void OnHasConnectorsChanged(bool value) => OnPropertyChanged(nameof(HasNoConnectors));

    partial void OnIsBotEnabledChanged(bool value)
    {
        if (_hydrating || _bots.CurrentBot is not { } bot) return;
        _bots.SetBotPaused(bot.Id, !value);
    }

    partial void OnLookAroundEnabledChanged(bool value)
    {
        if (_hydrating || _bots.CurrentBot is not { } bot) return;
        _bots.UpdateBotSettings(bot.Id, b => b.LookAroundEnabled = value);
    }

    partial void OnTidyUpMemoryEnabledChanged(bool value)
    {
        if (_hydrating || _bots.CurrentBot is not { } bot) return;
        _bots.UpdateBotSettings(bot.Id, b => b.TidyUpMemoryEnabled = value);
    }

    partial void OnStaleAfterDaysKeyChanged(string value)
    {
        if (_hydrating || _bots.CurrentBot is not { } bot) return;
        if (int.TryParse(value, out var days) && days > 0)
        {
            _bots.UpdateBotSettings(bot.Id, b => b.StaleAfterDays = days);
        }
    }

    partial void OnNotifyNeedsPermissionChanged(bool value)
    {
        if (_hydrating || _bots.CurrentBot is not { } bot) return;
        _bots.UpdateBotSettings(bot.Id, b => b.NotifyNeedsPermission = value);
    }

    partial void OnNotifyTaskCompletedChanged(bool value)
    {
        if (_hydrating || _bots.CurrentBot is not { } bot) return;
        _bots.UpdateBotSettings(bot.Id, b => b.NotifyTaskCompleted = value);
    }

    partial void OnSelectedAgentIdChanged(string value)
    {
        if (_hydrating || string.IsNullOrWhiteSpace(value) || _bots.CurrentBot is not { } bot) return;
        if (string.Equals(bot.AgentId, value, StringComparison.OrdinalIgnoreCase)) return;
        _bots.UpdateBotSettings(bot.Id, b => b.AgentId = value);
    }

    partial void OnSelectedPermissionModeChanged(string value)
    {
        if (_hydrating || string.IsNullOrWhiteSpace(value) || _bots.CurrentBot is not { } bot) return;
        if (string.Equals(bot.PermissionMode, value, StringComparison.OrdinalIgnoreCase)) return;
        _bots.UpdateBotSettings(bot.Id, b => b.PermissionMode = value);
    }

    partial void OnSelectedWhoCanAskChanged(string value)
    {
        if (_hydrating || string.IsNullOrWhiteSpace(value) || _bots.CurrentBot is not { } bot) return;
        if (string.Equals(bot.WhoCanAsk, value, StringComparison.OrdinalIgnoreCase)) return;
        _bots.UpdateBotSettings(bot.Id, b => b.WhoCanAsk = value);
    }

    partial void OnNotifyUnableToCompleteChanged(bool value)
    {
        if (_hydrating || _bots.CurrentBot is not { } bot) return;
        _bots.UpdateBotSettings(bot.Id, b => b.NotifyUnableToComplete = value);
    }

    private void BuildStaleDayOptions()
    {
        StaleDayOptions.Clear();
        foreach (var d in new[] { 3, 5, 7, 14 })
        {
            StaleDayOptions.Add(new SelectOption(d.ToString(), AppLangResources.Format(AppLangResourceKind.BotSettingsDays, d)));
        }

        PermissionOptions.Clear();
        foreach (var mode in PermissionModes)
        {
            PermissionOptions.Add(new SelectOption(mode, FormatPermissionMode(mode)));
        }

        WhoCanAskOptions.Clear();
        foreach (var scope in WhoCanAskModes)
        {
            WhoCanAskOptions.Add(new SelectOption(scope, FormatWhoCanAsk(scope)));
        }
    }

    private void BuildAgentOptions(string? currentAgentId)
    {
        var agents = AvailableAgentsForCycle().ToList();
        if (!string.IsNullOrWhiteSpace(currentAgentId)
            && !agents.Any(a => string.Equals(a.Id, currentAgentId, StringComparison.OrdinalIgnoreCase)))
        {
            var currentDef = _catalog.Find(currentAgentId);
            if (currentDef is not null)
            {
                agents.Insert(0, currentDef);
            }
        }

        var next = agents.Select(a => new SelectOption(a.Id, a.Name)).ToList();
        if (next.Count == 0 && !string.IsNullOrWhiteSpace(currentAgentId))
        {
            next.Add(new SelectOption(currentAgentId, currentAgentId));
        }

        if (AgentOptions.Count == next.Count
            && !AgentOptions.Where((opt, i) => opt.Value != next[i].Value || opt.Label != next[i].Label).Any())
        {
            return;
        }

        AgentOptions.Clear();
        foreach (var opt in next)
        {
            AgentOptions.Add(opt);
        }
    }

    public void Refresh()
    {
        var bot = _bots.CurrentBot;
        if (bot is null)
        {
            HasBot = false;
            IsSettingsOpen = false;
            BotId = "";
            BotName = "";
            BotSlug = "bot";
            BotHomePath = "";
            WorkItems.Clear();
            MemoryLines.Clear();
            Connectors.Clear();
            FilteredChats.Clear();
            RefreshScheduledTasks();
            NeedsCount = 0;
            if (_loadedFilesRoot is not null)
            {
                _loadedFilesRoot = null;
                Files.Load(null);
                Search.SetRoot(null);
            }
            return;
        }

        if (!string.Equals(BotId, bot.Id, StringComparison.Ordinal))
        {
            IsSettingsOpen = false;
        }

        _hydrating = true;
        try
        {
            HasBot = true;
            BotId = bot.Id;
            BotName = bot.Name;
            BotSlug = bot.Slug;
            BotHomePath = _bots.Home.EnsureHome(bot);
            BotInitial = bot.Name.Length > 0 ? bot.Name[..1].ToUpperInvariant() : "B";
            BotAvatarBrush = SolidColorBrush.Parse(bot.Color);
            BotAvatarImage = _icons?.Load(bot.Image);
            HasBotAvatarImage = BotAvatarImage is not null;
            HasBotAvatarIcon = !HasBotAvatarImage && !string.IsNullOrWhiteSpace(bot.Icon);
            ShowBotInitial = !HasBotAvatarImage && !HasBotAvatarIcon;
            BotAvatarIcon = NewBotDialogViewModel.ParseIcon(bot.Icon, LucideIconKind.Bot);
            BotAgentAccent = NewBotDialogViewModel.AgentAccent(bot.AgentId);
            BotAgentName = _catalog.Find(bot.AgentId)?.Name ?? bot.AgentId;
            BuildAgentOptions(bot.AgentId);
            SelectedAgentId = bot.AgentId;
            BotHostDisplayName = FormatHostName(bot.HostDeviceKey);
            PermissionModeLabel = FormatPermissionMode(bot.PermissionMode);
            SelectedPermissionMode = Array.IndexOf(PermissionModes, bot.PermissionMode) >= 0 ? bot.PermissionMode : "same_as_chat";
            WhoCanAskLabel = FormatWhoCanAsk(bot.WhoCanAsk);
            SelectedWhoCanAsk = Array.IndexOf(WhoCanAskModes, bot.WhoCanAsk) >= 0 ? bot.WhoCanAsk : "only_you";
            IsBotPaused = bot.IsPaused;
            IsBotEnabled = !bot.IsPaused;
            LookAroundEnabled = bot.LookAroundEnabled;
            LookAroundIntervalText = AppLangResources.Format(AppLangResourceKind.BotSettingsEveryHours, Math.Max(1, bot.LookAroundIntervalHours));
            TidyUpMemoryEnabled = bot.TidyUpMemoryEnabled;
            StaleAfterDaysKey = (bot.StaleAfterDays is 3 or 5 or 7 or 14 ? bot.StaleAfterDays : 5).ToString();
            NotifyNeedsPermission = bot.NotifyNeedsPermission;
            NotifyTaskCompleted = bot.NotifyTaskCompleted;
            NotifyUnableToComplete = bot.NotifyUnableToComplete;

            HomeEmptyItemsDescription = AppLangResources.Format(AppLangResourceKind.BotHomeEmptyItemsDescription, bot.Name);
            HomeAutomationsHint = AppLangResources.Format(AppLangResourceKind.BotHomeAutomationsHint, bot.Name);
            ConnectorsEmptyDescription = AppLangResources.Format(AppLangResourceKind.BotConnectorsEmptyDescription, bot.Name);

            var memCount = _bots.Home.CountMemoryFiles(bot);
            MemoryFilesSummary = memCount > 0
                ? AppLangResources.Format(AppLangResourceKind.BotSettingsFileCount, memCount)
                : AppLangResources.Get(AppLangResourceKind.BotSettingsNone);

            var enabledConns = bot.Connectors.Count(c => c.Enabled);
            ConnectorsCountSummary = enabledConns > 0
                ? enabledConns.ToString()
                : AppLangResources.Get(AppLangResourceKind.BotSettingsNone);

            ToolsCountSummary = (4 + enabledConns).ToString();

            var skillCount = _bots.Home.CountSkillEntries(bot);
            SkillsCountSummary = skillCount > 0
                ? skillCount.ToString()
                : AppLangResources.Get(AppLangResourceKind.BotSettingsNone);
        }
        finally
        {
            _hydrating = false;
        }

        var soul = _bots.Home.ReadSoulSummary(bot);
        SoulSummary = soul;
        HasSoul = !string.IsNullOrWhiteSpace(soul);

        // Work items
        WorkItems.Clear();
        foreach (var item in bot.Items.Where(i => i.Status != BotWorkItemStatus.Canceled))
        {
            WorkItems.Add(new BotWorkItemCardViewModel(
                item,
                resolve: (id, opt) => _bots.ResolveWorkItem(bot.Id, id, opt),
                openChat: chatId => _bots.OpenChat(bot.Id, chatId)));
        }
        HasWorkItems = WorkItems.Count > 0;

        // Memory preview
        MemoryLines.Clear();
        foreach (var line in _bots.Home.ReadMemoryLines(bot))
        {
            MemoryLines.Add(line);
        }
        HasMemory = MemoryLines.Count > 0;

        // Connectors (preserve expanded edit state across non-save refreshes)
        var editingIds = Connectors.Where(c => c.IsEditing).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        Connectors.Clear();
        foreach (var conn in bot.Connectors)
        {
            var item = new BotConnectorItemViewModel(
                bot.Id,
                conn,
                _bots,
                setEnabled: (id, enabled) => _bots.SetConnectorEnabled(bot.Id, id, enabled),
                remove: id => _bots.RemoveConnector(bot.Id, id));
            if (editingIds.Contains(conn.Id))
            {
                item.IsEditing = true;
            }
            Connectors.Add(item);
        }
        HasConnectors = Connectors.Count > 0;

        RefreshScheduledTasks();

        // Home files tree
        if (!string.Equals(_loadedFilesRoot, BotHomePath, StringComparison.Ordinal))
        {
            _loadedFilesRoot = BotHomePath;
            Files.Load(BotHomePath);
            Search.SetRoot(BotHomePath);
        }

        RefreshStatusAndChats();
    }

    private void RefreshStatusAndChats()
    {
        var bot = _bots.CurrentBot;
        if (bot is null) return;

        var working = bot.Items.Any(i => i.Status == BotWorkItemStatus.Working)
            || bot.Chats.Any(c => c.Status == BotChatStatus.Working || (_activity is not null && _activity.Of(c.Id) == ItemActivity.Working));
        IsBotWorking = working;
        BotStatusText = bot.IsPaused
            ? AppLangResources.Get(AppLangResourceKind.BotStatusPaused)
            : working
                ? AppLangResources.Get(AppLangResourceKind.BotStatusWorking)
                : AppLangResources.Get(AppLangResourceKind.BotStatusIdle);

        if (!bot.IsPaused && bot.LookAroundEnabled)
        {
            var scheduled = bot.Schedules.FirstOrDefault(s => s.Kind == BotScheduleKinds.LookAround && s.Enabled)?.NextRunAt;
            var nextRun = scheduled is { } at ? BotScheduleFormatter.FormatWhen(at, DateTimeOffset.Now) : ComputeNextLookAroundTime(bot.LookAroundIntervalHours);
            var nextPart = AppLangResources.Format(AppLangResourceKind.BotHomeNextLookAround, nextRun);
            BotStatusLine = $"{BotStatusText} · {nextPart}";
        }
        else
        {
            BotStatusLine = $"{BotStatusText} · {BotAgentName}";
        }

        NeedsCount = bot.Items.Count(i => i.Status == BotWorkItemStatus.NeedsYou)
            + bot.Chats.Count(c => c.Status == BotChatStatus.NeedsYou || (_activity is not null && _activity.Of(c.Id) == ItemActivity.Waiting));

        RebuildChats();
    }

    private void RebuildChats()
    {
        FilteredChats.Clear();
        var bot = _bots.CurrentBot;
        if (bot is null)
        {
            HasNoFilteredChats = true;
            return;
        }

        var query = ChatSearchQuery.Trim();
        var activeChatId = _bots.CurrentChat?.Id;
        var ordered = bot.Chats
            .OrderByDescending(c => c.IsOpen)
            .ThenByDescending(c => c.LastActivityAt)
            .Where(c => query.Length == 0
                || c.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (c.Subtitle?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        foreach (var chat in ordered)
        {
            var chatWorking = chat.Status == BotChatStatus.Working
                || (_activity is not null && _activity.Of(chat.Id) == ItemActivity.Working);
            var chatNeeds = chat.Status == BotChatStatus.NeedsYou
                || (_activity is not null && _activity.Of(chat.Id) == ItemActivity.Waiting);

            FilteredChats.Add(new BotChatRowViewModel(
                bot,
                chat,
                isSelected: chat.Id == activeChatId,
                isWorking: chatWorking,
                needsInput: chatNeeds,
                open: row => _bots.OpenChat(row.BotId, row.ChatId),
                rename: RenameChatAsync,
                close: row => _bots.CloseChat(row.BotId, row.ChatId),
                delete: row => _bots.DeleteChat(row.BotId, row.ChatId)));
        }

        HasNoFilteredChats = FilteredChats.Count == 0;
    }

    /// <summary>Lists the schedules the bot (or its owner) made; built-in automations have their own rows.</summary>
    private void RefreshScheduledTasks()
    {
        ScheduledTasks.Clear();
        var bot = _bots.CurrentBot;
        if (bot is not null && _scheduler is not null)
        {
            var now = DateTimeOffset.Now;
            var custom = bot.Schedules
                .Where(s => !s.IsBuiltIn)
                .OrderByDescending(s => s.Enabled)
                .ThenBy(s => s.NextRunAt ?? DateTimeOffset.MaxValue);
            foreach (var schedule in custom)
            {
                ScheduledTasks.Add(new BotScheduleRowViewModel(
                    bot,
                    schedule,
                    now,
                    _scheduler,
                    openChat: chatId => _bots.OpenChat(bot.Id, chatId)));
            }
            ScheduledTasksHint = AppLangResources.Format(AppLangResourceKind.BotScheduledTasksHint, bot.Name);
        }
        HasScheduledTasks = ScheduledTasks.Count > 0;
    }

    private void OpenBotFile(string path)
    {
        if (_bots.CurrentBot is not { } bot) return;
        _bots.OpenFile(bot.Id, path);
    }

    private async Task RenameChatAsync(BotChatRowViewModel row)
    {
        var name = await _dialogs.AskNameAsync(
            AppLangResources.Get(AppLangResourceKind.BotChatRenameTitle),
            AppLangResources.Get(AppLangResourceKind.BotDialogNameLabel),
            row.Title,
            AppLangResources.Get(AppLangResourceKind.Save));
        if (name is not null)
        {
            _bots.RenameChat(row.BotId, row.ChatId, name);
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (_bots.CurrentBot is { } bot)
        {
            _hydrating = true;
            try
            {
                BuildAgentOptions(bot.AgentId);
                SelectedAgentId = bot.AgentId;
            }
            finally
            {
                _hydrating = false;
            }
        }
        Tab = BotRightTab.Home;
        IsSettingsOpen = true;
    }

    public void OpenSettingsFor(string botId)
    {
        if (_bots.CurrentBot?.Id != botId)
        {
            _bots.SelectBot(botId);
        }
        if (BotId != botId)
        {
            Refresh();
        }
        Tab = BotRightTab.Home;
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private async Task EditAvatarAsync()
    {
        if (_bots.CurrentBot is not { } bot) return;
        var choice = await _dialogs.AskBotAvatarAsync(bot);
        if (choice is null) return;
        var previousImage = bot.Image;
        _bots.SetBotAppearance(bot.Id, choice.Color, choice.Icon, choice.Image);
        if (previousImage is not null && !string.Equals(previousImage, choice.Image, StringComparison.Ordinal))
        {
            _icons?.Delete(previousImage);
        }
    }

    [RelayCommand]
    private async Task OpenShareAsync()
    {
        if (_bots.CurrentBot is not { } bot) return;
        await _dialogs.ShowBotShareAsync(bot, _bots);
    }

    [RelayCommand]
    private void CycleAgent()
    {
        if (_bots.CurrentBot is not { } bot || _catalog.Definitions.Count == 0) return;
        var list = AvailableAgentsForCycle();
        if (list.Count == 0) return;
        var idx = -1;
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Id, bot.AgentId, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }
        var next = list[(idx + 1) % list.Count];
        _bots.UpdateBotSettings(bot.Id, b => b.AgentId = next.Id);
    }

    private IReadOnlyList<Octopus.Core.Agents.AgentDefinition> AvailableAgentsForCycle()
    {
        if (_scanner is not null)
        {
            var agentSettings = _settings?.Current.Agents;
            var installed = _scanner.Current.Installations
                .Select(i => _catalog.Find(i.AgentId))
                .OfType<Octopus.Core.Agents.AgentDefinition>()
                .Where(d => agentSettings is null || agentSettings.ProfileOf(d.Id).Enabled)
                .DistinctBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (installed.Count > 0)
            {
                return installed;
            }
        }
        return _catalog.Definitions;
    }

    [RelayCommand]
    private void CyclePermissionMode()
    {
        if (_bots.CurrentBot is not { } bot) return;
        var idx = Array.IndexOf(PermissionModes, bot.PermissionMode);
        var next = PermissionModes[(idx + 1 + PermissionModes.Length) % PermissionModes.Length];
        _bots.UpdateBotSettings(bot.Id, b => b.PermissionMode = next);
    }

    [RelayCommand]
    private void CycleWhoCanAsk()
    {
        if (_bots.CurrentBot is not { } bot) return;
        var idx = Array.IndexOf(WhoCanAskModes, bot.WhoCanAsk);
        var next = WhoCanAskModes[(idx + 1 + WhoCanAskModes.Length) % WhoCanAskModes.Length];
        _bots.UpdateBotSettings(bot.Id, b => b.WhoCanAsk = next);
    }

    [RelayCommand]
    private void OpenConnectorsTab()
    {
        IsSettingsOpen = false;
        Tab = BotRightTab.Connectors;
    }

    [RelayCommand]
    private void RevealSkillsFolder()
    {
        if (_bots.CurrentBot is not { } bot) return;
        _bots.Home.EnsureHome(bot);
        _fileOpener.Reveal(_bots.Home.GetSkillsDirectory(bot));
    }

    [RelayCommand]
    private async Task RenameBotAsync()
    {
        if (_bots.CurrentBot is not { } bot) return;
        var name = await _dialogs.AskNameAsync(
            AppLangResources.Get(AppLangResourceKind.BotRenameTitle),
            AppLangResources.Get(AppLangResourceKind.BotDialogNameLabel),
            bot.Name,
            AppLangResources.Get(AppLangResourceKind.Save));
        if (!string.IsNullOrWhiteSpace(name))
        {
            _bots.RenameBot(bot.Id, name);
        }
    }

    [RelayCommand]
    private async Task DeleteBotAsync()
    {
        if (_bots.CurrentBot is not { } bot) return;
        var ok = await _dialogs.ConfirmAsync(
            AppLangResources.Format(AppLangResourceKind.BotDeleteConfirmTitle, bot.Name),
            AppLangResources.Get(AppLangResourceKind.BotDeleteConfirmBody),
            AppLangResources.Get(AppLangResourceKind.BotMenuDelete),
            isDanger: true);
        if (ok)
        {
            IsSettingsOpen = false;
            _bots.DeleteBot(bot.Id);
        }
    }

    [RelayCommand]
    private void EditSoul()
    {
        if (_bots.CurrentBot is not { } bot) return;
        _bots.Home.EnsureHome(bot);
        _bots.OpenFile(bot.Id, _bots.Home.GetSoulPath(bot));
    }

    [RelayCommand]
    private void EditMemory()
    {
        if (_bots.CurrentBot is not { } bot) return;
        _bots.Home.EnsureHome(bot);
        _bots.OpenFile(bot.Id, _bots.Home.GetMemoryPath(bot));
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (_bots.CurrentBot is not { } bot) return;
        _bots.SetBotPaused(bot.Id, !bot.IsPaused);
    }

    [RelayCommand]
    private void RevealHomeFolder()
    {
        if (_bots.CurrentBot is not { } bot) return;
        var path = _bots.Home.EnsureHome(bot);
        _fileOpener.Reveal(path);
    }

    [RelayCommand]
    private void NewChat()
    {
        if (_bots.CurrentBot is not { } bot) return;
        _bots.CreateChat(bot.Id);
    }

    [RelayCommand]
    private async Task OpenConnectorCatalogAsync()
    {
        if (_bots.CurrentBot is not { } bot) return;
        await _dialogs.ShowConnectorCatalogAsync(bot, _bots);
    }

    [RelayCommand]
    private void RefreshFiles()
    {
        if (string.IsNullOrWhiteSpace(BotHomePath)) return;
        Files.Load(BotHomePath);
    }

    private static string ComputeNextLookAroundTime(int intervalHours)
    {
        var step = Math.Clamp(intervalHours, 1, 24);
        var now = DateTimeOffset.Now;
        var nextHour = ((now.Hour / step) + 1) * step;
        var target = now.Date.AddHours(nextHour);
        return target.ToString("HH:mm");
    }

    private static string FormatHostName(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || string.Equals(key, "local", StringComparison.OrdinalIgnoreCase))
        {
            return AppLangResources.Get(AppLangResourceKind.NewProjectLocalMachine);
        }
        if (key.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase)) return key[4..];
        if (key.StartsWith("remote:", StringComparison.OrdinalIgnoreCase)) return key[7..];
        return key;
    }

    private static string FormatPermissionMode(string? mode) => mode switch
    {
        "ask" => AppLangResources.Get(AppLangResourceKind.BotSettingsPermissionAsk),
        "read_only" => AppLangResources.Get(AppLangResourceKind.BotSettingsPermissionReadOnly),
        "auto" => AppLangResources.Get(AppLangResourceKind.BotSettingsPermissionAuto),
        _ => AppLangResources.Get(AppLangResourceKind.BotSettingsPermissionSameAsChat),
    };

    private static string FormatWhoCanAsk(string? scope) => scope switch
    {
        "members" => AppLangResources.Get(AppLangResourceKind.BotSettingsWhoMembers),
        "anyone" => AppLangResources.Get(AppLangResourceKind.BotSettingsWhoAnyone),
        _ => AppLangResources.Get(AppLangResourceKind.BotSettingsWhoOnlyYou),
    };
}
