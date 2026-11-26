using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Octopus.App.Plugins.Rendering;
using Octopus.Core.Plugins;
using Octopus.Core.Projects;
using Octopus.Plugins.Runtime;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Plugins;

/// <summary>Live metadata (badge text, color, tooltip) for one plugin-contributed right panel.</summary>
public sealed class PluginPanelItem(PluginHost host, PanelContribution contribution)
{
    public PluginHost Host { get; } = host;
    public PanelContribution Contribution { get; } = contribution;
    public string Id => Contribution.Id;
    public string TabKey => PluginPanels.KeyFor(Contribution.Id);
    public string? Badge { get; internal set; }
    public string? BadgeColor { get; internal set; }
    public string? Tooltip { get; internal set; }

    public string DisplayTitle => Contribution.Localized.Pick(PluginText.Language, "title") ?? Contribution.Title;
}

/// <summary>
/// Registry of all right-panel tabs contributed by enabled plugins (<c>contributes.panels</c>). Tracks live badge
/// updates and open requests so <see cref="Explorer.ExplorerPanelView"/> stays in sync with plugin lifecycles.
/// </summary>
public sealed class PluginPanels
{
    public const string TabKeyPrefix = "panel:";

    private readonly PluginManager _manager;
    private readonly ConcurrentDictionary<string, (string? Text, string? Color, string? Tooltip)> _badges = new(StringComparer.Ordinal);
    private List<PluginPanelItem> _items = [];

    public PluginPanels(PluginManager manager)
    {
        _manager = manager;
        _manager.HostChanged += (_, _) => Rebuild();
        _manager.RegistrationsChanged += (_, _) => Rebuild();
        Rebuild();
    }

    public IReadOnlyList<PluginPanelItem> Items => _items;

    /// <summary>Raised on the UI thread when the panel list or a panel badge changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on the UI thread when a plugin calls <c>panels.open(panelId)</c>.</summary>
    public event EventHandler<string>? OpenRequested;

    public static string KeyFor(string panelId) => TabKeyPrefix + panelId;

    public static bool TryParseKey(string? tabKey, out string panelId)
    {
        if (tabKey is not null && tabKey.StartsWith(TabKeyPrefix, StringComparison.Ordinal) && tabKey.Length > TabKeyPrefix.Length)
        {
            panelId = tabKey[TabKeyPrefix.Length..];
            return true;
        }
        panelId = "";
        return false;
    }

    public PluginPanelItem? Find(string panelId) =>
        _items.FirstOrDefault(i => string.Equals(i.Id, panelId, StringComparison.Ordinal));

