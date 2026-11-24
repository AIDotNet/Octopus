using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Octopus.App.Common;
using Octopus.App.Localization;
using TKCode.Arc;

namespace Octopus.App.Explorer.Files;

/// <summary>The file tree: a click opens a folder or a file (Enter too), the context menu acts on the row under the pointer.</summary>
public partial class FileTreeView : UserControl
{
    private FileTreeViewModel? _model;

    public FileTreeView()
    {
        InitializeComponent();
        Tree.ChildrenSelector = static node => (node as FileNodeViewModel)?.Children;
        Tree.HeaderSelector = static node => (node as FileNodeViewModel)?.Name;
        Tree.ItemExpanded += OnItemExpanded;
        Tree.ItemCollapsed += OnItemCollapsed;
        Tree.ItemInvoked += OnItemInvoked;
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        Tree.AddHandler(ContextRequestedEvent, OnTreeContextRequested, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.CopyRequested -= OnCopyRequested;
            _model.CollapseAllRequested -= OnCollapseAllRequested;
        }
        _model = DataContext as FileTreeViewModel;
        if (_model is null) return;
        _model.CopyRequested += OnCopyRequested;
        _model.CollapseAllRequested += OnCollapseAllRequested;

        MenuFactory.Attach(Tree,
        [
            new(AppLangResources.Get(AppLangResourceKind.ExplorerOpen), _model.OpenContextCommand, Glyph: ArcIcons.FileText),
            new(AppLangResources.Get(AppLangResourceKind.ExplorerReveal), _model.RevealContextCommand, SeparatorBefore: true, Glyph: ArcIcons.FolderOpen),
            new(AppLangResources.Get(AppLangResourceKind.ExplorerCopyPath), _model.CopyContextPathCommand, Glyph: ArcIcons.Copy),
            new(AppLangResources.Get(AppLangResourceKind.ExplorerCopyRelativePath), _model.CopyContextRelativePathCommand, Glyph: ArcIcons.Link),
            new(AppLangResources.Get(AppLangResourceKind.ExplorerNewFileMenu), _model.NewFileHereCommand, SeparatorBefore: true, Glyph: ArcIcons.FilePlus),
            new(AppLangResources.Get(AppLangResourceKind.ExplorerNewFolderMenu), _model.NewFolderHereCommand, Glyph: ArcIcons.Folder),
            new(AppLangResources.Get(AppLangResourceKind.ExplorerRenameMenu), _model.RenameContextCommand, SeparatorBefore: true, Glyph: ArcIcons.Pencil),
            new(AppLangResources.Get(AppLangResourceKind.ExplorerDeleteMenu), _model.DeleteContextCommand, IsDestructive: true, Glyph: ArcIcons.Trash2),
        ]);
    }

    private void OnItemExpanded(object? sender, TreeViewItemEventArgs e)
    {
        if (e.Item is not FileNodeViewModel node) return;
        node.IsExpanded = true;
        _ = _model?.EnsureLoadedAsync(node);
    }

    private void OnItemCollapsed(object? sender, TreeViewItemEventArgs e)
    {
        if (e.Item is FileNodeViewModel node) node.IsExpanded = false;
    }

    private void OnCollapseAllRequested(object? sender, EventArgs e) => Tree.CollapseAll();

    private void OnItemInvoked(object? sender, TreeViewItemEventArgs e)
    {
        if (e.Item is FileNodeViewModel node) _model?.RequestOpen(node);
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (_model?.SelectedNode is not { } node) return;
        if (e.Key == Key.F2 && e.KeyModifiers == KeyModifiers.None)
        {
            _model.ContextNode = node;
            if (_model.RenameContextCommand.CanExecute(null))
            {
                _model.RenameContextCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if ((e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None)
                 || (OperatingSystem.IsMacOS() && e.Key == Key.Back && e.KeyModifiers == KeyModifiers.Meta))
        {
            _model.ContextNode = node;
            if (_model.DeleteContextCommand.CanExecute(null))
            {
                _model.DeleteContextCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    /// <summary>The row under the pointer becomes the target of the context menu (and is selected, like a file manager does).</summary>
    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_model is null) return;
        var props = e.GetCurrentPoint(Tree).Properties;
        var isContextClick = props.IsRightButtonPressed
                             || (OperatingSystem.IsMacOS() && props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control));
        if (!isContextClick) return;
        SetContextTarget(NodeAt(e.Source) ?? NodeAtPoint(e.GetPosition(Tree)));
    }

    /// <summary>
    /// Ensures <see cref="FileTreeViewModel.ContextNode"/> is updated before the context menu opens, covering right-click,
    /// macOS Ctrl+Click, and keyboard invocation (Shift+F10 / Menu key).
    /// </summary>
    private void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_model is null) return;
        if (e.TryGetPosition(Tree, out var pos))
        {
            SetContextTarget(NodeAt(e.Source) ?? NodeAtPoint(pos));
        }
        else
        {
            _model.ContextNode = _model.SelectedNode;
        }
    }

    private void SetContextTarget(FileNodeViewModel? node)
    {
        if (_model is null) return;
        _model.ContextNode = node;
        if (node is not null) _model.SelectedNode = node;
    }

    private FileNodeViewModel? NodeAtPoint(Point point) =>
        Tree.InputHitTest(point) is { } hit ? NodeAt(hit) : null;

    /// <summary>
    /// Resolves the row model from any hit element inside a tree row — including the row's label template, right-side
    /// blank space, and left-side indent / chevron area drawn by <c>TreeViewRow</c> itself.
    /// </summary>
    private static FileNodeViewModel? NodeAt(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual.DataContext is FileNodeViewModel node) return node;
            if (visual is Control control)
            {
                foreach (var child in control.GetVisualChildren())
                {
                    if (child is ContentPresenter { Content: FileNodeViewModel presented }) return presented;
                }
            }
        }
        for (var element = source as StyledElement; element is not null; element = element.Parent)
        {
            if (element.DataContext is FileNodeViewModel node) return node;
        }
        return null;
    }

    private async void OnCopyRequested(object? sender, string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
