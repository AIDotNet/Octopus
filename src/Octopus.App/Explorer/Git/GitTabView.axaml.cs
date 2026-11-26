using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Octopus.App.Localization;
using TKCode.Arc;

namespace Octopus.App.Explorer.Git;

/// <summary>
/// The source control tab: branch menu, sync buttons, commit box, collapsible working changes and scroll-loaded
/// recent commits, plus an on-demand collapsible branch graph. A click on a change opens its diff, a double-click the
/// file; a click on a commit opens it to its files. The platform's command key with Enter in the message commits.
/// </summary>
public partial class GitTabView : UserControl
{
    private const double ScrollLoadThresholdPx = 120;
    private const double DefaultBranchGraphMaxHeight = 340;

    private GitPanelViewModel? _model;
    private ScrollViewer? _scrollViewer;
    private BranchGraph? _branchGraph;
    private bool _subscribed;

    public GitTabView()
    {
        InitializeComponent();
        MessageBox.Placeholder = AppLangResources.Get(OperatingSystem.IsMacOS()
            ? AppLangResourceKind.ExplorerCommitMessage
            : AppLangResourceKind.ExplorerCommitMessageOther);
        MessageBox.Label = MessageBox.Placeholder;
        MessageBox.AddHandler(KeyDownEvent, OnMessageKeyDown, RoutingStrategies.Tunnel);
        List.TemplateApplied += OnListTemplateApplied;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_model is not null && !_subscribed)
        {
            _model.PropertyChanged += OnModelChanged;
            _subscribed = true;
            BuildBranchMenu();
            BuildMoreMenu();
            UpdateSectionLayout();
            SyncBranchGraphView();
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_model is not null && _subscribed)
        {
            _model.PropertyChanged -= OnModelChanged;
            _subscribed = false;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null && _subscribed)
        {
            _model.PropertyChanged -= OnModelChanged;
            _subscribed = false;
        }
        _model = DataContext as GitPanelViewModel;
        if (_model is null)
        {
            DetachBranchGraph();
            return;
        }
        _model.PropertyChanged += OnModelChanged;
        _subscribed = true;
        BuildBranchMenu();
        BuildMoreMenu();
        UpdateSectionLayout();
        SyncBranchGraphView();
    }

    private void OnListTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        _scrollViewer?.ScrollChanged -= OnListScrollChanged;
        _scrollViewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
        _scrollViewer?.ScrollChanged += OnListScrollChanged;
    }

    private void OnListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scrollViewer is not { } sv || _model is not { } model) return;
        if (!model.IsCommitsExpanded || !model.HasMoreCommits || model.IsLoadingMoreCommits || model.IsRefreshing) return;
        if (sv.Extent.Height <= sv.Viewport.Height + 1) return;
        var remaining = sv.Extent.Height - sv.Viewport.Height - sv.Offset.Y;
        if (remaining > ScrollLoadThresholdPx) return;
        _ = LoadMorePreservingScrollAsync(model, sv);
    }

    private static async Task LoadMorePreservingScrollAsync(GitPanelViewModel model, ScrollViewer sv)
    {
        var saved = sv.Offset;
        await model.LoadMoreCommitsAsync();
        if (Math.Abs(sv.Offset.Y - saved.Y) > 1)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                sv.Offset = new Vector(saved.X, Math.Min(saved.Y, maxY));
            }, DispatcherPriority.Loaded);
        }
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
            case nameof(GitPanelViewModel.IsChangesExpanded) or nameof(GitPanelViewModel.IsCommitsExpanded):
                UpdateSectionLayout();
                break;
            case nameof(GitPanelViewModel.IsBranchGraphExpanded)
                or nameof(GitPanelViewModel.BranchGraphCommits)
                or nameof(GitPanelViewModel.BranchGraphTitle):
                UpdateSectionLayout();
                SyncBranchGraphView();
                break;
        }
    }

    private void UpdateSectionLayout()
    {
        if (_model is not { } model || SectionsGrid.RowDefinitions.Count < 2) return;
        var upperExpanded = model.IsChangesExpanded || model.IsCommitsExpanded;
        if (!upperExpanded && model.IsBranchGraphExpanded)
        {
            SectionsGrid.RowDefinitions[0].Height = GridLength.Auto;
            SectionsGrid.RowDefinitions[1].Height = GridLength.Star;
            BranchGraphHost.MaxHeight = double.PositiveInfinity;
        }
        else if (!upperExpanded)
        {
            SectionsGrid.RowDefinitions[0].Height = GridLength.Auto;
            SectionsGrid.RowDefinitions[1].Height = GridLength.Auto;
            BranchGraphHost.MaxHeight = DefaultBranchGraphMaxHeight;
        }
        else
        {
            SectionsGrid.RowDefinitions[0].Height = GridLength.Star;
            SectionsGrid.RowDefinitions[1].Height = GridLength.Auto;
            BranchGraphHost.MaxHeight = DefaultBranchGraphMaxHeight;
        }
    }

    private void SyncBranchGraphView()
    {
        if (_model is not { ShowBranchGraphControl: true } model)
        {
            DetachBranchGraph();
            return;
        }

        if (_branchGraph is null)
        {
            _branchGraph = new BranchGraph
            {
                AutoPlay = false,
                StartsFinished = true,
                AreControlsVisible = false,
                Labels = model.BranchGraphLabels,
                Title = model.BranchGraphTitle,
                ItemsSource = model.BranchGraphCommits,
            };
            BranchGraphSlot.Child = _branchGraph;
        }
        else
        {
            _branchGraph.Title = model.BranchGraphTitle;
            _branchGraph.ItemsSource = model.BranchGraphCommits;
        }
    }

    private void DetachBranchGraph()
    {
        if (_branchGraph is null) return;
        BranchGraphSlot.Child = null;
        _branchGraph = null;
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
            var remote = new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.ExplorerRemoteBranches), Icon = ArcIcons.Cloud, HasSeparatorBefore = menu.Items.Count > 0 };
            foreach (var branch in remotes)
            {
                remote.Items.Add(new MenuEntry { Header = branch.Name, Icon = ArcIcons.GitBranch, Command = model.SwitchBranchCommand, CommandParameter = branch });
            }
            menu.Items.Add(remote);
        }
        menu.Items.Add(new MenuEntry
        {
            Header = AppLangResources.Get(AppLangResourceKind.ExplorerNewBranch),
            Icon = ArcIcons.Plus,
            Command = model.NewBranchCommand,
            HasSeparatorBefore = menu.Items.Count > 0,
        });
        var deletable = model.LocalBranches.Where(b => !b.IsCurrent).ToList();
        if (deletable.Count > 0)
        {
            var delete = new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.ExplorerDeleteBranch), Icon = ArcIcons.Trash2 };
            foreach (var branch in deletable)
            {
                delete.Items.Add(new MenuEntry { Header = branch.Name, Icon = ArcIcons.GitBranch, IsDestructive = true, Command = model.DeleteBranchCommand, CommandParameter = branch });
            }
            menu.Items.Add(delete);
        }
        DropdownMenu.SetMenu(BranchButton, menu);
    }

    private void BuildMoreMenu()
    {
        if (_model is not { } model) return;
        var menu = new MenuSurface();
        menu.Items.Add(new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.ExplorerFetch), Icon = ArcIcons.RefreshCw, Command = model.FetchCommand });
        menu.Items.Add(new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.ExplorerPull), Icon = ArcIcons.Download, Command = model.PullCommand });
        menu.Items.Add(new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.ExplorerPush), Icon = ArcIcons.Upload, Command = model.PushCommand });
        menu.Items.Add(new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.ExplorerStash), Icon = ArcIcons.Archive, Command = model.StashCommand, HasSeparatorBefore = true });
        menu.Items.Add(new MenuEntry
        {
            Header = AppLangResources.Get(AppLangResourceKind.ExplorerStashPop) + (model.StashCount > 0 ? $" ({model.StashCount})" : ""),
            Icon = ArcIcons.ArrowUpFromLine,
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
        if (DataContext is GitPanelViewModel model && sender is Control { DataContext: GitChangeRowViewModel row })
        {
            model.SelectedChange = row;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) model.RequestDiff(row);
        }
    }

    private void OnChangeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is GitPanelViewModel model && sender is Control { DataContext: GitChangeRowViewModel row })
        {
            model.RequestOpen(row);
        }
    }

    private void OnCommitPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is GitPanelViewModel model && sender is Control { DataContext: GitCommitRowViewModel row }
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _ = model.ToggleCommit(row);
        }
    }

    private void OnCommitFilePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is GitPanelViewModel model && sender is Control { DataContext: GitCommitFileRowViewModel row }
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            model.RequestCommitFileDiff(row);
        }
    }
}
