using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Octopus.App.Workspace;
using TKCode.Arc;

namespace Octopus.App.Files;

/// <summary>
/// A diff tab: Monaco <see cref="DiffEditor"/> (side-by-side or inline, editable for local unstaged working-tree diffs
/// with Cmd/Ctrl+S saving) with a unified <see cref="CodeView"/> fallback for truncated or remote diffs.
/// </summary>
public partial class DiffTabView : UserControl, ITabContent
{
    private DiffTabViewModel? _model;
    private int _loadedId;

    public DiffTabView()
    {
        InitializeComponent();
        // Tunnel: Cmd/Ctrl+S works while the right-hand modified CodeEditor inside DiffEditor holds focus.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Diff.PropertyChanged += OnDiffPropertyChanged;
    }

    public DiffTabViewModel? Model => DataContext as DiffTabViewModel;
    public DiffEditor DiffEditorControl => Diff;

    public Task<bool> HasRunningChildAsync() => Task.FromResult(false);
    public Task<bool> HasUnsavedChangesAsync() => Task.FromResult(Model?.HasUnsavedChanges == true);
    public Task CloseAsync() => Task.CompletedTask;
    public Task RestartAsync() => Task.CompletedTask;

    public void FocusContent()
    {
        if (Model?.ShowDiffEditor == true) Diff.Focus();
        else Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
            _model.ReadModifiedEditor = null;
        }
        _loadedId = 0;
        _model = DataContext as DiffTabViewModel;
        if (_model is null)
        {
            Diff.ClearValue(FontFamilyProperty);
            Lines.ClearValue(CodeView.FontFamilyProperty);
            return;
        }
        // The terminal's font when the person chose one; otherwise the theme's monospace stack (the XAML default).
        if (_model.MonoFontFamily is { } family)
        {
            var font = new FontFamily(family);
            Diff.FontFamily = font;
            Lines.FontFamily = font;
        }
        else
        {
            Diff.ClearValue(FontFamilyProperty);
            Lines.ClearValue(CodeView.FontFamilyProperty);
        }
        _model.ReadModifiedEditor = () => Diff.ModifiedText ?? "";
        _model.PropertyChanged += OnModelPropertyChanged;
        LoadDiffIfNeeded();
    }

    private void OnDiffPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == DiffEditor.ModifiedTextProperty) _model?.ModifiedEditorChanged();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiffTabViewModel.DiffLoadId)) LoadDiffIfNeeded();
    }

    private void LoadDiffIfNeeded()
    {
        if (_model is not { ShowDiffEditor: true } model || model.DiffLoadId == _loadedId || model.DiffLoadId == 0) return;
        _loadedId = model.DiffLoadId;
        model.BeginDiffLoad();
        Diff.OriginalText = model.OriginalText;
        Diff.ModifiedText = model.ModifiedText;
        model.DiffLoaded(Diff.ModifiedText ?? "");
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.S || Model is not { CanSave: true } model) return;
        var command = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        if (e.KeyModifiers != command) return;
        e.Handled = true;
        await model.SaveAsync();
    }
}
