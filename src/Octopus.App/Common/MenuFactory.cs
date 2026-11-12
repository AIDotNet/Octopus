using System.Windows.Input;
using Avalonia.Controls;
using TKCode.Arc;

namespace Octopus.App.Common;

/// <summary>Builds the right-click menus of rows, tabs and the file tree: a list of commands, the destructive ones last.</summary>
public static class MenuFactory
{
    /// <summary>
    /// One row. A row with <see cref="Children"/> opens them as a submenu (its command is not used); a row with
    /// <see cref="IsChecked"/> set is one choice of a set and shows a check when it is the chosen one.
    /// </summary>
    public sealed record Entry(string Header, ICommand? Command, bool IsDestructive = false, bool SeparatorBefore = false)
    {
        public object? CommandParameter { get; init; }
        public bool? IsChecked { get; init; }
        /// <summary>A small control in the icon slot (a color swatch).</summary>
        public Control? Icon { get; init; }
        public IReadOnlyList<Entry>? Children { get; init; }
    }

    public static void Attach(Control target, IEnumerable<Entry> entries)
    {
        var surface = new MenuSurface();
        foreach (var entry in entries)
        {
            surface.Items.Add(Build(entry));
        }
        TKCode.Arc.ContextMenu.SetMenu(target, surface);
    }

    private static MenuEntry Build(Entry entry)
    {
        var row = new MenuEntry
        {
            Header = entry.Header,
            IsDestructive = entry.IsDestructive,
            HasSeparatorBefore = entry.SeparatorBefore,
        };
        if (entry.Children is { Count: > 0 } children)
        {
            foreach (var child in children)
            {
                row.Items.Add(Build(child));
            }
        }
        else
        {
            row.Command = entry.Command;
            row.CommandParameter = entry.CommandParameter;
        }
        if (entry.IsChecked is { } isChecked)
        {
            row.ToggleType = MenuEntryToggleType.Radio;
            row.IsChecked = isChecked;
        }
        if (entry.Icon is { } icon)
        {
            row.Icon = icon;
        }
        return row;
    }
}
