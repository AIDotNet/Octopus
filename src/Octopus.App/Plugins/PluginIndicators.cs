using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Composition;
using Octopus.App.Plugins.Rendering;
using Octopus.Plugins.Runtime;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using static Octopus.App.Plugins.Rendering.UiProps;

namespace Octopus.App.Plugins;

/// <summary>One small status item a plugin shows in the top bar (design/plugins/05-界面能力.md §7).</summary>
public sealed partial class PluginIndicator(PluginHost plugin, string id, int order) : ObservableObject
{
    public PluginHost Plugin { get; } = plugin;
    public string Id { get; } = id;
    public int Order { get; } = order;

    [ObservableProperty] public partial string? Text { get; set; }
    [ObservableProperty] public partial string? Icon { get; set; }
    [ObservableProperty] public partial string? Tooltip { get; set; }
    /// <summary>default, success, warning, danger, accent.</summary>
    [ObservableProperty] public partial string? Level { get; set; }
    [ObservableProperty] public partial bool IsShown { get; set; } = true;
    /// <summary>0…1 for a small ring before the text; null for none.</summary>
    [ObservableProperty] public partial double? Value { get; set; }

    public event EventHandler? Updated;

    internal void ApplyUpdate(JsonObject state)
    {
        var changed = false;
        if (state.ContainsKey("text")) { var v = PluginIndicators.Trim(Str(state, "text")); if (Text != v) { Text = v; changed = true; } }
        if (state.ContainsKey("icon")) { var v = Str(state, "icon"); if (Icon != v) { Icon = v; changed = true; } }
        if (state.ContainsKey("tooltip")) { var v = Str(state, "tooltip"); if (Tooltip != v) { Tooltip = v; changed = true; } }
        if (state.ContainsKey("level")) { var v = Str(state, "level"); if (Level != v) { Level = v; changed = true; } }
        if (state.ContainsKey("visible")) { var v = Bool(state, "visible") ?? true; if (IsShown != v) { IsShown = v; changed = true; } }
        if (state.ContainsKey("value")) { var v = Num(state, "value") is { } n ? (double?)Math.Clamp(n, 0, 1) : null; if (Value != v) { Value = v; changed = true; } }
        if (changed) Updated?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// The indicators of all plugins, in manifest order. Only ids the manifest declares under
/// <c>contributes.indicators</c> are shown, at most <see cref="MaxPerPlugin"/> per plugin.
/// </summary>
public sealed class PluginIndicators
{
    public const int MaxPerPlugin = 3;
    public const int MaxText = 40;

    public ObservableCollection<PluginIndicator> Items { get; } = [];

    public void Create(PluginHost plugin, string id)
    {
        var declared = plugin.Manifest.Contributes.Indicators.Select(i => i.Id).ToList();
        var order = declared.IndexOf(id);
        if (order < 0)
        {
            plugin.Write(PluginLogLevel.Warn, $"Indicator \"{id}\" is not declared under contributes.indicators; it is not shown.");
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (Items.Any(i => i.Plugin.Id == plugin.Id && i.Id == id)) return;
            if (Items.Count(i => i.Plugin.Id == plugin.Id) >= MaxPerPlugin) return;
            var indicator = new PluginIndicator(plugin, id, order);
            var index = 0;
            while (index < Items.Count && string.CompareOrdinal(Items[index].Plugin.Id, plugin.Id) <= 0 && !(Items[index].Plugin.Id == plugin.Id && Items[index].Order > order)) index++;
            Items.Insert(index, indicator);
        });
    }

    public void Update(PluginHost plugin, string id, JsonObject state) => Dispatcher.UIThread.Post(() =>
    {
        if (Items.FirstOrDefault(i => i.Plugin.Id == plugin.Id && i.Id == id) is not { } indicator) return;
        indicator.ApplyUpdate(state);
    });

    public void Remove(PluginHost plugin, string id) => Dispatcher.UIThread.Post(() =>
    {
        if (Items.FirstOrDefault(i => i.Plugin.Id == plugin.Id && i.Id == id) is { } indicator) Items.Remove(indicator);
    });

    public void RemoveAll(PluginHost plugin) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var indicator in Items.Where(i => i.Plugin.Id == plugin.Id).ToList()) Items.Remove(indicator);
    });

    internal static string? Trim(string? text) => text is { Length: > MaxText } ? text[..(MaxText - 1)] + "…" : text;
}

