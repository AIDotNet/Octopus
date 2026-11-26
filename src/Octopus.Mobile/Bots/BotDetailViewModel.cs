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

public enum BotDetailTab
{
    Sessions = 0,
    Home = 1,
    Connectors = 2,
    Settings = 3,
}

public sealed partial class BotWorkItemOptionViewModel(string label, Func<string, Task> choose) : ObservableObject
{
    public string Label { get; } = label;

    [RelayCommand]
    private Task ChooseAsync() => choose(Label);
}

public sealed class BotWorkItemRowViewModel
{
    public BotWorkItemRowViewModel(RemoteBotWorkItem item, Func<RemoteBotWorkItem, string?, Task> resolve)
    {
        Item = item;
        NumberText = $"#{item.Number}";
        Title = item.Title;
        Question = item.Question ?? "";
        HasQuestion = !string.IsNullOrWhiteSpace(item.Question);
        IsNeedsYou = item.IsNeedsYou;
        IsWorking = item.IsWorking;
        StatusText = item.IsNeedsYou
            ? MobileLangResources.Format(MobileLangResourceKind.BotsStatusNeeds, 1)
            : item.IsWorking
                ? MobileLangResources.Get(MobileLangResourceKind.BotsStatusWorking)
                : item.Status;
        Options = [.. item.Options.Select(opt => new BotWorkItemOptionViewModel(opt, chosen => resolve(item, chosen)))];
    }

    public RemoteBotWorkItem Item { get; }
    public string NumberText { get; }
    public string Title { get; }
    public string Question { get; }
    public bool HasQuestion { get; }
    public bool IsNeedsYou { get; }
    public bool IsWorking { get; }
    public string StatusText { get; }
    public IReadOnlyList<BotWorkItemOptionViewModel> Options { get; }
}

public sealed partial class BotChatItemViewModel : ObservableObject
{
    private readonly Func<RemoteBotChat, Task> _open;
    private readonly Action<RemoteBotChat> _openMenu;
    private readonly Func<RemoteBotChat, Task> _reopen;

    public BotChatItemViewModel(
        RemoteBotChat chat,
        Func<RemoteBotChat, Task> open,
        Action<RemoteBotChat> openMenu,
        Func<RemoteBotChat, Task> reopen)
    {
        Chat = chat;
        _open = open;
        _openMenu = openMenu;
        _reopen = reopen;
        Title = chat.Title;
        Subtitle = string.IsNullOrWhiteSpace(chat.Subtitle) ? (chat.AgentId ?? chat.Bot.AgentId) : chat.Subtitle!;
        IsHome = chat.IsHome;
        IsOpen = chat.IsOpen;
        IsClosed = !chat.IsOpen;
        IsWaiting = chat.IsOpen && chat.Activity == SessionActivity.Waiting;
        IsWorking = chat.IsOpen && chat.Activity == SessionActivity.Working;
        HasStatus = IsWaiting || IsWorking;
        StatusText = IsWaiting
            ? MobileLangResources.Get(MobileLangResourceKind.StatusWaiting)
            : IsWorking
                ? MobileLangResources.Get(MobileLangResourceKind.StatusWorking)
                : "";
        AgentInitial = BotUiHelpers.AgentInitial(chat.AgentId ?? chat.Bot.AgentId);
        AgentTint = BotUiHelpers.AgentBrush(chat.AgentId ?? chat.Bot.AgentId);
        TimeText = BotUiHelpers.FormatRelativeTime(chat.ClosedAt ?? chat.LastActivityAt);
    }

    public RemoteBotChat Chat { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public bool IsHome { get; }
    public bool IsOpen { get; }
    public bool IsClosed { get; }
    public bool IsWaiting { get; }
    public bool IsWorking { get; }
    public bool HasStatus { get; }
    public string StatusText { get; }
    public string AgentInitial { get; }
    public IBrush AgentTint { get; }
    public string TimeText { get; }

    [RelayCommand]
    private Task OpenAsync() => _open(Chat);

    [RelayCommand]
    private void OpenMenu() => _openMenu(Chat);

    [RelayCommand]
    private Task ReopenAsync() => _reopen(Chat);
}

public sealed partial class BotConnectorRowViewModel : ObservableObject
{
    private readonly Func<RemoteBotConnector, bool, Task>? _toggle;

