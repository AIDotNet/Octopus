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

public sealed partial class BotGroupThreadTabViewModel(
    string? threadId,
    string label,
    bool isSelected,
    Func<string?, Task> select) : ObservableObject
{
    public string? ThreadId { get; } = threadId;
    public string Label { get; } = label;
    public bool IsSelected { get; } = isSelected;

    [RelayCommand]
    private Task SelectAsync() => select(ThreadId);
}

public sealed partial class BotGroupTargetChipViewModel(
    string botId,
    string botName,
    string botColor,
    bool isSelected,
    Func<string, Task> select) : ObservableObject
{
    public string BotId { get; } = botId;
    public string BotName { get; } = botName;
    public string Initial { get; } = botName.Length > 0 ? botName[..1].ToUpperInvariant() : "B";
    public IBrush AvatarBrush { get; } = BotUiHelpers.ParseBrush(botColor, "#5B5BD6");
    public bool IsSelected { get; } = isSelected;

    [RelayCommand]
    private Task SelectAsync() => select(BotId);
}

public sealed partial class BotGroupTurnCardViewModel : ObservableObject
{
    private readonly Action<string> _replyAt;

    public BotGroupTurnCardViewModel(RemoteBotGroupTurn turn, Action<string> replyAt)
    {
        Turn = turn;
        _replyAt = replyAt;
        BotName = turn.BotName;
        Initial = turn.BotName.Length > 0 ? turn.BotName[..1].ToUpperInvariant() : "B";
        AvatarBrush = BotUiHelpers.ParseBrush(turn.BotColor, "#5B5BD6");
        IsQueued = turn.IsQueued;
        IsRunning = turn.IsRunning;
        IsCompleted = turn.IsCompleted;
        IsStopped = turn.IsStopped;
        IsFailed = turn.IsFailed;
        HasStatusPill = !IsCompleted;
        StatusText = turn.Status.ToLowerInvariant() switch
        {
            "running" => MobileLangResources.Get(MobileLangResourceKind.BotGroupsTurnRunning),
            "queued" => MobileLangResources.Get(MobileLangResourceKind.BotGroupsTurnQueued),
            "stopped" => MobileLangResources.Get(MobileLangResourceKind.BotGroupsTurnStopped),
            "failed" => MobileLangResources.Get(MobileLangResourceKind.BotGroupsTurnFailed),
            _ => "",
        };
        BodyText = !string.IsNullOrWhiteSpace(turn.Reply)
            ? turn.Reply!
            : !string.IsNullOrWhiteSpace(turn.StreamingPreview)
                ? turn.StreamingPreview!
                : "";
        HasBody = BodyText.Length > 0;
        ErrorText = turn.Error ?? "";
        HasError = !string.IsNullOrWhiteSpace(turn.Error);
        TimeText = BotUiHelpers.FormatRelativeTime(turn.CompletedAt ?? turn.CreatedAt);
    }

    public RemoteBotGroupTurn Turn { get; }
    public string BotName { get; }
    public string Initial { get; }
    public IBrush AvatarBrush { get; }
    public bool IsQueued { get; }
    public bool IsRunning { get; }
    public bool IsCompleted { get; }
    public bool IsStopped { get; }
    public bool IsFailed { get; }
    public bool HasStatusPill { get; }
    public string StatusText { get; }
    public string BodyText { get; }
    public bool HasBody { get; }
    public string ErrorText { get; }
    public bool HasError { get; }
    public string TimeText { get; }

    [RelayCommand]
    private void ReplyAt() => _replyAt(BotName);
}

public sealed partial class BotGroupEntryItemViewModel : ObservableObject
{
    private readonly Func<string, Task> _continueHandoff;

    public BotGroupEntryItemViewModel(
        RemoteBotGroupEntry entry,
        Action<string> replyAt,
        Func<string, Task> continueHandoff)
    {
        Entry = entry;
        _continueHandoff = continueHandoff;

        IsOwner = entry.IsOwner;
        IsMentionHandoff = !entry.IsOwner && entry.IsMentionHandoff && !entry.PausedByHopLimit;
        IsHopLimitPause = entry.PausedByHopLimit;
        AuthorLabel = entry.IsOwner
            ? MobileLangResources.Get(MobileLangResourceKind.BotGroupsYou)
            : (entry.AuthorBotName ?? entry.RequesterBotName ?? "Bot");
        Text = entry.Text;
        TimeText = BotUiHelpers.FormatRelativeTime(entry.CreatedAt);
        Turns = [.. entry.Turns.Select(t => new BotGroupTurnCardViewModel(t, replyAt))];
        HasTurns = Turns.Count > 0;
    }

