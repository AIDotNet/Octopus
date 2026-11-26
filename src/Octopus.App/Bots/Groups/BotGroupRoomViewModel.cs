using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Core.Bots;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.Localization;

namespace Octopus.App.Bots.Groups;

/// <summary>Room or thread pill in the Bot Group top bar.</summary>
public sealed partial class BotGroupRoomTabViewModel(
    string groupId,
    string? threadId,
    string title,
    bool isMainRoom,
    bool isSelected,
    int pendingApprovalCount,
    Action<string, string?> select) : ObservableObject
{
    public string GroupId { get; } = groupId;
    public string? ThreadId { get; } = threadId;
    public string Title { get; } = title;
    public bool IsMainRoom { get; } = isMainRoom;
    public LucideIconKind Icon { get; } = isMainRoom ? LucideIconKind.MessagesSquare : LucideIconKind.Hash;
    public int PendingApprovalCount { get; } = pendingApprovalCount;
    public bool HasPendingApproval => PendingApprovalCount > 0;
    public string PendingBadgeText => PendingApprovalCount > 99 ? "99+" : PendingApprovalCount.ToString();

    [ObservableProperty] public partial bool IsSelected { get; set; } = isSelected;

    [RelayCommand]
    private void Select() => select(GroupId, ThreadId);
}

/// <summary>Choice inside the composer's Reply Picker popover.</summary>
public sealed partial class BotGroupReplyModeChoiceViewModel(
    string label,
    BotGroupReplyMode mode,
    BotGroupAvatarTile? avatar,
    LucideIconKind icon,
    bool isSelected,
    Func<BotGroupReplyMode, Task> pick) : ObservableObject
{
    public string Label { get; } = label;
    public BotGroupReplyMode Mode { get; } = mode;
    public BotGroupAvatarTile? Avatar { get; } = avatar;
    public bool HasAvatar => Avatar is not null;
    public LucideIconKind Icon { get; } = icon;
    public bool IsSelected { get; } = isSelected;

    [RelayCommand]
    private Task SelectAsync() => pick(Mode);
}

/// <summary>Choice inside the composer's quick '@' mention popover.</summary>
public sealed partial class BotGroupMentionChoiceViewModel(
    string label,
    string insertToken,
    BotGroupAvatarTile? avatar,
    Action<string> insert) : ObservableObject
{
    public string Label { get; } = label;
    public string InsertToken { get; } = insertToken;
    public BotGroupAvatarTile? Avatar { get; } = avatar;
    public bool HasAvatar => Avatar is not null;

    [RelayCommand]
    private void Insert() => insert(InsertToken);
}

/// <summary>One clickable permission option button inside <see cref="BotGroupApprovalItemViewModel"/>.</summary>
public sealed partial class BotGroupApprovalOptionViewModel(
    string optionId,
    string label,
    ButtonVariant variant,
    Action<string> choose) : ObservableObject
{
    public string OptionId { get; } = optionId;
    public string Label { get; } = label;
    public ButtonVariant Variant { get; } = variant;

    [RelayCommand]
    private void Choose() => choose(OptionId);
}

/// <summary>One pending member approval rendered in the unified Approval Dock above the composer.</summary>
public sealed class BotGroupApprovalItemViewModel
{
    public BotGroupApprovalItemViewModel(
        BotGroupPendingApproval approval,
        BotGroupAvatarTile avatar,
        Action<string, string?> respond)
    {
        ApprovalId = approval.ApprovalId;
        BotId = approval.BotId;
        BotName = approval.BotName;
        Avatar = avatar;
        HeaderLabel = AppLangResources.Format(AppLangResourceKind.BotGroupApprovalNeedsYou, approval.BotName);
        Title = approval.Title;
        Detail = approval.Detail;
        HasDetail = !string.IsNullOrWhiteSpace(approval.Detail);
        WarningMessage = approval.WarningMessage;
        HasWarning = !string.IsNullOrWhiteSpace(approval.WarningMessage);

        var options = new List<BotGroupApprovalOptionViewModel>(approval.Options.Count);
        var hasPrimaryAllow = false;
        foreach (var opt in approval.Options)
        {
            var isReject = opt.Kind.StartsWith("reject", StringComparison.OrdinalIgnoreCase);
            var isAllow = opt.Kind.StartsWith("allow", StringComparison.OrdinalIgnoreCase);
            ButtonVariant variant;
            if (isAllow && !hasPrimaryAllow)
            {
                variant = ButtonVariant.Primary;
                hasPrimaryAllow = true;
            }
            else if (isReject)
            {
                variant = ButtonVariant.Danger;
            }
            else
            {
                variant = ButtonVariant.Secondary;
            }
            var label = string.IsNullOrWhiteSpace(opt.Name) ? opt.OptionId : opt.Name;
            options.Add(new BotGroupApprovalOptionViewModel(opt.OptionId, label, variant, id => respond(approval.ApprovalId, id)));
        }
        Options = options;
    }

