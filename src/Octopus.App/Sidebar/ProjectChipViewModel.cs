using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Core.Projects;

namespace Octopus.App.Sidebar;

/// <summary>One square of the project rail.</summary>
public sealed partial class ProjectChipViewModel(
    Project project,
    bool isCurrent,
    Action<ProjectChipViewModel> select,
    Action<ProjectChipViewModel> rename,
    Action<ProjectChipViewModel> reveal,
    Func<ProjectChipViewModel, Task> remove) : ObservableObject
{
    public string Id { get; } = project.Id;
    public string Name { get; } = project.Name;
    public string Path { get; } = project.Path;
    public string Initial { get; } = InitialOf(project.Name);

    /// <summary>The project's color: data chosen by the app, not a theme color.</summary>
    public IBrush Background { get; } = Color.TryParse(project.Color, out var color) ? new SolidColorBrush(color) : Brushes.Gray;

    public bool IsCurrent { get; } = isCurrent;
    public string Tip { get; } = project.Name + "\n" + project.Path;
    public double ChipOpacity => IsCurrent ? 1 : 0.72;

    [RelayCommand] private void Select() => select(this);
    [RelayCommand] private void Rename() => rename(this);
    [RelayCommand] private void Reveal() => reveal(this);
    [RelayCommand] private Task RemoveAsync() => remove(this);

    private static string InitialOf(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : char.ConvertFromUtf32(char.ConvertToUtf32(name.Trim(), 0)).ToUpperInvariant();
}
