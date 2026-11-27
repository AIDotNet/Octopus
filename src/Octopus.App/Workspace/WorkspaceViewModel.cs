using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Bots;
using Octopus.App.Bots.Groups;
using Octopus.App.Common;
using Octopus.App.Files;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Core.Bots;
using Octopus.Core.Projects;
using TKCode.Arc;

namespace Octopus.App.Workspace;

/// <summary>
/// The workspace: one dock layout per project (and per Bot in Bot mode), created when first shown and kept alive after
/// that, so switching projects or Bots only changes which layout is on screen and no terminal or agent session is touched.
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject, IWorkspaceCommands
{
    private readonly IProjectService _projects;
    private readonly ITabContentRegistry _registry;
    private readonly IItemOpener _opener;
    private readonly CloseConfirmation _confirmation;
    private readonly Dictionary<string, ProjectDockController> _controllers = [];
    private readonly Dictionary<string, BotDockController> _botControllers = [];
    private readonly Activity.IItemActivityService? _activity;
    private readonly IBotService? _bots;
    private readonly IBotGroupService? _botGroups;

    public WorkspaceViewModel(
        IProjectService projects,
        ITabContentRegistry registry,
        IItemOpener opener,
        CloseConfirmation confirmation,
        EmptyStateViewModel emptyState,
        Activity.IItemActivityService? activity = null,
        IBotService? bots = null,
        IBotGroupService? botGroups = null,
        BotGroupRoomViewModel? groupRoom = null)
    {
        _activity = activity;
        _bots = bots;
        _botGroups = botGroups;
        GroupRoom = groupRoom;
        _projects = projects;
        _registry = registry;
        _opener = opener;
        _confirmation = confirmation;
        EmptyState = emptyState;
        _projects.Changed += OnProjectsChanged;
        _bots?.Changed += OnBotsChanged;
        _botGroups?.Changed += OnBotGroupsChanged;
        Refresh();
    }

    public EmptyStateViewModel EmptyState { get; }
    public BotGroupRoomViewModel? GroupRoom { get; }

    /// <summary>The dock layout of the current project or bot; null while there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDockLayout), nameof(ShowNoProjectEmpty), nameof(ShowNoBotEmpty))]
    public partial object? CurrentLayout { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDockLayout), nameof(ShowNoProjectEmpty), nameof(ShowNoBotEmpty))]
    public partial bool HasProject { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDockLayout), nameof(ShowNoProjectEmpty), nameof(ShowNoBotEmpty))]
    public partial bool IsBotMode { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDockLayout), nameof(ShowNoProjectEmpty), nameof(ShowNoBotEmpty))]
    public partial bool IsGroupRoomActive { get; private set; }

    public bool ShowDockLayout => HasProject && !IsGroupRoomActive;
    public bool ShowNoProjectEmpty => !HasProject && !IsBotMode;
    public bool ShowNoBotEmpty => !HasProject && IsBotMode;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task NewBotAsync()
    {
        if (_bots is null) return;
        var dialogs = Composition.AppHost.Get<IDialogService>();
        var request = await dialogs.AskNewBotAsync();
        if (request is not null) _bots.CreateBot(request);
    }

    private ProjectDockController? CurrentController =>
        !IsBotMode && _projects.Current is { } project && _controllers.TryGetValue(project.Id, out var controller) ? controller : null;

    private BotDockController? CurrentBotController =>
        IsBotMode && _bots?.CurrentBot is { } bot && _botControllers.TryGetValue(bot.Id, out var controller) ? controller : null;

    private void OnBotsChanged(object? sender, BotChangedEventArgs e)
    {
        if (e.Kind is BotChangeKind.Mode or BotChangeKind.Bots or BotChangeKind.CurrentBot)
        {
            UiContext.Run(Refresh);
        }
    }

    private void OnBotGroupsChanged(object? sender, BotGroupChangedEventArgs e)
    {
        if (e.Kind is BotGroupChangeKind.Groups or BotGroupChangeKind.Selection)
        {
            UiContext.Run(Refresh);
        }
    }

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (e.Kind == ProjectChangeKind.Items && e.Item is { } removed && _projects.FindItem(removed.Id) is null
            && (e.Project is null || !_controllers.ContainsKey(e.Project.Id)))
        {
            // A session started remotely in a project never shown here has no controller: release it directly.
            _ = _registry.ReleaseAsync(removed.Id);
            return;
        }
        if (e.Kind == ProjectChangeKind.Projects && e.Project is { } goneProject
            && _projects.Projects.All(p => p.Id != goneProject.Id) && !_controllers.ContainsKey(goneProject.Id))
        {
            foreach (var item in goneProject.Items) _ = _registry.ReleaseAsync(item.Id);
        }
        if (e.Kind is ProjectChangeKind.Projects or ProjectChangeKind.CurrentProject)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        foreach (var gone in _controllers.Keys.Where(id => _projects.Projects.All(p => p.Id != id)).ToList())
        {
            _controllers.Remove(gone, out var controller);
            controller!.Dispose();
        }
        if (_bots is not null)
        {
            foreach (var goneBot in _botControllers.Keys.Where(id => _bots.Bots.All(b => b.Id != id)).ToList())
            {
                _botControllers.Remove(goneBot, out var botController);
                botController!.Dispose();
            }
        }

        IsBotMode = _bots?.IsBotMode == true;

        if (IsBotMode && _bots is not null)
        {
            foreach (var controller in _controllers.Values)
            {
                controller.SetActive(false);
            }
            if (_botGroups?.CurrentGroup is not null && GroupRoom is not null)
            {
                foreach (var botController in _botControllers.Values) botController.SetActive(false);
                HasProject = true;
                IsGroupRoomActive = true;
                CurrentLayout = null;
                return;
            }
            IsGroupRoomActive = false;
            var currentBot = _bots.CurrentBot;
            HasProject = currentBot is not null;
            if (currentBot is null)
            {
                foreach (var botController in _botControllers.Values) botController.SetActive(false);
                CurrentLayout = null;
                return;
            }
            if (!_botControllers.TryGetValue(currentBot.Id, out var existingBot))
            {
                existingBot = CreateBot(currentBot);
                _botControllers[currentBot.Id] = existingBot;
            }
            foreach (var botController in _botControllers.Values) botController.SetActive(ReferenceEquals(botController, existingBot));
            var botChanged = !ReferenceEquals(CurrentLayout, existingBot.Layout);
            CurrentLayout = existingBot.Layout;
            if (botChanged)
            {
                existingBot.ActivateCurrent();
            }
            return;
        }

        foreach (var botController in _botControllers.Values) botController.SetActive(false);
        IsGroupRoomActive = false;

        var current = _projects.Current;
        HasProject = current is not null;
        if (current is null)
        {
            foreach (var controller in _controllers.Values) controller.SetActive(false);
            CurrentLayout = null;
            return;
        }
        if (!_controllers.TryGetValue(current.Id, out var existing))
        {
            existing = Create(current);
            _controllers[current.Id] = existing;
        }
        foreach (var controller in _controllers.Values) controller.SetActive(ReferenceEquals(controller, existing));
        var changed = !ReferenceEquals(CurrentLayout, existing.Layout);
        CurrentLayout = existing.Layout;
        if (changed)
        {
            existing.ActivateCurrent();
        }
    }

    private ProjectDockController Create(Project project) =>
        new(project, _projects, _registry, _confirmation, () => _opener.OpenTerminal(), new EmptyProjectView { DataContext = EmptyState }, _activity);

    private BotDockController CreateBot(BotDefinition bot) =>
        new(bot, _bots!, _registry, _confirmation, new EmptyState
        {
            Title = bot.Name,
            Description = AppLangResources.Get(AppLangResourceKind.BotWorkspaceEmptyHint),
            Icon = ArcIcons.Sparkles,
        });

    /// <summary>Asks once when programs still run in some tab; true when the app may quit.</summary>
    public Task<bool> ConfirmQuitAsync() => _confirmation.ConfirmQuitAsync();

    /// <summary>Ends every process and waits until they are gone (the window is closing).</summary>
    public async Task CloseAllAsync()
    {
        _projects.Changed -= OnProjectsChanged;
        _bots?.Changed -= OnBotsChanged;
        _botGroups?.Changed -= OnBotGroupsChanged;
        foreach (var controller in _controllers.Values)
        {
            controller.Dispose();
        }
        _controllers.Clear();
        foreach (var controller in _botControllers.Values)
        {
            controller.Dispose();
        }
        _botControllers.Clear();
        await _registry.ReleaseAllAsync();
    }

    // ---- IWorkspaceCommands ----

    /// <summary>The group the next new tab opens in (a "+" of a tab strip was clicked); null for the active group.</summary>
    public void SetNewTabGroup(DockGroup? group)
    {
        if (CurrentBotController is { } botController)
        {
            botController.NewTabGroup = group;
        }
        else if (CurrentController is { } controller)
        {
            controller.NewTabGroup = group;
        }
    }

    public void SplitRight()
    {
        if (CurrentBotController is { } botController) botController.SplitRight();
        else CurrentController?.SplitRight();
    }

    public void MoveToNewGroup(string itemId) => CurrentController?.MoveToNewGroup(itemId);

    public void CloseActiveTab()
    {
        if (CurrentBotController is { } botController) botController.CloseActive();
        else CurrentController?.CloseActive();
    }

    public void SelectTab(int number)
    {
        if (CurrentBotController is { } botController) botController.SelectTab(number);
        else CurrentController?.SelectTab(number);
    }

    public void CycleTab(int delta)
    {
        if (CurrentBotController is { } botController) botController.CycleTab(delta);
        else CurrentController?.CycleTab(delta);
    }

    public void OpenDiff(DiffTabRequest request) => CurrentController?.OpenDiff(request);

    public void FocusActive()
    {
        if (CurrentBotController is { } botController) botController.ActivateCurrent();
        else CurrentController?.ActivateCurrent();
    }

    public async Task CloseItemAsync(string itemId)
    {
        if (await _confirmation.ConfirmCloseTabAsync(itemId))
        {
            if (_bots?.CurrentBot is { } bot && _bots.FindItem(itemId) is not null)
            {
                _bots.CloseBackingItem(bot.Id, itemId);
            }
            else
            {
                _projects.RemoveItem(itemId);
            }
        }
    }

    public async Task RestartItemAsync(string itemId)
    {
        if (_registry.Find(itemId) is { } content)
        {
            await content.RestartAsync();
        }
    }
}
