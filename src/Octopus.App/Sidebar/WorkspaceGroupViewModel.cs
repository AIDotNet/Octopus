using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Core.Projects;

namespace Octopus.App.Sidebar;

/// <summary>
/// One workspace group in the sidebar tree (Scheme C): either the project's default folder (<see cref="IsDefault"/>) or
/// a git worktree created under <c>~/.octopus/workspaces/&lt;project&gt;/&lt;branch&gt;</c>.
/// </summary>
public sealed partial class WorkspaceGroupViewModel : ObservableObject
{
    private readonly Action<WorkspaceGroupViewModel, bool> _onCollapsedChanged;
    private readonly Action<WorkspaceGroupViewModel> _reveal;
    private readonly Action<WorkspaceGroupViewModel>? _openTask;
    private readonly Action<WorkspaceGroupViewModel, bool>? _togglePin;
    private readonly Func<WorkspaceGroupViewModel, Task>? _remove;

    public WorkspaceGroupViewModel(
        ProjectWorkspace? workspace,
        string displayName,
        string fullPath,
        string shortPath,
        bool isExpanded,
        IEnumerable<ItemRowViewModel> items,
        Action<WorkspaceGroupViewModel, bool> onCollapsedChanged,
        Action<WorkspaceGroupViewModel> reveal,
        Action<WorkspaceGroupViewModel>? openTask = null,
        Func<WorkspaceGroupViewModel, Task>? remove = null,
        Action<WorkspaceGroupViewModel, bool>? togglePin = null)
    {
        Workspace = workspace;
        IsDefault = workspace is null;
        WorkspaceId = workspace?.Id ?? IItemOpener.DefaultWorkspaceId;
        DisplayName = displayName;
        FullPath = fullPath;
        ShortPath = shortPath;
        TaskKey = workspace?.Task?.Key;
        TaskTitle = workspace?.Task?.Title;
        TaskUrl = workspace?.Task?.Url;
        IsPinned = workspace?.IsPinned == true;
        IsExpanded = isExpanded;
        _onCollapsedChanged = onCollapsedChanged;
        _reveal = reveal;
        _openTask = openTask;
        _togglePin = togglePin;
        _remove = remove;
        foreach (var item in items)
        {
            Items.Add(item);
        }
        HasActiveItem = Items.Any(i => i.IsActiveOrHasActiveChild);
    }

    public ProjectWorkspace? Workspace { get; }
    public bool IsDefault { get; }
    public bool IsWorktree => !IsDefault;
    /// <summary>Value passed to <see cref="Launcher.LauncherRequest.Raise"/>: <c>""</c> for default, or the worktree id.</summary>
    public string WorkspaceId { get; }
    public string DisplayName { get; }
    public string FullPath { get; }
    public string ShortPath { get; }
    public string? TaskKey { get; }
    public string? TaskTitle { get; }
    public string? TaskUrl { get; }
    public bool HasTask => !string.IsNullOrEmpty(TaskKey);
    public bool HasTaskUrl => !string.IsNullOrWhiteSpace(TaskUrl);
    public bool IsPinned { get; }
    public bool CanPin => IsWorktree && _togglePin is not null;
    public bool CanRemove => IsWorktree && _remove is not null;

    public string HeaderTip => HasTask && !string.IsNullOrWhiteSpace(TaskTitle)
        ? $"{DisplayName} ({TaskKey}: {TaskTitle})\n{ShortPath}"
        : $"{DisplayName}\n{ShortPath}";

    public string AddItemTip => AppLangResources.Format(AppLangResourceKind.WorkspaceNewInTip, DisplayName);
    public string OpenTaskLabel => AppLangResources.Format(AppLangResourceKind.WorkspaceOpenLinkedTask, TaskKey ?? "");
    public string PinLabel => AppLangResources.Get(IsPinned ? AppLangResourceKind.WorkspaceUnpin : AppLangResourceKind.WorkspacePin);

    public ObservableCollection<ItemRowViewModel> Items { get; } = [];
    public bool IsEmpty => Items.Count == 0;

    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial bool HasActiveItem { get; set; }

    public void RefreshActive() => HasActiveItem = Items.Any(i => i.IsActiveOrHasActiveChild);

    [RelayCommand]
    private void Toggle()
    {
        IsExpanded = !IsExpanded;
        _onCollapsedChanged(this, !IsExpanded);
    }

    [RelayCommand]
    private void Reveal() => _reveal(this);

    [RelayCommand]
    private void OpenTask() => _openTask?.Invoke(this);

    [RelayCommand]
    private void TogglePin() => _togglePin?.Invoke(this, !IsPinned);

    [RelayCommand]
    private Task RemoveAsync() => _remove is null ? Task.CompletedTask : _remove(this);
}
