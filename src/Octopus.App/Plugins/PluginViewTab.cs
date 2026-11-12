using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Octopus.App.Plugins.Rendering;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Core.Plugins;
using Octopus.Core.Projects;
using Octopus.Plugins.Runtime;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Plugins;

/// <summary>
/// A tab whose content a plugin renders (design/plugins/06-标签与渲染.md §3). It opens the view when first shown,
/// shows loading, error and "unavailable" states of its own, keeps the plugin's state on close, and opens the view
/// again when the plugin comes back after a crash.
/// </summary>
public sealed class PluginViewTab : UserControl, ITabContent, IPluginSurface
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

    private readonly Project _project;
    private readonly ProjectItem _item;
    private readonly PluginWorkbench _workbench;
    private readonly IProjectService _projects;
    private readonly Border _host = new();
    private PluginHost? _plugin;
    private UiRenderer? _renderer;
    private Control? _rendered;
    private bool _opening;
    private bool _open;
    private bool _closed;
    private bool _visible;

    public PluginViewTab(Project project, ProjectItem item, PluginWorkbench workbench, IProjectService projects)
    {
        _project = project;
        _item = item;
        _workbench = workbench;
        _projects = projects;
        Content = _host;
        _workbench.Manager.HostChanged += OnHostChanged;
    }

    /// <summary><c>tab:&lt;item id&gt;</c>: the instance id the plugin knows this tab by.</summary>
    public string InstanceId => $"tab:{_item.Id}";

    public bool IsDirty { get; private set; }

    /// <summary>The plugin marked the tab dirty or clean, or changed its badge.</summary>
    public event EventHandler? ChromeChanged;

    public string? Badge { get; private set; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SetVisible(true);
        if (!_open && !_opening && !_closed) _ = OpenAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        SetVisible(false);
    }

    private void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        if (_open && _plugin is { } plugin)
        {
            _ = PluginWorkbench.InvokeQuietlyAsync(plugin, "view.visibility", [JsonValue.Create(InstanceId), JsonValue.Create(visible)]);
        }
    }

    // ---------------------------------------------------------------- opening

    private async Task OpenAsync()
    {
        _opening = true;
        try
        {
            ShowLoading();
            if (_workbench.FindView(_item.ViewId) is not var (owner, view))
            {
                ShowUnavailable(
                    PluginText.Get("提供这个视图的插件没有安装或已停用。", "The plugin that provides this view is not installed or is disabled."),
                    _item.ViewId);
                return;
            }
            var plugin = await _workbench.Manager.EnsureActiveAsync(owner.Id);
            if (plugin is null)
            {
                ShowError(PluginText.Get($"插件 {PluginWorkbench.NameOf(owner)} 没有运行。", $"The plugin {PluginWorkbench.NameOf(owner)} is not running."), owner.LastError);
                return;
            }
            if (!plugin.Providers.Contains(("view", view.Id)))
            {
                ShowError(
                    PluginText.Get("插件没有为这个视图注册实现。", "The plugin did not register this view."),
                    $"octopus.views.register('{view.Id}', …) was not called in activate().");
                return;
            }
            _plugin = plugin;
            _renderer = new UiRenderer(_workbench.SinkFor(plugin, InstanceId));
            _workbench.Attach(plugin.Id, InstanceId, this);
            var context = new JsonObject
            {
                ["instanceId"] = InstanceId,
                ["project"] = new JsonObject { ["id"] = _project.Id, ["name"] = _project.Name, ["path"] = _project.Path },
                ["state"] = PluginWorkbench.ParseState(_item.ViewState),
                ["visible"] = _visible,
                ["size"] = SizeOf(Bounds.Size),
            };
            var json = await plugin.InvokeAsync("view.open", [JsonValue.Create("view"), JsonValue.Create(view.Id), context], OpenTimeout);
            var result = JsonNode.Parse(json) as JsonObject;
            _open = true;
            Render(result?["tree"]);
        }
        catch (PluginInvokeException e)
        {
            ShowError(PluginText.Get("视图打开失败。", "The view failed to open."), e.Message);
        }
        catch (TimeoutException e)
        {
            ShowError(PluginText.Get("视图打开超时。", "The view took too long to open."), e.Message);
        }
        catch (JsonException e)
        {
            ShowError(PluginText.Get("视图返回的内容无法读取。", "The view returned something that cannot be read."), e.Message);
        }
        finally
        {
            _opening = false;
        }
    }

    private static JsonObject SizeOf(Size size) => new()
    {
        ["class"] = size.Width switch { 0 => "regular", < 480 => "compact", < 1100 => "regular", _ => "wide" },
        ["width"] = Math.Round(size.Width),
        ["height"] = Math.Round(size.Height),
    };

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var before = SizeOf(e.PreviousSize)["class"]!.GetValue<string>();
        var after = SizeOf(e.NewSize)["class"]!.GetValue<string>();
        // Only a change of size class is sent: a plugin re-rendering on every pixel of a drag would be wasted work.
        if (_open && _plugin is { } plugin && before != after)
        {
            plugin.Emit("view.resize", SizeOf(e.NewSize), args => args?["instanceId"]?.GetValue<string>() == InstanceId);
        }
    }

    // ---------------------------------------------------------------- IPluginSurface

    public void Update(JsonNode? tree)
    {
        if (_open) Render(tree);
    }

    private void Render(JsonNode? tree)
    {
        if (_renderer is null) return;
        var previous = _rendered;
        _rendered = _renderer.Render(tree, previous);
        if (!ReferenceEquals(_host.Child, _rendered)) _host.Child = _rendered;
    }

    public void Chrome(JsonObject change)
    {
        if (change["title"] is JsonValue t && t.TryGetValue<string>(out var title)) _projects.SetItemTitle(_item.Id, title);
        if (change.ContainsKey("badge")) Badge = change["badge"] is JsonValue b && b.TryGetValue<string>(out var badge) ? badge : change["badge"]?.ToJsonString();
        if (change["dirty"] is JsonValue d && d.TryGetValue<bool>(out var dirty)) IsDirty = dirty;
        ChromeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Error(string message) => _plugin?.Write(PluginLogLevel.Error, $"{InstanceId} render: {message}");

    public void SaveState(JsonNode? state) => _projects.SetItemViewState(_item.Id, PluginWorkbench.StateText(state));

    // ---------------------------------------------------------------- plugin state changes

    private void OnHostChanged(object? sender, PluginHost host)
    {
        if (host.Id != _plugin?.Id) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            switch (host.State)
            {
                case PluginHostState.Crashed or PluginHostState.Stopped or PluginHostState.Failed or PluginHostState.Inactive when _open:
                    _open = false;
                    _workbench.Detach(host.Id, InstanceId, this);
                    if (host.State == PluginHostState.Crashed)
                    {
                        // Restarted automatically after a back-off: open again when it is running.
                        ShowLoading();
                    }
                    else
                    {
                        ShowError(PluginText.Get("插件已停止。", "The plugin stopped."), host.LastError);
                    }
                    break;
                case PluginHostState.Running when !_open && !_opening && _visible:
                    _ = OpenAsync();
                    break;
            }
        });
    }

    // ---------------------------------------------------------------- states

    private void ShowLoading()
    {
        _rendered = null;
        _host.Child = new Skeleton { IsLoading = true, Lines = 4, Margin = new Thickness(24) };
    }

    private void ShowUnavailable(string message, string? detail)
    {
        _rendered = null;
        _host.Child = new EmptyState
        {
            Title = PluginText.Get("视图不可用", "View unavailable"),
            Description = detail is null ? message : $"{message}\n{detail}",
            Icon = new LucideIcon { Kind = LucideIconKind.Puzzle, Size = 28 },
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private void ShowError(string message, string? detail)
    {
        _rendered = null;
        var retry = new TKCode.Arc.Button { Content = PluginText.Get("重试", "Retry"), Variant = ButtonVariant.Secondary, Size = ControlSize.Small };
        retry.Click += async (_, _) =>
        {
            if (_workbench.FindView(_item.ViewId) is var (owner, _) && owner.State is PluginHostState.Stopped or PluginHostState.Failed)
            {
                await _workbench.Manager.ReloadAsync(owner.Id);
            }
            if (!_opening) await OpenAsync();
        };
        _host.Child = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                new Alert { Title = message, Tone = AlertTone.Danger, Content = detail is null ? null : new SelectableTextBlock { Text = detail, TextWrapping = Avalonia.Media.TextWrapping.Wrap } },
                retry,
            },
        };
    }

    // ---------------------------------------------------------------- ITabContent

    public Task<bool> HasRunningChildAsync() => Task.FromResult(false);

    public Task<bool> HasUnsavedChangesAsync() => Task.FromResult(IsDirty);

    public async Task CloseAsync()
    {
        _closed = true;
        _workbench.Manager.HostChanged -= OnHostChanged;
        if (_plugin is { } plugin && _open)
        {
            _open = false;
            var state = await PluginWorkbench.InvokeQuietlyAsync(plugin, "view.getState", [JsonValue.Create(InstanceId)], TimeSpan.FromSeconds(2));
            if (state is not null && state != "null")
            {
                SaveState(PluginWorkbench.ParseState(state));
            }
            await PluginWorkbench.InvokeQuietlyAsync(plugin, "view.close", [JsonValue.Create(InstanceId)], TimeSpan.FromSeconds(2));
            _workbench.Detach(plugin.Id, InstanceId, this);
        }
    }

    /// <summary>"Restart" on a plugin tab opens the view again (its state is kept).</summary>
    public async Task RestartAsync()
    {
        if (_plugin is { } plugin && _open)
        {
            _open = false;
            var state = await PluginWorkbench.InvokeQuietlyAsync(plugin, "view.getState", [JsonValue.Create(InstanceId)], TimeSpan.FromSeconds(2));
            if (state is not null && state != "null") SaveState(PluginWorkbench.ParseState(state));
            await PluginWorkbench.InvokeQuietlyAsync(plugin, "view.close", [JsonValue.Create(InstanceId)], TimeSpan.FromSeconds(2));
            _workbench.Detach(plugin.Id, InstanceId, this);
        }
        _closed = false;
        await OpenAsync();
    }

    public void FocusContent()
    {
        if (_rendered is { } content) Dispatcher.UIThread.Post(() => content.Focus(), DispatcherPriority.Input);
    }
}