    public BotConnectorRowViewModel(RemoteBotConnector connector, Func<RemoteBotConnector, bool, Task> toggle)
    {
        Connector = connector;
        Name = connector.DisplayName;
        Url = string.IsNullOrWhiteSpace(connector.Url) ? connector.AppId : connector.Url!;
        TransportBadge = string.Equals(connector.Transport, "sse", StringComparison.OrdinalIgnoreCase) ? "SSE" : "HTTP";
        BadgeText = Name.Length > 0 ? Name[..Math.Min(2, Name.Length)].ToUpperInvariant() : "MC";
        BadgeBrush = BotUiHelpers.ParseBrush(BotUiHelpers.ConnectorColor(connector.AppId), "#5B5BD6");
        IsEnabled = connector.Enabled;
        _toggle = toggle;
    }

    public RemoteBotConnector Connector { get; }
    public string Name { get; }
    public string Url { get; }
    public string TransportBadge { get; }
    public string BadgeText { get; }
    public IBrush BadgeBrush { get; }

    [ObservableProperty] public partial bool IsEnabled { get; set; }

    partial void OnIsEnabledChanged(bool value)
    {
        if (_toggle is not null && Connector.Enabled != value)
        {
            _ = _toggle(Connector, value);
        }
    }
}

/// <summary>
/// Full mobile workspace for a single <see cref="RemoteBot"/>:
/// <c>Chats (Sessions)</c> | <c>Home</c> | <c>Connectors</c> | <c>Settings</c>.
/// </summary>
public sealed partial class BotDetailViewModel : ObservableObject, IDisposable
{
    private readonly RemoteBot _bot;
    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;
    private bool _hydrating;

    public BotDetailViewModel(
        RemoteBot bot,
        IRemoteWorkspace workspace,
        INavigator navigator,
        BotDetailTab initialTab = BotDetailTab.Sessions)
    {
        _bot = bot;
        _workspace = workspace;
        _navigator = navigator;
        SelectedTab = (int)initialTab;

        TabLabels =
        [
            MobileLangResources.Get(MobileLangResourceKind.BotsTabSessions),
            MobileLangResources.Get(MobileLangResourceKind.BotsTabHome),
            MobileLangResources.Get(MobileLangResourceKind.BotsTabConnectors),
            MobileLangResources.Get(MobileLangResourceKind.BotsTabSettings),
        ];

        _bot.PropertyChanged += OnBotChanged;
        _bot.Chats.CollectionChanged += OnCollectionChanged;
        _bot.Items.CollectionChanged += OnCollectionChanged;
        _bot.Connectors.CollectionChanged += OnCollectionChanged;

        RebuildAll();
    }

    public RemoteBot Bot => _bot;
    public IReadOnlyList<string> TabLabels { get; }

