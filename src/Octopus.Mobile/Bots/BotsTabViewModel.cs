using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Bots;

/// <summary>One expandable Bot card inside the root Bots tab (Scheme A: embedded active chats + inline 1-tap approvals).</summary>
public sealed partial class BotAccordionCardViewModel : ObservableObject
{
    private readonly Action<RemoteBot, BotDetailTab> _openDetail;
    private readonly Action<RemoteBot> _openNewChat;
    private readonly Func<RemoteBotChat, Task> _openChat;
    private readonly Func<RemoteBot, RemoteBotWorkItem, string?, Task> _resolveItem;
    private readonly Action<string, bool> _rememberExpanded;

    public BotAccordionCardViewModel(
        RemoteBot bot,
        bool isExpanded,
        Action<RemoteBot, BotDetailTab> openDetail,
        Action<RemoteBot> openNewChat,
        Func<RemoteBotChat, Task> openChat,
        Func<RemoteBot, RemoteBotWorkItem, string?, Task> resolveItem,
        Action<string, bool> rememberExpanded)
    {
        Bot = bot;
        _openDetail = openDetail;
        _openNewChat = openNewChat;
        _openChat = openChat;
        _resolveItem = resolveItem;
        _rememberExpanded = rememberExpanded;

        Name = bot.Name;
        Initial = bot.Name.Length > 0 ? bot.Name[..1].ToUpperInvariant() : "B";
        AvatarBrush = BotUiHelpers.ParseBrush(bot.Color, "#5B5BD6");

        var openChats = bot.Chats.Where(c => c.IsOpen).OrderByDescending(c => c.IsHome).ThenByDescending(c => c.LastActivityAt).ToList();
        var pendingItems = bot.Items.Where(i => i.IsNeedsYou).ToList();
        var needsCount = pendingItems.Count + openChats.Count(c => c.Activity == SessionActivity.Waiting);
        var isOffline = !bot.Host.IsOnline;
        var isWorking = !isOffline && bot.IsWorking;

        HasAttention = needsCount > 0;
        CornerBadgeText = needsCount > 99 ? "99+" : needsCount.ToString();
        HasCornerBadge = needsCount > 0;
        IsOffline = isOffline;
        IsWorking = isWorking;

        StatusIsWaiting = !isOffline && needsCount > 0;
        StatusIsWorking = !isOffline && needsCount == 0 && isWorking;
        StatusIsMuted = isOffline || bot.IsPaused || (!StatusIsWaiting && !StatusIsWorking);
        StatusPillText = isOffline
            ? MobileLangResources.Get(MobileLangResourceKind.BotsStatusOffline)
            : bot.IsPaused
                ? MobileLangResources.Get(MobileLangResourceKind.BotsStatusPaused)
                : needsCount > 0
                    ? MobileLangResources.Format(MobileLangResourceKind.BotsStatusNeeds, needsCount)
                    : isWorking
                        ? MobileLangResources.Get(MobileLangResourceKind.BotsStatusWorking)
                        : $"{openChats.Count}";

        SubtitleText = MobileLangResources.Format(MobileLangResourceKind.BotsActiveSessionsSubtitle, bot.Host.Name, openChats.Count);
        AllSessionsButtonText = MobileLangResources.Format(MobileLangResourceKind.BotsAllSessionsCount, bot.Chats.Count);
        ConnectorsButtonText = MobileLangResources.Format(MobileLangResourceKind.BotsConnectorsCount, bot.Connectors.Count(c => c.Enabled));

        PendingWorkItems = [.. pendingItems.Select(item => new BotWorkItemRowViewModel(item, (it, opt) => _resolveItem(bot, it, opt)))];
        HasPendingWorkItems = PendingWorkItems.Count > 0;

        ActiveChats = [.. openChats.Select(chat => new BotChatItemViewModel(chat, _openChat, _ => _openDetail(bot, BotDetailTab.Sessions), _openChat))];
        HasActiveChats = ActiveChats.Count > 0;

        IsExpanded = isExpanded;
    }

