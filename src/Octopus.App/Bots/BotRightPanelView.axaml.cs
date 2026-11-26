using System.Linq.Expressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Octopus.App.Explorer;
using Octopus.App.Localization;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.Localization;

namespace Octopus.App.Bots;

public partial class BotRightPanelView : UserControl
{
    public static readonly StyledProperty<double> TrailingInsetProperty =
        AvaloniaProperty.Register<BotRightPanelView, double>(nameof(TrailingInset), 44);

    private BotRightPanelViewModel? _model;
    private Window? _window;
    private bool _trailingInsetScheduled;

    static BotRightPanelView()
    {
        TrailingInsetProperty.Changed.AddClassHandler<BotRightPanelView>((view, _) => view.ApplyTrailingInset());
    }

    public BotRightPanelView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyTrailingInset();
        Tabs.SizeChanged += (_, _) => { if (!Tabs.IsLabelAnimating) ApplyTrailingInset(); };
        Tabs.LabelsSettled += ScheduleTrailingInset;
        ArcLanguage.Changed += (_, _) => BuildTabs();
    }

    public double TrailingInset
    {
        get => GetValue(TrailingInsetProperty);
        set => SetValue(TrailingInsetProperty, value);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _model = DataContext as BotRightPanelViewModel;
        BuildTabs();
        ApplyTrailingInset();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = this.FindAncestorOfType<Window>();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTopBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && _window is not null)
        {
            _window.BeginMoveDrag(e);
        }
    }

    private void BuildTabs()
    {
        if (_model is null) return;
        var wanted = new List<ToggleGroupOption>
        {
            TabOption("home", LucideIconKind.House, AppLangResourceKind.BotRightTabHome, m => m.IsHomeTab, m => m.NeedsCountText, m => m.HasNeedsCount, ArcDesignTokenKind.Warning),
            TabOption("connectors", LucideIconKind.Plug, AppLangResourceKind.BotRightTabConnectors, m => m.IsConnectorsTab),
            TabOption("chats", LucideIconKind.MessageSquare, AppLangResourceKind.BotRightTabChats, m => m.IsChatsTab),
            TabOption("files", LucideIconKind.Files, AppLangResourceKind.BotRightTabFiles, m => m.IsFilesTab),
        };
        Tabs.ReplaceOptions(wanted, _model.TabKey);
        ScheduleTrailingInset();
    }

    private ToggleGroupOption TabOption(
        string value,
        LucideIconKind icon,
        AppLangResourceKind label,
        Expression<Func<BotRightPanelViewModel, bool>> isSelected,
        Expression<Func<BotRightPanelViewModel, string>>? count = null,
        Expression<Func<BotRightPanelViewModel, bool>>? hasCount = null,
        ArcDesignTokenKind countColor = ArcDesignTokenKind.TextMuted)
    {
        var name = AppLangResources.Get(label);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        content.Children.Add(new LucideIcon { Kind = icon, Size = 15, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TabLabelReveal
        {
            VerticalAlignment = VerticalAlignment.Center,
            [!TabLabelReveal.IsExpandedProperty] = CompiledBinding.Create(isSelected, source: _model!),
            Child = new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        if (count is not null && hasCount is not null)
        {
            var number = new TextBlock
            {
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                [!TextBlock.TextProperty] = CompiledBinding.Create(count, source: _model!),
                [!TextBlock.IsVisibleProperty] = CompiledBinding.Create(hasCount, source: _model!),
            };
            ArcTokens.Bind(number, TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeXS);
            ArcTokens.Bind(number, TextBlock.ForegroundProperty, countColor);
            content.Children.Add(number);
        }
        return new ToggleGroupOption { Value = value, Label = name, Title = name, Content = content };
    }

    private void ScheduleTrailingInset()
    {
        if (_trailingInsetScheduled) return;
        _trailingInsetScheduled = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _trailingInsetScheduled = false;
            ApplyTrailingInset();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void ApplyTrailingInset()
    {
        var tabsWidth = Tabs.ComputeMaxSettledWidth();
        var need = Tabs.Margin.Left + tabsWidth + 6;
        var available = Bounds.Width - TrailingInset;
        var isWrapped = Grid.GetRow(Tabs) == 1;
        var fits = Bounds.Width <= 0 || (isWrapped ? need + 8 <= available : need <= available);
        var row = fits ? 0 : 1;
        if (Grid.GetRow(Tabs) == row) return;
        Grid.SetRow(Tabs, row);
        Tabs.Margin = fits ? new Thickness(8, 0, 0, 0) : new Thickness(8, 0, 0, 6);
    }
}
