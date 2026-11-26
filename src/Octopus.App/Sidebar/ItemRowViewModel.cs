using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Core.Projects;

namespace Octopus.App.Sidebar;

/// <summary>One row of the project panel: a terminal, an agent or a file.</summary>
public sealed partial class ItemRowViewModel : ObservableObject
{
    private readonly Action<ItemRowViewModel> _activate;
    private readonly Action<ItemRowViewModel, string> _rename;
    private readonly Func<ItemRowViewModel, Task> _restart;
    private readonly Action<ItemRowViewModel> _moveToNewGroup;
    private readonly Func<ItemRowViewModel, Task> _close;
    private readonly Action<string, bool>? _onChildrenExpandedChanged;

    public ItemRowViewModel(
        ProjectItem item,
        string? modelName,
        bool isActive,
        Action<ItemRowViewModel> activate,
        Action<ItemRowViewModel, string> rename,
        Func<ItemRowViewModel, Task> restart,
        Action<ItemRowViewModel> moveToNewGroup,
        Func<ItemRowViewModel, Task> close,
        Action<string, bool>? onChildrenExpandedChanged = null)
    {
        Item = item;
        ModelName = modelName;
        IsActive = isActive;
        _activate = activate;
        _rename = rename;
        _restart = restart;
        _moveToNewGroup = moveToNewGroup;
        _close = close;
        _onChildrenExpandedChanged = onChildrenExpandedChanged;
    }

    public ProjectItem Item { get; }
    public string Id => Item.Id;
    public string Title => Item.DisplayTitle;
    public string? AgentId => Item.AgentId;
    public bool IsAgent => Item.Kind == ProjectItemKind.Agent;
    public bool IsTerminal => Item.Kind == ProjectItemKind.Terminal;
    public bool IsFile => Item.Kind == ProjectItemKind.File;
    public bool IsPlugin => Item.Kind == ProjectItemKind.Plugin;
    public bool CanRestart => Item.Kind != ProjectItemKind.File;
    public string? ModelName { get; }
    public bool HasModel => !string.IsNullOrEmpty(ModelName);

    [ObservableProperty] public partial bool IsActive { get; set; }
    [ObservableProperty] public partial bool IsRenaming { get; set; }
    /// <summary>The agent is working on a turn: the row shows a spinner.</summary>
    [ObservableProperty] public partial bool IsWorking { get; set; }
    /// <summary>The agent stopped to ask the user (a question, a permission request): a yellow dot.</summary>
    [ObservableProperty] public partial bool IsWaiting { get; set; }
    /// <summary>The agent's turn failed: a red dot.</summary>
    [ObservableProperty] public partial bool HasError { get; set; }
    /// <summary>A turn finished while the user was elsewhere: a green dot until the tab is viewed.</summary>
    [ObservableProperty] public partial bool IsDone { get; set; }
    public bool NeedsAttention => IsWaiting || HasError;
    /// <summary>The subagents the session started (Claude Code's Agent tool); null for an item that never had one.</summary>
    [ObservableProperty] public partial SubagentListViewModel? Subagents { get; set; }

    /// <summary>Agent sessions directly spawned by this item via Octopus multi-agent collaboration (Scheme C compact sub-rows).</summary>
    public ObservableCollection<ItemRowViewModel> SpawnedChildren { get; } = [];
    public bool HasSpawnedChildren => SpawnedChildren.Count > 0;
    public int SpawnedChildrenCount => SpawnedChildren.Count;
    public bool IsSpawnedChild => !string.IsNullOrEmpty(Item.ParentItemId);
    public string? IsolationBadge => Item.CollabIsolation switch
    {
        "worktree" => "wt",
        "read_only" => "ro",
        _ => null,
    };
    public bool HasIsolationBadge => !string.IsNullOrEmpty(IsolationBadge) && !HasHostBadge;

    /// <summary>The other computer this session runs on, for a delegate nested under a session of this computer (<c>@Build Server</c>).</summary>
    public string? HostBadge { get; set; }
    public bool HasHostBadge => !string.IsNullOrEmpty(HostBadge);
    public string? HostBadgeTip { get; set; }

    /// <summary>Where the work came from when another session (often on another computer) delegated it (<c>来自 MacBook</c>).</summary>
    public string? OriginText { get; set; }
    public string? OriginTip { get; set; }
    public bool HasOrigin => !string.IsNullOrEmpty(OriginText);
    public bool HasWaitingChild => SpawnedChildren.Any(c => c.IsWaiting || c.HasWaitingChild);
    public bool ShowSpawnedChildren => HasSpawnedChildren && IsChildrenExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSpawnedChildren))]
    public partial bool IsChildrenExpanded { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPeerThreads))]
    public partial int ActivePeerThreadCount { get; set; }
    public bool HasPeerThreads => ActivePeerThreadCount > 0;

    public bool IsActiveOrHasActiveChild => IsActive || SpawnedChildren.Any(c => c.IsActiveOrHasActiveChild);

    public void AddSpawnedChild(ItemRowViewModel child)
    {
        SpawnedChildren.Add(child);
        NotifyChildrenChanged();
    }

    public void NotifyChildrenChanged()
    {
        OnPropertyChanged(nameof(HasSpawnedChildren));
        OnPropertyChanged(nameof(SpawnedChildrenCount));
        OnPropertyChanged(nameof(HasWaitingChild));
        OnPropertyChanged(nameof(ShowSpawnedChildren));
    }

    public void RefreshActiveRecursive(string? activeItemId)
    {
        IsActive = Id == activeItemId;
        foreach (var child in SpawnedChildren)
        {
            child.RefreshActiveRecursive(activeItemId);
        }
    }

    public ItemRowViewModel? FindRecursive(string itemId)
    {
        if (Id == itemId) return this;
        foreach (var child in SpawnedChildren)
        {
            if (child.FindRecursive(itemId) is { } found) return found;
        }
        return null;
    }

    public void RefreshTitle() => OnPropertyChanged(nameof(Title));

    public void Show(Activity.ItemActivity activity)
    {
        IsWorking = activity == Activity.ItemActivity.Working;
        IsWaiting = activity == Activity.ItemActivity.Waiting;
        HasError = activity == Activity.ItemActivity.Error;
        IsDone = activity == Activity.ItemActivity.Done;
        OnPropertyChanged(nameof(NeedsAttention));
    }

    [RelayCommand] private void Activate() => _activate(this);
    [RelayCommand] private void BeginRename() => IsRenaming = true;
    [RelayCommand] private Task RestartAsync() => _restart(this);
    [RelayCommand] private void MoveToNewGroup() => _moveToNewGroup(this);
    [RelayCommand] private Task CloseAsync() => _close(this);
    [RelayCommand]
    private void ToggleChildrenExpanded()
    {
        IsChildrenExpanded = !IsChildrenExpanded;
        _onChildrenExpandedChanged?.Invoke(Id, IsChildrenExpanded);
    }

    public void CommitRename(string title)
    {
        IsRenaming = false;
        _rename(this, title);
    }
}
