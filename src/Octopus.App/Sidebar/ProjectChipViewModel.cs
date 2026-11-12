using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.Core.Projects;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Sidebar;

/// <summary>A group a project can be moved into, as its menu lists it.</summary>
public sealed record GroupChoice(string Id, string Name);

/// <summary>One square of the project rail.</summary>
public sealed partial class ProjectChipViewModel(
    Project project,
    bool isCurrent,
    Action<ProjectChipViewModel> select,
    Action<ProjectChipViewModel> rename,
    Action<ProjectChipViewModel> reveal,
    Func<ProjectChipViewModel, Task> remove,
    Func<ProjectChipViewModel, Task>? changeIcon = null,
    Bitmap? image = null,
    IReadOnlyList<GroupChoice>? groups = null,
    Action<ProjectChipViewModel, string?>? moveToGroup = null,
    Func<ProjectChipViewModel, Task>? newGroup = null) : ObservableObject
{
    public string Id { get; } = project.Id;
    public string Name { get; } = project.Name;
    public string Path { get; } = project.Path;
    public string Initial { get; } = InitialOf(project.Name);

    /// <summary>The project's color: data chosen by the app, not a theme color.</summary>
    public IBrush Background { get; } = Color.TryParse(project.Color, out var color) ? new SolidColorBrush(color) : Brushes.Gray;

    /// <summary>The project's picture (a small thumbnail); shown instead of the icon, the initial and the color.</summary>
    public Bitmap? Image { get; } = image;

    public bool HasImage => Image is not null;

    /// <summary>The chosen Lucide icon; null for none (or a pixel icon).</summary>
    public LucideIconKind? IconKind { get; } = ProjectIcons.Parse(project.Icon);

    /// <summary>The chosen built-in pixel icon ("pixel:…"); null for none.</summary>
    public PixelIcon? Pixel { get; } = PixelIcons.Parse(project.Icon);

    public bool ShowIcon => !HasImage && IconKind is not null;

    public bool ShowPixel => !HasImage && Pixel is not null;

    public bool ShowInitial => !HasImage && IconKind is null && Pixel is null;

    public bool IsCurrent { get; } = isCurrent;

    /// <summary>The folder is on an SSH host, not on this computer.</summary>
    public bool IsRemote { get; } = project.SshHost is not null;

    /// <summary>The group the square sits in; null: none.</summary>
    public string? GroupId { get; } = project.GroupId;

    /// <summary>The groups of the rail, for the "Move to group" menu.</summary>
    public IReadOnlyList<GroupChoice> Groups { get; } = groups ?? [];

    /// <summary>An agent (or terminal) of the project is working: the square carries a loading ring. Set in place by the rail.</summary>
    [ObservableProperty] public partial bool IsWorking { get; set; }

    public string Tip { get; } = project.Name + "\n" + (project.SshHost is { } host ? host + ":" + project.Path : project.Path);
    public double ChipOpacity => IsCurrent ? 1 : 0.72;

    [RelayCommand] private void Select() => select(this);
    [RelayCommand] private void Rename() => rename(this);
    [RelayCommand(CanExecute = nameof(CanReveal))] private void Reveal() => reveal(this);
    [RelayCommand] private Task RemoveAsync() => remove(this);
    [RelayCommand] private Task ChangeIconAsync() => changeIcon?.Invoke(this) ?? Task.CompletedTask;
    /// <summary>Moves the project into the group with this id; null takes it out of its group.</summary>
    [RelayCommand] private void MoveToGroup(string? groupId) => moveToGroup?.Invoke(this, groupId);
    /// <summary>Asks for a name and puts the project into a new group of that name.</summary>
    [RelayCommand] private Task NewGroupAsync() => newGroup?.Invoke(this) ?? Task.CompletedTask;

    /// <summary>A folder on an SSH host has nothing to show in this computer's file manager.</summary>
    private bool CanReveal => !IsRemote;

    private static string InitialOf(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : char.ConvertFromUtf32(char.ConvertToUtf32(name.Trim(), 0)).ToUpperInvariant();
}
