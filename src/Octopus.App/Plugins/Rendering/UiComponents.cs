using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using static Octopus.App.Plugins.Rendering.UiProps;
using ArcTabItem = TKCode.Arc.TabItem;
using ArcTreeView = TKCode.Arc.TreeView;

namespace Octopus.App.Plugins.Rendering;

/// <summary>
/// The component catalog of design/plugins/05-界面能力.md §4, each mapped to an Arc control. Where Arc has nothing
/// (dividers, the generic list) a plain Avalonia control with design tokens stands in; those gaps are listed in
/// design/plugins/10-分期与风险.md §3.
/// </summary>
internal static class UiComponents
{
    private static readonly Dictionary<string, IUiComponent> All = new(StringComparer.Ordinal)
    {
        ["Column"] = new Flex(Orientation.Vertical),
        ["Row"] = new Flex(Orientation.Horizontal),
        ["Form"] = new Flex(Orientation.Vertical),
        ["Field"] = new Flex(Orientation.Vertical),
        ["Grid"] = new GridComponent(),
        ["Stack"] = new StackComponent(),
        ["Scroll"] = new ScrollComponent(),
        ["Card"] = new CardComponent(),
        ["Section"] = new SectionComponent(),
        ["Toolbar"] = new Flex(Orientation.Horizontal, defaultGap: 1),
        ["Tabs"] = new TabsComponent(),
        ["Split"] = new SplitComponent(),
        ["Divider"] = new DividerComponent(),
        ["Spacer"] = new SpacerComponent(),
        ["Text"] = new TextComponent(),
        ["Markdown"] = new MarkdownComponent(),
        ["Code"] = new CodeComponent(),
        ["Icon"] = new IconComponent(),
        ["Badge"] = new BadgeComponent(),
        ["Kbd"] = new KbdComponent(),
        ["Progress"] = new ProgressComponent(),
        ["Spinner"] = new SpinnerComponent(),
        ["Skeleton"] = new SkeletonComponent(),
        ["Alert"] = new AlertComponent(),
        ["EmptyState"] = new EmptyStateComponent(),
        ["Link"] = new LinkComponent(),
        ["Tooltip"] = new TooltipComponent(),
        ["Meter"] = new MeterComponent(),
        ["Ring"] = new RingComponent(),
        ["Countdown"] = new CountdownComponent(),
        ["RelativeTime"] = new RelativeTimeComponent(),
        ["Stat"] = new StatComponent(),
        ["BarChart"] = new BarChartComponent(),
        ["Sparkline"] = new SparklineComponent(),
        ["JsonView"] = new JsonViewComponent(),
        ["Button"] = new ButtonComponent(icon: false),
        ["IconButton"] = new ButtonComponent(icon: true),
        ["Input"] = new TextFieldComponent<Input>(),
        ["Textarea"] = new TextFieldComponent<Textarea>(),
        ["Password"] = new TextFieldComponent<PasswordField>(),
        ["Search"] = new TextFieldComponent<SearchField>(),
        ["NumberField"] = new NumberComponent(),
        ["Select"] = new SelectComponent(),
        ["Combobox"] = new SelectComponent(),
        ["Checkbox"] = new CheckComponent(isSwitch: false),
        ["Switch"] = new CheckComponent(isSwitch: true),
        ["RadioGroup"] = new RadioComponent(),
        ["Segmented"] = new SegmentedComponent(),
        ["Slider"] = new SliderComponent(),
        ["TagInput"] = new TagInputComponent(),
        ["PathPicker"] = new PathPickerComponent(),
        ["List"] = new ListComponent(),
        ["Tree"] = new TreeComponent(),
        ["Table"] = new TableComponent(),
        ["Menu"] = new MenuComponent(),
        ["Popover"] = new PopoverComponent(),
    };

    public static IUiComponent? Find(string type) => All.GetValueOrDefault(type);

    public static IReadOnlyCollection<string> Types => All.Keys;
}

// ============================================================================ layout

/// <summary>Column and Row: children in a line, spaced by <c>gap</c>; a child with <c>grow</c> takes the free space.</summary>
internal sealed class Flex(Orientation orientation, double defaultGap = 0) : IUiComponent
{
    public bool HasChildren => true;

    public Control Create(NodeState state, UiRenderer renderer) => new Border { Child = new Grid() };

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var border = (Border)control;
        var p = state.Props;
        border.Padding = UiLayout.Spacing(p["padding"]);
        if (UiLayout.SurfaceToken(Str(p, "background")) is { } surface) UiRenderer.BindToken(state, border, Border.BackgroundProperty, surface);
        else border.Background = null;
        if (UiLayout.BorderToken(Str(p, "border")) is { } line)
        {
            border.BorderThickness = new Thickness(1);
            border.CornerRadius = new CornerRadius(6);
            UiRenderer.BindToken(state, border, Border.BorderBrushProperty, line);
        }
        else border.BorderThickness = default;
    }

    public void SetChildren(Control control, NodeState state, IReadOnlyList<Control> children)
    {
        var p = state.Props;
        var gap = UiLayout.Space(Num(p, "gap") ?? defaultGap);
        var wrap = Bool(p, "wrap") == true && orientation == Orientation.Horizontal;
        var border = (Border)control;
        if (wrap)
        {
            var panel = border.Child as WrapPanel ?? new WrapPanel();
            border.Child = panel;
            Replace(panel.Children, children);
            panel.ItemSpacing = gap;
            panel.LineSpacing = gap;
            return;
        }
        var grid = border.Child as Grid ?? new Grid();
        border.Child = grid;
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        grid.RowSpacing = orientation == Orientation.Vertical ? gap : 0;
        grid.ColumnSpacing = orientation == Orientation.Horizontal ? gap : 0;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var grow = child.Tag is NodeState s ? Num(s.Props, "grow") ?? 0 : 0;
            var length = grow > 0 ? new GridLength(grow, GridUnitType.Star) : GridLength.Auto;
            if (orientation == Orientation.Vertical)
            {
                grid.RowDefinitions.Add(new RowDefinition(length));
                Grid.SetRow(child, i);
                Grid.SetColumn(child, 0);
                if (Str(p, "align") is { } align) child.HorizontalAlignment = UiLayout.Horizontal(align);
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(length));
                Grid.SetColumn(child, i);
                Grid.SetRow(child, 0);
                child.VerticalAlignment = UiLayout.Vertical(Str(p, "align") ?? "center");
            }
        }
        if (orientation == Orientation.Horizontal && Str(p, "justify") is { } justify && children.All(c => c.Tag is not NodeState s || (Num(s.Props, "grow") ?? 0) == 0))
        {
            // "between": a star column between every pair pushes them apart; start/center/end align the whole row.
            if (justify == "between" && children.Count > 1)
            {
                grid.ColumnDefinitions.Clear();
                for (var i = 0; i < children.Count; i++)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition(i == children.Count - 1 ? GridLength.Auto : GridLength.Auto));
                    if (i < children.Count - 1) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                    Grid.SetColumn(children[i], i * 2);
                }
            }
            else
            {
                grid.HorizontalAlignment = UiLayout.Horizontal(justify);
            }
        }
        Replace(grid.Children, children);
    }

    internal static void Replace(Avalonia.Controls.Controls target, IReadOnlyList<Control> children)
    {
        if (target.Count == children.Count && target.Zip(children).All(pair => ReferenceEquals(pair.First, pair.Second)))
        {
            return;
        }
        target.Clear();
        foreach (var child in children)
        {
            if (child.Parent is Panel old) old.Children.Remove(child);
            else if (child.Parent is Decorator decorator) decorator.Child = null;
            else if (child.Parent is ContentControl content) content.Content = null;
            target.Add(child);
        }
    }
}

