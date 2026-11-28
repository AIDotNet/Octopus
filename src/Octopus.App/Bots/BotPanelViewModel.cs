using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Octopus.App.Activity;
using Octopus.App.Bots.Groups;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.App.Shell.Keymap;
using Octopus.Core.Bots;
using Octopus.Platform;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.Localization;

namespace Octopus.App.Bots;

/// <summary>Raised when the user clicks "Bot Settings..." from a Bot's context menu.</summary>
public sealed record OpenBotSettingsRequested(string BotId);

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
    private readonly Action<BotTreeNodeViewModel> _openSettings;
    private readonly Action<BotTreeNodeViewModel> _copyPath;
    private readonly Action<BotTreeNodeViewModel> _toggleUnread;
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
        Action<BotTreeNodeViewModel> openSettings,
        Action<BotTreeNodeViewModel> copyPath,
        Action<BotTreeNodeViewModel> toggleUnread,
        Action<BotTreeNodeViewModel> editSoul,
        Action<BotTreeNodeViewModel> revealFolder,
        Action<BotTreeNodeViewModel> togglePause,
        Func<BotTreeNodeViewModel, Task> rename,
        Func<BotTreeNodeViewModel, Task> delete,
        Bitmap? avatarImage = null)
    {
        BotId = bot.Id;
        Name = bot.Name;
        Initial = bot.Name.Length > 0 ? bot.Name[..1].ToUpperInvariant() : "B";
        AvatarBrush = SolidColorBrush.Parse(bot.Color);
        AvatarImage = avatarImage;
        HasAvatarImage = avatarImage is not null;
        HasAvatarIcon = !HasAvatarImage && !string.IsNullOrWhiteSpace(bot.Icon);
        ShowInitial = !HasAvatarImage && !HasAvatarIcon;
        AvatarIcon = NewBotDialogViewModel.ParseIcon(bot.Icon, LucideIconKind.Bot);
        AgentAccent = NewBotDialogViewModel.AgentAccent(bot.AgentId);
        IsPaused = bot.IsPaused;
        IsUnread = bot.IsUnread;
        IsCurrent = isCurrent;
        IsExpanded = isExpanded;
        NeedsCount = needsCount;
        IsWorking = isWorking;
        foreach (var c in chats) Chats.Add(c);
        _toggle = toggle;
        _newChat = newChat;
        _openSettings = openSettings;
        _copyPath = copyPath;
        _toggleUnread = toggleUnread;
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
    public Bitmap? AvatarImage { get; }
    public bool HasAvatarImage { get; }
    public bool HasAvatarIcon { get; }
    public bool ShowInitial { get; }
    public LucideIconKind AvatarIcon { get; }
    public IBrush AgentAccent { get; }
    public bool IsPaused { get; }
    public bool IsUnread { get; }
    public ObservableCollection<BotChatRowViewModel> Chats { get; } = [];

    [ObservableProperty] public partial bool IsCurrent { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedNeeds), nameof(ShowCollapsedWorking), nameof(ShowUnreadDot))]
    public partial bool IsExpanded { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedNeeds), nameof(ShowCollapsedWorking), nameof(ShowUnreadDot))]
    public partial int NeedsCount { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedWorking), nameof(ShowUnreadDot))]
    public partial bool IsWorking { get; set; }

    public bool ShowCollapsedNeeds => !IsExpanded && NeedsCount > 0;
    public bool ShowCollapsedWorking => !IsExpanded && NeedsCount == 0 && IsWorking;
    public bool ShowUnreadDot => IsUnread && !ShowCollapsedNeeds && !ShowCollapsedWorking;
    public string NeedsBadgeText => NeedsCount > 99 ? "99+" : NeedsCount.ToString();
    public string PauseMenuLabel => AppLangResources.Get(IsPaused ? AppLangResourceKind.BotMenuResume : AppLangResourceKind.BotMenuPause);

    [RelayCommand]
    private void Toggle() => _toggle(this);

    [RelayCommand]
    private void NewChat() => _newChat(this);

    [RelayCommand]
    private void OpenSettings() => _openSettings(this);

    [RelayCommand]
    private void CopyPath() => _copyPath(this);

    [RelayCommand]
    private void ToggleUnread() => _toggleUnread(this);

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
public sealed partial class BotPanelViewModel : ObservableObject, IDisposable
{
    private readonly IBotService _bots;
    private readonly IDialogService _dialogs;
    private readonly IFileOpener _files;
    private readonly IAppKeymap _keymap;
    private readonly IProjectIconStore? _icons;
    private readonly IItemActivityService? _activity;
    private readonly IBotGroupService? _botGroups;
    private readonly Workspace.ITabContentRegistry? _tabs;

