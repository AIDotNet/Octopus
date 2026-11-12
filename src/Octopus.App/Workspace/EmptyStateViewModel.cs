using CommunityToolkit.Mvvm.Input;
using Octopus.App.Services;

namespace Octopus.App.Workspace;

/// <summary>The entries of an empty project: new terminal, start an agent, open a file.</summary>
public sealed partial class EmptyStateViewModel(IItemOpener opener)
{
    [RelayCommand]
    private void NewTerminal() => opener.OpenTerminal();

    [RelayCommand]
    private Task OpenFileAsync() => opener.PickAndOpenFileAsync();

    [RelayCommand]
    private Task NewProjectAsync() => opener.PickAndAddProjectAsync();
}
