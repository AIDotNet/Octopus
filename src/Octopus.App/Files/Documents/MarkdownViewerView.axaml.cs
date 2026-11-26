using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using StreamMark.Avalonia.Extensions;
using StreamMark.Mermaid;
using TKCode.Arc;

namespace Octopus.App.Files.Documents;

/// <summary>
/// The Markdown viewer: the rich text editor (a fresh one per load, in Arc's Docs profile so tables, images, headings 4 to
/// 6, task lists and code languages are kept), the read-only preview, and the editable raw Markdown source
/// (<see cref="CodeEditor"/>). All state and every decision is in <see cref="MarkdownViewerModel"/>; this view only loads
/// text into the editors and reports what they do.
/// </summary>
public partial class MarkdownViewerView : UserControl, IFileViewerView
{
    /// <summary>Hide the source editor's minimap below this width so prose keeps room in narrow splits.</summary>
    private const double MinimapMinWidth = 520;

    /// <summary>Arc's Markdown defaults, plus <c>```mermaid</c> blocks drawn as diagrams (with a toggle back to the source).</summary>
    private static readonly MarkdownServices PreviewServices = new()
    {
        Plugins = [.. TKCode.Arc.Markdown.DefaultServices.Plugins, new MermaidPlugin()],
        CodeBlocks = TKCode.Arc.Markdown.DefaultServices.CodeBlocks,
    };

    private MarkdownViewerModel? _model;
    private int _loadedId;
    private int _sourceLoadedId;
    private EventHandler? _pendingPreviewReady;

    public MarkdownViewerView()
    {
        InitializeComponent();
        Preview.Services = PreviewServices;
        Source.TextChanged += OnSourceTextChanged;
        Source.SizeChanged += OnSourceSizeChanged;
    }

    /// <summary>The rich text editor of the document that is open; null while the file is read-only or not loaded.</summary>
    public RichTextEditor? Editor { get; private set; }

    public ScrollArea PreviewScrollArea => PreviewScroller;
    public ScrollArea EditorScrollArea => EditorScroller;
    public CodeEditor SourceEditor => Source;
    public TKCode.Arc.Markdown PreviewView => Preview;

    public void FocusViewer()
    {
        if (_model?.ShowSourceEditor == true) Source.Focus();
        else if (_model?.ShowSourceFallback == true) SourceFallback.CodeView.Focus();
        else if (Editor is not null && _model?.ShowEditor == true) Editor.FocusEditor();
        else Focus();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        CancelPendingPreviewReady();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        CancelPendingPreviewReady();
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
            _model.ReadEditor = null;
            _model.ReadSourceEditor = null;
        }
        _loadedId = 0;
        _sourceLoadedId = 0;
        _model = DataContext as MarkdownViewerModel;
        if (_model is null)
        {
            DetachEditor();
            Source.Text = "";
            Source.ClearValue(FontFamilyProperty);
            return;
        }
        ApplySourceFont(_model.MonoFontFamily);
        _model.ReadEditor = () => Editor?.GetMarkdown() ?? _model.Text;
        _model.ReadSourceEditor = () => Source.Text ?? _model.SourceViewText;
        _model.PropertyChanged += OnModelPropertyChanged;
        LoadEditorIfNeeded();
        LoadSourceIfNeeded();
    }

    private void ApplySourceFont(string? family)
    {
        if (!string.IsNullOrWhiteSpace(family)) Source.FontFamily = new FontFamily(family);
        else Source.ClearValue(FontFamilyProperty);
    }

    private void OnSourceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var show = e.NewSize.Width >= MinimapMinWidth;
        if (Source.ShowMinimap != show) Source.ShowMinimap = show;
    }

    private void DetachEditor()
    {
        if (Editor is not { } old) return;
        old.ImageLoading -= OnImageLoading;
        old.ContentChanged -= OnEditorContentChanged;
        EditorHost.Child = null;
        Editor = null;
    }

    private void OnEditorContentChanged(object? sender, EventArgs e) => _model?.EditorChanged();
    private void OnSourceTextChanged(object? sender, EventArgs e) => _model?.SourceEditorChanged();

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MarkdownViewerModel.LoadId):
                LoadEditorIfNeeded();
                break;
            case nameof(MarkdownViewerModel.SourceLoadId):
                LoadSourceIfNeeded();
                break;
            case nameof(MarkdownViewerModel.PreviewText):
                KeepPreviewPlace();
                break;
        }
    }

    /// <summary>Puts the model's text into a new rich text editor whenever the model says a load is due.</summary>
    private void LoadEditorIfNeeded()
    {
        if (_model is not { IsEditing: true } model || model.LoadId == _loadedId || model.LoadId == 0) return;
        _loadedId = model.LoadId;
        DetachEditor();
        model.BeginEditorLoad();
        var editor = new RichTextEditor { Profile = RichTextEditorProfile.Docs };
        // Set before the editor is in the tree: a start-up value is not an undo step, so Undo never steps back to an empty document.
        editor.Markdown = model.EditorText;
        editor.ImageLoading += OnImageLoading;
        EditorHost.Child = editor;
        Editor = editor;
        // The fidelity check already exported this text from an editor like this one: no second export of a large file.
        model.EditorLoaded(model.ExportOfEditorText ?? editor.GetMarkdown());
        // Edits are reported from here on; the load itself raised nothing the model could mistake for one.
        editor.ContentChanged += OnEditorContentChanged;
    }

    /// <summary>Loads raw Markdown into the source <see cref="CodeEditor"/> when the model requests it.</summary>
    private void LoadSourceIfNeeded()
    {
        if (_model is not { HasContent: true } model || model.SourceLoadId == _sourceLoadedId || model.SourceLoadId == 0) return;
        _sourceLoadedId = model.SourceLoadId;
        model.BeginSourceLoad();
        Source.Text = model.SourceViewText;
        model.SourceLoaded(Source.Text ?? "");
    }

    /// <summary>A relative picture address (./img.png) starts at the Markdown file's own folder.</summary>
    private void OnImageLoading(object? sender, RichTextImageLoadingEventArgs e)
    {
        if (_model is null || MarkdownImagePath.Resolve(_model.FolderPath, e.Source) is not { } file) return;
        e.StreamTask = Task.Run(() => MarkdownImagePath.Open(file));
    }

    /// <summary>The preview's text changed and it parses again: it returns to where it was once the new document is ready.</summary>
    private void KeepPreviewPlace()
    {
        CancelPendingPreviewReady();
        var offset = PreviewScroller.Offset;
        if (offset == default) return;
        Dispatcher.UIThread.Post(() => PreviewScroller.Offset = offset, DispatcherPriority.Loaded);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (ReferenceEquals(_pendingPreviewReady, handler))
            {
                _pendingPreviewReady = null;
            }
            Preview.DocumentReady -= handler;
            Dispatcher.UIThread.Post(() => PreviewScroller.Offset = offset, DispatcherPriority.Loaded);
        };
        _pendingPreviewReady = handler;
        Preview.DocumentReady += handler;
    }

    private void CancelPendingPreviewReady()
    {
        if (_pendingPreviewReady is not { } pending) return;
        _pendingPreviewReady = null;
        Preview.DocumentReady -= pending;
    }
}
