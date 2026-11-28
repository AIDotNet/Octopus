using System.Collections.Concurrent;
using Octopus.Agents;
using Octopus.Agents.Signals;
using Octopus.App.Activity;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Core.Terminals;
using Octopus.Storage;
using Octopus.Terminal.Avalonia;

namespace Octopus.App.Collab.Sessions;

/// <summary>
/// Native terminals on this computer (CLI agents and shells), reached through their PTY. A CLI agent's turn ends when
/// its hook reports Done (<see cref="ISignalHub"/>); the screen text since the prompt is then its answer.
/// </summary>
internal sealed class TerminalSessionProvider : ICollabSessionProvider
{
    private const int MaxAnswerChars = 3500;

    private readonly CollabSessions _sessions;
    private readonly TerminalSessionRegistry? _terminals;
    private readonly Func<ITabContentRegistry?>? _tabs;
    private readonly ISettingsStore _settings;
    private readonly IAgentCatalog _catalog;
    private readonly CollabProjectIndex _index;
    private readonly IItemActivityService? _activity;
    private readonly Action<string, string, string?, string?> _turnEnded;
    private readonly Action<string, string, bool, IReadOnlySet<string>?, IReadOnlySet<string>?>? _turnAborted;
    private readonly Action<string, TerminalSession?>? _sessionClosed;
    private readonly Action<string, IReadOnlySet<string>?, IReadOnlySet<string>?>? _sessionRetired;
    private readonly Action<string, string>? _linkStopped;
    private readonly Action<string, string, string>? _deliveryAccepted;
    private readonly IDisposable? _signalSub;
    private readonly object _trackGate = new();
    private readonly ConcurrentDictionary<string, byte> _consumedInitialLaunches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (TerminalSession Session, Action<long, string, TerminalExit> ExitHandler, Action<long, string> StopHandler)> _exitSubs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (TerminalSession? Session, bool Replaced)> _observedSessions = new(StringComparer.Ordinal);

    /// <param name="turnEnded">Called with (item id, answer, thread id, task id) when a terminal agent with an outstanding prompt finishes its turn.</param>
    /// <param name="turnAborted">Called with (item id, reason, failed, optional delivery id scope, optional owner link id scope) when a registry terminal session exits without a tab view owning retry logic.</param>
    /// <param name="sessionClosed">Called with (item id, closed session) when a tracked terminal session is removed from the registry.</param>
    /// <param name="sessionRetired">Called with (item id, retired delivery ids, retired link ids) when a tracked terminal session is replaced before removal is observed.</param>
    /// <param name="linkStopped">Called with (item id, stopped link id) when a live link is explicitly stopped (for example by restart).</param>
    /// <param name="deliveryAccepted">Called with (item id, delivery id, owning link id) once a PTY write is acknowledged.</param>
    public TerminalSessionProvider(
        CollabSessions sessions,
        CollabProjectIndex index,
        TerminalSessionRegistry? terminals,
        Func<ITabContentRegistry?>? tabs,
        ISettingsStore settings,
        IAgentCatalog catalog,
        ISignalHub? signals,
        IItemActivityService? activity,
        Action<string, string, string?, string?> turnEnded,
        Action<string, string, bool, IReadOnlySet<string>?, IReadOnlySet<string>?>? turnAborted = null,
        Action<string, TerminalSession?>? sessionClosed = null,
        Action<string, IReadOnlySet<string>?, IReadOnlySet<string>?>? sessionRetired = null,
        Action<string, string>? linkStopped = null,
        Action<string, string, string>? deliveryAccepted = null)
    {
        _sessions = sessions;
        _index = index;
        _terminals = terminals;
        _tabs = tabs;
        _settings = settings;
        _catalog = catalog;
        _activity = activity;
        _turnEnded = turnEnded;
        _turnAborted = turnAborted;
        _sessionClosed = sessionClosed;
        _sessionRetired = sessionRetired;
        _linkStopped = linkStopped;
        _deliveryAccepted = deliveryAccepted;
        _signalSub = signals?.Subscribe(null, null, OnSignal);
        if (_terminals is not null)
        {
            _terminals.Changed += OnTerminalChanged;
            foreach (var key in _terminals.Keys)
            {
                TrackSession(key);
            }
        }
    }