/// <summary>The top bar strip that shows the indicators; a click opens the plugin's popover or runs its handler.</summary>
public sealed class IndicatorStrip : Panel
{
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly Popover _popover = new() { Placement = PopoverPlacement.Bottom, Alignment = PopoverAlignment.End };
    private PluginIndicators? _indicators;

    public IndicatorStrip()
    {
        VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_row);
        // One popover for the strip, shown at whichever indicator was clicked.
        Children.Add(_popover);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_indicators is not null || Design.IsDesignMode) return;
        _indicators = AppHost.Get<PluginIndicators>();
        _indicators.Items.CollectionChanged += (_, _) => Rebuild();
        Rebuild();
    }

    private void Rebuild()
    {
        foreach (var old in _row.Children.OfType<IndicatorButton>()) old.Release();
        _row.Children.Clear();
        foreach (var indicator in _indicators!.Items) _row.Children.Add(new IndicatorButton(indicator, _popover));
    }

    private sealed class IndicatorButton : TKCode.Arc.Button
    {
        private readonly PluginIndicator _indicator;
        private readonly Image _image = new() { Width = 15, Height = 15, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        private readonly LucideIcon _icon = new() { Size = 14, VerticalAlignment = VerticalAlignment.Center };
        private readonly MiniRing _ring = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        private readonly Popover _popover;
        private Control? _rendered;
        private UiRenderer? _renderer;
        private string? _instanceId;
        private readonly List<IDisposable> _bindings = [];
        private ArcDesignTokenKind? _boundColor;

        protected override Type StyleKeyOverride => typeof(TKCode.Arc.Button);

        public IndicatorButton(PluginIndicator indicator, Popover popover)
        {
            _indicator = indicator;
            _popover = popover;
            Variant = ButtonVariant.Ghost;
            Size = ControlSize.Small;
            VerticalAlignment = VerticalAlignment.Center;
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { _ring, _image, _icon, _text } };
            indicator.Updated += OnIndicatorUpdated;
            Click += async (_, _) => await OnClickAsync();
            Apply();
        }

        private void OnIndicatorUpdated(object? sender, EventArgs e) => Apply();

        public void Release()
        {
            _indicator.Updated -= OnIndicatorUpdated;
            foreach (var b in _bindings) b.Dispose();
            _bindings.Clear();
            _boundColor = null;
            _ring.Release();
            UiRenderer.DisposeTree(_rendered);
            _rendered = null;
        }

        private void Apply()
        {
            IsVisible = _indicator.IsShown && (_indicator.Text is not null || _indicator.Icon is not null || _indicator.Value is not null);
            _text.Text = _indicator.Text;
            _text.IsVisible = _indicator.Text is not null;
            var bitmap = UiIcons.TryLoadPackageBitmap(_indicator.Plugin.Package.Directory, _indicator.Icon);
            _image.Source = bitmap;
            _image.IsVisible = bitmap is not null && _indicator.Value is null;
            var kind = bitmap is null ? UiIcons.Parse(_indicator.Icon) : null;
            _icon.IsVisible = kind is not null && _indicator.Value is null;
            if (kind is { } k) _icon.Kind = k;
            _ring.IsVisible = _indicator.Value is not null;
            _ring.Value = _indicator.Value ?? 0;
            _ring.Color = _indicator.Level switch
            {
                "danger" => ArcDesignTokenKind.Danger,
                "warning" => ArcDesignTokenKind.Warning,
                "success" => ArcDesignTokenKind.Success,
                _ => ArcDesignTokenKind.TextSecondary,
            };
            var color = UiLayout.ColorToken(_indicator.Level is "success" or "warning" or "danger" ? _indicator.Level : null) ?? ArcDesignTokenKind.TextSecondary;
            if (_boundColor != color)
            {
                _boundColor = color;
                foreach (var b in _bindings) b.Dispose();
                _bindings.Clear();
                _bindings.Add(ArcTokens.Bind(_text, TextBlock.ForegroundProperty, color, Avalonia.Data.BindingPriority.LocalValue));
                _bindings.Add(ArcTokens.Bind(_icon, LucideIcon.ForegroundProperty, color, Avalonia.Data.BindingPriority.LocalValue));
            }
            ToolTip.SetTip(this, _indicator.Tooltip is { } tip ? $"{tip}\n— {PluginWorkbench.NameOf(_indicator.Plugin)}" : PluginWorkbench.NameOf(_indicator.Plugin));
        }

        private async Task OnClickAsync()
        {
            if (_popover.IsOpen)
            {
                _popover.Close();
                return;
            }
            var plugin = _indicator.Plugin;
            var json = await PluginWorkbench.InvokeQuietlyAsync(plugin, $"indicator.click:{_indicator.Id}", []);
            if (json is null || JsonNode.Parse(json) is not JsonObject result || result["tree"] is not { } tree) return;
            var workbench = AppHost.Get<PluginWorkbench>();
            _instanceId = result["instanceId"]?.GetValue<string>() ?? $"indicator:{_indicator.Id}";
            _renderer ??= new UiRenderer(workbench.SinkFor(plugin, _instanceId), plugin.Package.Directory);
            var instanceId = _instanceId;
            // The content scrolls once it passes the height cap: a popover listing several agents and accounts is
            // taller than the window can show. The padding sits inside the scroll area, so the bar runs along the edge.
            var host = new Border { Padding = new Thickness(12) };
            var scroll = new ScrollArea { Content = host, MinWidth = 280, MaxWidth = 420, MaxHeight = 560, FadeLength = 0 };
            var surface = new PopoverSurface(this, host);
            UiRenderer.DisposeTree(_rendered);
            _rendered = null;
            surface.Update(tree.DeepClone());
            workbench.Attach(plugin.Id, instanceId, surface);
            _popover.Content = scroll;
            EventHandler<OverlayClosedEventArgs>? closed = null;
            closed = (_, _) =>
            {
                _popover.Closed -= closed;
                workbench.Detach(plugin.Id, instanceId, surface);
                UiRenderer.DisposeTree(_rendered);
                _rendered = null;
                _popover.Content = null;
                _ = PluginWorkbench.InvokeQuietlyAsync(plugin, "view.close", new JsonArray(JsonValue.Create(instanceId)));
            };
            _popover.Closed += closed;
            _popover.ShowAt(this);
        }

        private sealed class PopoverSurface(IndicatorButton owner, Border host) : IPluginSurface
        {
            public void Update(JsonNode? tree)
            {
                owner._rendered = owner._renderer!.Render(tree, owner._rendered);
                if (!ReferenceEquals(host.Child, owner._rendered)) host.Child = owner._rendered;
            }
        }
    }
}

