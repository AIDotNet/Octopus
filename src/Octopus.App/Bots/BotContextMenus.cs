using Avalonia.Controls;
using Octopus.App.Bots.Groups;
using Octopus.App.Common;
using Octopus.App.Localization;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Bots;

/// <summary>Builds Arc-styled right-click context menus for Bot rows, Bot chat rows, and Bot Group rows.</summary>
internal static class BotContextMenus
{
    public static void AttachGroupMenu(Control target, BotGroupTreeNodeViewModel node)
    {
        var entries = new List<MenuFactory.Entry>
        {
            new(AppLangResources.Get(AppLangResourceKind.BotGroupMenuNewThread), node.NewThreadCommand)
            {
                Icon = Icon(LucideIconKind.MessageSquarePlus),
            },
            new(AppLangResources.Get(AppLangResourceKind.BotGroupMenuManageMembers), node.ManageMembersCommand)
            {
                Icon = Icon(LucideIconKind.Users),
            },
            new(AppLangResources.Get(AppLangResourceKind.BotGroupMenuRename), node.RenameCommand, Glyph: ArcIcons.Pencil),
            new(
                AppLangResources.Get(AppLangResourceKind.BotGroupMenuDelete),
                node.DeleteCommand,
                IsDestructive: true,
                SeparatorBefore: true,
                Glyph: ArcIcons.Trash2),
        };
        MenuFactory.Attach(target, entries);
    }

    public static void AttachGroupRoomMenu(Control target, BotGroupRoomRowViewModel row)
    {
        var entries = new List<MenuFactory.Entry>
        {
            new(AppLangResources.Get(AppLangResourceKind.BotChatOpen), row.OpenCommand)
            {
                Icon = Icon(LucideIconKind.ArrowUpRight),
            },
        };
        if (row.CanDelete)
        {
            entries.Add(new(
                AppLangResources.Get(AppLangResourceKind.BotGroupMenuDeleteThread),
                row.DeleteCommand,
                IsDestructive: true,
                SeparatorBefore: true,
                Glyph: ArcIcons.Trash2));
        }
        MenuFactory.Attach(target, entries);
    }

    public static void AttachBotMenu(Control target, BotTreeNodeViewModel node)
    {
        var entries = new List<MenuFactory.Entry>
        {
            new(AppLangResources.Get(AppLangResourceKind.BotMenuRename), node.RenameCommand, Glyph: ArcIcons.Pencil),
            new(AppLangResources.Get(AppLangResourceKind.BotMenuSettings), node.OpenSettingsCommand)
            {
                Icon = Icon(LucideIconKind.Settings),
            },
            new(
                AppLangResources.Get(node.IsPaused ? AppLangResourceKind.BotMenuResumeBot : AppLangResourceKind.BotMenuPauseBot),
                node.TogglePauseCommand)
            {
                Icon = Icon(node.IsPaused ? LucideIconKind.Play : LucideIconKind.Pause),
            },
            new(AppLangResources.Get(AppLangResourceKind.ExplorerCopyPath), node.CopyPathCommand, SeparatorBefore: true, Glyph: ArcIcons.Copy),
            new(
                AppLangResources.Get(node.IsUnread ? AppLangResourceKind.BotMenuMarkRead : AppLangResourceKind.BotMenuMarkUnread),
                node.ToggleUnreadCommand)
            {
                Icon = Icon(node.IsUnread ? LucideIconKind.BellOff : LucideIconKind.Bell),
            },
            new(
                AppLangResources.Get(AppLangResourceKind.BotMenuDeleteBot),
                node.DeleteCommand,
                IsDestructive: true,
                SeparatorBefore: true,
                Glyph: ArcIcons.Trash2),
        };
        MenuFactory.Attach(target, entries);
    }

    public static void AttachChatMenu(Control target, BotChatRowViewModel row)
    {
        var entries = new List<MenuFactory.Entry>
        {
            new(AppLangResources.Get(AppLangResourceKind.BotChatOpen), row.OpenCommand)
            {
                Icon = Icon(LucideIconKind.ArrowUpRight),
            },
        };
        if (row.CanDelete)
        {
            entries.Add(new(
                AppLangResources.Get(AppLangResourceKind.BotChatDelete),
                row.DeleteChatCommand,
                IsDestructive: true,
                SeparatorBefore: true,
                Glyph: ArcIcons.Trash2));
        }
        MenuFactory.Attach(target, entries);
    }

    private static LucideIcon Icon(LucideIconKind kind) => new()
    {
        Kind = kind,
        Size = 14,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };
}
