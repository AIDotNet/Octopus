using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Octopus.App.Plugins.Rendering;
using Octopus.App.Services;
using Octopus.Core.Plugins;
using Octopus.Core.Projects;
using Octopus.Plugins.Runtime;
using Octopus.Storage;

namespace Octopus.App.Plugins;

/// <summary>Something on screen that shows a plugin tree: a tab, a dialog, an indicator's popover.</summary>
public interface IPluginSurface
{
    /// <summary>The plugin rendered again (<c>view.update</c>). Called on the UI thread.</summary>
    void Update(JsonNode? tree);

    /// <summary>Title, icon, badge or dirty flag changed (<c>view.chrome</c>). Called on the UI thread.</summary>
    void Chrome(JsonObject change) { }

    /// <summary>The render function threw (<c>view.error</c>). Called on the UI thread.</summary>
    void Error(string message) { }

    /// <summary>The plugin asked to keep its state (<c>view.saveState</c>). Called on the UI thread.</summary>
    void SaveState(JsonNode? state) { }
}

/// <summary>
/// The app side of plugin interfaces: which surface shows which view instance, opening view tabs, and running events
/// back into the plugin. Everything a plugin sends arrives on its own thread; it is handed to the UI thread here.
/// </summary>
public sealed class PluginWorkbench
{
    private readonly ConcurrentDictionary<(string Plugin, string Instance), IPluginSurface> _surfaces = new();
    private readonly Dictionary<(string Plugin, string Instance), PendingTree> _pendingTrees = new();
    private readonly IProjectService _projects;
    private readonly IItemOpener _opener;
    private readonly IPickerService _pickers;

    public PluginWorkbench(PluginManager manager, IProjectService projects, IItemOpener opener, IPickerService pickers)
    {
        Manager = manager;
        _projects = projects;
        _opener = opener;
        _pickers = pickers;
        manager.ViewOpener = (host, view, args) => Dispatcher.UIThread.InvokeAsync(() =>
        {
            var options = args.Count > 0 ? args[0] as JsonObject : null;
            OpenView(host, view, options?["state"], options?["title"]?.GetValue<string>());
        }).GetTask();
    }

    public PluginManager Manager { get; }

    // ---------------------------------------------------------------- surfaces

    public void Attach(string pluginId, string instanceId, IPluginSurface surface) => _surfaces[(pluginId, instanceId)] = surface;

    public void Detach(string pluginId, string instanceId, IPluginSurface surface) =>
        _surfaces.TryRemove(new KeyValuePair<(string, string), IPluginSurface>((pluginId, instanceId), surface));

    /// <summary>Runs <paramref name="action"/> on the UI thread for the surface showing the instance, if there is one.</summary>
    internal void Post(string pluginId, string instanceId, Action<IPluginSurface> action)
    {
        if (!_surfaces.TryGetValue((pluginId, instanceId), out var surface)) return;
        Dispatcher.UIThread.Post(() =>
        {
            // The surface may have closed while this waited.
            if (_surfaces.TryGetValue((pluginId, instanceId), out var current) && ReferenceEquals(current, surface)) action(surface);
        });
    }

    /// <summary>
    /// Shows a new tree (<c>view.update</c>) on the UI thread. Only the latest tree counts: while one is waiting for the
    /// UI thread a newer one replaces it, so a busy UI thread renders once instead of every stale tree in turn.
    /// </summary>
    internal void PostTree(string pluginId, string instanceId, JsonNode? tree)
    {
        var key = (pluginId, instanceId);
        if (!_surfaces.TryGetValue(key, out var surface)) return;
        PendingTree pending;
        lock (_pendingTrees)
        {
            if (_pendingTrees.TryGetValue(key, out var waiting) && ReferenceEquals(waiting.Surface, surface))
            {
                waiting.Tree = tree;
                return;
            }
            pending = new PendingTree(surface) { Tree = tree };
            _pendingTrees[key] = pending;
        }
        Dispatcher.UIThread.Post(() =>
        {
            JsonNode? latest;
            lock (_pendingTrees)
            {
                // From here on a newer tree needs a post of its own.
                if (_pendingTrees.TryGetValue(key, out var waiting) && ReferenceEquals(waiting, pending)) _pendingTrees.Remove(key);
                latest = pending.Tree;
            }
            // The surface may have closed while this waited.
            if (_surfaces.TryGetValue(key, out var current) && ReferenceEquals(current, surface)) surface.Update(latest);
        });
    }

    /// <summary>The newest tree for a surface, waiting for the UI thread.</summary>
    private sealed class PendingTree(IPluginSurface surface)
    {
        public IPluginSurface Surface { get; } = surface;
        public JsonNode? Tree { get; set; }
    }

    /// <summary>The event sink for one view instance: control events become <c>view.event</c> calls.</summary>
    public IUiEventSink SinkFor(PluginHost host, string instanceId) => new SurfaceSink(this, host, instanceId);

    // ---------------------------------------------------------------- views

