using System.Windows.Input;
using Avalonia.Controls;
using TKCode.Arc;

namespace Octopus.App.Common;

/// <summary>Builds the right-click menus of rows, tabs and the file tree: a list of commands, the destructive ones last.</summary>
public static class MenuFactory
{
    public sealed record Entry(string Header, ICommand Command, bool IsDestructive = false, bool SeparatorBefore = false);

    public static void Attach(Control target, IEnumerable<Entry> entries)
    {
        var surface = new MenuSurface();
        foreach (var entry in entries)
        {
            surface.Items.Add(new MenuEntry
            {
                Header = entry.Header,
                Command = entry.Command,
                IsDestructive = entry.IsDestructive,
                HasSeparatorBefore = entry.SeparatorBefore,
            });
        }
        TKCode.Arc.ContextMenu.SetMenu(target, surface);
    }
}
