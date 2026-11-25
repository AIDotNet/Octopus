using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Composition;
using Octopus.App.Files.Lsp;
using Octopus.App.Services;
using TKCode.Arc;

namespace Octopus.App.Files.Code;

/// <summary>
/// The code viewer: hosts Arc's Monaco-based <see cref="CodeEditor"/> when <c>Experiments.CodeEditor</c> is enabled
/// (with optional local LSP attachment and cross-file <see cref="CodeEditorActionHost"/> navigation), or falls back to
/// the lightweight read-only <see cref="CodeSurface"/> when turned off.
/// </summary>
public partial class CodeViewerView : UserControl, IFileViewerView
{
    /// <summary>Hide the minimap below this width so the source code keeps room in narrow splits.</summary>
    private const double MinimapMinWidth = 520;

    private CodeViewerModel? _model;
    private int _loadedId;
    private IDisposable? _lspAttachment;
    private CancellationTokenSource? _lspCts;

    public CodeViewerView()
    {
        InitializeComponent();
        Editor.TextChanged += OnEditorTextChanged;
        Editor.SizeChanged += OnEditorSizeChanged;
        Editor.LinkOpening += OnLinkOpening;
    }

    public CodeEditor EditorControl => Editor;
    public CodeSurface FallbackSurface => Fallback;

    public void FocusViewer()
    {
        if (_model?.ShowCodeEditor == true) Editor.Focus();
        else if (_model?.ShowReadOnlyFallback == true) Fallback.CodeView.Focus();
        else Focus();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        DetachLanguageServer();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        DetachLanguageServer();
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
            _model.ReadEditor = null;
        }
        _loadedId = 0;
        _model = DataContext as CodeViewerModel;
        if (_model is null)
        {
            Editor.ClearValue(FontFamilyProperty);
            return;
        }
        ApplyFont(_model.MonoFontFamily);
        _model.ReadEditor = () => Editor.Text ?? "";
        _model.PropertyChanged += OnModelPropertyChanged;
        LoadEditorIfNeeded();
        SyncLanguageServer();
    }

    private void ApplyFont(string? family)
    {
        if (!string.IsNullOrWhiteSpace(family)) Editor.FontFamily = new FontFamily(family);
        else Editor.ClearValue(FontFamilyProperty);
    }

    private void OnEditorSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var show = e.NewSize.Width >= MinimapMinWidth;
        if (Editor.ShowMinimap != show) Editor.ShowMinimap = show;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e) => _model?.EditorChanged();

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CodeViewerModel.LoadId):
                LoadEditorIfNeeded();
                SyncLanguageServer();
                break;
            case nameof(CodeViewerModel.UseLanguageServer):
            case nameof(CodeViewerModel.UseCodeEditor):
                SyncLanguageServer();
                break;
        }
    }

    private void LoadEditorIfNeeded()
    {
        if (_model is not { ShowCodeEditor: true } model || model.LoadId == _loadedId || model.LoadId == 0) return;
        var reloading = _loadedId > 0;
        _loadedId = model.LoadId;

        double? savedVertical = null;
        double? savedHorizontal = null;
        if (reloading)
        {
            savedVertical = FindScrollBar(Orientation.Vertical)?.Value;
            savedHorizontal = FindScrollBar(Orientation.Horizontal)?.Value;
        }

        model.BeginEditorLoad();
        Editor.Text = model.EditorText;
        model.EditorLoaded(Editor.Text ?? "");

        if (reloading)
        {
            RestoreViewport(savedVertical, savedHorizontal);
        }
    }

    private void RestoreViewport(double? vertical, double? horizontal)
    {
        void ApplyScroll()
        {
            if (vertical is { } v && FindScrollBar(Orientation.Vertical) is { } vBar)
            {
                vBar.Value = Math.Clamp(v, vBar.Minimum, vBar.Maximum);
            }
            if (horizontal is { } h && FindScrollBar(Orientation.Horizontal) is { } hBar)
            {
                hBar.Value = Math.Clamp(h, hBar.Minimum, hBar.Maximum);
            }
        }

        ApplyScroll();
        Dispatcher.UIThread.Post(ApplyScroll, DispatcherPriority.Loaded);
    }

    private void SyncLanguageServer()
    {
        if (_model is not { ShowCodeEditor: true, UseLanguageServer: true, LoadId: > 0 } model
            || string.IsNullOrWhiteSpace(model.Path))
        {
            DetachLanguageServer();
            return;
        }
        if (_lspAttachment is not null || _lspCts is not null) return;
        if (AppHost.Services.GetService(typeof(ILanguageServerManager)) is not ILanguageServerManager manager) return;

        var cts = new CancellationTokenSource();
        _lspCts = cts;
        _ = AttachLanguageServerAsync(manager, model, cts);
    }

    private async Task AttachLanguageServerAsync(ILanguageServerManager manager, CodeViewerModel model, CancellationTokenSource cts)
    {
        try
        {
            var attachment = await manager.AttachAsync(Editor, model.Path, model.EditorLanguageId, model.ProjectRoot, cts.Token);
            if (attachment is null) return;
            if (cts.IsCancellationRequested || !ReferenceEquals(_model, model) || !model.UseLanguageServer)
            {
                attachment.Dispose();
                return;
            }
            _lspAttachment = attachment;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            if (ReferenceEquals(_lspCts, cts))
            {
                _lspCts = null;
                cts.Dispose();
            }
        }
    }

    private void DetachLanguageServer()
    {
        if (_lspCts is { } cts)
        {
            _lspCts = null;
            cts.Cancel();
            cts.Dispose();
        }
        if (_lspAttachment is { } attachment)
        {
            _lspAttachment = null;
            try { attachment.Dispose(); } catch (Exception) { }
        }
    }

    private static void OnLinkOpening(object? sender, CodeEditorLinkOpeningEventArgs e)
    {
        if (e.Uri is not { IsFile: true } uri) return;
        var path = uri.LocalPath;
        if (!File.Exists(path)) return;
        if (AppHost.Services.GetService(typeof(IItemOpener)) is IItemOpener opener && opener.OpenFile(path) is not null)
        {
            e.Handled = true;
        }
    }

    internal ScrollBar? FindScrollBar(Orientation orientation) =>
        Editor.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(s => s.Orientation == orientation);
}
