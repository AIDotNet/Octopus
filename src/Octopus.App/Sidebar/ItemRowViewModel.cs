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

    public ItemRowViewModel(
        ProjectItem item,
        string? modelName,
        bool isActive,
        Action<ItemRowViewModel> activate,
        Action<ItemRowViewModel, string> rename,
        Func<ItemRowViewModel, Task> restart,
        Action<ItemRowViewModel> moveToNewGroup,
        Func<ItemRowViewModel, Task> close)
    {
        Item = item;
        ModelName = modelName;
        IsActive = isActive;
        _activate = activate;
        _rename = rename;
        _restart = restart;
        _moveToNewGroup = moveToNewGroup;
        _close = close;
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
    public bool NeedsAttention => IsWaiting || HasError;
    /// <summary>The subagents the session started (Claude Code's Agent tool); null for an item that never had one.</summary>
    [ObservableProperty] public partial SubagentListViewModel? Subagents { get; set; }

    public void RefreshTitle() => OnPropertyChanged(nameof(Title));

    public void Show(Activity.ItemActivity activity)
    {
        IsWorking = activity == Activity.ItemActivity.Working;
        IsWaiting = activity == Activity.ItemActivity.Waiting;
        HasError = activity == Activity.ItemActivity.Error;
        OnPropertyChanged(nameof(NeedsAttention));
    }

    [RelayCommand] private void Activate() => _activate(this);
    [RelayCommand] private void BeginRename() => IsRenaming = true;
    [RelayCommand] private Task RestartAsync() => _restart(this);
    [RelayCommand] private void MoveToNewGroup() => _moveToNewGroup(this);
    [RelayCommand] private Task CloseAsync() => _close(this);

    public void CommitRename(string title)
    {
        IsRenaming = false;
        _rename(this, title);
    }
}