/// <summary>
/// A 14-pixel progress ring for the top bar: a faint track and the filled part, nothing else. Arc's <c>Gauge</c>
/// draws its value in the middle, which cannot be read at this size.
/// </summary>
internal sealed class MiniRing : Panel
{
    private readonly Avalonia.Controls.Shapes.Arc _track = new() { StartAngle = 0, SweepAngle = 360, StrokeThickness = 2 };
    private readonly Avalonia.Controls.Shapes.Arc _fill = new() { StartAngle = -90, StrokeThickness = 2, StrokeLineCap = PenLineCap.Round };
    private IDisposable? _trackColor;
    private IDisposable? _fillColor;
    private ArcDesignTokenKind? _color;

    public MiniRing()
    {
        Width = 14;
        Height = 14;
        Children.Add(_track);
        Children.Add(_fill);
        _trackColor = ArcTokens.Bind(_track, Avalonia.Controls.Shapes.Shape.StrokeProperty, ArcDesignTokenKind.BorderStrong, Avalonia.Data.BindingPriority.LocalValue);
        Color = ArcDesignTokenKind.TextSecondary;
    }

    public void Release()
    {
        _trackColor?.Dispose();
        _trackColor = null;
        _fillColor?.Dispose();
        _fillColor = null;
        _color = null;
    }

    /// <summary>0…1.</summary>
    public double Value
    {
        get => _fill.SweepAngle / 360;
        set => _fill.SweepAngle = Math.Clamp(value, 0, 1) * 360;
    }

    public ArcDesignTokenKind Color
    {
        set
        {
            if (_color == value) return;
            _color = value;
            _fillColor?.Dispose();
            _fillColor = ArcTokens.Bind(_fill, Avalonia.Controls.Shapes.Shape.StrokeProperty, value, Avalonia.Data.BindingPriority.LocalValue);
        }
    }
}
