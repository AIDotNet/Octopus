using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using TKCode.Arc;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Octopus.App.Common;
using Octopus.App.Launcher;

namespace Octopus.App.Sidebar;

public partial class ProjectPanelView : UserControl
{
    private ProjectPanelViewModel? _model;

    public ProjectPanelView()
    {
        InitializeComponent();
        ItemList.ItemTitleSelector = item => (item as ItemRowViewModel)?.Title ?? "";
        ItemList.ContainerPrepared += (_, e) => MakeCompact(e.Container);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.RenameRequested -= OnRenameRequested;
        }
        _model = DataContext as ProjectPanelViewModel;
        if (_model is not null)
        {
            _model.RenameRequested += OnRenameRequested;
        }
    }

    private void OnRenameRequested(object? sender, EventArgs e) =>
        UiContext.PostToUi(() => NameEdit.BeginEdit(), DispatcherPriority.Input);

    /// <summary>
    /// Arc's reorderable row is a card with a large grip on the left and a position number; a sidebar row is flat and
    /// 30px high, with a small grip at its right end that shows under the pointer. The parts are Arc's own (its grip
    /// cannot be created from outside), restyled with local values, so dragging and the keyboard work as in Arc.
    /// </summary>
    private void MakeCompact(Control container)
    {
        if (container is not TemplatedControl item)
        {
            return;
        }
        item.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<Border>("PART_Row") is not { } row
                || e.NameScope.Find<Control>("PART_Handle") is not { } handle)
            {
                return;
            }
            // The panel's own color: invisible at rest, and it covers the rows a lifted row passes over.
            row.Bind(Border.BackgroundProperty, PanelRoot.GetObservable(Panel.BackgroundProperty));
            row.BorderThickness = default;
            row.BoxShadow = default;
            row.MinHeight = 0;
            row.Padding = default;
            if (e.NameScope.Find<ContentPresenter>("PART_ContentPresenter") is { } content)
            {
                content.Margin = default;
            }
            var presenter = e.NameScope.Find<ContentPresenter>("PART_ContentPresenter");
            foreach (var part in (handle.Parent as Panel)?.Children ?? [])
            {
                // What is left beside the grip and the content is the position number.
                if (!ReferenceEquals(part, handle) && !ReferenceEquals(part, presenter))
                {
                    part.IsVisible = false;
                }
            }
            Grid.SetColumn(handle, 2);
            handle.Width = 18;
            handle.Height = 24;
            handle.Margin = new Thickness(0, 0, 2, 0);
            void Show() => handle.Opacity = item.IsPointerOver || handle.IsKeyboardFocusWithin ? 1 : 0;
            item.PropertyChanged += (_, change) =>
            {
                if (change.Property == IsPointerOverProperty)
                {
                    Show();
                }
            };
            handle.GotFocus += (_, _) => Show();
            handle.LostFocus += (_, _) => Show();
            Show();
        };
    }

    private void OnAddItemClick(object? sender, RoutedEventArgs e) => LauncherRequest.Raise(AddItemButton);

    private void OnItemsReordered(object? sender, ReorderableListReorderedEventArgs e) =>
        _model?.CommitMove(e.OldIndex, e.NewIndex);
}
