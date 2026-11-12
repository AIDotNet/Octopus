using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Octopus.App.Appearance;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.Agents;

namespace Octopus.App;

/// <summary>
/// The application. Arc needs nothing here: no base class, no App.axaml and no styles. UseArcApp() (AppSetup.cs)
/// registers the themes. Services come from <see cref="AppHost"/>.
/// </summary>
public class App : Application
{
    public App()
    {
        // The name the platform shows for the process: on macOS Avalonia passes it to [NSProcessInfo setProcessName:]
        // (menu bar, Dock, Activity Monitor, the app menu's Hide/Quit items). Unset, that is "Avalonia Application".
        Name = "Octopus";
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Styles.Add(new StyleInclude(new Uri("avares://Octopus/")) { Source = new Uri("avares://Octopus/Common/AppStyles.axaml") });
        AppHost.Get<ThemeService>().Track();
        // The projects are read before the first frame; the agent scan runs in the background (its cached result first).
        // Task.Run: this thread has Avalonia's SynchronizationContext, so awaiting LoadAsync here would post its
        // continuation back to this (blocked) thread and deadlock before the window is ever created.
        var projects = AppHost.Get<IProjectService>();
        Task.Run(projects.LoadAsync).GetAwaiter().GetResult();
        _ = StartAgentScanAsync();
        // The signal channel's private endpoint (design/plugins/11 §1.3); agents launched from now on report to it.
        if (Plugins.PluginStartup.AutoStart)
        {
            try { AppHost.Get<Octopus.Agents.Signals.ISignalHub>().Start(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException) { }
        }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            // Plugins start once the window exists: views, dialogs and indicators need it.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = Plugins.PluginStartup.StartAsync(), Avalonia.Threading.DispatcherPriority.Background);
            // The updater starts once the window is up: its first check waits a while anyway (design/10-自动更新.md §7).
            Avalonia.Threading.Dispatcher.UIThread.Post(Updates.UpdateLifecycle.Start, Avalonia.Threading.DispatcherPriority.Background);
            // The window is gone and everything saved: hand a ready update to its installer, then let go of what the
            // services hold (file watchers, timers).
            desktop.Exit += (_, _) =>
            {
                Updates.UpdateLifecycle.InstallOnExit();
                AppHost.Reset();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartAgentScanAsync()
    {
        try
        {
            var scanner = AppHost.Get<IAgentScanner>();
            await scanner.LoadCacheAsync();
            await scanner.ScanAsync();
        }
        catch
        {
            // A failed scan only means the launcher lists no agents.
        }
    }
}
