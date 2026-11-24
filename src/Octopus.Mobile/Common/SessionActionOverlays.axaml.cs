using Avalonia.Controls;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Common;

/// <summary>The overlays of <see cref="SessionActionsViewModel"/>: put one in a page's Overlay with the page's actions as its DataContext.</summary>
public partial class SessionActionOverlays : UserControl
{
    private SessionActionsViewModel? _actions;

    public SessionActionOverlays()
    {
        InitializeComponent();
    }

    /// <summary>The rows of the menu as shown last, for tests.</summary>
    public IReadOnlyList<string> MenuLabels => [.. Menu.Actions.Select(action => action.Content?.ToString() ?? "")];

    public bool IsMenuOpen => Menu.IsOpen;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_actions is not null)
        {
            _actions.MenuRequested -= OnMenuRequested;
            _actions.Notice -= OnNotice;
        }
        _actions = DataContext as SessionActionsViewModel;
        if (_actions is not null)
        {
            _actions.MenuRequested += OnMenuRequested;
            _actions.Notice += OnNotice;
        }
    }

    // ActionSheets take their rows as controls, not from an ItemsSource.
    private void OnMenuRequested(string title, string? message, IReadOnlyList<SessionMenuEntry> entries)
    {
        Menu.Title = title;
        Menu.Message = message;
        Menu.Actions.Clear();
        foreach (var entry in entries)
        {
            Menu.Actions.Add(new ActionSheetItem { Content = entry.Label, Command = entry.Command, IsDestructive = entry.IsDestructive });
        }
        Menu.IsOpen = true;
    }

    private void OnNotice(string text) => Notices.Show(text);
}
