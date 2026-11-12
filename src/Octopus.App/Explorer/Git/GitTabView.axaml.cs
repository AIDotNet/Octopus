using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Octopus.App.Localization;
using TKCode.Arc;

namespace Octopus.App.Explorer.Git;

/// <summary>
/// The source control tab: branch menu, sync buttons, commit box, grouped changes and recent commits. A click on a
/// change opens its diff, a double-click the file; a click on a commit opens it to its files. The platform's command
/// key with Enter in the message commits.
/// </summary>
public partial class GitTabView : UserControl
{
    private GitPanelViewModel? _model;

    public GitTabView()
    {
        InitializeComponent();
        MessageBox.Placeholder = AppLangResources.Get(OperatingSystem.IsMacOS()
            ? AppLangResourceKind.ExplorerCommitMessage
            : AppLangResourceKind.ExplorerCommitMessageOther);
        MessageBox.Label = MessageBox.Placeholder;
        MessageBox.AddHandler(KeyDownEvent, OnMessageKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as GitPanelViewModel;
        if (_model is null) return;
        _model.PropertyChanged += OnModelChanged;
        BuildBranchMenu();
        BuildMoreMenu();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GitPanelViewModel.Branches):
                BuildBranchMenu();
                break;
            case nameof(GitPanelViewModel.IsAmend) or nameof(GitPanelViewModel.StashCount):
                BuildMoreMenu();
                break;
        }
    }

    /// <summary>The local branches to switch to (the current one checked), the remote ones in a submenu, then create and delete.</summary>
    private void BuildBranchMenu()
    {
        if (_model is not { } model) return;
        var menu = new MenuSurface();
        foreach (var branch in model.LocalBranches)
        {
            menu.Items.Add(new MenuEntry
            {
                Header = branch.Name,
                ToggleType = MenuEntryToggleType.Radio,
                IsChecked = branch.IsCurrent,
                Command = model.SwitchBranchCommand,
                CommandParameter = branch,
            });
        }
        var remotes = model.RemoteBranches.ToList();
        if (remotes.Count > 0)
        {
            var remote = new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.ExplorerRemoteBranches), HasSeparatorBefore = menu.Items.Count > 0 };
            foreach (var branch in remotes)
            {
                remote.Items.Add(new MenuEntry { Header = branch.Name, Command = model.SwitchBranchCommand, CommandParameter = branch });
            }
            menu.Items.Add(remote);
        }
        menu.Items.Add(new MenuEntry
        {
            Header = AppLangResources.Get(AppLangResourceKind.ExplorerNewBranch),
            Command = model.NewBranchCommand,
            HasSeparatorBefore = menu.Items.Count > 0,
        });
        var deletable = model.LocalBranches.Where(b => !b.IsCurrent).ToList();
        if (deletable.Count > 0)
        {
            var delete = new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.ExplorerDeleteBranch) };
            foreach (var branch in deletable)
            {
                delete.Items.Add(new MenuEntry { Header = branch.Name, IsDestructive = true, Command = model.DeleteBranchCommand, CommandParameter = branch });
            }
            menu.Items.Add(delete);
        }
        DropdownMenu.SetMenu(BranchButton, menu);
    }

    private void BuildMoreMenu()
    {
        if (_model is not { } model) return;
        var menu = new MenuSurface();
        menu.Items.Add(new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.ExplorerFetch), Command = model.FetchCommand });
        menu.Items.Add(new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.ExplorerPull), Command = model.PullCommand });
        menu.Items.Add(new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.ExplorerPush), Command = model.PushCommand });
        menu.Items.Add(new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.ExplorerStash), Command = model.StashCommand, HasSeparatorBefore = true });
        menu.Items.Add(new MenuEntry
        {
            Header = AppLangResources.Get(AppLangResourceKind.ExplorerStashPop) + (model.StashCount > 0 ? $" ({model.StashCount})" : ""),
            Command = model.StashPopCommand,
        });
        menu.Items.Add(new MenuEntry
        {
            Header = AppLangResources.Get(AppLangResourceKind.ExplorerAmend),
            ToggleType = MenuEntryToggleType.CheckBox,
            IsChecked = model.IsAmend,
            StaysOpenOnClick = false,
            Command = model.ToggleAmendCommand,
            HasSeparatorBefore = true,
        });
        DropdownMenu.SetMenu(MoreButton, menu);
    }

    private void OnMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not GitPanelViewModel model) return;
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if ((e.KeyModifiers & command) == 0) return;
        e.Handled = true;
        if (model.CommitCommand.CanExecute(null)) model.CommitCommand.Execute(null);
    }

    private void OnChangePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is GitPanelViewModel model && (sender as Control)?.DataContext is GitChangeRowViewModel row)
        {
            model.SelectedChange = row;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) model.RequestDiff(row);
        }
    }

    private void OnChangeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is GitPanelViewModel model && (sender as Control)?.DataContext is GitChangeRowViewModel row)
        {
            model.RequestOpen(row);
        }
    }

    private void OnCommitPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is GitPanelViewModel model && (sender as Control)?.DataContext is GitCommitRowViewModel row
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _ = model.ToggleCommit(row);
        }
    }

    private void OnCommitFilePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is GitPanelViewModel model && (sender as Control)?.DataContext is GitCommitFileRowViewModel row
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            model.RequestCommitFileDiff(row);
        }
    }
}