    public BotPanelViewModel(
        IBotService bots,
        IDialogService dialogs,
        IFileOpener files,
        IAppKeymap keymap,
        IProjectIconStore? icons = null,
        IItemActivityService? activity = null,
        IBotGroupService? botGroups = null,
        Workspace.ITabContentRegistry? tabs = null)
    {
        _bots = bots;
        _dialogs = dialogs;
        _files = files;
        _keymap = keymap;
        _icons = icons;
        _activity = activity;
        _botGroups = botGroups;
        _tabs = tabs;

        _bots.Changed += (_, e) =>
        {
            if (e.Kind != BotChangeKind.Files) UiContext.Run(Rebuild);
        };
        _botGroups?.Changed += (_, _) => UiContext.Run(Rebuild);
        _activity?.Changed += (_, _) => UiContext.Run(Rebuild);
        ArcLanguage.Changed += OnLanguageChanged;

        RebuildTemplates();
        Rebuild();
    }

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e) => UiContext.Run(() =>
    {
        RebuildTemplates();
        Rebuild();
    });

    public void Dispose() => ArcLanguage.Changed -= OnLanguageChanged;

    public ObservableCollection<BotGroupTreeNodeViewModel> GroupNodes { get; } = [];
    public ObservableCollection<BotTreeNodeViewModel> BotNodes { get; } = [];
    public ObservableCollection<BotQuickTemplateViewModel> StarterTemplates { get; } = [];

    /// <summary>Raised when a Bot's home path should be copied to the clipboard.</summary>
    public event EventHandler<string>? CopyRequested;

    [ObservableProperty] public partial string SearchQuery { get; set; } = "";
    [ObservableProperty] public partial int BotCount { get; private set; }
    [ObservableProperty] public partial bool HasBots { get; private set; }
    [ObservableProperty] public partial bool HasGroups { get; private set; }
    [ObservableProperty] public partial bool HasAnyContent { get; private set; }
    [ObservableProperty] public partial bool IsEmpty { get; private set; }

    public bool ShowNewGroupButton => _botGroups is not null;
    public string NewBotShortcutHint => _keymap.Hint(AppCommand.NewBot);

    partial void OnSearchQueryChanged(string value) => Rebuild();

    [RelayCommand]
    public Task NewBotAsync() => OpenNewBotDialogAsync(null);

    [RelayCommand]
    public async Task NewGroupAsync()
    {
        if (_botGroups is null)
        {
            return;
        }
        if (_bots.Bots.Count == 0)
        {
            await _dialogs.ConfirmAsync(
                AppLangResources.Get(AppLangResourceKind.BotGroupNoBotsTitle),
                AppLangResources.Get(AppLangResourceKind.BotGroupNoBotsBody),
                AppLangResources.Get(AppLangResourceKind.UpdateClose));
            return;
        }
        var result = await _dialogs.AskNewBotGroupAsync(_bots.Bots);
        if (result is null)
        {
            return;
        }
        _botGroups.CreateGroup(new CreateBotGroupRequest(
            result.Name,
            result.BotIds,
            result.ReplyMode,
            result.MaxHops));
    }

    [RelayCommand]
    public async Task ImportBotFromLinkAsync()
    {
        var link = await _dialogs.AskNameAsync(
            AppLangResources.Get(AppLangResourceKind.BotImportFromLinkButton),
            AppLangResources.Get(AppLangResourceKind.BotImportLinkPlaceholder),
            null,
            AppLangResources.Get(AppLangResourceKind.BotImportConfirm));
        if (string.IsNullOrWhiteSpace(link)) return;
        if (!BotShareCodec.TryDecodeString(link, out var payload, out _) || payload is null)
        {
            await _dialogs.ConfirmAsync(
                AppLangResources.Get(AppLangResourceKind.BotImportTitle),
                AppLangResources.Get(AppLangResourceKind.BotImportInvalidBody),
                AppLangResources.Get(AppLangResourceKind.UpdateClose));
            return;
        }
        if (await _dialogs.ConfirmBotImportAsync(payload))
        {
            _bots.ImportBot(payload);
        }
    }

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
        var allGroups = _botGroups?.Groups ?? [];
        BotCount = all.Count;
        HasBots = all.Count > 0;
        HasGroups = allGroups.Count > 0;
        HasAnyContent = HasBots || HasGroups;
        IsEmpty = !HasAnyContent;

        var query = SearchQuery.Trim();
        var activeGroupId = _botGroups?.CurrentGroup?.Id;
        var activeThreadId = _botGroups?.CurrentThreadId;
        var hasActiveGroup = !string.IsNullOrWhiteSpace(activeGroupId);
        var activeBotId = hasActiveGroup ? null : _bots.CurrentBot?.Id;
        var activeChatId = hasActiveGroup ? null : _bots.CurrentChat?.Id;

        GroupNodes.Clear();
        if (_botGroups is not null)
        {
            foreach (var group in allGroups)
            {
                var groupMatches = query.Length == 0
                    || group.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (group.Latest?.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

                var matchingThreads = group.Threads
                    .Where(t => groupMatches || (t.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();

                if (!groupMatches && matchingThreads.Count == 0)
                {
                    continue;
                }

                var isCurrentGroup = string.Equals(group.Id, activeGroupId, StringComparison.Ordinal);
                var rooms = new List<BotGroupRoomRowViewModel>(1 + matchingThreads.Count)
                {
                    new(
                        group.Id,
                        threadId: null,
                        title: AppLangResources.Get(AppLangResourceKind.BotGroupMainRoom),
                        isMainRoom: true,
                        isSelected: isCurrentGroup && string.IsNullOrWhiteSpace(activeThreadId),
                        pendingApprovalCount: _botGroups.GetPendingApprovals(group.Id).Count(a => string.IsNullOrWhiteSpace(a.ThreadId)),
                        open: row => _botGroups.SelectGroup(row.GroupId, row.ThreadId)),
                };

                foreach (var thread in matchingThreads)
                {
                    var threadTitle = string.IsNullOrWhiteSpace(thread.Title)
                        ? AppLangResources.Get(AppLangResourceKind.BotGroupUntitledThread)
                        : thread.Title!;
                    rooms.Add(new BotGroupRoomRowViewModel(
                        group.Id,
                        thread.Id,
                        threadTitle,
                        isMainRoom: false,
                        isSelected: isCurrentGroup && string.Equals(activeThreadId, thread.Id, StringComparison.Ordinal),
                        pendingApprovalCount: _botGroups.GetPendingApprovals(group.Id, thread.Id).Count,
                        open: row => _botGroups.SelectGroup(row.GroupId, row.ThreadId),
                        delete: DeleteGroupThreadAsync));
                }

                var preview = FormatGroupPreview(group);
                var totalPending = _botGroups.GetPendingApprovals(group.Id).Count;
                var working = _botGroups.IsGroupWorking(group.Id);
                var avatars = BotGroupUiHelpers.BuildAvatarCluster(group, _bots, _icons, 4);

                GroupNodes.Add(new BotGroupTreeNodeViewModel(
                    group,
                    avatars,
                    preview,
                    isCurrent: isCurrentGroup,
                    isExpanded: query.Length > 0 || group.IsExpanded,
                    pendingApprovalCount: totalPending,
                    isWorking: working,
                    rooms: rooms,
                    toggle: OnToggleGroup,
                    newThread: OnCreateGroupThreadAsync,
                    manageMembers: OnManageGroupMembersAsync,
                    rename: RenameGroupAsync,
                    delete: DeleteGroupAsync));
            }
        }

        BotNodes.Clear();
        foreach (var bot in all)
        {
            var botMatches = query.Length == 0 || bot.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            var orderedChats = bot.Chats
                .Where(c => c.Kind != BotChatKind.GroupMember)
                .OrderBy(c => c.Kind == BotChatKind.Home ? 0 : 1)
                .ThenByDescending(c => c.IsOpen)
                .ThenByDescending(c => c.LastActivityAt)
                .Where(c => botMatches
                    || c.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (c.Subtitle?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();

            if (!botMatches && orderedChats.Count == 0)
            {
                continue;
            }

            var chatVms = new List<BotChatRowViewModel>(orderedChats.Count);
            var needs = bot.Items.Count(i => i.Status == BotWorkItemStatus.NeedsYou);
            var working = bot.Items.Any(i => i.Status == BotWorkItemStatus.Working);

            foreach (var chat in orderedChats)
            {
                var chatWorking = chat.Status == BotChatStatus.Working
                    || (_activity is not null && _activity.Of(chat.Id) == ItemActivity.Working);
                var chatNeeds = chat.Status == BotChatStatus.NeedsYou
                    || (_activity is not null && _activity.Of(chat.Id) == ItemActivity.Waiting);
                if (chatNeeds)
                {
                    needs++;
                }
                if (chatWorking)
                {
                    working = true;
                }

                chatVms.Add(new BotChatRowViewModel(
                    bot,
                    chat,
                    isSelected: bot.Id == activeBotId && chat.Id == activeChatId,
                    isWorking: chatWorking,
                    needsInput: chatNeeds,
                    open: row => _bots.OpenChat(row.BotId, row.ChatId),
                    rename: RenameChatAsync,
                    close: row => Workspace.BotOwnerRetirement.RetireChatAndMutate(_bots, _tabs, row.BotId, row.ChatId, delete: false),
                    delete: row => Workspace.BotOwnerRetirement.RetireChatAndMutate(_bots, _tabs, row.BotId, row.ChatId, delete: true)));
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
                openSettings: OnOpenSettings,
                copyPath: OnCopyPath,
                toggleUnread: node => _bots.SetBotUnread(node.BotId, !node.IsUnread),
                editSoul: OnEditSoul,
                revealFolder: OnRevealFolder,
                togglePause: node => _bots.SetBotPaused(node.BotId, !node.IsPaused),
                rename: RenameBotAsync,
                delete: DeleteBotAsync,
                avatarImage: _icons?.Load(bot.Image)));
        }
    }

    private static string FormatGroupPreview(BotGroupDefinition group)
    {
        if (group.Latest is { } latest && !string.IsNullOrWhiteSpace(latest.Text))
        {
            return string.IsNullOrWhiteSpace(latest.AuthorName)
                ? latest.Text
                : $"{latest.AuthorName}: {latest.Text}";
        }
        return AppLangResources.Format(AppLangResourceKind.BotGroupMemberCount, group.Members.Count);
    }

    private void OnToggleGroup(BotGroupTreeNodeViewModel node)
    {
        if (_botGroups is null)
        {
            return;
        }
        if (!string.Equals(_botGroups.CurrentGroup?.Id, node.GroupId, StringComparison.Ordinal))
        {
            _botGroups.SelectGroup(node.GroupId);
            if (!node.IsExpanded)
            {
                _botGroups.SetGroupExpanded(node.GroupId, true);
            }
            return;
        }
        _botGroups.SetGroupExpanded(node.GroupId, !node.IsExpanded);
    }

    private async Task OnCreateGroupThreadAsync(BotGroupTreeNodeViewModel node)
    {
        if (_botGroups is null)
        {
            return;
        }
        await _botGroups.CreateThreadAsync(node.GroupId);
    }

    private async Task OnManageGroupMembersAsync(BotGroupTreeNodeViewModel node)
    {
        if (_botGroups is null || _botGroups.FindGroup(node.GroupId) is not { } group)
        {
            return;
        }
        var result = await _dialogs.AskEditBotGroupAsync(group, _bots.Bots);
        if (result is null)
        {
            return;
        }
        await _botGroups.UpdateGroupAsync(new UpdateBotGroupRequest(
            group.Id,
            group.Revision,
            result.Name,
            result.BotIds,
            result.ReplyMode,
            result.MaxHops));
    }

    private async Task RenameGroupAsync(BotGroupTreeNodeViewModel node)
    {
        if (_botGroups is null || _botGroups.FindGroup(node.GroupId) is not { } group)
        {
            return;
        }
        var name = await _dialogs.AskNameAsync(
            AppLangResources.Get(AppLangResourceKind.BotGroupRenameTitle),
            AppLangResources.Get(AppLangResourceKind.BotGroupDialogNameLabel),
            group.Name,
            AppLangResources.Get(AppLangResourceKind.Save));
        if (!string.IsNullOrWhiteSpace(name))
        {
            await _botGroups.UpdateGroupAsync(new UpdateBotGroupRequest(group.Id, group.Revision, Name: name));
        }
    }

    private async Task DeleteGroupAsync(BotGroupTreeNodeViewModel node)
    {
        if (_botGroups is null)
        {
            return;
        }
        var ok = await _dialogs.ConfirmAsync(
            AppLangResources.Format(AppLangResourceKind.BotGroupDeleteConfirmTitle, node.Name),
            AppLangResources.Get(AppLangResourceKind.BotGroupDeleteConfirmBody),
            AppLangResources.Get(AppLangResourceKind.BotGroupMenuDelete),
            isDanger: true);
        if (ok)
        {
            await _botGroups.DeleteGroupAsync(node.GroupId);
        }
    }

    private async Task DeleteGroupThreadAsync(BotGroupRoomRowViewModel row)
    {
        if (_botGroups is null || string.IsNullOrWhiteSpace(row.ThreadId))
        {
            return;
        }
        var hasMessages = false;
        try
        {
            hasMessages = _botGroups.ReadRoom(row.GroupId, row.ThreadId, limit: 1).Messages.Count > 0;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        if (hasMessages)
        {
            var ok = await _dialogs.ConfirmAsync(
                AppLangResources.Format(AppLangResourceKind.BotGroupThreadDeleteConfirmTitle, row.Title),
                AppLangResources.Get(AppLangResourceKind.BotGroupThreadDeleteConfirmBody),
                AppLangResources.Get(AppLangResourceKind.BotGroupMenuDeleteThread),
                isDanger: true);
            if (!ok)
            {
                return;
            }
        }
        await _botGroups.DeleteThreadAsync(row.GroupId, row.ThreadId);
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

    private void OnOpenSettings(BotTreeNodeViewModel node)
    {
        _bots.SelectBot(node.BotId);
        WeakReferenceMessenger.Default.Send(new OpenBotSettingsRequested(node.BotId));
    }

    private void OnCopyPath(BotTreeNodeViewModel node)
    {
        var bot = _bots.Bots.FirstOrDefault(b => b.Id == node.BotId);
        if (bot is null) return;
        var path = _bots.Home.EnsureHome(bot);
        CopyRequested?.Invoke(this, path);
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
            Workspace.BotOwnerRetirement.RetireBotAndDelete(_bots, _tabs, node.BotId);
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