    public void SetBadge(PluginHost plugin, string panelId, string? text, string? color, string? tooltip)
    {
        if (!plugin.Manifest.Contributes.Panels.Any(p => p.Id == panelId)) return;
        var trimmed = string.IsNullOrWhiteSpace(text) ? null : (text.Length > 16 ? text[..15] + "…" : text);
        _badges[panelId] = (trimmed, color, tooltip);
        Dispatcher.UIThread.Post(() =>
        {
            if (Find(panelId) is { } item)
            {
                item.Badge = trimmed;
                item.BadgeColor = color;
                item.Tooltip = tooltip;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    public void RemoveBadgesFor(PluginHost plugin)
    {
        var any = false;
        foreach (var p in plugin.Manifest.Contributes.Panels)
        {
            if (_badges.TryRemove(p.Id, out _)) any = true;
        }
        if (any) Dispatcher.UIThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public bool RequestOpen(string panelId)
    {
        if (Find(panelId) is null) return false;
        Dispatcher.UIThread.Post(() => OpenRequested?.Invoke(this, panelId));
        return true;
    }

    /// <summary>Checks whether <paramref name="panel"/> should appear in the current explorer context.</summary>
    public static bool MatchesContext(PanelContribution panel, bool hasProject, bool isGitRepo, string deviceKey)
    {
        if (panel.Scope == PanelScope.Project && !hasProject) return false;
        if (string.IsNullOrWhiteSpace(panel.When) || panel.When.Equals("any", StringComparison.OrdinalIgnoreCase)) return true;

        var (kind, _) = DeviceKeys.Parse(deviceKey);
        var kindName = kind switch
        {
            ProjectLocationKind.Ssh => "ssh",
            ProjectLocationKind.Remote => "remote",
            _ => "local",
        };
        return EvaluateWhen(panel.When, hasProject, isGitRepo, kindName);
    }

    private static bool EvaluateWhen(string expr, bool hasProject, bool isGitRepo, string deviceKind)
    {
        // Supports simple OR (||) of AND (&&) clauses with optional negation (!).
        foreach (var orPart in expr.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var allTrue = true;
            foreach (var andPart in orPart.Split("&&", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var clause = andPart.Trim();
                var negate = clause.StartsWith('!');
                if (negate) clause = clause[1..].Trim();
                var matched = EvaluateAtom(clause, hasProject, isGitRepo, deviceKind);
                if (negate) matched = !matched;
                if (!matched) { allTrue = false; break; }
            }
            if (allTrue) return true;
        }
        return false;
    }

    private static bool EvaluateAtom(string atom, bool hasProject, bool isGitRepo, string deviceKind)
    {
        if (atom.Contains("==", StringComparison.Ordinal))
        {
            var parts = atom.Split("==", 2, StringSplitOptions.TrimEntries);
            if (parts[0].Equals("deviceKind", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(deviceKind, parts[1].Trim('\'', '"'), StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        if (atom.Contains("!=", StringComparison.Ordinal))
        {
            var parts = atom.Split("!=", 2, StringSplitOptions.TrimEntries);
            if (parts[0].Equals("deviceKind", StringComparison.OrdinalIgnoreCase))
            {
                return !string.Equals(deviceKind, parts[1].Trim('\'', '"'), StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }
        return atom.ToLowerInvariant() switch
        {
            "true" or "any" => true,
            "false" => false,
            "hasproject" => hasProject,
            "isgitrepo" => isGitRepo,
            "islocal" => deviceKind == "local",
            "isssh" => deviceKind == "ssh",
            "isremote" => deviceKind == "remote",
            _ => true,
        };
    }

    private void Rebuild()
    {
        var list = _manager.Hosts
            .Where(h => h.State is not (PluginHostState.Failed or PluginHostState.Stopped))
            .SelectMany(h => h.Manifest.Contributes.Panels.Select(p =>
            {
                var item = new PluginPanelItem(h, p);
                if (_badges.TryGetValue(p.Id, out var b))
                {
                    item.Badge = b.Text;
                    item.BadgeColor = b.Color;
                    item.Tooltip = b.Tooltip;
                }
                return item;
            }))
            .OrderBy(i => i.Contribution.Order)
            .ThenBy(i => i.Host.Id, StringComparer.Ordinal)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();

        Dispatcher.UIThread.Post(() =>
        {
            _items = list;
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }
}

/// <summary>
/// Hosts one plugin-rendered right panel inside <see cref="Explorer.ExplorerPanelView"/>. Lazily activates the plugin
/// when first shown, pauses updates via <c>view.visibility</c> when hidden, and recovers automatically after a crash.
/// </summary>
public sealed class PluginPanelHost : UserControl, IPluginSurface, IDisposable
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

    private readonly string _panelId;
    private readonly PluginWorkbench _workbench;
    private readonly PluginPanels _panels;
    private readonly IPluginProjectContext _projects;
    private readonly Border _host = new();
    private PluginHost? _plugin;
    private UiRenderer? _renderer;
    private Control? _rendered;
    private bool _opening;
    private bool _open;
    private bool _disposed;
    private bool _panelVisible;
    private JsonNode? _savedState;

    public PluginPanelHost(string panelId, PluginWorkbench workbench, PluginPanels panels, IPluginProjectContext projects)
    {
        _panelId = panelId;
        _workbench = workbench;
        _panels = panels;
        _projects = projects;
        Content = _host;
        _workbench.Manager.HostChanged += OnHostChanged;
    }

    public string InstanceId => $"panel:{_panelId}";

    /// <summary>Called by the explorer when this panel tab becomes active/inactive or the right sidebar opens/closes.</summary>
    public void SetPanelVisible(bool visible)
    {
        if (_panelVisible == visible) return;
        _panelVisible = visible;
        if (visible && !_open && !_opening && !_disposed)
        {
            _ = OpenAsync();
        }
        if (_open && _plugin is { } plugin)
        {
            _ = PluginWorkbench.InvokeQuietlyAsync(plugin, "view.visibility", new JsonArray(JsonValue.Create(InstanceId), JsonValue.Create(visible)));
        }
    }

    private async Task OpenAsync()
    {
        _opening = true;
        try
        {
            ShowLoading();
            var item = _panels.Find(_panelId);
            if (item is null)
            {
                ShowUnavailable(PluginText.Get("提供此面板的插件未启用。", "The plugin providing this panel is not enabled."), _panelId);
                return;
            }
            var plugin = await _workbench.Manager.EnsureActiveAsync(item.Host.Id);
            if (plugin is null)
            {
                ShowError(PluginText.Get($"插件 {PluginWorkbench.NameOf(item.Host)} 没有运行。", $"The plugin {PluginWorkbench.NameOf(item.Host)} is not running."), item.Host.LastError);
                return;
            }
            if (!plugin.Providers.Contains(("panel", _panelId)))
            {
                ShowError(
                    PluginText.Get("插件没有为这个面板注册实现。", "The plugin did not register this panel."),
                    $"octopus.panels.register('{_panelId}', …) was not called in activate().");
                return;
            }
            _plugin = plugin;
            _renderer = new UiRenderer(_workbench.SinkFor(plugin, InstanceId), plugin.Package.Directory);
            _workbench.Attach(plugin.Id, InstanceId, this);
            var currentProject = _projects.Current;
            var context = new JsonObject
            {
                ["instanceId"] = InstanceId,
                ["project"] = currentProject is null ? null : new JsonObject
                {
                    ["id"] = currentProject.Id,
                    ["name"] = currentProject.Name,
                    ["path"] = currentProject.Path,
                    ["deviceKey"] = currentProject.DeviceKey,
                    ["locationKind"] = currentProject.LocationKind,
                    ["sshHost"] = currentProject.SshHost,
                    ["remoteHost"] = currentProject.RemoteHost,
                },
                ["state"] = _savedState?.DeepClone(),
                ["visible"] = _panelVisible,
                ["size"] = SizeOf(Bounds.Size),
            };
            var json = await plugin.InvokeAsync("view.open", new JsonArray(JsonValue.Create("panel"), JsonValue.Create(_panelId), context), OpenTimeout);
            var result = JsonNode.Parse(json) as JsonObject;
            _open = true;
            Render(result?["tree"]);
            // Visibility may have changed while opening.
            if (!_panelVisible)
            {
                _ = PluginWorkbench.InvokeQuietlyAsync(plugin, "view.visibility", new JsonArray(JsonValue.Create(InstanceId), JsonValue.Create(false)));
            }
        }
        catch (PluginInvokeException e) { ShowError(PluginText.Get("面板打开失败。", "The panel failed to open."), e.Message); }
        catch (TimeoutException e) { ShowError(PluginText.Get("面板打开超时。", "The panel took too long to open."), e.Message); }
        catch (JsonException e) { ShowError(PluginText.Get("面板返回的内容无法读取。", "The panel returned invalid UI data."), e.Message); }
        finally { _opening = false; }
    }

    private static JsonObject SizeOf(Size size) => new()
    {
        ["class"] = size.Width switch { 0 => "compact", < 480 => "compact", < 1100 => "regular", _ => "wide" },
        ["width"] = Math.Round(size.Width),
        ["height"] = Math.Round(size.Height),
    };

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var before = SizeOf(e.PreviousSize)["class"]!.GetValue<string>();
        var after = SizeOf(e.NewSize)["class"]!.GetValue<string>();
        if (_open && _plugin is { } plugin && before != after)
        {
            plugin.Emit("view.resize", SizeOf(e.NewSize), args => args?["instanceId"]?.GetValue<string>() == InstanceId);
        }
    }

    public void Update(JsonNode? tree)
    {
        if (_open && !_disposed) Render(tree);
    }

    public void Chrome(JsonObject change)
    {
        if (_plugin is null) return;
        if (change.ContainsKey("badge"))
        {
            var badge = change["badge"] is JsonValue b && b.TryGetValue<string>(out var s) ? s : change["badge"]?.ToJsonString();
            _panels.SetBadge(_plugin, _panelId, badge, null, null);
        }
    }

    public void Error(string message) => _plugin?.Write(PluginLogLevel.Error, $"{InstanceId} render: {message}");

    public void SaveState(JsonNode? state) => _savedState = state?.DeepClone();

    private void Render(JsonNode? tree)
    {
        if (_renderer is null) return;
        var previous = _rendered;
        _rendered = _renderer.Render(tree, previous);
        if (!ReferenceEquals(_host.Child, _rendered)) _host.Child = _rendered;
    }

    private void OnHostChanged(object? sender, PluginHost host)
    {
        if (_plugin is not null && host.Id != _plugin.Id) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            switch (host.State)
            {
                case PluginHostState.Crashed or PluginHostState.Stopped or PluginHostState.Failed or PluginHostState.Inactive when _open && host.Id == _plugin?.Id:
                    _open = false;
                    _workbench.Detach(host.Id, InstanceId, this);
                    if (host.State == PluginHostState.Crashed) ShowLoading();
                    else ShowError(PluginText.Get("插件已停止。", "The plugin stopped."), host.LastError);
                    break;
                case PluginHostState.Running when !_open && !_opening && _panelVisible && _panels.Find(_panelId)?.Host.Id == host.Id:
                    _ = OpenAsync();
                    break;
            }
        });
    }

    private void ShowLoading()
    {
        UiRenderer.DisposeTree(_rendered);
        _rendered = null;
        _host.Child = new Skeleton { IsLoading = true, Lines = 5, Margin = new Thickness(14) };
    }

    private void ShowUnavailable(string message, string? detail)
    {
        UiRenderer.DisposeTree(_rendered);
        _rendered = null;
        _host.Child = new EmptyState
        {
            Title = PluginText.Get("面板不可用", "Panel unavailable"),
            Description = detail is null ? message : $"{message}\n{detail}",
            Icon = new LucideIcon { Kind = LucideIconKind.Puzzle, Size = 24 },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12),
        };
    }

    private void ShowError(string message, string? detail)
    {
        UiRenderer.DisposeTree(_rendered);
        _rendered = null;
        var retry = new TKCode.Arc.Button { Content = PluginText.Get("重试", "Retry"), Variant = ButtonVariant.Secondary, Size = ControlSize.Small };
        retry.Click += async (_, _) =>
        {
            if (_panels.Find(_panelId) is { } item && item.Host.State is PluginHostState.Stopped or PluginHostState.Failed)
            {
                await _workbench.Manager.ReloadAsync(item.Host.Id);
            }
            if (!_opening) await OpenAsync();
        };
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        body.Children.Add(new EmptyState
        {
            Title = message,
            Description = detail ?? "",
            Icon = new LucideIcon { Kind = LucideIconKind.TriangleAlert, Size = 24 },
        });
        body.Children.Add(retry);
        _host.Child = body;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workbench.Manager.HostChanged -= OnHostChanged;
        if (_open && _plugin is { } plugin)
        {
            _workbench.Detach(plugin.Id, InstanceId, this);
            _ = PluginWorkbench.InvokeQuietlyAsync(plugin, "view.close", new JsonArray(JsonValue.Create(InstanceId)));
        }
        UiRenderer.DisposeTree(_rendered);
        _rendered = null;
    }
}
