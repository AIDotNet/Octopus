using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Octopus.App.Localization;
using Octopus.App.Activity;
using Octopus.App.Services;
using Octopus.Storage;
using Octopus.Updates;

namespace Octopus.App.Shell;

/// <summary>
/// The icon in the notification area (the menu bar on macOS): click it to bring the window back, and its menu has Show,
/// Check for updates and Quit. With "close to tray" on, the window's close button only hides the window
/// (<see cref="HidesOnClose"/>); terminals and agents keep running. Quitting always goes through the window's normal
/// close, so the question about running programs is still asked.
/// </summary>
internal sealed class TrayService(ISettingsStore settings, IWindowAccessor windows, IUpdateService updates, Activity.IItemActivityService activity, IProjectService projects)
{
    private TrayIcon? _icon;
    private NativeMenuItem? _show, _check, _quit;

    /// <summary>The close button hides the window instead of closing it.</summary>
    public bool HidesOnClose => _icon is { IsVisible: true } && settings.Current.Tray is { Enabled: true, CloseToTray: true };

    public void Start(Application app)
    {
        if (_icon is not null) return;
        using (var png = AssetLoader.Open(new Uri("avares://Octopus/Assets/Icon/octopus.png")))
        {
            _icon = new TrayIcon { Icon = new WindowIcon(png), ToolTipText = "Octopus" };
        }
        _show = new NativeMenuItem();
        _show.Click += (_, _) => ShowWindow();
        _check = new NativeMenuItem();
        _check.Click += (_, _) =>
        {
            ShowWindow();
            _ = updates.CheckAsync(manual: true);
        };
        _quit = new NativeMenuItem();
        _quit.Click += (_, _) => Quit();
        _icon.Menu = new NativeMenu { _show, _check, new NativeMenuItemSeparator(), _quit };
        // Windows and Linux: a click on the icon itself. On macOS the click opens the menu instead.
        _icon.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(app, new TrayIcons { _icon });
        Refresh();
        settings.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        activity.Changed += (_, _) => RefreshTip();

        // macOS: a click on the Dock icon while the window is hidden brings it back.
        if (app.TryGetFeature<IActivatableLifetime>() is { } lifetime)
        {
            lifetime.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen) Dispatcher.UIThread.Post(ShowWindow);
            };
        }
    }

    /// <summary>Takes the settings: whether the icon shows, and the language of its menu.</summary>
    private void Refresh()
    {
        if (_icon is null) return;
        _icon.IsVisible = settings.Current.Tray.Enabled;
        _show!.Header = AppLangResources.Get(AppLangResourceKind.TrayShow);
        _check!.Header = AppLangResources.Get(AppLangResourceKind.SettingsUpdateCheck);
        _quit!.Header = AppLangResources.Get(AppLangResourceKind.TrayQuit);
    }

    /// <summary>The tooltip says how many agents wait for the user, so a hidden window still tells.</summary>
    private void RefreshTip()
    {
        if (_icon is null) return;
        var waiting = projects.Projects.SelectMany(p => p.Items).Count(i => activity.Of(i.Id) == ItemActivity.Waiting);
        _icon.ToolTipText = waiting == 0 ? "Octopus" : AppLangResources.Format(AppLangResourceKind.TrayWaiting, waiting);
    }

    /// <summary>Shows the window again, in front, from the tray, a minimized state or hiding.</summary>
    public void ShowWindow()
    {
        if (windows.Window is not Window window) return;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        // A new version found while the window was hidden is told about now.
        Updates.UpdateLifecycle.PresentNotes();
    }

    private void Quit()
    {
        if (windows.Window is not Window window) return;
        // Visible first: the question about running programs needs a window to sit in.
        ShowWindow();
        window.Close();
    }
}