internal sealed class GridComponent : IUiComponent
{
    public bool HasChildren => true;
    public Control Create(NodeState state, UiRenderer renderer) => new Grid();
    public void Update(Control control, NodeState state, UiRenderer renderer) { }

    public void SetChildren(Control control, NodeState state, IReadOnlyList<Control> children)
    {
        var grid = (Grid)control;
        var p = state.Props;
        var gap = UiLayout.Space(Num(p, "gap") ?? 2);
        var columns = Num(p, "columns") is { } n ? (int)Math.Clamp(n, 1, 24) : Arr(p, "columns")?.Count ?? 2;
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var rows = (children.Count + columns - 1) / columns;
        for (var r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowSpacing = gap;
        grid.ColumnSpacing = gap;
        for (var i = 0; i < children.Count; i++)
        {
            Grid.SetColumn(children[i], i % columns);
            Grid.SetRow(children[i], i / columns);
        }
        Flex.Replace(grid.Children, children);
    }
}

internal sealed class StackComponent : IUiComponent
{
    public bool HasChildren => true;
    public Control Create(NodeState state, UiRenderer renderer) => new Panel();
    public void Update(Control control, NodeState state, UiRenderer renderer) { }
    public void SetChildren(Control control, NodeState state, IReadOnlyList<Control> children) => Flex.Replace(((Panel)control).Children, children);
}

internal sealed class ScrollComponent : IUiComponent
{
    public bool HasChildren => true;
    public Control Create(NodeState state, UiRenderer renderer) => new ScrollArea();

    public void Update(Control control, NodeState state, UiRenderer renderer) =>
        ((ScrollArea)control).Orientation = Str(state.Props, "direction") switch
        {
            "horizontal" => ScrollAreaOrientation.Horizontal,
            "both" => ScrollAreaOrientation.Both,
            _ => ScrollAreaOrientation.Vertical,
        };

    public void SetChildren(Control control, NodeState state, IReadOnlyList<Control> children)
    {
        var scroll = (ScrollArea)control;
        var child = children.FirstOrDefault();
        if (!ReferenceEquals(scroll.Content, child)) scroll.Content = child;
    }
}

internal sealed class CardComponent : IUiComponent
{
    public bool HasChildren => true;
    public Control Create(NodeState state, UiRenderer renderer) => new Card { Content = new StackPanel { Spacing = 8 } };

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var card = (Card)control;
        card.Title = Str(state.Props, "title");
        card.Description = Str(state.Props, "description");
    }

    public void SetChildren(Control control, NodeState state, IReadOnlyList<Control> children) =>
        Flex.Replace(((StackPanel)((Card)control).Content!).Children, children);
}

/// <summary>A titled group that folds; Arc's Accordion is an items control, so a header button and a panel stand in.</summary>
internal sealed class SectionComponent : IUiComponent
{
    public bool HasChildren => true;

    public Control Create(NodeState state, UiRenderer renderer)
    {
        var header = new TKCode.Arc.Button { Variant = ButtonVariant.Ghost, Size = ControlSize.Small, HorizontalAlignment = HorizontalAlignment.Left };
        var body = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        var root = new StackPanel { Children = { header, body } };
        header.Click += (_, _) =>
        {
            body.IsVisible = !body.IsVisible;
            header.Content = (body.IsVisible ? "▾ " : "▸ ") + (Str(state.Props, "title") ?? "");
        };
        state.Extra = body;
        return root;
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var body = (StackPanel)state.Extra!;
        if (UiRenderer.TakeValue(state)) body.IsVisible = Bool(state.Props, "expanded") ?? true;
        ((TKCode.Arc.Button)((StackPanel)control).Children[0]).Content = (body.IsVisible ? "▾ " : "▸ ") + (Str(state.Props, "title") ?? "");
    }

    public void SetChildren(Control control, NodeState state, IReadOnlyList<Control> children) => Flex.Replace(((StackPanel)state.Extra!).Children, children);
}

/// <summary>Tabs over one content node: the strip selects, the plugin renders what belongs to the selection.</summary>
internal sealed class TabsComponent : IUiComponent
{
    public bool HasChildren => true;

    public Control Create(NodeState state, UiRenderer renderer)
    {
        var tabs = new Tabs();
        var content = new ContentControl();
        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedItem is ArcTabItem { Tag: string id } && !Equals(state.Extra, "updating")) renderer.Fire(state, "onSelect", JsonValue.Create(id));
        };
        return new DockPanel { Children = { tabs.Also(t => DockPanel.SetDock(t, Dock.Top)), content } };
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var tabs = (Tabs)((DockPanel)control).Children[0];
        state.Extra = "updating";
        try
        {
            var items = (Arr(state.Props, "tabs") ?? []).OfType<JsonObject>().Select(t => new ArcTabItem { Header = Str(t, "title"), Tag = Str(t, "id") }).ToList();
            var ids = items.Select(i => (string?)i.Tag).ToList();
            var current = tabs.Items.OfType<ArcTabItem>().Select(i => (string?)i.Tag).ToList();
            if (!ids.SequenceEqual(current))
            {
                tabs.Items.Clear();
                foreach (var item in items) tabs.Items.Add(item);
            }
            var selected = Str(state.Props, "selected");
            if (selected is not null && tabs.Items.OfType<ArcTabItem>().FirstOrDefault(i => (string?)i.Tag == selected) is { } match) tabs.SelectedItem = match;
        }
        finally
        {
            state.Extra = null;
        }
    }

    public void SetChildren(Control control, NodeState state, IReadOnlyList<Control> children)
    {
        var content = (ContentControl)((DockPanel)control).Children[1];
        var child = children.FirstOrDefault();
        if (!ReferenceEquals(content.Content, child)) content.Content = child;
    }
}

