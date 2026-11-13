using Avalonia.Controls;
using Avalonia.Interactivity;
using Octopus.Updates;

namespace Octopus.App.Workspace;

/// <summary>A quiet link to the project's GitHub page, shown on the empty and welcome pages.</summary>
public partial class StarOnGitHubButton : UserControl
{
    public StarOnGitHubButton() => InitializeComponent();

    private async void OnClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            await top.Launcher.LaunchUriAsync(new Uri(UpdateEndpoints.RepositoryUrl));
        }
    }
}
