using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Octopus.App.Plugins.Rendering;

/// <summary>What a rendered plugin tree needs from the view that shows it.</summary>
public interface IUiEventSink
{
    /// <summary>A control event: runs the plugin's handler <paramref name="handlerId"/> with <paramref name="args"/>.</summary>
    void Fire(string handlerId, JsonArray args);

    /// <summary>A control bound to a command id instead of a handler.</summary>
    void ExecuteCommand(string commandId, JsonArray args);

    /// <summary>The system file or folder picker for a path picker; null when cancelled.</summary>
    Task<string?> PickPathAsync(bool folder, string? title);
}

/// <summary>Per control: which node it renders and the handler ids of its latest render.</summary>
internal sealed class NodeState(string type, IUiComponent component)
{
    public string Type { get; } = type;
    public IUiComponent Component { get; } = component;
    public string? Key { get; set; }
    public JsonObject Props { get; set; } = [];
    public List<Control> Children { get; set; } = [];
    /// <summary>The <c>rev</c> the host-held value was last overwritten at (design/plugins/05-界面能力.md §2).</summary>
    public string? AppliedRev { get; set; }
    public bool ValueApplied { get; set; }
    public object? Extra { get; set; }
    /// <summary>Token bindings by target and property; one that the next update asks for again is kept, not rebound.</summary>
    public Dictionary<(StyledElement Target, AvaloniaProperty Property), TokenBinding> Bindings { get; } = [];
}

/// <summary>A property bound to a design token, and whether the update in progress asked for it.</summary>
internal sealed class TokenBinding(ArcDesignTokenKind kind, IDisposable subscription)
{
    public ArcDesignTokenKind Kind { get; } = kind;
    public IDisposable Subscription { get; } = subscription;
    public bool Used { get; set; } = true;
}

/// <summary>
/// Turns plugin interface trees (<c>{t, k, p, c}</c>) into Arc controls (design/plugins/05-界面能力.md §1). A node keeps
/// its control while its type and key stay the same, so typing, scrolling and focus survive re-renders; only the
/// properties are applied again, and only when they changed. Unknown component types render as a visible placeholder
/// instead of failing.
/// </summary>
public sealed class UiRenderer(IUiEventSink sink)
{
    public const int MaxNodes = 20_000;

    private int _nodes;

    internal IUiEventSink Sink => sink;

    /// <summary>Renders <paramref name="tree"/>, reusing <paramref name="previous"/> (the result of the last render) where it can.</summary>
    public Control Render(JsonNode? tree, Control? previous)
    {
        _nodes = 0;
        if (tree is not JsonObject node)
        {
            return Placeholder("The view rendered nothing.");
        }
        return Reconcile(previous, node);
    }

