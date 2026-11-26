using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Octopus.Agents;
using Octopus.Agents.Accounts;
using Octopus.App.Activity;
using Octopus.App.Bots;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.Core.Agents;
using Octopus.Core.Bots;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Storage;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.Localization;

namespace Octopus.App.Sidebar;

/// <summary>One Bot's avatar shown inside the Bots entry frame on the rail (up to four; overflow is omitted).</summary>
public sealed record BotRailAvatarTile(
    string Id,
    string Name,
    string Initial,
    IBrush Background,
    Bitmap? Image,
    LucideIconKind? IconKind)
{
    public bool HasImage => Image is not null;
    public bool HasIcon => !HasImage && IconKind is not null;
    public bool ShowInitial => !HasImage && IconKind is null;
    public LucideIconKind EffectiveIconKind => IconKind ?? LucideIconKind.Bot;
}

/// <summary>The 54 px column of project squares and group folders, "+" and settings. Squares and folders can be dragged.</summary>
public sealed partial class ProjectRailViewModel : ObservableObject
{
    /// <summary>Maximum number of Bot avatars shown inside the Bots rail frame (2x2 grid); extras are omitted.</summary>
    public const int MaxBotRailAvatars = 4;
    private readonly IProjectService _projects;
    private readonly IItemOpener _opener;
    private readonly IDialogService _dialogs;
    private readonly IFileOpener _files;
    private readonly ISettingsNavigator _navigator;
    private readonly IProjectIconStore? _icons;
    private readonly IItemActivityService? _activity;
    private readonly IAgentCatalog? _catalog;
    private readonly IAgentScanner? _scanner;
    private readonly ISettingsStore? _settings;
    private readonly IAgentAccountService? _accounts;
    private readonly DeviceSwitcherViewModel? _devices;
    private readonly IBotService? _bots;
    /// <summary>The open group as the rail last showed it; null before the first build and when all are closed.</summary>
    private string? _shownExpanded;
    private bool _built;

    public ProjectRailViewModel(
        IProjectService projects,
        IItemOpener opener,
        IDialogService dialogs,
        IFileOpener files,
        ISettingsNavigator navigator,
        IProjectIconStore? icons = null,
        IItemActivityService? activity = null,
        IAgentCatalog? catalog = null,
        IAgentScanner? scanner = null,
        ISettingsStore? settings = null,
        IAgentAccountService? accounts = null,
        DeviceSwitcherViewModel? devices = null,
        IBotService? bots = null)
    {
        _icons = icons;
        _activity = activity;
        _catalog = catalog;
        _scanner = scanner;
        _settings = settings;
        _accounts = accounts;
        _devices = devices;
        _bots = bots;
        _projects = projects;
        _opener = opener;
        _dialogs = dialogs;
        _files = files;
        _navigator = navigator;
        _projects.Changed += OnProjectsChanged;
        _accounts?.Changed += (_, _) => UiContext.Run(Rebuild);
        _settings?.Changed += (_, _) => UiContext.Run(Rebuild);
        _scanner?.Changed += (_, _) => UiContext.Run(Rebuild);
        _bots?.Changed += (_, e) => UiContext.Run(() => RefreshBotState(e.Kind));
        ArcLanguage.Changed += (_, _) => UiContext.Run(Rebuild);
        // The rail lives as long as the shell, like the activity service: no unsubscribe.
        _activity?.Changed += (_, itemId) =>
        {
            if (_projects.FindProjectOf(itemId) is { } project)
            {
                ShowWork(project);
            }
            if (_bots is not null)
            {
                RefreshBotBadges();
            }
        };
        Rebuild();
        RefreshBotState();
    }

    /// <summary>Device switcher bound to the top circular badge on the rail (Option B).</summary>
    public DeviceSwitcherViewModel? Devices => _devices;

    public bool HasDeviceSwitcher => _devices is not null;

    public bool HasBots => _bots is not null;

