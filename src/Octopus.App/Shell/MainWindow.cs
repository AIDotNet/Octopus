using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Octopus.App.Appearance;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Shell.Keymap;
using Octopus.Platform;
using Octopus.Storage;
using SkiaSharp;
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
    private readonly ThemeService _theme;
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

        _theme = AppHost.Get<ThemeService>();
        // No Arc title bar: the content reaches the window's top edge. The system buttons are centered in the top row band.
        IsTitleBarVisible = false;
        ExtendClientAreaTitleBarHeightHint = _theme.Metrics.TopRowHeight;
        _theme.DensityChanged += OnDensityChanged;
        Closed += (_, _) => _theme.DensityChanged -= OnDensityChanged;

        AppHost.Get<IWindowAccessor>().Window = this;
        _shell = AppHost.Get<ShellViewModel>();
        _keymap = AppHost.Get<IAppKeymap>();
        Content = new ShellView { DataContext = _shell };

        // Tunnel phase: the shortcuts work while a terminal has the focus.
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // A press in the top row that no tab, button or field took moves the window.
        AddHandler(PointerPressedEvent, OnTopRowPressed, Avalonia.Interactivity.RoutingStrategies.Bubble);
        Activated += (_, _) => _shell.StarPrompt.SetWindowState(true, Bounds.Width);
        Deactivated += (_, _) => _shell.StarPrompt.SetWindowState(false, Bounds.Width);
        SizeChanged += (_, e) => _shell.StarPrompt.SetWindowState(IsActive, e.NewSize.Width);
        PropertyChanged += OnWindowPropertyChanged;
        WindowPlacementTracker.Track(this, AppHost.Get<ISettingsStore>());
        WindowsImeGuard.Attach(this);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized)
        {
            TrimGraphicsCaches();
        }
    }

    private static void TrimGraphicsCaches()
    {
        try
        {
            SKGraphics.PurgeResourceCache();
            SKGraphics.PurgeFontCache();
        }
        catch (Exception)
        {
            // Non-critical hint; ignore on environments without Skia initialized.
        }
    }

    private void OnDensityChanged(object? sender, UiDensityMetrics metrics) =>
        ExtendClientAreaTitleBarHeightHint = metrics.TopRowHeight;

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e) =>
        _shell.StarPrompt.NotifyUserInput();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        _shell.StarPrompt.NotifyUserInput();
        if (e.Key == Key.Escape && _shell.Account.IsSignInDialogOpen)
        {
            _shell.Account.CloseSignInDialogCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (_keymap.Match(e) is { } command)
        {
            e.Handled = true;
            _shell.Execute(command);
        }
    }

    private void OnTopRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.GetPosition(this).Y >= _theme.Metrics.TopRowHeight)
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
        // The close button with "close to tray" or "ask" on: hide directly, or prompt the user to minimize vs quit.
        // Quitting from the tray menu, the updater's restart, and the system ending the app close the window
        // programmatically or for another reason, and bypass this prompt.
        if (e is { IsProgrammatic: false, CloseReason: WindowCloseReason.WindowClosing })
        {
            var tray = AppHost.Get<TrayService>();
            if (tray.HidesOnClose || tray.AsksOnClose)
            {
                e.Cancel = true;
                await HandleTrayCloseAsync(tray);
                return;
            }
        }
        // The first close request only starts the shutdown (it may ask, and ending terminals takes a moment); the window
        // closes for real once everything is saved and ended. Further requests meanwhile change nothing.
        e.Cancel = true;
        if (_closing)
        {
            return;
        }
        _closing = true;
        await FinishShutDownAsync();
    }

    internal async Task HandleTrayCloseAsync(TrayService tray)
    {
        if (tray.HidesOnClose)
        {
            Hide();
            TrimGraphicsCaches();
            return;
        }
        if (!tray.AsksOnClose || _closing)
        {
            return;
        }
        _closing = true;
        CloseWindowPromptResult choice;
        try
        {
            choice = await AppHost.Get<IDialogService>().AskCloseWindowActionAsync();
        }
        catch (Exception)
        {
            _closing = false;
            return;
        }
        if (choice.Decision == CloseWindowDecision.Cancel)
        {
            _closing = false;
            return;
        }
        if (choice.RememberChoice)
        {
            var action = choice.Decision == CloseWindowDecision.MinimizeToTray
                ? Octopus.Core.Settings.TrayCloseActions.MinimizeToTray
                : Octopus.Core.Settings.TrayCloseActions.Quit;
            AppHost.Get<ISettingsStore>().Update(s =>
            {
                s.Tray.CloseAction = action;
                s.Tray.CloseToTray = action == Octopus.Core.Settings.TrayCloseActions.MinimizeToTray;
            });
        }
        if (choice.Decision == CloseWindowDecision.MinimizeToTray)
        {
            _closing = false;
            Hide();
            TrimGraphicsCaches();
            return;
        }
        await FinishShutDownAsync();
    }

    private async Task FinishShutDownAsync()
    {
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
