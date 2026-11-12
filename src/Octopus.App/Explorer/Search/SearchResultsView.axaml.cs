using Avalonia.Controls;
using Avalonia.Input;

namespace Octopus.App.Explorer.Search;

/// <summary>The results of a name or content search. Double-click or Enter opens the file.</summary>
public partial class SearchResultsView : UserControl
{
    public SearchResultsView()
    {
        InitializeComponent();
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is FileSearchViewModel model && List.SelectedItem is SearchRowViewModel row) model.RequestOpen(row);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is FileSearchViewModel model && List.SelectedItem is SearchRowViewModel row)
        {
            model.RequestOpen(row);
            e.Handled = true;
        }
    }
}
