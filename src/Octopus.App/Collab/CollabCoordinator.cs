using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Octopus.Agents;
using Octopus.Agents.Signals;
using Octopus.App.Activity;
using Octopus.App.Collab.Ledger;
using Octopus.App.Collab.Sessions;
using Octopus.App.Collab.Tools;
using Octopus.App.Services;
using Octopus.App.Services.Workspaces;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Remote.Protocol;
using Octopus.Storage;
using Octopus.Terminal.Avalonia;

namespace Octopus.App.Collab;

/// <summary>Kind of targeted collaboration change delivered to item observers.</summary>
public enum CollabChangeKind
{
    Topology,
    Activity,
    Title,
}

/// <summary>
/// Central runtime orchestrating cross-agent task delegation, @-mention routing, cross-session negotiation threads,
/// ACP GUI + native Terminal PTY bridging, git worktree isolation, and child permission bubbling.
/// </summary>
public interface ICollabCoordinator
{
    event Action<string?>? Changed;

    CollabMcpServer Server { get; }
    bool IsEnabled { get; }

    /// <summary>Builds the built-in <c>octopus-collab</c> MCP server config for an ACP GUI session, or null when disabled/stopped.</summary>
    AcpMcpServerConfig? BuildAcpMcpServer(string itemId);

    void RegisterGuiSession(ICollabSessionBridge bridge);
    void UnregisterGuiSession(string itemId);
    void UnregisterGuiSession(ICollabSessionBridge bridge) => UnregisterGuiSession(bridge.ItemId);

    /// <summary>Called when an ACP GUI session finishes a turn; triggers implicit reply fallback and caller wakeup if applicable.</summary>
    void NotifyTurnCompleted(string itemId, string? finalAssistantText);

    /// <summary>
    /// Called when a session finishes a turn with known collaboration correlation. Settles only an unsettled
    /// expectation matching <paramref name="threadId"/> or <paramref name="taskId"/>; if neither is provided (an
    /// ordinary user turn), queued collaboration expectations are left untouched.
    /// </summary>
    void NotifyTurnCompleted(string itemId, string? finalAssistantText, string? threadId, string? taskId)
        => NotifyTurnCompleted(itemId, finalAssistantText);

    /// <summary>
    /// Called when a session finishes a turn with known collaboration correlation and optional per-delivery identity.
    /// </summary>
    void NotifyTurnCompleted(string itemId, string? finalAssistantText, string? threadId, string? taskId, string? deliveryId)
        => NotifyTurnCompleted(itemId, finalAssistantText, threadId, taskId);

    /// <summary>
    /// Called when a remote session's turn ended normally while unfinished host-side child delegations remain active,
    /// preserving responsibility on the origin task instead of reporting premature completion.
    /// </summary>
    void NotifyTurnDeferred(string itemId, string? threadId, string? taskId, string? deliveryId = null) { }

    /// <summary>
    /// Called when a session's turn ends without an answer: stopped by the user (<paramref name="failed"/> false) or broken
    /// by an error (true). Settles any task waiting on that session and tells its delegator. With
    /// <paramref name="cancelDelegationsSince"/>, also cancels the tasks this session delegated since that moment.
    /// </summary>
    void NotifyTurnAborted(string itemId, string reason, bool failed, DateTimeOffset? cancelDelegationsSince = null);

    /// <summary>
    /// Called when a session's turn ends without an answer and its collaboration correlation is known. Aborts only the
    /// expectation matching <paramref name="threadId"/> or <paramref name="taskId"/> (leaving queued jobs untouched for
    /// an ordinary user turn), while <paramref name="cancelDelegationsSince"/> still cancels delegations started during that turn.
    /// </summary>
    void NotifyTurnAborted(string itemId, string reason, bool failed, string? threadId, string? taskId, DateTimeOffset? cancelDelegationsSince = null)
        => NotifyTurnAborted(itemId, reason, failed, cancelDelegationsSince);

    /// <summary>
    /// Called when a session's turn ends without an answer with known collaboration correlation and optional per-delivery identity.
    /// </summary>
    void NotifyTurnAborted(string itemId, string reason, bool failed, string? threadId, string? taskId, string? deliveryId, DateTimeOffset? cancelDelegationsSince = null)
        => NotifyTurnAborted(itemId, reason, failed, threadId, taskId, cancelDelegationsSince);

    /// <summary>
    /// Atomically validates freshness and records acceptance ownership for a collaboration delivery before it is
    /// enqueued or executed on the target session. Subsequent acknowledgements for the same delivery are idempotent.
    /// </summary>
    bool TryAcceptCollabDelivery(
        string itemId,
        string? threadId,
        string? taskId,
        bool expectReply,
        string? deliveryId,
        Func<bool>? shouldCommit = null) =>
        shouldCommit is null || shouldCommit();

    /// <summary>
    /// Atomically checks freshness and claims dispatch ownership for a queued collaboration delivery right before a turn
    /// starts executing. Returns false if the delivery was already consumed or cancelled before dispatch took ownership.
    /// </summary>
    bool TryClaimCollabDelivery(string itemId, string? threadId, string? taskId, string? deliveryId, Func<bool>? shouldDispatch = null) =>
        shouldDispatch is null || shouldDispatch();

    /// <summary>
    /// Releases tracked wakeup state for a collaboration delivery that finished, stopped, failed, or was discarded
    /// without an active reply expectation.
    /// </summary>
    void ReleaseCollabDelivery(string itemId, string? deliveryId) { }

    /// <summary>Cancels a delegated task, stops its delegate, and cascades to the tasks that delegate spawned in turn.</summary>
    Task<bool> CancelTaskAsync(string taskId, string reason, CancellationToken ct = default);

    /// <summary>Reports a live permission approval request from a spawned child agent so its parent can approve in-place.</summary>
    void ReportChildApproval(CollabBubbledApproval approval);

    /// <summary>Clears any pending bubbled approval for <paramref name="childItemId"/> (resolved or cancelled).</summary>
    void ClearChildApproval(string childItemId, string? approvalId = null);

    IReadOnlyList<CollabBubbledApproval> GetBubbledApprovalsForParent(string parentItemId);

    /// <summary>Lists peer sessions visible from <paramref name="callerItemId"/> (current project or merged group).</summary>
    IReadOnlyList<CollabPeerInfo> ListPeers(string callerItemId, string scope = "group");

    /// <summary>Lists installed agents eligible for delegation (with search aliases such as <c>agy</c> / <c>反重力</c>).</summary>
    IReadOnlyList<CollabAvailableAgentInfo> ListAvailableAgents();

    /// <summary>Lists direct child <see cref="ProjectItem"/>s spawned by <paramref name="parentItemId"/>, in any project (a delegate on another computer too).</summary>
    IReadOnlyList<ProjectItem> GetSpawnedChildren(string parentItemId);

    /// <summary>
    /// The paired host's name when <paramref name="itemId"/> lives in a project of another computer; null for a session
    /// here. Reads the projects: call it on the UI thread.
    /// </summary>
    string? HostNameOf(string itemId);

    /// <summary>Lists active collaboration threads involving <paramref name="itemId"/>.</summary>
    IReadOnlyList<CollabThreadRecord> GetThreadsForItem(string itemId);

    /// <summary>Lists active tasks delegated by or targeting <paramref name="itemId"/>.</summary>
    IReadOnlyList<CollabTaskRecord> GetTasksForItem(string itemId);

    /// <summary>
    /// Returns true when a <see cref="Changed"/> notification for <paramref name="changedItemId"/> (or a global refresh
    /// when null/empty) can affect <paramref name="observerItemId"/>'s collaboration topology, threads, or bubbled approvals.
    /// Thread-safe: safe to call from any thread without touching UI-only collections.
    /// </summary>
    bool IsRelevantChange(string observerItemId, string? changedItemId) => true;

    /// <summary>
    /// Subscribes <paramref name="handler"/> to targeted collaboration updates affecting <paramref name="observerItemId"/>
    /// (plus global null/empty refreshes). Disposing the returned handle unsubscribes immediately.
    /// </summary>
    IDisposable SubscribeItemChanges(string observerItemId, Action<string?> handler)
    {
        void OnChanged(string? itemId)
        {
            if (IsRelevantChange(observerItemId, itemId))
            {
                handler(itemId);
            }
        }
        Changed += OnChanged;
        return new CollabSubscription(() => Changed -= OnChanged);
    }

    /// <summary>
    /// Subscribes <paramref name="handler"/> to targeted collaboration updates affecting <paramref name="observerItemId"/>,
    /// classifying topology, activity-only, and item-title updates.
    /// </summary>
    IDisposable SubscribeItemChanges(string observerItemId, Action<string?, CollabChangeKind> handler) =>
        SubscribeItemChanges(observerItemId, itemId => handler(itemId, CollabChangeKind.Topology));

    CollabThreadRecord? FindThread(string threadId);

    /// <summary>Delivers a user-initiated direct message to another session (bypassing the current model).</summary>
    Task<CollabThreadRecord?> SendUserDirectMessageAsync(string fromItemId, string targetItemId, string text, CancellationToken ct = default);

    /// <summary>
    /// Delivers one user message to several sessions at once (bypassing the current model): one shared thread, each
    /// target told who else was addressed. One target behaves like <see cref="SendUserDirectMessageAsync"/>.
    /// </summary>
    Task<CollabThreadRecord?> SendUserBroadcastAsync(string fromItemId, IReadOnlyList<string> targetItemIds, string text, CancellationToken ct = default);

    /// <summary>User interjection inside a negotiation thread: records the message and broadcasts to participants.</summary>
    Task<bool> InterjectInThreadAsync(string threadId, string text, bool extendBudget = false, CancellationToken ct = default);

    /// <summary>Formats structured mention metadata appended to a user prompt when sessions/agents are @-mentioned.</summary>
    string FormatMentionContextBlock(string callerItemId, IReadOnlyList<CollabMentionReference> mentions);

    /// <summary>Resolves well-known aliases (e.g. <c>反重力</c> -> <c>agy</c>, <c>cc</c> -> <c>claude</c>).</summary>
    string? ResolveAgentAlias(string input);
}

public sealed class CollabCoordinator : ICollabCoordinator, ICollabToolHandler, ICollabOperations, IDisposable
{