/// <summary>Panes side by side with draggable splitters (Arc's ResizablePanels sizes in percent and adds restore tabs, see 09-实现状态 §6).</summary>
internal sealed class SplitComponent : IUiComponent
{
    public bool HasChildren => true;
    public Control Create(NodeState state, UiRenderer renderer) => new Grid();
    public void Update(Control control, NodeState state, UiRenderer renderer) { }

    public void SetChildren(Control control, NodeState state, IReadOnlyList<Control> children)
    {
        var grid = (Grid)control;
        var vertical = Str(state.Props, "direction") == "vertical";
        var sizes = Arr(state.Props, "sizes")?.Select(n => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : 1).ToList() ?? [];
        var all = new List<Control>();
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        for (var i = 0; i < children.Count; i++)
        {
            var size = new GridLength(i < sizes.Count && sizes[i] > 0 ? sizes[i] : 1, GridUnitType.Star);
            if (i > 0)
            {
                var splitter = new GridSplitter { ResizeDirection = vertical ? GridResizeDirection.Rows : GridResizeDirection.Columns };
                if (vertical) { grid.RowDefinitions.Add(new RowDefinition(4, GridUnitType.Pixel)); Grid.SetRow(splitter, grid.RowDefinitions.Count - 1); }
                else { grid.ColumnDefinitions.Add(new ColumnDefinition(4, GridUnitType.Pixel)); Grid.SetColumn(splitter, grid.ColumnDefinitions.Count - 1); }
                all.Add(splitter);
            }
            if (vertical) { grid.RowDefinitions.Add(new RowDefinition(size)); Grid.SetRow(children[i], grid.RowDefinitions.Count - 1); }
            else { grid.ColumnDefinitions.Add(new ColumnDefinition(size)); Grid.SetColumn(children[i], grid.ColumnDefinitions.Count - 1); }
            all.Add(children[i]);
        }
        grid.Children.Clear();
        foreach (var c in all)
        {
            if (c.Parent is Panel old) old.Children.Remove(c);
            grid.Children.Add(c);
        }
    }
}

/// <summary>A 1-pixel line in the border color. Arc has no divider component (09-实现状态 §6).</summary>
internal sealed class DividerComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new Border { Height = 1, Margin = new Thickness(0, 4) };
    public void Update(Control control, NodeState state, UiRenderer renderer) => UiRenderer.BindToken(state, control, Border.BackgroundProperty, ArcDesignTokenKind.BorderSubtle);
}

internal sealed class SpacerComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new Panel();
    public void Update(Control control, NodeState state, UiRenderer renderer) { }
}

// ============================================================================ display

internal sealed class TextComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new SelectableTextBlock();

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var text = (SelectableTextBlock)control;
        var p = state.Props;
        text.Text = Str(p, "value") ?? "";
        text.TextWrapping = Bool(p, "truncate") == true ? TextWrapping.NoWrap : TextWrapping.Wrap;
        text.TextTrimming = Bool(p, "truncate") == true ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        text.IsHitTestVisible = Bool(p, "selectable") != false;
        text.TextAlignment = Str(p, "align") switch { "end" => TextAlignment.Right, "center" => TextAlignment.Center, _ => TextAlignment.Left };
        var variant = Str(p, "variant") ?? "body";
        text.FontWeight = variant is "title" or "heading" ? FontWeight.SemiBold : FontWeight.Normal;
        UiRenderer.BindToken(state, text, TextBlock.FontSizeProperty, variant switch
        {
            "title" => ArcDesignTokenKind.TypeLG,
            "heading" => ArcDesignTokenKind.TypeBase,
            "caption" => ArcDesignTokenKind.TypeXS,
            _ => ArcDesignTokenKind.TypeSM,
        });
        if (variant == "mono") UiRenderer.BindToken(state, text, TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        else text.ClearValue(TextBlock.FontFamilyProperty);
        var color = UiLayout.ColorToken(Str(p, "color")) ?? (variant == "caption" ? ArcDesignTokenKind.TextSecondary : ArcDesignTokenKind.Foreground);
        UiRenderer.BindToken(state, text, TextBlock.ForegroundProperty, color);
    }
}

internal sealed class MarkdownComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new Markdown { OpensLinks = false };
    public void Update(Control control, NodeState state, UiRenderer renderer) => ((Markdown)control).Text = Str(state.Props, "source") ?? "";
}

internal sealed class CodeComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new CodeBlock();

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var code = (CodeBlock)control;
        code.Code = Str(state.Props, "source") ?? "";
        code.Language = Str(state.Props, "language");
        code.ShowLineNumbers = Bool(state.Props, "lineNumbers") ?? false;
    }
}

internal static class UiIcons
{
    /// <summary><c>lucide:chart-column</c> → <see cref="LucideIconKind.ChartColumn"/>; unknown names give null.</summary>
    public static LucideIconKind? Parse(string? reference)
    {
        if (reference is null || !reference.StartsWith("lucide:", StringComparison.Ordinal)) return null;
        var pascal = string.Concat(reference[7..].Split('-', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        return Enum.TryParse<LucideIconKind>(pascal, ignoreCase: false, out var kind) ? kind : null;
    }

    public static Control? Create(string? reference, double size = 16) =>
        Parse(reference) is { } kind ? new LucideIcon { Kind = kind, Size = size } : null;
}

internal sealed class IconComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new LucideIcon();

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var icon = (LucideIcon)control;
        icon.Kind = UiIcons.Parse(Str(state.Props, "icon")) ?? LucideIconKind.CircleHelp;
        icon.Size = Str(state.Props, "size") switch { "sm" => 14, "lg" => 22, _ => 16 };
        if (UiLayout.ColorToken(Str(state.Props, "color")) is { } color) UiRenderer.BindToken(state, icon, LucideIcon.ForegroundProperty, color);
    }
}

internal sealed class BadgeComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new Badge { Size = BadgeSize.Small };

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var badge = (Badge)control;
        badge.Content = Str(state.Props, "text") ?? "";
        badge.Tone = Str(state.Props, "color") switch
        {
            "success" => BadgeTone.Success,
            "warning" => BadgeTone.Warning,
            "danger" => BadgeTone.Danger,
            "accent" => BadgeTone.Info,
            _ => BadgeTone.Neutral,
        };
    }
}

