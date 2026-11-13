using TKCode.Arc;
using Avalonia.Controls;
using Octopus.App.Common;
using Octopus.App.Localization;

namespace Octopus.App.Sidebar;

public partial class ProjectChipView : UserControl
{
    public ProjectChipView() => InitializeComponent();

    /// <summary>Shows where a dragged square would go: into a group with this one, or before or after it.</summary>
    public void ShowDropMark(DropMark mark)
    {
        IntoMark.IsVisible = mark == DropMark.Into;
        BeforeLine.IsVisible = mark == DropMark.Before;
        AfterLine.IsVisible = mark == DropMark.After;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not ProjectChipViewModel chip)
        {
            return;
        }
        if (chip.IconKind is { } kind)
        {
            ChipIcon.Kind = kind;
        }
        MenuFactory.Attach(ChipButton,
        [
            new(AppLangResources.Get(AppLangResourceKind.RenameProject), chip.RenameCommand, Glyph: ArcIcons.Pencil),
            new(AppLangResources.Get(AppLangResourceKind.ChangeProjectIcon), chip.ChangeIconCommand, Glyph: ArcIcons.Image),
            new(AppLangResources.Get(AppLangResourceKind.MoveToGroup), null, Glyph: ArcIcons.Folder) { Children = GroupMenuOf(chip) },
            new(AppLangResources.Get(AppLangResourceKind.RevealInFileManager), chip.RevealCommand, Glyph: ArcIcons.FolderOpen),
            new(AppLangResources.Get(AppLangResourceKind.RemoveProject), chip.RemoveCommand, IsDestructive: true, SeparatorBefore: true, Glyph: ArcIcons.Trash2),
        ]);
    }

    /// <summary>"No group", every group (the project's own one checked), then "New group…".</summary>
    public static List<MenuFactory.Entry> GroupMenuOf(ProjectChipViewModel chip)
    {
        var entries = new List<MenuFactory.Entry>
        {
            new(AppLangResources.Get(AppLangResourceKind.NoGroup), chip.MoveToGroupCommand) { IsChecked = chip.GroupId is null },
        };
        entries.AddRange(chip.Groups.Select(g =>
            new MenuFactory.Entry(g.Name, chip.MoveToGroupCommand) { CommandParameter = g.Id, IsChecked = g.Id == chip.GroupId }));
        entries.Add(new(AppLangResources.Get(AppLangResourceKind.NewGroupMenu), chip.NewGroupCommand, SeparatorBefore: true, Glyph: ArcIcons.Plus));
        return entries;
    }
}
