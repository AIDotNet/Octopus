using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Activity;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.App.Shell.Keymap;
using Octopus.Core.Bots;
using Octopus.Platform;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.Localization;

namespace Octopus.App.Bots;

public sealed partial class BotQuickTemplateViewModel(
    BotTemplateDefinition template,
    string label,
    string description,
    LucideIconKind icon,
    IBrush accent,
    Func<string, Task> openNew)
{
    public string Id => template.Id;
    public string Label { get; } = label;
    public string Description { get; } = description;
    public LucideIconKind Icon { get; } = icon;
    public IBrush Accent { get; } = accent;

    [RelayCommand]
    private Task Create() => openNew(Id);
}

public sealed partial class BotChatRowViewModel : ObservableObject
{
    private readonly Action<BotChatRowViewModel> _open;
    private readonly Func<BotChatRowViewModel, Task> _rename;
    private readonly Action<BotChatRowViewModel> _close;
    private readonly Action<BotChatRowViewModel> _delete;

    public BotChatRowViewModel(
        BotDefinition bot,
        BotChat chat,
        bool isSelected,
        bool isWorking,
        bool needsInput,
        Action<BotChatRowViewModel> open,
        Func<BotChatRowViewModel, Task> rename,
        Action<BotChatRowViewModel> close,
        Action<BotChatRowViewModel> delete)
    {
        BotId = bot.Id;
        ChatId = chat.Id;
        Kind = chat.Kind;
        Title = chat.DisplayTitle;
        Subtitle = BuildSubtitle(chat);
        TimeText = FormatRelativeTime(chat.LastActivityAt);
        IsHome = chat.Kind == BotChatKind.Home;
        IsClosed = !chat.IsOpen || chat.Status == BotChatStatus.Closed;
        IsSelected = isSelected;
        IsWorking = isWorking || chat.Status == BotChatStatus.Working;
        NeedsYou = needsInput || chat.Status == BotChatStatus.NeedsYou;
        Icon = chat.Kind switch
        {
            BotChatKind.Home => LucideIconKind.House,
            BotChatKind.Automation => LucideIconKind.Clock,
            BotChatKind.Connector => LucideIconKind.Plug,
            _ => LucideIconKind.MessageSquare,
        };
        _open = open;
        _rename = rename;
        _close = close;
        _delete = delete;
    }

    public string BotId { get; }
    public string ChatId { get; }
    public BotChatKind Kind { get; }
    public string Title { get; }
    public string? Subtitle { get; }
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
    public string TimeText { get; }
    public bool IsHome { get; }
    public bool CanDelete => !IsHome;
    public bool IsClosed { get; }
    public LucideIconKind Icon { get; }

    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsWorking { get; set; }
    [ObservableProperty] public partial bool NeedsYou { get; set; }

    public bool ShowTime => !IsWorking && !NeedsYou;

    partial void OnIsWorkingChanged(bool value) => OnPropertyChanged(nameof(ShowTime));
    partial void OnNeedsYouChanged(bool value) => OnPropertyChanged(nameof(ShowTime));

    [RelayCommand]
    private void Open() => _open(this);

    [RelayCommand]
    private Task RenameAsync() => _rename(this);

    [RelayCommand]
    private void CloseChat() => _close(this);

    [RelayCommand]
    private void DeleteChat() => _delete(this);

    public static string? BuildSubtitle(BotChat chat)
    {
        if (chat.ClosedAt is { } closedAt && (!chat.IsOpen || chat.Status == BotChatStatus.Closed))
        {
            var isZh = ArcLanguage.Current.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            var dateStr = isZh
                ? closedAt.ToLocalTime().ToString("M月d日")
                : closedAt.ToLocalTime().ToString("MMM d");
            var prefix = AppLangResources.Format(AppLangResourceKind.BotClosedAtPrefix, dateStr);
            return string.IsNullOrWhiteSpace(chat.Subtitle) ? prefix : $"{prefix} · {chat.Subtitle}";
        }
        return chat.Subtitle;
    }

