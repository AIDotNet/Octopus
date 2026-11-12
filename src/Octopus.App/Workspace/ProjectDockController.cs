using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.Threading;
using Octopus.App.Files;
using Octopus.App.Services;
using Octopus.Core.Projects;
using TKCode.Arc;

namespace Octopus.App.Workspace;

/// <summary>
/// The dock layout of one project. Every item is a document pane whose content is the item's cached control; the project
/// keeps the layout as JSON. The controller keeps the project's items and the layout's panes in step in both directions.
/// </summary>
public sealed class ProjectDockController : IDisposable
{
    private readonly Project _project;
    private readonly IProjectService _projects;
    private readonly ITabContentRegistry _registry;
    private readonly CloseConfirmation _confirmation;
    private readonly Func<ProjectItem?> _newTerminal;
    private readonly Dictionary<string, DockPane> _panes = [];
    private readonly HashSet<string> _approvedClose = [];
    /// <summary>The panes of diff tabs: they have no project item, so syncing with the items must leave them alone.</summary>
    private readonly HashSet<string> _diffKeys = [];
    private bool _syncing;
    private readonly Activity.IItemActivityService? _activity;
    /// <summary>Turns the loading ring of the tabs whose agent is working; runs only while there is one.</summary>
    private readonly DispatcherTimer _spin = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private int _spinFrame;
    private bool _loading;

    public ProjectDockController(
        Project project,
        IProjectService projects,
        ITabContentRegistry registry,
        CloseConfirmation confirmation,
        Func<ProjectItem?> newTerminal,
        Control emptyContent,
        Activity.IItemActivityService? activity = null)
    {
        _activity = activity;
        if (_activity is not null)
        {
            _activity.Changed += OnActivityChanged;
        }
        _spin.Tick += (_, _) => Spin();
        _project = project;
        _projects = projects;
        _registry = registry;
        _confirmation = confirmation;
        _newTerminal = newTerminal;

        Layout = new DockLayout
        {
            // A terminal in a floating window has not been verified: panes stay docked.
            FloatingMode = DockFloatingMode.Disabled,
            EmptyDocumentContent = emptyContent,
            Workspace = new DockWorkspace { Root = new DockDocumentArea() },
            PaneResolver = id => _project.Items.FirstOrDefault(i => i.Id == id) is { } item ? PaneOf(item) : null,
        };

        _loading = true;
        foreach (var item in project.Items)
        {
            PaneOf(item);
        }
        if (project.DockLayout is { Length: > 0 } json)
        {
            Layout.LoadLayout(json);
        }
        foreach (var pane in _panes.Values.Where(p => p.Group is null && p.State == DockPaneState.Docked).ToList())
        {
            Layout.Open(pane);
        }
        if (project.ActiveItemId is { } active && _panes.TryGetValue(active, out var activePane))
        {
            Layout.ActivePane = activePane;
        }
        _loading = false;

        Layout.LayoutChanged += (_, _) =>
        {
            if (!_loading)
            {
                _projects.SetDockLayout(_project, Layout.SaveLayout());
            }
        };
        Layout.ActivePaneChanged += OnActivePaneChanged;
        Layout.PaneClosing += OnPaneClosing;
        Layout.PaneClosed += OnPaneClosed;
        _projects.Changed += OnProjectsChanged;
    }

    public DockLayout Layout { get; }

    public DockPane? ActivePane => Layout.ActivePane;

    /// <summary>
    /// The group a "+" in a tab strip was clicked in: the next tab opens there instead of in the active group. Set while
    /// the new-tab menu is open and cleared when it closes. The active pane is left alone: moving it would move the
    /// keyboard focus into that group's terminal and take it from the menu.
    /// </summary>
    public DockGroup? NewTabGroup { get; set; }

    /// <summary>Selects the project's active tab and puts the keyboard focus into it.</summary>
    public void ActivateCurrent()
    {
        if (_project.ActiveItemId is { } id && _panes.TryGetValue(id, out var pane))
        {
            Dispatcher.UIThread.Post(() => Focus(pane), DispatcherPriority.Background);
        }
    }

    public void SplitRight()
    {
        if (Layout.ActivePane is { } active)
        {
            SplitRight(active);
        }
    }

    public void MoveToNewGroup(string itemId)
    {
        if (_panes.TryGetValue(itemId, out var pane))
        {
            SplitRight(pane);
        }
    }

