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
        if (state.ContainsKey("text")) indicator.Text = Trim(Str(state, "text"));
        if (state.ContainsKey("icon")) indicator.Icon = Str(state, "icon");
        if (state.ContainsKey("tooltip")) indicator.Tooltip = Str(state, "tooltip");
        if (state.ContainsKey("level")) indicator.Level = Str(state, "level");
        if (state.ContainsKey("visible")) indicator.IsShown = Bool(state, "visible") ?? true;
        if (state.ContainsKey("value")) indicator.Value = Num(state, "value") is { } v ? Math.Clamp(v, 0, 1) : null;
    });

    public void Remove(PluginHost plugin, string id) => Dispatcher.UIThread.Post(() =>
    {
        if (Items.FirstOrDefault(i => i.Plugin.Id == plugin.Id && i.Id == id) is { } indicator) Items.Remove(indicator);
    });

    public void RemoveAll(PluginHost plugin) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var indicator in Items.Where(i => i.Plugin.Id == plugin.Id).ToList()) Items.Remove(indicator);
    });

    private static string? Trim(string? text) => text is { Length: > MaxText } ? text[..(MaxText - 1)] + "…" : text;
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
        _row.Children.Clear();
        foreach (var indicator in _indicators!.Items) _row.Children.Add(new IndicatorButton(indicator, _popover));
    }

    private sealed class IndicatorButton : TKCode.Arc.Button
    {
        private readonly PluginIndicator _indicator;
        private readonly LucideIcon _icon = new() { Size = 14, VerticalAlignment = VerticalAlignment.Center };
        private readonly MiniRing _ring = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        private readonly Popover _popover;
        private Control? _rendered;
        private UiRenderer? _renderer;
        private string? _instanceId;
        private readonly List<IDisposable> _bindings = [];

        protected override Type StyleKeyOverride => typeof(TKCode.Arc.Button);

        public IndicatorButton(PluginIndicator indicator, Popover popover)
        {
            _indicator = indicator;
            _popover = popover;
            Variant = ButtonVariant.Ghost;
            Size = ControlSize.Small;
            VerticalAlignment = VerticalAlignment.Center;
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { _ring, _icon, _text } };
            indicator.PropertyChanged += (_, _) => Apply();
            Click += async (_, _) => await OnClickAsync();
            Apply();
        }

        private void Apply()
        {
            IsVisible = _indicator.IsShown && (_indicator.Text is not null || _indicator.Icon is not null || _indicator.Value is not null);
            _text.Text = _indicator.Text;
            _text.IsVisible = _indicator.Text is not null;
            var kind = UiIcons.Parse(_indicator.Icon);
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
            foreach (var b in _bindings) b.Dispose();
            _bindings.Clear();
            var color = UiLayout.ColorToken(_indicator.Level is "success" or "warning" or "danger" ? _indicator.Level : null) ?? ArcDesignTokenKind.TextSecondary;
            _bindings.Add(ArcTokens.Bind(_text, TextBlock.ForegroundProperty, color, Avalonia.Data.BindingPriority.LocalValue));
            _bindings.Add(ArcTokens.Bind(_icon, LucideIcon.ForegroundProperty, color, Avalonia.Data.BindingPriority.LocalValue));
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
            _renderer ??= new UiRenderer(workbench.SinkFor(plugin, _instanceId));
            var instanceId = _instanceId;
            var host = new Border { Padding = new Thickness(12), MinWidth = 280, MaxWidth = 420, MaxHeight = 560 };
            var surface = new PopoverSurface(this, host);
            _rendered = null;
            surface.Update(tree.DeepClone());
            workbench.Attach(plugin.Id, instanceId, surface);
            _popover.Content = host;
            EventHandler<OverlayClosedEventArgs>? closed = null;
            closed = (_, _) =>
            {
                _popover.Closed -= closed;
                workbench.Detach(plugin.Id, instanceId, surface);
                _ = PluginWorkbench.InvokeQuietlyAsync(plugin, "view.close", [JsonValue.Create(instanceId)]);
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
    private IDisposable? _fillColor;

    public MiniRing()
    {
        Width = 14;
        Height = 14;
        Children.Add(_track);
        Children.Add(_fill);
        ArcTokens.Bind(_track, Avalonia.Controls.Shapes.Shape.StrokeProperty, ArcDesignTokenKind.BorderStrong, Avalonia.Data.BindingPriority.LocalValue);
        Color = ArcDesignTokenKind.TextSecondary;
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
            _fillColor?.Dispose();
            _fillColor = ArcTokens.Bind(_fill, Avalonia.Controls.Shapes.Shape.StrokeProperty, value, Avalonia.Data.BindingPriority.LocalValue);
        }
    }
}
