using Avalonia.Controls;
using Avalonia.Threading;
using Octopus.App.Bots;
using Octopus.App.Common;
using Octopus.Core.Bots;
using Octopus.Core.Projects;
using TKCode.Arc;

namespace Octopus.App.Workspace;

/// <summary>
/// The center dock layout of one <see cref="BotDefinition"/>: hosts live agent chat tabs (ACP GUI or Terminal)
/// and editable home files (<c>SOUL.md</c>, <c>MEMORY.md</c>, etc.) inside the bot's home directory.
/// </summary>
public sealed class BotDockController : IDisposable
{
    private readonly string _botId;
    private readonly Project _backing;
    private readonly IBotService _bots;
    private readonly ITabContentRegistry _registry;
    private readonly CloseConfirmation _confirmation;
    private readonly Dictionary<string, DockPane> _panes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _approvedClose = new(StringComparer.Ordinal);
    private bool _syncing;
    private bool _loading;

    public BotDockController(
        BotDefinition bot,
        IBotService bots,
        ITabContentRegistry registry,
        CloseConfirmation confirmation,
        Control emptyContent)
    {
        _botId = bot.Id;
        _bots = bots;
        _registry = registry;
        _confirmation = confirmation;
        _backing = bots.GetBackingProject(bot);

        Layout = new DockLayout
        {
            FloatingMode = DockFloatingMode.Disabled,
            EmptyDocumentContent = emptyContent,
            Workspace = new DockWorkspace { Root = new DockDocumentArea() },
            PaneResolver = id => _backing.Items.FirstOrDefault(i => i.Id == id) is { } item ? PaneOf(item) : null,
        };

        _loading = true;
        foreach (var item in _backing.Items)
        {
            PaneOf(item);
        }
        if (_backing.DockLayout is { Length: > 0 } json)
        {
            Layout.LoadLayout(json);
        }
        foreach (var pane in _panes.Values.Where(p => p.Group is null && p.State == DockPaneState.Docked).ToList())
        {
            Layout.Open(pane);
        }
        if (_backing.ActiveItemId is { } active && _panes.TryGetValue(active, out var activePane))
        {
            Layout.ActivePane = activePane;
        }
        _loading = false;

        Layout.LayoutChanged += OnLayoutChanged;
        Layout.ActivePaneChanged += OnActivePaneChanged;
        Layout.PaneClosing += OnPaneClosing;
        Layout.PaneClosed += OnPaneClosed;
        _bots.Changed += OnBotsChanged;
    }

    public DockLayout Layout { get; }
    public DockGroup? NewTabGroup { get; set; }

    public void ActivateCurrent()
    {
        if (_backing.ActiveItemId is { } id && _panes.TryGetValue(id, out var pane))
        {
            Dispatcher.UIThread.Post(() => Focus(pane), DispatcherPriority.Background);
        }
    }

    public void SplitRight()
    {
        if (Layout.ActivePane is not { Group: { } group } active) return;
        if (group.Panes.Count > 1)
        {
            Layout.Move(active, DockTarget.Split(group, DockPosition.Right));
            return;
        }
        var chat = _bots.CreateChat(_botId);
        if (chat is not null && _panes.TryGetValue(chat.Id, out var created) && created.Group is not null)
        {
            Layout.Move(created, DockTarget.Split(group, DockPosition.Right));
        }
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
        if (group is null || group.Panes.Count < 2) return;
        var index = group.Panes.IndexOf(Layout.ActivePane!);
        Focus(group.Panes[(index + delta + group.Panes.Count) % group.Panes.Count]);
    }

    public void Dispose()
    {
        Layout.LayoutChanged -= OnLayoutChanged;
        Layout.ActivePaneChanged -= OnActivePaneChanged;
        Layout.PaneClosing -= OnPaneClosing;
        Layout.PaneClosed -= OnPaneClosed;
        _bots.Changed -= OnBotsChanged;
        foreach (var id in _panes.Keys.ToList())
        {
            _ = _registry.ReleaseAsync(id);
        }
        _panes.Clear();
    }

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        if (!_loading)
        {
            _bots.SetDockLayout(_botId, Layout.SaveLayout());
        }
    }

    private void OnActivePaneChanged(object? sender, EventArgs e)
    {
        if (_syncing || _loading || Layout.ActivePane is not { } pane) return;
        _bots.ActivateBackingItem(_botId, pane.Id);
        _registry.Find(pane.Id)?.FocusContent();
    }

    private void OnPaneClosing(object? sender, DockPaneClosingEventArgs e)
    {
        if (_syncing || e.Pane is null) return;
        if (_approvedClose.Remove(e.Pane.Id)) return;
        if (_registry.Find(e.Pane.Id) is not { } content) return;

        e.Cancel = true;
        _ = ConfirmCloseAsync(e.Pane);
    }

    private async Task ConfirmCloseAsync(DockPane pane)
    {
        if (!await _confirmation.ConfirmCloseTabAsync(pane.Id)) return;
        _approvedClose.Add(pane.Id);
        Layout.Close(pane);
    }

    private void OnPaneClosed(object? sender, DockPaneEventArgs e)
    {
        if (e.Pane is null) return;
        var id = e.Pane.Id;
        _panes.Remove(id);
        if (!_syncing)
        {
            _bots.CloseBackingItem(_botId, id);
        }
        _ = _registry.ReleaseAsync(id);
    }

    private void OnBotsChanged(object? sender, BotChangedEventArgs e)
    {
        if (e.Bot is not null && e.Bot.Id != _botId) return;
        UiContext.Run(SyncWithBacking);
    }

    private void SyncWithBacking()
    {
        // Sync titles and open new items
        foreach (var item in _backing.Items)
        {
            if (_panes.TryGetValue(item.Id, out var existing))
            {
                if (existing.Title != item.DisplayTitle) existing.Title = item.DisplayTitle;
                if (existing.Group is null) OpenPane(existing);
            }
            else
            {
                OpenPane(PaneOf(item));
            }
        }

        // Close panes whose items were removed
        var liveIds = _backing.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var staleId in _panes.Keys.Where(id => !liveIds.Contains(id)).ToList())
        {
            if (_panes.TryGetValue(staleId, out var stale))
            {
                _syncing = true;
                try { Layout.Close(stale); }
                finally { _syncing = false; }
            }
            _panes.Remove(staleId);
            _ = _registry.ReleaseAsync(staleId);
        }

        // Activate active item
        if (_backing.ActiveItemId is { } activeId && _panes.TryGetValue(activeId, out var activePane)
            && !ReferenceEquals(Layout.ActivePane, activePane))
        {
            Focus(activePane);
        }
    }

    private void OpenPane(DockPane pane)
    {
        _syncing = true;
        try
        {
            Layout.Open(pane, NewTabGroup is { Panes.Count: > 0 } group ? DockTarget.Into(group) : null);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Focus(DockPane pane)
    {
        _syncing = true;
        try { Layout.Activate(pane); }
        finally { _syncing = false; }
        _registry.Find(pane.Id)?.FocusContent();
    }

    private DockPane PaneOf(ProjectItem item)
    {
        if (_panes.TryGetValue(item.Id, out var existing)) return existing;
        var pane = new DockPane
        {
            Id = item.Id,
            Title = item.DisplayTitle,
            Icon = item.Kind == ProjectItemKind.File ? ArcIcons.FileText : ArcIcons.Sparkles,
            CanFloat = false,
            Content = _registry.GetOrCreate(_backing, item),
        };
        _panes[item.Id] = pane;
        return pane;
    }
}