    public bool Handles(Project project, ProjectItem item) =>
        !ProjectLocation.IsOnRemoteHost(project) && item.Surface != AgentSurface.Gui;

    public ICollabSessionBridge? TryAttach(Project project, ProjectItem item)
    {
        if (_terminals?.Find(item.Id) is null) return null;
        TrackSession(item.Id);
        return Bridge(project, item);
    }

    public async Task<ICollabSessionBridge?> EnsureAsync(Project project, ProjectItem item, CancellationToken ct)
    {
        if (!_settings.Current.Agents.Collab.EnableTerminalBridge || _terminals is null) return null;
        if (_terminals.Find(item.Id) is null)
        {
            await CollabUi.InvokeAsync(() =>
            {
                if (_tabs?.Invoke()?.GetOrCreate(project, item) is Terminals.TerminalTabView termView)
                {
                    termView.EnsureStarted(startWhenHidden: true);
                }
            }).ConfigureAwait(false);
            // Give the new tab a moment to attach and spawn its PTY.
            for (var i = 0; i < 15 && _terminals.Find(item.Id) is null; i++)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        if (_terminals.Find(item.Id) is { IsRunning: true })
        {
            TrackSession(item.Id);
            return Bridge(project, item);
        }
        return null;
    }

    private TerminalCollabSession Bridge(Project project, ProjectItem item)
    {
        var hasInitial = TryGetInitialLaunch(item, out var initialKey, out var initialThreadId, out var initialTaskId, out var initialDeliveryId);
        var bridge = new TerminalCollabSession(
            item.Id,
            item.AgentId ?? "terminal",
            item.DisplayTitle,
            string.IsNullOrWhiteSpace(item.WorkingDirectory) ? project.Path : item.WorkingDirectory!,
            _terminals!,
            _activity,
            hasInitial ? () => _consumedInitialLaunches.TryAdd(initialKey, 0) : null,
            _sessionRetired is { } retiredCallback
                ? deliveryId => retiredCallback(item.Id, new HashSet<string>(StringComparer.Ordinal) { deliveryId }, null)
                : null,
            _deliveryAccepted is { } acceptedCallback
                ? (deliveryId, linkId) => acceptedCallback(item.Id, deliveryId, linkId)
                : null);
        if (hasInitial && _terminals!.Find(item.Id) is { } term)
        {
            bridge.ReserveInitialTurn(term, initialThreadId, initialTaskId, expectReply: true, initialDeliveryId);
        }
        return bridge;
    }

    private bool TryGetInitialLaunch(ProjectItem item, out string key, out string? threadId, out string? taskId, out string? deliveryId)
    {
        key = "";
        threadId = null;
        taskId = null;
        deliveryId = null;
        var launch = item.Launch;
        if (launch is null || string.IsNullOrWhiteSpace(launch.Prompt)) return false;
        if (!string.IsNullOrWhiteSpace(launch.SessionId) || launch.Resume) return false;
        if (string.IsNullOrWhiteSpace(launch.CollabThreadId) && string.IsNullOrWhiteSpace(launch.CollabTaskId)) return false;
        if (item.AgentId is not { } agentId || _catalog.Find(agentId) is not { } definition) return false;
        if (definition.Capabilities.Prompt is null) return false;

        threadId = launch.CollabThreadId;
        taskId = launch.CollabTaskId;
        deliveryId = launch.CollabDeliveryId;
        key = $"{item.Id}\0{taskId ?? ""}\0{threadId ?? ""}";
        return !_consumedInitialLaunches.ContainsKey(key);
    }

    private void OnTerminalChanged(string itemId) => TrackSession(itemId);

    private void TrackSession(string itemId)
    {
        if (_terminals is null) return;
        TerminalSession? removedSession = null;
        TerminalSession? retiredSession = null;
        var observedNull = false;
        lock (_trackGate)
        {
            var current = _terminals.Find(itemId);
            ObserveSession(itemId, current);
            if (_turnAborted is null && _sessionClosed is null && _sessionRetired is null && _linkStopped is null) return;
            if (current is null)
            {
                if (_exitSubs.TryRemove(itemId, out var removed))
                {
                    removed.Session.LinkExited -= removed.ExitHandler;
                    removed.Session.LinkStopped -= removed.StopHandler;
                    removedSession = removed.Session;
                    observedNull = true;
                }
            }
            else
            {
                if (_exitSubs.TryGetValue(itemId, out var existing))
                {
                    if (ReferenceEquals(existing.Session, current)) return;
                    existing.Session.LinkExited -= existing.ExitHandler;
                    existing.Session.LinkStopped -= existing.StopHandler;
                    retiredSession = existing.Session;
                }
                Action<long, string, TerminalExit> exitHandler = (generation, linkId, exit) => OnRegistrySessionExited(itemId, current, generation, linkId, exit);
                Action<long, string> stopHandler = (generation, linkId) => OnRegistryLinkStopped(itemId, current, generation, linkId);
                _exitSubs[itemId] = (current, exitHandler, stopHandler);
                current.LinkExited += exitHandler;
                current.LinkStopped += stopHandler;
            }
        }

        if (observedNull && removedSession is not null)
        {
            if (_terminals.Find(itemId) is null)
            {
                if (_sessions.Find(itemId) is TerminalCollabSession terminalBridge)
                {
                    _sessions.Unregister(terminalBridge);
                }
                _sessionClosed?.Invoke(itemId, removedSession);
            }
            else
            {
                SettleRetiredSession(itemId, removedSession);
            }
        }
        else if (retiredSession is not null)
        {
            SettleRetiredSession(itemId, retiredSession);
        }
    }

    internal void SettleRetiredSession(string itemId, TerminalSession retiredSession)
    {
        if (_sessionRetired is null) return;
        var retiredDeliveries = CollectRetiredDeliveries(itemId, retiredSession, generation: null);
        HashSet<string>? retiredLinks = null;
        var linkIds = retiredSession.SnapshotLinkIds();
        if (linkIds.Count > 0)
        {
            retiredLinks = new HashSet<string>(linkIds, StringComparer.Ordinal);
        }
        else if (!string.IsNullOrWhiteSpace(retiredSession.LastLinkId))
        {
            retiredLinks = new HashSet<string>(StringComparer.Ordinal) { retiredSession.LastLinkId };
        }
        if (retiredDeliveries is { Count: > 0 } || retiredLinks is { Count: > 0 })
        {
            _sessionRetired(itemId, retiredDeliveries, retiredLinks);
        }
    }

    private HashSet<string>? CollectRetiredDeliveries(string itemId, TerminalSession retiredSession, long? generation)
    {
        HashSet<string>? retiredDeliveries = null;
        var terminalBridge = _sessions.Find(itemId) as TerminalCollabSession;
        if (terminalBridge?.TakeAcceptedDeliveries(retiredSession, generation) is { Count: > 0 } tracked)
        {
            retiredDeliveries = new HashSet<string>(tracked, StringComparer.Ordinal);
        }
        if ((generation is null or 1)
            && _index.FindItem(itemId) is { } item
            && TryGetInitialLaunch(item, out var initialKey, out _, out _, out var initialDeliveryId)
            && !string.IsNullOrWhiteSpace(initialDeliveryId)
            && (terminalBridge is null || !terminalBridge.HasAcceptedDeliveryForOtherSession(retiredSession, initialDeliveryId))
            && _consumedInitialLaunches.TryAdd(initialKey, 0))
        {
            (retiredDeliveries ??= new HashSet<string>(StringComparer.Ordinal)).Add(initialDeliveryId);
        }
        return retiredDeliveries;
    }

    private bool ObserveSession(string itemId, TerminalSession? current)
    {
        var updated = _observedSessions.AddOrUpdate(
            itemId,
            _ => (current, false),
            (_, existing) =>
            {
                if (current is null)
                {
                    return (null, existing.Replaced || existing.Session is not null);
                }
                if (ReferenceEquals(existing.Session, current))
                {
                    return existing;
                }
                return (current, existing.Replaced || existing.Session is not null);
            });
        return updated.Replaced;
    }

    private void OnRegistryLinkStopped(string itemId, TerminalSession session, long stoppedGeneration, string stoppedLinkId)
    {
        if (_linkStopped is null || string.IsNullOrWhiteSpace(stoppedLinkId)) return;
        (_sessions.Find(itemId) as TerminalCollabSession)?.TakeAcceptedDeliveries(session, stoppedGeneration);
        _linkStopped(itemId, stoppedLinkId);
    }

    private void OnRegistrySessionExited(string itemId, TerminalSession session, long exitedGeneration, string exitedLinkId, TerminalExit exit)
    {
        if (_turnAborted is null) return;
        // When a TerminalTabView owns this item, TerminalTabView.OnLinkExited handles resume-retry and collab notification.
        if (_tabs?.Invoke()?.Find(itemId) is Terminals.TerminalTabView) return;
        var code = exit.ExitCode ?? (exit.Signal is { } sig ? 128 + sig : null);
        var reason = code is { } exitCode
            ? $"Terminal session exited unexpectedly (code {exitCode})."
            : "Terminal session exited unexpectedly.";
        var isCurrentLink = ReferenceEquals(_terminals?.Find(itemId), session)
            && session.Generation == exitedGeneration
            && string.Equals(session.CurrentLinkId, exitedLinkId, StringComparison.Ordinal);
        if (isCurrentLink)
        {
            (_sessions.Find(itemId) as TerminalCollabSession)?.TakeAcceptedDeliveries(session, exitedGeneration);
            _turnAborted(itemId, reason, true, null, null);
            return;
        }
        var exitedDeliveries = CollectRetiredDeliveries(itemId, session, exitedGeneration);
        var exitedLinks = !string.IsNullOrWhiteSpace(exitedLinkId)
            ? new HashSet<string>(StringComparer.Ordinal) { exitedLinkId }
            : null;
        if (exitedDeliveries is { Count: > 0 } || exitedLinks is { Count: > 0 })
        {
            _turnAborted(itemId, reason, true, exitedDeliveries, exitedLinks);
        }
    }

    private void OnSignal(AgentSignal signal)
    {
        if (string.IsNullOrWhiteSpace(signal.ItemId)) return;
        if (_index.FindItem(signal.ItemId) is not { } item || item.AgentId is not { } agentId || _catalog.Find(agentId) is not { } definition) return;
        if (SignalStatusMap.Resolve(definition, signal) != SignalStatus.Done) return;
        var currentTerm = _terminals?.Find(signal.ItemId);
        var linkReplaced = ObserveSession(signal.ItemId, currentTerm);
        var terminal = _sessions.Find(signal.ItemId) as TerminalCollabSession;
        if (terminal is null && TryGetInitialLaunch(item, out _, out _, out _, out _) && _index.FindProjectOf(signal.ItemId) is { } project)
        {
            terminal = _sessions.TryAttach(project, item) as TerminalCollabSession;
        }
        if (terminal is null)
        {
            if (currentTerm is not null && TerminalCollabSession.IsSignalValidForLink(currentTerm, signal, linkReplaced))
            {
                currentTerm.TryConsumeLeadingManualTurn();
            }
            return;
        }
        if (terminal.TakeAnswer(MaxAnswerChars, signal, linkReplaced) is { } answer) _turnEnded(signal.ItemId, answer.Text, answer.ThreadId, answer.TaskId);
    }

    public void Dispose()
    {
        _signalSub?.Dispose();
        _consumedInitialLaunches.Clear();
        _observedSessions.Clear();
        _terminals?.Changed -= OnTerminalChanged;
        lock (_trackGate)
        {
            foreach (var entry in _exitSubs.Values)
            {
                entry.Session.LinkExited -= entry.ExitHandler;
                entry.Session.LinkStopped -= entry.StopHandler;
            }
            _exitSubs.Clear();
        }
    }
}
