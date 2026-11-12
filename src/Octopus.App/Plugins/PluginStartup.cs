using System.Text.Json.Nodes;
using Avalonia.Threading;
using Octopus.Agents;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.Plugins.Runtime;
using Octopus.Storage;
using TKCode.Arc;

namespace Octopus.App.Plugins;

/// <summary>
/// Starts the plugins after the window is up and keeps them informed: the current project (activation events and
/// <c>projects.onDidChangeCurrent</c>), the active tab, installed agents (<c>onAgentInstalled:&lt;id&gt;</c>) and the theme.
/// </summary>
public static class PluginStartup
{
    /// <summary>
    /// False keeps the app from starting plugins and the signal hub by itself (the headless test host, whose tests
    /// start them when they test them).
    /// </summary>
    public static bool AutoStart { get; set; } = true;

    public static async Task StartAsync()
    {
        if (!AutoStart) return;
        try
        {
            var manager = AppHost.Get<PluginManager>();
            _ = AppHost.Get<PluginWorkbench>(); // sets the manager's view opener
            var projects = AppHost.Get<IProjectService>();
            var workbench = AppHost.Get<WorkbenchModule>();
            var context = AppHost.Get<AppProjectContext>();

            var window = AppHost.Get<IWindowAccessor>().Window as Avalonia.Controls.Window;
            var lastInput = DateTimeOffset.Now;

            JsonObject PublishState()
            {
                var state = new JsonObject
                {
                    ["focused"] = window?.IsActive ?? true,
                    ["visible"] = window is null || (window.IsVisible && window.WindowState != Avalonia.Controls.WindowState.Minimized),
                    ["idleSeconds"] = (long)(DateTimeOffset.Now - lastInput).TotalSeconds,
                    ["appearance"] = ArcTheme.Manager.IsDark ? "dark" : "light",
                    ["language"] = PluginText.Language,
                    ["activeItem"] = WorkbenchModule.DescribeActive(projects.Current),
                    ["projectId"] = projects.Current?.Id,
                };
                workbench.SetAppState(state);
                return state;
            }

            if (window is not null)
            {
                // Focus and minimizing drive the plugins' "only when visible" schedules (scheduler.every).
                void StateChanged() => manager.Broadcast("app.state", PublishState());
                window.Activated += (_, _) => StateChanged();
                window.Deactivated += (_, _) => StateChanged();
                window.PropertyChanged += (_, e) =>
                {
                    if (e.Property == Avalonia.Controls.Window.WindowStateProperty) StateChanged();
                };
                window.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, _) => lastInput = DateTimeOffset.Now, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
                window.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (_, _) => lastInput = DateTimeOffset.Now, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            }
            // A sleep shows up as a gap in a one-minute clock; plugins hear app.onDidWake and refresh.
            var tick = DateTimeOffset.Now;
            var wakeTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            wakeTimer.Tick += (_, _) =>
            {
                var now = DateTimeOffset.Now;
                if (now - tick > TimeSpan.FromMinutes(3)) manager.Broadcast("app.wake", null);
                tick = now;
                PublishState();
            };
            wakeTimer.Start();

            PublishState();
            projects.Changed += (_, e) =>
            {
                switch (e.Kind)
                {
                    case ProjectChangeKind.CurrentProject:
                        PublishState();
                        _ = manager.ProjectOpenedAsync(projects.Current?.Path);
                        manager.Broadcast("projects.current", context.Current is { } p
                            ? new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["path"] = p.Path }
                            : null);
                        break;
                    case ProjectChangeKind.Active:
                        PublishState();
                        manager.Broadcast("workspace.activeItem", WorkbenchModule.DescribeActive(projects.Current));
                        break;
                    case ProjectChangeKind.Items when e.Item is { } item && e.Project is { } project:
                        var open = project.Items.Contains(item);
                        manager.Broadcast(open ? "workspace.opened" : "workspace.closed", new JsonObject { ["id"] = item.Id, ["kind"] = item.Kind.ToString().ToLowerInvariant(), ["title"] = item.DisplayTitle });
                        break;
                }
            };
            ArcTheme.Manager.StateChanged += (_, e) =>
            {
                PublishState();
                if (e.WasDark != e.IsDark) manager.Broadcast("theme.changed", new JsonObject { ["appearance"] = e.IsDark ? "dark" : "light" });
            };

            // Claude configurations the user connected (Settings → Plugins): their last status line, then each new one.
            try { AppHost.Get<Octopus.Agents.Signals.ClaudeStatusLineWatcher>().Start(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            // Off the UI thread: reading packages and starting engines is not the window's work.
            await Task.Run(manager.StartAsync);
            AppHost.Get<PluginThemes>().Apply();
            await manager.ProjectOpenedAsync(projects.Current?.Path);

            var scanner = AppHost.Get<IAgentScanner>();
            await FireInstalledAsync(manager, scanner);
            scanner.Changed += (_, _) => _ = FireInstalledAsync(manager, scanner);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Plugins never keep Octopus from starting; the settings page shows each plugin's state and log.
            Console.Error.WriteLine($"Plugins did not start: {e}");
        }
    }

    private static Task FireInstalledAsync(PluginManager manager, IAgentScanner scanner) =>
        Task.WhenAll(scanner.Current.Installations.Select(i => manager.FireAsync($"onAgentInstalled:{i.AgentId}")));
}
