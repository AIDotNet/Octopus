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
        // Created through its class, not loaded by URI: the URI lookup finds the compiled XAML by reflection, which a
        // trimmed (NativeAOT) build does not keep.
        Styles.Add(new Common.AppStyles());
        AppHost.Get<ThemeService>().Track();
        // The projects are read before the first frame; the agent scan runs in the background (its cached result first).
        // Task.Run: this thread has Avalonia's SynchronizationContext, so awaiting LoadAsync here would post its
        // continuation back to this (blocked) thread and deadlock before the window is ever created.
        var projects = AppHost.Get<IProjectService>();
        Task.Run(projects.LoadAsync).GetAwaiter().GetResult();
        _ = StartAgentScanAsync();
        // Ends the ssh processes that a run that did not end properly left, and starts the automatic tunnels of the project.
        AppHost.Get<Octopus.App.Services.ITunnelService>().Initialize();
        // The signal channel's private endpoint (design/plugins/11 §1.3); agents launched from now on report to it.
        if (Plugins.PluginStartup.AutoStart)
        {
            try { AppHost.Get<Octopus.Agents.Signals.ISignalHub>().Start(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException) { }
        }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            AppHost.Get<Shell.TrayService>().Start(this);
            AppHost.Get<Activity.AgentAttentionNotifier>().Start();
            // Plugins start once the window exists: views, dialogs and indicators need it.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = Plugins.PluginStartup.StartAsync(), Avalonia.Threading.DispatcherPriority.Background);
            // The updater starts once the window is up: its first check waits a while anyway (design/10-自动更新.md §7).
            Avalonia.Threading.Dispatcher.UIThread.Post(Updates.UpdateLifecycle.Start, Avalonia.Threading.DispatcherPriority.Background);
            // The window is gone and everything saved: hand a ready update to its installer, then let go of what the
            // services hold (file watchers, timers).
            desktop.Exit += (_, _) =>
            {
                // No ssh process of a tunnel outlives the app (an installer that starts now may replace the files they run from).
                AppHost.Get<Octopus.App.Services.ITunnelService>().StopAll();
                // Settings are written a moment after each change: the last one goes to disk before an installer starts.
                AppHost.Get<Octopus.Storage.ISettingsStore>().Flush();
                Updates.UpdateLifecycle.InstallOnExit();
                AppHost.Reset();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static readonly TimeSpan ScanDelayWithCache = TimeSpan.FromSeconds(4);

    private static async Task StartAgentScanAsync()
    {
        try
        {
            var scanner = AppHost.Get<IAgentScanner>();
            await scanner.LoadCacheAsync();
            // With a cached result the launcher already lists the agents: the rescan (one version run per installed agent,
            // a Node start each for most CLIs) waits until the window is up instead of competing with the first frames.
            if (scanner.Current.ScannedAt != default) await Task.Delay(ScanDelayWithCache);
            await scanner.ScanAsync();
        }
        catch
        {
            // A failed scan only means the launcher lists no agents.
        }
    }
}