    public RemoteBot Bot { get; }
    public string Name { get; }
    public string Initial { get; }
    public IBrush AvatarBrush { get; }
    public string SubtitleText { get; }
    public bool HasAttention { get; }
    public bool HasCornerBadge { get; }
    public string CornerBadgeText { get; }
    public bool IsOffline { get; }
    public bool IsWorking { get; }
    public bool StatusIsWaiting { get; }
    public bool StatusIsWorking { get; }
    public bool StatusIsMuted { get; }
    public string StatusPillText { get; }
    public string AllSessionsButtonText { get; }
    public string ConnectorsButtonText { get; }
    public IReadOnlyList<BotWorkItemRowViewModel> PendingWorkItems { get; }
    public bool HasPendingWorkItems { get; }
    public IReadOnlyList<BotChatItemViewModel> ActiveChats { get; }
    public bool HasActiveChats { get; }

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        _rememberExpanded(Bot.Id, IsExpanded);
    }

    [RelayCommand]
    private void OpenSessions() => _openDetail(Bot, BotDetailTab.Sessions);

    [RelayCommand]
    private void OpenHome() => _openDetail(Bot, BotDetailTab.Home);

    [RelayCommand]
    private void OpenConnectors() => _openDetail(Bot, BotDetailTab.Connectors);

    [RelayCommand]
    private void OpenSettings() => _openDetail(Bot, BotDetailTab.Settings);

    [RelayCommand]
    private void NewChat() => _openNewChat(Bot);
}

/// <summary>
/// ViewModel for the root Bots tab (Scheme A): cross-host Bot aggregation, accordion session cards,
/// 1-tap work item approval, New Bot sheet, Import from <c>octopus://bot/import</c> sheet, and quick New Chat sheet.
/// </summary>
public sealed partial class BotsTabViewModel : ObservableObject, IDisposable
{
    private static readonly (string Id, string Color, MobileLangResourceKind Label)[] Templates =
    [
        ("pr", "#3B82D6", MobileLangResourceKind.BotsTemplatePr),
        ("ci", "#D97757", MobileLangResourceKind.BotsTemplateCi),
        ("daily", "#2F9E63", MobileLangResourceKind.BotsTemplateDaily),
        ("blank", "#5B5BD6", MobileLangResourceKind.BotsTemplateBlank),
    ];

    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;
    private readonly Dictionary<string, bool> _expandedMemory = new(StringComparer.Ordinal);
    private readonly List<RemoteHost> _watchedHosts = [];
    private readonly List<RemoteBot> _watchedBots = [];

    public BotsTabViewModel(IRemoteWorkspace workspace, INavigator navigator)
    {
        _workspace = workspace;
        _navigator = navigator;
        TemplateLabels = [.. Templates.Select(t => MobileLangResources.Get(t.Label))];

        _workspace.Hosts.CollectionChanged += OnStructureChanged;
        Rebuild();
    }

    public event Action? SummaryChanged;

    public IReadOnlyList<string> TemplateLabels { get; }
    public ObservableCollection<BotAccordionCardViewModel> VisibleBots { get; } = [];
    public ObservableCollection<string> OnlineHostNames { get; } = [];
    public ObservableCollection<string> AvailableAgentNames { get; } = [];
    public ObservableCollection<string> QuickChatAgentNames { get; } = [];

