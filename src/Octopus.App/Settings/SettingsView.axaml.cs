using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Octopus.App.Common;
using Octopus.App.Composition;

namespace Octopus.App.Settings;

/// <summary>
/// The settings, shown as a full page over the main window's content. The shell creates one, sets
/// <see cref="InitialSection"/> when it wants a particular page, and removes it on <see cref="CloseRequested"/>.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        // Own model from the start: this view's bindings are typed to SettingsViewModel, and until it has a DataContext of
        // its own it would inherit the shell's, which they cannot read (an InvalidCastException in every binding).
        DataContext = ActivatorUtilities.CreateInstance<SettingsViewModel>(AppHost.Services);
        InitializeComponent();
    }

    /// <summary>Key of the section to show first: <c>agents</c>, <c>general</c>, <c>terminal</c>, <c>appearance</c>, <c>shortcuts</c>.</summary>
    public string? InitialSection { get; set; }

    /// <summary>Raised by "Back to app" and by Escape.</summary>
    public event EventHandler? CloseRequested;

    protected void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // Normally the model from the constructor; a view that was unloaded (its model let go) and is shown again needs a new one.
        if (DataContext is not SettingsViewModel)
        {
            DataContext = ActivatorUtilities.CreateInstance<SettingsViewModel>(AppHost.Services);
        }
        var viewModel = (SettingsViewModel)DataContext!;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.Show(InitialSection);
        ScrollToTop();
        Focus();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        // Writes what is still being typed, then lets go of the pages.
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            DataContext = null;
            viewModel.Dispose();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled)
        {
            e.Handled = true;
            RequestClose();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.Page))
        {
            ScrollToTop();
        }
    }

    /// <summary>A page always opens at its top (controls that take focus while the page is built must not scroll it).</summary>
    private void ScrollToTop() =>
        UiContext.PostToUi(() => ContentScroll.Offset = default, DispatcherPriority.Loaded);

    private void OnBackClick(object? sender, RoutedEventArgs e) => RequestClose();
}