    public static string FormatRelativeTime(DateTimeOffset timestamp)
    {
        var diff = DateTimeOffset.UtcNow - timestamp;
        if (diff.TotalMinutes < 1) return "1m";
        if (diff.TotalHours < 1) return $"{Math.Max(1, (int)diff.TotalMinutes)}m";
        if (diff.TotalDays < 1) return $"{Math.Max(1, (int)diff.TotalHours)}h";
        return $"{Math.Max(1, (int)diff.TotalDays)}d";
    }
}

public sealed partial class BotTreeNodeViewModel : ObservableObject
{
    private readonly Action<BotTreeNodeViewModel> _toggle;
    private readonly Action<BotTreeNodeViewModel> _newChat;
    private readonly Action<BotTreeNodeViewModel> _editSoul;
    private readonly Action<BotTreeNodeViewModel> _revealFolder;
    private readonly Action<BotTreeNodeViewModel> _togglePause;
    private readonly Func<BotTreeNodeViewModel, Task> _rename;
    private readonly Func<BotTreeNodeViewModel, Task> _delete;

    public BotTreeNodeViewModel(
        BotDefinition bot,
        bool isCurrent,
        bool isExpanded,
        int needsCount,
        bool isWorking,
        IReadOnlyList<BotChatRowViewModel> chats,
        Action<BotTreeNodeViewModel> toggle,
        Action<BotTreeNodeViewModel> newChat,
        Action<BotTreeNodeViewModel> editSoul,
        Action<BotTreeNodeViewModel> revealFolder,
        Action<BotTreeNodeViewModel> togglePause,
        Func<BotTreeNodeViewModel, Task> rename,
        Func<BotTreeNodeViewModel, Task> delete)
    {
        BotId = bot.Id;
        Name = bot.Name;
        Initial = bot.Name.Length > 0 ? bot.Name[..1].ToUpperInvariant() : "B";
        AvatarBrush = SolidColorBrush.Parse(bot.Color);
        AgentAccent = NewBotDialogViewModel.AgentAccent(bot.AgentId);
        IsPaused = bot.IsPaused;
        IsCurrent = isCurrent;
        IsExpanded = isExpanded;
        NeedsCount = needsCount;
        IsWorking = isWorking;
        foreach (var c in chats) Chats.Add(c);
        _toggle = toggle;
        _newChat = newChat;
        _editSoul = editSoul;
        _revealFolder = revealFolder;
        _togglePause = togglePause;
        _rename = rename;
        _delete = delete;
    }

    public string BotId { get; }
    public string Name { get; }
    public string Initial { get; }
    public IBrush AvatarBrush { get; }
    public IBrush AgentAccent { get; }
    public bool IsPaused { get; }
    public ObservableCollection<BotChatRowViewModel> Chats { get; } = [];

    [ObservableProperty] public partial bool IsCurrent { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedNeeds), nameof(ShowCollapsedWorking))]
    public partial bool IsExpanded { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedNeeds), nameof(ShowCollapsedWorking))]
    public partial int NeedsCount { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedWorking))]
    public partial bool IsWorking { get; set; }

    public bool ShowCollapsedNeeds => !IsExpanded && NeedsCount > 0;
    public bool ShowCollapsedWorking => !IsExpanded && NeedsCount == 0 && IsWorking;
    public string NeedsBadgeText => NeedsCount > 99 ? "99+" : NeedsCount.ToString();
    public string PauseMenuLabel => AppLangResources.Get(IsPaused ? AppLangResourceKind.BotMenuResume : AppLangResourceKind.BotMenuPause);

    [RelayCommand]
    private void Toggle() => _toggle(this);

    [RelayCommand]
    private void NewChat() => _newChat(this);

    [RelayCommand]
    private void EditSoul() => _editSoul(this);

    [RelayCommand]
    private void RevealFolder() => _revealFolder(this);

    [RelayCommand]
    private void TogglePause() => _togglePause(this);

    [RelayCommand]
    private Task RenameAsync() => _rename(this);

    [RelayCommand]
    private Task DeleteAsync() => _delete(this);
}

