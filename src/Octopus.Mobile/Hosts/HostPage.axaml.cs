using System.Collections.Specialized;
using Avalonia;
using Octopus.Mobile.Common;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Hosts;

/// <summary>One computer's projects and sessions (design/ui/移动端布局方案.html, A ②), with the "New" sheet over it.</summary>
public partial class HostPage : Page, IClosablePage
{
    private readonly HostViewModel _viewModel;

    public HostPage(HostViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        // ActionSheets take their rows as controls, not from an ItemsSource: rebuilt when options change.
        viewModel.NewSession.Models.CollectionChanged += OnModelsChanged;
        viewModel.NewSession.BaseBranches.CollectionChanged += OnBaseBranchesChanged;
        viewModel.NewSession.AvailableExistingBranches.CollectionChanged += OnExistingBranchesChanged;
        viewModel.NewSession.CurrentTaskOptions.CollectionChanged += OnTaskOptionsChanged;
        viewModel.GroupEditor.MoveSheetChanged += FillMoveGroupActions;
        viewModel.Host.ProjectGroups.CollectionChanged += OnProjectGroupsChanged;
        // Holding a session row opens its menu (design/ui/移动端功能扩展方案.html, M-C ①).
        _ = new LongPress(this, OnLongPress);
        FillModels();
        FillBaseBranches();
        FillExistingBranches();
        FillTaskOptions();
        FillMoveGroupActions();
    }

    public void OnClosed()
    {
        _viewModel.NewSession.Models.CollectionChanged -= OnModelsChanged;
        _viewModel.NewSession.BaseBranches.CollectionChanged -= OnBaseBranchesChanged;
        _viewModel.NewSession.AvailableExistingBranches.CollectionChanged -= OnExistingBranchesChanged;
        _viewModel.NewSession.CurrentTaskOptions.CollectionChanged -= OnTaskOptionsChanged;
        _viewModel.GroupEditor.MoveSheetChanged -= FillMoveGroupActions;
        _viewModel.Host.ProjectGroups.CollectionChanged -= OnProjectGroupsChanged;
        _viewModel.Dispose();
    }

    private bool OnLongPress(StyledElement pressed)
    {
        for (var element = pressed; element is not null; element = element.Parent)
        {
            if (element.DataContext is not SessionRowViewModel row) continue;
            row.Held();
            _viewModel.ShowMenu(row.Session);
            return true;
        }
        return false;
    }

    private void OnModelsChanged(object? sender, NotifyCollectionChangedEventArgs e) => FillModels();
    private void OnBaseBranchesChanged(object? sender, NotifyCollectionChangedEventArgs e) => FillBaseBranches();
    private void OnExistingBranchesChanged(object? sender, NotifyCollectionChangedEventArgs e) => FillExistingBranches();
    private void OnTaskOptionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => FillTaskOptions();
    private void OnProjectGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => FillMoveGroupActions();

    private void FillMoveGroupActions()
    {
        MoveProjectGroupSheet.Actions.Clear();
        var editor = _viewModel.GroupEditor;
        var target = editor.MoveProjectTarget;
        if (target is null) return;

        foreach (var group in _viewModel.Host.ProjectGroups)
        {
            var isCurrent = string.Equals(target.GroupId, group.Id, StringComparison.Ordinal);
            MoveProjectGroupSheet.Actions.Add(new ActionSheetItem
            {
                Content = isCurrent ? $"✓ {group.Name}" : group.Name,
                Command = editor.MoveProjectToGroupCommand,
                CommandParameter = group.Id,
            });
        }

        if (!string.IsNullOrWhiteSpace(target.GroupId))
        {
            MoveProjectGroupSheet.Actions.Add(new ActionSheetItem
            {
                Content = MobileLangResources.Get(MobileLangResourceKind.ProjectGroupMoveRemove),
                Command = editor.MoveProjectToGroupCommand,
                CommandParameter = null,
            });
        }

        MoveProjectGroupSheet.Actions.Add(new ActionSheetItem
        {
            Content = MobileLangResources.Get(MobileLangResourceKind.ProjectGroupMoveNew),
            Command = editor.MoveProjectToNewGroupCommand,
        });
    }

    private void FillModels()
    {
        var choice = _viewModel.NewSession;
        ModelSheet.Actions.Clear();
        ModelSheet.Actions.Add(new ActionSheetItem
        {
            Content = MobileLangResources.Get(MobileLangResourceKind.NewSessionModelDefault),
            Command = choice.SelectModelCommand,
            CommandParameter = null,
        });
        foreach (var model in choice.Models)
        {
            ModelSheet.Actions.Add(new ActionSheetItem { Content = model, Command = choice.SelectModelCommand, CommandParameter = model });
        }
    }

    private void FillBaseBranches()
    {
        var choice = _viewModel.NewSession;
        BaseBranchSheet.Actions.Clear();
        foreach (var branch in choice.BaseBranches)
        {
            BaseBranchSheet.Actions.Add(new ActionSheetItem
            {
                Content = branch.Name,
                Command = choice.SelectBaseBranchCommand,
                CommandParameter = branch.Name,
            });
        }
    }

    private void FillExistingBranches()
    {
        var choice = _viewModel.NewSession;
        ExistingBranchSheet.Actions.Clear();
        foreach (var branch in choice.AvailableExistingBranches)
        {
            ExistingBranchSheet.Actions.Add(new ActionSheetItem
            {
                Content = branch.Name,
                Command = choice.SelectExistingBranchCommand,
                CommandParameter = branch,
            });
        }
    }

    private void FillTaskOptions()
    {
        var choice = _viewModel.NewSession;
        TaskSheet.Actions.Clear();
        foreach (var task in choice.CurrentTaskOptions)
        {
            TaskSheet.Actions.Add(new ActionSheetItem
            {
                Content = $"{task.Key} · {task.Title}",
                Command = choice.SelectTaskCommand,
                CommandParameter = task,
            });
        }
    }
}