    /// <summary>Up to <see cref="MaxBotRailAvatars"/> Bot avatars shown in the rail's Bots frame; overflow is omitted.</summary>
    public ObservableCollection<BotRailAvatarTile> BotAvatars { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyBotDashedFrame), nameof(ShowBotIdleFrame), nameof(BotChipOpacity))]
    public partial bool IsBotMode { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyBot), nameof(ShowEmptyBotFrame), nameof(ShowEmptyBotDashedFrame), nameof(ShowBotIdleFrame), nameof(ShowSingleBotAvatar), nameof(ShowBotAvatarGrid), nameof(BotChipOpacity))]
    public partial int VisibleBotAvatarCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BotChipOpacity))]
    public partial BotRailAvatarTile? SingleBotAvatar { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBotAttention), nameof(BotAttentionBadgeText), nameof(ShowBotWorking))]
    public partial int BotAttentionCount { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBotWorking))]
    public partial bool IsAnyBotWorking { get; private set; }

    public bool HasAnyBot => VisibleBotAvatarCount > 0;
    public bool ShowEmptyBotFrame => VisibleBotAvatarCount == 0;
    public bool ShowEmptyBotDashedFrame => !IsBotMode && ShowEmptyBotFrame;
    public bool ShowBotIdleFrame => !IsBotMode && HasAnyBot;
    public bool ShowSingleBotAvatar => VisibleBotAvatarCount == 1;
    public bool ShowBotAvatarGrid => VisibleBotAvatarCount > 1;
    public double BotChipOpacity => ShowEmptyBotFrame || IsBotMode || (ShowSingleBotAvatar && SingleBotAvatar?.HasImage == true) ? 1 : 0.72;

    public bool HasBotAttention => BotAttentionCount > 0;
    public string BotAttentionBadgeText => BotAttentionCount > 99 ? "99+" : BotAttentionCount.ToString();
    public bool ShowBotWorking => !HasBotAttention && IsAnyBotWorking;

    [RelayCommand]
    private void EnterBotMode() => _bots?.SetBotMode(true);

    private void RefreshBotState(BotChangeKind? kind = null)
    {
        if (_bots is null) return;
        IsBotMode = _bots.IsBotMode;
        if (kind is null or BotChangeKind.Bots)
        {
            RefreshBotAvatars();
        }
        RefreshBotBadges();
        UpdateCurrentRings();
    }

    private void RefreshBotAvatars()
    {
        if (_bots is null) return;
        BotAvatars.Clear();
        foreach (var bot in _bots.Bots.Take(MaxBotRailAvatars))
        {
            BotAvatars.Add(CreateBotAvatarTile(bot));
        }
        SingleBotAvatar = BotAvatars.Count == 1 ? BotAvatars[0] : null;
        VisibleBotAvatarCount = BotAvatars.Count;
    }

    private BotRailAvatarTile CreateBotAvatarTile(BotDefinition bot)
    {
        var image = _icons?.Load(bot.Image);
        IBrush background = image is not null
            ? Brushes.Transparent
            : (Color.TryParse(bot.Color, out var color) ? new SolidColorBrush(color) : Brushes.Gray);
        LucideIconKind? iconKind = !string.IsNullOrWhiteSpace(bot.Icon)
            && Enum.TryParse<LucideIconKind>(bot.Icon.Trim(), ignoreCase: true, out var parsed)
                ? parsed
                : null;
        return new BotRailAvatarTile(bot.Id, bot.Name, InitialOfBot(bot.Name), background, image, iconKind);
    }

    private static string InitialOfBot(string name) =>
        string.IsNullOrWhiteSpace(name) ? "B" : char.ConvertFromUtf32(char.ConvertToUtf32(name.Trim(), 0)).ToUpperInvariant();

    private void RefreshBotBadges()
    {
        if (_bots is null) return;
        BotAttentionCount = _bots.TotalNeedsAttentionCount();
        IsAnyBotWorking = _bots.HasWorkingBots();
    }

    private void UpdateCurrentRings()
    {
        var current = IsBotMode ? null : _projects.Current?.Id;
        foreach (var chip in Chips)
        {
            chip.IsCurrent = chip.Id == current;
        }
        foreach (var group in Entries.OfType<ProjectGroupViewModel>())
        {
            group.RefreshCurrent();
        }
    }

    /// <summary>Every project's square on the currently selected device, in or out of a group.</summary>
    public ObservableCollection<ProjectChipViewModel> Chips { get; } = [];

    /// <summary>What the rail shows, top to bottom: the squares of projects in no group, then the groups.</summary>
    public ObservableCollection<object> Entries { get; } = [];

    /// <summary>The colors a group can take, as the group menu offers them (name, value).</summary>
    public static IReadOnlyList<(AppLangResourceKind Name, string Value)> GroupColors { get; } =
    [
        (AppLangResourceKind.ColorIndigo, ProjectService.Palette[0]),
        (AppLangResourceKind.ColorSky, ProjectService.Palette[1]),
        (AppLangResourceKind.ColorEmerald, ProjectService.Palette[2]),
        (AppLangResourceKind.ColorAmber, ProjectService.Palette[3]),
        (AppLangResourceKind.ColorRed, ProjectService.Palette[4]),
        (AppLangResourceKind.ColorPink, ProjectService.Palette[5]),
        (AppLangResourceKind.ColorViolet, ProjectService.Palette[6]),
        (AppLangResourceKind.ColorTeal, ProjectService.Palette[7]),
    ];

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (e.Kind is ProjectChangeKind.Projects or ProjectChangeKind.Groups or ProjectChangeKind.ProjectAccounts or ProjectChangeKind.Device)
        {
            Rebuild();
        }
        else if (e.Kind == ProjectChangeKind.CurrentProject)
        {
            // Another project was chosen: only the rings move. The squares and folders stay as they are.
            UpdateCurrentRings();
        }
        else if (e.Kind == ProjectChangeKind.GroupExpanded)
        {
            // In place: the folder views stay and play the change, and the folder that was clicked keeps the focus.
            foreach (var group in Entries.OfType<ProjectGroupViewModel>())
            {
                group.IsExpanded = group.Id == _projects.ExpandedGroupId;
            }
            _shownExpanded = _projects.ExpandedGroupId;
        }
        else if (e.Kind == ProjectChangeKind.Items && e.Project is { } project)
        {
            // A working agent's tab was closed (or one was added).
            ShowWork(project);
        }
    }

    /// <summary>True while any terminal or agent of the project is working on a turn.</summary>
    private bool IsWorking(Project project) =>
        _activity is not null && project.Items.Any(i => _activity.Of(i.Id) == ItemActivity.Working);

    /// <summary>True while any terminal or agent of the project waits for the user.</summary>
    private bool NeedsInput(Project project) =>
        _activity is not null && project.Items.Any(i => _activity.Of(i.Id) == ItemActivity.Waiting);

    /// <summary>True while an agent or terminal of the project has a finished turn that was not looked at.</summary>
    private bool IsDone(Project project) =>
        _activity is not null && project.Items.Any(i => _activity.Of(i.Id) == ItemActivity.Done);

    /// <summary>Updates the project's loading ring and waiting dot in place, and the ones of the folder it is in.</summary>
    private void ShowWork(Project project)
    {
        if (Chips.FirstOrDefault(c => c.Id == project.Id) is not { } chip)
        {
            return;
        }
        chip.IsWorking = IsWorking(project);
        chip.NeedsInput = NeedsInput(project);
        chip.IsDone = IsDone(project);
        foreach (var group in Entries.OfType<ProjectGroupViewModel>())
        {
            group.IsWorking = group.Chips.Any(c => c.IsWorking);
            group.NeedsInput = group.Chips.Any(c => c.NeedsInput);
            group.IsDone = group.Chips.Any(c => c.IsDone);
        }
    }

    private void Rebuild()
    {
        var current = IsBotMode ? null : _projects.Current?.Id;
        var activeDeviceKey = _projects.ActiveDeviceKey;
        var choices = _projects.Groups.Select(g => new GroupChoice(g.Id, g.Name)).ToList();
        var members = _projects.Groups.ToDictionary(g => g.Id, _ => new List<ProjectChipViewModel>());
        Chips.Clear();
        Entries.Clear();
        foreach (var project in _projects.Projects)
        {
            if (!DeviceKeys.Matches(project, activeDeviceKey))
            {
                continue;
            }
            var chip = new ProjectChipViewModel(
                project, project.Id == current, Select, Rename, Reveal, RemoveAsync, ChangeIconAsync, _icons?.Load(project.Image),
                choices, MoveToGroup, NewGroupAsync, BuildAgentAccountMenus(project), SetProjectAgentAccount)
            {
                IsWorking = IsWorking(project),
                NeedsInput = NeedsInput(project),
                IsDone = IsDone(project),
            };
            Chips.Add(chip);
            if (project.GroupId is { } groupId && members.TryGetValue(groupId, out var list))
            {
                list.Add(chip);
            }
            else
            {
                Entries.Add(chip);
            }
        }
        var expanded = _projects.ExpandedGroupId;
        foreach (var group in _projects.Groups)
        {
            if (members[group.Id].Count == 0 && _projects.Projects.Any(p => p.GroupId == group.Id))
            {
                continue;
            }
            var isExpanded = group.Id == expanded;
            Entries.Add(new ProjectGroupViewModel(
                group, isExpanded, members[group.Id], ToggleGroup, RenameGroupAsync, RemoveGroup, SetGroupColor, SetGroupMergeWorkspaces)
            {
                // A group that opened or closed with this change (a new group opens as it is made) plays it.
                AnimateOnAttach = _built && isExpanded != (group.Id == _shownExpanded),
                IsWorking = members[group.Id].Any(c => c.IsWorking),
                NeedsInput = members[group.Id].Any(c => c.NeedsInput),
                IsDone = members[group.Id].Any(c => c.IsDone),
            });
        }
        _shownExpanded = expanded;
        _built = true;
    }

    private void Select(ProjectChipViewModel chip)
    {
        _bots?.SetBotMode(false);
        _projects.SetCurrent(chip.Id);
    }

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
            var image = _projects.Projects.FirstOrDefault(p => p.Id == chip.Id)?.Image;
            _projects.RemoveProject(chip.Id);
            _icons?.Delete(image);
        }
    }

    private async Task ChangeIconAsync(ProjectChipViewModel chip)
    {
        if (_projects.Projects.FirstOrDefault(p => p.Id == chip.Id) is not { } project)
        {
            return;
        }
        var choice = await _dialogs.AskProjectAppearanceAsync(project);
        if (choice is null)
        {
            return;
        }
        var previous = project.Image;
        _projects.SetProjectAppearance(project.Id, choice.Color, choice.Icon, choice.Image);
        if (previous is not null && previous != choice.Image)
        {
            _icons?.Delete(previous);
        }
    }

    // ---- groups ----

    private void MoveToGroup(ProjectChipViewModel chip, string? groupId) => _projects.MoveProjectToGroup(chip.Id, groupId);

    private void SetProjectAgentAccount(ProjectChipViewModel chip, ProjectAgentAccountSelection selection) =>
        _projects.SetProjectAgentAccount(chip.Id, selection.AgentId, selection.AccountId);

    private IReadOnlyList<ProjectAgentAccountMenu> BuildAgentAccountMenus(Project project)
    {
        if (!ProjectLocation.IsOnThisComputer(project)
            || _accounts is null
            || _catalog is null
            || _scanner is null
            || _settings is null)
        {
            return [];
        }

        var menus = new List<ProjectAgentAccountMenu>();
        var agentSettings = _settings.Current.Agents;
        foreach (var installation in _scanner.Current.Installations)
        {
            var definition = _catalog.Find(installation.AgentId);
            if (definition?.Accounts is null || !agentSettings.ProfileOf(definition.Id).Enabled)
            {
                continue;
            }
            var accounts = _accounts.GetAccounts(definition);
            if (accounts.Count < 2)
            {
                continue;
            }

            var profile = agentSettings.ProfileOf(definition.Id);
            var globalPreferredId = _accounts.ResolvePreferredAccountId(definition, profile, project: null);
            var globalPreferred = accounts.FirstOrDefault(a => a.Id == globalPreferredId) ?? accounts[0];
            var globalLabel = DisplayAccountLabel(globalPreferred);

            project.AgentAccounts.TryGetValue(definition.Id, out var projectOverrideId);
            if (!string.IsNullOrWhiteSpace(projectOverrideId) && accounts.All(a => a.Id != projectOverrideId))
            {
                projectOverrideId = null;
            }

            var options = new List<ProjectAgentAccountOption>
            {
                new(null, AppLangResources.Format(AppLangResourceKind.ProjectAccountFollowDefault, globalLabel), string.IsNullOrEmpty(projectOverrideId)),
            };
            foreach (var account in accounts)
            {
                options.Add(new ProjectAgentAccountOption(account.Id, DisplayAccountLabel(account), account.Id == projectOverrideId));
            }
            menus.Add(new ProjectAgentAccountMenu(definition.Id, definition.Name, options));
        }
        return menus;
    }

    private static string DisplayAccountLabel(AgentAccountRecord account) =>
        account.IsDefaultOrigin && account.Label == "Default"
            ? AppLangResources.Get(AppLangResourceKind.AccountDefaultName)
            : account.Label;

    private Task NewGroupAsync(ProjectChipViewModel chip) => NewGroupWithAsync([chip.Id]);

    /// <summary>Asks for a name, then makes a group of the projects (it opens); nothing happens when the dialog is dismissed.</summary>
    private async Task NewGroupWithAsync(IReadOnlyList<string> projectIds)
    {
        var name = await _dialogs.AskNameAsync(
            AppLangResources.Get(AppLangResourceKind.NewGroupTitle),
            AppLangResources.Get(AppLangResourceKind.GroupNameLabel),
            AppLangResources.Get(AppLangResourceKind.NewGroupDefaultName),
            AppLangResources.Get(AppLangResourceKind.CreateGroup));
        if (name is null)
        {
            return;
        }
        _projects.AddGroup(name.Length > 0 ? name : AppLangResources.Get(AppLangResourceKind.NewGroupDefaultName), projectIds);
    }

    /// <summary>Opens the group, closing the open one; an open group closes.</summary>
    private void ToggleGroup(ProjectGroupViewModel group) => _projects.SetExpandedGroup(group.IsExpanded ? null : group.Id);

    private async Task RenameGroupAsync(ProjectGroupViewModel group)
    {
        var name = await _dialogs.AskNameAsync(
            AppLangResources.Get(AppLangResourceKind.RenameGroupTitle),
            AppLangResources.Get(AppLangResourceKind.GroupNameLabel),
            group.Name,
            AppLangResources.Get(AppLangResourceKind.Save));
        if (!string.IsNullOrWhiteSpace(name))
        {
            _projects.RenameGroup(group.Id, name);
        }
    }

    private void RemoveGroup(ProjectGroupViewModel group) => _projects.RemoveGroup(group.Id);

    private void SetGroupColor(ProjectGroupViewModel group, string color) => _projects.SetGroupColor(group.Id, color);

    private void SetGroupMergeWorkspaces(ProjectGroupViewModel group, bool merge) => _projects.SetGroupMergeWorkspaces(group.Id, merge);

    // ---- dragging a square ----

    /// <summary>A square dropped on a group's folder or capsule: the project goes into that group.</summary>
    public void DropOnGroup(string projectId, string groupId) => _projects.MoveProjectToGroup(projectId, groupId);

    /// <summary>A square dropped on the rail outside any group: the project leaves its group.</summary>
    public void DropOnRail(string projectId) => _projects.MoveProjectToGroup(projectId, null);

    /// <summary>A square dropped on the upper or lower edge of another: it goes right before or after it, in its group.</summary>
    public void DropProjectNextTo(string projectId, string anchorProjectId, bool after) =>
        _projects.MoveProject(projectId, anchorProjectId, after);

    /// <summary>A folder dropped on the upper or lower half of another: it goes right before or after it.</summary>
    public void DropGroupNextTo(string groupId, string anchorGroupId, bool after) => _projects.MoveGroup(groupId, anchorGroupId, after);

    /// <summary>A folder dropped on the rail below the groups: it goes last.</summary>
    public void DropGroupLast(string groupId)
    {
        if (_projects.Groups.LastOrDefault() is { } last)
        {
            _projects.MoveGroup(groupId, last.Id, after: true);
        }
    }

    /// <summary>
    /// A square dropped on another square: into the other's group when it has one; otherwise the two make a new group
    /// (after asking for its name).
    /// </summary>
    public Task DropOnProjectAsync(string projectId, string targetId)
    {
        var target = _projects.Projects.FirstOrDefault(p => p.Id == targetId);
        if (target is null || projectId == targetId || _projects.Projects.All(p => p.Id != projectId))
        {
            return Task.CompletedTask;
        }
        if (target.GroupId is { } groupId)
        {
            _projects.MoveProjectToGroup(projectId, groupId);
            return Task.CompletedTask;
        }
        return NewGroupWithAsync([targetId, projectId]);
    }

    [RelayCommand]
    private Task NewProjectAsync() => _opener.PickAndAddProjectAsync();

    [RelayCommand]
    private void OpenSettings() => _navigator.OpenSettings();
}
