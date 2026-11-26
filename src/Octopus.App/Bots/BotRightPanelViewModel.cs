using System.Collections.ObjectModel;
using Avalonia.Media;
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
    private readonly Action<string, bool>? _setEnabled;
    private readonly Action<string> _remove;

    public BotConnectorItemViewModel(
        BotConnectorBinding binding,
        Action<string, bool> setEnabled,
        Action<string> remove)
    {
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

    [ObservableProperty] public partial bool IsEnabled { get; set; }

    partial void OnIsEnabledChanged(bool value) => _setEnabled?.Invoke(Id, value);

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
/// <c>Home</c> | <c>连接器</c> | <c>聊天</c> | <c>文件</c>.
/// </summary>
public sealed partial class BotRightPanelViewModel : ObservableObject
{
    private readonly IBotService _bots;
    private readonly IDialogService _dialogs;
    private readonly IAgentCatalog _catalog;
    private readonly IFileOpener _fileOpener;
    private readonly IItemActivityService? _activity;
    private string? _loadedFilesRoot;

    public BotRightPanelViewModel(
        IBotService bots,
        IDialogService dialogs,
        IAgentCatalog catalog,
        IFileOpener fileOpener,
        FileTreeViewModel files,
        FileSearchViewModel search,
        IItemActivityService? activity = null)
    {
        _bots = bots;
        _dialogs = dialogs;
        _catalog = catalog;
        _fileOpener = fileOpener;
        Files = files;
        Search = search;
        _activity = activity;

        Files.OpenRequested += (_, path) => OpenBotFile(path);
        Search.OpenRequested += (_, path) => OpenBotFile(path);

        _bots.Changed += (_, _) => UiContext.Run(Refresh);
        _activity?.Changed += (_, _) => UiContext.Run(RefreshStatusAndChats);
        ArcLanguage.Changed += (_, _) => UiContext.Run(Refresh);

        Refresh();
    }

    public FileTreeViewModel Files { get; }
    public FileSearchViewModel Search { get; }

    public ObservableCollection<BotWorkItemCardViewModel> WorkItems { get; } = [];
    public ObservableCollection<string> MemoryLines { get; } = [];
    public ObservableCollection<BotConnectorItemViewModel> Connectors { get; } = [];
    public ObservableCollection<BotChatRowViewModel> FilteredChats { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabKey), nameof(IsHomeTab), nameof(IsConnectorsTab), nameof(IsChatsTab), nameof(IsFilesTab))]
    public partial BotRightTab Tab { get; set; } = BotRightTab.Home;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoBot), nameof(IsHomeTab), nameof(IsConnectorsTab), nameof(IsChatsTab), nameof(IsFilesTab))]
    public partial bool HasBot { get; private set; }

    [ObservableProperty] public partial string BotId { get; private set; } = "";
    [ObservableProperty] public partial string BotName { get; private set; } = "";
    [ObservableProperty] public partial string BotSlug { get; private set; } = "bot";
    [ObservableProperty] public partial string BotHomePath { get; private set; } = "";
    [ObservableProperty] public partial string BotInitial { get; private set; } = "B";
    [ObservableProperty] public partial IBrush BotAvatarBrush { get; private set; } = SolidColorBrush.Parse("#5b5bd6");
    [ObservableProperty] public partial IBrush BotAgentAccent { get; private set; } = SolidColorBrush.Parse("#d97757");
    [ObservableProperty] public partial string BotAgentName { get; private set; } = "";
    [ObservableProperty] public partial bool IsBotPaused { get; private set; }
    [ObservableProperty] public partial bool IsBotWorking { get; private set; }
    [ObservableProperty] public partial string BotStatusText { get; private set; } = "";
    [ObservableProperty] public partial string SoulSummary { get; private set; } = "";
    [ObservableProperty] public partial bool HasSoul { get; private set; }
    [ObservableProperty] public partial bool HasWorkItems { get; private set; }
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
    public bool IsConnectorsTab => HasBot && Tab == BotRightTab.Connectors;
    public bool IsChatsTab => HasBot && Tab == BotRightTab.Chats;
    public bool IsFilesTab => HasBot && Tab == BotRightTab.Files;

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

    public void Refresh()
    {
        var bot = _bots.CurrentBot;
        if (bot is null)
        {
            HasBot = false;
            BotId = "";
            BotName = "";
            BotSlug = "bot";
            BotHomePath = "";
            WorkItems.Clear();
            MemoryLines.Clear();
            Connectors.Clear();
            FilteredChats.Clear();
            NeedsCount = 0;
            if (_loadedFilesRoot is not null)
            {
                _loadedFilesRoot = null;
                Files.Load(null);
                Search.SetRoot(null);
            }
            return;
        }

        HasBot = true;
        BotId = bot.Id;
        BotName = bot.Name;
        BotSlug = bot.Slug;
        BotHomePath = _bots.Home.EnsureHome(bot);
        BotInitial = bot.Name.Length > 0 ? bot.Name[..1].ToUpperInvariant() : "B";
        BotAvatarBrush = SolidColorBrush.Parse(bot.Color);
        BotAgentAccent = NewBotDialogViewModel.AgentAccent(bot.AgentId);
        BotAgentName = _catalog.Find(bot.AgentId)?.Name ?? bot.AgentId;
        IsBotPaused = bot.IsPaused;
        ConnectorsEmptyDescription = AppLangResources.Format(AppLangResourceKind.BotConnectorsEmptyDescription, bot.Name);

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

        // Connectors
        Connectors.Clear();
        foreach (var conn in bot.Connectors)
        {
            Connectors.Add(new BotConnectorItemViewModel(
                conn,
                setEnabled: (id, enabled) => _bots.SetConnectorEnabled(bot.Id, id, enabled),
                remove: id => _bots.RemoveConnector(bot.Id, id)));
        }
        HasConnectors = Connectors.Count > 0;

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
}
