using Avalonia.Controls;
using Avalonia.Interactivity;
using Octopus.App.Localization;
using TKCode.Arc;

namespace Octopus.App.Explorer.Tunnels;

/// <summary>The tunnels tab: the tunnels of the project's SSH host, each with a switch, and the actions on all of them.</summary>
public partial class TunnelsTabView : UserControl
{
    public TunnelsTabView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not TunnelsPanelViewModel panel) return;

        var more = new MenuSurface();
        more.Items.Add(new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.TunnelsNew), Icon = ArcIcons.Plus, Command = panel.AddCommand });
        more.Items.Add(new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.TunnelsStartAll), Icon = ArcIcons.Play, Command = panel.StartAllCommand, HasSeparatorBefore = true });
        more.Items.Add(new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.TunnelsStopAll), Icon = ArcIcons.Square, Command = panel.StopAllCommand });
        DropdownMenu.SetMenu(MoreButton, more);
    }

    /// <summary>The menu of a row is built when its button is created: it needs the row's own commands.</summary>
    private void OnRowMoreLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TunnelRowViewModel row } button) return;
        var menu = new MenuSurface();
        if (row.CanOpen)
        {
            menu.Items.Add(new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.TunnelsOpen), Icon = ArcIcons.ExternalLink, Command = row.OpenCommand });
        }
        menu.Items.Add(new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.TunnelsCopyAddress), Icon = ArcIcons.Copy, Command = row.CopyAddressCommand });
        menu.Items.Add(new MenuEntry
        {
            Header = AppLangResources.Get(AppLangResourceKind.TunnelsShowLog),
            Icon = ArcIcons.FileText,
            Command = row.ToggleLogCommand,
            HasSeparatorBefore = true,
        });
        menu.Items.Add(new MenuEntry { Header = AppLangResources.Get(AppLangResourceKind.TunnelsEdit), Icon = ArcIcons.Pencil, Command = row.EditCommand });
        menu.Items.Add(new MenuEntry
        {
            Header = AppLangResources.Get(AppLangResourceKind.TunnelsDelete),
            Icon = ArcIcons.Trash2,
            Command = row.DeleteCommand,
            IsDestructive = true,
            HasSeparatorBefore = true,
        });
        DropdownMenu.SetMenu(button, menu);
    }
}
