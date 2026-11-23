using Avalonia.Controls;
using Avalonia.Interactivity;
using Octopus.App.Common;
using Octopus.App.Launcher;
using Octopus.App.Localization;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Sidebar;

public partial class WorkspaceGroupView : UserControl
{
    private WorkspaceGroupViewModel? _model;

    public WorkspaceGroupView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _model = DataContext as WorkspaceGroupViewModel;
        if (_model is null) return;

        var entries = new List<MenuFactory.Entry>
        {
            new(AppLangResources.Get(AppLangResourceKind.WorkspaceRevealFolder), _model.RevealCommand, Glyph: ArcIcons.Folder),
        };
        if (_model.HasTaskUrl)
        {
            entries.Add(new(_model.OpenTaskLabel, _model.OpenTaskCommand, Glyph: ArcIcons.ExternalLink));
        }
        if (_model.CanPin)
        {
            entries.Add(new(_model.PinLabel, _model.TogglePinCommand, Glyph: Lucide.Geometry(_model.IsPinned ? LucideIconKind.PinOff : LucideIconKind.Pin)));
        }
        if (_model.CanRemove)
        {
            entries.Add(new(AppLangResources.Get(AppLangResourceKind.WorkspaceRemove), _model.RemoveCommand, SeparatorBefore: true, Glyph: ArcIcons.Trash2));
        }
        MenuFactory.Attach(HeaderButton, entries);
    }

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            LauncherRequest.Raise(AddButton, _model.WorkspaceId);
            e.Handled = true;
        }
    }
}