internal sealed class KbdComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1), Child = new TextBlock() };

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var border = (Border)control;
        var text = (TextBlock)border.Child!;
        text.Text = state.Props["keys"] switch
        {
            JsonArray keys => string.Join(" ", keys.Select(k => k?.GetValue<string>())),
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => "",
        };
        UiRenderer.BindToken(state, border, Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        UiRenderer.BindToken(state, text, TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        UiRenderer.BindToken(state, text, TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeXS);
    }
}

internal sealed class ProgressComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new Progress { Maximum = 1 };

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var progress = (Progress)control;
        progress.Value = Math.Clamp(Num(state.Props, "value") ?? 0, 0, 1);
        progress.Label = Str(state.Props, "label");
        progress.IsValueVisible = Str(state.Props, "label") is not null;
    }
}

/// <summary>Indeterminate progress. Arc's Progress is determinate only; Avalonia's ProgressBar stands in.</summary>
internal sealed class SpinnerComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new ProgressBar { IsIndeterminate = true, MinWidth = 60, Height = 3 };
    public void Update(Control control, NodeState state, UiRenderer renderer) { }
}

internal sealed class SkeletonComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new Skeleton { IsLoading = true };
    public void Update(Control control, NodeState state, UiRenderer renderer) => ((Skeleton)control).Lines = (int)Math.Clamp(Num(state.Props, "lines") ?? 3, 1, 20);
}

internal sealed class AlertComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new Alert();

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var alert = (Alert)control;
        var p = state.Props;
        alert.Title = Str(p, "title");
        alert.Tone = Str(p, "kind") switch
        {
            "success" => AlertTone.Success,
            "warning" => AlertTone.Warning,
            "danger" => AlertTone.Danger,
            _ => AlertTone.Info,
        };
        alert.Content = Actions.Build(state, renderer, Str(p, "description"), Arr(p, "actions"));
    }
}

internal sealed class EmptyStateComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new EmptyState();

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var empty = (EmptyState)control;
        empty.Title = Str(state.Props, "title");
        empty.Description = Str(state.Props, "description");
        empty.Icon = UiIcons.Create(Str(state.Props, "icon"), 24);
        empty.Action = Actions.Build(state, renderer, null, Arr(state.Props, "actions"));
    }
}

/// <summary>Buttons for <c>actions: [{label, onClick}]</c>, optionally under a description.</summary>
internal static class Actions
{
    public static Control? Build(NodeState state, UiRenderer renderer, string? description, JsonArray? actions)
    {
        if (description is null && (actions is null || actions.Count == 0)) return null;
        var panel = new StackPanel { Spacing = 8 };
        if (description is not null) panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        if (actions is { Count: > 0 })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            for (var i = 0; i < actions.Count; i++)
            {
                if (actions[i] is not JsonObject action) continue;
                var index = i;
                var button = new TKCode.Arc.Button { Content = Str(action, "label"), Size = ControlSize.Small, Variant = index == 0 ? ButtonVariant.Primary : ButtonVariant.Secondary };
                button.Click += (_, _) =>
                {
                    if (UiRenderer.Handler((JsonObject)((JsonArray)state.Props["actions"]!)[index]!, "onClick") is { } id) renderer.Sink.Fire(id, []);
                };
                row.Children.Add(button);
            }
            panel.Children.Add(row);
        }
        return panel;
    }
}

internal sealed class LinkComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer)
    {
        var button = new TKCode.Arc.Button { Variant = ButtonVariant.Ghost, Size = ControlSize.Small, Padding = new Thickness(0), Cursor = new Cursor(StandardCursorType.Hand) };
        button.Click += (_, _) => renderer.Fire(state, "onClick");
        return button;
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var text = new TextBlock { Text = Str(state.Props, "label") ?? Str(state.Props, "text") ?? "", TextDecorations = TextDecorations.Underline };
        UiRenderer.BindToken(state, text, TextBlock.ForegroundProperty, ArcDesignTokenKind.Accent);
        ((TKCode.Arc.Button)control).Content = text;
    }
}

internal sealed class TooltipComponent : IUiComponent
{
    public bool HasChildren => true;
    public Control Create(NodeState state, UiRenderer renderer) => new ContentControl();
    public void Update(Control control, NodeState state, UiRenderer renderer) => ToolTip.SetTip(control, Str(state.Props, "text") ?? Str(state.Props, "tooltip"));

    public void SetChildren(Control control, NodeState state, IReadOnlyList<Control> children)
    {
        var host = (ContentControl)control;
        if (!ReferenceEquals(host.Content, children.FirstOrDefault())) host.Content = children.FirstOrDefault();
    }
}

/// <summary>A labeled usage bar (value of max) whose color follows the level.</summary>
internal sealed class MeterComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new Progress { IsValueVisible = true };

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var meter = (Progress)control;
        meter.Maximum = Num(state.Props, "max") ?? 100;
        meter.Value = Math.Clamp(Num(state.Props, "value") ?? 0, 0, meter.Maximum);
        meter.Label = Str(state.Props, "label") is { } label && Str(state.Props, "detail") is { } detail ? $"{label} · {detail}" : Str(state.Props, "label");
    }
}

internal sealed class RingComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new Gauge { Minimum = 0, Maximum = 100 }.Also(UiLayout.OwnLineHeight);

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var gauge = (Gauge)control;
        gauge.Value = Math.Clamp(Num(state.Props, "value") ?? 0, 0, 100);
        gauge.Label = Str(state.Props, "label");
        gauge.Tone = Str(state.Props, "level") switch { "warning" => GaugeTone.Warning, "danger" => GaugeTone.Danger, "success" => GaugeTone.Success, _ => GaugeTone.Accent };
        var size = Str(state.Props, "size") switch { "sm" => 28, "lg" => 96, _ => 56 };
        gauge.Width = size;
        gauge.Height = size;
    }
}

/// <summary>Counts down to a moment; the host ticks it, the plugin never re-renders for it (design 05 §2).</summary>
internal sealed class CountdownComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new Countdown().Also(UiLayout.OwnLineHeight);

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var countdown = (Countdown)control;
        countdown.Target = Num(state.Props, "to") is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null;
        countdown.Label = Str(state.Props, "label") ?? "";
    }
}

/// <summary>"3 minutes ago", refreshed by the host every 30 seconds.</summary>
internal sealed class RelativeTimeComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer)
    {
        var text = new TextBlock();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) => Refresh(text, state);
        text.AttachedToVisualTree += (_, _) => timer.Start();
        text.DetachedFromVisualTree += (_, _) => timer.Stop();
        return text;
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        UiRenderer.BindToken(state, control, TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        Refresh((TextBlock)control, state);
    }

    private static void Refresh(TextBlock text, NodeState state)
    {
        if (Num(state.Props, "at") is not { } ms) { text.Text = ""; return; }
        text.Text = Common.RelativeTime.Format(DateTimeOffset.FromUnixTimeMilliseconds((long)ms), DateTimeOffset.Now);
    }
}