    /// <summary>The enabled plugin that contributes view <paramref name="viewId"/>, with the contribution.</summary>
    public (PluginHost Host, ViewContribution View)? FindView(string? viewId)
    {
        if (viewId is null) return null;
        foreach (var host in Manager.Hosts)
        {
            if (host.Manifest.Contributes.Views.FirstOrDefault(v => v.Id == viewId) is { } view) return (host, view);
        }
        return null;
    }

    /// <summary>Every view that asks to be in the launcher, with its plugin.</summary>
    public IEnumerable<(PluginHost Host, ViewContribution View)> LauncherViews =>
        Manager.Hosts.Where(h => h.State is not (PluginHostState.Failed or PluginHostState.Stopped))
            .SelectMany(h => h.Manifest.Contributes.Views.Where(v => v.Launcher).Select(v => (h, v)));

    /// <summary>
    /// Opens a view as a tab of the current project, or shows the one that is open already when the view is a
    /// singleton (per project or for the whole app). Returns the tab's item, null without a project.
    /// </summary>
    public ProjectItem? OpenView(PluginHost host, ViewContribution view, JsonNode? state = null, string? title = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        var existing = view.Singleton switch
        {
            ViewSingleton.Project => _projects.Current?.Items.FirstOrDefault(i => i.Kind == ProjectItemKind.Plugin && i.ViewId == view.Id),
            ViewSingleton.App => _projects.Projects.SelectMany(p => p.Items).FirstOrDefault(i => i.Kind == ProjectItemKind.Plugin && i.ViewId == view.Id),
            _ => null,
        };
        if (existing is not null)
        {
            _projects.ActivateItem(existing.Id);
            return existing;
        }
        return _opener.OpenView(view.Id, title ?? LocalizedTitle(view), StateText(state));
    }

    /// <summary>The plugin's name in the app's language.</summary>
    public static string NameOf(PluginHost host) => host.Manifest.Localized.Pick(PluginText.Language, "name") ?? host.Manifest.Name;

    public static string LocalizedTitle(ViewContribution view) => view.Localized.Pick(PluginText.Language, "title") ?? view.Title;

    /// <summary>The JSON a plugin gave as view state, if it fits (64 KB, design/plugins/06-标签与渲染.md §4).</summary>
    internal static string? StateText(JsonNode? state)
    {
        if (state is null) return null;
        var text = state.ToJsonString();
        return text.Length <= 64 * 1024 ? text : null;
    }

    internal static JsonNode? ParseState(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal async Task<string?> PickPathAsync(bool folder)
    {
        var start = _projects.Current?.Path;
        return folder ? await _pickers.PickFolderAsync() : await _pickers.PickFileAsync(start);
    }

    /// <summary>Calls a plugin and logs instead of throwing: control events must never break the UI.</summary>
    internal static async Task<string?> InvokeQuietlyAsync(PluginHost host, string handler, JsonArray args, TimeSpan? timeout = null)
    {
        try
        {
            return await host.InvokeAsync(handler, args, timeout).ConfigureAwait(false);
        }
        catch (Exception e) when (e is PluginInvokeException or TimeoutException or OperationCanceledException)
        {
            host.Write(PluginLogLevel.Warn, $"{handler}: {e.Message}");
            return null;
        }
    }

    internal void ExecuteCommand(PluginHost? caller, string commandId, JsonArray args) => _ = ExecuteCommandAsync(caller, commandId, args);

    private async Task ExecuteCommandAsync(PluginHost? caller, string commandId, JsonArray args)
    {
        try
        {
            await Manager.ExecuteCommandAsync(commandId, args).ConfigureAwait(false);
        }
        catch (Exception e) when (e is PluginApiException or PluginInvokeException or TimeoutException)
        {
            caller?.Write(PluginLogLevel.Warn, $"Command {commandId}: {e.Message}");
        }
    }

    private sealed class SurfaceSink(PluginWorkbench workbench, PluginHost host, string instanceId) : IUiEventSink
    {
        public void Fire(string handlerId, JsonArray args) =>
            _ = InvokeQuietlyAsync(host, "view.event", [JsonValue.Create(instanceId), JsonValue.Create(handlerId), args]);

        public void ExecuteCommand(string commandId, JsonArray args) => workbench.ExecuteCommand(host, commandId, args);

        public Task<string?> PickPathAsync(bool folder, string? title) => workbench.PickPathAsync(folder);
    }
}

/// <summary>Plugin interface strings in the two languages Octopus ships; kept out of the app's XLIFF files on purpose
/// while the plugin system is a preview (design/plugins/10-分期与风险.md).</summary>
internal static class PluginText
{
    /// <summary>The language chosen in settings; Simplified Chinese when none is (as <c>AppLanguages.Initial</c>).</summary>
    public static string Language =>
        Composition.AppHost.Get<ISettingsStore>().Current.Appearance.Language is { Length: > 0 } saved ? saved : "zh-CN";

    private static bool Chinese => Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public static string Get(string zh, string en) => Chinese ? zh : en;
}
