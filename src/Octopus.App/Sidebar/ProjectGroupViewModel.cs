using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Localization;
using Octopus.Core.Projects;

namespace Octopus.App.Sidebar;

/// <summary>One of the four small squares inside a closed folder: a project's color or picture, or an empty place.</summary>
public sealed record GroupPreviewTile(IBrush Background, Bitmap? Image)
{
    public bool HasImage => Image is not null;
}

/// <summary>
/// A group on the project rail. Closed, it is a folder square showing its first four projects; open, its projects stand
/// in a tinted capsule under a folder icon that closes it. Only one group is open at a time (the project service keeps it).
/// </summary>
public sealed partial class ProjectGroupViewModel : ObservableObject
{
    /// <summary>The place of a missing project in the folder: a faint square that reads on light and dark.</summary>
    private static readonly IBrush s_emptyTile = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x2E, 0x80, 0x80, 0x88));

    private readonly Action<ProjectGroupViewModel> _toggle;
    private readonly Func<ProjectGroupViewModel, Task> _rename;
    private readonly Action<ProjectGroupViewModel> _remove;
    private readonly Action<ProjectGroupViewModel, string> _setColor;

    public ProjectGroupViewModel(
        ProjectGroup group,
        bool isExpanded,
        IReadOnlyList<ProjectChipViewModel> chips,
        Action<ProjectGroupViewModel> toggle,
        Func<ProjectGroupViewModel, Task> rename,
        Action<ProjectGroupViewModel> remove,
        Action<ProjectGroupViewModel, string> setColor)
    {
        _toggle = toggle;
        _rename = rename;
        _remove = remove;
        _setColor = setColor;
        Id = group.Id;
        Name = group.Name;
        Color = group.Color;
        IsExpanded = isExpanded;
        Chips = chips;
        var tint = Avalonia.Media.Color.TryParse(group.Color, out var color) ? color : Colors.Gray;
        Tint = new SolidColorBrush(tint);
        FolderBackground = new SolidColorBrush(tint, 0.26);
        CapsuleBackground = new SolidColorBrush(tint, 0.14);
        Previews = Enumerable.Range(0, 4)
            .Select(i => i < chips.Count ? new GroupPreviewTile(chips[i].Background, chips[i].Image) : new GroupPreviewTile(s_emptyTile, null))
            .ToList();
        var count = chips.Count == 1
            ? AppLangResources.Get(AppLangResourceKind.GroupProjectCountOne)
            : AppLangResources.Format(AppLangResourceKind.GroupProjectCount, chips.Count);
        _summary = Name + " · " + count;
    }

    private readonly string _summary;

    public string Id { get; }
    public string Name { get; }
    /// <summary>The group's color as stored; null: none chosen.</summary>
    public string? Color { get; }
    /// <summary>Open on the rail. Changed in place when another group opens, so the view can animate the change.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCollapsed), nameof(HasCurrent), nameof(FolderOpacity), nameof(Tip))]
    public partial bool IsExpanded { get; set; }

    public bool IsCollapsed => !IsExpanded;

    /// <summary>
    /// The rail was rebuilt while this group opened or closed (a new group opens as it is made): the new view plays the
    /// change instead of showing the end state at once. The view clears it once played.
    /// </summary>
    public bool AnimateOnAttach { get; set; }

    /// <summary>The squares of the group's projects, in order; shown while the group is open.</summary>
    public IReadOnlyList<ProjectChipViewModel> Chips { get; }

    /// <summary>Always four: the first four projects, then empty places.</summary>
    public IReadOnlyList<GroupPreviewTile> Previews { get; }

    /// <summary>The current project is hidden in this closed group: the folder takes its accent ring.</summary>
    public bool HasCurrent => !IsExpanded && Chips.Any(c => c.IsCurrent);

    /// <summary>A project of the group is working: the closed folder carries the loading ring for it.</summary>
    [ObservableProperty] public partial bool IsWorking { get; set; }

    /// <summary>Dimmed like a project square that is not current.</summary>
    public double FolderOpacity => HasCurrent ? 1 : 0.72;

    /// <summary>The current project changed (the squares were updated in place): the folder's ring follows.</summary>
    public void RefreshCurrent()
    {
        OnPropertyChanged(nameof(HasCurrent));
        OnPropertyChanged(nameof(FolderOpacity));
    }

    /// <summary>The group's color (data, like a project's color): the open folder icon.</summary>
    public IBrush Tint { get; }
    public IBrush FolderBackground { get; }
    public IBrush CapsuleBackground { get; }

    public string Tip => _summary + "\n" + AppLangResources.Get(IsExpanded ? AppLangResourceKind.CloseGroup : AppLangResourceKind.OpenGroup);

    [RelayCommand] private void Toggle() => _toggle(this);
    [RelayCommand] private Task RenameAsync() => _rename(this);
    [RelayCommand] private void Remove() => _remove(this);
    [RelayCommand] private void SetColor(string color) => _setColor(this, color);
}