    public string ApprovalId { get; }
    public string BotId { get; }
    public string BotName { get; }
    public BotGroupAvatarTile Avatar { get; }
    public string HeaderLabel { get; }
    public string Title { get; }
    public string? Detail { get; }
    public bool HasDetail { get; }
    public string? WarningMessage { get; }
    public bool HasWarning { get; }
    public IReadOnlyList<BotGroupApprovalOptionViewModel> Options { get; }
}

/// <summary>One Bot Turn card nested under a <see cref="BotGroupMessageItemViewModel"/>.</summary>
public sealed partial class BotGroupTurnItemViewModel : ObservableObject
{
    private readonly Func<string, Task> _stopBot;
    private readonly Action<string> _replyBot;
    private readonly Action<string, string> _inspectSession;

    public BotGroupTurnItemViewModel(
        BotGroupTurn turn,
        BotGroupAvatarTile avatar,
        string agentName,
        string? streamingPreview,
        Func<string, Task> stopBot,
        Action<string> replyBot,
        Action<string, string> inspectSession)
    {
        OperationId = turn.OperationId;
        BotId = turn.BotId;
        ChatId = turn.ChatId;
        BotName = turn.BotName;
        Avatar = avatar;
        AgentName = agentName;
        AgentAccent = NewBotDialogViewModel.AgentAccent(turn.AgentId);
        Status = turn.Status;
        IsQueued = turn.Status == BotGroupTurnStatus.Queued;
        IsRunning = turn.Status == BotGroupTurnStatus.Running;
        IsCompleted = turn.Status == BotGroupTurnStatus.Completed;
        IsStopped = turn.Status == BotGroupTurnStatus.Stopped;
        IsFailed = turn.Status == BotGroupTurnStatus.Failed;
        IsActive = IsQueued || IsRunning;
        StatusLabel = turn.Status switch
        {
            BotGroupTurnStatus.Queued => AppLangResources.Get(AppLangResourceKind.BotGroupTurnQueued),
            BotGroupTurnStatus.Running => AppLangResources.Get(AppLangResourceKind.BotGroupTurnRunning),
            BotGroupTurnStatus.Completed => AppLangResources.Get(AppLangResourceKind.BotGroupTurnCompleted),
            BotGroupTurnStatus.Stopped => AppLangResources.Get(AppLangResourceKind.BotGroupTurnStopped),
            _ => AppLangResources.Get(AppLangResourceKind.BotGroupTurnFailed),
        };
        BodyMarkdown = IsRunning && !string.IsNullOrWhiteSpace(streamingPreview)
            ? streamingPreview!
            : (turn.Reply ?? "");
        ErrorText = turn.Error;
        HasError = !string.IsNullOrWhiteSpace(turn.Error);
        CanInspect = !string.IsNullOrWhiteSpace(turn.ChatId);
        _stopBot = stopBot;
        _replyBot = replyBot;
        _inspectSession = inspectSession;
    }

    public string OperationId { get; }
    public string BotId { get; }
    public string ChatId { get; }
    public string BotName { get; }
    public BotGroupAvatarTile Avatar { get; }
    public string AgentName { get; }
    public IBrush AgentAccent { get; }
    public BotGroupTurnStatus Status { get; }
    public bool IsQueued { get; }
    public bool IsRunning { get; }
    public bool IsCompleted { get; }
    public bool IsStopped { get; }
    public bool IsFailed { get; }
    public bool IsActive { get; }
    public string StatusLabel { get; }
    public string? ErrorText { get; }
    public bool HasError { get; }
    public bool CanInspect { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBody))]
    public partial string BodyMarkdown { get; set; } = "";

    public bool HasBody => !string.IsNullOrWhiteSpace(BodyMarkdown);

    [RelayCommand]
    private Task StopAsync() => _stopBot(BotId);

    [RelayCommand]
    private void Reply() => _replyBot(BotId);

    [RelayCommand]
    private void Inspect()
    {
        if (CanInspect)
        {
            _inspectSession(BotId, ChatId);
        }
    }
}

