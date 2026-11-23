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

    private static readonly AttachedProperty<bool> s_compactWiredProperty =
        AvaloniaProperty.RegisterAttached<ProjectPanelView, TemplatedControl, bool>("CompactWired");
    private static readonly AttachedProperty<IDisposable?> s_rowBackgroundSubProperty =
        AvaloniaProperty.RegisterAttached<ProjectPanelView, TemplatedControl, IDisposable?>("RowBackgroundSub");

    /// <summary>
    /// Arc's reorderable row is a card with a large grip on the left and a position number; a sidebar row is flat and
    /// 30px high, with a small grip at its right end that shows under the pointer. The parts are Arc's own (its grip
    /// cannot be created from outside), restyled with local values, so dragging and the keyboard work as in Arc.
    /// </summary>
    private void MakeCompact(Control container)
    {
        if (container is not TemplatedControl item || item.GetValue(s_compactWiredProperty))
        {
            return;
        }
        item.SetValue(s_compactWiredProperty, true);
        Control? currentHandle = null;
        void Show()
        {
            if (currentHandle is { } h) h.Opacity = item.IsPointerOver || h.IsKeyboardFocusWithin ? 1 : 0;
        }
        void OnHandleFocus(object? s, RoutedEventArgs _) => Show();

        item.PropertyChanged += (_, change) =>
        {
            if (change.Property == IsPointerOverProperty) Show();
        };
        item.DetachedFromVisualTree += (_, _) =>
        {
            item.GetValue(s_rowBackgroundSubProperty)?.Dispose();
            item.SetValue(s_rowBackgroundSubProperty, null);
        };
        item.TemplateApplied += (_, e) =>
        {
            item.GetValue(s_rowBackgroundSubProperty)?.Dispose();
            item.SetValue(s_rowBackgroundSubProperty, null);
            if (currentHandle is not null)
            {
                currentHandle.GotFocus -= OnHandleFocus;
                currentHandle.LostFocus -= OnHandleFocus;
                currentHandle = null;
            }
            if (e.NameScope.Find<Border>("PART_Row") is not { } row
                || e.NameScope.Find<Control>("PART_Handle") is not { } handle)
            {
                return;
            }
            currentHandle = handle;
            // The panel's own color: invisible at rest, and it covers the rows a lifted row passes over.
            if (item.IsAttachedToVisualTree())
            {
                item.SetValue(s_rowBackgroundSubProperty, row.Bind(Border.BackgroundProperty, PanelRoot.GetObservable(Panel.BackgroundProperty)));
            }
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
            handle.GotFocus += OnHandleFocus;
            handle.LostFocus += OnHandleFocus;
            Show();
        };
    }

    private void OnAddItemClick(object? sender, RoutedEventArgs e) => LauncherRequest.Raise(AddItemButton);

    private void OnItemsReordered(object? sender, ReorderableListReorderedEventArgs e) =>
        _model?.CommitMove(e.OldIndex, e.NewIndex);
}