    internal Control Reconcile(Control? existing, JsonObject node)
    {
        if (++_nodes > MaxNodes)
        {
            return Placeholder($"More than {MaxNodes} nodes: the rest is not shown.");
        }
        var type = node["t"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : "";
        var props = node["p"] as JsonObject ?? [];
        Control control;
        NodeState state;
        var unchanged = false;
        if (existing?.Tag is NodeState old && old.Type == type)
        {
            control = existing;
            state = old;
            // Same properties as last time: applying them again would only redo work (and rebuild content some
            // components create on each update). Children are still reconciled; they have their own properties.
            unchanged = JsonNode.DeepEquals(old.Props, props);
        }
        else
        {
            var component = UiComponents.Find(type) ?? new UnknownComponent(type);
            state = new NodeState(type, component);
            control = component.Create(state, this);
            control.Tag = state;
        }
        state.Key = node["k"] is JsonValue k && k.TryGetValue<string>(out var key) ? key : null;
        state.Props = props;
        try
        {
            if (!unchanged) Update(control, state);
            if (state.Component.HasChildren)
            {
                state.Children = ReconcileChildren(state.Children, node["c"] as JsonArray);
                state.Component.SetChildren(control, state, state.Children);
            }
            UiLayout.ApplyCommon(control, props);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            // A property of the wrong shape breaks this node only (design 05 §8).
            return Placeholder($"{type}: {e.Message}");
        }
        return control;
    }

    /// <summary>Applies the node's properties; token bindings the update does not ask for again are released after it.</summary>
    private void Update(Control control, NodeState state)
    {
        foreach (var binding in state.Bindings.Values) binding.Used = false;
        state.Component.Update(control, state, this);
        List<(StyledElement, AvaloniaProperty)>? stale = null;
        foreach (var (key, binding) in state.Bindings)
        {
            if (!binding.Used) (stale ??= []).Add(key);
        }
        foreach (var key in stale ?? [])
        {
            state.Bindings.Remove(key, out var binding);
            binding!.Subscription.Dispose();
        }
    }

    /// <summary>Keyed children keep their control by key; unkeyed ones by position among the unkeyed.</summary>
    private List<Control> ReconcileChildren(List<Control> old, JsonArray? nodes)
    {
        var result = new List<Control>();
        if (nodes is null)
        {
            return result;
        }
        var byKey = new Dictionary<string, Control>(StringComparer.Ordinal);
        var unkeyed = new Queue<Control>();
        foreach (var child in old)
        {
            if (child.Tag is NodeState { Key: { } k }) byKey.TryAdd(k, child);
            else unkeyed.Enqueue(child);
        }
        foreach (var item in nodes)
        {
            if (item is not JsonObject child)
            {
                continue;
            }
            Control? previous = null;
            if (child["k"] is JsonValue key && key.TryGetValue<string>(out var k))
            {
                byKey.Remove(k, out previous);
            }
            else if (unkeyed.Count > 0)
            {
                previous = unkeyed.Dequeue();
            }
            result.Add(Reconcile(previous, child));
        }
        return result;
    }

    // ---------------------------------------------------------------- helpers for components

    internal static string? Handler(JsonObject props, string name) =>
        props[name] is JsonObject h && h["$h"] is JsonValue v && v.TryGetValue<string>(out var id) ? id : null;

    /// <summary>Runs the handler named by property <paramref name="name"/> as it is in the latest render, or its command.</summary>
    internal void Fire(NodeState state, string name, params JsonNode?[] args)
    {
        var array = new JsonArray();
        foreach (var a in args) array.Add(a?.DeepClone());
        if (Handler(state.Props, name) is { } id)
        {
            sink.Fire(id, array);
        }
        else if (name == "onClick" && UiProps.Str(state.Props, "command") is { } command)
        {
            sink.ExecuteCommand(command, state.Props["args"] as JsonArray is { } given ? (JsonArray)given.DeepClone() : array);
        }
    }

    /// <summary>True when the host-held value should be overwritten from <c>value</c>: first render, or a new <c>rev</c>.</summary>
    internal static bool TakeValue(NodeState state)
    {
        var rev = state.Props["rev"]?.ToJsonString();
        if (!state.ValueApplied || rev != state.AppliedRev)
        {
            state.ValueApplied = true;
            state.AppliedRev = rev;
            return true;
        }
        return false;
    }

    /// <summary>Binds a property to a token for this update; the binding of the last update is kept when it is the same token.</summary>
    internal static void BindToken(NodeState state, StyledElement target, AvaloniaProperty property, ArcDesignTokenKind kind)
    {
        if (state.Bindings.TryGetValue((target, property), out var existing))
        {
            if (existing.Kind == kind)
            {
                existing.Used = true;
                return;
            }
            existing.Subscription.Dispose();
        }
        state.Bindings[(target, property)] = new TokenBinding(kind, ArcTokens.Bind(target, property, kind, BindingPriority.LocalValue));
    }

    internal static Control Placeholder(string message)
    {
        var text = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(4) };
        ArcTokens.Bind(text, TextBlock.ForegroundProperty, ArcDesignTokenKind.Danger, BindingPriority.LocalValue);
        return new Border { Child = text, BorderThickness = new Thickness(1), Padding = new Thickness(6), CornerRadius = new CornerRadius(4) }
            .Also(b => ArcTokens.Bind(b, Border.BorderBrushProperty, ArcDesignTokenKind.Danger, BindingPriority.LocalValue));
    }
}

/// <summary>One kind of node.</summary>
internal interface IUiComponent
{
    bool HasChildren => false;
    Control Create(NodeState state, UiRenderer renderer);
    void Update(Control control, NodeState state, UiRenderer renderer);
    void SetChildren(Control control, NodeState state, IReadOnlyList<Control> children) { }
}

internal sealed class UnknownComponent(string type) : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) =>
        UiRenderer.Placeholder($"This version of Octopus has no \"{type}\" component.");

    public void Update(Control control, NodeState state, UiRenderer renderer) { }
}