/// <summary>One transcript entry: an owner post, a bot handoff line, or a hop-limit pause notice, plus its Bot Turn cards.</summary>
public sealed partial class BotGroupMessageItemViewModel : ObservableObject
{
    private readonly Func<string, Task> _continueHandoff;

    public BotGroupMessageItemViewModel(
        BotGroupMessage message,
        IReadOnlyList<BotGroupMentionSegment> mentionSegments,
        string? handoffText,
        string? pausedTargetsText,
        IReadOnlyList<BotGroupTurnItemViewModel> turns,
        Func<string, Task> continueHandoff)
    {
        MessageId = message.Id;
        Sequence = message.Sequence;
        IsOwner = message.IsOwner;
        IsHopLimitPaused = !message.IsOwner && message.PausedByHopLimit;
        IsHandoffLine = !message.IsOwner && !message.PausedByHopLimit && message.IsMentionHandoff;
        Text = message.Text;
        MentionSegments = mentionSegments;
        HandoffText = handoffText ?? message.Text;
        PausedTargetsText = pausedTargetsText;
        HasPausedTargets = !string.IsNullOrWhiteSpace(pausedTargetsText);
        TimeText = message.CreatedAt.ToLocalTime().ToString("HH:mm");
        Turns = turns;
        HasTurns = turns.Count > 0;
        _continueHandoff = continueHandoff;
    }

    public string MessageId { get; }
    public long Sequence { get; }
    public bool IsOwner { get; }
    public bool IsHandoffLine { get; }
    public bool IsHopLimitPaused { get; }
    public string Text { get; }
    public IReadOnlyList<BotGroupMentionSegment> MentionSegments { get; }
    public string HandoffText { get; }
    public string? PausedTargetsText { get; }
    public bool HasPausedTargets { get; }
    public string TimeText { get; }
    public IReadOnlyList<BotGroupTurnItemViewModel> Turns { get; }
    public bool HasTurns { get; }

    [ObservableProperty] public partial bool IsResuming { get; private set; }

    [RelayCommand]
    private async Task ContinueHandoffAsync()
    {
        if (IsResuming)
        {
            return;
        }
        IsResuming = true;
        try
        {
            await _continueHandoff(MessageId);
        }
        finally
        {
            IsResuming = false;
        }
    }
}

/// <summary>Center workspace ViewModel for an active multi-bot Group Room (or thread).</summary>
public sealed partial class BotGroupRoomViewModel : ObservableObject, IDisposable
{
    private const int PageStep = 60;

    private readonly IBotGroupService _groups;
    private readonly IBotService _bots;
    private readonly IDialogService _dialogs;
    private readonly IAgentCatalog _catalog;
    private readonly IProjectIconStore? _icons;
    private readonly Dictionary<string, BotGroupTurnItemViewModel> _turnsByOp = new(StringComparer.Ordinal);

    private string? _activeGroupId;
    private string? _activeThreadId;
    private int _loadedLimit = PageStep;
    private long _lastMessageSequence;

