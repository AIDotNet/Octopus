using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.Platform;

namespace Octopus.App.Sidebar;

/// <summary>The 54 px column of project squares, "+" and settings.</summary>
public sealed partial class ProjectRailViewModel : ObservableObject
{
    private readonly IProjectService _projects;
    private readonly IItemOpener _opener;
    private readonly IDialogService _dialogs;
    private readonly IFileOpener _files;
    private readonly ISettingsNavigator _navigator;

    public ProjectRailViewModel(
        IProjectService projects,
        IItemOpener opener,
        IDialogService dialogs,
        IFileOpener files,
        ISettingsNavigator navigator)
    {
        _projects = projects;
        _opener = opener;
        _dialogs = dialogs;
        _files = files;
        _navigator = navigator;
        _projects.Changed += OnProjectsChanged;
        Rebuild();
    }

    public ObservableCollection<ProjectChipViewModel> Chips { get; } = [];

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (e.Kind is ProjectChangeKind.Projects or ProjectChangeKind.CurrentProject)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var current = _projects.Current?.Id;
        Chips.Clear();
        foreach (var project in _projects.Projects)
        {
            Chips.Add(new ProjectChipViewModel(project, project.Id == current, Select, Rename, Reveal, RemoveAsync));
        }
    }

    private void Select(ProjectChipViewModel chip) => _projects.SetCurrent(chip.Id);

    private void Rename(ProjectChipViewModel chip)
    {
        _projects.SetCurrent(chip.Id);
        WeakReferenceMessenger.Default.Send(new RenameProjectRequested(chip.Id));
    }

    private void Reveal(ProjectChipViewModel chip) => _files.Reveal(chip.Path);

    private async Task RemoveAsync(ProjectChipViewModel chip)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            AppLangResources.Format(AppLangResourceKind.RemoveProjectTitle, chip.Name),
            AppLangResources.Get(AppLangResourceKind.RemoveProjectBody),
            AppLangResources.Get(AppLangResourceKind.RemoveProject),
            isDanger: true);
        if (confirmed)
        {
            // Closing the items first ends their processes; the dock layout of the project goes with the project.
            _projects.RemoveProject(chip.Id);
        }
    }

    [RelayCommand]
    private Task NewProjectAsync() => _opener.PickAndAddProjectAsync();

    [RelayCommand]
    private void OpenSettings() => _navigator.OpenSettings();
}
