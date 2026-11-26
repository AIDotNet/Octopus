using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using TKCode.Arc;

namespace Octopus.App.Settings.Terminal;

public partial class TerminalPageView : UserControl
{
    private TerminalSettingsViewModel? _model;

    public TerminalPageView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WatchModel(DataContext as TerminalSettingsViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        WatchModel(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        WatchModel(TopLevel.GetTopLevel(this) is null ? null : DataContext as TerminalSettingsViewModel);
    }

    private void WatchModel(TerminalSettingsViewModel? model)
    {
        if (ReferenceEquals(_model, model)) return;
        _model?.PropertyChanged -= OnModelChanged;
        _model = model;
        if (model is null) return;
        model.PropertyChanged += OnModelChanged;
        RestoreShellSelection();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalSettingsViewModel.ShellModes)) RestoreShellSelection();
    }

    private void RestoreShellSelection()
    {
        var model = _model;
        // ItemsSource resets selection after the binding has already published the unchanged saved value.
        // Reapply to the control after Arc's deferred entry refresh; another source notification is deduplicated.
        Dispatcher.UIThread.Post(() =>
        {
            if (model is not null && ReferenceEquals(model, _model))
                ShellSelect.SetCurrentValue(Select.SelectedValueProperty, model.ShellMode);
        }, DispatcherPriority.Background);
    }
}