internal sealed class StatComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new StackPanel { Spacing = 2, Children = { new TextBlock(), new TextBlock(), new TextBlock() } }.Also(UiLayout.OwnLineHeight);

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var panel = (StackPanel)control;
        var (label, value, detail) = ((TextBlock)panel.Children[0], (TextBlock)panel.Children[1], (TextBlock)panel.Children[2]);
        label.Text = Str(state.Props, "label");
        value.Text = Str(state.Props, "value") ?? Num(state.Props, "value")?.ToString(CultureInfo.CurrentCulture);
        value.FontWeight = FontWeight.SemiBold;
        detail.Text = Str(state.Props, "detail");
        detail.IsVisible = detail.Text is not null;
        UiRenderer.BindToken(state, label, TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        UiRenderer.BindToken(state, label, TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeXS);
        UiRenderer.BindToken(state, value, TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeXL);
        UiRenderer.BindToken(state, detail, TextBlock.ForegroundProperty, ArcDesignTokenKind.TextMuted);
        UiRenderer.BindToken(state, detail, TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeXS);
    }
}

internal sealed record ChartRow(string Category, double Value);

internal sealed class BarChartComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new BarChart
    {
        XSelector = r => ((ChartRow)r).Category,
        YSelector = r => ((ChartRow)r).Value,
        TickFormatter = Compact,
    }.Also(UiLayout.OwnLineHeight);

    private static readonly Func<ChartValueContext, string> CompactValue = context => Compact(context.Value);

    /// <summary>1,010,000 → "1.01M", 1,689,323,400 → "1.69B": at most two decimals, for ticks, headlines and cells.</summary>
    internal static string Compact(double value) => Math.Abs(value) switch
    {
        >= 1e12 => (value / 1e12).ToString("0.##", CultureInfo.CurrentCulture) + "T",
        >= 1e9 => (value / 1e9).ToString("0.##", CultureInfo.CurrentCulture) + "B",
        >= 1e6 => (value / 1e6).ToString("0.##", CultureInfo.CurrentCulture) + "M",
        >= 1e3 => (value / 1e3).ToString("0.##", CultureInfo.CurrentCulture) + "K",
        _ => value.ToString("0.##", CultureInfo.CurrentCulture),
    };

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var chart = (BarChart)control;
        var categories = (Arr(state.Props, "categories") ?? []).Select(c => c?.GetValue<string>() ?? "").ToList();
        var series = (Arr(state.Props, "series") ?? []).OfType<JsonObject>().ToList();
        // Arc's bar chart shows one measure (design 05 §4 notes it): several series are added up per category.
        var totals = categories.Select((c, i) => new ChartRow(c, series.Sum(s => Arr(s, "values") is { } v && i < v.Count && v[i] is JsonValue n && n.TryGetValue<double>(out var d) ? d : 0))).ToList();
        // Only a real change: a new list (or a new formatter below) restarts the headline's rolling digits.
        if (chart.ItemsSource is not List<ChartRow> current || !current.SequenceEqual(totals)) chart.ItemsSource = totals;
        // plotHeight, not height: the layout property "height" sizes the whole control (headline and axis included).
        if (Num(state.Props, "plotHeight") is { } h) chart.PlotHeight = Math.Clamp(h, 60, 1200);
        chart.Label = Str(state.Props, "label") ?? "";
        // The large average readout only when asked: in a card it competes with the card's own title.
        chart.ShowAverage = Bool(state.Props, "showAverage") ?? false;
        // compact: the headline and the hovered value read 1.01M instead of 1,010,000.
        var formatter = Bool(state.Props, "compact") == true ? CompactValue : null;
        if (!ReferenceEquals(chart.ValueFormatter, formatter)) chart.ValueFormatter = formatter!;
        if (Str(state.Props, "averageLabel") is { } averageLabel) chart.AverageLabel = averageLabel;
        if (Str(state.Props, "unit") is { } unit) chart.Unit = unit;
        if (Str(state.Props, "valueLabel") is { } valueLabel) chart.ValueLabel = valueLabel;
        chart.Period = Str(state.Props, "period") ?? "";
    }
}

internal sealed class SparklineComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new Sparkline { YSelector = v => (double)v }.Also(UiLayout.OwnLineHeight);

    public void Update(Control control, NodeState state, UiRenderer renderer) =>
        ((Sparkline)control).ItemsSource = (Arr(state.Props, "values") ?? []).Select(v => v is JsonValue n && n.TryGetValue<double>(out var d) ? (object)d : 0d).ToList();
}

internal sealed class JsonViewComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer) => new JsonViewer { ExpandDepth = 1 };
    public void Update(Control control, NodeState state, UiRenderer renderer) => ((JsonViewer)control).Value = state.Props["value"]?.DeepClone();
}

// ============================================================================ input

internal sealed class ButtonComponent(bool icon) : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer)
    {
        var button = new TKCode.Arc.Button();
        button.Click += (_, _) => renderer.Fire(state, "onClick");
        return button;
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var button = (TKCode.Arc.Button)control;
        var p = state.Props;
        var glyph = UiIcons.Create(Str(p, "icon"), 16);
        if (icon)
        {
            button.Content = glyph;
            button.Variant = ButtonVariant.Ghost;
            ToolTip.SetTip(button, Str(p, "tooltip"));
        }
        else
        {
            button.Content = glyph is null
                ? Str(p, "label")
                : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { glyph, new TextBlock { Text = Str(p, "label"), VerticalAlignment = VerticalAlignment.Center } } };
            button.Variant = Str(p, "variant") switch
            {
                "primary" => ButtonVariant.Primary,
                "ghost" => ButtonVariant.Ghost,
                "danger" => ButtonVariant.Danger,
                _ => ButtonVariant.Secondary,
            };
        }
        button.Size = Str(p, "size") switch { "md" => ControlSize.Medium, "lg" => ControlSize.Large, _ => ControlSize.Small };
        button.IsLoading = Bool(p, "loading") ?? false;
    }
}