    [ObservableProperty] public partial string SearchQuery { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll), nameof(IsFilterNeeds), nameof(IsFilterWorking))]
    public partial int SelectedFilter { get; set; } // 0 = All, 1 = Needs, 2 = Working

    [ObservableProperty] public partial int TotalBotCount { get; private set; }
    [ObservableProperty] public partial int TotalNeedsCount { get; private set; }
    [ObservableProperty] public partial int TotalWorkingCount { get; private set; }
    [ObservableProperty] public partial bool HasBots { get; private set; }
    [ObservableProperty] public partial bool HasVisibleBots { get; private set; }
    [ObservableProperty] public partial bool HasOnlineHosts { get; private set; }
    [ObservableProperty] public partial string FilterAllText { get; private set; } = "";
    [ObservableProperty] public partial string FilterNeedsText { get; private set; } = "";
    [ObservableProperty] public partial string FilterWorkingText { get; private set; } = "";
    [ObservableProperty] public partial string? ErrorMessage { get; private set; }

    // New Bot Sheet
    [ObservableProperty] public partial bool IsNewBotOpen { get; set; }
    [ObservableProperty] public partial string NewBotName { get; set; } = "";
    [ObservableProperty] public partial int SelectedHostIndex { get; set; }
    [ObservableProperty] public partial int SelectedAgentIndex { get; set; }
    [ObservableProperty] public partial int SelectedTemplateIndex { get; set; }
    [ObservableProperty] public partial string NewBotSoul { get; set; } = "";

    // Import Bot Sheet
    [ObservableProperty] public partial bool IsImportOpen { get; set; }
    [ObservableProperty] public partial string ImportLink { get; set; } = "";

    // Quick New Chat Sheet
    [ObservableProperty] public partial bool IsNewChatOpen { get; set; }
    [ObservableProperty] public partial RemoteBot? NewChatTargetBot { get; private set; }
    [ObservableProperty] public partial int SelectedQuickChatAgentIndex { get; set; }
    [ObservableProperty] public partial string QuickChatTitle { get; set; } = "";

    public bool IsEmpty => !HasBots;
    public bool IsFilteredEmpty => HasBots && !HasVisibleBots;
    public bool IsFilterAll => SelectedFilter == 0;
    public bool IsFilterNeeds => SelectedFilter == 1;
    public bool IsFilterWorking => SelectedFilter == 2;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasNoOnlineHosts => !HasOnlineHosts;
    public string QuickChatSheetTitle => NewChatTargetBot is null
        ? MobileLangResources.Get(MobileLangResourceKind.BotsNewChatTitle)
        : $"{NewChatTargetBot.Name} · {MobileLangResources.Get(MobileLangResourceKind.BotsNewChatTitle)}";

    partial void OnSearchQueryChanged(string value) => RebuildVisible();
    partial void OnSelectedFilterChanged(int value) => RebuildVisible();
    partial void OnSelectedHostIndexChanged(int value) => RebuildAgentsForSelectedHost();
    partial void OnHasBotsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFilteredEmpty));
    }
    partial void OnHasVisibleBotsChanged(bool value) => OnPropertyChanged(nameof(IsFilteredEmpty));
    partial void OnHasOnlineHostsChanged(bool value) => OnPropertyChanged(nameof(HasNoOnlineHosts));
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    private void SelectFilterAll() => SelectedFilter = 0;

    [RelayCommand]
    private void SelectFilterNeeds() => SelectedFilter = 1;

    [RelayCommand]
    private void SelectFilterWorking() => SelectedFilter = 2;

    [RelayCommand]
    private void OpenNewBot()
    {
        ErrorMessage = null;
        NewBotName = "";
        NewBotSoul = "";
        SelectedTemplateIndex = 0;
        RebuildHostChoices();
        IsNewBotOpen = true;
    }

    [RelayCommand]
    private void OpenImportBot()
    {
        ErrorMessage = null;
        ImportLink = "";
        RebuildHostChoices();
        IsImportOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmCreateBotAsync()
    {
        var host = GetSelectedOnlineHost();
        if (host is null)
        {
            ErrorMessage = MobileLangResources.Get(MobileLangResourceKind.BotsNoOnlineHostHint);
            return;
        }
        var tpl = SelectedTemplateIndex >= 0 && SelectedTemplateIndex < Templates.Length ? Templates[SelectedTemplateIndex] : Templates[0];
        var agentId = SelectedAgentIndex >= 0 && SelectedAgentIndex < host.Agents.Count ? host.Agents[SelectedAgentIndex].Id : host.Agents.FirstOrDefault()?.Id;
        var name = string.IsNullOrWhiteSpace(NewBotName) ? MobileLangResources.Get(tpl.Label) : NewBotName.Trim();
        IsNewBotOpen = false;
        await RunAsync(async () =>
        {
            var created = await _workspace.CreateBotAsync(host, new CreateRemoteBotRequest(name, agentId, tpl.Color, tpl.Id, string.IsNullOrWhiteSpace(NewBotSoul) ? null : NewBotSoul.Trim()));
            _expandedMemory[created.Id] = true;
            Rebuild();
            _navigator.ShowBot(created, BotDetailTab.Sessions);
        });
    }

    [RelayCommand]
    private async Task ConfirmImportBotAsync()
    {
        var host = GetSelectedOnlineHost();
        if (host is null)
        {
            ErrorMessage = MobileLangResources.Get(MobileLangResourceKind.BotsNoOnlineHostHint);
            return;
        }
        var link = ImportLink.Trim();
        if (link.Length == 0) return;
        await RunAsync(async () =>
        {
            var imported = await _workspace.ImportBotAsync(host, link);
            IsImportOpen = false;
            _expandedMemory[imported.Id] = true;
            Rebuild();
            _navigator.ShowBot(imported, BotDetailTab.Sessions);
        });
    }

    [RelayCommand]
    private async Task ConfirmQuickNewChatAsync()
    {
        var bot = NewChatTargetBot;
        if (bot is null) return;
        var agents = bot.Host.Agents;
        var agentId = SelectedQuickChatAgentIndex >= 0 && SelectedQuickChatAgentIndex < agents.Count
            ? agents[SelectedQuickChatAgentIndex].Id
            : bot.AgentId;
        var title = string.IsNullOrWhiteSpace(QuickChatTitle) ? null : QuickChatTitle.Trim();
        IsNewChatOpen = false;
        await RunAsync(async () =>
        {
            var chat = await _workspace.CreateBotChatAsync(bot, new CreateRemoteBotChatRequest(agentId, title));
            _expandedMemory[bot.Id] = true;
            Rebuild();
            _navigator.ShowSession(chat.ToRemoteSession());
        });
    }

    private void OpenQuickNewChat(RemoteBot bot)
    {
        ErrorMessage = null;
        NewChatTargetBot = bot;
        QuickChatTitle = "";
        QuickChatAgentNames.Clear();
        var defaultIdx = 0;
        for (var i = 0; i < bot.Host.Agents.Count; i++)
        {
            QuickChatAgentNames.Add(bot.Host.Agents[i].Name);
            if (string.Equals(bot.Host.Agents[i].Id, bot.AgentId, StringComparison.OrdinalIgnoreCase))
            {
                defaultIdx = i;
            }
        }
        if (QuickChatAgentNames.Count == 0) QuickChatAgentNames.Add(bot.AgentId);
        SelectedQuickChatAgentIndex = defaultIdx;
        OnPropertyChanged(nameof(QuickChatSheetTitle));
        IsNewChatOpen = true;
    }

    private async Task OpenChatAsync(RemoteBotChat chat)
    {
        await RunAsync(async () =>
        {
            var opened = chat.IsOpen ? chat : await _workspace.OpenBotChatAsync(chat);
            _navigator.ShowSession(opened.ToRemoteSession());
        });
    }

    private async Task ResolveWorkItemAsync(RemoteBot bot, RemoteBotWorkItem item, string? chosenOption)
    {
        await RunAsync(async () =>
        {
            await _workspace.ResolveBotWorkItemAsync(bot, item, chosenOption);
            Rebuild();
        });
    }

    public void Rebuild()
    {
        UnwatchAll();
        WatchAll();
        RebuildHostChoices();
        RebuildVisible();
    }

    private void RebuildVisible()
    {
        var allBots = _workspace.Hosts
            .Where(h => h.IsPaired)
            .SelectMany(h => h.Bots)
            .OrderByDescending(b => b.NeedsAttentionCount > 0)
            .ThenByDescending(b => b.IsWorking)
            .ThenByDescending(b => b.Host.IsOnline)
            .ToList();

        TotalBotCount = allBots.Count;
        TotalNeedsCount = allBots.Sum(b => b.NeedsAttentionCount);
        TotalWorkingCount = allBots.Count(b => b.IsWorking);
        HasBots = allBots.Count > 0;

        FilterAllText = MobileLangResources.Format(MobileLangResourceKind.BotsFilterAll, TotalBotCount);
        FilterNeedsText = MobileLangResources.Format(MobileLangResourceKind.BotsFilterNeeds, TotalNeedsCount);
        FilterWorkingText = MobileLangResources.Format(MobileLangResourceKind.BotsFilterWorking, TotalWorkingCount);

        var q = SearchQuery.Trim();
        var filtered = allBots.Where(b =>
        {
            if (SelectedFilter == 1 && b.NeedsAttentionCount == 0) return false;
            if (SelectedFilter == 2 && !b.IsWorking) return false;
            if (q.Length == 0) return true;
            return b.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || b.Host.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || b.Chats.Any(c => c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || (c.Subtitle?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }).ToList();

        VisibleBots.Clear();
        foreach (var bot in filtered)
        {
            var expanded = _expandedMemory.TryGetValue(bot.Id, out var saved)
                ? saved
                : (bot.NeedsAttentionCount > 0 || bot.IsWorking || filtered.Count <= 3);
            VisibleBots.Add(new BotAccordionCardViewModel(
                bot,
                expanded,
                openDetail: (b, tab) => _navigator.ShowBot(b, tab),
                openNewChat: OpenQuickNewChat,
                openChat: OpenChatAsync,
                resolveItem: ResolveWorkItemAsync,
                rememberExpanded: (id, exp) => _expandedMemory[id] = exp));
        }

        HasVisibleBots = VisibleBots.Count > 0;
        SummaryChanged?.Invoke();
    }

    private List<RemoteHost> GetOnlinePairedHosts() =>
        [.. _workspace.Hosts.Where(h => h.IsPaired && h.IsOnline)];

    private RemoteHost? GetSelectedOnlineHost()
    {
        var online = GetOnlinePairedHosts();
        if (online.Count == 0) return null;
        return SelectedHostIndex >= 0 && SelectedHostIndex < online.Count ? online[SelectedHostIndex] : online[0];
    }

    private void RebuildHostChoices()
    {
        var online = GetOnlinePairedHosts();
        OnlineHostNames.Clear();
        foreach (var host in online) OnlineHostNames.Add(host.Name);
        HasOnlineHosts = online.Count > 0;
        if (SelectedHostIndex < 0 || SelectedHostIndex >= online.Count) SelectedHostIndex = 0;
        RebuildAgentsForSelectedHost();
    }

    private void RebuildAgentsForSelectedHost()
    {
        AvailableAgentNames.Clear();
        var host = GetSelectedOnlineHost();
        if (host is not null)
        {
            foreach (var agent in host.Agents) AvailableAgentNames.Add(agent.Name);
        }
        if (AvailableAgentNames.Count == 0) AvailableAgentNames.Add("Claude");
        if (SelectedAgentIndex < 0 || SelectedAgentIndex >= AvailableAgentNames.Count) SelectedAgentIndex = 0;
    }

    private async Task RunAsync(Func<Task> action)
    {
        ErrorMessage = null;
        try
        {
            await action();
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
    }

    private void WatchAll()
    {
        foreach (var host in _workspace.Hosts)
        {
            _watchedHosts.Add(host);
            host.PropertyChanged += OnHostChanged;
            host.Bots.CollectionChanged += OnStructureChanged;
            foreach (var bot in host.Bots)
            {
                _watchedBots.Add(bot);
                bot.PropertyChanged += OnBotChanged;
                bot.Chats.CollectionChanged += OnStructureChanged;
                bot.Items.CollectionChanged += OnStructureChanged;
                bot.Connectors.CollectionChanged += OnStructureChanged;
            }
        }
    }

    private void UnwatchAll()
    {
        foreach (var bot in _watchedBots)
        {
            bot.PropertyChanged -= OnBotChanged;
            bot.Chats.CollectionChanged -= OnStructureChanged;
            bot.Items.CollectionChanged -= OnStructureChanged;
            bot.Connectors.CollectionChanged -= OnStructureChanged;
        }
        _watchedBots.Clear();
        foreach (var host in _watchedHosts)
        {
            host.PropertyChanged -= OnHostChanged;
            host.Bots.CollectionChanged -= OnStructureChanged;
        }
        _watchedHosts.Clear();
    }

    private void OnStructureChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();
    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemoteHost.Link) or nameof(RemoteHost.IsPaired)) Rebuild();
    }
    private void OnBotChanged(object? sender, PropertyChangedEventArgs e) => RebuildVisible();

    public void Dispose()
    {
        _workspace.Hosts.CollectionChanged -= OnStructureChanged;
        UnwatchAll();
    }
}
