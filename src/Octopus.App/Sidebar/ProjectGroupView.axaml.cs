using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Octopus.App.Common;
using Octopus.App.Localization;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.Motion;

namespace Octopus.App.Sidebar;

/// <summary>
/// A group on the rail. Opening and closing is animated: the frame's height springs between the folder's 42 px and the
/// capsule's height while the folder and the capsule cross-fade, and the squares below slide along. Arc's motion value
/// drives it, so it follows the window's motion level (Reduced and Off jump), and a second click mid-way turns it
/// around from where it is.
/// </summary>
public partial class ProjectGroupView : UserControl
{
    /// <summary>The height of the closed folder: its 42 px slot.</summary>
    private const double FolderHeight = 42;

    private ProjectGroupViewModel? _model;
    /// <summary>0 closed, 1 open; exists while the view is in the visual tree.</summary>
    private MotionValue<double>? _openness;
    private double _openHeight = FolderHeight;
    private int _generation;

    public ProjectGroupView() => InitializeComponent();

    /// <summary>Shows where a dragged thing would go: a square into this group, a folder before or after this one.</summary>
    public void ShowDropMark(DropMark mark)
    {
        var open = _model?.IsExpanded == true;
        FolderDropMark.IsVisible = mark == DropMark.Into && !open;
        CapsuleDropMark.IsVisible = mark == DropMark.Into && open;
        BeforeLine.IsVisible = mark == DropMark.Before;
        AfterLine.IsVisible = mark == DropMark.After;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }
        _model = DataContext as ProjectGroupViewModel;
        if (_model is null)
        {
            return;
        }
        _model.PropertyChanged += OnModelChanged;
        // The same menu on the closed folder and on the open capsule's folder icon.
        MenuFactory.Attach(FolderButton, MenuOf(_model));
        MenuFactory.Attach(HeaderButton, MenuOf(_model));
        Start();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _openness = new MotionValue<double>(this, _model?.IsExpanded == true ? 1 : 0, MotionVectors.Double);
        _openness.Changed += Apply;
        Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _generation++;
        _openness?.Dispose();
        _openness = null;
        if (_model is not null)
        {
            Rest(_model.IsExpanded);
        }
    }

    /// <summary>Shows the group as it is, or plays the change the rail asked for when it rebuilt.</summary>
    private void Start()
    {
        if (_model is null)
        {
            return;
        }
        if (_openness is not null && _model.AnimateOnAttach)
        {
            _model.AnimateOnAttach = false;
            Animate(_model.IsExpanded, fromOtherState: true);
        }
        else
        {
            _openness?.Jump(_model.IsExpanded ? 1 : 0);
            Rest(_model.IsExpanded);
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectGroupViewModel.IsExpanded) && _model is not null)
        {
            Animate(_model.IsExpanded, fromOtherState: false);
        }
    }

    private async void Animate(bool open, bool fromOtherState)
    {
        if (_openness is not { } openness)
        {
            Rest(open);
            return;
        }
        // Both are shown during the change; the capsule's height is measured here because it is hidden at rest.
        Folder.IsVisible = true;
        Capsule.IsVisible = true;
        Capsule.Measure(Size.Infinity);
        _openHeight = Math.Max(FolderHeight, Capsule.DesiredSize.Height);
        if (fromOtherState)
        {
            openness.Jump(open ? 0 : 1);
        }
        else
        {
            Apply(openness.Value);
        }
        var generation = ++_generation;
        var arrived = await openness.AnimateTo(open ? 1 : 0, MotionSpec.Spring(ArcSpring.Settle));
        if (arrived && generation == _generation)
        {
            Rest(open);
        }
    }

    private void Apply(double value)
    {
        var openness = Math.Clamp(value, 0, 1);
        Frame.Height = FolderHeight + (_openHeight - FolderHeight) * openness;
        Folder.Opacity = 1 - openness;
        Capsule.Opacity = openness;
    }

    /// <summary>At rest: the frame follows its content and only the folder or only the capsule is shown.</summary>
    private void Rest(bool open)
    {
        Frame.Height = double.NaN;
        Folder.IsVisible = !open;
        Capsule.IsVisible = open;
        Folder.Opacity = 1;
        Capsule.Opacity = 1;
    }

    private static List<MenuFactory.Entry> MenuOf(ProjectGroupViewModel group) =>
    [
        new(AppLangResources.Get(AppLangResourceKind.RenameGroup), group.RenameCommand),
        new(AppLangResources.Get(AppLangResourceKind.GroupColor), null)
        {
            Children = ProjectRailViewModel.GroupColors
                .Select(c => new MenuFactory.Entry(AppLangResources.Get(c.Name), group.SetColorCommand)
                {
                    CommandParameter = c.Value,
                    Icon = Swatch(c.Value, string.Equals(c.Value, group.Color, StringComparison.OrdinalIgnoreCase)),
                })
                .ToList(),
        },
        new(AppLangResources.Get(AppLangResourceKind.RemoveGroup), group.RemoveCommand, IsDestructive: true, SeparatorBefore: true),
    ];

    /// <summary>A small square of the color; the group's own color carries a check (a row with an icon shows no check of its own).</summary>
    private static Control Swatch(string color, bool isCurrent)
    {
        var swatch = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new Avalonia.CornerRadius(4),
            Background = new SolidColorBrush(Color.Parse(color)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (isCurrent)
        {
            swatch.Child = new LucideIcon
            {
                Kind = LucideIconKind.Check,
                Size = 10,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        return swatch;
    }
}