/// <summary>
/// Input, Textarea, Password, Search. The text belongs to the host: typing never waits for the plugin, which hears
/// about it 100 ms after the last key. A new <c>rev</c> overwrites the text (design 05 §2).
/// </summary>
internal sealed class TextFieldComponent<T> : IUiComponent where T : FieldTextBox, new()
{
    public Control Create(NodeState state, UiRenderer renderer)
    {
        var field = new T();
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            renderer.Fire(state, "onChange", JsonValue.Create(field.Text ?? ""));
        };
        field.TextChanged += (_, _) =>
        {
            if (Equals(state.Extra, "applying")) return;
            debounce.Stop();
            debounce.Start();
        };
        field.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && field is not Textarea)
            {
                debounce.Stop();
                renderer.Fire(state, "onChange", JsonValue.Create(field.Text ?? ""));
                renderer.Fire(state, "onSubmit", JsonValue.Create(field.Text ?? ""));
            }
        };
        return field;
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var field = (T)control;
        var p = state.Props;
        field.Label = Str(p, "label");
        field.IsLabelVisible = Str(p, "label") is not null;
        field.Placeholder = Str(p, "placeholder");
        field.Description = Str(p, "description");
        field.ErrorMessage = Str(p, "error");
        field.IsInvalid = Str(p, "error") is not null;
        if (field is Textarea textarea)
        {
            textarea.AcceptsReturn = true;
            if (Num(p, "rows") is { } rows) textarea.FrameMinHeight = rows * 20;
            if (Bool(p, "mono") == true) UiRenderer.BindToken(state, textarea, TextBox.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        }
        if (UiRenderer.TakeValue(state))
        {
            state.Extra = "applying";
            try { field.Text = Str(p, "value") ?? ""; }
            finally { state.Extra = null; }
        }
        if (Bool(p, "autoFocus") == true && !field.IsFocused) Dispatcher.UIThread.Post(() => field.Focus());
    }
}

internal sealed class NumberComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer)
    {
        var field = new NumberField();
        field.ValueChanged += (_, _) =>
        {
            if (!Equals(state.Extra, "applying")) renderer.Fire(state, "onChange", JsonValue.Create((double)field.Value));
        };
        return field;
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var field = (NumberField)control;
        var p = state.Props;
        field.Label = Str(p, "label");
        field.IsLabelVisible = Str(p, "label") is not null;
        if (Num(p, "min") is { } min) field.Minimum = (decimal)min;
        if (Num(p, "max") is { } max) field.Maximum = (decimal)max;
        if (Num(p, "step") is { } step) field.Step = (decimal)step;
        if (UiRenderer.TakeValue(state) && Num(p, "value") is { } value)
        {
            state.Extra = "applying";
            try { field.Value = (decimal)value; }
            finally { state.Extra = null; }
        }
    }
}

internal sealed record Choice(string Value, string Label, string? Description);

internal sealed class SelectComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer)
    {
        var select = new Select { TextSelector = o => ((Choice)o!).Label, ValueSelector = o => ((Choice)o!).Value };
        select.SelectionChanged += (_, _) =>
        {
            if (!Equals(state.Extra, "applying") && select.SelectedItem is Choice choice) renderer.Fire(state, "onChange", JsonValue.Create(choice.Value));
        };
        return select;
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var select = (Select)control;
        var p = state.Props;
        select.Label = Str(p, "label");
        select.Placeholder = Str(p, "placeholder");
        var choices = Choices(p);
        state.Extra = "applying";
        try
        {
            if (select.ItemsSource is not List<Choice> old || !old.SequenceEqual(choices)) select.ItemsSource = choices;
            if (UiRenderer.TakeValue(state)) select.SelectedItem = choices.FirstOrDefault(c => c.Value == Str(p, "value"));
        }
        finally
        {
            state.Extra = null;
        }
    }

    internal static List<Choice> Choices(JsonObject p) =>
        (Arr(p, "options") ?? []).OfType<JsonObject>().Select(o => new Choice(Str(o, "value") ?? "", Str(o, "label") ?? Str(o, "value") ?? "", Str(o, "description"))).ToList();
}

internal sealed class CheckComponent(bool isSwitch) : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer)
    {
        Avalonia.Controls.Primitives.ToggleButton toggle = isSwitch ? new Switch() : new Checkbox();
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (!Equals(state.Extra, "applying")) renderer.Fire(state, "onChange", JsonValue.Create(toggle.IsChecked == true));
        };
        return toggle;
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var toggle = (Avalonia.Controls.Primitives.ToggleButton)control;
        toggle.Content = Str(state.Props, "label");
        if (toggle is Checkbox box) box.Description = Str(state.Props, "description");
        if (UiRenderer.TakeValue(state))
        {
            state.Extra = "applying";
            try { toggle.IsChecked = Bool(state.Props, "value") ?? false; }
            finally { state.Extra = null; }
        }
    }
}

internal sealed class RadioComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer)
    {
        var group = new RadioGroup();
        group.SelectionChanged += (_, _) =>
        {
            if (!Equals(state.Extra, "applying") && group.SelectedItem is RadioOption option) renderer.Fire(state, "onChange", JsonValue.Create(option.Value as string));
        };
        return group;
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var group = (RadioGroup)control;
        group.Header = Str(state.Props, "label");
        state.Extra = "applying";
        try
        {
            var options = SelectComponent.Choices(state.Props).Select(c => new RadioOption { Value = c.Value, Label = c.Label, Description = c.Description }).ToList();
            if (group.Items.Count != options.Count || !group.Items.OfType<RadioOption>().Select(o => o.Value).SequenceEqual(options.Select(o => o.Value)))
            {
                group.Items.Clear();
                foreach (var option in options) group.Items.Add(option);
            }
            if (UiRenderer.TakeValue(state)) group.SelectedItem = group.Items.OfType<RadioOption>().FirstOrDefault(o => (string?)o.Value == Str(state.Props, "value"));
        }
        finally
        {
            state.Extra = null;
        }
    }
}

internal sealed class SegmentedComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer)
    {
        var segmented = new SegmentedControl();
        segmented.SelectionChanged += (_, _) =>
        {
            if (!Equals(state.Extra, "applying") && segmented.SelectedItem is SegmentedOption option) renderer.Fire(state, "onChange", JsonValue.Create(option.Value as string));
        };
        return segmented;
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var segmented = (SegmentedControl)control;
        state.Extra = "applying";
        try
        {
            var options = SelectComponent.Choices(state.Props);
            if (segmented.Items.Count != options.Count || !segmented.Items.OfType<SegmentedOption>().Select(o => o.Value as string).SequenceEqual(options.Select(o => o.Value)))
            {
                segmented.Items.Clear();
                foreach (var option in options) segmented.Items.Add(new SegmentedOption { Value = option.Value, Label = option.Label });
            }
            if (UiRenderer.TakeValue(state)) segmented.SelectedItem = segmented.Items.OfType<SegmentedOption>().FirstOrDefault(o => (string?)o.Value == Str(state.Props, "value"));
        }
        finally
        {
            state.Extra = null;
        }
    }
}

