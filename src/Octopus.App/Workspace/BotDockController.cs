using Avalonia.Controls;
using Avalonia.Threading;
using Octopus.App.AgentGui;
using Octopus.App.Bots;
using Octopus.App.Common;
using Octopus.App.Files;
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
    private readonly Action<Action> _postBackground;
    private readonly Dictionary<string, DockPane> _panes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _approvedClose = new(StringComparer.Ordinal);
    private readonly HashSet<Func<Task>> _pendingReleases = [];
    private bool _syncing;
    private bool _resyncNeeded;
    private bool _active = true;
    private bool _disposed;
    private int _activateVersion;
    private readonly bool _loading;

    public BotDockController(
        BotDefinition bot,
        IBotService bots,
        ITabContentRegistry registry,
        CloseConfirmation confirmation,
        Control emptyContent,
        Action<Action>? postBackground = null)
    {
        _botId = bot.Id;
        _bots = bots;
        _registry = registry;
        _confirmation = confirmation;
        _postBackground = postBackground ?? (static action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background));
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

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (!active)
        {
            ++_activateVersion;
        }
    }

    public void ActivateCurrent()
    {
        var version = ++_activateVersion;
        if (!_active || _disposed || _backing.ActiveItemId is not { } id || !_panes.TryGetValue(id, out var pane))
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (!_active || _disposed || version != _activateVersion
                || !_bots.IsBotMode
                || _bots.CurrentBot?.Id != _botId
                || _backing.ActiveItemId != id
                || !_panes.TryGetValue(id, out var current)
                || !ReferenceEquals(current, pane))
            {
                return;
            }
            Focus(pane);
        }, DispatcherPriority.Background);
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
        _disposed = true;
        _active = false;
        ++_activateVersion;
        Layout.LayoutChanged -= OnLayoutChanged;
        Layout.ActivePaneChanged -= OnActivePaneChanged;
        Layout.PaneClosing -= OnPaneClosing;
        Layout.PaneClosed -= OnPaneClosed;
        _bots.Changed -= OnBotsChanged;
        foreach (var id in _panes.Keys.ToList())
        {
            if (_registry.CaptureForRelease(id) is { } release)
            {
                _ = release();
            }
        }
        foreach (var release in _pendingReleases.ToList())
        {
            _ = release();
        }
        _panes.Clear();
        _pendingReleases.Clear();
    }

    private void ScheduleRelease(Func<Task>? release)
    {
        if (release is null || !_pendingReleases.Add(release))
        {
            return;
        }
        _postBackground(() =>
        {
            if (_pendingReleases.Remove(release))
            {
                _ = release();
            }
        });
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
        if (_syncing || _loading || _disposed || !_active || Layout.ActivePane is not { } pane) return;
        _syncing = true;
        try { _bots.ActivateBackingItem(_botId, pane.Id); }
        finally { _syncing = false; }
        Dispatcher.UIThread.Post(() =>
        {
            if (_active && !_disposed && ReferenceEquals(Layout.ActivePane, pane))
            {
                _registry.Find(pane.Id)?.FocusContent();
            }
        }, DispatcherPriority.Input);
    }

    private bool NeedsCloseConfirmation(ITabContent content)
    {
        if (content is FileTabView fileView)
        {
            return fileView.Model?.HasUnsavedChanges == true;
        }
        if (content is AgentGuiTabView)
        {
            var running = content.HasRunningChildAsync();
            return !running.IsCompletedSuccessfully || running.Result;
        }
        return true;
    }

    private void OnPaneClosing(object? sender, DockPaneClosingEventArgs e)
    {
        if (_syncing || e.Pane is null) return;
        if (_approvedClose.Remove(e.Pane.Id)) return;
        if (_registry.Find(e.Pane.Id) is not { } content) return;
        if (!NeedsCloseConfirmation(content)) return;

        e.Cancel = true;
        _ = ConfirmCloseAsync(e.Pane);
    }

    private async Task ConfirmCloseAsync(DockPane pane)
    {
        var originalContent = pane.Content;
        var concreteRegistry = _registry as TabContentRegistry;
        using var confirmation = concreteRegistry?.BeginConfirmation(pane.Id);
        if (!await _confirmation.ConfirmCloseTabAsync(pane.Id)) return;
        if (_disposed
            || !_panes.TryGetValue(pane.Id, out var currentPane)
            || !ReferenceEquals(currentPane, pane)
            || !ReferenceEquals(pane.Content, originalContent)
            || (confirmation is not null && !confirmation.IsStillCurrent()))
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
        if (e.Pane is null || _syncing) return;
        var id = e.Pane.Id;
        _panes.Remove(id);
        e.Pane.Content = null;
        e.Pane.Icon = null;
        _syncing = true;
        _resyncNeeded = false;
        try
        {
            BotOwnerRetirement.RetireAndMutate(
                _registry,
                id,
                () => _bots.CloseBackingItem(_botId, id),
                ScheduleRelease);
        }
        finally
        {
            _syncing = false;
        }
        if (_resyncNeeded && !_disposed)
        {
            SyncWithBacking();
        }
    }

    private void OnBotsChanged(object? sender, BotChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.Bot is not null && e.Bot.Id != _botId) return;
        if (_syncing)
        {
            _resyncNeeded = true;
            return;
        }
        UiContext.Run(() =>
        {
            if (_disposed) return;
            if (e.Kind == BotChangeKind.Chats
                && e.IsRemoved
                && e.Chat is { } removedChat
                && !BotOwnerRetirement.IsRetiring(removedChat.Id)
                && _backing.Items.Any(i => i.Id == removedChat.Id)
                && e.SupersededOwner is { } superseded
                && _registry is TabContentRegistry concrete)
            {
                // An uncaptured close (e.g. from an optional-registry sidebar) was reopened by an earlier observer
                // before this dock observed the close. Retire only the exact pre-close owner/generation captured
                // before the event fired, never a restarted or newly attached successor.
                ScheduleRelease(concrete.CaptureForReleaseIfMatches(removedChat.Id, superseded));
            }
            SyncWithBacking();
        });
    }

    private void SyncWithBacking()
    {
        _resyncNeeded = false;

        // Sync titles, reconcile replaced controls, and open new items
        foreach (var item in _backing.Items)
        {
            var currentContent = _registry.GetOrCreate(_backing, item);
            if (_panes.TryGetValue(item.Id, out var existing))
            {
                if (existing.Title != item.DisplayTitle) existing.Title = item.DisplayTitle;
                if (!ReferenceEquals(existing.Content, currentContent)) existing.Content = currentContent;
                if (existing.Group is null) OpenPane(existing);
            }
            else
            {
                OpenPane(PaneOf(item, currentContent));
            }
        }

        // Close panes whose items were removed
        var liveIds = _backing.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var staleId in _panes.Keys.Where(id => !liveIds.Contains(id)).ToList())
        {
            void CloseStalePane()
            {
                if (_panes.TryGetValue(staleId, out var stale))
                {
                    _syncing = true;
                    try { Layout.Close(stale); }
                    finally { _syncing = false; }
                    stale.Content = null;
                    stale.Icon = null;
                }
                _panes.Remove(staleId);
            }

            if (BotOwnerRetirement.IsRetiring(staleId))
            {
                // The initiating close/delete route already captured the original owner and owns its retirement;
                // detach only the stale pane so a replacement attached during removal is never recaptured.
                CloseStalePane();
            }
            else
            {
                BotOwnerRetirement.RetireAndMutate(_registry, staleId, CloseStalePane, ScheduleRelease);
            }
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
        if (_active && !_disposed)
        {
            _registry.Find(pane.Id)?.FocusContent();
        }
    }

    private DockPane PaneOf(ProjectItem item, Control? content = null)
    {
        if (_panes.TryGetValue(item.Id, out var existing)) return existing;
        var pane = new DockPane
        {
            Id = item.Id,
            Title = item.DisplayTitle,
            Icon = item.Kind == ProjectItemKind.File ? ArcIcons.FileText : ArcIcons.Sparkles,
            CanFloat = false,
            Content = content ?? _registry.GetOrCreate(_backing, item),
        };
        _panes[item.Id] = pane;
        return pane;
    }
}
