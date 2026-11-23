using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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
    private bool _isEditingName;
    private string? _editingProjectId;

    public ProjectPanelView()
    {
        InitializeComponent();
        ItemList.ItemTitleSelector = item => (item as ItemRowViewModel)?.Title ?? "";
        ItemList.ContainerPrepared += (_, e) => MakeCompact(e.Container);
        NameInput.AddHandler(KeyDownEvent, OnNameInputKeyDown, RoutingStrategies.Tunnel);
        NameInput.PropertyChanged += OnNameInputPropertyChanged;
        NameInput.LostFocus += OnNameInputLostFocus;
    }

    /// <summary>True while the project name inline editor is open.</summary>
    public bool IsEditingProjectName => _isEditingName;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.RenameRequested -= OnRenameRequested;
            _model.PropertyChanged -= OnModelPropertyChanged;
        }
        if (_isEditingName)
        {
            EndNameEdit(focusDisplay: false);
        }
        _model = DataContext as ProjectPanelViewModel;
        if (_model is not null)
        {
            _model.RenameRequested += OnRenameRequested;
            _model.PropertyChanged += OnModelPropertyChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_isEditingName)
        {
            EndNameEdit(focusDisplay: false);
        }
    }

    private void OnRenameRequested(object? sender, EventArgs e) =>
        UiContext.PostToUi(BeginNameEdit, DispatcherPriority.Input);

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isEditingName)
        {
            return;
        }
        if (e.PropertyName is nameof(ProjectPanelViewModel.ProjectId) or nameof(ProjectPanelViewModel.HasProject)
            && (!(_model?.HasProject ?? false) || _model?.ProjectId != _editingProjectId))
        {
            // Switching projects while editing commits the draft to the project that was being edited, never the new one.
            CommitNameEdit(focusDisplay: false);
        }
    }

    /// <summary>Opens the project name inline editor and selects all text for immediate replacement.</summary>
    public void BeginNameEdit()
    {
        if (_model is not { HasProject: true, ProjectId: { } projectId })
        {
            return;
        }
        if (_isEditingName && _editingProjectId == projectId)
        {
            NameInput.Focus();
            NameInput.SelectAll();
            return;
        }
        _isEditingName = true;
        _editingProjectId = projectId;
        NameInput.Text = _model.ProjectName;
        UpdateNameEditActions();
        NameDisplayButton.IsVisible = false;
        NameEditFrame.IsVisible = true;
        NameInput.Focus();
        NameInput.SelectAll();
    }

    /// <summary>Commits the active edit (reverting quietly if blank) and closes the inline editor.</summary>
    public void CommitNameEdit(bool focusDisplay = false)
    {
        if (!_isEditingName)
        {
            return;
        }
        var projectId = _editingProjectId;
        var draft = NameInput.Text;
        EndNameEdit(focusDisplay);
        _model?.CommitProjectRename(projectId, draft);
    }

    /// <summary>Cancels the active edit without saving and closes the inline editor.</summary>
    public void CancelNameEdit(bool focusDisplay = false)
    {
        if (!_isEditingName)
        {
            return;
        }
        EndNameEdit(focusDisplay);
    }

    private void EndNameEdit(bool focusDisplay)
    {
        _isEditingName = false;
        _editingProjectId = null;
        NameEditFrame.IsVisible = false;
        NameDisplayButton.IsVisible = true;
        if (focusDisplay)
        {
            NameDisplayButton.Focus();
        }
    }

    private void OnNameDisplayClick(object? sender, RoutedEventArgs e) => BeginNameEdit();

    private void OnNameSaveClick(object? sender, RoutedEventArgs e) => CommitNameEdit(focusDisplay: true);

    private void OnNameCancelClick(object? sender, RoutedEventArgs e) => CancelNameEdit(focusDisplay: true);

    private void OnNameInputPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty && _isEditingName)
        {
            UpdateNameEditActions();
        }
    }

    private void UpdateNameEditActions() =>
        NameSaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NameInput.Text);

    private void OnNameInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isEditingName || HasImePreedit())
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitNameEdit(focusDisplay: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelNameEdit(focusDisplay: true);
        }
    }

    private bool HasImePreedit() =>
        !string.IsNullOrEmpty(NameInput.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault()?.PreeditText);

    private void OnNameInputLostFocus(object? sender, RoutedEventArgs e)
    {
        if (!_isEditingName)
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isEditingName || NameSaveButton.IsPressed || NameCancelButton.IsPressed)
            {
                return;
            }
            if (TopLevel.GetTopLevel(this) is { } topLevel)
            {
                if (topLevel is WindowBase window && !window.IsActive)
                {
                    return;
                }
                if (topLevel.FocusManager?.GetFocusedElement() is Visual focused
                    && (ReferenceEquals(focused, NameEditFrame) || NameEditFrame.IsVisualAncestorOf(focused)))
                {
                    return;
                }
            }
            CommitNameEdit(focusDisplay: false);
        }, DispatcherPriority.Input);
    }

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