internal sealed class SliderComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer)
    {
        var slider = new TKCode.Arc.Slider();
        slider.ValueCommitted += (_, _) => renderer.Fire(state, "onChange", JsonValue.Create(slider.Value));
        return slider;
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var slider = (TKCode.Arc.Slider)control;
        slider.Label = Str(state.Props, "label");
        slider.Minimum = Num(state.Props, "min") ?? 0;
        slider.Maximum = Num(state.Props, "max") ?? 100;
        if (Num(state.Props, "step") is { } step) slider.SmallChange = step;
        if (UiRenderer.TakeValue(state)) slider.Value = Num(state.Props, "value") ?? slider.Minimum;
    }
}

internal sealed class TagInputComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer)
    {
        var tags = new TagInput();
        tags.ValueChanged += (_, _) =>
        {
            if (Equals(state.Extra, "applying")) return;
            var array = new JsonArray();
            foreach (var tag in tags.Value ?? []) array.Add((JsonNode)JsonValue.Create(tag));
            renderer.Fire(state, "onChange", array);
        };
        return tags;
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var tags = (TagInput)control;
        tags.Label = Str(state.Props, "label");
        tags.IsLabelVisible = Str(state.Props, "label") is not null;
        tags.Placeholder = Str(state.Props, "placeholder");
        if (UiRenderer.TakeValue(state))
        {
            state.Extra = "applying";
            try { tags.Value = (Arr(state.Props, "value") ?? []).Select(v => v?.GetValue<string>() ?? "").ToList(); }
            finally { state.Extra = null; }
        }
    }
}

/// <summary>A path field with a "Choose…" button that opens the system picker; the chosen path becomes the plugin's (design 04 §3.5).</summary>
internal sealed class PathPickerComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer)
    {
        var field = new Input { IsReadOnly = true };
        var choose = new TKCode.Arc.Button { Content = "…", Size = ControlSize.Small, Variant = ButtonVariant.Secondary, VerticalAlignment = VerticalAlignment.Bottom };
        choose.Click += async (_, _) =>
        {
            var picked = await renderer.Sink.PickPathAsync(Str(state.Props, "kind") == "folder", Str(state.Props, "label"));
            if (picked is null) return;
            field.Text = picked;
            renderer.Fire(state, "onChange", JsonValue.Create(picked));
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        grid.Children.Add(field);
        Grid.SetColumn(choose, 1);
        grid.Children.Add(choose);
        state.Extra = field;
        return grid;
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var field = (Input)state.Extra!;
        field.Label = Str(state.Props, "label");
        field.IsLabelVisible = Str(state.Props, "label") is not null;
        if (UiRenderer.TakeValue(state)) field.Text = Str(state.Props, "value") ?? "";
    }
}

// ============================================================================ data

internal sealed class ListRow(JsonObject item)
{
    public JsonObject Item { get; } = item;
    public string Id => Str(Item, "id") ?? "";
    public string Title => Str(Item, "title") ?? "";
    public string? Subtitle => Str(Item, "subtitle") ?? Str(Item, "description");
    public string? Badge => Item["badge"] is JsonValue v ? v.ToString() : null;
    public override bool Equals(object? obj) => obj is ListRow other && JsonNode.DeepEquals(Item, other.Item);
    public override int GetHashCode() => Id.GetHashCode(StringComparison.Ordinal);
}

/// <summary>A virtualized list with fixed rows (icon, title, subtitle, badge, trailing actions); see design 05 §5.</summary>
internal sealed class ListComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer)
    {
        var list = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncDataTemplate<ListRow>((row, _) => Row(row, renderer), supportsRecycling: false),
        };
        list.SelectionChanged += (_, _) =>
        {
            if (!Equals(state.Extra, "applying") && list.SelectedItem is ListRow row) renderer.Fire(state, "onSelect", JsonValue.Create(row.Id));
        };
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is ListRow row) renderer.Fire(state, "onActivate", JsonValue.Create(row.Id)); };
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter && list.SelectedItem is ListRow row) renderer.Fire(state, "onActivate", JsonValue.Create(row.Id)); };
        return list;
    }

    private static Control Row(ListRow row, UiRenderer renderer)
    {
        if (row is null) return new Panel();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(2, 3) };
        if (UiIcons.Create(Str(row.Item, "icon")) is { } icon)
        {
            icon.Margin = new Thickness(0, 0, 8, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(icon);
        }
        var texts = new StackPanel { Spacing = 1 };
        texts.Children.Add(new TextBlock { Text = row.Title, TextTrimming = TextTrimming.CharacterEllipsis });
        if (row.Subtitle is { } subtitle)
        {
            var sub = new TextBlock { Text = subtitle, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
            ArcTokens.Bind(sub, TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary, Avalonia.Data.BindingPriority.LocalValue);
            texts.Children.Add(sub);
        }
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        if (row.Badge is { } badge)
        {
            var b = new Badge { Content = badge, Size = BadgeSize.Small, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(b, 2);
            grid.Children.Add(b);
        }
        if (row.Item["trailing"] is JsonArray trailing && trailing.Count > 0)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 0, 0, 0) };
            foreach (var action in trailing.OfType<JsonObject>())
            {
                var button = new TKCode.Arc.Button { Content = UiIcons.Create(Str(action, "icon"), 14), Variant = ButtonVariant.Ghost, Size = ControlSize.Small };
                ToolTip.SetTip(button, Str(action, "tooltip"));
                var command = Str(action, "command");
                var args = action["args"] as JsonArray;
                button.Click += (_, e) =>
                {
                    e.Handled = true;
                    if (command is not null) renderer.Sink.ExecuteCommand(command, args is null ? [] : (JsonArray)args.DeepClone());
                };
                actions.Children.Add(button);
            }
            Grid.SetColumn(actions, 3);
            grid.Children.Add(actions);
        }
        return grid;
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var list = (ListBox)control;
        var rows = (Arr(state.Props, "items") ?? []).OfType<JsonObject>().Take(5000).Select(i => new ListRow((JsonObject)i.DeepClone())).ToList();
        state.Extra = "applying";
        try
        {
            if (list.ItemsSource is not List<ListRow> old || !old.SequenceEqual(rows))
            {
                var selected = (list.SelectedItem as ListRow)?.Id;
                list.ItemsSource = rows;
                if (selected is not null) list.SelectedItem = rows.FirstOrDefault(r => r.Id == selected);
            }
            if (UiRenderer.TakeValue(state) && Str(state.Props, "selected") is { } id) list.SelectedItem = rows.FirstOrDefault(r => r.Id == id);
        }
        finally
        {
            state.Extra = null;
        }
    }
}

