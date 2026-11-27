using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Octopus.App.Plugins.Settings;

/// <summary>Settings → Plugins.</summary>
public partial class PluginsPageView : UserControl
{
    public static readonly StyledProperty<int> FeaturedGridColumnsProperty =
        AvaloniaProperty.Register<PluginsPageView, int>(nameof(FeaturedGridColumns), defaultValue: 2);

    public static readonly StyledProperty<int> MarketGridColumnsProperty =
        AvaloniaProperty.Register<PluginsPageView, int>(nameof(MarketGridColumns), defaultValue: 3);

    public PluginsPageView()
    {
        InitializeComponent();
        SizeChanged += OnViewSizeChanged;
    }

    public int FeaturedGridColumns
    {
        get => GetValue(FeaturedGridColumnsProperty);
        private set => SetValue(FeaturedGridColumnsProperty, value);
    }

    public int MarketGridColumns
    {
        get => GetValue(MarketGridColumnsProperty);
        private set => SetValue(MarketGridColumnsProperty, value);
    }

    private void OnViewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        if (width <= 0) return;
        FeaturedGridColumns = width < 580 ? 1 : 2;
        MarketGridColumns = width < 480 ? 1 : width < 720 ? 2 : 3;
    }

    private void OnMarketplaceCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // Clicking the card's Install/Upgrade button handles its own command; don't also open the detail dialog.
        if (e.Source is Visual visual && visual.FindAncestorOfType<Avalonia.Controls.Button>(includeSelf: true) is not null)
        {
            return;
        }
        if (sender is Control { DataContext: MarketplacePluginRowViewModel row })
        {
            row.OpenDetailsCommand.Execute(null);
            e.Handled = true;
        }
    }
}
