using Avalonia.Controls;
using Avalonia.Input;

namespace Octopus.App.Explorer.Git;

/// <summary>The source control tab: branch, grouped changes and recent commits. Single click selects, double-click opens the file.</summary>
public partial class GitTabView : UserControl
{
    public GitTabView()
    {
        InitializeComponent();
    }

    private void OnChangePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is GitPanelViewModel model && (sender as Control)?.DataContext is GitChangeRowViewModel row)
        {
            model.SelectedChange = row;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) model.RequestDiff(row);
        }
    }

    private void OnChangeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is GitPanelViewModel model && (sender as Control)?.DataContext is GitChangeRowViewModel row)
        {
            model.RequestOpen(row);
        }
    }

    private void OnCommitPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is GitPanelViewModel model && (sender as Control)?.DataContext is GitCommitRowViewModel row)
        {
            model.SelectedCommit = row;
        }
    }
}
