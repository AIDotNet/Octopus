using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Octopus.App.Workspace;

namespace Octopus.App.Files;

/// <summary>
/// A file tab: the frame around the viewer of the file's kind. It passes the save shortcut and the unsaved state to the
/// window; everything else (the info bar, the content) belongs to the viewer.
/// </summary>
public partial class FileTabView : UserControl, ITabContent
{
    public FileTabView()
    {
        InitializeComponent();
        // Tunnel: the shortcut works while the editor inside the viewer has the focus and would take the key.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    public FileTabViewModel? Model => DataContext as FileTabViewModel;

    /// <summary>The view of the viewer that is showing (a <see cref="Control"/> chosen by <see cref="FileViewers"/>).</summary>
    public Control? ViewerView => Content as Control;

    public Task<bool> HasRunningChildAsync() => Task.FromResult(false);
    public Task<bool> HasUnsavedChangesAsync() => Task.FromResult(Model?.HasUnsavedChanges == true);
    public Task CloseAsync() => Task.CompletedTask;
    public Task RestartAsync() => Task.CompletedTask;

    public void FocusContent()
    {
        if (Content is IFileViewerView viewer) viewer.FocusViewer();
        else Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Content = Model is { } model ? FileViewers.For(model.Kind).CreateView(model.Viewer) : null;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.S || Model is not { CanSave: true } model) return;
        // The platform's own "command" modifier (Cmd on macOS, Ctrl elsewhere): no operating system is asked here.
        var command = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        if (e.KeyModifiers != command) return;
        e.Handled = true;
        await model.SaveAsync();
    }
}
