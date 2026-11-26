using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Localization;
using Octopus.Core.Bots;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Bots.Groups;

/// <summary>One room row (Main Room or a <see cref="BotGroupThread"/>) inside an expanded Bot Group node in the left sidebar.</summary>
public sealed partial class BotGroupRoomRowViewModel : ObservableObject
{
    private readonly Action<BotGroupRoomRowViewModel> _open;

    public BotGroupRoomRowViewModel(
        string groupId,
        string? threadId,
        string title,
        bool isMainRoom,
        bool isSelected,
        int pendingApprovalCount,
        Action<BotGroupRoomRowViewModel> open)
    {
        GroupId = groupId;
        ThreadId = threadId;
        Title = title;
        IsMainRoom = isMainRoom;
        IsSelected = isSelected;
        PendingApprovalCount = pendingApprovalCount;
        Icon = isMainRoom ? LucideIconKind.MessagesSquare : LucideIconKind.Hash;
        _open = open;
    }

    public string GroupId { get; }
    public string? ThreadId { get; }
    public string Title { get; }
    public bool IsMainRoom { get; }
    public LucideIconKind Icon { get; }
    public int PendingApprovalCount { get; }
    public bool HasPendingApproval => PendingApprovalCount > 0;
    public string PendingBadgeText => PendingApprovalCount > 99 ? "99+" : PendingApprovalCount.ToString();

    [ObservableProperty] public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Open() => _open(this);
}

/// <summary>One Bot Group parent node in the left Bot sidebar, with an avatar cluster, badges, and room/thread children.</summary>
public sealed partial class BotGroupTreeNodeViewModel : ObservableObject
{
    private readonly Action<BotGroupTreeNodeViewModel> _toggle;
    private readonly Func<BotGroupTreeNodeViewModel, Task> _newThread;
    private readonly Func<BotGroupTreeNodeViewModel, Task> _manageMembers;
    private readonly Func<BotGroupTreeNodeViewModel, Task> _rename;
    private readonly Func<BotGroupTreeNodeViewModel, Task> _delete;

    public BotGroupTreeNodeViewModel(
        BotGroupDefinition group,
        IReadOnlyList<BotGroupAvatarTile> avatars,
        string previewText,
        bool isCurrent,
        bool isExpanded,
        int pendingApprovalCount,
        bool isWorking,
        IReadOnlyList<BotGroupRoomRowViewModel> rooms,
        Action<BotGroupTreeNodeViewModel> toggle,
        Func<BotGroupTreeNodeViewModel, Task> newThread,
        Func<BotGroupTreeNodeViewModel, Task> manageMembers,
        Func<BotGroupTreeNodeViewModel, Task> rename,
        Func<BotGroupTreeNodeViewModel, Task> delete)
    {
        GroupId = group.Id;
        Name = group.Name;
        PreviewText = previewText;
        HasPreview = !string.IsNullOrWhiteSpace(previewText);
        MemberCountText = AppLangResources.Format(AppLangResourceKind.BotGroupMemberCount, group.Members.Count);
        IsUnread = group.IsUnread;
        IsCurrent = isCurrent;
        IsExpanded = isExpanded;
        PendingApprovalCount = pendingApprovalCount;
        IsWorking = isWorking;
        Avatars = avatars;
        HasSingleAvatar = avatars.Count == 1;
        HasMultiAvatar = avatars.Count > 1;
        SingleAvatar = avatars.Count > 0 ? avatars[0] : null;
        foreach (var room in rooms)
        {
            Rooms.Add(room);
        }
        _toggle = toggle;
        _newThread = newThread;
        _manageMembers = manageMembers;
        _rename = rename;
        _delete = delete;
    }

    public string GroupId { get; }
    public string Name { get; }
    public string PreviewText { get; }
    public bool HasPreview { get; }
    public string MemberCountText { get; }
    public bool IsUnread { get; }
    public IReadOnlyList<BotGroupAvatarTile> Avatars { get; }
    public bool HasSingleAvatar { get; }
    public bool HasMultiAvatar { get; }
    public BotGroupAvatarTile? SingleAvatar { get; }
    public ObservableCollection<BotGroupRoomRowViewModel> Rooms { get; } = [];

    [ObservableProperty] public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPendingBadge), nameof(ShowWorkingSpinner), nameof(ShowUnreadDot))]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPendingBadge), nameof(ShowWorkingSpinner), nameof(ShowUnreadDot), nameof(PendingBadgeText))]
    public partial int PendingApprovalCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWorkingSpinner), nameof(ShowUnreadDot))]
    public partial bool IsWorking { get; set; }

    public bool ShowPendingBadge => PendingApprovalCount > 0;
    public bool ShowWorkingSpinner => !ShowPendingBadge && IsWorking;
    public bool ShowUnreadDot => IsUnread && !ShowPendingBadge && !ShowWorkingSpinner;
    public string PendingBadgeText => PendingApprovalCount > 99 ? "99+" : PendingApprovalCount.ToString();

    [RelayCommand]
    private void Toggle() => _toggle(this);

    [RelayCommand]
    private Task NewThreadAsync() => _newThread(this);

    [RelayCommand]
    private Task ManageMembersAsync() => _manageMembers(this);

    [RelayCommand]
    private Task RenameAsync() => _rename(this);

    [RelayCommand]
    private Task DeleteAsync() => _delete(this);
}