/// <summary>
/// Scheme A · Bot Tree left sidebar panel:
/// top "+ New Bot" CTA button, search box, and an expandable tree of Bots with their chats (Home Chat pinned first).
/// </summary>
public sealed partial class BotPanelViewModel : ObservableObject
{
    private readonly IBotService _bots;
    private readonly IDialogService _dialogs;
    private readonly IFileOpener _files;
    private readonly IAppKeymap _keymap;
    private readonly IItemActivityService? _activity;

    public BotPanelViewModel(
        IBotService bots,
        IDialogService dialogs,
        IFileOpener files,
        IAppKeymap keymap,
        IItemActivityService? activity = null)
    {
        _bots = bots;
        _dialogs = dialogs;
        _files = files;
        _keymap = keymap;
        _activity = activity;

        _bots.Changed += (_, _) => UiContext.Run(Rebuild);
        if (_activity is not null)
        {
            _activity.Changed += (_, _) => UiContext.Run(Rebuild);
        }
        ArcLanguage.Changed += (_, _) => UiContext.Run(() =>
        {
            RebuildTemplates();
            Rebuild();
        });

        RebuildTemplates();
        Rebuild();
    }

    public ObservableCollection<BotTreeNodeViewModel> BotNodes { get; } = [];
    public ObservableCollection<BotQuickTemplateViewModel> StarterTemplates { get; } = [];

    [ObservableProperty] public partial string SearchQuery { get; set; } = "";
    [ObservableProperty] public partial int BotCount { get; private set; }
    [ObservableProperty] public partial bool HasBots { get; private set; }
    [ObservableProperty] public partial bool IsEmpty { get; private set; }

    public string NewBotShortcutHint => _keymap.Hint(AppCommand.NewBot);

    partial void OnSearchQueryChanged(string value) => Rebuild();

    [RelayCommand]
    public Task NewBotAsync() => OpenNewBotDialogAsync(null);

    public async Task OpenNewBotDialogAsync(string? templateId)
    {
        var request = await _dialogs.AskNewBotAsync(templateId);
        if (request is null) return;
        _bots.CreateBot(request);
    }

    private void RebuildTemplates()
    {
        StarterTemplates.Clear();
        var isZh = ArcLanguage.Current.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        foreach (var tpl in BotCatalog.Templates.Where(t => t.Id != "blank"))
        {
            var label = NewBotDialogViewModel.TemplateLabel(tpl.Id);
            var desc = isZh ? tpl.DefaultSoulZh : tpl.DefaultSoulEn;
            StarterTemplates.Add(new BotQuickTemplateViewModel(
                tpl,
                label,
                desc,
                NewBotDialogViewModel.ParseIcon(tpl.IconName, LucideIconKind.Bot),
                SolidColorBrush.Parse(tpl.Color),
                OpenNewBotDialogAsync));
        }
    }