    public RemoteBotGroupEntry Entry { get; }
    public bool IsOwner { get; }
    public bool IsMentionHandoff { get; }
    public bool IsHopLimitPause { get; }
    public string AuthorLabel { get; }
    public string Text { get; }
    public string TimeText { get; }
    public IReadOnlyList<BotGroupTurnCardViewModel> Turns { get; }
    public bool HasTurns { get; }

    [RelayCommand]
    private Task ContinueHandoffAsync() => _continueHandoff(Entry.Id);
}

public sealed partial class BotGroupApprovalOptionViewModel(
    string optionId,
    string label,
    string? kind,
    Func<string?, Task> choose) : ObservableObject
{
    public string OptionId { get; } = optionId;
    public string Label { get; } = label;
    public bool IsAllow { get; } = string.Equals(kind, "allow_once", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "allow_always", StringComparison.OrdinalIgnoreCase);
    public bool IsReject { get; } = string.Equals(kind, "reject_once", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "reject_always", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private Task ChooseAsync() => choose(OptionId);
}

public sealed class BotGroupApprovalCardViewModel
{
    public BotGroupApprovalCardViewModel(
        RemoteBotGroupApproval approval,
        Func<string, string?, Task> respond)
    {
        Approval = approval;
        BotName = approval.BotName;
        Title = approval.Title;
        Detail = approval.Detail ?? "";
        HasDetail = !string.IsNullOrWhiteSpace(approval.Detail);
        WarningMessage = approval.WarningMessage ?? "";
        HasWarning = !string.IsNullOrWhiteSpace(approval.WarningMessage);
        Options = [.. approval.Options.Select(opt => new BotGroupApprovalOptionViewModel(
            opt.Id,
            opt.Label,
            opt.Kind,
            chosen => respond(approval.ApprovalId, chosen)))];
    }

    public RemoteBotGroupApproval Approval { get; }
    public string BotName { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool HasDetail { get; }
    public string WarningMessage { get; }
    public bool HasWarning { get; }
    public IReadOnlyList<BotGroupApprovalOptionViewModel> Options { get; }
}

public sealed partial class BotGroupMemberToggleViewModel : ObservableObject
{
    public BotGroupMemberToggleViewModel(string botId, string botName, string botColor, string agentId, bool isSelected)
    {
        BotId = botId;
        BotName = botName;
        Initial = botName.Length > 0 ? botName[..1].ToUpperInvariant() : "B";
        AvatarBrush = BotUiHelpers.ParseBrush(botColor, "#5B5BD6");
        AgentLabel = agentId;
        IsSelected = isSelected;
    }

    public string BotId { get; }
    public string BotName { get; }
    public string Initial { get; }
    public IBrush AvatarBrush { get; }
    public string AgentLabel { get; }

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>
/// Full mobile Group Room view model: thread switcher, transcript (owner posts, bot handoffs, hop-limit pause card,
/// bot turn cards with @Reply), pending approvals dock, ReplyMode switcher, composer with Send &amp; Stop, and member management sheet.
/// </summary>
public sealed partial class BotGroupDetailViewModel : ObservableObject, IDisposable
{
    private readonly RemoteBotGroup _group;
    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;
    private bool _disposed;
    private long? _earliestSequence;

    public BotGroupDetailViewModel(
        RemoteBotGroup group,
        IRemoteWorkspace workspace,
        INavigator navigator,
        string? initialThreadId = null)
    {
        _group = group;
        _workspace = workspace;
        _navigator = navigator;
        CurrentThreadId = initialThreadId;

        _group.PropertyChanged += OnGroupChanged;
        _group.Members.CollectionChanged += OnGroupStructureChanged;
        _group.Threads.CollectionChanged += OnGroupStructureChanged;

        SyncHeaderAndModes();
        RebuildThreadTabs();
        _ = RefreshAsync();
    }

    public RemoteBotGroup Group => _group;

    public ObservableCollection<BotGroupThreadTabViewModel> ThreadTabs { get; } = [];
    public ObservableCollection<BotGroupEntryItemViewModel> Entries { get; } = [];
    public ObservableCollection<BotGroupApprovalCardViewModel> PendingApprovals { get; } = [];
    public ObservableCollection<BotGroupTargetChipViewModel> TargetBotChoices { get; } = [];
    public ObservableCollection<BotGroupMemberToggleViewModel> MemberChoices { get; } = [];

    [ObservableProperty] public partial string GroupName { get; private set; } = "";
    [ObservableProperty] public partial string GroupSubtitle { get; private set; } = "";
    [ObservableProperty] public partial string? CurrentThreadId { get; private set; }
    [ObservableProperty] public partial string ReplyModeKind { get; private set; } = "mentions";
    [ObservableProperty] public partial string? ReplyModeBotId { get; private set; }

    [ObservableProperty] public partial string ComposerText { get; set; } = "";
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial bool IsSending { get; private set; }
    [ObservableProperty] public partial bool HasEarlier { get; private set; }
    [ObservableProperty] public partial bool HasEntries { get; private set; }
    [ObservableProperty] public partial bool HasPendingApprovals { get; private set; }
    [ObservableProperty] public partial string PendingApprovalsHeader { get; private set; } = "";
    [ObservableProperty] public partial bool CanStop { get; private set; }
    [ObservableProperty] public partial string? ErrorMessage { get; private set; }

    // Member management & group settings sheet
    [ObservableProperty] public partial bool IsMembersSheetOpen { get; set; }
    [ObservableProperty] public partial string EditGroupName { get; set; } = "";
    [ObservableProperty] public partial bool IsDeleteConfirmOpen { get; set; }

    public bool IsReplyModeBot => string.Equals(ReplyModeKind, "bot", StringComparison.OrdinalIgnoreCase);
    public bool IsReplyModeAll => string.Equals(ReplyModeKind, "all", StringComparison.OrdinalIgnoreCase);
    public bool IsReplyModeMentions => !IsReplyModeBot && !IsReplyModeAll;
    public bool ShowTargetBotBar => IsReplyModeBot && TargetBotChoices.Count > 0;
    public bool IsTranscriptEmpty => !IsLoading && !HasEntries;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool CanSend => !IsSending && !string.IsNullOrWhiteSpace(ComposerText);
    public string DeleteConfirmTitle => MobileLangResources.Format(MobileLangResourceKind.BotGroupsDeleteConfirm, GroupName);

    partial void OnComposerTextChanged(string value) => OnPropertyChanged(nameof(CanSend));
    partial void OnIsSendingChanged(bool value) => OnPropertyChanged(nameof(CanSend));
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsTranscriptEmpty));
    partial void OnHasEntriesChanged(bool value) => OnPropertyChanged(nameof(IsTranscriptEmpty));
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnReplyModeKindChanged(string value)
    {
        OnPropertyChanged(nameof(IsReplyModeBot));
        OnPropertyChanged(nameof(IsReplyModeAll));
        OnPropertyChanged(nameof(IsReplyModeMentions));
        OnPropertyChanged(nameof(ShowTargetBotBar));
    }

    [RelayCommand]
    private void Back() => _navigator.Back();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }
        IsLoading = true;
        try
        {
            var snapshot = await _workspace.ReadBotGroupRoomAsync(_group, CurrentThreadId);
            if (_disposed)
            {
                return;
            }
            ApplySnapshot(snapshot, prepend: false);
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadEarlierAsync()
    {
        if (!HasEarlier || _earliestSequence is null || IsLoading)
        {
            return;
        }
        IsLoading = true;
        try
        {
            var snapshot = await _workspace.ReadBotGroupRoomAsync(_group, CurrentThreadId, beforeSequence: _earliestSequence);
            if (_disposed)
            {
                return;
            }
            ApplySnapshot(snapshot, prepend: true);
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = ComposerText.Trim();
        if (text.Length == 0 || IsSending)
        {
            return;
        }
        ErrorMessage = null;
        IsSending = true;
        try
        {
            await _workspace.SendBotGroupMessageAsync(_group, text, CurrentThreadId);
            ComposerText = "";
            await RefreshAsync();
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        ErrorMessage = null;
        try
        {
            await _workspace.StopBotGroupAsync(_group, CurrentThreadId);
            await RefreshAsync();
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
    }

    [RelayCommand]
    private async Task CreateThreadAsync()
    {
        ErrorMessage = null;
        try
        {
            var thread = await _workspace.CreateBotGroupThreadAsync(_group);
            CurrentThreadId = thread.Id;
            RebuildThreadTabs();
            await RefreshAsync();
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
    }

    [RelayCommand]
    private Task SetReplyModeBotAsync() => SetReplyModeAsync("bot", _group.ReplyModeBotId ?? _group.Members.FirstOrDefault()?.BotId);

    [RelayCommand]
    private Task SetReplyModeAllAsync() => SetReplyModeAsync("all", null);

    [RelayCommand]
    private Task SetReplyModeMentionsAsync() => SetReplyModeAsync("mentions", null);

    [RelayCommand]
    private void OpenMembersSheet()
    {
        ErrorMessage = null;
        EditGroupName = _group.Name;
        RebuildMemberChoices();
        IsMembersSheetOpen = true;
    }

    [RelayCommand]
    private async Task SaveMembersAsync()
    {
        var selectedIds = MemberChoices.Where(m => m.IsSelected).Select(m => m.BotId).ToList();
        if (selectedIds.Count == 0)
        {
            ErrorMessage = MobileLangResources.Get(MobileLangResourceKind.BotGroupsNewMembersRequired);
            return;
        }
        var nextName = string.IsNullOrWhiteSpace(EditGroupName) ? _group.Name : EditGroupName.Trim();
        ErrorMessage = null;
        try
        {
            await _workspace.UpdateBotGroupAsync(_group, new UpdateRemoteBotGroupRequest(
                _group.Id,
                _group.Revision,
                Name: nextName,
                BotIds: selectedIds));
            IsMembersSheetOpen = false;
            SyncHeaderAndModes();
            await RefreshAsync();
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
    }

    [RelayCommand]
    private void AskDeleteGroup()
    {
        IsMembersSheetOpen = false;
        IsDeleteConfirmOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmDeleteGroupAsync()
    {
        IsDeleteConfirmOpen = false;
        ErrorMessage = null;
        try
        {
            await _workspace.DeleteBotGroupAsync(_group);
            _navigator.Back();
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
    }

    public void InsertMention(string botName)
    {
        var token = $"@{botName} ";
        if (string.IsNullOrWhiteSpace(ComposerText))
        {
            ComposerText = token;
        }
        else if (ComposerText.EndsWith(' '))
        {
            ComposerText += token;
        }
        else
        {
            ComposerText += " " + token;
        }
    }

    private async Task SelectThreadAsync(string? threadId)
    {
        if (string.Equals(CurrentThreadId ?? "", threadId ?? "", StringComparison.Ordinal))
        {
            return;
        }
        CurrentThreadId = threadId;
        RebuildThreadTabs();
        await RefreshAsync();
    }

    private async Task SetReplyModeAsync(string kind, string? botId)
    {
        ErrorMessage = null;
        try
        {
            await _workspace.UpdateBotGroupAsync(_group, new UpdateRemoteBotGroupRequest(
                _group.Id,
                _group.Revision,
                ReplyModeKind: kind,
                ReplyModeBotId: botId));
            SyncHeaderAndModes();
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
    }

    private async Task ContinueHandoffAsync(string messageId)
    {
        ErrorMessage = null;
        try
        {
            await _workspace.ContinueBotGroupHandoffAsync(_group, messageId, extraHops: 3);
            await RefreshAsync();
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
    }

    private async Task RespondApprovalAsync(string approvalId, string? optionId)
    {
        ErrorMessage = null;
        try
        {
            await _workspace.RespondBotGroupApprovalAsync(_group, approvalId, optionId);
            await RefreshAsync();
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
    }

    private void ApplySnapshot(RemoteBotGroupRoomSnapshot snapshot, bool prepend)
    {
        HasEarlier = snapshot.HasEarlier;
        if (!prepend)
        {
            Entries.Clear();
            foreach (var entry in snapshot.Messages)
            {
                Entries.Add(new BotGroupEntryItemViewModel(entry, InsertMention, ContinueHandoffAsync));
            }
        }
        else
        {
            var existingIds = Entries.Select(e => e.Entry.Id).ToHashSet(StringComparer.Ordinal);
            var index = 0;
            foreach (var entry in snapshot.Messages)
            {
                if (existingIds.Add(entry.Id))
                {
                    Entries.Insert(index++, new BotGroupEntryItemViewModel(entry, InsertMention, ContinueHandoffAsync));
                }
            }
        }

        _earliestSequence = Entries.Count > 0 ? Entries[0].Entry.Sequence : null;
        HasEntries = Entries.Count > 0;

        PendingApprovals.Clear();
        foreach (var approval in snapshot.Approvals)
        {
            PendingApprovals.Add(new BotGroupApprovalCardViewModel(approval, RespondApprovalAsync));
        }
        HasPendingApprovals = PendingApprovals.Count > 0;
        PendingApprovalsHeader = MobileLangResources.Format(MobileLangResourceKind.BotGroupsApprovalsHeader, PendingApprovals.Count);

        CanStop = _group.IsWorking || Entries.Any(e => e.Entry.Turns.Any(t => t.IsActive));
        SyncHeaderAndModes();
        RebuildThreadTabs();
    }

    private void SyncHeaderAndModes()
    {
        GroupName = _group.Name;
        GroupSubtitle = MobileLangResources.Format(MobileLangResourceKind.BotGroupsMembersCount, _group.Host.Name, _group.Members.Count);
        ReplyModeKind = _group.ReplyModeKind;
        ReplyModeBotId = _group.ReplyModeBotId;
        OnPropertyChanged(nameof(DeleteConfirmTitle));

        TargetBotChoices.Clear();
        var effectiveBotId = string.IsNullOrWhiteSpace(_group.ReplyModeBotId)
            ? _group.Members.FirstOrDefault()?.BotId
            : _group.ReplyModeBotId;
        foreach (var member in _group.Members)
        {
            var isSelected = string.Equals(member.BotId, effectiveBotId, StringComparison.Ordinal);
            TargetBotChoices.Add(new BotGroupTargetChipViewModel(
                member.BotId,
                member.BotName,
                member.BotColor,
                isSelected,
                botId => SetReplyModeAsync("bot", botId)));
        }
        OnPropertyChanged(nameof(ShowTargetBotBar));
    }

    private void RebuildThreadTabs()
    {
        ThreadTabs.Clear();
        ThreadTabs.Add(new BotGroupThreadTabViewModel(
            null,
            MobileLangResources.Get(MobileLangResourceKind.BotGroupsMainRoom),
            string.IsNullOrWhiteSpace(CurrentThreadId),
            SelectThreadAsync));
        foreach (var thread in _group.Threads)
        {
            var title = string.IsNullOrWhiteSpace(thread.Title)
                ? MobileLangResources.Get(MobileLangResourceKind.BotGroupsUntitledThread)
                : thread.Title!;
            ThreadTabs.Add(new BotGroupThreadTabViewModel(
                thread.Id,
                title,
                string.Equals(CurrentThreadId, thread.Id, StringComparison.Ordinal),
                SelectThreadAsync));
        }
    }

    private void RebuildMemberChoices()
    {
        MemberChoices.Clear();
        var activeIds = _group.Members.Select(m => m.BotId).ToHashSet(StringComparer.Ordinal);
        var added = new HashSet<string>(StringComparer.Ordinal);

        foreach (var bot in _group.Host.Bots)
        {
            added.Add(bot.Id);
            MemberChoices.Add(new BotGroupMemberToggleViewModel(
                bot.Id,
                bot.Name,
                bot.Color,
                bot.AgentId,
                activeIds.Contains(bot.Id)));
        }

        foreach (var member in _group.Members)
        {
            if (added.Add(member.BotId))
            {
                MemberChoices.Add(new BotGroupMemberToggleViewModel(
                    member.BotId,
                    member.BotName,
                    member.BotColor,
                    member.AgentId,
                    isSelected: true));
            }
        }
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        SyncHeaderAndModes();
        if (e.PropertyName is nameof(RemoteBotGroup.IsWorking) or nameof(RemoteBotGroup.PendingApprovalCount))
        {
            CanStop = _group.IsWorking || Entries.Any(item => item.Entry.Turns.Any(t => t.IsActive));
        }
    }

    private void OnGroupStructureChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncHeaderAndModes();
        RebuildThreadTabs();
    }

    public void Dispose()
    {
        _disposed = true;
        _group.PropertyChanged -= OnGroupChanged;
        _group.Members.CollectionChanged -= OnGroupStructureChanged;
        _group.Threads.CollectionChanged -= OnGroupStructureChanged;
    }
}
