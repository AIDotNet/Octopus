using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Octopus.App.Files.Code;
using TKCode.Arc;

namespace Octopus.App.Files.Documents;

/// <summary>
/// The Markdown viewer: the rich text editor (a fresh one per load, in Arc's Docs profile so tables, images, headings 4 to
/// 6, task lists and code languages are kept), the read-only preview, and the source. All state and every decision is in
/// <see cref="MarkdownViewerModel"/>; this view only loads text into the editor and reports what the editor does.
/// </summary>
public partial class MarkdownViewerView : UserControl, IFileViewerView
{
    private MarkdownViewerModel? _model;
    private int _loadedId;

    public MarkdownViewerView() => InitializeComponent();

    /// <summary>The editor of the document that is open; null while the file is read-only or not loaded.</summary>
    public RichTextEditor? Editor { get; private set; }

    public ScrollArea PreviewScrollArea => PreviewScroller;
    public ScrollArea EditorScrollArea => EditorScroller;
    public CodeSurface SourceSurface => Source;
    public TKCode.Arc.Markdown PreviewView => Preview;

    public void FocusViewer()
    {
        if (Editor is not null && _model?.ShowEditor == true) Editor.FocusEditor();
        else Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
            _model.ReadEditor = null;
        }
        _model = DataContext as MarkdownViewerModel;
        if (_model is null) return;
        _model.ReadEditor = () => Editor?.GetMarkdown() ?? _model.Text;
        _model.PropertyChanged += OnModelPropertyChanged;
        LoadEditorIfNeeded();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MarkdownViewerModel.LoadId):
                LoadEditorIfNeeded();
                break;
            case nameof(MarkdownViewerModel.Text):
                KeepPreviewPlace();
                break;
        }
    }

    /// <summary>Puts the model's text into a new editor whenever the model says a load is due.</summary>
    private void LoadEditorIfNeeded()
    {
        if (_model is not { IsEditing: true } model || model.LoadId == _loadedId || model.LoadId == 0) return;
        _loadedId = model.LoadId;
        model.BeginEditorLoad();
        var editor = new RichTextEditor { Profile = RichTextEditorProfile.Docs };
        // Set before the editor is in the tree: a start-up value is not an undo step, so Undo never steps back to an empty document.
        editor.Markdown = model.EditorText;
        editor.ImageLoading += OnImageLoading;
        EditorHost.Child = editor;
        Editor = editor;
        model.EditorLoaded(editor.GetMarkdown());
        // Edits are reported from here on; the load itself raised nothing the model could mistake for one.
        editor.ContentChanged += (_, _) => _model?.EditorChanged();
    }

    /// <summary>A relative picture address (./img.png) starts at the Markdown file's own folder.</summary>
    private void OnImageLoading(object? sender, RichTextImageLoadingEventArgs e)
    {
        if (_model is null || MarkdownImagePath.Resolve(_model.FolderPath, e.Source) is not { } file) return;
        e.StreamTask = Task.Run(() => MarkdownImagePath.Open(file));
    }

    /// <summary>The file changed on disk and the preview parses again: it returns to where it was once the new document is ready.</summary>
    private void KeepPreviewPlace()
    {
        var offset = PreviewScroller.Offset;
        if (offset == default) return;
        Dispatcher.UIThread.Post(() => PreviewScroller.Offset = offset, DispatcherPriority.Loaded);
        void OnReady(object? s, EventArgs args)
        {
            Preview.DocumentReady -= OnReady;
            Dispatcher.UIThread.Post(() => PreviewScroller.Offset = offset, DispatcherPriority.Loaded);
        }
        Preview.DocumentReady += OnReady;
    }
}
