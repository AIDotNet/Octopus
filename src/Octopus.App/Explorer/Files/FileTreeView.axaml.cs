using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
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
        Tree.ItemExpanded += OnItemExpanded;
        Tree.ItemCollapsed += OnItemCollapsed;
        Tree.Tapped += OnTapped;
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
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
        _model?.EnsureLoaded(node);
    }

    private void OnItemCollapsed(object? sender, TreeViewItemEventArgs e)
    {
        if (e.Item is FileNodeViewModel node) node.IsExpanded = false;
    }

    private void OnCollapseAllRequested(object? sender, EventArgs e) => Tree.CollapseAll();

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (NodeAt(e.Source) is { } node) _model?.RequestOpen(node);
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _model?.SelectedNode is { } node)
        {
            _model.RequestOpen(node);
        }
    }

    /// <summary>The row under the pointer becomes the target of the context menu (and is selected, like a file manager does).</summary>
    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_model is null || !e.GetCurrentPoint(Tree).Properties.IsRightButtonPressed) return;
        var node = NodeAt(e.Source);
        _model.ContextNode = node;
        if (node is not null) _model.SelectedNode = node;
    }

    private static FileNodeViewModel? NodeAt(object? source)
    {
        for (var element = source as Avalonia.StyledElement; element is not null; element = element.Parent)
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