    public void Rebuild()
    {
        var all = _bots.Bots;
        BotCount = all.Count;
        HasBots = all.Count > 0;
        IsEmpty = all.Count == 0;

        var query = SearchQuery.Trim();
        var activeBotId = _bots.CurrentBot?.Id;
        var activeChatId = _bots.CurrentChat?.Id;

        BotNodes.Clear();
        foreach (var bot in all)
        {
            var botMatches = query.Length == 0 || bot.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            var orderedChats = bot.Chats
                .OrderBy(c => c.Kind == BotChatKind.Home ? 0 : 1)
                .ThenByDescending(c => c.IsOpen)
                .ThenByDescending(c => c.LastActivityAt)
                .Where(c => botMatches
                    || c.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (c.Subtitle?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();

            if (!botMatches && orderedChats.Count == 0) continue;

            var chatVms = new List<BotChatRowViewModel>(orderedChats.Count);
            var needs = bot.Items.Count(i => i.Status == BotWorkItemStatus.NeedsYou);
            var working = bot.Items.Any(i => i.Status == BotWorkItemStatus.Working);

            foreach (var chat in orderedChats)
            {
                var chatWorking = chat.Status == BotChatStatus.Working
                    || (_activity is not null && _activity.Of(chat.Id) == ItemActivity.Working);
                var chatNeeds = chat.Status == BotChatStatus.NeedsYou
                    || (_activity is not null && _activity.Of(chat.Id) == ItemActivity.Waiting);
                if (chatNeeds) needs++;
                if (chatWorking) working = true;

                chatVms.Add(new BotChatRowViewModel(
                    bot,
                    chat,
                    isSelected: bot.Id == activeBotId && chat.Id == activeChatId,
                    isWorking: chatWorking,
                    needsInput: chatNeeds,
                    open: row => _bots.OpenChat(row.BotId, row.ChatId),
                    rename: RenameChatAsync,
                    close: row => _bots.CloseChat(row.BotId, row.ChatId),
                    delete: row => _bots.DeleteChat(row.BotId, row.ChatId)));
            }

            var expanded = query.Length > 0 || bot.IsExpanded;
            BotNodes.Add(new BotTreeNodeViewModel(
                bot,
                isCurrent: bot.Id == activeBotId,
                isExpanded: expanded,
                needsCount: needs,
                isWorking: working,
                chats: chatVms,
                toggle: OnToggleBot,
                newChat: node => _bots.CreateChat(node.BotId),
                editSoul: OnEditSoul,
                revealFolder: OnRevealFolder,
                togglePause: node => _bots.SetBotPaused(node.BotId, !node.IsPaused),
                rename: RenameBotAsync,
                delete: DeleteBotAsync));
        }
    }

    private void OnToggleBot(BotTreeNodeViewModel node)
    {
        if (_bots.CurrentBot?.Id != node.BotId)
        {
            _bots.SelectBot(node.BotId);
            if (!node.IsExpanded)
            {
                _bots.SetBotExpanded(node.BotId, true);
            }
            return;
        }
        _bots.SetBotExpanded(node.BotId, !node.IsExpanded);
    }

    private void OnEditSoul(BotTreeNodeViewModel node)
    {
        var bot = _bots.Bots.FirstOrDefault(b => b.Id == node.BotId);
        if (bot is null) return;
        _bots.Home.EnsureHome(bot);
        _bots.OpenFile(bot.Id, _bots.Home.GetSoulPath(bot));
    }

    private void OnRevealFolder(BotTreeNodeViewModel node)
    {
        var bot = _bots.Bots.FirstOrDefault(b => b.Id == node.BotId);
        if (bot is null) return;
        var path = _bots.Home.EnsureHome(bot);
        _files.Reveal(path);
    }

    private async Task RenameBotAsync(BotTreeNodeViewModel node)
    {
        var name = await _dialogs.AskNameAsync(
            AppLangResources.Get(AppLangResourceKind.BotRenameTitle),
            AppLangResources.Get(AppLangResourceKind.BotDialogNameLabel),
            node.Name,
            AppLangResources.Get(AppLangResourceKind.Save));
        if (!string.IsNullOrWhiteSpace(name))
        {
            _bots.RenameBot(node.BotId, name);
        }
    }

    private async Task DeleteBotAsync(BotTreeNodeViewModel node)
    {
        var ok = await _dialogs.ConfirmAsync(
            AppLangResources.Format(AppLangResourceKind.BotDeleteConfirmTitle, node.Name),
            AppLangResources.Get(AppLangResourceKind.BotDeleteConfirmBody),
            AppLangResources.Get(AppLangResourceKind.BotMenuDelete),
            isDanger: true);
        if (ok)
        {
            _bots.DeleteBot(node.BotId);
        }
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
}
