using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Octopus.App.Common;
using Octopus.App.Localization;
using TKCode.Arc;

namespace Octopus.App.Sidebar;

public partial class ItemRowView : UserControl
{
    private ItemRowViewModel? _model;

    public ItemRowView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }
        _model = DataContext as ItemRowViewModel;
        if (_model is null)
        {
            return;
        }
        _model.PropertyChanged += OnModelChanged;
        var entries = new List<MenuFactory.Entry>
        {
            new(AppLangResources.Get(AppLangResourceKind.Rename), _model.BeginRenameCommand),
        };
        if (_model.CanRestart)
        {
            entries.Add(new(AppLangResources.Get(AppLangResourceKind.Restart), _model.RestartCommand));
        }
        entries.Add(new(AppLangResources.Get(AppLangResourceKind.MoveToRightGroup), _model.MoveToNewGroupCommand));
        entries.Add(new(AppLangResources.Get(AppLangResourceKind.CloseItem), _model.CloseCommand, SeparatorBefore: true));
        MenuFactory.Attach(RowButton, entries);
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ItemRowViewModel.IsRenaming) && _model is { IsRenaming: true })
        {
            UiContext.PostToUi(() => Editor.BeginEdit(), DispatcherPriority.Input);
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => _model?.BeginRenameCommand.Execute(null);

    private void OnTextCommitted(object? sender, InlineEditCommittedEventArgs e) => _model?.CommitRename(e.NewValue);

    private void OnEditCanceled(object? sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            _model.IsRenaming = false;
        }
    }
}