    /// <summary>
    /// Puts the tab in a new group on the right of its own. A tab that is alone in its group has nowhere to go: a new
    /// terminal is started in the new group instead, so the result is the same split (design 03 section 3.2).
    /// </summary>
    private void SplitRight(DockPane pane)
    {
        var group = pane.Group;
        if (group is null)
        {
            return;
        }
        if (group.Panes.Count > 1)
        {
            Layout.Move(pane, DockTarget.Split(group, DockPosition.Right));
            return;
        }
        var item = _newTerminal();
        if (item is not null && _panes.TryGetValue(item.Id, out var created) && created.Group is not null)
        {
            Layout.Move(created, DockTarget.Split(group, DockPosition.Right));
        }
    }

    /// <summary>
    /// A diff tab has a pane but no project item: it stays out of <c>Project.Items</c>, so it is never saved as an item
    /// and is not restored. The saved layout may still name its pane; the resolver knows no such item and drops it.
    /// </summary>
    public void OpenDiff(DiffTabRequest request)
    {
        var key = request.Key;
        if (_panes.TryGetValue(key, out var existing))
        {
            ((existing.Content as Control)?.DataContext as DiffTabViewModel)?.Refresh();
            Focus(existing);
            return;
        }
        var control = _registry.GetOrCreateDiff(request);
        var pane = new DockPane
        {
            Id = key,
            Title = (control.DataContext as DiffTabViewModel)?.Title ?? System.IO.Path.GetFileName(request.Path),
            Icon = ArcIcons.FileText,
            CanFloat = false,
            Content = control,
        };
        _panes[key] = pane;
        _diffKeys.Add(key);
        Layout.Open(pane);
        Focus(pane);
    }

    public void CloseActive()
    {
        if (Layout.ActivePane is { } pane)
        {
            Layout.Close(pane);
        }
    }

    public void SelectTab(int number)
    {
        var panes = Layout.ActivePane?.Group?.Panes;
        if (panes is not null && number >= 1 && number <= panes.Count)
        {
            Focus(panes[number - 1]);
        }
    }

    public void CycleTab(int delta)
    {
        var group = Layout.ActivePane?.Group;
        if (group is null || group.Panes.Count < 2)
        {
            return;
        }
        var index = group.Panes.IndexOf(Layout.ActivePane!);
        Focus(group.Panes[(index + delta + group.Panes.Count) % group.Panes.Count]);
    }

    public void Dispose()
    {
        _spin.Stop();
        if (_activity is not null)
        {
            _activity.Changed -= OnActivityChanged;
        }
        _projects.Changed -= OnProjectsChanged;
        foreach (var id in _panes.Keys.ToList())
        {
            _ = _registry.ReleaseAsync(id);
        }
        _panes.Clear();
    }

    private void Focus(DockPane pane)
    {
        _syncing = true;
        try { Layout.Activate(pane); }
        finally { _syncing = false; }
        _projects.ActivateItem(pane.Id);
        _registry.Find(pane.Id)?.FocusContent();
    }

    private DockPane PaneOf(ProjectItem item)
    {
        if (_panes.TryGetValue(item.Id, out var existing))
        {
            return existing;
        }
        var pane = new DockPane
        {
            Id = item.Id,
            Title = item.DisplayTitle,
            Icon = IconOf(item),
            CanFloat = false,
            Content = _registry.GetOrCreate(_project, item),
        };
        _panes[item.Id] = pane;
        ShowUnsavedMark(pane, item);
        return pane;
    }

    private static Geometry IconOf(ProjectItem item) => item.Kind switch
    {
        ProjectItemKind.Terminal => ArcIcons.Terminal,
        // DockPane.Icon is drawn by Arc as a 1.75-unit outline: a filled brand logo turns into a blob at 14 px, so
        // the tab keeps the generic mark (the sidebar row shows the real logo).
        ProjectItemKind.Agent => ArcIcons.Sparkles,
        ProjectItemKind.Plugin => ArcIcons.LayoutGrid,
        _ => ArcIcons.FileText,
    };

    /// <summary>
    /// The loading ring at eight angles. DockPane.Icon is a geometry, not a control, so the tab cannot host Arc's
    /// spinner: the ring is turned by swapping the icon. Arc scales an icon by its bounds, so every frame must have
    /// the same ones: the gap is 45° wide and starts on a multiple of 45°, which keeps the circle's four outermost
    /// points on the ring in every frame.
    /// </summary>
    private static readonly Geometry[] s_spinFrames = Enumerable.Range(0, 8).Select(SpinFrame).ToArray();

    private static Geometry SpinFrame(int index)
    {
        const double center = 12, radius = 9;
        static string Point(double degrees)
        {
            var radians = degrees * Math.PI / 180;
            return FormattableString.Invariant($"{center + radius * Math.Cos(radians):0.###} {center + radius * Math.Sin(radians):0.###}");
        }
        var gap = index * 45;
        return Geometry.Parse($"M{Point(gap + 45)} A{radius} {radius} 0 1 1 {Point(gap)}");
    }

