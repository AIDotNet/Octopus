using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Octopus.App.Appearance;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Shell.Keymap;
using Octopus.Platform;
using Octopus.Storage;
using TKCode.Arc;

namespace Octopus.App.Shell;

/// <summary>
/// The desktop window. There is no title bar of its own: the client area starts at the top edge (the system's window
/// buttons float over it on macOS, caption buttons sit at the top right elsewhere) and the top 40 px row, made of the
/// sidebar head, the dock's tab strips and the right panel's tab row, is what moves the window. Closing it runs the
/// shutdown of <see cref="ShellViewModel"/> first (confirm, save, end the terminals).
/// </summary>
public class MainWindow : ArcWindow
{
    private const double TopRowHeight = 40;

    private readonly ShellViewModel _shell;
    private readonly IAppKeymap _keymap;
    private bool _closing;
    private bool _shutDown;

    public MainWindow()
    {
        Title = "Octopus";
        // The window and taskbar icon on Windows and Linux (the exe's own icon is in the csproj). macOS has no window
        // icon: its Dock icon is set by the platform service.
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Octopus/Assets/Icon/octopus.png")));
        using (var dock = AssetLoader.Open(new Uri("avares://Octopus/Assets/Icon/dock.png")))
        using (var png = new MemoryStream())
        {
            dock.CopyTo(png);
            AppHost.Get<IAppIcon>().Set(png.ToArray());
        }
        Width = 1280;
        Height = 800;
        MinWidth = 720;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // No Arc title bar: the content reaches the window's top edge. The system buttons are centered in a 40 px band.
        IsTitleBarVisible = false;
        ExtendClientAreaTitleBarHeightHint = TopRowHeight;

        AppHost.Get<IWindowAccessor>().Window = this;
        _shell = AppHost.Get<ShellViewModel>();
        _keymap = AppHost.Get<IAppKeymap>();
        Content = new ShellView { DataContext = _shell };

        // Tunnel phase: the shortcuts work while a terminal has the focus.
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // A press in the top row that no tab, button or field took moves the window.
        AddHandler(PointerPressedEvent, OnTopRowPressed, Avalonia.Interactivity.RoutingStrategies.Bubble);
        WindowPlacementTracker.Track(this, AppHost.Get<ISettingsStore>());
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_keymap.Match(e) is { } command)
        {
            e.Handled = true;
            _shell.Execute(command);
        }
    }

    private void OnTopRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.GetPosition(this).Y >= TopRowHeight)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else if (WindowState != WindowState.FullScreen)
        {
            BeginMoveDrag(e);
        }
        e.Handled = true;
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_shutDown)
        {
            return;
        }
        // The first close request only starts the shutdown (it may ask, and ending terminals takes a moment); the window
        // closes for real once everything is saved and ended. Further requests meanwhile change nothing.
        e.Cancel = true;
        if (_closing)
        {
            return;
        }
        _closing = true;
        var quit = true;
        try
        {
            quit = await _shell.ShutDownAsync();
        }
        catch (Exception)
        {
            // Closing must not be blocked by a failing shutdown step.
        }
        _closing = false;
        if (!quit)
        {
            // "Restart and update" was answered with "keep the app open": a later quit must not relaunch.
            AppHost.Get<Octopus.Updates.IUpdateService>().RelaunchAfterInstall = false;
        }
        if (quit)
        {
            _shutDown = true;
            Close();
        }
    }
}