    private static readonly Dictionary<string, string[]> ExtraAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = ["claude", "cc", "claude-code", "claudecode", "克劳德"],
        ["codex"] = ["codex", "codex-cli", "openai"],
        ["agy"] = ["agy", "antigravity", "antigravity-cli", "反重力", "反重力cli"],
        ["gemini"] = ["gemini", "gemini-cli"],
        ["cursor"] = ["cursor", "cursor-agent"],
        ["opencode"] = ["opencode"],
        ["copilot"] = ["copilot", "github-copilot"],
        ["qwen"] = ["qwen", "qwen-code", "通义"],
        ["kimi"] = ["kimi", "kimi-code"],
    };

    private readonly IProjectService _projects;
    // Thread-safe reads of the projects; _projects itself is only used to change them (on the UI thread) and for events.
    private readonly CollabProjectIndex _index;
    private readonly IAgentCatalog _catalog;
    private readonly IAgentScanner _scanner;
    private readonly ISettingsStore _settings;
    private readonly CollabMcpServer _server;
    private readonly CollabCliMcpInjector? _cliInjector;
    private readonly IItemActivityService? _activity;
    private readonly TerminalSessionRegistry? _terminalSessions;
    private readonly Func<ITabContentRegistry?>? _tabRegistryFactory;
    private readonly Func<IWorkspaceManager?>? _workspaceManagerFactory;

    // Every session the bus talks to (GUI tabs, terminals, conversations on other computers), reached through providers.
    private readonly CollabSessions _sessions = new();
    private readonly RemoteSessionProvider _remoteProvider;
    private readonly TerminalSessionProvider _terminalProvider;
    // Tasks, threads, open cards, owed answers, blocked await_events and read marks; persisted (collab/state.json).
    private readonly CollabLedger _ledger;
    // The MCP tools agents see; each maps its JSON arguments to one of the ICollabOperations below.
    private readonly CollabToolRegistry _tools;
    // Rules product decisions may change (wake-ups, cascades, budgets, limits); defaults follow the user's settings.
    private readonly ICollabPolicy _policy;
    private readonly Func<ICollabRemoteHosts?>? _remoteHostsFactory;
    private readonly object _observerGate = new();
    private readonly Dictionary<string, List<Action<string?, CollabChangeKind>>> _itemObservers = new(StringComparer.Ordinal);
    private bool _disposed;

    public CollabCoordinator(
        IProjectService projects,
        IAgentCatalog catalog,
        IAgentScanner scanner,
        ISettingsStore settings,
        CollabMcpServer server,
        CollabCliMcpInjector? cliInjector = null,
        IItemActivityService? activity = null,
        ISignalHub? signals = null,
        TerminalSessionRegistry? terminalSessions = null,
        Func<ITabContentRegistry?>? tabRegistryFactory = null,
        Func<IWorkspaceManager?>? workspaceManagerFactory = null,
        CollabStateStore? store = null,
        Func<ICollabRemoteHosts?>? remoteHostsFactory = null,
        IEnumerable<ICollabTool>? tools = null,
        ICollabPolicy? policy = null)
    {
        _projects = projects;
        // Subscribed before this coordinator's own handler, so that handler already sees the new snapshot.
        _index = new CollabProjectIndex(projects);
        _catalog = catalog;
        _scanner = scanner;
        _settings = settings;
        _server = server;
        _cliInjector = cliInjector;
        _activity = activity;
        _terminalSessions = terminalSessions;
        _tabRegistryFactory = tabRegistryFactory;
        _workspaceManagerFactory = workspaceManagerFactory;
        _remoteHostsFactory = remoteHostsFactory;
        _tools = new CollabToolRegistry(tools ?? CollabToolRegistry.BuiltIn);
        _policy = policy ?? new DefaultCollabPolicy(settings);
        _ledger = new CollabLedger(store);
        _ledger.Restore();

        // First match wins: another computer's project, then a GUI tab here, then a terminal here.
        _remoteProvider = new RemoteSessionProvider(_sessions, () => RemoteHosts, this, id => RaiseChanged(id));
        _sessions.AddProvider(_remoteProvider);
        _sessions.AddProvider(new LocalGuiSessionProvider(_sessions, tabRegistryFactory));
        _terminalProvider = new TerminalSessionProvider(
            _sessions,
            _index,
            terminalSessions,
            tabRegistryFactory,
            settings,
            catalog,
            signals,
            activity,
            NotifyTurnCompleted,
            (id, reason, failed, onlyDeliveryIds, onlyOwnerLinkIds) => NotifySessionAbortedAllPending(id, reason, failed, onlyDeliveryIds, onlyOwnerLinkIds),
            NotifyTerminalSessionClosed,
            NotifyTerminalSessionRetired,
            NotifyTerminalLinkStopped,
            NotifyTerminalDeliveryAccepted);
        _sessions.AddProvider(_terminalProvider);

        _server.AttachHandler(this);
        _projects.Changed += OnProjectsChanged;
        _activity?.Changed += OnActivityChanged;
    }

    public event Action<string?>? Changed;

    public CollabMcpServer Server => _server;

    /// <summary>Number of await_events calls currently blocked (diagnostics and tests).</summary>
    internal int PendingAwaitCount
    {
        get
        {
            return _ledger.WaiterCount;
        }
    }

    public bool IsEnabled => _settings.Current.Agents.Collab.Enabled && _server.IsRunning;

    public AcpMcpServerConfig? BuildAcpMcpServer(string itemId)
    {
        if (!IsEnabled) return null;
        return _server.BuildAcpServerConfig(itemId, ItemLaunchService.ResolveForwarderPath());
    }

    internal (AcpMcpServerConfig Config, IDisposable Lease)? TryBuildAcpMcpServerLease(string itemId, string? leaseId = null)
    {
        if (!IsEnabled) return null;
        return _server.TryBuildAcpServerConfigLease(itemId, ItemLaunchService.ResolveForwarderPath(), leaseId);
    }

    private sealed class BridgeLifetime
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _deliveryIds = new(StringComparer.Ordinal);
        private long _generation;
        private bool _unregistered;

        public bool IsActive(long capturedGeneration)
        {
            lock (_gate) return !_unregistered && _generation == capturedGeneration;
        }

        public void MarkRegistered()
        {
            lock (_gate) _unregistered = false;
        }

        public HashSet<string> MarkUnregisteredAndTakeDeliveries()
        {
            lock (_gate)
            {
                _generation++;
                _unregistered = true;
                var snapshot = new HashSet<string>(_deliveryIds, StringComparer.Ordinal);
                _deliveryIds.Clear();
                return snapshot;
            }
        }

        public bool TryBeginDelivery(string? deliveryId, out long capturedGeneration)
        {
            lock (_gate)
            {
                if (_unregistered)
                {
                    capturedGeneration = _generation;
                    return false;
                }
                capturedGeneration = _generation;
                if (!string.IsNullOrWhiteSpace(deliveryId))
                {
                    _deliveryIds.Add(deliveryId);
                }
                return true;
            }
        }

        public bool TryTrackDeliveryIfActive(string? deliveryId)
        {
            if (string.IsNullOrWhiteSpace(deliveryId)) return false;
            lock (_gate)
            {
                if (_unregistered)
                {
                    return false;
                }
                _deliveryIds.Add(deliveryId);
                return true;
            }
        }

        public void UntrackDelivery(string? deliveryId)
        {
            if (string.IsNullOrWhiteSpace(deliveryId)) return;
            lock (_gate)
            {
                _deliveryIds.Remove(deliveryId);
            }
        }
    }

    internal const long AmbiguousLegacyIngressToken = -1;

    private sealed class InFlightDelivery(
        long ingressToken,
        string envelope,
        string? deliveryId,
        string? threadId,
        string? taskId,
        bool expectReply,
        CancellationToken cancellationToken)
    {
        public long IngressToken { get; } = ingressToken;
        public string Envelope { get; } = envelope;
        public string NormalizedEnvelope { get; } = NormalizeEnvelopeForComparison(envelope);
        public string? DeliveryId { get; } = deliveryId;
        public string? ThreadId { get; } = threadId;
        public string? TaskId { get; } = taskId;
        public bool ExpectReply { get; } = expectReply;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public Func<bool>? Guard { get; set; }
        public long BridgeGeneration;
        public bool BeginCompleted;
        public bool Accepted { get; set; }
        public bool AcceptedWithGuardOrId { get; set; }
        public bool Dispatched { get; set; }
    }

    private static string NormalizeEnvelopeForComparison(string text) =>
        text.ReplaceLineEndings("\n").Trim();

    [ThreadStatic]
    private static bool s_observingGuard;
    [ThreadStatic]
    private static InFlightDelivery? s_observedGuardDelivery;
    [ThreadStatic]
    private static long s_lastGuardPassedToken;
    private long _ingressTokenSeq;
    private readonly ConditionalWeakTable<ICollabSessionBridge, BridgeLifetime> _bridgeLifetimes = new();
    private readonly object _deliveryLifetimeGate = new();
    private readonly Dictionary<string, BridgeLifetime> _deliveryLifetimeById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<InFlightDelivery>> _inFlightDeliveries = new(StringComparer.Ordinal);

    private bool TryBeginBridgeDelivery(BridgeLifetime lifetime, string? deliveryId, out long capturedGeneration)
    {
        lock (_deliveryLifetimeGate)
        {
            if (!lifetime.TryBeginDelivery(deliveryId, out capturedGeneration))
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(deliveryId))
            {
                _deliveryLifetimeById[deliveryId] = lifetime;
            }
            return true;
        }
    }

    private void TransferBridgeDeliveryIfActive(BridgeLifetime targetLifetime, string deliveryId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId)) return;
        lock (_deliveryLifetimeGate)
        {
            if (targetLifetime.TryTrackDeliveryIfActive(deliveryId))
            {
                _deliveryLifetimeById[deliveryId] = targetLifetime;
            }
        }
    }

    private HashSet<string> UnregisterBridgeLifetime(BridgeLifetime lifetime)
    {
        lock (_deliveryLifetimeGate)
        {
            var deliveries = lifetime.MarkUnregisteredAndTakeDeliveries();
            foreach (var id in deliveries)
            {
                if (_deliveryLifetimeById.TryGetValue(id, out var mapped) && ReferenceEquals(mapped, lifetime))
                {
                    _deliveryLifetimeById.Remove(id);
                }
            }
            return deliveries;
        }
    }

    private void UntrackBridgeDeliveryForOwner(BridgeLifetime ownerLifetime, string? deliveryId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId)) return;
        lock (_deliveryLifetimeGate)
        {
            ownerLifetime.UntrackDelivery(deliveryId);
            if (_deliveryLifetimeById.TryGetValue(deliveryId, out var mapped) && ReferenceEquals(mapped, ownerLifetime))
            {
                _deliveryLifetimeById.Remove(deliveryId);
            }
        }
    }

    private void UntrackBridgeDelivery(string itemId, string? deliveryId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId)) return;
        lock (_deliveryLifetimeGate)
        {
            if (_deliveryLifetimeById.Remove(deliveryId, out var ownerLifetime))
            {
                ownerLifetime.UntrackDelivery(deliveryId);
                return;
            }
        }
        if (_sessions.Find(itemId) is { } bridge && _bridgeLifetimes.TryGetValue(bridge, out var lifetime))
        {
            lifetime.UntrackDelivery(deliveryId);
        }
    }

    private void RegisterInFlightDeliveryLocked(string targetItemId, InFlightDelivery delivery)
    {
        if (!_inFlightDeliveries.TryGetValue(targetItemId, out var list))
        {
            list = [];
            _inFlightDeliveries[targetItemId] = list;
        }
        list.Add(delivery);
    }

    private void RemoveInFlightDeliveryLocked(string targetItemId, InFlightDelivery delivery)
    {
        if (!_inFlightDeliveries.TryGetValue(targetItemId, out var list)) return;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(list[i], delivery))
            {
                list.RemoveAt(i);
                break;
            }
        }
        if (list.Count == 0)
        {
            _inFlightDeliveries.Remove(targetItemId);
        }
    }

    private bool HasInFlightDeliveryLocked(string targetItemId, string deliveryId) =>
        !string.IsNullOrWhiteSpace(deliveryId)
        && _inFlightDeliveries.TryGetValue(targetItemId, out var list)
        && list.Any(d => string.Equals(d.DeliveryId, deliveryId, StringComparison.Ordinal));

    private static bool InvokeGuardWithObservation(Func<bool> guard, out InFlightDelivery? observedDelivery)
    {
        var previousObserving = s_observingGuard;
        var previousObserved = s_observedGuardDelivery;
        s_observingGuard = true;
        s_observedGuardDelivery = null;
        try
        {
            var result = guard();
            observedDelivery = s_observedGuardDelivery;
            return result;
        }
        finally
        {
            s_observingGuard = previousObserving;
            s_observedGuardDelivery = previousObserved;
        }
    }

    private bool IsInFlightTokenActive(string targetItemId, long ingressToken)
    {
        if (ingressToken <= 0) return false;
        lock (_ledger.Gate)
        {
            return _inFlightDeliveries.TryGetValue(targetItemId, out var list)
                && list.Any(d => d.IngressToken == ingressToken);
        }
    }

    private static Task<bool> DeliverToBridgeWithoutOuterGuardObservation(
        ICollabSessionBridge bridge,
        string envelope,
        string? threadId,
        string? taskId,
        bool expectReply,
        string? deliveryId,
        Func<bool> effectiveShouldCommit,
        CancellationToken ct)
    {
        var previousObserving = s_observingGuard;
        var previousObserved = s_observedGuardDelivery;
        s_observingGuard = false;
        s_observedGuardDelivery = null;
        try
        {
            return bridge.DeliverCollabPromptAsync(envelope, threadId, taskId, expectReply, deliveryId, effectiveShouldCommit, ct);
        }
        finally
        {
            s_observingGuard = previousObserving;
            s_observedGuardDelivery = previousObserved;
        }
    }

    /// <summary>
    /// Captures a primitive ingress token on the calling thread when an ID-less, guard-less legacy 5-argument call
    /// enters the real GUI, deriving evidence from actual ingress inputs (normalized payload, exact string reference,
    /// caller cancellation token, or an unambiguous calling-thread guard check). Returns
    /// <see cref="AmbiguousLegacyIngressToken"/> when concurrent calls are genuinely indistinguishable so later queue
    /// drains never steal an identifiable call's ownership.
    /// </summary>
    internal long TryCaptureLegacyIngressToken(
        string targetItemId,
        string promptText,
        string? threadId,
        string? taskId,
        bool expectReply,
        CancellationToken cancellationToken)
    {
        var threadGuardToken = s_lastGuardPassedToken;
        s_lastGuardPassedToken = 0;
        var normalizedPrompt = NormalizeEnvelopeForComparison(promptText);
        lock (_ledger.Gate)
        {
            if (!_inFlightDeliveries.TryGetValue(targetItemId, out var list) || list.Count == 0)
            {
                return AmbiguousLegacyIngressToken;
            }

            List<InFlightDelivery>? matches = null;
            for (var i = 0; i < list.Count; i++)
            {
                var candidate = list[i];
                if (candidate.Accepted || candidate.ExpectReply != expectReply) continue;
                if (!string.Equals(candidate.ThreadId, threadId, StringComparison.Ordinal)) continue;
                if (!string.Equals(candidate.TaskId, taskId, StringComparison.Ordinal)) continue;
                if (!string.Equals(candidate.NormalizedEnvelope, normalizedPrompt, StringComparison.Ordinal)) continue;
                (matches ??= []).Add(candidate);
            }
            if (matches is null || matches.Count == 0)
            {
                return AmbiguousLegacyIngressToken;
            }
            if (matches.Count == 1)
            {
                return matches[0].IngressToken;
            }

            // Multiple concurrent ID-less calls share identical thread and normalized payload: refine only with
            // factual ingress evidence (unique original string instance, unique cancellable caller token, or a
            // single uncompeted guard check on this thread). Otherwise record explicit ambiguity.
            InFlightDelivery? uniqueByRef = null;
            for (var i = 0; i < matches.Count; i++)
            {
                if (!ReferenceEquals(matches[i].Envelope, promptText)) continue;
                if (uniqueByRef is not null)
                {
                    uniqueByRef = null;
                    break;
                }
                uniqueByRef = matches[i];
            }
            if (uniqueByRef is not null)
            {
                return uniqueByRef.IngressToken;
            }

            if (cancellationToken.CanBeCanceled)
            {
                InFlightDelivery? uniqueByCt = null;
                for (var i = 0; i < matches.Count; i++)
                {
                    if (matches[i].CancellationToken != cancellationToken) continue;
                    if (uniqueByCt is not null)
                    {
                        uniqueByCt = null;
                        break;
                    }
                    uniqueByCt = matches[i];
                }
                if (uniqueByCt is not null)
                {
                    return uniqueByCt.IngressToken;
                }
            }

            if (threadGuardToken > 0)
            {
                for (var i = 0; i < matches.Count; i++)
                {
                    if (matches[i].IngressToken == threadGuardToken)
                    {
                        return matches[i].IngressToken;
                    }
                }
            }

            return AmbiguousLegacyIngressToken;
        }
    }

    private InFlightDelivery? FindInFlightDeliveryLocked(
        string targetItemId,
        string? threadId,
        string? taskId,
        bool? expectReply,
        string? deliveryId,
        Func<bool>? guard,
        InFlightDelivery? observedByGuard,
        long legacyIngressToken,
        bool forDispatch)
    {
        if (!_inFlightDeliveries.TryGetValue(targetItemId, out var list) || list.Count == 0) return null;
        if (legacyIngressToken < 0)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(deliveryId))
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].DeliveryId, deliveryId, StringComparison.Ordinal))
                {
                    return list[i];
                }
            }
            return null;
        }
        if (observedByGuard is not null)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], observedByGuard))
                {
                    return list[i];
                }
            }
        }
        if (guard is not null)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i].Guard, guard))
                {
                    return list[i];
                }
            }
        }
        if (legacyIngressToken > 0)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].IngressToken == legacyIngressToken)
                {
                    return list[i];
                }
            }
            return null;
        }
        if (guard is not null)
        {
            return null;
        }
        // Without a guard observation, delivery ID, or captured ingress token, only attribute when a single
        // unambiguous anonymous matching in-flight call exists; never steal a call accepted via guard/ID or guess.
        InFlightDelivery? uniqueMatch = null;
        for (var i = 0; i < list.Count; i++)
        {
            var candidate = list[i];
            if (candidate.AcceptedWithGuardOrId || !string.IsNullOrWhiteSpace(candidate.DeliveryId)) continue;
            if (forDispatch ? (!candidate.Accepted || candidate.Dispatched) : candidate.Accepted) continue;
            if (expectReply is { } expected && candidate.ExpectReply != expected) continue;
            if (!string.Equals(candidate.ThreadId, threadId, StringComparison.Ordinal)) continue;
            if (!string.Equals(candidate.TaskId, taskId, StringComparison.Ordinal)) continue;
            if (uniqueMatch is not null)
            {
                return null;
            }
            uniqueMatch = candidate;
        }
        if (uniqueMatch is not null || !forDispatch)
        {
            return uniqueMatch;
        }
        for (var i = 0; i < list.Count; i++)
        {
            var candidate = list[i];
            if (candidate.AcceptedWithGuardOrId || !string.IsNullOrWhiteSpace(candidate.DeliveryId)) continue;
            if (candidate.Dispatched) continue;
            if (expectReply is { } expected && candidate.ExpectReply != expected) continue;
            if (!string.Equals(candidate.ThreadId, threadId, StringComparison.Ordinal)) continue;
            if (!string.Equals(candidate.TaskId, taskId, StringComparison.Ordinal)) continue;
            if (uniqueMatch is not null)
            {
                return null;
            }
            uniqueMatch = candidate;
        }
        return uniqueMatch;
    }

    public void RegisterGuiSession(ICollabSessionBridge bridge)
    {
        if (_bridgeLifetimes.TryGetValue(bridge, out var lifetime))
        {
            lifetime.MarkRegistered();
        }
        _sessions.Register(bridge);
        RaiseChanged(bridge.ItemId);
    }

    public void UnregisterGuiSession(string itemId)
    {
        if (_sessions.Find(itemId) is { } existing && _bridgeLifetimes.TryGetValue(existing, out var lifetime))
        {
            _ = UnregisterBridgeLifetime(lifetime);
        }
        if (!_sessions.Unregister(itemId)) return;
        CompleteGuiUnregister(itemId);
    }

    public void UnregisterGuiSession(ICollabSessionBridge bridge) =>
        UnregisterGuiSession(bridge, ownedDeliveryIds: null);

    internal void UnregisterGuiSession(ICollabSessionBridge bridge, IReadOnlySet<string>? ownedDeliveryIds)
    {
        var lifetime = _bridgeLifetimes.GetOrCreateValue(bridge);
        var bridgeDeliveries = UnregisterBridgeLifetime(lifetime);

        if (_sessions.Unregister(bridge))
        {
            CompleteGuiUnregister(bridge.ItemId);
            return;
        }

        HashSet<string>? scopedDeliveries = null;
        if (ownedDeliveryIds is { Count: > 0 })
        {
            scopedDeliveries = new HashSet<string>(ownedDeliveryIds, StringComparer.Ordinal);
        }
        if (bridgeDeliveries.Count > 0)
        {
            scopedDeliveries ??= new HashSet<string>(StringComparer.Ordinal);
            scopedDeliveries.UnionWith(bridgeDeliveries);
        }
        if (scopedDeliveries is { Count: > 0 })
        {
            // If another registered bridge (e.g. the underlying connected GUI after retiring a forwarding adapter)
            // is actively executing one of these deliveries (either by exact delivery ID or legacy thread/task match),
            // transfer tracking atomically only if the delivery is still active under the ledger gate.
            var current = _sessions.Find(bridge.ItemId);
            string? sampledActiveDeliveryId = null;
            string? sampledActiveThreadId = null;
            string? sampledActiveTaskId = null;
            var hasActiveSample = current is not null
                && !ReferenceEquals(current, bridge)
                && current.TryGetActiveCollabTurn(out sampledActiveThreadId, out sampledActiveTaskId, out sampledActiveDeliveryId);

            // Under the ledger gate: (1) preserve & transfer any still-live actively executing delivery without
            // reinserting an ID that completed/released during sampling, and (2) revoke in-flight unaccepted
            // provisional entries so DeliverEnvelopeAsync rolls back cleanly when the call returns.
            List<string>? deferredUnaccepted = null;
            lock (_ledger.Gate)
            {
                if (hasActiveSample && current is not null)
                {
                    var matchedActiveId = !string.IsNullOrWhiteSpace(sampledActiveDeliveryId) && scopedDeliveries.Contains(sampledActiveDeliveryId)
                        ? sampledActiveDeliveryId
                        : string.IsNullOrWhiteSpace(sampledActiveDeliveryId)
                            ? _ledger.FindDispatchedPendingDeliveryId(bridge.ItemId, sampledActiveThreadId, sampledActiveTaskId, scopedDeliveries)
                            : null;
                    if (!string.IsNullOrWhiteSpace(matchedActiveId))
                    {
                        scopedDeliveries.Remove(matchedActiveId);
                        if (_ledger.HasActivePendingDelivery(bridge.ItemId, matchedActiveId, threadId: null, taskId: null)
                            || _ledger.HasActiveWakeupByDeliveryId(bridge.ItemId, matchedActiveId))
                        {
                            var currentLifetime = _bridgeLifetimes.GetOrCreateValue(current);
                            _ledger.UpdatePendingBridgeOwner(bridge.ItemId, matchedActiveId, currentLifetime);
                            TransferBridgeDeliveryIfActive(currentLifetime, matchedActiveId);
                        }
                    }
                }

                foreach (var id in scopedDeliveries)
                {
                    if (HasInFlightDeliveryLocked(bridge.ItemId, id)
                        && _ledger.TryDiscardUnacceptedPendingByDeliveryId(bridge.ItemId, id))
                    {
                        (deferredUnaccepted ??= []).Add(id);
                    }
                }
            }
            if (deferredUnaccepted is not null)
            {
                foreach (var id in deferredUnaccepted)
                {
                    scopedDeliveries.Remove(id);
                }
            }
        }
        if (scopedDeliveries is { Count: > 0 })
        {
            AbortTasksForTarget(
                bridge.ItemId,
                "The delegate session was closed before it answered.",
                failed: false,
                allOpenTasks: false,
                allPending: true,
                wakeCaller: false,
                onlyDeliveryIds: scopedDeliveries);
        }
    }

    private void CompleteGuiUnregister(string itemId)
    {
        ClearChildApproval(itemId);
        // The session is gone, so whatever it was asked to answer will never be answered. No wake-up: this also runs
        // while the app shuts down, and the delegator learns the outcome from await_events or the persisted ledger.
        AbortTasksForTarget(itemId, "The delegate session was closed before it answered.", failed: false, allOpenTasks: true, allPending: true, wakeCaller: false);

        // Cancel any unfinished tasks this closed session delegated so descendants are not left running on closed threads.
        List<string> delegated;
        lock (_ledger.Gate)
        {
            delegated = _ledger.Tasks.Values
                .Where(t => t.CallerItemId == itemId && !t.IsFinished)
                .Select(t => t.TaskId)
                .ToList();
        }
        foreach (var delegatedTaskId in delegated)
        {
            CancelTaskTree(delegatedTaskId, "The delegating session was closed.", CancellationToken.None);
        }

        RaiseChanged(itemId);
    }

    public void ReportChildApproval(CollabBubbledApproval approval)
    {
        lock (_ledger.Gate)
        {
            _ledger.SetApproval(approval);
            foreach (var task in _ledger.Tasks.Values)
            {
                if (task.TargetItemId == approval.ChildItemId && task.Status == CollabTaskStatus.Running)
                {
                    _ledger.SetTaskStatus(task, CollabTaskStatus.WaitingApproval, $"Waiting approval: {approval.ToolTitle}");
                }
            }
        }
        _ledger.SignalWaiters();
        RaiseChanged(approval.ParentItemId);
    }

    public void ClearChildApproval(string childItemId, string? approvalId = null)
    {
        string? parentId = null;
        lock (_ledger.Gate)
        {
            var (cleared, hasRemaining) = _ledger.ClearApprovals(childItemId, approvalId);
            if (cleared.Count > 0)
            {
                parentId = cleared[^1].ParentItemId;
            }
            // The task waits as long as any card of that child is still open.
            if (cleared.Count > 0 && !hasRemaining)
            {
                foreach (var task in _ledger.Tasks.Values)
                {
                    if (task.TargetItemId == childItemId && task.Status == CollabTaskStatus.WaitingApproval)
                    {
                        _ledger.SetTaskStatus(task, CollabTaskStatus.Running);
                    }
                }
            }
        }
        if (parentId is not null)
        {
            RaiseChanged(parentId);
        }
    }

    public IReadOnlyList<CollabBubbledApproval> GetBubbledApprovalsForParent(string parentItemId)
    {
        lock (_ledger.Gate)
        {
            return _ledger.Approvals.Values
                .Where(a => a.ParentItemId == parentItemId && !a.IsResolved)
                .ToList();
        }
    }

    public IReadOnlyList<ProjectItem> GetSpawnedChildren(string parentItemId)
    {
        if (_index.FindProjectOf(parentItemId) is not { } home) return [];
        // Children in the parent's own project first (sidebar order), then delegates on other computers.
        return _index.ItemsOf(home)
            .Concat(_index.Projects.Where(p => !ReferenceEquals(p, home)).SelectMany(_index.ItemsOf))
            .Where(i => string.Equals(i.ParentItemId, parentItemId, StringComparison.Ordinal))
            .ToList();
    }

    public string? HostNameOf(string itemId) =>
        _index.FindProjectOf(itemId) is { RemoteHost: { Length: > 0 } hostId } project && ProjectLocation.IsOnRemoteHost(project)
            ? _remoteHostsFactory?.Invoke()?.HostName(hostId) ?? hostId
            : null;

    /// <summary>This computer's name as paired devices see it (Settings → Remote), for "delegated from" labels.</summary>
    private string LocalDeviceName =>
        _settings.Current.Remote.DeviceName is { } named && !string.IsNullOrWhiteSpace(named) ? named.Trim() : Environment.MachineName;

    public IReadOnlyList<CollabThreadRecord> GetThreadsForItem(string itemId)
    {
        lock (_ledger.Gate)
        {
            return _ledger.GetThreadsForParticipant(itemId);
        }
    }

    public IReadOnlyList<CollabTaskRecord> GetTasksForItem(string itemId)
    {
        lock (_ledger.Gate)
        {
            return _ledger.Tasks.Values
                .Where(t => t.CallerItemId == itemId || t.TargetItemId == itemId)
                .OrderByDescending(t => t.CreatedAt)
                .ToList();
        }
    }

    public CollabThreadRecord? FindThread(string threadId)
    {
        lock (_ledger.Gate)
        {
            return _ledger.Threads.GetValueOrDefault(threadId);
        }
    }

    public IDisposable SubscribeItemChanges(string observerItemId, Action<string?> handler) =>
        SubscribeItemChanges(observerItemId, (changedItemId, _) => handler(changedItemId));

    public IDisposable SubscribeItemChanges(string observerItemId, Action<string?, CollabChangeKind> handler)
    {
        lock (_observerGate)
        {
            if (!_itemObservers.TryGetValue(observerItemId, out var handlers))
            {
                handlers = [];
                _itemObservers[observerItemId] = handlers;
            }
            handlers.Add(handler);
        }
        return new CollabSubscription(() =>
        {
            lock (_observerGate)
            {
                if (_itemObservers.TryGetValue(observerItemId, out var handlers))
                {
                    handlers.Remove(handler);
                    if (handlers.Count == 0)
                    {
                        _itemObservers.Remove(observerItemId);
                    }
                }
            }
        });
    }

    public bool IsRelevantChange(string observerItemId, string? changedItemId)
    {
        if (string.IsNullOrEmpty(changedItemId)
            || string.Equals(observerItemId, changedItemId, StringComparison.Ordinal))
        {
            return true;
        }

        if (_index.FindItem(observerItemId) is { } observer
            && string.Equals(observer.ParentItemId, changedItemId, StringComparison.Ordinal))
        {
            return true;
        }

        if (_index.FindItem(changedItemId) is { } changed
            && string.Equals(changed.ParentItemId, observerItemId, StringComparison.Ordinal))
        {
            return true;
        }

        lock (_ledger.Gate)
        {
            return _ledger.HasSharedThread(observerItemId, changedItemId)
                || _ledger.HasApproval(observerItemId, changedItemId);
        }
    }

    public IReadOnlyList<CollabPeerInfo> ListPeers(string callerItemId, string scope = "group")
    {
        var visibleProjects = ResolveVisibleProjects(callerItemId, scope);
        var result = new List<CollabPeerInfo>();
        foreach (var project in visibleProjects)
        {
            foreach (var item in _index.ItemsOf(project))
            {
                if (item.Kind is not (ProjectItemKind.Agent or ProjectItemKind.Terminal)) continue;
                var agentId = item.AgentId ?? "terminal";
                var agentName = item.AgentId is not null ? (_catalog.Find(item.AgentId)?.Name ?? item.AgentId) : "Terminal";
                var surface = item.Kind == ProjectItemKind.Agent && item.Surface == AgentSurface.Gui ? "gui" : "terminal";
                var status = DescribeStatus(item.Id);
                var cwd = string.IsNullOrWhiteSpace(item.WorkingDirectory) ? project.Path : item.WorkingDirectory!;
                var branch = item.WorkspaceId is { } wid ? _index.FindWorkspace(project, wid)?.Branch : null;
                var summary = BuildOneLineSummary(item.Id);
                result.Add(new CollabPeerInfo(
                    item.Id,
                    agentId,
                    agentName,
                    item.DisplayTitle,
                    surface,
                    status,
                    cwd,
                    branch,
                    item.ParentItemId,
                    item.CollabIsolation,
                    summary));
            }
        }
        var remotes = RemoteHosts;
        foreach (var project in VisibleRemoteProjects())
        {
            var hostName = remotes!.HostName(project.RemoteHost!);
            foreach (var item in _index.ItemsOf(project))
            {
                if (item.Kind is not (ProjectItemKind.Agent or ProjectItemKind.Terminal)) continue;
                result.Add(new CollabPeerInfo(
                    item.Id,
                    item.AgentId ?? "terminal",
                    item.AgentId is not null ? (_catalog.Find(item.AgentId)?.Name ?? item.AgentId) : "Terminal",
                    item.DisplayTitle,
                    "remote",
                    DescribeStatus(item.Id),
                    project.Path,
                    null,
                    item.ParentItemId,
                    item.CollabIsolation,
                    BuildOneLineSummary(item.Id),
                    hostName));
            }
        }
        return result;
    }

    private ICollabRemoteHosts? RemoteHosts =>
        _settings.Current.Agents.Collab.AllowRemoteHosts ? _remoteHostsFactory?.Invoke() : null;

    /// <summary>Projects on paired hosts that the user added to this computer's sidebar; their sessions are reachable peers.</summary>
    private IReadOnlyList<Project> VisibleRemoteProjects() =>
        RemoteHosts is null
            ? []
            : _index.Projects.Where(p => ProjectLocation.IsOnRemoteHost(p) && p.RemoteHost is { Length: > 0 } && p.RemoteId is { Length: > 0 }).ToList();

    public IReadOnlyList<CollabAvailableAgentInfo> ListAvailableAgents()
    {
        var installedIds = new HashSet<string>(_scanner.Current.Installations.Select(i => i.AgentId), StringComparer.OrdinalIgnoreCase);
        var settings = _settings.Current.Agents;
        var list = new List<CollabAvailableAgentInfo>();

        foreach (var def in _catalog.Definitions)
        {
            if (!settings.ProfileOf(def.Id).Enabled) continue;
            // Show installed agents first (and always allow known scanned ones; when no scan ran yet in tests, include verified catalog items).
            if (installedIds.Count > 0 && !installedIds.Contains(def.Id)) continue;
            var surface = AgentSurfaceResolver.Resolve(def, settings, acpAvailable: true);
            var aliases = ExtraAliases.TryGetValue(def.Id, out var a) ? a : [def.Id];
            list.Add(new CollabAvailableAgentInfo(
                def.Id,
                def.Name,
                def.SupportsAcp,
                surface == AgentSurface.Gui ? "gui" : "terminal",
                aliases));
        }
        return list;
    }

    public string? ResolveAgentAlias(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var needle = input.Trim().TrimStart('@');
        if (_catalog.Find(needle) is { } exact) return exact.Id;

        foreach (var (agentId, aliases) in ExtraAliases)
        {
            if (aliases.Any(a => string.Equals(a, needle, StringComparison.OrdinalIgnoreCase))
                && _catalog.Find(agentId) is not null)
            {
                return agentId;
            }
        }
        var byName = _catalog.Definitions.FirstOrDefault(d =>
            string.Equals(d.Name, needle, StringComparison.OrdinalIgnoreCase)
            || d.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
        return byName?.Id;
    }

    public string FormatMentionContextBlock(string callerItemId, IReadOnlyList<CollabMentionReference> mentions)
    {
        if (mentions.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("<octopus-mentions>");
        var order = 0;
        foreach (var m in mentions)
        {
            if (m.Kind == "session")
            {
                var status = DescribeStatus(m.TargetId);
                var item = _index.FindItem(m.TargetId);
                var surface = item?.Surface == AgentSurface.Gui ? "gui" : "terminal";
                sb.Append("  <session order=\"").Append(++order)
                  .Append("\" id=\"").Append(EscapeXml(m.TargetId))
                  .Append("\" label=\"").Append(EscapeXml(m.DisplayLabel))
                  .Append("\" agent=\"").Append(EscapeXml(m.AgentId ?? item?.AgentId ?? "terminal"))
                  .Append("\" surface=\"").Append(surface)
                  .Append("\" status=\"").Append(status).Append('"');
                if (!string.IsNullOrWhiteSpace(m.Branch))
                {
                    sb.Append(" branch=\"").Append(EscapeXml(m.Branch)).Append('"');
                }
                sb.AppendLine(" />");
            }
            else if (m.Kind == "agent")
            {
                sb.Append("  <agent order=\"").Append(++order)
                  .Append("\" id=\"").Append(EscapeXml(m.TargetId))
                  .Append("\" label=\"").Append(EscapeXml(m.DisplayLabel))
                  .AppendLine("\" action=\"delegate_or_spawn\" />");
            }
        }
        if (order > 1)
        {
            sb.AppendLine("  <!-- The user addressed these participants inline (each label appears verbatim in the message). Give each the part the sentence assigns to it, dispatch them in parallel, then await_events for their results. -->");
        }
        sb.AppendLine("  <!-- Use octopus-collab MCP tools: send_message(target_session_id, ...) or read_session(target_session_id) for <session>, and delegate_task(target, goal) for <agent>. -->");
        sb.Append("</octopus-mentions>");
        return sb.ToString();
    }

    public async Task<CollabThreadRecord?> SendUserDirectMessageAsync(string fromItemId, string targetItemId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var res = await SendMessageInternalAsync(
            callerItemId: fromItemId,
            targetItemId: targetItemId,
            message: text.Trim(),
            threadId: null,
            title: TruncateTitle(text.Trim()),
            expectReply: false,
            maxTurnsOverride: null,
            isDirectUserRelay: true,
            ct).ConfigureAwait(false);
        return res.Thread;
    }

    public async Task<CollabThreadRecord?> SendUserBroadcastAsync(string fromItemId, IReadOnlyList<string> targetItemIds, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var targets = targetItemIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && !string.Equals(id, fromItemId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (targets.Count == 0) return null;
        if (targets.Count == 1) return await SendUserDirectMessageAsync(fromItemId, targets[0], text, ct).ConfigureAwait(false);

        var body = text.Trim();
        var labels = targets.Select(id => _index.FindItem(id) is { } item ? "@" + FormatCallerLabel(item) : id).ToList();
        // Every delivery counts as a hop; widen the new thread's budget so the fan-out itself does not eat the replies'.
        var defaultMaxHops = _policy.DefaultMaxHops;
        CollabThreadRecord? thread = null;
        for (var i = 0; i < targets.Count; i++)
        {
            var others = labels.Where((_, j) => j != i);
            var message = $"{body}\n\n(Also addressed in this message: {string.Join(", ", others)}.)";
            var res = await SendMessageInternalAsync(
                callerItemId: fromItemId,
                targetItemId: targets[i],
                message: message,
                threadId: thread?.ThreadId,
                title: TruncateTitle(body),
                expectReply: false,
                maxTurnsOverride: thread is null ? defaultMaxHops + targets.Count - 1 : null,
                isDirectUserRelay: true,
                ct).ConfigureAwait(false);
            thread ??= res.Thread;
        }
        return thread;
    }

    public async Task<bool> InterjectInThreadAsync(string threadId, string text, bool extendBudget = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        List<string> participants;
        int round;
        lock (_ledger.Gate)
        {
            if (!_ledger.Threads.TryGetValue(threadId, out var thread)) return false;
            if (extendBudget)
            {
                thread.MaxHops = Math.Min(CollabSettings.MaxAllowedNegotiationHops, thread.MaxHops + 4);
                if (thread.Status == CollabThreadStatus.PausedBudget) _ledger.SetThreadStatus(thread, CollabThreadStatus.Active);
            }
            round = thread.Hops;
            _ledger.AppendMessage(thread, new CollabMessageRecord
            {
                MessageId = _ledger.NextId("msg"),
                ThreadId = threadId,
                FromItemId = "user",
                FromLabel = "User",
                Round = round,
                Text = text.Trim(),
                IsUserInterjection = true,
            });
            participants = [.. thread.ParticipantItemIds];
        }

        var envelope = CollabEnvelope.Interjection(threadId, text);
        foreach (var pid in participants)
        {
            await DeliverEnvelopeAsync(pid, envelope, threadId, taskId: null, expectReply: false, callerItemId: "user", ct).ConfigureAwait(false);
        }
        _ledger.SignalWaiters();
        RaiseChangedForItems(participants);
        return true;
    }

    /* ==================== MCP Tool Dispatch ==================== */

    public async Task<JsonObject> CallToolAsync(string callerItemId, string toolName, JsonObject? arguments, CancellationToken cancellationToken)
    {
        if (!_settings.Current.Agents.Collab.Enabled)
        {
            return ToolError("Multi-agent collaboration is currently disabled in Octopus Settings → Agents.");
        }

        if (_tools.Find(toolName) is not { } tool)
        {
            return CollabToolResult.Error($"Unknown collaboration tool: {toolName}");
        }
        return await tool.CallAsync(this, callerItemId, new CollabToolArgs(arguments), cancellationToken).ConfigureAwait(false);
    }

    public JsonArray ListTools() => _tools.Schemas();

    public CollabIsolationMode DefaultIsolation => _settings.Current.Agents.Collab.DefaultIsolation;

    public IReadOnlyList<CollabRemoteProjectInfo> ListRemoteProjects()
    {
        if (RemoteHosts is not { } remotes) return [];
        return [.. VisibleRemoteProjects().Select(p => new CollabRemoteProjectInfo(p.Id, p.Name, remotes.HostName(p.RemoteHost!), p.Path))];
    }

    public async Task<JsonObject> ReadSessionAsync(string callerItemId, string targetSessionId, int maxTurns, CancellationToken ct)
    {
        var targetId = targetSessionId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return ToolError("target_session_id is required.");
        }
        if (!IsPeerVisible(callerItemId, targetId))
        {
            return ToolError($"Session '{targetId}' is not found in your current project or merged workspace group.");
        }

        var item = _index.FindItem(targetId)!;
        if (_index.FindProjectOf(targetId) is { } targetProject && ProjectLocation.IsOnRemoteHost(targetProject))
        {
            await _sessions.EnsureAsync(targetProject, item, ct).ConfigureAwait(false);
        }
        var (observedThreadSeqs, observedTaskSeqs) = CapturePeerWakeupCeiling(callerItemId, targetId);
        var summary = ReadSessionContent(item, maxTurns);

        CancelStaleWakeupsForPeer(callerItemId, observedThreadSeqs, observedTaskSeqs);

        return ToolOk(new JsonObject
        {
            ["session_id"] = item.Id,
            ["agent_id"] = item.AgentId ?? "terminal",
            ["title"] = item.DisplayTitle,
            ["surface"] = item.Surface == AgentSurface.Gui ? "gui" : "terminal",
            ["status"] = DescribeStatus(item.Id),
            ["summary"] = summary,
        });
    }

    public async Task<JsonObject> DelegateTaskAsync(string callerItemId, CollabDelegateRequest request, CancellationToken ct)
    {
        var target = request.Target;
        var goal = request.Goal;
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(goal))
        {
            return ToolError("Both 'target' and 'goal' are required.");
        }

        var collabCfg = _settings.Current.Agents.Collab;
        var depth = ComputeDelegationDepth(callerItemId);
        if (depth >= _policy.MaxDelegationDepth)
        {
            return ToolError($"Delegation depth limit ({_policy.MaxDelegationDepth}) reached; complete the task directly or ask the user.");
        }

        // Case 1: Target matches an existing visible session id -> route directly into that session.
        if (_index.FindItem(target) is { } existingItem && IsPeerVisible(callerItemId, existingItem.Id))
        {
            if (existingItem.Id == callerItemId)
            {
                return ToolError("Cannot delegate a task to yourself.");
            }
            var title = request.Title ?? TruncateTitle(goal);
            var taskId = _ledger.NextId("task");
            var threadId = _ledger.NextId("t");
            var taskRecord = new CollabTaskRecord
            {
                TaskId = taskId,
                CallerItemId = callerItemId,
                TargetItemId = existingItem.Id,
                TargetAgentId = existingItem.AgentId ?? "terminal",
                Title = title,
                Goal = goal,
                Isolation = CollabIsolationMode.Shared,
                ThreadId = threadId,
                Status = CollabTaskStatus.Running,
                LatestActivity = "Dispatched to existing session",
            };
            var sent = await SendMessageInternalAsync(
                callerItemId,
                existingItem.Id,
                CollabEnvelope.TaskBody(taskId, goal, request.ContextFiles),
                threadId: threadId,
                title: title,
                expectReply: true,
                maxTurnsOverride: null,
                isDirectUserRelay: false,
                ct,
                delegatedTask: taskRecord).ConfigureAwait(false);
            if (!sent.Succeeded)
            {
                return ToolError(sent.Error ?? "Failed to deliver task to existing session.");
            }
            CollabTaskStatus status;
            lock (_ledger.Gate)
            {
                status = taskRecord.Status;
            }
            return ToolOk(new JsonObject
            {
                ["task_id"] = taskId,
                ["session_id"] = existingItem.Id,
                ["thread_id"] = sent.Thread?.ThreadId,
                ["reused_existing_session"] = true,
                ["status"] = status.ToString().ToLowerInvariant(),
            });
        }

        // Case 2: Spawn a new child agent session nested under callerItemId.
        var agentId = ResolveAgentAlias(target);
        if (agentId is null || _catalog.Find(agentId) is not { } definition)
        {
            return ToolError($"Unknown target session or agent '{target}'. Call list_peers to see valid session_ids and available agents.");
        }

        if (request.ProjectId is { Length: > 0 } projectId)
        {
            var remoteProject = VisibleRemoteProjects().FirstOrDefault(p => p.Id == projectId);
            if (remoteProject is not null)
            {
                return await DelegateToRemoteAsync(callerItemId, remoteProject, definition, request, ct).ConfigureAwait(false);
            }
            if (_index.FindProjectOf(callerItemId)?.Id != projectId)
            {
                return ToolError($"project_id '{projectId}' is neither a remote project from list_peers nor your own project.");
            }
        }

        var callerProject = _index.FindProjectOf(callerItemId);
        var callerItem = _index.FindItem(callerItemId);
        if (callerProject is null || callerItem is null)
        {
            return ToolError("Caller session project could not be resolved.");
        }
        if (!ProjectLocation.CanRunAgents(callerProject))
        {
            return ToolError("Agents cannot be spawned inside this remote/SSH project.");
        }

        var isolation = ParseIsolation(request.Isolation) ?? collabCfg.DefaultIsolation;
        var isolationKey = IsolationToKey(isolation);
        var requestedSurface = ParseSurface(request.Surface);
        var resolvedSurface = AgentSurfaceResolver.Resolve(
            definition,
            _settings.Current.Agents,
            requestedSurface,
            acpAvailable: ProjectLocation.IsOnThisComputer(callerProject));

        if (resolvedSurface == AgentSurface.Terminal && !collabCfg.EnableTerminalBridge)
        {
            return ToolError($"Agent '{definition.Name}' resolves to Terminal mode, but Terminal CLI collaboration bridge is disabled in settings.");
        }

        var trimmedGoal = goal.Trim();
        CollabTaskRecord? dup;
        lock (_ledger.Gate)
        {
            dup = _ledger.Tasks.Values.FirstOrDefault(t =>
                !t.IsFinished
                && t.CallerItemId == callerItemId
                && string.Equals(t.TargetAgentId, definition.Id, StringComparison.OrdinalIgnoreCase)
                && t.Isolation == isolation
                && string.Equals(t.Goal.Trim(), trimmedGoal, StringComparison.Ordinal));
        }
        if (dup is not null
            && _index.FindItem(dup.TargetItemId) is { } dupItem
            && _index.FindProjectOf(dup.TargetItemId) is { } dupProject)
        {
            await InvokeOnUiAsync(() =>
            {
                var control = _tabRegistryFactory?.Invoke()?.GetOrCreate(dupProject, dupItem);
                if (control is AgentGui.AgentGuiTabView guiView)
                {
                    guiView.EnsureStarted();
                }
                else if (control is Terminals.TerminalTabView termView)
                {
                    termView.EnsureStarted(startWhenHidden: true);
                }
            }).ConfigureAwait(false);
            return ToolOk(new JsonObject
            {
                ["task_id"] = dup.TaskId,
                ["session_id"] = dup.TargetItemId,
                ["thread_id"] = dup.ThreadId,
                ["agent_id"] = dup.TargetAgentId,
                ["title"] = dup.Title,
                ["status"] = dup.Status.ToString().ToLowerInvariant(),
                ["deduplicated"] = true,
                ["note"] = "An identical task is already running on this agent; wait for it with await_events or cancel it with cancel_task before re-delegating.",
            });
        }

        var taskIdNew = _ledger.NextId("task");
        var threadIdNew = _ledger.NextId("t");
        var deliveryIdNew = _ledger.NextId("del");
        var taskTitle = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(taskTitle))
        {
            taskTitle = $"{definition.Name} · {TruncateTitle(goal, 28)}";
        }

        // Resolve working directory & optional Git Worktree isolation.
        var targetWorkspaceId = callerItem.WorkspaceId;
        var targetCwd = string.IsNullOrWhiteSpace(callerItem.WorkingDirectory) ? callerProject.Path : callerItem.WorkingDirectory!;
        string? worktreeBranch = null;
        string? worktreeNote = null;

        if (isolation == CollabIsolationMode.Worktree)
        {
            var wm = _workspaceManagerFactory?.Invoke();
            if (wm is not null)
            {
                var branchSuffix = $"{definition.Id}-{taskIdNew[^4..]}";
                var branchName = $"collab/{branchSuffix}";
                try
                {
                    var wtResult = await wm.CreateAsync(
                        callerProject,
                        new CreateWorkspaceRequest(
                            CreateBranch: true,
                            BranchName: branchName,
                            BaseRef: null,
                            IsRemoteBranch: false,
                            Task: null,
                            InjectTaskPrompt: false,
                            AfterCreate: WorkspaceAfterCreate.None),
                        ct).ConfigureAwait(false);
                    if (wtResult.Succeeded && wtResult.Workspace is { } ws)
                    {
                        targetWorkspaceId = ws.Id;
                        targetCwd = ws.Path;
                        worktreeBranch = ws.Branch;
                    }
                    else
                    {
                        worktreeNote = $"Worktree unavailable ({wtResult.Error ?? "not a git repo"}); fell back to shared workspace.";
                        isolation = CollabIsolationMode.Shared;
                        isolationKey = IsolationToKey(isolation);
                    }
                }
                catch (Exception ex)
                {
                    worktreeNote = $"Worktree creation failed ({ex.Message}); fell back to shared workspace.";
                    isolation = CollabIsolationMode.Shared;
                    isolationKey = IsolationToKey(isolation);
                }
            }
            else
            {
                worktreeNote = "WorkspaceManager unavailable; fell back to shared workspace.";
                isolation = CollabIsolationMode.Shared;
                isolationKey = IsolationToKey(isolation);
            }
        }

        var callerLabel = FormatCallerLabel(callerItem);
        var contextFiles = request.ContextFiles;
        var initialPrompt = CollabEnvelope.Delegation(
            taskIdNew, threadIdNew, callerLabel, goal, contextFiles,
            worktreeBranch: worktreeBranch, readOnly: isolation == CollabIsolationMode.ReadOnly);

        var childItem = new ProjectItem
        {
            Kind = ProjectItemKind.Agent,
            AgentId = definition.Id,
            Title = taskTitle,
            Surface = resolvedSurface,
            WorkspaceId = targetWorkspaceId,
            WorkingDirectory = targetCwd,
            ParentItemId = callerItemId,
            CollabIsolation = isolationKey,
            Launch = new AgentLaunchOptions
            {
                Model = request.Model,
                Effort = request.Effort,
                Permission = isolation == CollabIsolationMode.ReadOnly ? PermissionModes.Plan : null,
                Surface = resolvedSurface,
                Prompt = initialPrompt,
                CollabThreadId = threadIdNew,
                CollabTaskId = taskIdNew,
                CollabDeliveryId = deliveryIdNew,
            },
        };

        var thread = new CollabThreadRecord
        {
            ThreadId = threadIdNew,
            Title = taskTitle,
            InitiatorItemId = callerItemId,
            ParticipantItemIds = [callerItemId, childItem.Id],
            Hops = 1,
            MaxHops = _policy.DefaultMaxHops,
            Messages =
            [
                new CollabMessageRecord
                {
                    MessageId = _ledger.NextId("msg"),
                    ThreadId = threadIdNew,
                    FromItemId = callerItemId,
                    FromLabel = callerLabel,
                    FromAgentId = callerItem.AgentId,
                    ToItemId = childItem.Id,
                    ToLabel = taskTitle,
                    Round = 1,
                    Text = goal,
                },
            ],
        };

        var record = new CollabTaskRecord
        {
            TaskId = taskIdNew,
            CallerItemId = callerItemId,
            TargetItemId = childItem.Id,
            TargetAgentId = definition.Id,
            Title = taskTitle,
            Goal = goal,
            Isolation = isolation,
            WorktreeBranch = worktreeBranch,
            WorktreePath = worktreeBranch is not null ? targetCwd : null,
            ThreadId = threadIdNew,
            Status = CollabTaskStatus.Running,
            LatestActivity = $"Starting {definition.Name} ({resolvedSurface})",
        };

        var callerTurn = ReadCallerActiveTurn(callerItemId);
        lock (_ledger.Gate)
        {
            _ledger.Add(thread);
            _ledger.Add(record);
            RecordDelegatedChildOwnershipLocked(callerItemId, callerTurn, taskIdNew);
            var initialPending = new PendingExpectation(callerItemId, taskIdNew, threadIdNew, DateTimeOffset.UtcNow, deliveryIdNew);
            _ledger.EnqueuePending(childItem.Id, initialPending);
            AcceptPendingDeliveryLocked(childItem.Id, initialPending);
        }

        // Mount the spawned agent underneath its parent in the sidebar and start its session behind the scenes.
        await InvokeOnUiAsync(() =>
        {
            _projects.AddItem(callerProject, childItem, activate: false);
            var control = _tabRegistryFactory?.Invoke()?.GetOrCreate(callerProject, childItem);
            if (control is AgentGui.AgentGuiTabView guiView)
            {
                guiView.EnsureStarted();
            }
            else if (control is Terminals.TerminalTabView termView)
            {
                termView.EnsureStarted(startWhenHidden: true);
            }
        }).ConfigureAwait(false);

        if (resolvedSurface == AgentSurface.Terminal)
        {
            _sessions.TryAttach(callerProject, childItem);
        }

        RaiseChanged(callerItemId);
        RaiseChanged(childItem.Id);

        CollabTaskStatus currentStatus;
        lock (_ledger.Gate)
        {
            currentStatus = record.Status;
        }

        var response = new JsonObject
        {
            ["task_id"] = taskIdNew,
            ["session_id"] = childItem.Id,
            ["thread_id"] = threadIdNew,
            ["agent_id"] = definition.Id,
            ["title"] = taskTitle,
            ["surface"] = resolvedSurface == AgentSurface.Gui ? "gui" : "terminal",
            ["isolation"] = isolationKey,
            ["working_directory"] = targetCwd,
            ["status"] = currentStatus.ToString().ToLowerInvariant(),
        };
        if (worktreeBranch is not null) response["worktree_branch"] = worktreeBranch;
        if (worktreeNote is not null) response["note"] = worktreeNote;
        return ToolOk(response);
    }

    public async Task<JsonObject> SendMessageAsync(string callerItemId, CollabSendRequest request, CancellationToken ct)
    {
        var targetId = request.TargetSessionId;
        var message = request.Message;
        if (string.IsNullOrWhiteSpace(targetId) || string.IsNullOrWhiteSpace(message))
        {
            return ToolError("Both 'target_session_id' and 'message' are required.");
        }
        var threadId = request.ThreadId;
        var title = request.Title;
        var expectReply = request.ExpectReply;
        var maxTurns = request.MaxTurns;

        var sent = await SendMessageInternalAsync(
            callerItemId, targetId, message, threadId, title, expectReply, maxTurns, isDirectUserRelay: false, ct).ConfigureAwait(false);

        if (!sent.Succeeded)
        {
            return ToolError(sent.Error ?? "Message delivery failed.");
        }

        return ToolOk(new JsonObject
        {
            ["thread_id"] = sent.Thread!.ThreadId,
            ["target_session_id"] = targetId,
            ["round"] = sent.Thread.Hops,
            ["max_hops"] = sent.Thread.MaxHops,
            ["status"] = "delivered",
            ["expect_reply"] = expectReply,
        });
    }

    /// <summary>
    /// Records an explicit reply on <paramref name="threadId"/> and reports recording separately from wakeup delivery.
    /// Backward-compatible receipt fields:
    /// <list type="bullet">
    /// <item><c>recorded</c>: always <c>true</c> on a non-error response.</item>
    /// <item><c>delivery</c>: <c>delivered</c> (wakeup prompt accepted by recipient), <c>await_events_signaled</c> (an active <c>await_events</c> waiter was signaled and wakeup prompt was skipped; not proof of receipt), <c>await_events_consumed</c> (the recipient already consumed the recorded reply via <c>await_events</c> before wakeup delivery), <c>wakeup_disabled</c>, <c>no_recipient</c>, or <c>failed</c>.</item>
    /// <item><c>delivered_to</c>: present only when <c>delivery</c> is <c>delivered</c> (wakeup prompt accepted).</item>
    /// <item><c>recipient_session_id</c>: present when a recipient exists but prompt delivery was not confirmed.</item>
    /// <item><c>delivery_error</c>: bounded generic error code (<c>wakeup_not_accepted</c> or <c>wakeup_delivery_error</c>) when <c>delivery</c> is <c>failed</c>.</item>
    /// <item><c>note</c>: bounded caller guidance for non-delivered outcomes (never raw receiver exception text).</item>
    /// </list>
    /// </summary>
    public async Task<JsonObject> ReplyThreadAsync(string callerItemId, string threadId, string message, string status, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(message))
        {
            return ToolError("Both 'thread_id' and 'message' are required.");
        }
        if (status is not ("continue" or "agreed" or "blocked")) status = "continue";

        var result = await RecordReplyAndWakeAsync(callerItemId, threadId, message.Trim(), status, isImplicitFallback: false, ct).ConfigureAwait(false);
        if (!result.Recorded)
        {
            return ToolError(result.Error ?? "Failed to reply to thread.");
        }

        var payload = new JsonObject
        {
            ["thread_id"] = threadId,
            ["round"] = result.Round,
            ["status"] = status,
            ["recorded"] = true,
            ["wakeup_delivered"] = result.Delivery == ReplyDeliveryOutcome.Delivered,
            ["delivery"] = result.Delivery switch
            {
                ReplyDeliveryOutcome.Delivered => "delivered",
                ReplyDeliveryOutcome.Awaited => "await_events_signaled",
                ReplyDeliveryOutcome.Consumed => "await_events_consumed",
                ReplyDeliveryOutcome.WakeDisabled => "wakeup_disabled",
                ReplyDeliveryOutcome.NoRecipient => "no_recipient",
                _ => "failed",
            },
        };

        if (result.Delivery == ReplyDeliveryOutcome.Delivered)
        {
            payload["delivered_to"] = result.RecipientItemId;
        }
        else if (result.RecipientItemId is not null)
        {
            payload["recipient_id"] = result.RecipientItemId;
            payload["recipient_session_id"] = result.RecipientItemId;
        }

        if (result.Delivery == ReplyDeliveryOutcome.Awaited)
        {
            payload["note"] = "Reply was recorded on the thread and an active await_events waiter was signaled; wakeup prompt was skipped to avoid duplicate delivery. Do not resend a duplicate reply.";
        }
        else if (result.Delivery == ReplyDeliveryOutcome.Consumed)
        {
            payload["note"] = "Reply was recorded on the thread and already consumed by the recipient via await_events; wakeup prompt was skipped to avoid duplicate delivery. Do not resend a duplicate reply.";
        }
        else if (result.Delivery == ReplyDeliveryOutcome.Failed)
        {
            var isDeliveryError = result.FailureReason == ReplyDeliveryFailureReason.DeliveryError;
            payload["delivery_error"] = isDeliveryError ? "wakeup_delivery_error" : "wakeup_not_accepted";
            payload["note"] = isDeliveryError
                ? "Reply was recorded on the thread, but wakeup delivery to the recipient session encountered an internal error. Do not resend a duplicate reply."
                : "Reply was recorded on the thread, but wakeup delivery to the recipient session was not accepted. Do not resend a duplicate reply.";
        }
        else if (result.Delivery == ReplyDeliveryOutcome.WakeDisabled)
        {
            payload["note"] = $"Reply was recorded on the thread; automatic caller wakeup is disabled. The recipient can retrieve it via await_events with thread_id '{threadId}'.";
        }

        return ToolOk(payload);
    }

    public async Task<JsonObject> AwaitEventsAsync(string callerItemId, IReadOnlyCollection<string> ids, int timeoutSeconds, CancellationToken ct)
    {
        var watched = new HashSet<string>(ids, StringComparer.Ordinal);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);

        while (true)
        {
            EventWaiter? waiter = null;
            AwaitSnapshot snap;
            List<(string ThreadId, string DeliveryId)>? consumedWakeups = null;
            var activeDeliveryId = ReadActiveDeliveryId(callerItemId);
            lock (_ledger.Gate)
            {
                ct.ThrowIfCancellationRequested();
                snap = BuildAwaitSnapshotLocked(callerItemId, watched);
                var done = snap.HasUpdate || !snap.HasPending || DateTimeOffset.UtcNow >= deadline;
                if (done)
                {
                    _ledger.MarkSeen(callerItemId, snap.Reported);
                    if (snap.HasUpdate)
                    {
                        consumedWakeups = _ledger.ConsumeWakeupsAtOrBelow(
                            callerItemId,
                            snap.ObservedThreadSeqs,
                            snap.ObservedTaskSeqs,
                            activeDeliveryId);
                    }
                }
                else
                {
                    // Registered under the same lock as the snapshot, so no change can slip in between.
                    waiter = new EventWaiter(
                        callerItemId,
                        watched,
                        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                        ct);
                    _ledger.Waiters.Add(waiter);
                }
            }

            if (waiter is null)
            {
                return FinishAwait(callerItemId, snap, consumedWakeups);
            }

            try
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    await waiter.Tcs.Task.WaitAsync(remaining, ct).ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
                // Fall through: the next loop iteration takes a final snapshot past the deadline and returns it.
            }
            finally
            {
                lock (_ledger.Gate) { _ledger.Waiters.Remove(waiter); }
            }
        }
    }

    private JsonObject FinishAwait(
        string callerItemId,
        AwaitSnapshot snap,
        IReadOnlyList<(string ThreadId, string DeliveryId)>? consumedWakeups)
    {
        if (snap.HasUpdate)
        {
            CancelStaleWakeups(callerItemId, consumedWakeups);
        }
        if (!snap.HasUpdate && !snap.HasPending)
        {
            snap.Payload["note"] = "Nothing is pending for this session: no delegated task is still running and no thread awaits a reply.";
        }
        return ToolOk(snap.Payload);
    }

    private sealed record AwaitSnapshot(
        JsonObject Payload,
        bool HasUpdate,
        bool HasPending,
        List<(string Id, long Seq)> Reported,
        Dictionary<string, long> ObservedThreadSeqs,
        Dictionary<string, long> ObservedTaskSeqs);

    /// <summary>
    /// Builds the await_events view for <paramref name="callerItemId"/>. A task or thread counts as an update only when it
    /// changed since this caller last saw it, so finished work is reported once instead of on every later call. Finished,
    /// already-seen items are omitted unless named in <paramref name="filterIds"/>.
    /// </summary>
    private AwaitSnapshot BuildAwaitSnapshotLocked(string callerItemId, HashSet<string> filterIds)
    {
        var seen = _ledger.Seen.GetValueOrDefault(callerItemId);
        var hasUpdate = false;
        var hasPending = false;
        List<(string, long)> reported = [];
        Dictionary<string, long> observedThreadSeqs = new(StringComparer.Ordinal);
        Dictionary<string, long> observedTaskSeqs = new(StringComparer.Ordinal);
        var matchedIds = new HashSet<string>(StringComparer.Ordinal);
        var tasksArr = new JsonArray();
        var threadsArr = new JsonArray();

        foreach (var t in _ledger.Tasks.Values.OrderBy(t => t.CreatedAt))
        {
            if (t.CallerItemId != callerItemId) continue;
            var explicitlyNamed = filterIds.Contains(t.TaskId) || (t.ThreadId is not null && filterIds.Contains(t.ThreadId));
            if (filterIds.Count > 0 && !explicitlyNamed) continue;
            if (filterIds.Contains(t.TaskId)) matchedIds.Add(t.TaskId);

            var isNew = t.UpdatedSeq > (seen?.GetValueOrDefault(t.TaskId) ?? 0);
            if (!t.IsFinished)
            {
                hasPending = true;
            }
            else if (isNew)
            {
                hasUpdate = true;
                observedTaskSeqs[t.TaskId] = t.UpdatedSeq;
            }
            if (!explicitlyNamed && t.IsFinished && !isNew) continue;

            reported.Add((t.TaskId, t.UpdatedSeq));
            tasksArr.Add((JsonNode)new JsonObject
            {
                ["task_id"] = t.TaskId,
                ["session_id"] = t.TargetItemId,
                ["agent_id"] = t.TargetAgentId,
                ["title"] = t.Title,
                ["status"] = t.Status.ToString().ToLowerInvariant(),
                ["new"] = isNew,
                ["latest_activity"] = t.LatestActivity,
                ["result"] = t.ResultSummary,
            });
        }

        foreach (var th in _ledger.Threads.Values.OrderBy(th => th.UpdatedAt))
        {
            if (!th.ParticipantItemIds.Contains(callerItemId, StringComparer.Ordinal)) continue;
            var explicitlyNamed = filterIds.Contains(th.ThreadId);
            if (filterIds.Count > 0 && !explicitlyNamed) continue;
            if (explicitlyNamed) matchedIds.Add(th.ThreadId);

            var latest = th.Messages.LastOrDefault();
            var isNew = th.UpdatedSeq > (seen?.GetValueOrDefault(th.ThreadId) ?? 0);
            if (isNew && latest is not null && latest.FromItemId != callerItemId)
            {
                hasUpdate = true;
                observedThreadSeqs[th.ThreadId] = th.UpdatedSeq;
            }
            // Still waiting on the other side: the caller spoke last and the thread is open.
            if (!th.IsClosed && latest?.FromItemId == callerItemId) hasPending = true;
            if (!explicitlyNamed && !isNew && (th.IsClosed || latest?.FromItemId != callerItemId)) continue;

            reported.Add((th.ThreadId, th.UpdatedSeq));
            threadsArr.Add((JsonNode)new JsonObject
            {
                ["thread_id"] = th.ThreadId,
                ["title"] = th.Title,
                ["status"] = th.StatusKey,
                ["new"] = isNew,
                ["hops"] = th.Hops,
                ["max_hops"] = th.MaxHops,
                ["latest_from"] = latest?.FromLabel,
                ["latest_message"] = latest?.Text,
            });
        }

        var payload = new JsonObject
        {
            ["tasks"] = tasksArr,
            ["threads"] = threadsArr,
        };
        // A task id from before a restart that was pruned, or a typo: say so rather than waiting on it forever.
        var unknown = filterIds.Where(id => !matchedIds.Contains(id) && !_ledger.Tasks.Values.Any(t => t.ThreadId == id)).ToList();
        if (unknown.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var id in unknown) arr.Add((JsonNode)JsonValue.Create(id));
            payload["unknown_ids"] = arr;
        }
        return new AwaitSnapshot(payload, hasUpdate, hasPending, reported, observedThreadSeqs, observedTaskSeqs);
    }

    public async Task<JsonObject> CancelDelegatedTaskAsync(string callerItemId, string taskId, string? reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return ToolError("task_id is required.");
        }
        CollabTaskStatus status;
        lock (_ledger.Gate)
        {
            if (!_ledger.Tasks.TryGetValue(taskId, out var task))
            {
                return ToolError($"Unknown task '{taskId}'.");
            }
            if (task.CallerItemId != callerItemId)
            {
                return ToolError($"Task '{taskId}' was delegated by another session; only its delegator can cancel it.");
            }
            status = task.Status;
        }
        if (status is CollabTaskStatus.Completed or CollabTaskStatus.Failed or CollabTaskStatus.Cancelled)
        {
            return ToolOk(new JsonObject
            {
                ["task_id"] = taskId,
                ["status"] = status.ToString().ToLowerInvariant(),
                ["note"] = "Task had already finished; nothing to cancel.",
            });
        }

        var cancelResult = await CancelTaskTreeAsync(
            taskId,
            string.IsNullOrWhiteSpace(reason) ? "Cancelled by the delegating agent." : reason,
            ct).ConfigureAwait(false);
        var response = new JsonObject
        {
            ["task_id"] = taskId,
            ["status"] = "cancelled",
            ["cancelled_count"] = cancelResult.CancelledCount,
        };
        if (cancelResult.UnstoppedLegacyRemoteHosts.Count > 0)
        {
            var hosts = string.Join(", ", cancelResult.UnstoppedLegacyRemoteHosts);
            response["host_interrupt_skipped"] = true;
            response["note"] = $"Cancelled locally; {hosts} does not support host-checked task cancellation, so no unscoped stop was sent and the remote session may still be running there. Stop it directly on that host if needed.";
        }
        return ToolOk(response);
    }

    private string? ReadActiveDeliveryId(string itemId) =>
        _sessions.Find(itemId) is { } bridge
        && bridge.TryGetActiveCollabTurn(out _, out _, out var deliveryId)
            ? deliveryId
            : null;

    public bool TryAcceptCollabDelivery(
        string itemId,
        string? threadId,
        string? taskId,
        bool expectReply,
        string? deliveryId,
        Func<bool>? shouldCommit = null) =>
        TryAcceptCollabDelivery(itemId, threadId, taskId, expectReply, deliveryId, shouldCommit, legacyIngressToken: 0);

    internal bool TryAcceptCollabDelivery(
        string itemId,
        string? threadId,
        string? taskId,
        bool expectReply,
        string? deliveryId,
        Func<bool>? shouldCommit,
        long legacyIngressToken)
    {
        lock (_ledger.Gate)
        {
            InFlightDelivery? observedByGuard = null;
            if (shouldCommit is not null && !InvokeGuardWithObservation(shouldCommit, out observedByGuard))
            {
                return false;
            }
            if (expectReply)
            {
                var pending = _ledger.FindPendingForDelivery(itemId, threadId, taskId, deliveryId);
                if (pending is not null)
                {
                    if (!pending.Accepted && IsStaleUnacceptedDeliveryLocked(itemId, pending))
                    {
                        return false;
                    }
                    AcceptPendingDeliveryLocked(itemId, pending);
                }
                else if (string.IsNullOrWhiteSpace(deliveryId) && !string.IsNullOrWhiteSpace(threadId))
                {
                    AssignTaskOwnershipLocked(itemId, threadId, taskId, deliveryId: null, reopenCompleted: true);
                }
            }
            if (!string.IsNullOrWhiteSpace(deliveryId))
            {
                _ledger.MarkWakeupManagedLifetime(itemId, deliveryId);
            }
            if (FindInFlightDeliveryLocked(itemId, threadId, taskId, expectReply, deliveryId, shouldCommit, observedByGuard, legacyIngressToken, forDispatch: false) is { } inFlight)
            {
                inFlight.Accepted = true;
                if (observedByGuard is not null || shouldCommit is not null || !string.IsNullOrWhiteSpace(deliveryId))
                {
                    inFlight.AcceptedWithGuardOrId = true;
                }
            }
            return true;
        }
    }

    private bool IsCallerTerminalGenerationRetired(string? callerItemId, object? callerTerminalSession, string? callerTerminalLinkId)
    {
        if (string.IsNullOrWhiteSpace(callerItemId) || callerTerminalSession is null || _terminalSessions is null)
        {
            return false;
        }
        var currentSession = _terminalSessions.Find(callerItemId);
        if (!ReferenceEquals(currentSession, callerTerminalSession))
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(callerTerminalLinkId)
            && !string.Equals(currentSession?.CurrentLinkId, callerTerminalLinkId, StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }

    private bool IsCallerTerminalGenerationRetiredLocked(PendingExpectation pending) =>
        IsCallerTerminalGenerationRetired(pending.CallerItemId, pending.CallerTerminalSession, pending.CallerTerminalLinkId);

    private bool IsSupersededBeforeAcceptanceLocked(string targetItemId, PendingExpectation pending)
    {
        if (pending.ExpectedInitialTaskOwnershipEpoch is not null
            && !string.IsNullOrWhiteSpace(pending.TaskId))
        {
            if (!_ledger.Tasks.TryGetValue(pending.TaskId, out var task)
                || task.IsFinished
                || (_ledger.GetTaskOwnerDelivery(pending.TaskId) is { Length: > 0 } currentOwner
                    && !string.Equals(currentOwner, pending.DeliveryId, StringComparison.Ordinal)))
            {
                return true;
            }
        }
        if (IsCallerTerminalGenerationRetiredLocked(pending)
            && !string.IsNullOrWhiteSpace(pending.ThreadId)
            && (_ledger.GetLatestAcceptedDeliverySeq(targetItemId, pending.ThreadId) > pending.DispatchedAcceptedSeq
                || (_ledger.Threads.TryGetValue(pending.ThreadId, out var cancelledThread)
                    && cancelledThread.Status == CollabThreadStatus.Cancelled)))
        {
            return true;
        }
        return false;
    }

    private bool IsStaleUnacceptedDeliveryLocked(string targetItemId, PendingExpectation pending) =>
        IsSupersededBeforeAcceptanceLocked(targetItemId, pending)
        || IsCallerTerminalGenerationRetiredLocked(pending);

    private bool AcceptPendingDeliveryLocked(string targetItemId, PendingExpectation pending, bool markDispatched = false)
    {
        if (pending.Accepted)
        {
            if (markDispatched)
            {
                _ledger.RecordDispatchedDelivery(targetItemId, pending.ThreadId, pending.AcceptedSeq);
            }
            return true;
        }
        pending.Accepted = true;
        if (IsStaleUnacceptedDeliveryLocked(targetItemId, pending))
        {
            return false;
        }
        pending.AcceptedSeq = _ledger.RecordAcceptedDelivery(targetItemId, pending.ThreadId, markDispatched);
        if (!string.IsNullOrWhiteSpace(pending.ThreadId))
        {
            pending.AcceptedThreadRevision = _ledger.GetThreadTurnRevision(pending.ThreadId);
        }
        AssignTaskOwnershipLocked(
            targetItemId,
            pending.ThreadId,
            pending.TaskId,
            pending.DeliveryId,
            reopenCompleted: pending.ReopenCompletedTask && !pending.SettledExplicitly,
            transferCallerTerminalOwnership: pending.TransferCallerTerminalOwnership,
            callerItemId: pending.CallerItemId,
            callerTerminalLinkId: pending.CallerTerminalLinkId,
            callerTerminalSession: pending.CallerTerminalSession);
        return true;
    }

    private void AssignTaskOwnershipLocked(
        string targetItemId,
        string? threadId,
        string? taskId,
        string? deliveryId,
        bool reopenCompleted,
        bool transferCallerTerminalOwnership = false,
        string? callerItemId = null,
        string? callerTerminalLinkId = null,
        object? callerTerminalSession = null)
    {
        void ClaimTask(CollabTaskRecord task)
        {
            var canReopenTerminalRetirement = reopenCompleted
                && task.Status == CollabTaskStatus.Cancelled
                && transferCallerTerminalOwnership
                && string.Equals(task.CallerItemId, callerItemId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(callerTerminalLinkId)
                && !IsCallerTerminalGenerationRetired(task.CallerItemId, callerTerminalSession, callerTerminalLinkId)
                && _ledger.CanReopenFromCallerTerminalRetirement(task.TaskId, callerTerminalLinkId);
            if ((reopenCompleted && task.Status == CollabTaskStatus.Completed) || canReopenTerminalRetirement)
            {
                _ledger.SetTaskStatus(task, CollabTaskStatus.Running, "Follow-up sent");
            }
            if (!task.IsFinished)
            {
                _ledger.SetTaskOwnerDelivery(task.TaskId, deliveryId);
                if (transferCallerTerminalOwnership)
                {
                    if (string.Equals(task.CallerItemId, callerItemId, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(callerTerminalLinkId))
                    {
                        _ledger.SetCallerTerminalLink(task.TaskId, callerTerminalLinkId);
                    }
                    else
                    {
                        _ledger.RemoveCallerTerminalLink(task.TaskId);
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(taskId)
            && _ledger.Tasks.TryGetValue(taskId, out var directTask))
        {
            ClaimTask(directTask);
        }
        if (!string.IsNullOrWhiteSpace(threadId))
        {
            foreach (var task in _ledger.Tasks.Values)
            {
                if (task.ThreadId == threadId
                    && task.TargetItemId == targetItemId
                    && !string.Equals(task.TaskId, taskId, StringComparison.Ordinal))
                {
                    ClaimTask(task);
                }
            }
        }
    }

    private bool IsPendingSupersededLocked(string targetItemId, PendingExpectation pending)
    {
        if (pending.Accepted
            && !string.IsNullOrWhiteSpace(pending.ThreadId)
            && (_ledger.HasNewerDispatchedDelivery(targetItemId, pending.ThreadId, pending.AcceptedSeq)
                || _ledger.HasNewerAcceptedPendingOnDifferentBridge(targetItemId, pending.ThreadId, pending)))
        {
            return true;
        }
        if (pending.ThreadId is { } tid && _ledger.Threads.TryGetValue(tid, out var thread))
        {
            if (thread.Status is CollabThreadStatus.Cancelled or CollabThreadStatus.Interrupted)
            {
                return true;
            }
            if (pending.AcceptedThreadRevision is { } acceptedRev
                && _ledger.GetThreadTurnRevision(tid) != acceptedRev)
            {
                return true;
            }
        }
        var owningTaskId = pending.TaskId ?? ResolvePendingTaskIdLocked(targetItemId, pending);
        if (!string.IsNullOrWhiteSpace(owningTaskId))
        {
            if (!_ledger.Tasks.TryGetValue(owningTaskId, out var task) || task.IsFinished)
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(pending.DeliveryId)
                && _ledger.GetTaskOwnerDelivery(owningTaskId) is { Length: > 0 } currentOwner
                && !string.Equals(currentOwner, pending.DeliveryId, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public bool TryClaimCollabDelivery(
        string itemId,
        string? threadId,
        string? taskId,
        string? deliveryId,
        Func<bool>? shouldDispatch = null) =>
        TryClaimCollabDelivery(itemId, threadId, taskId, deliveryId, shouldDispatch, legacyIngressToken: 0);

    internal bool TryClaimCollabDelivery(
        string itemId,
        string? threadId,
        string? taskId,
        string? deliveryId,
        Func<bool>? shouldDispatch,
        long legacyIngressToken)
    {
        lock (_ledger.Gate)
        {
            if (!string.IsNullOrWhiteSpace(deliveryId) && _ledger.IsDeliveryDispatched(itemId, deliveryId))
            {
                var hasPending = _ledger.HasActivePendingDelivery(itemId, deliveryId, threadId, taskId);
                return !IsDeliveryCancelledLocked(threadId, taskId, expectReply: hasPending);
            }
            InFlightDelivery? observedByGuard = null;
            if (shouldDispatch is not null && !InvokeGuardWithObservation(shouldDispatch, out observedByGuard))
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(taskId)
                && _ledger.Tasks.TryGetValue(taskId, out var claimTask)
                && claimTask.Status == CollabTaskStatus.Cancelled)
            {
                return false;
            }
            var inFlight = FindInFlightDeliveryLocked(itemId, threadId, taskId, expectReply: null, deliveryId, shouldDispatch, observedByGuard, legacyIngressToken, forDispatch: true);
            var effectiveDeliveryId = !string.IsNullOrWhiteSpace(deliveryId)
                ? deliveryId
                : inFlight?.DeliveryId
                    ?? (legacyIngressToken > 0
                        ? _ledger.FindPendingDeliveryIdByIngressToken(itemId, legacyIngressToken)
                        : null);
            if (!string.IsNullOrWhiteSpace(effectiveDeliveryId))
            {
                if (_ledger.FindPendingForDelivery(itemId, threadId, taskId, effectiveDeliveryId) is { Accepted: false } pending
                    && !AcceptPendingDeliveryLocked(itemId, pending))
                {
                    return false;
                }
                _ledger.MarkDeliveryDispatched(itemId, effectiveDeliveryId);
            }
            if (inFlight is not null)
            {
                inFlight.Accepted = true;
                inFlight.Dispatched = true;
            }
            return true;
        }
    }

    public void ReleaseCollabDelivery(string itemId, string? deliveryId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId)) return;
        lock (_ledger.Gate)
        {
            _ledger.RemoveActiveWakeupByDeliveryId(itemId, deliveryId);
            UntrackBridgeDelivery(itemId, deliveryId);
        }
    }

    private void CancelStaleWakeups(
        string callerItemId,
        IReadOnlyList<(string ThreadId, string DeliveryId)>? consumedWakeups)
    {
        if (consumedWakeups is not { Count: > 0 }) return;
        if (_sessions.Find(callerItemId) is not { } gui) return;
        foreach (var (threadId, deliveryId) in consumedWakeups)
        {
            gui.CancelQueuedCollabPrompts(threadId: threadId, taskId: null, deliveryId: deliveryId);
        }
    }

    private (Dictionary<string, long> ObservedThreadSeqs, Dictionary<string, long> ObservedTaskSeqs) CapturePeerWakeupCeiling(
        string callerItemId,
        string peerItemId)
    {
        Dictionary<string, long> observedThreadSeqs = new(StringComparer.Ordinal);
        Dictionary<string, long> observedTaskSeqs = new(StringComparer.Ordinal);
        lock (_ledger.Gate)
        {
            foreach (var t in _ledger.Tasks.Values)
            {
                if (t.CallerItemId == callerItemId
                    && t.TargetItemId == peerItemId
                    && t.Status is CollabTaskStatus.Completed or CollabTaskStatus.Failed or CollabTaskStatus.Cancelled)
                {
                    observedTaskSeqs[t.TaskId] = t.UpdatedSeq;
                }
            }
            foreach (var th in _ledger.Threads.Values)
            {
                if (th.ParticipantItemIds.Contains(callerItemId, StringComparer.Ordinal)
                    && th.ParticipantItemIds.Contains(peerItemId, StringComparer.Ordinal)
                    && th.Messages.LastOrDefault()?.FromItemId == peerItemId)
                {
                    observedThreadSeqs[th.ThreadId] = th.UpdatedSeq;
                }
            }
        }
        return (observedThreadSeqs, observedTaskSeqs);
    }

    private void CancelStaleWakeupsForPeer(
        string callerItemId,
        IReadOnlyDictionary<string, long> observedThreadSeqs,
        IReadOnlyDictionary<string, long> observedTaskSeqs)
    {
        if (observedThreadSeqs.Count == 0 && observedTaskSeqs.Count == 0) return;
        List<(string ThreadId, string DeliveryId)> consumedWakeups;
        var activeDeliveryId = ReadActiveDeliveryId(callerItemId);
        lock (_ledger.Gate)
        {
            consumedWakeups = _ledger.ConsumeWakeupsAtOrBelow(callerItemId, observedThreadSeqs, observedTaskSeqs, activeDeliveryId);
        }
        CancelStaleWakeups(callerItemId, consumedWakeups);
    }

    /* ==================== Routing, Wakeup & Terminal Bridge ==================== */

    private async Task<(bool Succeeded, CollabThreadRecord? Thread, string? Error)> SendMessageInternalAsync(
        string callerItemId,
        string targetItemId,
        string message,
        string? threadId,
        string? title,
        bool expectReply,
        int? maxTurnsOverride,
        bool isDirectUserRelay,
        CancellationToken ct,
        CollabTaskRecord? delegatedTask = null)
    {
        if (callerItemId == targetItemId)
        {
            return (false, null, "Cannot send a collaboration message to yourself.");
        }
        if (!IsPeerVisible(callerItemId, targetItemId))
        {
            return (false, null, $"Target session '{targetItemId}' is not visible in this project/group.");
        }

        var callerItem = _index.FindItem(callerItemId);
        var targetItem = _index.FindItem(targetItemId);
        if (callerItem is null || targetItem is null)
        {
            return (false, null, "Source or target session no longer exists.");
        }

        var callerLabel = isDirectUserRelay ? $"User (via {FormatCallerLabel(callerItem)})" : FormatCallerLabel(callerItem);
        var targetLabel = FormatCallerLabel(targetItem);
        var defaultMaxHops = _policy.DefaultMaxHops;

        CollabThreadRecord thread;
        int round;
        var createdNewThread = false;
        var previousThreadStatus = CollabThreadStatus.Active;
        var previousThreadHops = 0;
        string? appendedMessageId = null;
        long initialThreadSeq = 0;
        var initialTaskOwnershipEpoch = 0;
        var taskId = delegatedTask?.TaskId;
        var callerTerminalSession = _terminalSessions?.Find(callerItemId);
        var callerTerminalLinkId = callerTerminalSession?.CurrentLinkId;
        var callerTurn = delegatedTask is not null ? ReadCallerActiveTurn(callerItemId) : default;
        var budgetPaused = false;
        lock (_ledger.Gate)
        {
            if (!string.IsNullOrWhiteSpace(threadId) && _ledger.Threads.TryGetValue(threadId, out var existing))
            {
                thread = existing;
                _ledger.AddThreadParticipant(thread, callerItemId);
                _ledger.AddThreadParticipant(thread, targetItemId);
            }
            else
            {
                var newId = string.IsNullOrWhiteSpace(threadId) ? _ledger.NextId("t") : threadId;
                thread = new CollabThreadRecord
                {
                    ThreadId = newId,
                    Title = string.IsNullOrWhiteSpace(title) ? TruncateTitle(message, 36) : title.Trim(),
                    InitiatorItemId = callerItemId,
                    ParticipantItemIds = [callerItemId, targetItemId],
                    MaxHops = maxTurnsOverride is { } m
                        ? Math.Clamp(m, CollabSettings.MinNegotiationHops, CollabSettings.MaxAllowedNegotiationHops)
                        : defaultMaxHops,
                };
                _ledger.Add(thread);
                createdNewThread = true;
            }

            if (thread.Hops >= thread.MaxHops)
            {
                _ledger.SetThreadStatus(thread, CollabThreadStatus.PausedBudget);
                budgetPaused = true;
                round = thread.Hops;
            }
            else
            {
                previousThreadStatus = thread.Status;
                previousThreadHops = thread.Hops;
                thread.Hops++;
                round = thread.Hops;
                if (expectReply || !thread.IsClosed)
                {
                    thread.Status = CollabThreadStatus.Active;
                }
                appendedMessageId = _ledger.NextId("msg");
                _ledger.AppendMessage(thread, new CollabMessageRecord
                {
                    MessageId = appendedMessageId,
                    ThreadId = thread.ThreadId,
                    FromItemId = isDirectUserRelay ? "user" : callerItemId,
                    FromLabel = callerLabel,
                    FromAgentId = callerItem.AgentId,
                    ToItemId = targetItemId,
                    ToLabel = targetLabel,
                    Round = round,
                    Text = message,
                    IsUserInterjection = isDirectUserRelay,
                });
                initialThreadSeq = thread.UpdatedSeq;

                if (expectReply && taskId is null)
                {
                    foreach (var task in _ledger.Tasks.Values)
                    {
                        if (task.ThreadId == thread.ThreadId && task.TargetItemId == targetItemId && task.Status == CollabTaskStatus.Completed)
                        {
                            taskId = task.TaskId;
                            break;
                        }
                    }
                }

                if (delegatedTask is not null)
                {
                    _ledger.Add(delegatedTask);
                    initialTaskOwnershipEpoch = _ledger.GetTaskOwnershipEpoch(delegatedTask.TaskId);
                    RecordDelegatedChildOwnershipLocked(callerItemId, callerTurn, delegatedTask.TaskId, recordCallerTerminalLink: false);
                }
            }
        }
        if (budgetPaused)
        {
            RaiseChanged(callerItemId);
            return (false, thread, $"Negotiation thread '{thread.ThreadId}' reached its hop budget ({thread.Hops}/{thread.MaxHops}). Ask the user to review or extend the thread.");
        }

        var targetIsRemote = _index.FindProjectOf(targetItemId) is { } tp && ProjectLocation.IsOnRemoteHost(tp);
        var envelope = CollabEnvelope.Message(thread.ThreadId, callerLabel, round, thread.MaxHops, message, expectReply, targetIsRemote);
        bool delivered;
        string? acceptedDeliveryId;
        try
        {
            (delivered, acceptedDeliveryId) = await DeliverEnvelopeCoreAsync(
                targetItemId,
                envelope,
                thread.ThreadId,
                taskId,
                expectReply,
                callerItemId,
                ct,
                reopenCompletedTask: expectReply && delegatedTask is null,
                callerTerminalLinkId: expectReply ? callerTerminalLinkId : null,
                transferCallerTerminalOwnership: expectReply,
                expectedInitialTaskOwnershipEpoch: delegatedTask is not null ? initialTaskOwnershipEpoch : null,
                callerTerminalSession: expectReply ? callerTerminalSession : null).ConfigureAwait(false);
        }
        catch
        {
            if (delegatedTask is not null)
            {
                RollbackUndeliveredTask(delegatedTask, thread, createdNewThread, initialTaskOwnershipEpoch, initialThreadSeq, callerItemId, targetItemId);
            }
            else if (!createdNewThread && previousThreadStatus == CollabThreadStatus.Cancelled)
            {
                RollbackUndeliveredCancelledThreadReopen(thread, previousThreadStatus, previousThreadHops, appendedMessageId, initialThreadSeq, callerItemId, targetItemId);
            }
            throw;
        }
        if (!delivered)
        {
            if (delegatedTask is not null)
            {
                RollbackUndeliveredTask(delegatedTask, thread, createdNewThread, initialTaskOwnershipEpoch, initialThreadSeq, callerItemId, targetItemId);
                bool cancelledBeforeDelivery;
                lock (_ledger.Gate)
                {
                    cancelledBeforeDelivery = delegatedTask.Status == CollabTaskStatus.Cancelled;
                }
                if (cancelledBeforeDelivery)
                {
                    return (false, thread, $"Task '{delegatedTask.TaskId}' was cancelled before delivery.");
                }
            }
            else if (!createdNewThread && previousThreadStatus == CollabThreadStatus.Cancelled)
            {
                RollbackUndeliveredCancelledThreadReopen(thread, previousThreadStatus, previousThreadHops, appendedMessageId, initialThreadSeq, callerItemId, targetItemId);
            }
            return (false, thread, $"Could not deliver message to '{targetLabel}' (session not running).");
        }

        if (expectReply
            && !string.IsNullOrWhiteSpace(callerTerminalLinkId)
            && IsCallerTerminalGenerationRetired(callerItemId, callerTerminalSession, callerTerminalLinkId))
        {
            List<string>? retiredOutbound = null;
            lock (_ledger.Gate)
            {
                foreach (var task in _ledger.Tasks.Values)
                {
                    if (task.IsFinished
                        || !string.Equals(task.CallerItemId, callerItemId, StringComparison.Ordinal)
                        || !string.Equals(task.ThreadId, thread.ThreadId, StringComparison.Ordinal)
                        || !string.Equals(task.TargetItemId, targetItemId, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var ownedByRetiredCall =
                        (string.Equals(_ledger.GetCallerTerminalLink(task.TaskId), callerTerminalLinkId, StringComparison.Ordinal)
                            && (string.IsNullOrWhiteSpace(acceptedDeliveryId)
                                || string.Equals(_ledger.GetTaskOwnerDelivery(task.TaskId), acceptedDeliveryId, StringComparison.Ordinal)))
                        || (delegatedTask is not null
                            && string.Equals(task.TaskId, delegatedTask.TaskId, StringComparison.Ordinal)
                            && _ledger.GetTaskOwnerDelivery(task.TaskId) is null);
                    if (ownedByRetiredCall)
                    {
                        (retiredOutbound ??= []).Add(task.TaskId);
                    }
                }
            }
            if (retiredOutbound is { Count: > 0 })
            {
                foreach (var outboundTaskId in retiredOutbound)
                {
                    CancelTaskTree(
                        outboundTaskId,
                        "The delegating session was closed.",
                        CancellationToken.None,
                        cancelledByCallerTerminalRetirement: true,
                        retiredCallerLinkId: callerTerminalLinkId);
                }
            }
        }

        _ledger.SignalWaiters();
        RaiseChanged(callerItemId);
        RaiseChanged(targetItemId);
        return (true, thread, null);
    }

    private void RollbackUndeliveredCancelledThreadReopen(
        CollabThreadRecord thread,
        CollabThreadStatus previousStatus,
        int previousHops,
        string? appendedMessageId,
        long initialThreadSeq,
        string callerItemId,
        string targetItemId)
    {
        var rolledBack = false;
        lock (_ledger.Gate)
        {
            if (thread.UpdatedSeq == initialThreadSeq && thread.Status == CollabThreadStatus.Active)
            {
                thread.Status = previousStatus;
                thread.Hops = previousHops;
                if (!string.IsNullOrWhiteSpace(appendedMessageId)
                    && thread.Messages.Count > 0
                    && string.Equals(thread.Messages[^1].MessageId, appendedMessageId, StringComparison.Ordinal))
                {
                    thread.Messages.RemoveAt(thread.Messages.Count - 1);
                }
                _ledger.Touch(thread);
                rolledBack = true;
            }
        }
        if (rolledBack)
        {
            _ledger.SignalWaiters();
            RaiseChanged(callerItemId);
            RaiseChanged(targetItemId);
        }
    }

    private void RollbackUndeliveredTask(
        CollabTaskRecord delegatedTask,
        CollabThreadRecord thread,
        bool createdNewThread,
        int initialTaskOwnershipEpoch,
        long initialThreadSeq,
        string callerItemId,
        string targetItemId)
    {
        var rolledBack = false;
        lock (_ledger.Gate)
        {
            if (_ledger.GetTaskOwnershipEpoch(delegatedTask.TaskId) == initialTaskOwnershipEpoch
                && !delegatedTask.IsFinished
                && !_ledger.HasUnfinishedChildTask(delegatedTask.TaskId))
            {
                rolledBack = _ledger.RemoveTask(delegatedTask.TaskId);
                if (createdNewThread
                    && thread.UpdatedSeq == initialThreadSeq
                    && !thread.IsClosed
                    && thread.Messages.Count <= 1)
                {
                    _ledger.RemoveThread(thread.ThreadId);
                }
            }
        }
        if (rolledBack)
        {
            _ledger.SignalWaiters();
            RaiseChanged(callerItemId);
            RaiseChanged(targetItemId);
        }
    }

    private enum ReplyDeliveryOutcome
    {
        Delivered,
        Awaited,
        Consumed,
        WakeDisabled,
        NoRecipient,
        Failed,
    }

    private enum ReplyDeliveryFailureReason
    {
        None,
        NotAccepted,
        DeliveryError,
    }

    private sealed record ReplyRecordResult(
        bool Recorded,
        int Round,
        string? RecipientItemId,
        ReplyDeliveryOutcome Delivery,
        string? Error = null,
        ReplyDeliveryFailureReason FailureReason = ReplyDeliveryFailureReason.None);

    private async Task<ReplyRecordResult> RecordReplyAndWakeAsync(
        string responderItemId,
        string threadId,
        string message,
        string status,
        bool isImplicitFallback,
        CancellationToken ct,
        long? expectedThreadRevision = null,
        string? expectedTaskId = null,
        int? expectedTaskOwnershipEpoch = null,
        string? expectedDeliveryId = null,
        long? expectedAcceptedSeq = null,
        string? expectedCallerItemId = null,
        object? expectedCallerTerminalSession = null,
        string? expectedCallerTerminalLinkId = null)
    {
        var responderItem = _index.FindItem(responderItemId);
        var responderLabel = responderItem is not null ? FormatCallerLabel(responderItem) : responderItemId;
        string? recipientId = null;
        int round;
        int maxHops;
        string threadTitle;

        long replySeq;
        List<(string TaskId, long Seq)> matchingTasks = [];
        List<string> matchingTaskIds = [];
        var activeDeliveryId = !isImplicitFallback ? ReadActiveDeliveryId(responderItemId) : null;
        string? retiredCallerCancelTaskId = null;
        string? retiredCallerCancelLinkId = null;
        var callerRetiredBeforeReply = false;
        var replyCallerItemId = expectedCallerItemId;
        var replyCallerTerminalSession = expectedCallerTerminalSession;
        var replyCallerTerminalLinkId = expectedCallerTerminalLinkId;
        lock (_ledger.Gate)
        {
            if (!_ledger.Threads.TryGetValue(threadId, out var thread))
            {
                return new ReplyRecordResult(false, 0, null, ReplyDeliveryOutcome.NoRecipient, $"Unknown collaboration thread '{threadId}'.");
            }
            if (!thread.ParticipantItemIds.Contains(responderItemId, StringComparer.Ordinal))
            {
                return new ReplyRecordResult(false, 0, null, ReplyDeliveryOutcome.NoRecipient, $"You are not a participant of thread '{threadId}'.");
            }
            if (isImplicitFallback)
            {
                if (thread.Status is CollabThreadStatus.Cancelled or CollabThreadStatus.Interrupted)
                {
                    return new ReplyRecordResult(false, thread.Hops, null, ReplyDeliveryOutcome.NoRecipient, $"Collaboration thread '{threadId}' closed before implicit reply.");
                }
                if (expectedThreadRevision is { } expectedRev && _ledger.GetThreadTurnRevision(threadId) != expectedRev)
                {
                    return new ReplyRecordResult(false, thread.Hops, null, ReplyDeliveryOutcome.NoRecipient, $"Collaboration thread '{threadId}' advanced before implicit reply.");
                }
                if (expectedAcceptedSeq is { } acceptedSeq
                    && _ledger.HasNewerAcceptedDelivery(responderItemId, threadId, acceptedSeq))
                {
                    return new ReplyRecordResult(false, thread.Hops, null, ReplyDeliveryOutcome.NoRecipient, $"Collaboration thread '{threadId}' accepted a newer turn before implicit reply.");
                }
                if (!string.IsNullOrWhiteSpace(expectedTaskId)
                    && (!_ledger.Tasks.TryGetValue(expectedTaskId, out var expectedTask)
                        || expectedTask.IsFinished
                        || (expectedTaskOwnershipEpoch is { } expectedEpoch && _ledger.GetTaskOwnershipEpoch(expectedTaskId) != expectedEpoch)
                        || (!string.IsNullOrWhiteSpace(expectedDeliveryId)
                            && _ledger.GetTaskOwnerDelivery(expectedTaskId) is { Length: > 0 } currentOwner
                            && !string.Equals(currentOwner, expectedDeliveryId, StringComparison.Ordinal))))
                {
                    return new ReplyRecordResult(false, thread.Hops, null, ReplyDeliveryOutcome.NoRecipient, $"Collaboration task '{expectedTaskId}' already settled before implicit reply.");
                }
                if (IsCallerTerminalGenerationRetired(expectedCallerItemId, expectedCallerTerminalSession, expectedCallerTerminalLinkId))
                {
                    callerRetiredBeforeReply = true;
                    retiredCallerCancelTaskId = expectedTaskId;
                    retiredCallerCancelLinkId = expectedCallerTerminalLinkId;
                }
            }
            // Clear pending implicit expectation so an explicit reply_thread won't fire again at end_turn.
            // Accepting before explicit settlement ensures an early reply inside a follow-up delivery reopens and
            // updates the original task before the delivering call returns.
            if (!isImplicitFallback)
            {
                var explicitPending = _ledger.FindPendingForExplicitSettlement(responderItemId, threadId, activeDeliveryId);
                while (explicitPending is not null
                    && IsCallerTerminalGenerationRetiredLocked(explicitPending)
                    && (IsSupersededBeforeAcceptanceLocked(responderItemId, explicitPending)
                        || _ledger.HasNewerAcceptedDelivery(responderItemId, threadId, explicitPending)))
                {
                    explicitPending.SettledExplicitly = true;
                    _ledger.RemovePending(responderItemId, explicitPending);
                    explicitPending = _ledger.FindPendingForExplicitSettlement(responderItemId, threadId, activeDeliveryId);
                }
                if (explicitPending is not null)
                {
                    if (IsCallerTerminalGenerationRetiredLocked(explicitPending))
                    {
                        explicitPending.SettledExplicitly = true;
                        _ledger.RemovePending(responderItemId, explicitPending);
                        callerRetiredBeforeReply = true;
                        retiredCallerCancelTaskId = explicitPending.TaskId ?? ResolvePendingTaskIdLocked(responderItemId, explicitPending);
                        retiredCallerCancelLinkId = explicitPending.CallerTerminalLinkId;
                    }
                    else
                    {
                        if (!explicitPending.Accepted)
                        {
                            AcceptPendingDeliveryLocked(responderItemId, explicitPending);
                        }
                        explicitPending.SettledExplicitly = true;
                        replyCallerItemId = explicitPending.CallerItemId;
                        replyCallerTerminalSession = explicitPending.CallerTerminalSession;
                        replyCallerTerminalLinkId = explicitPending.CallerTerminalLinkId;
                    }
                }
                if (!callerRetiredBeforeReply)
                {
                    _ledger.MarkThreadTurnSuperseded(threadId);
                }
            }

            if (callerRetiredBeforeReply)
            {
                round = thread.Hops;
                maxHops = thread.MaxHops;
                threadTitle = thread.Title;
                replySeq = thread.UpdatedSeq;
            }
            else
            {
                thread.Status = status switch
                {
                    "continue" when thread.Hops >= thread.MaxHops => CollabThreadStatus.PausedBudget,
                    "agreed" => CollabThreadStatus.Agreed,
                    "blocked" => CollabThreadStatus.Blocked,
                    _ => CollabThreadStatus.Active,
                };

                thread.Hops++;
                round = thread.Hops;
                maxHops = thread.MaxHops;
                threadTitle = thread.Title;
                recipientId = ResolveReplyRecipientLocked(thread, responderItemId);

                _ledger.AppendMessage(thread, new CollabMessageRecord
                {
                    MessageId = _ledger.NextId("msg"),
                    ThreadId = threadId,
                    FromItemId = responderItemId,
                    FromLabel = responderLabel,
                    FromAgentId = responderItem?.AgentId,
                    ToItemId = recipientId,
                    ToLabel = recipientId is not null && _index.FindItem(recipientId) is { } rec ? FormatCallerLabel(rec) : null,
                    Round = round,
                    Text = TruncateBody(message, _policy.MaxReplyChars),
                    Status = status,
                });
                replySeq = thread.UpdatedSeq;

                foreach (var task in _ledger.Tasks.Values)
                {
                    if (task.ThreadId == threadId && task.TargetItemId == responderItemId && !task.IsFinished)
                    {
                        if (isImplicitFallback
                            && !string.IsNullOrWhiteSpace(expectedTaskId)
                            && !string.Equals(task.TaskId, expectedTaskId, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        _ledger.SetTaskStatus(
                            task,
                            status == "blocked" ? CollabTaskStatus.Failed : CollabTaskStatus.Completed,
                            isImplicitFallback ? "Turn ended (reported automatically)" : status == "agreed" ? "Agreed & completed" : "Replied to parent",
                            TruncateBody(message, _policy.MaxReplyChars));
                        matchingTasks.Add((task.TaskId, task.UpdatedSeq));
                        matchingTaskIds.Add(task.TaskId);
                    }
                }
            }
        }
        if (callerRetiredBeforeReply)
        {
            if (!string.IsNullOrWhiteSpace(retiredCallerCancelTaskId))
            {
                CancelTaskTree(
                    retiredCallerCancelTaskId,
                    "The delegating session was closed.",
                    CancellationToken.None,
                    cancelledByCallerTerminalRetirement: true,
                    retiredCallerLinkId: retiredCallerCancelLinkId);
            }
            return new ReplyRecordResult(false, round, null, ReplyDeliveryOutcome.NoRecipient, "The delegating session was closed before the reply was recorded.");
        }

        // Only suppress wakeup and report await_events_signaled when a matching uncancelled waiter was actually signaled.
        var signaledWaiters = _ledger.SignalWaiters();
        var callerSignaled = recipientId is not null
            && signaledWaiters.Any(w => w.CallerItemId == recipientId && w.Matches(threadId, matchingTaskIds));
        RaiseChanged(responderItemId);
        if (recipientId is not null) RaiseChanged(recipientId);

        ReplyDeliveryOutcome delivery;
        var failureReason = ReplyDeliveryFailureReason.None;
        if (recipientId is null)
        {
            delivery = ReplyDeliveryOutcome.NoRecipient;
        }
        else if (callerSignaled)
        {
            delivery = ReplyDeliveryOutcome.Awaited;
        }
        else if (IsReplyConsumed(recipientId, threadId, replySeq, matchingTasks))
        {
            delivery = ReplyDeliveryOutcome.Consumed;
        }
        else if (!_policy.WakeCallerOnReply)
        {
            delivery = ReplyDeliveryOutcome.WakeDisabled;
        }
        else
        {
            var wakeup = CollabEnvelope.Reply(
                threadId, threadTitle, responderLabel, round, maxHops, status, TruncateBody(message, _policy.MaxReplyChars),
                autoReportedFromSessionId: isImplicitFallback ? responderItemId : null);
            var shouldExpectFollowUp = status == "continue" && round < maxHops;
            try
            {
                var guardTerminalRecipient = string.Equals(recipientId, replyCallerItemId, StringComparison.Ordinal)
                    && replyCallerTerminalSession is not null;
                var delivered = await DeliverEnvelopeAsync(
                    recipientId,
                    wakeup,
                    threadId,
                    taskId: null,
                    expectReply: shouldExpectFollowUp,
                    callerItemId: responderItemId,
                    ct,
                    shouldCommit: () =>
                        !IsReplyConsumed(recipientId, threadId, replySeq, matchingTasks)
                        && (!guardTerminalRecipient
                            || !IsCallerTerminalGenerationRetired(recipientId, replyCallerTerminalSession, replyCallerTerminalLinkId)),
                    wakeupSeq: replySeq,
                    wakeupTasks: matchingTasks).ConfigureAwait(false);
                if (delivered)
                {
                    delivery = ReplyDeliveryOutcome.Delivered;
                }
                else if (IsReplyConsumed(recipientId, threadId, replySeq, matchingTasks))
                {
                    delivery = ReplyDeliveryOutcome.Consumed;
                }
                else
                {
                    delivery = ReplyDeliveryOutcome.Failed;
                    failureReason = ReplyDeliveryFailureReason.NotAccepted;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                delivery = ReplyDeliveryOutcome.Failed;
                failureReason = ReplyDeliveryFailureReason.DeliveryError;
            }
        }

        return new ReplyRecordResult(true, round, recipientId, delivery, FailureReason: failureReason);
    }

    public void NotifyTurnCompleted(string itemId, string? finalAssistantText) =>
        NotifyTurnCompletedCore(itemId, finalAssistantText, threadId: null, taskId: null, deliveryId: null, hasTurnIdentity: false);

    public void NotifyTurnCompleted(string itemId, string? finalAssistantText, string? threadId, string? taskId) =>
        NotifyTurnCompletedCore(itemId, finalAssistantText, threadId, taskId, deliveryId: null, hasTurnIdentity: true);

    public void NotifyTurnCompleted(string itemId, string? finalAssistantText, string? threadId, string? taskId, string? deliveryId) =>
        NotifyTurnCompletedCore(itemId, finalAssistantText, threadId, taskId, deliveryId, hasTurnIdentity: true);

    public void NotifyTurnDeferred(string itemId, string? threadId, string? taskId, string? deliveryId = null)
    {
        PendingExpectation? pending;
        lock (_ledger.Gate)
        {
            pending = _ledger.MarkPendingDeferred(itemId, threadId, taskId, deliveryId);
            if (pending is not null)
            {
                var owningTaskId = ResolvePendingTaskIdLocked(itemId, pending);
                if (owningTaskId is not null && _ledger.Tasks.TryGetValue(owningTaskId, out var task) && !task.IsFinished)
                {
                    _ledger.SetTaskStatus(task, task.Status, "Waiting for delegated work");
                }
            }
        }
        if (pending is null) return;
        RaiseChanged(pending.CallerItemId);
        RaiseChanged(itemId);
    }

    internal bool HasUnfinishedDelegationsForTurn(string itemId, string? externalTurnKey, string? threadId, string? taskId)
    {
        lock (_ledger.Gate)
        {
            if (!string.IsNullOrWhiteSpace(externalTurnKey) && _ledger.HasUnfinishedExternalTurnChild(externalTurnKey))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(taskId)
                && _ledger.Tasks.TryGetValue(taskId, out var task)
                && task.TargetItemId == itemId
                && !task.IsFinished
                && _ledger.HasUnfinishedChildTask(task.TaskId))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                var byThread = _ledger.Tasks.Values.FirstOrDefault(t =>
                    t.TargetItemId == itemId && t.ThreadId == threadId && !t.IsFinished);
                if (byThread is not null && _ledger.HasUnfinishedChildTask(byThread.TaskId))
                {
                    return true;
                }
            }
            return false;
        }
    }

    private void NotifyTurnCompletedCore(string itemId, string? finalAssistantText, string? threadId, string? taskId, string? deliveryId, bool hasTurnIdentity)
    {
        PendingExpectation? pending;
        var deferredForChildren = false;
        long? expectedThreadRevision = null;
        long? expectedAcceptedSeq = null;
        string? expectedTaskId = null;
        int? expectedTaskOwnershipEpoch = null;
        string? retiredCallerTaskId = null;
        string? retiredCallerLinkId = null;
        lock (_ledger.Gate)
        {
            pending = _ledger.TakePendingForTurn(itemId, threadId, taskId, hasTurnIdentity, deliveryId);
            if (pending is not null)
            {
                UntrackBridgeDelivery(itemId, pending.DeliveryId ?? deliveryId);
                if (IsCallerTerminalGenerationRetiredLocked(pending))
                {
                    if (!IsSupersededBeforeAcceptanceLocked(itemId, pending))
                    {
                        retiredCallerTaskId = pending.TaskId ?? ResolvePendingTaskIdLocked(itemId, pending);
                        retiredCallerLinkId = pending.CallerTerminalLinkId;
                    }
                    pending = null;
                }
                else if (!pending.Accepted && !AcceptPendingDeliveryLocked(itemId, pending))
                {
                    pending = null;
                }
                if (pending is not null && IsPendingSupersededLocked(itemId, pending))
                {
                    pending = null;
                }
            }
            if (pending is not null)
            {
                var owningTaskId = ResolvePendingTaskIdLocked(itemId, pending);
                if (owningTaskId is not null && _ledger.HasUnfinishedChildTask(owningTaskId))
                {
                    deferredForChildren = true;
                    if (_ledger.Tasks.TryGetValue(owningTaskId, out var task) && !task.IsFinished)
                    {
                        _ledger.BumpTaskOwnershipEpoch(owningTaskId);
                        _ledger.SetTaskStatus(task, task.Status, "Waiting for delegated work");
                    }
                }
                else if (pending.ThreadId is { } pendingTid && _ledger.Threads.ContainsKey(pendingTid))
                {
                    expectedThreadRevision = pending.AcceptedThreadRevision ?? _ledger.GetThreadTurnRevision(pendingTid);
                    expectedAcceptedSeq = Math.Max(pending.AcceptedSeq, _ledger.GetLatestAcceptedDeliverySeq(itemId, pendingTid));
                    expectedTaskId = pending.TaskId ?? owningTaskId;
                    if (!string.IsNullOrWhiteSpace(expectedTaskId))
                    {
                        expectedTaskOwnershipEpoch = _ledger.GetTaskOwnershipEpoch(expectedTaskId);
                    }
                }
            }
        }
        if (retiredCallerTaskId is not null)
        {
            CancelTaskTree(
                retiredCallerTaskId,
                "The delegating session was closed.",
                CancellationToken.None,
                cancelledByCallerTerminalRetirement: true,
                retiredCallerLinkId: retiredCallerLinkId);
            return;
        }
        if (pending is null) return;
        if (deferredForChildren)
        {
            RaiseChanged(pending.CallerItemId);
            RaiseChanged(itemId);
            return;
        }

        var summary = string.IsNullOrWhiteSpace(finalAssistantText)
            ? "(Target session finished its turn with no final text output.)"
            : TruncateBody(finalAssistantText.Trim(), _policy.MaxReplyChars);

        if (pending.ThreadId is { } tid)
        {
            _ = Task.Run(() => RecordReplyAndWakeAsync(
                itemId,
                tid,
                summary,
                status: _policy.ImplicitReplyStatus,
                isImplicitFallback: true,
                CancellationToken.None,
                expectedThreadRevision: expectedThreadRevision,
                expectedTaskId: expectedTaskId,
                expectedTaskOwnershipEpoch: expectedTaskOwnershipEpoch,
                expectedDeliveryId: pending.DeliveryId,
                expectedAcceptedSeq: expectedAcceptedSeq,
                expectedCallerItemId: pending.CallerItemId,
                expectedCallerTerminalSession: pending.CallerTerminalSession,
                expectedCallerTerminalLinkId: pending.CallerTerminalLinkId));
            return;
        }

        if (pending.TaskId is { } pendingTaskId)
        {
            lock (_ledger.Gate)
            {
                if (_ledger.Tasks.TryGetValue(pendingTaskId, out var task) && !task.IsFinished)
                {
                    _ledger.SetTaskStatus(task, CollabTaskStatus.Completed, "Completed", summary);
                }
            }
            _ledger.SignalWaiters();
            RaiseChanged(pending.CallerItemId);
            RaiseChanged(itemId);
        }
    }

    private bool IsReplyConsumed(string recipientId, string threadId, long replySeq, IReadOnlyList<(string TaskId, long Seq)> matchingTasks)
    {
        lock (_ledger.Gate)
        {
            return _ledger.IsReplyConsumed(recipientId, threadId, replySeq, matchingTasks);
        }
    }

    private async Task<bool> DeliverEnvelopeAsync(
        string targetItemId,
        string envelope,
        string? threadId,
        string? taskId,
        bool expectReply,
        string callerItemId,
        CancellationToken ct,
        Func<bool>? shouldCommit = null,
        long? wakeupSeq = null,
        IReadOnlyList<(string TaskId, long Seq)>? wakeupTasks = null,
        bool reopenCompletedTask = false) =>
        (await DeliverEnvelopeCoreAsync(
            targetItemId,
            envelope,
            threadId,
            taskId,
            expectReply,
            callerItemId,
            ct,
            shouldCommit,
            wakeupSeq,
            wakeupTasks,
            reopenCompletedTask).ConfigureAwait(false)).Delivered;

    private async Task<(bool Delivered, string? DeliveryId)> DeliverEnvelopeCoreAsync(
        string targetItemId,
        string envelope,
        string? threadId,
        string? taskId,
        bool expectReply,
        string callerItemId,
        CancellationToken ct,
        Func<bool>? shouldCommit = null,
        long? wakeupSeq = null,
        IReadOnlyList<(string TaskId, long Seq)>? wakeupTasks = null,
        bool reopenCompletedTask = false,
        string? callerTerminalLinkId = null,
        bool transferCallerTerminalOwnership = false,
        int? expectedInitialTaskOwnershipEpoch = null,
        object? callerTerminalSession = null)
    {
        if (_index.FindItem(targetItemId) is not { } targetItem || _index.FindProjectOf(targetItemId) is not { } targetProject) return (false, null);
        if (await _sessions.EnsureAsync(targetProject, targetItem, ct).ConfigureAwait(false) is not { } bridge) return (false, null);
        var bridgeLifetime = _bridgeLifetimes.GetOrCreateValue(bridge);
        PendingExpectation? pending = null;
        QueuedWakeup? wakeup = null;
        string? deliveryId = null;
        InFlightDelivery inFlight;
        Func<bool> effectiveShouldCommit;
        lock (_ledger.Gate)
        {
            if (IsDeliveryCancelledLocked(threadId, taskId, expectReply))
            {
                return (false, null);
            }
            if (expectReply || wakeupSeq is not null)
            {
                deliveryId = _ledger.NextId("del");
            }
            var ingressToken = Interlocked.Increment(ref _ingressTokenSeq);
            if (expectReply)
            {
                var dispatchedAcceptedSeq = _ledger.GetLatestAcceptedDeliverySeq(targetItemId, threadId);
                pending = new PendingExpectation(
                    callerItemId,
                    taskId,
                    threadId,
                    DateTimeOffset.UtcNow,
                    deliveryId,
                    wakeupSeq,
                    reopenCompletedTask,
                    callerTerminalLinkId,
                    transferCallerTerminalOwnership,
                    expectedInitialTaskOwnershipEpoch,
                    callerTerminalSession,
                    dispatchedAcceptedSeq)
                {
                    BridgeOwner = bridgeLifetime,
                    IngressToken = ingressToken,
                };
                _ledger.EnqueuePending(targetItemId, pending);
            }
            if (wakeupSeq is { } replySeq && !string.IsNullOrWhiteSpace(threadId) && deliveryId is not null)
            {
                wakeup = new QueuedWakeup(deliveryId, threadId, replySeq, wakeupTasks ?? [], _ledger.GetSessionEpoch(targetItemId));
                _ledger.AddActiveWakeup(targetItemId, wakeup);
            }
            inFlight = new InFlightDelivery(ingressToken, envelope, deliveryId, threadId, taskId, expectReply, ct);
            effectiveShouldCommit = () =>
            {
                if (s_observingGuard)
                {
                    s_observedGuardDelivery = inFlight;
                }
                var ok = Volatile.Read(ref inFlight.BeginCompleted)
                    && bridgeLifetime.IsActive(Volatile.Read(ref inFlight.BridgeGeneration))
                    && (shouldCommit is null || shouldCommit())
                    && IsDeliveryStillActive(targetItemId, pending, wakeup, threadId, taskId, expectReply);
                if (ok && !s_observingGuard)
                {
                    var prev = s_lastGuardPassedToken;
                    s_lastGuardPassedToken = prev > 0
                        && prev != inFlight.IngressToken
                        && IsInFlightTokenActive(targetItemId, prev)
                        ? AmbiguousLegacyIngressToken
                        : inFlight.IngressToken;
                }
                return ok;
            };
            inFlight.Guard = effectiveShouldCommit;
            RegisterInFlightDeliveryLocked(targetItemId, inFlight);
        }
        if (!TryBeginBridgeDelivery(bridgeLifetime, deliveryId, out var bridgeGeneration))
        {
            lock (_ledger.Gate)
            {
                RemoveInFlightDeliveryLocked(targetItemId, inFlight);
                if (pending is not null) _ledger.RemovePending(targetItemId, pending);
                if (wakeup is not null) _ledger.RemoveActiveWakeup(targetItemId, wakeup);
            }
            return (false, null);
        }
        Volatile.Write(ref inFlight.BridgeGeneration, bridgeGeneration);
        Volatile.Write(ref inFlight.BeginCompleted, true);
        var delivered = false;
        try
        {
            delivered = await DeliverToBridgeWithoutOuterGuardObservation(
                bridge, envelope, threadId, taskId, expectReply, deliveryId, effectiveShouldCommit, ct).ConfigureAwait(false);
        }
        finally
        {
            if (s_lastGuardPassedToken == inFlight.IngressToken)
            {
                s_lastGuardPassedToken = 0;
            }
            if (!delivered)
            {
                lock (_ledger.Gate)
                {
                    RemoveInFlightDeliveryLocked(targetItemId, inFlight);
                    if (pending is not null) _ledger.RemovePending(targetItemId, pending);
                    if (wakeup is not null) _ledger.RemoveActiveWakeup(targetItemId, wakeup);
                }
                UntrackBridgeDeliveryForOwner(bridgeLifetime, deliveryId);
            }
            else
            {
                bool untrackDelivery;
                lock (_ledger.Gate)
                {
                    RemoveInFlightDeliveryLocked(targetItemId, inFlight);
                    var bridgeStillActive = bridgeLifetime.IsActive(bridgeGeneration);
                    var shouldRetireOneWay = wakeup is not null
                        && !expectReply
                        && !wakeup.ManagedLifetime
                        && !bridge.TracksActiveTurn
                        && !bridge.SupportsExactDeliveryWithdrawal;

                    if (!bridgeStillActive && pending is not null && !pending.Accepted)
                    {
                        // The bridge was unregistered before this receipt arrived and never accepted ownership of the
                        // reply expectation; clean up ledger state and report undelivered so callers never observe an
                        // orphaned running task.
                        _ledger.RemovePending(targetItemId, pending);
                        if (wakeup is not null) _ledger.RemoveActiveWakeup(targetItemId, wakeup);
                        delivered = false;
                        untrackDelivery = true;
                    }
                    else if (pending is null
                        && !inFlight.Dispatched
                        && (!bridgeStillActive
                            || (shouldCommit is not null && !shouldCommit())
                            || IsDeliveryCancelledLocked(threadId, taskId, expectReply: false)
                            || (wakeup is not null
                                && (wakeup.Revoked
                                    || wakeup.SessionEpoch != _ledger.GetSessionEpoch(targetItemId)
                                    || _ledger.IsWakeupReadConsumed(targetItemId, wakeup)))))
                    {
                        // One-way delivery was never dispatched on the target and its guard was already revoked
                        // (bridge retired, reply consumed via await_events/read_session, or cancelled) before receipt.
                        if (wakeup is not null) _ledger.RemoveActiveWakeup(targetItemId, wakeup);
                        delivered = false;
                        untrackDelivery = true;
                    }
                    else
                    {
                        // Idempotent fallback for bridges that do not call TryAcceptCollabDelivery before returning.
                        // Already-accepted deliveries cannot advance current ownership on late acknowledgement, and
                        // stale unaccepted deliveries are removed so they cannot intercept a successor's completion.
                        if (pending is not null
                            && !pending.Accepted
                            && _ledger.HasPendingReference(targetItemId, pending)
                            && !AcceptPendingDeliveryLocked(targetItemId, pending, markDispatched: !bridge.TracksActiveTurn))
                        {
                            _ledger.RemovePending(targetItemId, pending);
                        }
                        if (wakeup is not null)
                        {
                            if (!bridgeStillActive)
                            {
                                _ledger.RemoveActiveWakeup(targetItemId, wakeup);
                            }
                            else if (shouldRetireOneWay)
                            {
                                // Retire ActiveWakeups bookkeeping for non-tracking legacy bridges without revoking any
                                // guard closure they may still hold in an internal queue (revocation on unregister uses
                                // bridgeLifetime.IsActive(bridgeGeneration)).
                                _ledger.RetireActiveWakeup(targetItemId, wakeup);
                            }
                        }
                        untrackDelivery = !bridgeStillActive
                            || shouldRetireOneWay
                            || (pending is null && (wakeup is null || wakeup.Revoked))
                            || (pending is not null && !_ledger.HasPendingReference(targetItemId, pending));
                    }
                }
                if (untrackDelivery)
                {
                    UntrackBridgeDeliveryForOwner(bridgeLifetime, deliveryId);
                }
            }
        }
        return (delivered, deliveryId);
    }

    internal bool IsInitialLaunchDeliveryCancelled(string itemId, string? deliveryId, string? threadId, string? taskId)
    {
        lock (_ledger.Gate)
        {
            if (IsDeliveryCancelledLocked(threadId, taskId, expectReply: true))
            {
                return true;
            }
            var trackedLocally = (!string.IsNullOrWhiteSpace(taskId) && _ledger.Tasks.ContainsKey(taskId))
                || (!string.IsNullOrWhiteSpace(threadId) && _ledger.Threads.ContainsKey(threadId));
            return trackedLocally && !_ledger.HasActivePendingDelivery(itemId, deliveryId, threadId, taskId);
        }
    }

    private bool IsDeliveryCancelledLocked(string? threadId, string? taskId, bool expectReply)
    {
        if (!string.IsNullOrWhiteSpace(taskId)
            && _ledger.Tasks.TryGetValue(taskId, out var task)
            && task.Status == CollabTaskStatus.Cancelled)
        {
            return true;
        }
        if (expectReply
            && !string.IsNullOrWhiteSpace(threadId)
            && _ledger.Threads.TryGetValue(threadId, out var thread)
            && thread.Status == CollabThreadStatus.Cancelled)
        {
            return true;
        }
        return false;
    }

    private bool IsDeliveryStillActive(
        string targetItemId,
        PendingExpectation? pending,
        QueuedWakeup? wakeup,
        string? threadId,
        string? taskId,
        bool expectReply)
    {
        lock (_ledger.Gate)
        {
            if (IsDeliveryCancelledLocked(threadId, taskId, expectReply))
            {
                return false;
            }
            if (pending is not null
                && (!_ledger.HasPendingReference(targetItemId, pending)
                    || (!pending.Accepted && IsCallerTerminalGenerationRetiredLocked(pending))))
            {
                return false;
            }
            if (wakeup is not null
                && (wakeup.Revoked
                    || wakeup.SessionEpoch != _ledger.GetSessionEpoch(targetItemId)
                    || _ledger.IsWakeupReadConsumed(targetItemId, wakeup)))
            {
                return false;
            }
            return true;
        }
    }

    /* ==================== Remote Hosts ==================== */

    /// <summary>
    /// Starts <paramref name="definition"/> in a project of a paired host and follows it like a spawned child: same task,
    /// thread and wake-up, its permission cards raised here, its answer taken from the host's timeline.
    /// </summary>
    private async Task<JsonObject> DelegateToRemoteAsync(
        string callerItemId,
        Project project,
        AgentDefinition definition,
        CollabDelegateRequest request,
        CancellationToken ct)
    {
        var goal = request.Goal;
        var remotes = RemoteHosts;
        if (remotes is null)
        {
            return ToolError("Delegating to other computers is turned off in Octopus settings (Agents → collaboration).");
        }
        var hostId = project.RemoteHost!;
        var hostName = remotes.HostName(hostId);
        // The host has no plan mode or collab worktrees over the wire yet; refuse rather than silently widen access.
        var requestedIsolation = ParseIsolation(request.Isolation);
        if (requestedIsolation is { } wanted && !_policy.AllowsRemoteIsolation(wanted))
        {
            return ToolError($"Isolation '{IsolationToKey(wanted)}' cannot be enforced on {hostName}; omit isolation there, or delegate locally to use it.");
        }
        var callerItem = _index.FindItem(callerItemId);
        if (callerItem is null)
        {
            return ToolError("Caller session could not be resolved.");
        }

        ICollabRemoteHost host;
        try
        {
            host = await remotes.ConnectAsync(hostId, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ToolError($"{hostName} cannot be reached right now ({e.Message}).");
        }
        if (!host.SupportsAcpGui)
        {
            return ToolError($"{hostName} runs an Octopus version without GUI conversations, so a delegate there cannot be followed. Update Octopus on that computer.");
        }

        var taskId = _ledger.NextId("task");
        var threadId = _ledger.NextId("t");
        var deliveryId = _ledger.NextId("del");
        var title = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = $"{definition.Name} · {TruncateTitle(goal, 28)}";
        var callerLabel = FormatCallerLabel(callerItem);
        var prompt = CollabEnvelope.Delegation(taskId, threadId, callerLabel, goal, request.ContextFiles, fromDevice: LocalDeviceName);

        ItemMessage created;
        try
        {
            created = await host.CreateItemAsync(new CreateItemMessage
            {
                ProjectId = project.RemoteId!,
                Kind = ItemKinds.Agent,
                AgentId = definition.Id,
                Model = request.Model,
                Effort = request.Effort,
                Prompt = prompt,
                Surface = ItemSurfaces.Gui,
                Title = title,
                OriginDevice = LocalDeviceName,
                OriginSession = callerLabel,
                CollabDeliveryId = deliveryId,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ToolError($"{hostName} could not start {definition.Name}: {e.Message}");
        }

        // Mirror the item here now (the sidebar sync would add it too, without its parent), nested under the caller.
        await InvokeOnUiAsync(() =>
        {
            if (_index.FindItem(created.Id) is { } mirrored)
            {
                mirrored.ParentItemId = callerItemId;
                mirrored.CollabIsolation = "shared";
            }
            else
            {
                _projects.AddItem(project, new ProjectItem
                {
                    Id = created.Id,
                    Kind = ProjectItemKind.Agent,
                    AgentId = definition.Id,
                    Title = title,
                    ParentItemId = callerItemId,
                    CollabIsolation = "shared",
                    Launch = new AgentLaunchOptions { Model = created.Model, Effort = created.Effort },
                }, activate: false);
            }
        }).ConfigureAwait(false);

        var maxHops = _policy.DefaultMaxHops;
        var callerTurn = ReadCallerActiveTurn(callerItemId);
        lock (_ledger.Gate)
        {
            var thread = new CollabThreadRecord
            {
                ThreadId = threadId,
                Title = title,
                InitiatorItemId = callerItemId,
                ParticipantItemIds = [callerItemId, created.Id],
                Hops = 1,
                MaxHops = maxHops,
                Messages =
                [
                    new CollabMessageRecord
                    {
                        MessageId = _ledger.NextId("msg"),
                        ThreadId = threadId,
                        FromItemId = callerItemId,
                        FromLabel = callerLabel,
                        FromAgentId = callerItem.AgentId,
                        ToItemId = created.Id,
                        ToLabel = $"{title} @ {hostName}",
                        Round = 1,
                        Text = goal,
                    },
                ],
            };
            var record = new CollabTaskRecord
            {
                TaskId = taskId,
                CallerItemId = callerItemId,
                TargetItemId = created.Id,
                TargetAgentId = definition.Id,
                Title = title,
                Goal = goal,
                Isolation = CollabIsolationMode.Shared,
                ThreadId = threadId,
                Status = CollabTaskStatus.Running,
                LatestActivity = $"Starting {definition.Name} on {hostName}",
            };
            _ledger.Add(thread);
            _ledger.Add(record);
            RecordDelegatedChildOwnershipLocked(callerItemId, callerTurn, taskId);
            // Set before following the session, so an answer already in its first snapshot is taken as this task's.
            var remotePending = new PendingExpectation(callerItemId, taskId, threadId, DateTimeOffset.UtcNow, deliveryId);
            _ledger.EnqueuePending(created.Id, remotePending);
            AcceptPendingDeliveryLocked(created.Id, remotePending);
        }

        bool followed;
        try
        {
            followed = await _remoteProvider.FollowAsync(host, hostName, project, created.Id, definition.Id, title, callerItemId, fresh: true, ct, threadId, taskId, deliveryId).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AbortTasksForTarget(created.Id, $"Started {definition.Name} on {hostName}, but following its conversation was cancelled before attachment completed; check it on that computer.", failed: true, allOpenTasks: false, allPending: false, wakeCaller: false, threadId: threadId, taskId: taskId, deliveryId: deliveryId, hasTurnIdentity: true);
            RaiseChanged(callerItemId);
            RaiseChanged(created.Id);
            throw;
        }
        if (!followed)
        {
            AbortTasksForTarget(created.Id, $"Started {definition.Name} on {hostName}, but could not follow its conversation; check it on that computer.", failed: true, allOpenTasks: false, allPending: false, wakeCaller: false, threadId: threadId, taskId: taskId, deliveryId: deliveryId, hasTurnIdentity: true);
        }
        remotes.RequestSync(hostId);
        RaiseChanged(callerItemId);
        RaiseChanged(created.Id);

        var response = new JsonObject
        {
            ["task_id"] = taskId,
            ["session_id"] = created.Id,
            ["thread_id"] = threadId,
            ["agent_id"] = definition.Id,
            ["title"] = title,
            ["surface"] = "gui",
            ["isolation"] = "shared",
            ["host"] = hostName,
            ["working_directory"] = project.Path,
            ["status"] = followed ? "running" : "failed",
        };
        if (requestedIsolation is null && _settings.Current.Agents.Collab.DefaultIsolation != CollabIsolationMode.Shared)
        {
            response["note"] = $"Your default isolation does not apply on {hostName}; the delegate works directly in {project.Path} there.";
        }
        return ToolOk(response);
    }

    /* ==================== Cancellation & Aborted Turns ==================== */

    public void NotifyTurnAborted(string itemId, string reason, bool failed, DateTimeOffset? cancelDelegationsSince = null) =>
        NotifyTurnAbortedCore(itemId, reason, failed, threadId: null, taskId: null, deliveryId: null, hasTurnIdentity: false, cancelDelegationsSince);

    public void NotifyTurnAborted(string itemId, string reason, bool failed, string? threadId, string? taskId, DateTimeOffset? cancelDelegationsSince = null) =>
        NotifyTurnAbortedCore(itemId, reason, failed, threadId, taskId, deliveryId: null, hasTurnIdentity: true, cancelDelegationsSince);

    public void NotifyTurnAborted(string itemId, string reason, bool failed, string? threadId, string? taskId, string? deliveryId, DateTimeOffset? cancelDelegationsSince = null) =>
        NotifyTurnAbortedCore(itemId, reason, failed, threadId, taskId, deliveryId, hasTurnIdentity: true, cancelDelegationsSince);

    internal void NotifySessionAbortedAllPending(
        string itemId,
        string reason,
        bool failed,
        IReadOnlySet<string>? onlyDeliveryIds = null,
        IReadOnlySet<string>? onlyOwnerLinkIds = null) =>
        AbortTasksForTarget(itemId, reason, failed, allOpenTasks: false, allPending: true, wakeCaller: true, onlyDeliveryIds: onlyDeliveryIds, onlyOwnerLinkIds: onlyOwnerLinkIds);

    internal void NotifyTerminalDeliveryAccepted(string itemId, string deliveryId, string linkId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId) || string.IsNullOrWhiteSpace(linkId)) return;
        bool stamped;
        lock (_ledger.Gate)
        {
            stamped = _ledger.SetPendingOwnerLink(itemId, deliveryId, linkId);
        }
        if (!stamped) return;
        if (_terminalSessions is not null
            && !string.Equals(_terminalSessions.Find(itemId)?.CurrentLinkId, linkId, StringComparison.Ordinal))
        {
            AbortTasksForTarget(
                itemId,
                "The delegate session was closed before it answered.",
                failed: false,
                allOpenTasks: false,
                allPending: true,
                wakeCaller: false,
                onlyDeliveryIds: new HashSet<string>(StringComparer.Ordinal) { deliveryId });
        }
    }

    internal void NotifyTerminalLinkStopped(string itemId, string linkId)
    {
        if (string.IsNullOrWhiteSpace(linkId)) return;
        AbortTasksForTarget(
            itemId,
            "The delegate session was restarted before it answered.",
            failed: false,
            allOpenTasks: false,
            allPending: true,
            wakeCaller: false,
            onlyOwnerLinkIds: new HashSet<string>(StringComparer.Ordinal) { linkId });
    }

    internal void NotifyTerminalSessionRetired(string itemId, TerminalSession retiredSession) =>
        _terminalProvider.SettleRetiredSession(itemId, retiredSession);

    internal void NotifyTerminalSessionRetired(
        string itemId,
        IReadOnlySet<string>? deliveryIds,
        IReadOnlySet<string>? ownerLinkIds = null)
    {
        if ((deliveryIds is null || deliveryIds.Count == 0) && (ownerLinkIds is null || ownerLinkIds.Count == 0)) return;
        List<string>? retiredOutbound = null;
        if (ownerLinkIds is { Count: > 0 })
        {
            lock (_ledger.Gate)
            {
                retiredOutbound = _ledger.GetUnfinishedOutboundTasksForCallerLinks(itemId, ownerLinkIds);
            }
        }
        AbortTasksForTarget(
            itemId,
            "The delegate session was closed before it answered.",
            failed: false,
            allOpenTasks: false,
            allPending: true,
            wakeCaller: false,
            onlyDeliveryIds: deliveryIds,
            onlyOwnerLinkIds: ownerLinkIds);
        if (retiredOutbound is { Count: > 0 })
        {
            foreach (var delegatedTaskId in retiredOutbound)
            {
                string? retiredLink = null;
                lock (_ledger.Gate)
                {
                    if (_ledger.GetCallerTerminalLink(delegatedTaskId) is { Length: > 0 } currentLink
                        && ownerLinkIds!.Contains(currentLink))
                    {
                        retiredLink = currentLink;
                    }
                }
                if (retiredLink is not null)
                {
                    CancelTaskTree(
                        delegatedTaskId,
                        "The delegating session was closed.",
                        CancellationToken.None,
                        cancelledByCallerTerminalRetirement: true,
                        retiredCallerLinkId: retiredLink);
                }
            }
        }
    }

    internal void NotifyTerminalSessionClosed(string itemId, TerminalSession? closedSession = null)
    {
        if (_terminalSessions?.Find(itemId) is not null)
        {
            if (closedSession is not null)
            {
                NotifyTerminalSessionRetired(itemId, closedSession);
            }
            return;
        }

        // Snapshot outbound delegations BEFORE publishing any Changed notification so a replacement session
        // attaching during those callbacks cannot have its newly delegated work cancelled by this stale close.
        // Skip unaccepted initial delegations so refused/cancelled dispatches still roll back without residue.
        List<(string TaskId, string? CallerLinkId)> delegated;
        lock (_ledger.Gate)
        {
            delegated = _ledger.Tasks.Values
                .Where(t => t.CallerItemId == itemId && !t.IsFinished && !_ledger.HasUnacceptedInitialDelegation(t.TaskId))
                .Select(t => (t.TaskId, closedSession?.CurrentLinkId ?? _ledger.GetCallerTerminalLink(t.TaskId)))
                .ToList();
        }

        if (_sessions.Find(itemId) is TerminalCollabSession terminalBridge)
        {
            _sessions.Unregister(terminalBridge);
        }
        AbortTasksForTarget(itemId, "The delegate session was closed before it answered.", failed: false, allOpenTasks: true, allPending: true, wakeCaller: false);
        ClearChildApproval(itemId);

        foreach (var (delegatedTaskId, retiredLinkId) in delegated)
        {
            CancelTaskTree(
                delegatedTaskId,
                "The delegating session was closed.",
                CancellationToken.None,
                cancelledByCallerTerminalRetirement: true,
                retiredCallerLinkId: retiredLinkId);
        }
    }

    private void NotifyTurnAbortedCore(
        string itemId,
        string reason,
        bool failed,
        string? threadId,
        string? taskId,
        string? deliveryId,
        bool hasTurnIdentity,
        DateTimeOffset? cancelDelegationsSince)
    {
        var abortedTaskIds = AbortTasksForTarget(itemId, reason, failed, allOpenTasks: false, allPending: false, wakeCaller: true, threadId, taskId, deliveryId, hasTurnIdentity);

        if (!_policy.CancelDelegationsWhenStopped) return;
        if (cancelDelegationsSince is null && (failed || abortedTaskIds.Count == 0)) return;
        List<string> delegated;
        lock (_ledger.Gate)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (cancelDelegationsSince is { } since)
            {
                foreach (var t in _ledger.Tasks.Values)
                {
                    if (t.CallerItemId == itemId && !t.IsFinished && t.CreatedAt >= since)
                    {
                        ids.Add(t.TaskId);
                    }
                }
            }
            if (!failed)
            {
                foreach (var abortedTaskId in abortedTaskIds)
                {
                    foreach (var childTaskId in _ledger.GetUnfinishedChildTaskIds(abortedTaskId))
                    {
                        ids.Add(childTaskId);
                    }
                }
            }
            delegated = [.. ids];
        }
        if (delegated.Count == 0) return;
        _ = Task.Run(async () =>
        {
            foreach (var delegatedTaskId in delegated)
            {
                await CancelTaskTreeAsync(delegatedTaskId, "The delegating session's turn was stopped by the user.", CancellationToken.None).ConfigureAwait(false);
            }
        });
    }

    public async Task<bool> CancelTaskAsync(string taskId, string reason, CancellationToken ct = default) =>
        (await CancelTaskTreeAsync(taskId, reason, ct).ConfigureAwait(false)).CancelledCount > 0;

    /// <summary>
    /// Settles the tasks that <paramref name="targetItemId"/> owed an answer: one turn's expectation
    /// (<paramref name="allPending"/> false), all pending expectations for a lost/closed session
    /// (<paramref name="allPending"/> true), or every open task/thread (<paramref name="allOpenTasks"/> true).
    /// </summary>
    private List<string> AbortTasksForTarget(
        string targetItemId,
        string reason,
        bool failed,
        bool allOpenTasks,
        bool allPending,
        bool wakeCaller,
        string? threadId = null,
        string? taskId = null,
        string? deliveryId = null,
        bool hasTurnIdentity = false,
        IReadOnlySet<string>? onlyDeliveryIds = null,
        IReadOnlySet<string>? onlyOwnerLinkIds = null)
    {
        var newStatus = failed ? CollabTaskStatus.Failed : CollabTaskStatus.Cancelled;
        var threadStatus = failed ? CollabThreadStatus.Blocked : CollabThreadStatus.Cancelled;
        List<string> abortedTaskIds = [];
        List<(string CallerId, string? ThreadId, string Title, int Round, int MaxHops, long ReplySeq, IReadOnlyList<(string TaskId, long Seq)> Tasks)> notify = [];
        HashSet<string> approvalParents = new(StringComparer.Ordinal);
        var responderLabel = _index.FindItem(targetItemId) is { } responder ? FormatCallerLabel(responder) : targetItemId;

        lock (_ledger.Gate)
        {
            bool IsSupersededOrStillOwned(PendingExpectation p) =>
                IsPendingSupersededLocked(targetItemId, p)
                || (p.ThreadId is { } ptid
                    && _ledger.HasNewerAcceptedDelivery(targetItemId, ptid, p));

            List<PendingExpectation> settledPending;
            if (allPending || allOpenTasks)
            {
                settledPending = _ledger.TakeAllPending(
                    targetItemId,
                    allOpenTasks ? null : onlyDeliveryIds,
                    allOpenTasks ? null : onlyOwnerLinkIds);
                foreach (var taken in settledPending)
                {
                    UntrackBridgeDelivery(targetItemId, taken.DeliveryId);
                }
                if (!allOpenTasks && (onlyDeliveryIds is not null || onlyOwnerLinkIds is not null))
                {
                    settledPending.RemoveAll(IsSupersededOrStillOwned);
                }
            }
            else
            {
                var single = _ledger.TakePendingForTurn(targetItemId, threadId, taskId, hasTurnIdentity, deliveryId);
                if (single is not null)
                {
                    UntrackBridgeDelivery(targetItemId, single.DeliveryId ?? deliveryId);
                }
                if (single is not null && IsSupersededOrStillOwned(single))
                {
                    single = null;
                }
                settledPending = single is not null ? [single] : [];
            }
            if (settledPending.Count == 0 && !allOpenTasks) return abortedTaskIds;

            var clearApprovals = allOpenTasks || (allPending && onlyDeliveryIds is null && onlyOwnerLinkIds is null);
            var settledTasksByThread = new Dictionary<string, List<(string TaskId, long Seq)>>(StringComparer.Ordinal);
            foreach (var task in _ledger.Tasks.Values)
            {
                if (task.TargetItemId != targetItemId || task.IsFinished) continue;
                var owed = allOpenTasks
                    || settledPending.Any(p =>
                        (p.TaskId is not null && task.TaskId == p.TaskId)
                        || (p.ThreadId is not null && task.ThreadId == p.ThreadId));
                if (!owed) continue;
                if (task.Status == CollabTaskStatus.WaitingApproval) clearApprovals = true;
                _ledger.SetTaskStatus(task, newStatus, failed ? "Failed" : "Stopped", reason);
                abortedTaskIds.Add(task.TaskId);
                if (task.ThreadId is { } taskTid)
                {
                    if (!settledTasksByThread.TryGetValue(taskTid, out var list))
                    {
                        list = [];
                        settledTasksByThread[taskTid] = list;
                    }
                    list.Add((task.TaskId, task.UpdatedSeq));
                }
            }

            if (clearApprovals || !_ledger.Tasks.Values.Any(t => t.TargetItemId == targetItemId && !t.IsFinished && t.Status == CollabTaskStatus.WaitingApproval))
            {
                var (clearedApprovals, _) = _ledger.ClearApprovals(targetItemId, approvalId: null);
                foreach (var existing in clearedApprovals)
                {
                    approvalParents.Add(existing.ParentItemId);
                }
            }

            // Close the thread(s) the target owed a reply on, recording why, so the other side sees the outcome.
            var threadCallers = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var pending in settledPending)
            {
                if (pending.ThreadId is { } pendingThread)
                {
                    threadCallers[pendingThread] = pending.CallerItemId;
                }
            }
            if (allOpenTasks)
            {
                foreach (var th in _ledger.Threads.Values)
                {
                    if (!th.IsClosed
                        && th.ParticipantItemIds.Contains(targetItemId, StringComparer.Ordinal)
                        && !_ledger.HasUnacceptedInitialDelegationForThread(th.ThreadId, targetItemId))
                    {
                        threadCallers.TryAdd(th.ThreadId, null);
                    }
                }
            }
            foreach (var (tid, pendingCaller) in threadCallers)
            {
                if (!_ledger.Threads.TryGetValue(tid, out var thread) || thread.IsClosed) continue;
                var callerId = pendingCaller ?? ResolveReplyRecipientLocked(thread, targetItemId);
                thread.Status = threadStatus;
                _ledger.MarkThreadTurnSuperseded(tid);
                _ledger.AppendMessage(thread, new CollabMessageRecord
                {
                    MessageId = _ledger.NextId("msg"),
                    ThreadId = tid,
                    FromItemId = targetItemId,
                    FromLabel = responderLabel,
                    ToItemId = callerId,
                    Round = thread.Hops,
                    Text = reason,
                    Status = threadStatus.ToKey(),
                });
                if (callerId is not null && callerId != "user")
                {
                    IReadOnlyList<(string TaskId, long Seq)> threadTasks = settledTasksByThread.TryGetValue(tid, out var list) ? list : [];
                    notify.Add((callerId, tid, thread.Title, thread.Hops, thread.MaxHops, thread.UpdatedSeq, threadTasks));
                }
            }
        }

        var signaledWaiters = _ledger.SignalWaiters();
        RaiseChanged(targetItemId);
        foreach (var parentId in approvalParents) RaiseChanged(parentId);
        foreach (var n in notify)
        {
            RaiseChanged(n.CallerId);
            var taskIds = n.Tasks.Select(task => task.TaskId).ToArray();
            var signaled = signaledWaiters.Any(w => w.CallerItemId == n.CallerId && w.Matches(n.ThreadId!, taskIds));
            if (!wakeCaller || signaled || !_policy.WakeCallerOnReply) continue;
            if (IsReplyConsumed(n.CallerId, n.ThreadId!, n.ReplySeq, n.Tasks)) continue;
            var envelope = CollabEnvelope.Reply(n.ThreadId!, n.Title, responderLabel, n.Round, n.MaxHops, threadStatus.ToKey(), reason);
            _ = Task.Run(() => DeliverEnvelopeAsync(
                n.CallerId,
                envelope,
                n.ThreadId,
                taskId: null,
                expectReply: false,
                callerItemId: targetItemId,
                CancellationToken.None,
                shouldCommit: () => !IsReplyConsumed(n.CallerId, n.ThreadId!, n.ReplySeq, n.Tasks),
                wakeupSeq: n.ReplySeq,
                wakeupTasks: n.Tasks));
        }
        return abortedTaskIds;
    }

    private readonly record struct CancelTreeResult(int CancelledCount, List<string> UnstoppedLegacyRemoteHosts);

    /// <summary>Cancels <paramref name="rootTaskId"/> and everything its (spawned) delegate delegated further. Returns how many tasks were cancelled and any older remote hosts whose unscoped interrupt was declined.</summary>
    private Task<CancelTreeResult> CancelTaskTreeAsync(string rootTaskId, string reason, CancellationToken ct) =>
        Task.FromResult(CancelTaskTree(rootTaskId, reason, ct));

    private CancelTreeResult CancelTaskTree(
        string rootTaskId,
        string reason,
        CancellationToken ct,
        bool cancelledByCallerTerminalRetirement = false,
        string? retiredCallerLinkId = null)
    {
        var count = 0;
        List<string> unstoppedLegacyRemoteHosts = [];
        var queue = new Queue<(string TaskId, string Reason)>();
        queue.Enqueue((rootTaskId, reason));
        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (queue.TryDequeue(out var next))
        {
            if (!visited.Add(next.TaskId)) continue;
            CollabTaskRecord task;
            bool interrupt;
            string? removedDeliveryId;
            List<string> nested;
            lock (_ledger.Gate)
            {
                if (!_ledger.Tasks.TryGetValue(next.TaskId, out var found) || found.IsFinished) continue;
                task = found;
                var ownedNested = _ledger.GetUnfinishedChildTaskIds(task.TaskId);
                _ledger.SetTaskStatus(task, CollabTaskStatus.Cancelled, "Cancelled", next.Reason);
                if (cancelledByCallerTerminalRetirement && string.Equals(task.TaskId, rootTaskId, StringComparison.Ordinal))
                {
                    _ledger.MarkCancelledByCallerTerminalRetirement(task.TaskId, retiredCallerLinkId);
                }

                var (_, wasHead, hasEarlierPending, wasDeferred, pendingDeliveryId) = _ledger.RemovePendingForTask(task.TargetItemId, task.ThreadId, task.TaskId);
                removedDeliveryId = pendingDeliveryId;

                if (task.ThreadId is { } tid && _ledger.Threads.TryGetValue(tid, out var thread) && !thread.IsClosed)
                {
                    _ledger.SetThreadStatus(thread, CollabThreadStatus.Cancelled);
                }

                // Cascade strictly to unfinished tasks owned by the cancelled task so unrelated work on a reused
                // (or spawned-then-reused) session stays intact.
                // A queued task sitting behind an earlier active job is removed from the queue without interrupting that active job,
                // and an already-deferred turn has already ended on the target so cancelling it must not stop later manual work.
                var isDedicatedChild = _index.FindItem(task.TargetItemId)?.ParentItemId == task.CallerItemId;
                interrupt = !wasDeferred && (wasHead || (isDedicatedChild && !hasEarlierPending));
                nested = ownedNested;
            }

            count++;
            foreach (var id in nested) queue.Enqueue((id, "Its delegating task was cancelled."));
            ct.ThrowIfCancellationRequested();
            var unstoppedLegacyHost = StopDelegate(task, removedDeliveryId, interrupt);
            if (unstoppedLegacyHost is not null)
            {
                if (!unstoppedLegacyRemoteHosts.Contains(unstoppedLegacyHost, StringComparer.Ordinal))
                {
                    unstoppedLegacyRemoteHosts.Add(unstoppedLegacyHost);
                }
                lock (_ledger.Gate)
                {
                    task.ResultSummary = FormatLegacyRemoteCancelSummary(next.Reason, unstoppedLegacyHost);
                }
            }
            RaiseChanged(task.CallerItemId);
            RaiseChanged(task.TargetItemId);
        }

        if (count > 0) _ledger.SignalWaiters();
        return new CancelTreeResult(count, unstoppedLegacyRemoteHosts);
    }

    private static string FormatLegacyRemoteCancelSummary(string reason, string hostName) =>
        $"{reason} (Cancelled locally; {hostName} does not support host-checked task cancellation, so the remote session may still be running there. Stop it directly on that host if needed.)";

    private string? StopDelegate(CollabTaskRecord task, string? removedDeliveryId, bool interruptByLedgerOrder)
    {
        if (_index.FindItem(task.TargetItemId) is not { } item || _index.FindProjectOf(task.TargetItemId) is not { } project) return null;
        if (item.ParentItemId == task.CallerItemId && item.Launch is { } launch
            && (launch.CollabTaskId is null || launch.CollabTaskId == task.TaskId))
        {
            launch.Prompt = null;
        }
        if (_sessions.TryAttach(project, item) is not { } bridge) return null;
        var removedQueued = !string.IsNullOrWhiteSpace(removedDeliveryId)
            ? bridge.CancelQueuedCollabPrompts(task.ThreadId, task.TaskId, removedDeliveryId)
            : 0;
        if (removedQueued == 0)
        {
            removedQueued = bridge.CancelQueuedCollabPrompts(task.ThreadId, task.TaskId);
        }
        var interrupt = interruptByLedgerOrder;
        if (bridge.TryGetActiveCollabTurn(out var activeThreadId, out var activeTaskId, out var activeDeliveryId))
        {
            interrupt = MatchesCollabTurn(activeThreadId, activeTaskId, activeDeliveryId, task.ThreadId, task.TaskId, removedDeliveryId)
                || removedQueued == 0;
        }
        if (!interrupt) return null;
        if (bridge is RemoteCollabSession remote && !remote.SupportsScopedCancel)
        {
            // Clear local mirror tracking synchronously without sending an unscoped acp/cancel to an older peer
            // that cannot verify host-time turn identity.
            _ = bridge.CancelActiveTurnAsync(task.ThreadId, task.TaskId, removedDeliveryId);
            return remote.HostName;
        }
        // Don't wait out the agent's cancel grace period inside an MCP call.
        _ = Task.Run(async () =>
        {
            try { await bridge.CancelActiveTurnAsync(task.ThreadId, task.TaskId, removedDeliveryId).ConfigureAwait(false); }
            catch { /* best effort */ }
        }, CancellationToken.None);
        return null;
    }

    private static bool MatchesCollabTurn(
        string? candidateThreadId,
        string? candidateTaskId,
        string? candidateDeliveryId,
        string? threadId,
        string? taskId,
        string? deliveryId)
    {
        if (!string.IsNullOrWhiteSpace(deliveryId))
        {
            return !string.IsNullOrWhiteSpace(candidateDeliveryId)
                && string.Equals(candidateDeliveryId, deliveryId, StringComparison.Ordinal);
        }
        var hasThread = !string.IsNullOrWhiteSpace(threadId);
        var hasTask = !string.IsNullOrWhiteSpace(taskId);
        if (hasThread && hasTask)
        {
            var hasCandidateThread = !string.IsNullOrWhiteSpace(candidateThreadId);
            var hasCandidateTask = !string.IsNullOrWhiteSpace(candidateTaskId);
            if (hasCandidateThread && hasCandidateTask)
            {
                return string.Equals(candidateThreadId, threadId, StringComparison.Ordinal)
                    && string.Equals(candidateTaskId, taskId, StringComparison.Ordinal);
            }
            return (hasCandidateTask && string.Equals(candidateTaskId, taskId, StringComparison.Ordinal))
                || (hasCandidateThread && string.Equals(candidateThreadId, threadId, StringComparison.Ordinal));
        }
        if (hasTask)
        {
            return string.Equals(candidateTaskId, taskId, StringComparison.Ordinal);
        }
        if (hasThread)
        {
            return string.Equals(candidateThreadId, threadId, StringComparison.Ordinal);
        }
        return false;
    }

    /* ==================== Helpers ==================== */

    private readonly record struct CallerActiveTurn(
        bool TracksActiveTurn,
        bool HasActiveTurn,
        string? ThreadId,
        string? TaskId,
        bool CanInferPendingOwnership,
        string? ExternalTurnKey,
        string? CallerTerminalLinkId);

    private CallerActiveTurn ReadCallerActiveTurn(string callerItemId)
    {
        var callerTerminalLinkId = _terminalSessions?.Find(callerItemId)?.CurrentLinkId;
        if (_sessions.Find(callerItemId) is not { } bridge)
        {
            return new CallerActiveTurn(false, false, null, null, CanInferPendingOwnership: true, ExternalTurnKey: null, callerTerminalLinkId);
        }
        if (!bridge.TracksActiveTurn)
        {
            return new CallerActiveTurn(false, false, null, null, bridge.CanInferPendingOwnership, ExternalTurnKey: null, callerTerminalLinkId);
        }
        if (bridge.TryGetActiveCollabTurn(out var threadId, out var taskId, out _))
        {
            return new CallerActiveTurn(true, bridge.HasActiveTurn, threadId, taskId, bridge.CanInferPendingOwnership, bridge.ActiveExternalTurnKey, callerTerminalLinkId);
        }
        return new CallerActiveTurn(true, bridge.HasActiveTurn, bridge.ActiveCollabThreadId, bridge.ActiveCollabTaskId, bridge.CanInferPendingOwnership, bridge.ActiveExternalTurnKey, callerTerminalLinkId);
    }

    private void RecordDelegatedChildOwnershipLocked(
        string callerItemId,
        CallerActiveTurn callerTurn,
        string childTaskId,
        bool recordCallerTerminalLink = true)
    {
        if (recordCallerTerminalLink && !string.IsNullOrWhiteSpace(callerTurn.CallerTerminalLinkId))
        {
            _ledger.SetCallerTerminalLink(childTaskId, callerTurn.CallerTerminalLinkId);
        }
        var owningParentTaskId = ResolveOwningParentTaskIdLocked(callerItemId, callerTurn);
        if (owningParentTaskId is not null)
        {
            _ledger.SetParentTask(childTaskId, owningParentTaskId);
        }
        else if (callerTurn is { TracksActiveTurn: true, HasActiveTurn: true, ExternalTurnKey: { Length: > 0 } externalTurnKey })
        {
            _ledger.SetExternalTurnOwner(childTaskId, externalTurnKey);
        }
    }

    /// <summary>
    /// Resolves which open task targeting <paramref name="callerItemId"/> owns a newly delegated child task, or null
    /// for root/unrelated human work. Hold <see cref="_ledger.Gate"/>.
    /// </summary>
    private string? ResolveOwningParentTaskIdLocked(string callerItemId, CallerActiveTurn callerTurn)
    {
        if (callerTurn.TracksActiveTurn)
        {
            if (!callerTurn.HasActiveTurn)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(callerTurn.TaskId)
                && _ledger.Tasks.TryGetValue(callerTurn.TaskId, out var activeTask)
                && activeTask.TargetItemId == callerItemId
                && !activeTask.IsFinished)
            {
                return activeTask.TaskId;
            }
            if (!string.IsNullOrWhiteSpace(callerTurn.ThreadId))
            {
                var byThread = _ledger.Tasks.Values.FirstOrDefault(t =>
                    t.TargetItemId == callerItemId && t.ThreadId == callerTurn.ThreadId && !t.IsFinished);
                if (byThread is not null)
                {
                    return byThread.TaskId;
                }
                // A wakeup turn from a completed child thread still belongs to that child's unfinished parent task.
                var childTask = _ledger.Tasks.Values.FirstOrDefault(t =>
                    t.CallerItemId == callerItemId && t.ThreadId == callerTurn.ThreadId);
                if (childTask is not null
                    && _ledger.GetParentTaskId(childTask.TaskId) is { } ancestorId
                    && _ledger.Tasks.TryGetValue(ancestorId, out var ancestor)
                    && ancestor.TargetItemId == callerItemId
                    && !ancestor.IsFinished)
                {
                    return ancestor.TaskId;
                }
            }
            // Active turn with no local collaboration identity is an ordinary human turn (or remote turn tracked separately).
            return null;
        }

        var callerItem = _index.FindItem(callerItemId);

        // On a reused bridge that cannot verify active turn identity (e.g., a shared PTY terminal),
        // missing identity cannot establish authority to own or cancel another turn's descendants.
        if (callerTurn.CanInferPendingOwnership
            && callerItem?.Surface != AgentSurface.Terminal
            && _ledger.Pending.TryGetValue(callerItemId, out var pendingList))
        {
            var head = pendingList.FirstOrDefault(p => !p.SettledExplicitly && !p.Deferred);
            if (head is not null)
            {
                var headTaskId = ResolvePendingTaskIdLocked(callerItemId, head);
                if (headTaskId is not null)
                {
                    return headTaskId;
                }
            }
        }

        if (callerItem?.ParentItemId is { Length: > 0 } parentItemId)
        {
            var dedicatedOpen = _ledger.Tasks.Values
                .Where(t => t.TargetItemId == callerItemId && t.CallerItemId == parentItemId && !t.IsFinished)
                .OrderByDescending(t => t.CreatedAt)
                .FirstOrDefault();
            if (dedicatedOpen is not null)
            {
                return dedicatedOpen.TaskId;
            }
        }

        return null;
    }

    private string? ResolvePendingTaskIdLocked(string targetItemId, PendingExpectation pending)
    {
        if (!string.IsNullOrWhiteSpace(pending.TaskId)
            && _ledger.Tasks.TryGetValue(pending.TaskId, out var exact)
            && exact.TargetItemId == targetItemId
            && !exact.IsFinished)
        {
            return exact.TaskId;
        }
        if (!string.IsNullOrWhiteSpace(pending.ThreadId))
        {
            var byThread = _ledger.Tasks.Values.FirstOrDefault(t =>
                t.TargetItemId == targetItemId && t.ThreadId == pending.ThreadId && !t.IsFinished);
            if (byThread is not null)
            {
                return byThread.TaskId;
            }
        }
        return null;
    }

    /// <summary>
    /// Who a reply on <paramref name="thread"/> from <paramref name="responderItemId"/> goes to: the session that last wrote
    /// to the responder (so in a three-way thread the answer reaches whoever asked), else the thread's initiator.
    /// A user relay (from "user") is answered to the session the user typed in, which is the initiator.
    /// </summary>
    private static string? ResolveReplyRecipientLocked(CollabThreadRecord thread, string responderItemId)
    {
        for (var i = thread.Messages.Count - 1; i >= 0; i--)
        {
            var m = thread.Messages[i];
            if (m.ToItemId == responderItemId && m.FromItemId != responderItemId && m.FromItemId != "user")
            {
                return m.FromItemId;
            }
        }
        if (thread.InitiatorItemId != responderItemId && thread.InitiatorItemId.Length > 0)
        {
            return thread.InitiatorItemId;
        }
        return thread.ParticipantItemIds.FirstOrDefault(id => id != responderItemId);
    }


    private IReadOnlyList<Project> ResolveVisibleProjects(string callerItemId, string scope)
    {
        var current = _index.FindProjectOf(callerItemId) ?? _index.Current;
        if (current is null) return [];
        if (string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase) || current.GroupId is null)
        {
            return [current];
        }
        var group = _index.Groups.FirstOrDefault(g => g.Id == current.GroupId);
        if (group is null || !group.MergeWorkspaces)
        {
            return [current];
        }
        return _index.Projects
            .Where(p => p.GroupId == group.Id && ProjectLocation.IsOnThisComputer(p))
            .ToList();
    }

    private bool IsPeerVisible(string callerItemId, string targetItemId)
    {
        var visibleProjects = ResolveVisibleProjects(callerItemId, "group");
        // One lookup instead of scanning every visible project's items.
        return _index.FindProjectOf(targetItemId) is { } owner
            && (visibleProjects.Contains(owner) || VisibleRemoteProjects().Contains(owner));
    }

    private int ComputeDelegationDepth(string itemId)
    {
        var depth = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cur = _index.FindItem(itemId);
        while (cur?.ParentItemId is { Length: > 0 } parentId && seen.Add(parentId) && depth < 16)
        {
            depth++;
            cur = _index.FindItem(parentId);
        }
        return depth;
    }

    private string DescribeStatus(string itemId)
    {
        bool waitingApproval;
        bool hasOpenTask;
        lock (_ledger.Gate)
        {
            waitingApproval = _ledger.Approvals.Any(kv => kv.Key.Child == itemId && !kv.Value.IsResolved);
            hasOpenTask = _ledger.Tasks.Values.Any(t => t.TargetItemId == itemId && !t.IsFinished);
        }
        if (waitingApproval) return "waiting_approval";

        if (_sessions.Find(itemId) is { } session)
        {
            if (session.IsBusy) return session.IsConnected ? "working" : "starting";
            if (!session.IsConnected) return "disconnected";
            return hasOpenTask ? "starting" : "idle";
        }
        var activity = _activity?.Of(itemId) ?? ItemActivity.Idle;
        return activity switch
        {
            ItemActivity.Working => "working",
            ItemActivity.Waiting => "waiting_approval",
            ItemActivity.Done => "done",
            ItemActivity.Error => "error",
            _ => hasOpenTask ? "starting" : "idle",
        };
    }

    private string? BuildOneLineSummary(string itemId)
    {
        lock (_ledger.Gate)
        {
            var task = _ledger.Tasks.Values.LastOrDefault(t => t.TargetItemId == itemId);
            if (!string.IsNullOrWhiteSpace(task?.LatestActivity)) return task.LatestActivity;
        }
        if (_sessions.Find(itemId) is { } gui && !string.IsNullOrWhiteSpace(gui.CurrentStatusDetail))
        {
            return gui.CurrentStatusDetail;
        }
        return null;
    }

    private string ReadSessionContent(ProjectItem item, int maxTurns) =>
        _index.FindProjectOf(item.Id) is { } project && _sessions.TryAttach(project, item) is { } bridge
            ? bridge.ReadRecentSummary(maxTurns)
            : "(Session is not active in memory yet.)";

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (e.Kind == ProjectChangeKind.ItemTitle && e.Item is { } titled)
        {
            if (!_disposed)
            {
                NotifyRelatedItemObservers([titled.Id], changedHint: titled.Id, includeChangedItems: true, changeKind: CollabChangeKind.Title);
            }
            return;
        }
        if (e.Kind == ProjectChangeKind.Items && e.Item is { } removed && _index.FindItem(removed.Id) is null)
        {
            CleanupRemovedItem(removed, notifyParent: true);
        }
        else if (e.Kind == ProjectChangeKind.Projects && e.Project is { } goneProject
            && _projects.Projects.All(p => p.Id != goneProject.Id))
        {
            var removedIds = new HashSet<string>(goneProject.Items.Select(i => i.Id), StringComparer.Ordinal);
            HashSet<string>? survivingParents = null;
            foreach (var item in goneProject.Items)
            {
                if (_index.FindItem(item.Id) is null) CleanupRemovedItem(item, notifyParent: false);
                if (!string.IsNullOrEmpty(item.ParentItemId) && !removedIds.Contains(item.ParentItemId))
                {
                    (survivingParents ??= new HashSet<string>(StringComparer.Ordinal)).Add(item.ParentItemId);
                }
            }
            if (survivingParents is not null)
            {
                foreach (var parentId in survivingParents)
                {
                    RaiseChanged(parentId);
                }
            }
        }
    }

    private void CleanupRemovedItem(ProjectItem removed, bool notifyParent)
    {
        _server.RevokeToken(removed.Id);
        _cliInjector?.CleanupItem(removed.Id);
        _sessions.Remove(removed.Id);
        ClearChildApproval(removed.Id);
        // A removed session can never answer: settle every open task it owed, so its delegator stops waiting on it.
        AbortTasksForTarget(removed.Id, "The delegate session was removed before it answered.", failed: false, allOpenTasks: true, allPending: true, wakeCaller: false);

        // Cancel any unfinished tasks this removed session delegated so descendants are not orphaned.
        List<string> delegated;
        lock (_ledger.Gate)
        {
            delegated = _ledger.Tasks.Values
                .Where(t => t.CallerItemId == removed.Id && !t.IsFinished)
                .Select(t => t.TaskId)
                .ToList();
        }
        foreach (var delegatedTaskId in delegated)
        {
            CancelTaskTree(delegatedTaskId, "The delegating session was removed.", CancellationToken.None);
        }

        if (notifyParent && !string.IsNullOrEmpty(removed.ParentItemId))
        {
            RaiseChanged(removed.ParentItemId);
        }
    }

    private string FormatCallerLabel(ProjectItem item)
    {
        var agent = item.AgentId is not null ? (_catalog.Find(item.AgentId)?.Name ?? item.AgentId) : "Terminal";
        var label = string.Equals(item.DisplayTitle, agent, StringComparison.OrdinalIgnoreCase)
            ? agent
            : $"{agent} · {item.DisplayTitle}";
        // A session on another computer says where it runs, in every envelope and thread that names it. Read from the
        // sessions followed here (thread-safe) rather than by walking the projects, which belong to the UI thread.
        return _sessions.Find(item.Id) is RemoteCollabSession remote ? $"{label} @ {remote.HostName}" : label;
    }

    private static Task InvokeOnUiAsync(Action action) => CollabUi.InvokeAsync(action);

    private void OnActivityChanged(object? sender, string itemId)
    {
        if (_disposed || string.IsNullOrEmpty(itemId)) return;
        NotifyRelatedItemObservers([itemId], changedHint: itemId, includeChangedItems: false, changeKind: CollabChangeKind.Activity);
    }

    private void RaiseChanged(string? itemId)
    {
        _ledger.SchedulePersist();
        NotifyItemObservers(itemId);
        Changed?.Invoke(itemId);
    }

    private void RaiseChangedForItems(IReadOnlyCollection<string> itemIds)
    {
        _ledger.SchedulePersist();
        if (itemIds.Count == 0) return;

        HashSet<string> distinct = new(StringComparer.Ordinal);
        foreach (var itemId in itemIds)
        {
            if (!string.IsNullOrEmpty(itemId))
            {
                distinct.Add(itemId);
            }
        }
        if (distinct.Count == 0) return;

        List<Action<string?, CollabChangeKind>>? toNotify = null;
        lock (_observerGate)
        {
            if (_itemObservers.Count > 0)
            {
                foreach (var itemId in distinct)
                {
                    if (_itemObservers.TryGetValue(itemId, out var handlers))
                    {
                        (toNotify ??= []).AddRange(handlers);
                    }
                }
            }
        }

        if (toNotify is not null)
        {
            var changedHint = distinct.Count == 1 ? distinct.First() : null;
            foreach (var handler in toNotify)
            {
                handler(changedHint, CollabChangeKind.Topology);
            }
        }

        foreach (var itemId in distinct)
        {
            Changed?.Invoke(itemId);
        }
    }

    private void NotifyItemObservers(string? changedItemId)
    {
        if (string.IsNullOrEmpty(changedItemId))
        {
            List<Action<string?, CollabChangeKind>>? all;
            lock (_observerGate)
            {
                if (_itemObservers.Count == 0) return;
                all = [];
                foreach (var handlers in _itemObservers.Values)
                {
                    all.AddRange(handlers);
                }
            }
            foreach (var handler in all)
            {
                handler(null, CollabChangeKind.Topology);
            }
            return;
        }

        NotifyRelatedItemObservers([changedItemId], changedHint: changedItemId, includeChangedItems: true, changeKind: CollabChangeKind.Topology);
    }

    private void NotifyRelatedItemObservers(IReadOnlyCollection<string> changedItemIds, string? changedHint, bool includeChangedItems = true, CollabChangeKind changeKind = CollabChangeKind.Topology)
    {
        if (changedItemIds.Count == 0) return;

        lock (_observerGate)
        {
            if (_itemObservers.Count == 0) return;
        }

        var relatedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var changedItemId in changedItemIds)
        {
            if (string.IsNullOrEmpty(changedItemId)) continue;
            if (includeChangedItems)
            {
                relatedIds.Add(changedItemId);
            }
            if (_index.FindItem(changedItemId)?.ParentItemId is { Length: > 0 } parentId)
            {
                relatedIds.Add(parentId);
            }
        }

        lock (_ledger.Gate)
        {
            foreach (var changedItemId in changedItemIds)
            {
                if (!string.IsNullOrEmpty(changedItemId))
                {
                    _ledger.CollectRelatedItemsLocked(changedItemId, relatedIds);
                }
            }
        }

        if (!includeChangedItems)
        {
            foreach (var changedItemId in changedItemIds)
            {
                relatedIds.Remove(changedItemId);
            }
        }

        List<Action<string?, CollabChangeKind>>? toNotify = null;
        lock (_observerGate)
        {
            foreach (var id in relatedIds)
            {
                if (_itemObservers.TryGetValue(id, out var handlers))
                {
                    (toNotify ??= []).AddRange(handlers);
                }
            }
            foreach (var (observerId, handlers) in _itemObservers)
            {
                if (relatedIds.Contains(observerId)
                    || (!includeChangedItems && ContainsItemId(changedItemIds, observerId)))
                {
                    continue;
                }
                if (_index.FindItem(observerId) is { } observer
                    && observer.ParentItemId is { Length: > 0 } observerParentId
                    && ContainsItemId(changedItemIds, observerParentId))
                {
                    (toNotify ??= []).AddRange(handlers);
                }
            }
        }

        if (toNotify is null) return;
        foreach (var handler in toNotify)
        {
            handler(changedHint, changeKind);
        }
    }

    private static bool ContainsItemId(IReadOnlyCollection<string> itemIds, string candidate)
    {
        if (itemIds is HashSet<string> set)
        {
            return set.Contains(candidate);
        }
        foreach (var id in itemIds)
        {
            if (string.Equals(id, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static JsonObject ToolOk(JsonObject payload) => CollabToolResult.Ok(payload);

    private static JsonObject ToolError(string message) => CollabToolResult.Error(message);

    internal static CollabIsolationMode? ParseIsolation(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "shared" => CollabIsolationMode.Shared,
        "worktree" => CollabIsolationMode.Worktree,
        "read_only" or "readonly" or "plan" => CollabIsolationMode.ReadOnly,
        _ => null,
    };

    public static string IsolationToKey(CollabIsolationMode mode) => mode switch
    {
        CollabIsolationMode.Worktree => "worktree",
        CollabIsolationMode.ReadOnly => "read_only",
        _ => "shared",
    };

    private static AgentSurface? ParseSurface(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "gui" or "acp" => AgentSurface.Gui,
        "terminal" or "cli" => AgentSurface.Terminal,
        _ => null,
    };

    private static string TruncateTitle(string text, int max = 32)
    {
        var firstLine = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        return firstLine.Length <= max ? firstLine : firstLine[..max] + "…";
    }

    private static string TruncateBody(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n…(truncated)";

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
             .Replace("\"", "&quot;", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal)
             .Replace(">", "&gt;", StringComparison.Ordinal);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _projects.Changed -= OnProjectsChanged;
        _activity?.Changed -= OnActivityChanged;
        lock (_observerGate)
        {
            _itemObservers.Clear();
        }
        _index.Dispose();
        _ledger.SignalWaiters();
        _ledger.Dispose();
        _sessions.Dispose();
    }
}

internal sealed class CollabSubscription(Action unsubscribe) : IDisposable
{
    private Action? _unsubscribe = unsubscribe;

    public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
}
