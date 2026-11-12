using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Octopus.App.Sidebar;

/// <summary>
/// The rail. Project squares and group folders can be dragged.
/// <list type="bullet">
/// <item>A square on the upper or lower edge of another square goes right before or after it (into that square's
/// group, or out of any); on the middle of a square in no group the two make a new group; on a folder or an open
/// capsule it joins that group; on the rail below everything it leaves its group.</item>
/// <item>A folder on the upper or lower half of another folder goes right before or after it; on the rail below
/// everything it goes last. Folders stay after the squares in no group.</item>
/// </list>
/// </summary>
public partial class ProjectRailView : UserControl
{
    // Application formats, not in-process ones: on macOS Avalonia (12.1.5) leaves in-process formats off the
    // pasteboard, and AppKit aborts the whole app on a drag whose only item has no pasteboard type ("There are 0 items
    // on the pasteboard, but 1 drag images"). The payload is just an id, so exposing it to the pasteboard is harmless.
    private static readonly DataFormat<string> s_project = DataFormat.CreateStringApplicationFormat("octopus.project-square");
    private static readonly DataFormat<string> s_group = DataFormat.CreateStringApplicationFormat("octopus.project-group");

    /// <summary>How far the pointer moves with the button down before a press becomes a drag.</summary>
    private const double DragThreshold = 6;

    /// <summary>On a square in no group: the share of its height at the top and at the bottom that places instead of grouping.</summary>
    private const double EdgeShare = 0.3;

    private PointerPressedEventArgs? _press;
    private Point _pressPoint;
    private (DataFormat<string> Format, string Id)? _pressed;
    private bool _dragging;
    private Control? _markView;
    private DropMark _mark;

    public ProjectRailView()
    {
        InitializeComponent();
        // Squares and folders are buttons that take the press themselves: listen on the way down.
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => _press = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => Mark(null, DropMark.None));
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private ProjectRailViewModel? Model => DataContext as ProjectRailViewModel;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _press = null;
        if (_dragging || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is not Visual source)
        {
            return;
        }
        if (source.FindAncestorOfType<ProjectChipView>(includeSelf: true)?.DataContext is ProjectChipViewModel chip)
        {
            _pressed = (s_project, chip.Id);
        }
        else if (source.FindAncestorOfType<ProjectGroupView>(includeSelf: true)?.DataContext is ProjectGroupViewModel group)
        {
            _pressed = (s_group, group.Id);
        }
        else
        {
            return;
        }
        _press = e;
        _pressPoint = e.GetPosition(this);
    }

    private async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || _pressed is not { } pressed || _dragging)
        {
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _press = null;
            return;
        }
        var delta = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
        {
            return;
        }
        _press = null;
        _dragging = true;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(pressed.Format, pressed.Id));
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
            Mark(null, DropMark.None);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var plan = PlanOf(e);
        e.DragEffects = plan is null ? DragDropEffects.None : DragDropEffects.Move;
        Mark(plan?.View, plan?.Mark ?? DropMark.None);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        Mark(null, DropMark.None);
        if (PlanOf(e) is not { } plan || Model is not { } model)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Move;
        await plan.Apply(model);
    }

    private DropPlan? PlanOf(DragEventArgs e)
    {
        var source = e.Source as Visual;
        if (e.DataTransfer.TryGetValue(s_project) is { } projectId)
        {
            return PlanForSquare(e, source, projectId);
        }
        return e.DataTransfer.TryGetValue(s_group) is { } groupId ? PlanForFolder(e, source, groupId) : null;
    }

    /// <summary>Where a dragged square would go; null over itself.</summary>
    private DropPlan? PlanForSquare(DragEventArgs e, Visual? source, string projectId)
    {
        for (var visual = source; visual is not null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
        {
            switch (visual)
            {
                case ProjectChipView { DataContext: ProjectChipViewModel chip } view:
                {
                    if (chip.Id == projectId)
                    {
                        return null;
                    }
                    // In a group the halves place it; in no group the middle makes a group of the two.
                    var edge = chip.GroupId is null ? EdgeShare : 0.5;
                    var y = e.GetPosition(view).Y / Math.Max(1, view.Bounds.Height);
                    if (y < edge)
                    {
                        return new(view, DropMark.Before, m => Done(() => m.DropProjectNextTo(projectId, chip.Id, after: false)));
                    }
                    if (y > 1 - edge)
                    {
                        return new(view, DropMark.After, m => Done(() => m.DropProjectNextTo(projectId, chip.Id, after: true)));
                    }
                    return new(view, DropMark.Into, m => m.DropOnProjectAsync(projectId, chip.Id));
                }
                case ProjectGroupView { DataContext: ProjectGroupViewModel group } view:
                    return new(view, DropMark.Into, m => Done(() => m.DropOnGroup(projectId, group.Id)));
            }
        }
        return new(null, DropMark.None, m => Done(() => m.DropOnRail(projectId)));
    }

    /// <summary>Where a dragged folder would go: among the folders only; null over itself or over a square in no group.</summary>
    private DropPlan? PlanForFolder(DragEventArgs e, Visual? source, string groupId)
    {
        if (source?.FindAncestorOfType<ProjectGroupView>(includeSelf: true) is { DataContext: ProjectGroupViewModel target } view)
        {
            if (target.Id == groupId)
            {
                return null;
            }
            var after = e.GetPosition(view).Y > view.Bounds.Height / 2;
            return new(view, after ? DropMark.After : DropMark.Before, m => Done(() => m.DropGroupNextTo(groupId, target.Id, after)));
        }
        if (source?.FindAncestorOfType<ProjectChipView>(includeSelf: true) is not null)
        {
            return null;
        }
        return new(null, DropMark.None, m => Done(() => m.DropGroupLast(groupId)));
    }

    private static Task Done(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private void Mark(Control? view, DropMark mark)
    {
        if (ReferenceEquals(view, _markView) && mark == _mark)
        {
            return;
        }
        SetMark(_markView, DropMark.None);
        _markView = view;
        _mark = mark;
        SetMark(_markView, mark);
    }

    private static void SetMark(Control? view, DropMark mark)
    {
        switch (view)
        {
            case ProjectChipView chip:
                chip.ShowDropMark(mark);
                break;
            case ProjectGroupView group:
                group.ShowDropMark(mark);
                break;
        }
    }

    /// <summary>A drop: the view to mark (null: the rail itself), the mark, and what dropping does.</summary>
    private sealed record DropPlan(Control? View, DropMark Mark, Func<ProjectRailViewModel, Task> Apply);
}