    public ObservableCollection<BotChatItemViewModel> ActiveChats { get; } = [];
    public ObservableCollection<BotChatItemViewModel> ClosedChats { get; } = [];
    public ObservableCollection<BotWorkItemRowViewModel> WorkItems { get; } = [];
    public ObservableCollection<BotConnectorRowViewModel> Connectors { get; } = [];
    public ObservableCollection<string> AgentNames { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSessionsTab), nameof(IsHomeTab), nameof(IsConnectorsTab), nameof(IsSettingsTab))]
    public partial int SelectedTab { get; set; }

    [ObservableProperty] public partial string BotName { get; private set; } = "";
    [ObservableProperty] public partial string BotSubtitle { get; private set; } = "";
    [ObservableProperty] public partial string BotInitial { get; private set; } = "B";
    [ObservableProperty] public partial IBrush BotAvatarBrush { get; private set; } = SolidColorBrush.Parse("#5B5BD6");
    [ObservableProperty] public partial string HostName { get; private set; } = "";
    [ObservableProperty] public partial string AgentName { get; private set; } = "";
    [ObservableProperty] public partial string SoulSummary { get; private set; } = "";
    [ObservableProperty] public partial bool HasSoul { get; private set; }
    [ObservableProperty] public partial string ShareLinkText { get; private set; } = "";
    [ObservableProperty] public partial bool HasShareLink { get; private set; }

    [ObservableProperty] public partial bool IsBotEnabled { get; set; } = true;
    [ObservableProperty] public partial bool LookAroundEnabled { get; set; } = true;
    [ObservableProperty] public partial bool TidyUpMemoryEnabled { get; set; } = true;

    // Sessions management filters
    [ObservableProperty] public partial string ChatSearchQuery { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatFilterAll), nameof(IsChatFilterActive), nameof(IsChatFilterClosed))]
    public partial int ChatFilter { get; set; } // 0 = All, 1 = Active, 2 = Closed

    [ObservableProperty] public partial string FilterAllText { get; private set; } = "";
    [ObservableProperty] public partial string FilterActiveText { get; private set; } = "";
    [ObservableProperty] public partial string FilterClosedText { get; private set; } = "";
    [ObservableProperty] public partial string ActiveChatsHeader { get; private set; } = "";
    [ObservableProperty] public partial string ClosedChatsHeader { get; private set; } = "";
    [ObservableProperty] public partial bool HasActiveChats { get; private set; }
    [ObservableProperty] public partial bool HasClosedChats { get; private set; }
    [ObservableProperty] public partial bool IsChatsEmpty { get; private set; }
    [ObservableProperty] public partial bool HasWorkItems { get; private set; }
    [ObservableProperty] public partial bool HasConnectors { get; private set; }
    [ObservableProperty] public partial string? ErrorMessage { get; private set; }

    // Overlays: New chat, Chat ActionSheet, Rename chat, Delete Bot confirmation
    [ObservableProperty] public partial bool IsNewChatOpen { get; set; }
    [ObservableProperty] public partial int SelectedAgentIndex { get; set; }
    [ObservableProperty] public partial string NewChatTitle { get; set; } = "";

    [ObservableProperty] public partial bool IsChatMenuOpen { get; set; }
    [ObservableProperty] public partial RemoteBotChat? MenuChat { get; private set; }
    public string MenuChatTitle => MenuChat?.Title ?? "";
    public bool MenuChatIsOpen => MenuChat?.IsOpen ?? false;
    public bool MenuChatIsClosed => MenuChat is { IsOpen: false };
    public bool MenuChatCanDelete => MenuChat is { IsHome: false };

    [ObservableProperty] public partial bool IsRenameOpen { get; set; }
    [ObservableProperty] public partial string RenameTitle { get; set; } = "";

    [ObservableProperty] public partial bool IsDeleteConfirmOpen { get; set; }
    public string DeleteConfirmTitle => MobileLangResources.Format(MobileLangResourceKind.BotsSettingsDeleteConfirm, BotName, HostName);

    public bool IsSessionsTab => SelectedTab == (int)BotDetailTab.Sessions;
    public bool IsHomeTab => SelectedTab == (int)BotDetailTab.Home;
    public bool IsConnectorsTab => SelectedTab == (int)BotDetailTab.Connectors;
    public bool IsSettingsTab => SelectedTab == (int)BotDetailTab.Settings;

    public bool IsChatFilterAll => ChatFilter == 0;
    public bool IsChatFilterActive => ChatFilter == 1;
    public bool IsChatFilterClosed => ChatFilter == 2;
    public bool HasNoWorkItems => !HasWorkItems;
    public bool HasNoConnectors => !HasConnectors;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    partial void OnChatSearchQueryChanged(string value) => RebuildChats();
    partial void OnChatFilterChanged(int value) => RebuildChats();
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnHasWorkItemsChanged(bool value) => OnPropertyChanged(nameof(HasNoWorkItems));
    partial void OnHasConnectorsChanged(bool value) => OnPropertyChanged(nameof(HasNoConnectors));

    partial void OnIsBotEnabledChanged(bool value)
    {
        if (_hydrating) return;
        _ = RunAsync(() => _workspace.UpdateBotAsync(_bot, isPaused: !value));
    }

    partial void OnLookAroundEnabledChanged(bool value)
    {
        if (_hydrating) return;
        _ = RunAsync(() => _workspace.UpdateBotAsync(_bot, lookAroundEnabled: value));
    }

    partial void OnTidyUpMemoryEnabledChanged(bool value)
    {
        if (_hydrating) return;
        _ = RunAsync(() => _workspace.UpdateBotAsync(_bot, tidyUpMemoryEnabled: value));
    }

    [RelayCommand]
    private void Back() => _navigator.Back();

    [RelayCommand]
    private void SelectChatFilterAll() => ChatFilter = 0;

    [RelayCommand]
    private void SelectChatFilterActive() => ChatFilter = 1;

    [RelayCommand]
    private void SelectChatFilterClosed() => ChatFilter = 2;

    [RelayCommand]
    private void OpenNewChat()
    {
        ErrorMessage = null;
        NewChatTitle = "";
        RebuildAgentOptions();
        IsNewChatOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmCreateChatAsync()
    {
        var agents = _bot.Host.Agents;
        var chosenAgentId = SelectedAgentIndex >= 0 && SelectedAgentIndex < agents.Count
            ? agents[SelectedAgentIndex].Id
            : _bot.AgentId;
        var title = string.IsNullOrWhiteSpace(NewChatTitle) ? null : NewChatTitle.Trim();
        IsNewChatOpen = false;
        await RunAsync(async () =>
        {
            var created = await _workspace.CreateBotChatAsync(_bot, new CreateRemoteBotChatRequest(chosenAgentId, title));
            RebuildChats();
            _navigator.ShowSession(created.ToRemoteSession());
        });
    }

    [RelayCommand]
    private Task OpenMenuChatAsync()
    {
        var target = MenuChat;
        IsChatMenuOpen = false;
        return target is null ? Task.CompletedTask : OpenChatAsync(target);
    }

    [RelayCommand]
    private void StartRenameMenuChat()
    {
        if (MenuChat is null) return;
        RenameTitle = MenuChat.Title;
        IsChatMenuOpen = false;
        IsRenameOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmRenameChatAsync()
    {
        var target = MenuChat;
        var next = RenameTitle.Trim();
        IsRenameOpen = false;
        if (target is null || next.Length == 0) return;
        await RunAsync(async () =>
        {
            await _workspace.RenameBotChatAsync(target, next);
            RebuildChats();
        });
    }

    [RelayCommand]
    private async Task CloseMenuChatAsync()
    {
        var target = MenuChat;
        IsChatMenuOpen = false;
        if (target is null) return;
        await RunAsync(async () =>
        {
            await _workspace.CloseBotChatAsync(target);
            RebuildChats();
        });
    }

    [RelayCommand]
    private async Task ReopenMenuChatAsync()
    {
        var target = MenuChat;
        IsChatMenuOpen = false;
        if (target is null) return;
        await ReopenChatAsync(target);
    }

    [RelayCommand]
    private async Task DeleteMenuChatAsync()
    {
        var target = MenuChat;
        IsChatMenuOpen = false;
        if (target is null || target.IsHome) return;
        await RunAsync(async () =>
        {
            await _workspace.DeleteBotChatAsync(target);
            RebuildChats();
        });
    }

    [RelayCommand]
    private void BrowseFiles() => _navigator.ShowFiles(_bot.BackingProject);

    [RelayCommand]
    private void AskDeleteBot() => IsDeleteConfirmOpen = true;

    [RelayCommand]
    private async Task ConfirmDeleteBotAsync()
    {
        IsDeleteConfirmOpen = false;
        await RunAsync(async () =>
        {
            await _workspace.DeleteBotAsync(_bot);
            _navigator.Back();
        });
    }

    private async Task OpenChatAsync(RemoteBotChat chat)
    {
        await RunAsync(async () =>
        {
            var opened = chat.IsOpen ? chat : await _workspace.OpenBotChatAsync(chat);
            RebuildChats();
            _navigator.ShowSession(opened.ToRemoteSession());
        });
    }

    private Task ReopenChatAsync(RemoteBotChat chat) => OpenChatAsync(chat);

    private void OpenChatMenu(RemoteBotChat chat)
    {
        MenuChat = chat;
        OnPropertyChanged(nameof(MenuChatTitle));
        OnPropertyChanged(nameof(MenuChatIsOpen));
        OnPropertyChanged(nameof(MenuChatIsClosed));
        OnPropertyChanged(nameof(MenuChatCanDelete));
        IsChatMenuOpen = true;
    }

    private async Task ResolveWorkItemAsync(RemoteBotWorkItem item, string? chosenOption)
    {
        await RunAsync(async () =>
        {
            await _workspace.ResolveBotWorkItemAsync(_bot, item, chosenOption);
            RebuildWorkItems();
            RebuildChats();
        });
    }

    private Task ToggleConnectorAsync(RemoteBotConnector connector, bool enabled) =>
        RunAsync(() => _workspace.ToggleBotConnectorAsync(_bot, connector, enabled));

    private void RebuildAll()
    {
        _hydrating = true;
        try
        {
            BotName = _bot.Name;
            HostName = _bot.Host.Name;
            var openCount = _bot.Chats.Count(c => c.IsOpen);
            BotSubtitle = MobileLangResources.Format(MobileLangResourceKind.BotsActiveSessionsSubtitle, _bot.Host.Name, openCount);
            BotInitial = _bot.Name.Length > 0 ? _bot.Name[..1].ToUpperInvariant() : "B";
            BotAvatarBrush = BotUiHelpers.ParseBrush(_bot.Color, "#5B5BD6");
            AgentName = _bot.Host.Agents.FirstOrDefault(a => a.Id == _bot.AgentId)?.Name ?? _bot.AgentId;
            SoulSummary = _bot.SoulSummary?.Trim() ?? "";
            HasSoul = SoulSummary.Length > 0;
            ShareLinkText = _bot.ShareLink ?? "";
            HasShareLink = !string.IsNullOrWhiteSpace(ShareLinkText);
            IsBotEnabled = !_bot.IsPaused;
            LookAroundEnabled = _bot.LookAroundEnabled;
            TidyUpMemoryEnabled = _bot.TidyUpMemoryEnabled;
            OnPropertyChanged(nameof(DeleteConfirmTitle));
        }
        finally
        {
            _hydrating = false;
        }

        RebuildAgentOptions();
        RebuildChats();
        RebuildWorkItems();
        RebuildConnectors();
    }

    private void RebuildAgentOptions()
    {
        AgentNames.Clear();
        var agents = _bot.Host.Agents;
        var defaultIdx = 0;
        for (var i = 0; i < agents.Count; i++)
        {
            AgentNames.Add(agents[i].Name);
            if (string.Equals(agents[i].Id, _bot.AgentId, StringComparison.OrdinalIgnoreCase))
            {
                defaultIdx = i;
            }
        }
        if (AgentNames.Count == 0)
        {
            AgentNames.Add(_bot.AgentId);
        }
        SelectedAgentIndex = defaultIdx;
    }

    private void RebuildChats()
    {
        var all = _bot.Chats.ToList();
        var allOpen = all.Where(c => c.IsOpen).OrderByDescending(c => c.IsHome).ThenByDescending(c => c.LastActivityAt).ToList();
        var allClosed = all.Where(c => !c.IsOpen).OrderByDescending(c => c.ClosedAt ?? c.LastActivityAt).ToList();

        FilterAllText = MobileLangResources.Format(MobileLangResourceKind.BotsSessionsFilterAll, all.Count);
        FilterActiveText = MobileLangResources.Format(MobileLangResourceKind.BotsSessionsFilterActive, allOpen.Count);
        FilterClosedText = MobileLangResources.Format(MobileLangResourceKind.BotsSessionsFilterClosed, allClosed.Count);

        var q = ChatSearchQuery.Trim();
        bool Matches(RemoteBotChat c) => q.Length == 0
            || c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (c.Subtitle?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (c.AgentId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);

        ActiveChats.Clear();
        if (ChatFilter != 2)
        {
            foreach (var c in allOpen.Where(Matches))
            {
                ActiveChats.Add(new BotChatItemViewModel(c, OpenChatAsync, OpenChatMenu, ReopenChatAsync));
            }
        }

        ClosedChats.Clear();
        if (ChatFilter != 1)
        {
            foreach (var c in allClosed.Where(Matches))
            {
                ClosedChats.Add(new BotChatItemViewModel(c, OpenChatAsync, OpenChatMenu, ReopenChatAsync));
            }
        }

        HasActiveChats = ActiveChats.Count > 0;
        HasClosedChats = ClosedChats.Count > 0;
        ActiveChatsHeader = MobileLangResources.Format(MobileLangResourceKind.BotsSessionsActiveHeader, ActiveChats.Count);
        ClosedChatsHeader = MobileLangResources.Format(MobileLangResourceKind.BotsSessionsClosedHeader, ClosedChats.Count);
        IsChatsEmpty = !HasActiveChats && !HasClosedChats;
        BotSubtitle = MobileLangResources.Format(MobileLangResourceKind.BotsActiveSessionsSubtitle, _bot.Host.Name, allOpen.Count);
    }

    private void RebuildWorkItems()
    {
        WorkItems.Clear();
        foreach (var item in _bot.Items.Where(i => !string.Equals(i.Status, "done", StringComparison.OrdinalIgnoreCase) && !string.Equals(i.Status, "canceled", StringComparison.OrdinalIgnoreCase)))
        {
            WorkItems.Add(new BotWorkItemRowViewModel(item, ResolveWorkItemAsync));
        }
        HasWorkItems = WorkItems.Count > 0;
    }

    private void RebuildConnectors()
    {
        Connectors.Clear();
        foreach (var conn in _bot.Connectors)
        {
            Connectors.Add(new BotConnectorRowViewModel(conn, ToggleConnectorAsync));
        }
        HasConnectors = Connectors.Count > 0;
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

    private void OnBotChanged(object? sender, PropertyChangedEventArgs e) => RebuildAll();
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildAll();

    public void Dispose()
    {
        _bot.PropertyChanged -= OnBotChanged;
        _bot.Chats.CollectionChanged -= OnCollectionChanged;
        _bot.Items.CollectionChanged -= OnCollectionChanged;
        _bot.Connectors.CollectionChanged -= OnCollectionChanged;
    }
}

internal static class BotUiHelpers
{
    public static IBrush ParseBrush(string? hex, string fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex)) return SolidColorBrush.Parse(hex);
        }
        catch (FormatException)
        {
        }
        return SolidColorBrush.Parse(fallback);
    }

    public static string AgentInitial(string? agentId) => (agentId ?? "").ToLowerInvariant() switch
    {
        "codex" => "X",
        "gemini" => "G",
        "opencode" => "O",
        _ => "C",
    };

    public static IBrush AgentBrush(string? agentId) => (agentId ?? "").ToLowerInvariant() switch
    {
        "codex" => SolidColorBrush.Parse("#1F9D74"),
        "gemini" => SolidColorBrush.Parse("#4C7FE0"),
        "opencode" => SolidColorBrush.Parse("#8B6FD6"),
        _ => SolidColorBrush.Parse("#D97757"),
    };

    public static string ConnectorColor(string? appId) => (appId ?? "").ToLowerInvariant() switch
    {
        "github" => "#24292E",
        "context7" => "#22C55E",
        "sentry" => "#362D59",
        "datadog" => "#632CA6",
        "linear" => "#5E6AD2",
        "slack" => "#4A154B",
        "notion" => "#1F1F1F",
        "cloudflare" => "#F38020",
        "supabase" => "#3ECF8E",
        "gitlab" => "#FC6D26",
        _ => "#5B5BD6",
    };

    public static string FormatRelativeTime(DateTimeOffset time)
    {
        var diff = DateTimeOffset.Now - time;
        if (diff.TotalMinutes < 1) return "刚刚";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes}m";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours}h";
        return time.ToLocalTime().ToString("MM/dd");
    }
}