internal static class ControlExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}

/// <summary>Typed reads of node properties; a value of another type reads as absent.</summary>
internal static class UiProps
{
    public static string? Str(JsonObject p, string name) => p[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static double? Num(JsonObject p, string name) => p[name] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;
    public static bool? Bool(JsonObject p, string name) => p[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    public static JsonArray? Arr(JsonObject p, string name) => p[name] as JsonArray;
}

/// <summary>Layout properties every node understands (design 05 §3): sizes, spacing levels, visibility.</summary>
internal static class UiLayout
{
    /// <summary>Spacing level n → n × 4 device-independent pixels (Arc's Space1 … Space24 steps), capped at 12.</summary>
    public static double Space(double level) => Math.Clamp(level, 0, 12) * 4;

    public static Thickness Spacing(JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<double>(out var all) => new Thickness(Space(all)),
        JsonArray { Count: 2 } a when a[0] is JsonValue y && y.TryGetValue<double>(out var vy) && a[1] is JsonValue x && x.TryGetValue<double>(out var vx)
            => new Thickness(Space(vx), Space(vy)),
        _ => default,
    };

    public static void ApplyCommon(Control control, JsonObject p)
    {
        control.IsVisible = UiProps.Bool(p, "visible") ?? true;
        if (p["margin"] is { } margin) control.Margin = Spacing(margin);
        switch (p["width"])
        {
            case JsonValue v when v.TryGetValue<double>(out var w):
                control.Width = Math.Clamp(w, 0, 4000);
                break;
            case JsonValue v when v.TryGetValue<string>(out var s) && s == "fill":
                control.HorizontalAlignment = HorizontalAlignment.Stretch;
                break;
        }
        switch (p["height"])
        {
            case JsonValue v when v.TryGetValue<double>(out var h):
                control.Height = Math.Clamp(h, 0, 4000);
                break;
            case JsonValue v when v.TryGetValue<string>(out var s) && s == "fill":
                control.VerticalAlignment = VerticalAlignment.Stretch;
                break;
        }
        if (UiProps.Str(p, "tooltip") is { } tip) ToolTip.SetTip(control, tip);
        if (UiProps.Bool(p, "disabled") is { } disabled) control.IsEnabled = !disabled;
    }

    /// <summary>
    /// Lets a control with large text lay out its own lines. Arc's <c>Card</c> passes an inherited line height (19.6) to
    /// its content, and text set larger than body text inside it is cut in half (the bar chart's headline was).
    /// </summary>
    public static void OwnLineHeight(Control control) => control.SetValue(TextBlock.LineHeightProperty, double.NaN);

    public static HorizontalAlignment Horizontal(string? align) => align switch
    {
        "start" => HorizontalAlignment.Left,
        "center" => HorizontalAlignment.Center,
        "end" => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Stretch,
    };

    public static VerticalAlignment Vertical(string? align) => align switch
    {
        "start" => VerticalAlignment.Top,
        "center" => VerticalAlignment.Center,
        "end" => VerticalAlignment.Bottom,
        _ => VerticalAlignment.Stretch,
    };

    public static ArcDesignTokenKind? ColorToken(string? name) => name switch
    {
        "default" => ArcDesignTokenKind.Foreground,
        "secondary" => ArcDesignTokenKind.TextSecondary,
        "muted" => ArcDesignTokenKind.TextMuted,
        "accent" => ArcDesignTokenKind.Accent,
        "success" => ArcDesignTokenKind.Success,
        "warning" => ArcDesignTokenKind.Warning,
        "danger" => ArcDesignTokenKind.Danger,
        _ => null,
    };

    public static ArcDesignTokenKind? SurfaceToken(string? name) => name switch
    {
        "surface" => ArcDesignTokenKind.Surface,
        "surfaceMuted" => ArcDesignTokenKind.SurfaceMuted,
        "surfaceRaised" => ArcDesignTokenKind.SurfaceRaised,
        _ => null,
    };

    public static ArcDesignTokenKind? BorderToken(string? name) => name switch
    {
        "subtle" => ArcDesignTokenKind.BorderSubtle,
        "default" => ArcDesignTokenKind.Border,
        "strong" => ArcDesignTokenKind.BorderStrong,
        _ => null,
    };
}
