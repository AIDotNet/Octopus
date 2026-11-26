using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Octopus.Agents;
using Octopus.App.Appearance;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Shell;

namespace Octopus.App;

/// <summary>
/// The application. Arc needs nothing here: no base class, no App.axaml and no styles. UseArcApp() (AppSetup.cs)
/// registers the themes. Services come from <see cref="AppHost"/>.
/// </summary>
public class App : Application
{
    public App()
    {
        StartupTrace.Log("app:ctor");
        // The name the platform shows for the process: on macOS Avalonia passes it to [NSProcessInfo setProcessName:]
        // (menu bar, Dock, Activity Monitor, the app menu's Hide/Quit items). Unset, that is "Avalonia Application".
        Name = "Octopus";
    }

    public override void OnFrameworkInitializationCompleted()
    {
        StartupTrace.Log("app:initialize:begin");
        // Created through its class, not loaded by URI: the URI lookup finds the compiled XAML by reflection, which a
        // trimmed (NativeAOT) build does not keep.
        Styles.Add(new Common.AppStyles());
        StartupTrace.Log("app:initialize:styles");
        AppHost.Get<ThemeService>().Track();
        StartupTrace.Log("app:initialize:theme");
        // The projects are read before the first frame; the agent scan runs in the background (its cached result first).
        // Task.Run: this thread has Avalonia's SynchronizationContext, so awaiting LoadAsync here would post its
        // continuation back to this (blocked) thread and deadlock before the window is ever created.
        var projects = AppHost.Get<IProjectService>();
        Task.Run(projects.LoadAsync).GetAwaiter().GetResult();
        StartupTrace.Log("app:initialize:projects-loaded", projects.Current?.Name);
        AppHost.Get<Bots.IBotService>().Load();
        StartupTrace.Log("app:initialize:bots-loaded");
        _ = StartAgentScanAsync();
        // Ends the ssh processes that a run that did not end properly left, and starts the automatic tunnels of the project.
        AppHost.Get<Octopus.App.Services.ITunnelService>().Initialize();
        StartupTrace.Log("app:initialize:tunnels");
        // The signal channel's private endpoint (design/plugins/11 §1.3); agents launched from now on report to it.
        if (Plugins.PluginStartup.AutoStart)
        {
            try { AppHost.Get<Octopus.Agents.Signals.ISignalHub>().Start(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
            {
                StartupTrace.LogException("app:initialize:signal-hub", e);
            }
            try
            {
                // Initialize coordinator first so the MCP server has its handler attached before accepting connections.
                _ = AppHost.Get<Collab.ICollabCoordinator>();
                AppHost.Get<Collab.CollabMcpServer>().Start();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
            {
                StartupTrace.LogException("app:initialize:collab-mcp", e);
            }
            try
            {
                // Bots' own schedules (and the look-around / memory-maintenance switches), plus the bot-only MCP tools to make them.
                AppHost.Get<Bots.BotScheduleMcpServer>().Start();
                AppHost.Get<Bots.BotScheduler>().Start();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
            {
                StartupTrace.LogException("app:initialize:bot-scheduler", e);
            }
        }
        StartupTrace.Log("app:initialize:signal-hub");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            StartupTrace.Log("app:initialize:main-window");
            var protocolServer = ProtocolActivation.StartServer(uri => HandleProtocolUri(desktop, uri));
            StartupTrace.Log("app:initialize:protocol-server");
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e is ProtocolActivatedEventArgs { Uri: { } uri }) HandleProtocolUri(desktop, uri);
                };
            }
            AppHost.Get<Shell.TrayService>().Start(this);
            StartupTrace.Log("app:initialize:tray");
            AppHost.Get<Activity.AgentAttentionNotifier>().Start();
            StartupTrace.Log("app:initialize:attention-notifier");
            // Projects on other computers: kept in line with their hosts from now on; the host of the one on screen is
            // connected in the background.
            var remoteProjects = AppHost.Get<Remote.RemoteProjects>();
            if (projects.Current is { RemoteHost: { } remoteHost }) _ = Task.Run(() => remoteProjects.ConnectQuietlyAsync(remoteHost));
            // Remote access that was left on listens again from the start; off, nothing of it is even created.
            if (AppHost.Get<Octopus.Storage.ISettingsStore>().Current.Remote is { LanEnabled: true } or { RelayHostEnabled: true })
            {
                _ = Task.Run(AppHost.Get<Remote.RemoteHostRuntime>().ApplyAsync);
            }
            // Plugins start once the window exists: views, dialogs and indicators need it.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                StartupTrace.Log("app:background:plugins:begin");
                _ = Plugins.PluginStartup.StartAsync();
                StartupTrace.Log("app:background:plugins:end");
            }, Avalonia.Threading.DispatcherPriority.Background);
            // The updater starts once the window is up: its first check waits a while anyway (design/10-自动更新.md §7).
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                StartupTrace.Log("app:background:updates:begin");
                Updates.UpdateLifecycle.Start();
                StartupTrace.Log("app:background:updates:end");
            }, Avalonia.Threading.DispatcherPriority.Background);
            // Scheduled branch workspace cleanup & activity tracking starts after the window is up.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                StartupTrace.Log("app:background:cleanup:begin");
                AppHost.Get<Octopus.App.Services.Workspaces.IWorkspaceCleanupService>().Start();
                StartupTrace.Log("app:background:cleanup:end");
            }, Avalonia.Threading.DispatcherPriority.Background);
            // Non-intrusive GitHub Star reminder tracking starts once the window is up.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                StartupTrace.Log("app:background:star-prompt:begin");
                AppHost.Get<StarPrompt.StarPromptViewModel>().Start();
                StartupTrace.Log("app:background:star-prompt:end");
            }, Avalonia.Threading.DispatcherPriority.Background);
            StartupTrace.Log("app:initialize:scheduled-background-work");
            // SIGTERM (kill, logout, an installer) quits like the Quit menu item, so Exit below still runs and saves.
            StartupTrace.SetShutdownHandler(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.Shutdown()));
            // The window is gone and everything saved: hand a ready update to its installer, then let go of what the
            // services hold (file watchers, timers).
            desktop.Exit += (_, _) =>
            {
                StartupTrace.Log("desktop:exit");
                protocolServer.Dispose();
                // No ssh process of a tunnel outlives the app (an installer that starts now may replace the files they run from).
                AppHost.Get<Octopus.App.Services.ITunnelService>().StopAll();
                // Settings are written a moment after each change: the last one goes to disk before an installer starts.
                AppHost.Get<Octopus.Storage.ISettingsStore>().Flush();
                Updates.UpdateLifecycle.InstallOnExit();
                AppHost.Reset();
            };
        }
        base.OnFrameworkInitializationCompleted();
        StartupTrace.Log("app:initialize:end");
    }

    private static void HandleProtocolUri(IClassicDesktopStyleApplicationLifetime desktop, Uri uri)
    {
        if (!string.Equals(uri.Scheme, "octopus", StringComparison.OrdinalIgnoreCase)) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (desktop.MainWindow is not { } window) return;
            if (window.WindowState == Avalonia.Controls.WindowState.Minimized)
            {
                window.WindowState = Avalonia.Controls.WindowState.Normal;
            }
            window.Show();
            window.Activate();
            if (!OperatingSystem.IsMacOS())
            {
                var wasTopmost = window.Topmost;
                window.Topmost = true;
                window.Topmost = wasTopmost;
            }

            if (Bots.BotShareCodec.IsBotImportUri(uri))
            {
                _ = HandleBotImportUriAsync(uri);
            }
        });
    }

    private static async Task HandleBotImportUriAsync(Uri uri)
    {
        if (!Bots.BotShareCodec.TryDecode(uri, out var payload, out _) || payload is null)
        {
            return;
        }
        var dialogs = AppHost.Get<IDialogService>();
        if (!await dialogs.ConfirmBotImportAsync(payload))
        {
            return;
        }
        AppHost.Get<Bots.IBotService>().ImportBot(payload);
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
