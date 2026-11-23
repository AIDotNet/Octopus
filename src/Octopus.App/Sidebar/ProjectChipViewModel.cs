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

/// <summary>One selectable subscription account for an agent in a project's right-click menu.</summary>
public sealed record ProjectAgentAccountOption(string? AccountId, string Label, bool IsChecked);

/// <summary>An agent and its available subscription accounts for a project's right-click menu.</summary>
public sealed record ProjectAgentAccountMenu(string AgentId, string AgentName, IReadOnlyList<ProjectAgentAccountOption> Options);

/// <summary>Command parameter for setting a project's agent account override.</summary>
public sealed record ProjectAgentAccountSelection(string AgentId, string? AccountId);

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
    Func<ProjectChipViewModel, Task>? newGroup = null,
    IReadOnlyList<ProjectAgentAccountMenu>? agentAccounts = null,
    Action<ProjectChipViewModel, ProjectAgentAccountSelection>? setAgentAccount = null) : ObservableObject
{
    public string Id { get; } = project.Id;
    public string Name { get; } = project.Name;
    public string Path { get; } = project.Path;
    public string Initial { get; } = InitialOf(project.Name);

    /// <summary>
    /// The square's fill: the project's color behind an initial or icon, or transparent when the project uses a custom
    /// picture so transparent pixels and rounded edges never show the random fallback color underneath.
    /// </summary>
    public IBrush Background { get; } = image is not null
        ? Brushes.Transparent
        : (Color.TryParse(project.Color, out var color) ? new SolidColorBrush(color) : Brushes.Gray);

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

    /// <summary>The square of the current project. Set in place by the rail when another project is chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipOpacity), nameof(ShowDimBackdrop))]
    public partial bool IsCurrent { get; set; } = isCurrent;

    /// <summary>
    /// Neutral opaque backing behind an inactive solid-color tile so dimming it inside a tinted group capsule never
    /// mixes the group's color into the project's own color.
    /// </summary>
    public bool ShowDimBackdrop => !HasImage && !IsCurrent;

    /// <summary>The folder is on an SSH host, not on this computer.</summary>
    public bool IsRemote { get; } = !ProjectLocation.IsOnThisComputer(project);

    /// <summary>The group the square sits in; null: none.</summary>
    public string? GroupId { get; } = project.GroupId;

    /// <summary>The groups of the rail, for the "Move to group" menu.</summary>
    public IReadOnlyList<GroupChoice> Groups { get; } = groups ?? [];

    /// <summary>Per-agent subscription account choices for local projects when an agent has multiple accounts.</summary>
    public IReadOnlyList<ProjectAgentAccountMenu> AgentAccounts { get; } = agentAccounts ?? [];

    public bool HasAgentAccounts => AgentAccounts.Count > 0;

    /// <summary>An agent (or terminal) of the project is working: the square carries a loading ring. Set in place by the rail.</summary>
    [ObservableProperty] public partial bool IsWorking { get; set; }

    /// <summary>An agent of the project stopped to ask the user: the square carries a yellow dot (it wins over the ring).</summary>
    [ObservableProperty] public partial bool NeedsInput { get; set; }

    /// <summary>A turn of the project finished while the user was elsewhere: a green dot, under the ring and the yellow dot, until it is viewed.</summary>
    [ObservableProperty] public partial bool IsDone { get; set; }

    public string Tip { get; } = project.Name + "\n" + ProjectLocation.Describe(project,
        project.RemoteHost is { } host ? Composition.AppHost.Get<Remote.RemoteClientRuntime>().HostName(host) : null);
    public double ChipOpacity => IsCurrent || HasImage ? 1 : 0.72;

    [RelayCommand] private void Select() => select(this);
    [RelayCommand] private void Rename() => rename(this);
    [RelayCommand(CanExecute = nameof(CanReveal))] private void Reveal() => reveal(this);
    [RelayCommand] private Task RemoveAsync() => remove(this);
    [RelayCommand] private Task ChangeIconAsync() => changeIcon?.Invoke(this) ?? Task.CompletedTask;
    /// <summary>Moves the project into the group with this id; null takes it out of its group.</summary>
    [RelayCommand] private void MoveToGroup(string? groupId) => moveToGroup?.Invoke(this, groupId);
    /// <summary>Asks for a name and puts the project into a new group of that name.</summary>
    [RelayCommand] private Task NewGroupAsync() => newGroup?.Invoke(this) ?? Task.CompletedTask;
    /// <summary>Sets or clears a project-level default subscription account for an agent.</summary>
    [RelayCommand] private void SetAgentAccount(ProjectAgentAccountSelection? selection)
    {
        if (selection is not null) setAgentAccount?.Invoke(this, selection);
    }

    /// <summary>A folder on an SSH host has nothing to show in this computer's file manager.</summary>
    private bool CanReveal => !IsRemote;

    private static string InitialOf(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : char.ConvertFromUtf32(char.ConvertToUtf32(name.Trim(), 0)).ToUpperInvariant();
}