internal sealed class TreeComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer)
    {
        var tree = new ArcTreeView
        {
            HeaderSelector = o => ((ListRow)o).Title,
            ChildrenSelector = o => ((ListRow)o).Item["children"] is JsonArray children ? children.OfType<JsonObject>().Select(c => new ListRow(c)).ToList() : null,
            RowHeight = 26,
        };
        tree.SelectionChanged += (_, _) => { if (tree.SelectedItem is ListRow row) renderer.Fire(state, "onSelect", JsonValue.Create(row.Id)); };
        tree.ItemInvoked += (_, _) => { if (tree.SelectedItem is ListRow row) renderer.Fire(state, "onActivate", JsonValue.Create(row.Id)); };
        return tree;
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var tree = (ArcTreeView)control;
        var rows = (Arr(state.Props, "items") ?? []).OfType<JsonObject>().Select(i => new ListRow((JsonObject)i.DeepClone())).ToList();
        if (tree.ItemsSource is not List<ListRow> old || !old.SequenceEqual(rows)) tree.ItemsSource = rows;
    }
}

internal sealed class TableRow(JsonNode? id, JsonArray cells)
{
    public JsonNode? Id { get; } = id;
    public JsonArray Cells { get; } = cells;
}

internal sealed class TableComponent : IUiComponent
{
    public Control Create(NodeState state, UiRenderer renderer)
    {
        var table = new Table { IsStriped = true, RowKeySelector = r => ((TableRow)r).Id?.ToJsonString() ?? "" };
        table.SortChanged += (_, _) =>
        {
            if (table.SortKey is { } key) renderer.Fire(state, "onSort", JsonValue.Create(key), JsonValue.Create(table.SortDirection.ToString().ToLowerInvariant()));
        };
        return table;
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var table = (Table)control;
        var columns = (Arr(state.Props, "columns") ?? []).OfType<JsonObject>().ToList();
        var signature = string.Join("|", columns.Select(c => $"{Str(c, "id")}:{Str(c, "title")}:{Str(c, "format")}"));
        if (!Equals(state.Extra, signature))
        {
            table.Columns.Clear();
            for (var i = 0; i < columns.Count; i++)
            {
                var index = i;
                // format "compact": 1,689,323,400 shows as 1.69B (sorting still uses the number).
                var compact = Str(columns[i], "format") == "compact";
                table.Columns.Add(new DataTableColumn
                {
                    Header = Str(columns[i], "title") ?? "",
                    Key = Str(columns[i], "id"),
                    IsSortable = Bool(columns[i], "sortable") ?? false,
                    IsNumeric = Str(columns[i], "type") == "number" || Str(columns[i], "align") == "end",
                    ValueSelector = r => ((TableRow)r).Cells.Count > index ? Display(((TableRow)r).Cells[index], compact) : "",
                    SortValueSelector = r => ((TableRow)r).Cells.Count > index && ((TableRow)r).Cells[index] is JsonValue v && v.TryGetValue<double>(out var d) ? d : (object?)Display(((TableRow)r).Cells.ElementAtOrDefault(index)),
                });
            }
            state.Extra = signature;
        }
        table.ItemsSource = (Arr(state.Props, "rows") ?? []).OfType<JsonObject>().Take(5000)
            .Select(r => new TableRow(r["id"]?.DeepClone(), r["cells"] as JsonArray is { } cells ? (JsonArray)cells.DeepClone() : [])).ToList();
    }

    private static string Display(JsonNode? cell, bool compact = false) => cell switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when compact && v.TryGetValue<double>(out var c) => BarChartComponent.Compact(c),
        // Whole numbers with group separators (2,008,910); fractions as they are.
        JsonValue v when v.TryGetValue<double>(out var d) => d == Math.Floor(d) && Math.Abs(d) < 1e15 ? d.ToString("N0", CultureInfo.CurrentCulture) : d.ToString("G", CultureInfo.CurrentCulture),
        _ => cell.ToJsonString(),
    };
}

/// <summary>A dropdown menu on any node: the first child is the trigger.</summary>
internal sealed class MenuComponent : IUiComponent
{
    public bool HasChildren => true;
    public Control Create(NodeState state, UiRenderer renderer) => new DropdownMenu();

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        var menu = (DropdownMenu)control;
        menu.ItemsSource = Entries(Arr(state.Props, "items") ?? [], renderer).ToList();
    }

    private static IEnumerable<MenuEntry> Entries(JsonArray items, UiRenderer renderer)
    {
        foreach (var item in items.OfType<JsonObject>())
        {
            var entry = new MenuEntry
            {
                Header = Str(item, "label"),
                Icon = UiIcons.Create(Str(item, "icon"), 14),
                IsDestructive = Bool(item, "danger") ?? false,
                HasSeparatorBefore = Bool(item, "separatorBefore") ?? false,
            };
            if (item["children"] is JsonArray children) entry.ItemsSource = Entries(children, renderer).ToList();
            var handler = UiRenderer.Handler(item, "onSelect");
            var command = Str(item, "command");
            var args = item["args"] as JsonArray;
            entry.Click += (_, _) =>
            {
                if (handler is not null) renderer.Sink.Fire(handler, []);
                else if (command is not null) renderer.Sink.ExecuteCommand(command, args is null ? [] : (JsonArray)args.DeepClone());
            };
            yield return entry;
        }
    }

    public void SetChildren(Control control, NodeState state, IReadOnlyList<Control> children)
    {
        var menu = (DropdownMenu)control;
        if (!ReferenceEquals(menu.Trigger, children.FirstOrDefault())) menu.Trigger = children.FirstOrDefault();
    }
}

/// <summary>A popover: first child is the trigger, second the content; open state belongs to the host after the first render.</summary>
internal sealed class PopoverComponent : IUiComponent
{
    public bool HasChildren => true;

    public Control Create(NodeState state, UiRenderer renderer)
    {
        var popover = new Popover();
        popover.Opened += (_, _) => renderer.Fire(state, "onOpenChange", JsonValue.Create(true));
        popover.Closed += (_, _) => renderer.Fire(state, "onOpenChange", JsonValue.Create(false));
        return popover;
    }

    public void Update(Control control, NodeState state, UiRenderer renderer)
    {
        if (UiRenderer.TakeValue(state) && Bool(state.Props, "open") is { } open) ((Popover)control).IsOpen = open;
    }

    public void SetChildren(Control control, NodeState state, IReadOnlyList<Control> children)
    {
        var popover = (Popover)control;
        var trigger = children.ElementAtOrDefault(0);
        var content = children.ElementAtOrDefault(1);
        if (!ReferenceEquals(popover.Trigger, trigger)) popover.Trigger = trigger;
        if (!ReferenceEquals(popover.Content, content)) popover.Content = content;
    }
}