    public BotGroupRoomViewModel(
        IBotGroupService groups,
        IBotService bots,
        IDialogService dialogs,
        IAgentCatalog catalog,
        IProjectIconStore? icons = null)
    {
        _groups = groups;
        _bots = bots;
        _dialogs = dialogs;
        _catalog = catalog;
        _icons = icons;

        _groups.Changed += OnGroupsChanged;
        _groups.StreamingUpdated += OnStreamingUpdated;
        _bots.Changed += (_, _) => UiContext.Run(() => Refresh(scrollToBottomIfNew: false));
        ArcLanguage.Changed += OnLanguageChanged;

        Refresh(scrollToBottomIfNew: true);
    }

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e) =>
        UiContext.Run(() => Refresh(scrollToBottomIfNew: false));

    public void Dispose() => ArcLanguage.Changed -= OnLanguageChanged;

    public ObservableCollection<BotGroupAvatarTile> GroupAvatars { get; } = [];
    public ObservableCollection<BotGroupRoomTabViewModel> RoomTabs { get; } = [];
    public ObservableCollection<BotGroupMessageItemViewModel> Messages { get; } = [];
    public ObservableCollection<BotGroupApprovalItemViewModel> Approvals { get; } = [];
    public ObservableCollection<SelectOption> ApprovalSelectorOptions { get; } = [];
    public ObservableCollection<BotGroupReplyModeChoiceViewModel> ReplyModeChoices { get; } = [];
    public ObservableCollection<BotGroupMentionChoiceViewModel> MentionChoices { get; } = [];

    public event EventHandler<bool>? ScrollToBottomRequested;
    public event EventHandler? FocusComposerRequested;

    [ObservableProperty] public partial bool HasGroup { get; private set; }
    [ObservableProperty] public partial string GroupName { get; private set; } = "";
    [ObservableProperty] public partial string MemberCountText { get; private set; } = "";
    [ObservableProperty] public partial bool HasSingleAvatar { get; private set; }
    [ObservableProperty] public partial bool HasMultiAvatar { get; private set; }
    [ObservableProperty] public partial BotGroupAvatarTile? SingleAvatar { get; private set; }
    [ObservableProperty] public partial bool IsWorking { get; private set; }
    [ObservableProperty] public partial bool HasEarlier { get; private set; }
    [ObservableProperty] public partial bool IsTranscriptEmpty { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultipleApprovals), nameof(ApprovalCountText))]
    public partial bool HasApprovals { get; private set; }

    [ObservableProperty] public partial int ApprovalCount { get; private set; }
    [ObservableProperty] public partial string? SelectedApprovalId { get; set; }
    [ObservableProperty] public partial BotGroupApprovalItemViewModel? SelectedApproval { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    public partial string ComposerText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    public partial bool IsSending { get; private set; }

    [ObservableProperty] public partial string AudiencePreviewText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    public partial bool HasAudienceError { get; private set; }

    [ObservableProperty] public partial string AudienceErrorText { get; private set; } = "";
    [ObservableProperty] public partial string? SendErrorText { get; private set; }
    [ObservableProperty] public partial bool HasSendError { get; private set; }
    [ObservableProperty] public partial bool IsReplyPickerOpen { get; set; }
    [ObservableProperty] public partial bool IsMentionPickerOpen { get; set; }

    public bool HasMultipleApprovals => ApprovalCount > 1;
    public string ApprovalCountText => AppLangResources.Format(AppLangResourceKind.BotGroupApprovalCount, ApprovalCount);
    public bool CanSend => HasGroup && !IsSending && !HasAudienceError && !string.IsNullOrWhiteSpace(ComposerText);

    partial void OnComposerTextChanged(string value)
    {
        if (HasSendError)
        {
            SendErrorText = null;
            HasSendError = false;
        }
        UpdateDraftAudiencePreview();
    }

    partial void OnSelectedApprovalIdChanged(string? value)
    {
        SelectedApproval = Approvals.FirstOrDefault(a => string.Equals(a.ApprovalId, value, StringComparison.Ordinal))
            ?? Approvals.FirstOrDefault();
    }

    private void OnGroupsChanged(object? sender, BotGroupChangedEventArgs e)
    {
        UiContext.Run(() =>
        {
            var group = _groups.CurrentGroup;
            var switchedRoom = group is not null
                && (!string.Equals(_activeGroupId, group.Id, StringComparison.Ordinal)
                    || !string.Equals(_activeThreadId, _groups.CurrentThreadId, StringComparison.Ordinal));
            if (switchedRoom)
            {
                _loadedLimit = PageStep;
                _lastMessageSequence = 0;
            }
            Refresh(scrollToBottomIfNew: switchedRoom || e.Kind == BotGroupChangeKind.Messages);
        });
    }

    private void OnStreamingUpdated(object? sender, BotGroupStreamingEventArgs e)
    {
        UiContext.Run(() =>
        {
            if (!string.Equals(e.GroupId, _activeGroupId, StringComparison.Ordinal)
                || !string.Equals(e.ThreadId, _activeThreadId, StringComparison.Ordinal))
            {
                return;
            }
            if (_turnsByOp.TryGetValue(e.OperationId, out var turnVm))
            {
                turnVm.BodyMarkdown = e.PreviewText;
            }
        });
    }

    public void Refresh(bool scrollToBottomIfNew = false)
    {
        var group = _groups.CurrentGroup;
        if (group is null)
        {
            HasGroup = false;
            _activeGroupId = null;
            _activeThreadId = null;
            GroupName = "";
            MemberCountText = "";
            GroupAvatars.Clear();
            RoomTabs.Clear();
            Messages.Clear();
            _turnsByOp.Clear();
            Approvals.Clear();
            ApprovalSelectorOptions.Clear();
            SelectedApproval = null;
            SelectedApprovalId = null;
            HasApprovals = false;
            ApprovalCount = 0;
            IsWorking = false;
            HasEarlier = false;
            IsTranscriptEmpty = true;
            return;
        }

        var threadId = _groups.CurrentThreadId;
        var switchedRoom = !string.Equals(_activeGroupId, group.Id, StringComparison.Ordinal)
            || !string.Equals(_activeThreadId, threadId, StringComparison.Ordinal);
        _activeGroupId = group.Id;
        _activeThreadId = threadId;

        HasGroup = true;
        GroupName = group.Name;
        MemberCountText = AppLangResources.Format(AppLangResourceKind.BotGroupMemberCount, group.Members.Count);

        var avatars = BotGroupUiHelpers.BuildAvatarCluster(group, _bots, _icons, 4);
        GroupAvatars.Clear();
        foreach (var tile in avatars)
        {
            GroupAvatars.Add(tile);
        }
        HasSingleAvatar = avatars.Count == 1;
        HasMultiAvatar = avatars.Count > 1;
        SingleAvatar = avatars.Count > 0 ? avatars[0] : null;

        RebuildRoomTabs(group, threadId);
        RebuildComposerPickers(group, threadId);

        var page = _groups.ReadRoom(group.Id, threadId, beforeSequence: null, limit: _loadedLimit);
        HasEarlier = page.HasEarlier;

        var roomMembers = group.GetRoomMembers(threadId);
        var memberNames = roomMembers
            .Select(m => _bots.Bots.FirstOrDefault(b => b.Id == m.BotId)?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Cast<string>()
            .ToList();

        _turnsByOp.Clear();
        Messages.Clear();
        var anyActiveTurn = false;
        long maxSeq = 0;

        foreach (var msg in page.Messages)
        {
            if (msg.Sequence > maxSeq)
            {
                maxSeq = msg.Sequence;
            }

            var turnVms = new List<BotGroupTurnItemViewModel>(msg.Turns.Count);
            foreach (var turn in msg.Turns)
            {
                if (turn.Status is BotGroupTurnStatus.Queued or BotGroupTurnStatus.Running)
                {
                    anyActiveTurn = true;
                }
                var botDef = _bots.Bots.FirstOrDefault(b => string.Equals(b.Id, turn.BotId, StringComparison.Ordinal));
                var avatar = BotGroupUiHelpers.BuildBotAvatar(
                    turn.BotId,
                    turn.BotName,
                    botDef?.Color ?? turn.BotColor,
                    botDef?.Icon ?? turn.BotIcon,
                    botDef?.Image,
                    _icons);
                var agentName = _catalog.Find(turn.AgentId)?.Name ?? turn.AgentId;
                var preview = turn.Status == BotGroupTurnStatus.Running
                    ? _groups.GetStreamingPreview(turn.OperationId)
                    : null;

                var turnVm = new BotGroupTurnItemViewModel(
                    turn,
                    avatar,
                    agentName,
                    preview,
                    StopSingleBotAsync,
                    ReplyToBot,
                    InspectMemberSession);
                _turnsByOp[turn.OperationId] = turnVm;
                turnVms.Add(turnVm);
            }

            var segments = msg.IsOwner
                ? BotGroupMentionParser.SplitDisplaySegments(msg.Text, memberNames, includeEveryone: true)
                : [];

            string? handoffText = null;
            if (!msg.IsOwner && msg.IsMentionHandoff && !msg.PausedByHopLimit)
            {
                var fromName = string.IsNullOrWhiteSpace(msg.RequesterBotName) ? "Bot" : msg.RequesterBotName!;
                var toNames = msg.Turns.Count > 0
                    ? string.Join(", ", msg.Turns.Select(t => t.BotName).Distinct(StringComparer.Ordinal))
                    : msg.Text;
                handoffText = AppLangResources.Format(AppLangResourceKind.BotGroupHandoffLine, fromName, toNames);
            }

            string? pausedTargetsText = null;
            if (msg.PausedByHopLimit && msg.PausedTargetBotIds is { Count: > 0 })
            {
                var targetNames = msg.PausedTargetBotIds
                    .Select(id => _bots.Bots.FirstOrDefault(b => b.Id == id)?.Name ?? id)
                    .ToList();
                pausedTargetsText = string.Join(", ", targetNames.Select(n => "@" + n));
            }

            Messages.Add(new BotGroupMessageItemViewModel(
                msg,
                segments,
                handoffText,
                pausedTargetsText,
                turnVms,
                ContinueHandoffAsync));
        }

        IsTranscriptEmpty = Messages.Count == 0;
        IsWorking = anyActiveTurn || _groups.IsGroupWorking(group.Id);

        RebuildApprovals(page.Approvals);
        UpdateDraftAudiencePreview();

        var hasNewMessage = maxSeq > _lastMessageSequence;
        _lastMessageSequence = maxSeq;
        if (switchedRoom || (scrollToBottomIfNew && hasNewMessage))
        {
            ScrollToBottomRequested?.Invoke(this, switchedRoom);
        }
    }

    private void RebuildRoomTabs(BotGroupDefinition group, string? activeThreadId)
    {
        RoomTabs.Clear();
        var mainPending = _groups.GetPendingApprovals(group.Id).Count(a => string.IsNullOrWhiteSpace(a.ThreadId));
        RoomTabs.Add(new BotGroupRoomTabViewModel(
            group.Id,
            threadId: null,
            title: AppLangResources.Get(AppLangResourceKind.BotGroupMainRoom),
            isMainRoom: true,
            isSelected: string.IsNullOrWhiteSpace(activeThreadId),
            pendingApprovalCount: mainPending,
            select: (gid, tid) => _groups.SelectGroup(gid, tid)));

        foreach (var thread in group.Threads)
        {
            var title = string.IsNullOrWhiteSpace(thread.Title)
                ? AppLangResources.Get(AppLangResourceKind.BotGroupUntitledThread)
                : thread.Title!;
            var pending = _groups.GetPendingApprovals(group.Id, thread.Id).Count;
            RoomTabs.Add(new BotGroupRoomTabViewModel(
                group.Id,
                thread.Id,
                title,
                isMainRoom: false,
                isSelected: string.Equals(activeThreadId, thread.Id, StringComparison.Ordinal),
                pendingApprovalCount: pending,
                select: (gid, tid) => _groups.SelectGroup(gid, tid)));
        }
    }

    private void RebuildComposerPickers(BotGroupDefinition group, string? threadId)
    {
        ReplyModeChoices.Clear();
        ReplyModeChoices.Add(new BotGroupReplyModeChoiceViewModel(
            AppLangResources.Get(AppLangResourceKind.BotGroupReplyModeMentions),
            new BotGroupReplyMode { Kind = BotGroupReplyModeKind.Mentions },
            avatar: null,
            icon: LucideIconKind.AtSign,
            isSelected: group.ReplyMode.Kind == BotGroupReplyModeKind.Mentions,
            pick: SetReplyModeAsync));
        ReplyModeChoices.Add(new BotGroupReplyModeChoiceViewModel(
            AppLangResources.Get(AppLangResourceKind.BotGroupReplyModeAll),
            new BotGroupReplyMode { Kind = BotGroupReplyModeKind.All },
            avatar: null,
            icon: LucideIconKind.Users,
            isSelected: group.ReplyMode.Kind == BotGroupReplyModeKind.All,
            pick: SetReplyModeAsync));

        MentionChoices.Clear();
        MentionChoices.Add(new BotGroupMentionChoiceViewModel(
            AppLangResources.Get(AppLangResourceKind.BotGroupComposerEveryone),
            "@everyone ",
            avatar: null,
            insert: InsertMentionToken));

        var roomMembers = group.GetRoomMembers(threadId);
        foreach (var member in roomMembers)
        {
            var bot = _bots.Bots.FirstOrDefault(b => string.Equals(b.Id, member.BotId, StringComparison.Ordinal));
            if (bot is null)
            {
                continue;
            }
            var avatar = BotGroupUiHelpers.BuildBotAvatar(bot.Id, bot.Name, bot.Color, bot.Icon, bot.Image, _icons);
            ReplyModeChoices.Add(new BotGroupReplyModeChoiceViewModel(
                bot.Name,
                new BotGroupReplyMode { Kind = BotGroupReplyModeKind.Bot, BotId = bot.Id },
                avatar: avatar,
                icon: LucideIconKind.Bot,
                isSelected: group.ReplyMode.Kind == BotGroupReplyModeKind.Bot
                    && string.Equals(group.ReplyMode.BotId, bot.Id, StringComparison.Ordinal),
                pick: SetReplyModeAsync));
            MentionChoices.Add(new BotGroupMentionChoiceViewModel(
                "@" + bot.Name,
                $"@{bot.Name} ",
                avatar: avatar,
                insert: InsertMentionToken));
        }
    }

    private void RebuildApprovals(IReadOnlyList<BotGroupPendingApproval> pending)
    {
        var previousId = SelectedApprovalId;
        Approvals.Clear();
        ApprovalSelectorOptions.Clear();

        foreach (var approval in pending)
        {
            var bot = _bots.Bots.FirstOrDefault(b => string.Equals(b.Id, approval.BotId, StringComparison.Ordinal));
            var avatar = BotGroupUiHelpers.BuildBotAvatar(
                approval.BotId,
                approval.BotName,
                bot?.Color ?? approval.BotColor,
                bot?.Icon,
                bot?.Image,
                _icons);
            var item = new BotGroupApprovalItemViewModel(approval, avatar, RespondApproval);
            Approvals.Add(item);
            ApprovalSelectorOptions.Add(new SelectOption(approval.ApprovalId, item.HeaderLabel));
        }

        ApprovalCount = Approvals.Count;
        HasApprovals = Approvals.Count > 0;
        if (Approvals.Count == 0)
        {
            SelectedApprovalId = null;
            SelectedApproval = null;
        }
        else
        {
            var chosen = Approvals.FirstOrDefault(a => string.Equals(a.ApprovalId, previousId, StringComparison.Ordinal))
                ?? Approvals[0];
            SelectedApprovalId = chosen.ApprovalId;
            SelectedApproval = chosen;
        }
        OnPropertyChanged(nameof(HasMultipleApprovals));
        OnPropertyChanged(nameof(ApprovalCountText));
    }

    private void UpdateDraftAudiencePreview()
    {
        var group = _groups.CurrentGroup;
        if (group is null)
        {
            AudiencePreviewText = "";
            HasAudienceError = false;
            AudienceErrorText = "";
            return;
        }

        var members = ResolveNamedMembers(group, _activeThreadId);
        var audience = BotGroupMentionParser.ResolveDraftAudience(ComposerText, group.ReplyMode, members);

        if (!audience.IsValid)
        {
            HasAudienceError = true;
            AudienceErrorText = audience.Error switch
            {
                BotGroupAudienceError.AmbiguousMention => AppLangResources.Get(AppLangResourceKind.BotGroupComposerErrorAmbiguous),
                BotGroupAudienceError.UnknownLeadingMention => AppLangResources.Get(AppLangResourceKind.BotGroupComposerErrorUnknown),
                BotGroupAudienceError.DefaultReplierNotMember => AppLangResources.Get(AppLangResourceKind.BotGroupComposerErrorDefaultMissing),
                _ => AppLangResources.Get(AppLangResourceKind.BotGroupComposerErrorUnknown),
            };
            AudiencePreviewText = AudienceErrorText;
            return;
        }

        HasAudienceError = false;
        AudienceErrorText = "";

        if (audience.BotIds.Count == 0)
        {
            AudiencePreviewText = AppLangResources.Get(AppLangResourceKind.BotGroupComposerRepliesNone);
            return;
        }

        if (!audience.AddressedByMention && group.ReplyMode.Kind == BotGroupReplyModeKind.All)
        {
            AudiencePreviewText = AppLangResources.Format(AppLangResourceKind.BotGroupComposerRepliesAll, audience.BotIds.Count);
            return;
        }

        var names = audience.BotIds
            .Select(id => members.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal))?.Name ?? id)
            .ToList();

        if (audience.AddressedByMention)
        {
            AudiencePreviewText = names.Count == members.Count && members.Count > 1
                ? AppLangResources.Format(AppLangResourceKind.BotGroupComposerRepliesAll, names.Count)
                : AppLangResources.Format(AppLangResourceKind.BotGroupComposerRepliesMentions, string.Join(", ", names));
        }
        else
        {
            AudiencePreviewText = string.Join(", ", names);
        }
    }

    private IReadOnlyList<BotGroupNamedMember> ResolveNamedMembers(BotGroupDefinition group, string? threadId)
    {
        var roomMembers = group.GetRoomMembers(threadId);
        var result = new List<BotGroupNamedMember>(roomMembers.Count);
        foreach (var member in roomMembers)
        {
            var bot = _bots.Bots.FirstOrDefault(b => string.Equals(b.Id, member.BotId, StringComparison.Ordinal));
            if (bot is not null)
            {
                result.Add(new BotGroupNamedMember(bot.Id, bot.Name));
            }
        }
        return result;
    }

    [RelayCommand]
    public async Task SendAsync()
    {
        var group = _groups.CurrentGroup;
        if (group is null || !CanSend)
        {
            return;
        }

        var text = ComposerText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        IsSending = true;
        SendErrorText = null;
        HasSendError = false;
        try
        {
            await _groups.SendMessageAsync(group.Id, group.Revision, text, _activeThreadId);
            ComposerText = "";
            ScrollToBottomRequested?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            SendErrorText = ex.Message;
            HasSendError = true;
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        if (_activeGroupId is null)
        {
            return;
        }
        await _groups.StopAsync(_activeGroupId, _activeThreadId, botId: null);
    }

    [RelayCommand]
    private async Task CreateThreadAsync()
    {
        if (_activeGroupId is null)
        {
            return;
        }
        await _groups.CreateThreadAsync(_activeGroupId);
    }

    [RelayCommand]
    private async Task ManageMembersAsync()
    {
        var group = _groups.CurrentGroup;
        if (group is null)
        {
            return;
        }
        var result = await _dialogs.AskEditBotGroupAsync(group, _bots.Bots);
        if (result is null)
        {
            return;
        }
        await _groups.UpdateGroupAsync(new UpdateBotGroupRequest(
            group.Id,
            group.Revision,
            result.Name,
            result.BotIds,
            result.ReplyMode,
            result.MaxHops));
    }

    [RelayCommand]
    private void LoadEarlier()
    {
        _loadedLimit += PageStep;
        Refresh(scrollToBottomIfNew: false);
    }

    private async Task SetReplyModeAsync(BotGroupReplyMode mode)
    {
        IsReplyPickerOpen = false;
        var group = _groups.CurrentGroup;
        if (group is null)
        {
            return;
        }
        await _groups.UpdateGroupAsync(new UpdateBotGroupRequest(
            group.Id,
            group.Revision,
            ReplyMode: mode));
    }

    private void InsertMentionToken(string token)
    {
        IsMentionPickerOpen = false;
        var current = ComposerText ?? "";
        if (current.Length == 0 || char.IsWhiteSpace(current[^1]))
        {
            ComposerText = current + token;
        }
        else
        {
            ComposerText = current + " " + token;
        }
        FocusComposerRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task StopSingleBotAsync(string botId)
    {
        if (_activeGroupId is null)
        {
            return;
        }
        await _groups.StopAsync(_activeGroupId, _activeThreadId, botId);
    }

    private void ReplyToBot(string botId)
    {
        var group = _groups.CurrentGroup;
        if (group is null)
        {
            return;
        }
        var members = ResolveNamedMembers(group, _activeThreadId);
        ComposerText = BotGroupMentionParser.PrependReplyMention(botId, ComposerText ?? "", members);
        FocusComposerRequested?.Invoke(this, EventArgs.Empty);
    }

    private void InspectMemberSession(string botId, string chatId) =>
        _groups.OpenMemberSessionInspector(botId, chatId);

    private async Task ContinueHandoffAsync(string pausedMessageId)
    {
        if (_activeGroupId is null)
        {
            return;
        }
        await _groups.ContinueHandoffAsync(_activeGroupId, pausedMessageId, extraHops: 3);
    }

    private void RespondApproval(string approvalId, string? optionId) =>
        _groups.RespondApproval(approvalId, optionId);
}
