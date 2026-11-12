using Avalonia.VisualTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Octopus.App.Launcher;
using Octopus.App.Settings;
using TKCode.Arc;

namespace Octopus.App.Shell;

public partial class ShellView : UserControl
{
    private ShellViewModel? _model;
    private SettingsView? _settings;
    private readonly PanelSlide _left;
    private readonly PanelSlide _right;
    /// <summary>What the columns are heading for (the model's state as the view last applied it).</summary>
    private bool _leftCollapsed;
    private bool _rightOpen;

    public ShellView()
    {
        InitializeComponent();
        _left = new PanelSlide(this, 0);
        _right = new PanelSlide(this, 0);
        _left.Frame += OnLeftFrame;
        _left.Arrived += OnLeftArrived;
        _right.Frame += OnRightFrame;
        _right.Arrived += OnRightArrived;
        AddHandler(LauncherRequest.RequestedEvent, OnLauncherRequested);
        // A "+" in a tab strip names its group for the next new tab; the choice ends with the menu.
        LauncherPopover.Closed += (_, _) => _model?.Workspace.SetNewTabGroup(null);
        CollapsedLead.SizeChanged += (_, _) => UpdateInsets();
        Toolbar.SizeChanged += (_, _) => UpdateInsets();
        TopRight.SizeChanged += (_, _) => UpdateInsets();
        RightPanel.FileOpenRequested += (_, path) => _model?.OpenFileFromPanel(path);
        RightPanel.DiffRequested += (_, request) => _model?.OpenDiffFromPanel(request);
        UpdateInsets();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.SettingsRequested -= OnSettingsRequested;
            _model.Launcher.CloseRequested -= OnLauncherCloseRequested;
            _model.PropertyChanged -= OnModelPropertyChanged;
        }
        _model = DataContext as ShellViewModel;
        if (_model is not null)
        {
            _model.SettingsRequested += OnSettingsRequested;
            _model.Launcher.CloseRequested += OnLauncherCloseRequested;
            _model.PropertyChanged += OnModelPropertyChanged;
            // The saved state is shown as it is, without a slide.
            _leftCollapsed = _model.IsSidebarCollapsed;
            _rightOpen = _model.IsRightPanelOpen;
            _left.Jump(_model.LeftColumnWidth.Value);
            _right.Jump(_model.RightColumnWidth.Value);
            ApplyProject();
        }
        UpdateInsets();
    }

    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsSidebarCollapsed) or nameof(ShellViewModel.LeftColumnWidth))
        {
            SyncLeft();
        }
        if (e.PropertyName is nameof(ShellViewModel.IsRightPanelOpen) or nameof(ShellViewModel.RightColumnWidth))
        {
            SyncRight();
        }
        if (e.PropertyName is nameof(ShellViewModel.CurrentProjectPath) or nameof(ShellViewModel.CurrentProjectName))
        {
            ApplyProject();
        }
    }

    // ---- Side panels: slide in and out -------------------------------------------------------------------------------
    //
    // The columns animate (PanelSlide). What must not animate with them is the content:
    //  - A side panel keeps the width it has when open and is clipped by its column (a drawer sliding in), so its tree or
    //    list is laid out once instead of on every frame.
    //  - The workspace keeps the width it has with the panel closed while the column beside it moves, and the clipping
    //    area cuts it off. The terminals inside get their final size once, when the slide has arrived: the terminal control
    //    resizes its PTY on every size change (there is no debounce upstream), and a shell would redraw 25 times a slide.

    /// <summary>The model's sidebar state changed (or its width): slide when the state flipped, set the width otherwise.</summary>
    private void SyncLeft()
    {
        var collapsed = _model!.IsSidebarCollapsed;
        var target = _model.LeftColumnWidth.Value;
        if (collapsed != _leftCollapsed)
        {
            _leftCollapsed = collapsed;
            BeginLeft();
            _left.Slide(target);
        }
        else if (_left.IsSliding)
        {
            _left.Slide(target);
        }
        else
        {
            // A dragged width, set directly: dragging the splitter replaced the column's width, which breaks a binding.
            _left.Jump(target);
        }
    }

    private void SyncRight()
    {
        var open = _model!.IsRightPanelOpen;
        var target = _model.RightColumnWidth.Value;
        if (open != _rightOpen)
        {
            _rightOpen = open;
            BeginRight();
            _right.Slide(target);
        }
        else if (_right.IsSliding)
        {
            _right.Slide(target);
        }
        else
        {
            _right.Jump(target);
        }
    }

    private void BeginLeft()
    {
        var wasSliding = _left.IsSliding;
        FreezeWorkspace(leftMoves: true, rightMoves: _right.IsSliding);
        if (wasSliding)
        {
            return;
        }
        // The area keeps its open width, pinned to the clip's right edge: the sidebar slides off to the left.
        LeftArea.Width = ShellViewModel.RailWidth + _model!.PanelWidth;
        LeftArea.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        LeftClip.IsVisible = true;
        LeftSplitter.IsVisible = false;
        // The lead of the first tab strip appears when the sidebar is gone, never while it is still on its way.
        CollapsedLead.Opacity = 0;
        CollapsedLead.IsHitTestVisible = false;
    }

    private void OnLeftFrame(double width)
    {
        Main.ColumnDefinitions[0].Width = new GridLength(width);
        UpdateLeftInset();
    }

    private void OnLeftArrived()
    {
        LeftArea.Width = double.NaN;
        LeftArea.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        var visible = !_leftCollapsed;
        LeftClip.IsVisible = visible;
        LeftSplitter.IsVisible = visible;
        CollapsedLead.Opacity = visible ? 0 : 1;
        CollapsedLead.IsHitTestVisible = !visible;
        ThawWorkspace();
        UpdateInsets();
    }

    private void BeginRight()
    {
        var wasSliding = _right.IsSliding;
        FreezeWorkspace(leftMoves: _left.IsSliding, rightMoves: true);
        if (wasSliding)
        {
            return;
        }
        // The panel keeps the open width, pinned to the clip's right edge: it slides in from the window's edge.
        RightPanel.Width = _model!.RightPanelWidth;
        RightPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        RightPanel.IsVisible = true;
        RightSplitter.IsVisible = false;
        var column = Main.ColumnDefinitions[4];
        column.MinWidth = 0;
        column.MaxWidth = double.PositiveInfinity;
        // The split button follows the panel's edge only once the slide has arrived.
        Toolbar.Opacity = 0;
        Toolbar.IsHitTestVisible = false;
    }

    private void OnRightFrame(double width) => Main.ColumnDefinitions[4].Width = new GridLength(width);

    private void OnRightArrived()
    {
        RightPanel.Width = double.NaN;
        RightPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        var open = _rightOpen;
        var column = Main.ColumnDefinitions[4];
        RightPanel.IsVisible = open;
        RightSplitter.IsVisible = open;
        // The limits apply only while it is open: a closed panel is zero wide.
        column.MinWidth = open ? ShellViewModel.MinRightPanelWidth : 0;
        column.MaxWidth = open ? ShellViewModel.MaxRightPanelWidth : double.PositiveInfinity;
        Toolbar.Opacity = 1;
        Toolbar.IsHitTestVisible = true;
        ThawWorkspace();
        UpdateInsets();
    }

    /// <summary>
    /// Pins the workspace to the width it has while the moving panels are out of the way, so it keeps that layout all
    /// slide long. Read from the columns as they are now: nothing of this change has been laid out yet.
    /// </summary>
    private void FreezeWorkspace(bool leftMoves, bool rightMoves)
    {
        var columns = Main.ColumnDefinitions;
        var left = leftMoves ? 0 : columns[0].ActualWidth + columns[1].ActualWidth;
        var right = rightMoves ? 0 : columns[3].ActualWidth + columns[4].ActualWidth;
        var width = Main.Bounds.Width - left - right;
        if (width <= 0)
        {
            return;
        }
        WorkspaceHost.Width = width;
        WorkspaceHost.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
    }

    private void ThawWorkspace()
    {
        if (_left.IsSliding || _right.IsSliding)
        {
            return;
        }
        WorkspaceHost.Width = double.NaN;
        WorkspaceHost.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
    }

    /// <summary>The first tab strip leaves room for the collapsed lead; while the sidebar slides, for what is not yet covered.</summary>
    private void UpdateLeftInset()
    {
        var lead = CollapsedLead.Bounds.Width;
        // The strip must not start inside the lead's room (the system's window buttons are there): when the workspace's
        // left edge is nearer to the window's edge than the lead is wide, the strip gives way by the difference, so the
        // tabs stand still while the last part of the slide runs.
        WorkspaceHost.LeftInset = _left.IsSliding
            ? Math.Max(0, lead - Main.ColumnDefinitions[0].Width.Value)
            : _leftCollapsed ? lead : 0;
    }

    /// <summary>The first tab strip leaves room for the collapsed lead, the last one for the toolbar.</summary>
    private void UpdateInsets()
    {
        UpdateLeftInset();
        if (!_right.IsSliding)
        {
            // Panel closed: the toolbar sits left of the corner switch. Panel open: the switch is over the panel's own top row.
            var open = _rightOpen;
            Toolbar.Margin = new Thickness(0, 0, open ? 6 : TopRight.Bounds.Width + 6, 0);
            WorkspaceHost.RightInset = Toolbar.IsVisible ? Toolbar.Bounds.Width + Toolbar.Margin.Right : 0;
        }
        // The panel's top row stops where the corner switch (with the caption buttons, where there are any) begins.
        RightPanel.TrailingInset = TopRight.Bounds.Width + 6;
    }

    /// <summary>
    /// The right panel follows the current project. Set from here rather than bound: the panel is its own data context.
    /// The name goes first, so the panel never shows the new folder under the old name.
    /// </summary>
    private void ApplyProject()
    {
        RightPanel.ProjectName = _model?.CurrentProjectName;
        RightPanel.ProjectPath = _model?.CurrentProjectPath;
    }

    private void OnRightSplitterDragCompleted(object? sender, Avalonia.Input.VectorEventArgs e)
    {
        if (_model is { IsRightPanelOpen: true })
        {
            _model.CommitRightPanelWidth(Main.ColumnDefinitions[4].ActualWidth);
        }
    }

    private void OnLauncherRequested(object? sender, LauncherRequestedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }
        _model.Launcher.Refresh();
        // From the right half of the window the menu opens leftwards, so it stays inside the window.
        var onRight = e.Anchor.TranslatePoint(default, this) is { } point && point.X > Bounds.Width / 2;
        LauncherPopover.Alignment = onRight ? PopoverAlignment.End : PopoverAlignment.Start;
        LauncherMenuView.PanelOnLeft = onRight;
        LauncherPopover.ShowAt(e.Anchor);
        e.Handled = true;
    }

    private void OnLauncherCloseRequested(object? sender, EventArgs e) => LauncherPopover.Close();

    private void OnSplitterDragCompleted(object? sender, Avalonia.Input.VectorEventArgs e)
    {
        if (_model is not null && _model.IsSidebarVisible)
        {
            _model.CommitPanelWidth(LeftArea.Bounds.Width - ShellViewModel.RailWidth);
        }
    }

    private void OnSettingsRequested(object? sender, string? section)
    {
        if (_settings is not null)
        {
            return;
        }
        var view = _settings = new SettingsView { InitialSection = section };
        view.CloseRequested += (_, _) =>
        {
            Overlay.Children.Remove(view);
            _settings = null;
            _model?.Workspace.FocusActive();
        };
        Overlay.Children.Add(view);
    }
}