    private bool IsWorking(string itemId) => _activity?.Of(itemId) == Activity.ItemActivity.Working;

    private void OnActivityChanged(object? sender, string itemId)
    {
        if (!_panes.TryGetValue(itemId, out var pane) || _project.Items.FirstOrDefault(i => i.Id == itemId) is not { } item)
        {
            return;
        }
        pane.Icon = IsWorking(itemId) ? s_spinFrames[_spinFrame] : IconOf(item);
        _spin.IsEnabled = _panes.Keys.Any(IsWorking);
    }

    private void Spin()
    {
        _spinFrame = (_spinFrame + 1) % s_spinFrames.Length;
        var any = false;
        foreach (var (id, pane) in _panes)
        {
            if (IsWorking(id))
            {
                pane.Icon = s_spinFrames[_spinFrame];
                any = true;
            }
        }
        _spin.IsEnabled = any;
    }

    /// <summary>A file tab with edits that are not saved shows a dot before its title.</summary>
    private static void ShowUnsavedMark(DockPane pane, ProjectItem item)
    {
        if (pane.Content is Plugins.PluginViewTab view)
        {
            view.ChromeChanged += (_, _) => pane.Title = view.IsDirty ? "● " + item.DisplayTitle : item.DisplayTitle;
            return;
        }
        if (pane.Content is not Control { DataContext: FileTabViewModel file }) return;
        file.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileTabViewModel.HasUnsavedChanges)) pane.Title = TitleOf(item, file);
        };
    }

    private static string TitleOf(ProjectItem item, FileTabViewModel? file) => file?.HasUnsavedChanges == true ? "● " + item.DisplayTitle : item.DisplayTitle;

    private void OnActivePaneChanged(object? sender, DockPaneEventArgs e)
    {
        if (_syncing || _loading || e.Pane is not { } pane)
        {
            return;
        }
        _syncing = true;
        try { _projects.ActivateItem(pane.Id); }
        finally { _syncing = false; }
        Dispatcher.UIThread.Post(() => _registry.Find(pane.Id)?.FocusContent(), DispatcherPriority.Input);
    }

    /// <summary>A tab with a running program asks first: the close is cancelled, confirmed, then done again.</summary>
    private void OnPaneClosing(object? sender, DockPaneClosingEventArgs e)
    {
        var pane = e.Pane!;
        if (_syncing || _approvedClose.Remove(pane.Id))
        {
            return;
        }
        e.Cancel = true;
        _ = ConfirmAndCloseAsync(pane);
    }

    private async Task ConfirmAndCloseAsync(DockPane pane)
    {
        if (!await _confirmation.ConfirmCloseTabAsync(pane.Id))
        {
            return;
        }
        _approvedClose.Add(pane.Id);
        if (!Layout.Close(pane))
        {
            _approvedClose.Remove(pane.Id);
        }
    }

    private void OnPaneClosed(object? sender, DockPaneEventArgs e)
    {
        if (e.Pane is not { } pane)
        {
            return;
        }
        _panes.Remove(pane.Id);
        _ = _registry.ReleaseAsync(pane.Id);
        if (_diffKeys.Remove(pane.Id))
        {
            return;
        }
        if (!_syncing)
        {
            _syncing = true;
            try { _projects.RemoveItem(pane.Id); }
            finally { _syncing = false; }
        }
    }

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Project, _project))
        {
            return;
        }
        switch (e.Kind)
        {
            case ProjectChangeKind.Items when !_syncing:
                SyncItems();
                break;
            case ProjectChangeKind.ItemTitle when e.Item is { } item && _panes.TryGetValue(item.Id, out var titled):
                titled.Title = TitleOf(item, (titled.Content as Control)?.DataContext as FileTabViewModel);
                break;
            case ProjectChangeKind.Active when !_syncing && e.Item is { } active
                                              && _panes.TryGetValue(active.Id, out var pane) && pane.Group is not null
                                              && !ReferenceEquals(Layout.ActivePane, pane):
                Focus(pane);
                break;
        }
    }

    private void SyncItems()
    {
        foreach (var gone in _panes.Keys.Where(id => !_diffKeys.Contains(id) && _project.Items.All(i => i.Id != id)).ToList())
        {
            var pane = _panes[gone];
            _syncing = true;
            try { Layout.Close(pane); }
            finally { _syncing = false; }
            if (_panes.Remove(gone))
            {
                _ = _registry.ReleaseAsync(gone);
            }
        }
        foreach (var item in _project.Items.Where(i => !_panes.ContainsKey(i.Id)).ToList())
        {
            Layout.Open(PaneOf(item), NewTabGroup is { Panes.Count: > 0 } target ? DockTarget.Into(target) : null);
        }
    }
}
