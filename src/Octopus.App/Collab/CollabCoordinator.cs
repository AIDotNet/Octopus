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

    /// <summary>Called when an ACP GUI session finishes a turn; triggers implicit reply fallback and caller wakeup if applicable.</summary>
    void NotifyTurnCompleted(string itemId, string? finalAssistantText);

    /// <summary>
    /// Called when a session's turn ends without an answer: stopped by the user (<paramref name="failed"/> false) or broken
    /// by an error (true). Settles any task waiting on that session and tells its delegator. With
    /// <paramref name="cancelDelegationsSince"/>, also cancels the tasks this session delegated since that moment.
    /// </summary>
    void NotifyTurnAborted(string itemId, string reason, bool failed, DateTimeOffset? cancelDelegationsSince = null);

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
    private readonly Func<ITabContentRegistry?>? _tabRegistryFactory;
    private readonly Func<IWorkspaceManager?>? _workspaceManagerFactory;

    // Every session the bus talks to (GUI tabs, terminals, conversations on other computers), reached through providers.
    private readonly CollabSessions _sessions = new();
    private readonly RemoteSessionProvider _remoteProvider;
    // Tasks, threads, open cards, owed answers, blocked await_events and read marks; persisted (collab/state.json).
    private readonly CollabLedger _ledger;
    // The MCP tools agents see; each maps its JSON arguments to one of the ICollabOperations below.
    private readonly CollabToolRegistry _tools;
    // Rules product decisions may change (wake-ups, cascades, budgets, limits); defaults follow the user's settings.
    private readonly ICollabPolicy _policy;
    private readonly Func<ICollabRemoteHosts?>? _remoteHostsFactory;
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
        _sessions.AddProvider(new TerminalSessionProvider(
            _sessions, _index, terminalSessions, tabRegistryFactory, settings, catalog, signals, activity, NotifyTurnCompleted));

        _server.AttachHandler(this);
        _projects.Changed += OnProjectsChanged;
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

    public void RegisterGuiSession(ICollabSessionBridge bridge)
    {
        _sessions.Register(bridge);
        RaiseChanged(bridge.ItemId);
    }

    public void UnregisterGuiSession(string itemId)
    {
        if (!_sessions.Unregister(itemId)) return;
        ClearChildApproval(itemId);
        // The session is gone, so whatever it was asked to answer will never be answered. No wake-up: this also runs
        // while the app shuts down, and the delegator learns the outcome from await_events or the persisted ledger.
        AbortTasksForTarget(itemId, "The delegate session was closed before it answered.", failed: false, allOpenTasks: false, wakeCaller: false);
        RaiseChanged(itemId);
    }

    public void ReportChildApproval(CollabBubbledApproval approval)
    {
        lock (_ledger.Gate)
        {
            _ledger.Approvals[(approval.ChildItemId, approval.ApprovalId)] = approval;
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
            var cleared = _ledger.Approvals
                .Where(kv => kv.Key.Child == childItemId && (approvalId is null || kv.Key.Approval == approvalId))
                .ToList();
            foreach (var (key, existing) in cleared)
            {
                parentId = existing.ParentItemId;
                _ledger.Approvals.Remove(key);
            }
            // The task waits as long as any card of that child is still open.
            if (cleared.Count > 0 && !_ledger.Approvals.Keys.Any(k => k.Child == childItemId))
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
            return _ledger.Threads.Values
                .Where(t => t.ParticipantItemIds.Contains(itemId, StringComparer.Ordinal))
                .OrderByDescending(t => t.UpdatedAt)
                .ToList();
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
            ct: ct).ConfigureAwait(false);
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
                ct: ct).ConfigureAwait(false);
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
        RaiseChanged(null);
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
        var summary = ReadSessionContent(item, maxTurns);

        CancelStaleWakeupsForPeer(callerItemId, targetId);

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
            lock (_ledger.Gate)
            {
                _ledger.Add(taskRecord);
            }
            var sent = await SendMessageInternalAsync(
                callerItemId,
                existingItem.Id,
                CollabEnvelope.TaskBody(taskId, goal, request.ContextFiles),
                threadId: threadId,
                title: title,
                expectReply: true,
                maxTurnsOverride: null,
                isDirectUserRelay: false,
                taskId: taskId,
                ct: ct).ConfigureAwait(false);
            if (!sent.Succeeded)
            {
                lock (_ledger.Gate)
                {
                    _ledger.Tasks.Remove(taskId);
                }
                RaiseChanged(callerItemId);
                return ToolError(sent.Error ?? "Failed to deliver task to existing session.");
            }
            CollabTaskStatus currentStatus;
            lock (_ledger.Gate)
            {
                currentStatus = _ledger.Tasks.TryGetValue(taskId, out var current) ? current.Status : taskRecord.Status;
            }
            RaiseChanged(callerItemId);
            return ToolOk(new JsonObject
            {
                ["task_id"] = taskId,
                ["session_id"] = existingItem.Id,
                ["thread_id"] = sent.Thread?.ThreadId ?? threadId,
                ["reused_existing_session"] = true,
                ["status"] = currentStatus.ToString().ToLowerInvariant(),
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
                    termView.EnsureStarted();
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

        lock (_ledger.Gate)
        {
            _ledger.Add(thread);
            _ledger.Add(record);
            _ledger.EnqueuePending(childItem.Id, new PendingExpectation(callerItemId, taskIdNew, threadIdNew, DateTimeOffset.UtcNow));
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
                termView.EnsureStarted();
            }
        }).ConfigureAwait(false);

        RaiseChanged(callerItemId);
        RaiseChanged(childItem.Id);

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
            ["status"] = "running",
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
            callerItemId, targetId, message, threadId, title, expectReply, maxTurns, isDirectUserRelay: false, ct: ct).ConfigureAwait(false);

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

    public async Task<JsonObject> ReplyThreadAsync(string callerItemId, string threadId, string message, string status, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(message))
        {
            return ToolError("Both 'thread_id' and 'message' are required.");
        }
        if (status is not ("continue" or "agreed" or "blocked")) status = "continue";

        var result = await RecordReplyAndWakeAsync(callerItemId, threadId, message.Trim(), status, isImplicitFallback: false, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return ToolError(result.Error ?? "Failed to reply to thread.");
        }

        var payload = new JsonObject
        {
            ["thread_id"] = threadId,
            ["round"] = result.Round,
            ["status"] = status,
            ["recorded"] = true,
            ["delivered_to"] = result.Delivered ? result.RecipientItemId : null,
            ["wakeup_delivered"] = result.Delivered,
        };
        if (result.RecipientItemId is not null && !result.Delivered)
        {
            payload["recipient_id"] = result.RecipientItemId;
            payload["note"] = "Reply recorded in thread, but wakeup prompt could not be delivered to the recipient session.";
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
            lock (_ledger.Gate)
            {
                snap = BuildAwaitSnapshotLocked(callerItemId, watched);
                var done = snap.HasUpdate || !snap.HasPending || DateTimeOffset.UtcNow >= deadline;
                if (done)
                {
                    _ledger.MarkSeen(callerItemId, snap.Reported);
                }
                else
                {
                    // Registered under the same lock as the snapshot, so no change can slip in between.
                    waiter = new EventWaiter(
                        callerItemId,
                        watched,
                        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                    _ledger.Waiters.Add(waiter);
                }
            }

            if (waiter is null)
            {
                return FinishAwait(callerItemId, snap);
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

    private JsonObject FinishAwait(string callerItemId, AwaitSnapshot snap)
    {
        if (snap.HasUpdate)
        {
            CancelStaleWakeups(callerItemId, snap.UpdatedThreadIds, snap.UpdatedTaskIds);
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
        List<string> UpdatedThreadIds,
        List<string> UpdatedTaskIds);

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
        List<string> updatedThreads = [];
        List<string> updatedTasks = [];
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
                updatedTasks.Add(t.TaskId);
                if (t.ThreadId is { Length: > 0 } tid) updatedThreads.Add(tid);
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
                updatedThreads.Add(th.ThreadId);
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
        return new AwaitSnapshot(payload, hasUpdate, hasPending, reported, updatedThreads, updatedTasks);
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

        var cancelled = await CancelTaskTreeAsync(
            taskId,
            string.IsNullOrWhiteSpace(reason) ? "Cancelled by the delegating agent." : reason,
            ct).ConfigureAwait(false);
        return ToolOk(new JsonObject
        {
            ["task_id"] = taskId,
            ["status"] = "cancelled",
            ["cancelled_count"] = cancelled,
        });
    }

    private void CancelStaleWakeups(string callerItemId, IReadOnlyCollection<string> threadIds, IReadOnlyCollection<string> taskIds)
    {
        if (_sessions.Find(callerItemId) is not { } gui) return;
        foreach (var threadId in threadIds.Distinct(StringComparer.Ordinal))
        {
            gui.CancelQueuedCollabPrompts(threadId: threadId);
        }
        foreach (var taskId in taskIds.Distinct(StringComparer.Ordinal))
        {
            gui.CancelQueuedCollabPrompts(taskId: taskId);
        }
    }

    private void CancelStaleWakeupsForPeer(string callerItemId, string peerItemId)
    {
        if (_sessions.Find(callerItemId) is not { } gui) return;
        List<string> threadIds = [];
        List<string> taskIds = [];
        lock (_ledger.Gate)
        {
            foreach (var t in _ledger.Tasks.Values)
            {
                if (t.CallerItemId == callerItemId
                    && t.TargetItemId == peerItemId
                    && t.Status is CollabTaskStatus.Completed or CollabTaskStatus.Failed or CollabTaskStatus.Cancelled)
                {
                    taskIds.Add(t.TaskId);
                    if (t.ThreadId is { Length: > 0 } tid) threadIds.Add(tid);
                }
            }
            foreach (var th in _ledger.Threads.Values)
            {
                if (th.ParticipantItemIds.Contains(callerItemId, StringComparer.Ordinal)
                    && th.ParticipantItemIds.Contains(peerItemId, StringComparer.Ordinal)
                    && th.Messages.LastOrDefault()?.FromItemId == peerItemId)
                {
                    threadIds.Add(th.ThreadId);
                }
            }
        }
        CancelStaleWakeups(callerItemId, threadIds, taskIds);
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
        string? taskId = null,
        CancellationToken ct = default)
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
        lock (_ledger.Gate)
        {
            if (!string.IsNullOrWhiteSpace(threadId) && _ledger.Threads.TryGetValue(threadId, out var existing))
            {
                thread = existing;
                if (!thread.ParticipantItemIds.Contains(callerItemId, StringComparer.Ordinal)) thread.ParticipantItemIds.Add(callerItemId);
                if (!thread.ParticipantItemIds.Contains(targetItemId, StringComparer.Ordinal)) thread.ParticipantItemIds.Add(targetItemId);
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
            }

            if (thread.Hops >= thread.MaxHops)
            {
                _ledger.SetThreadStatus(thread, CollabThreadStatus.PausedBudget);
                RaiseChanged(callerItemId);
                return (false, thread, $"Negotiation thread '{thread.ThreadId}' reached its hop budget ({thread.Hops}/{thread.MaxHops}). Ask the user to review or extend the thread.");
            }

            thread.Hops++;
            round = thread.Hops;
            thread.Status = CollabThreadStatus.Active;
            _ledger.AppendMessage(thread, new CollabMessageRecord
            {
                MessageId = _ledger.NextId("msg"),
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

            foreach (var task in _ledger.Tasks.Values)
            {
                if (task.ThreadId == thread.ThreadId && task.TargetItemId == targetItemId && task.Status == CollabTaskStatus.Completed)
                {
                    _ledger.SetTaskStatus(task, CollabTaskStatus.Running, "Follow-up sent");
                }
            }
        }

        var targetIsRemote = _index.FindProjectOf(targetItemId) is { } tp && ProjectLocation.IsOnRemoteHost(tp);
        var envelope = CollabEnvelope.Message(thread.ThreadId, callerLabel, round, thread.MaxHops, message, expectReply, targetIsRemote);
        var delivered = await DeliverEnvelopeAsync(targetItemId, envelope, thread.ThreadId, taskId: taskId, expectReply, callerItemId, ct).ConfigureAwait(false);
        if (!delivered)
        {
            return (false, thread, $"Could not deliver message to '{targetLabel}' (session not running).");
        }

        _ledger.SignalWaiters();
        RaiseChanged(callerItemId);
        RaiseChanged(targetItemId);
        return (true, thread, null);
    }

    private async Task<(bool Succeeded, int Round, string? RecipientItemId, bool Delivered, string? Error)> RecordReplyAndWakeAsync(
        string responderItemId,
        string threadId,
        string message,
        string status,
        bool isImplicitFallback,
        CancellationToken ct)
    {
        var responderItem = _index.FindItem(responderItemId);
        var responderLabel = responderItem is not null ? FormatCallerLabel(responderItem) : responderItemId;
        string? recipientId = null;
        int round;
        int maxHops;
        string threadTitle;

        var callerActivelyAwaiting = false;
        lock (_ledger.Gate)
        {
            if (!_ledger.Threads.TryGetValue(threadId, out var thread))
            {
                return (false, 0, null, false, $"Unknown collaboration thread '{threadId}'.");
            }
            if (!thread.ParticipantItemIds.Contains(responderItemId, StringComparer.Ordinal))
            {
                return (false, 0, null, false, $"You are not a participant of thread '{threadId}'.");
            }
            // Clear pending implicit expectation so an explicit reply_thread won't fire again at end_turn.
            if (!isImplicitFallback)
            {
                _ledger.RemovePendingByThread(responderItemId, threadId);
            }

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

            List<string> matchingTaskIds = [];
            foreach (var task in _ledger.Tasks.Values)
            {
                if (task.ThreadId == threadId && task.TargetItemId == responderItemId && !task.IsFinished)
                {
                    _ledger.SetTaskStatus(
                        task,
                        status == "blocked" ? CollabTaskStatus.Failed : CollabTaskStatus.Completed,
                        isImplicitFallback ? "Turn ended (reported automatically)" : status == "agreed" ? "Agreed & completed" : "Replied to parent",
                        TruncateBody(message, _policy.MaxReplyChars));
                    matchingTaskIds.Add(task.TaskId);
                }
            }

            // If the recipient is already blocked in await_events watching this thread/task,
            // _ledger.SignalWaiters() will deliver the reply inline — skip queueing a duplicate wakeup turn.
            if (recipientId is not null)
            {
                callerActivelyAwaiting = _ledger.Waiters.Any(w =>
                    w.CallerItemId == recipientId && w.Matches(threadId, matchingTaskIds));
            }
        }

        _ledger.SignalWaiters();
        RaiseChanged(responderItemId);
        if (recipientId is not null) RaiseChanged(recipientId);

        var delivered = false;
        if (recipientId is not null)
        {
            if (callerActivelyAwaiting)
            {
                delivered = true;
            }
            else if (_policy.WakeCallerOnReply)
            {
                var wakeup = CollabEnvelope.Reply(
                    threadId, threadTitle, responderLabel, round, maxHops, status, TruncateBody(message, _policy.MaxReplyChars),
                    autoReportedFromSessionId: isImplicitFallback ? responderItemId : null);
                var shouldExpectFollowUp = status == "continue" && round < maxHops;
                delivered = await DeliverEnvelopeAsync(recipientId, wakeup, threadId, taskId: null, expectReply: shouldExpectFollowUp, callerItemId: responderItemId, ct).ConfigureAwait(false);
            }
        }

        return (true, round, recipientId, delivered, null);
    }

    public void NotifyTurnCompleted(string itemId, string? finalAssistantText)
    {
        PendingExpectation? pending;
        lock (_ledger.Gate)
        {
            if (!_ledger.TryDequeuePending(itemId, out pending)) return;
        }

        var summary = string.IsNullOrWhiteSpace(finalAssistantText)
            ? "(Target session finished its turn with no final text output.)"
            : TruncateBody(finalAssistantText.Trim(), _policy.MaxReplyChars);

        if (pending.ThreadId is { } tid)
        {
            _ = Task.Run(() => RecordReplyAndWakeAsync(itemId, tid, summary, status: _policy.ImplicitReplyStatus, isImplicitFallback: true, CancellationToken.None));
            return;
        }

        if (pending.TaskId is { } taskId)
        {
            lock (_ledger.Gate)
            {
                if (_ledger.Tasks.TryGetValue(taskId, out var task) && !task.IsFinished)
                {
                    _ledger.SetTaskStatus(task, CollabTaskStatus.Completed, "Completed", summary);
                }
            }
            _ledger.SignalWaiters();
            RaiseChanged(pending.CallerItemId);
            RaiseChanged(itemId);
        }
    }

    private async Task<bool> DeliverEnvelopeAsync(
        string targetItemId,
        string envelope,
        string? threadId,
        string? taskId,
        bool expectReply,
        string callerItemId,
        CancellationToken ct)
    {
        if (_index.FindItem(targetItemId) is not { } targetItem || _index.FindProjectOf(targetItemId) is not { } targetProject) return false;
        if (await _sessions.EnsureAsync(targetProject, targetItem, ct).ConfigureAwait(false) is not { } bridge) return false;
        PendingExpectation? expectation = null;
        if (expectReply)
        {
            expectation = new PendingExpectation(callerItemId, taskId, threadId, DateTimeOffset.UtcNow);
            lock (_ledger.Gate)
            {
                _ledger.EnqueuePending(targetItemId, expectation);
            }
        }
        var delivered = await bridge.DeliverCollabPromptAsync(envelope, threadId, taskId, ct).ConfigureAwait(false);
        if (!delivered && expectation is not null)
        {
            lock (_ledger.Gate)
            {
                _ledger.RemovePendingExact(targetItemId, expectation);
            }
        }
        return delivered;
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
            // Set before following the session, so an answer already in its first snapshot is taken as this task's.
            _ledger.EnqueuePending(created.Id, new PendingExpectation(callerItemId, taskId, threadId, DateTimeOffset.UtcNow));
        }

        var followed = await _remoteProvider.FollowAsync(host, hostName, project, created.Id, definition.Id, title, callerItemId, fresh: true, ct).ConfigureAwait(false);
        if (!followed)
        {
            AbortTasksForTarget(created.Id, $"Started {definition.Name} on {hostName}, but could not follow its conversation; check it on that computer.", failed: true, allOpenTasks: false, wakeCaller: false);
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

    public void NotifyTurnAborted(string itemId, string reason, bool failed, DateTimeOffset? cancelDelegationsSince = null)
    {
        AbortTasksForTarget(itemId, reason, failed, allOpenTasks: false, wakeCaller: true);

        if (cancelDelegationsSince is not { } since || !_policy.CancelDelegationsWhenStopped) return;
        List<string> delegated;
        lock (_ledger.Gate)
        {
            delegated = _ledger.Tasks.Values
                .Where(t => t.CallerItemId == itemId && !t.IsFinished && t.CreatedAt >= since)
                .Select(t => t.TaskId)
                .ToList();
        }
        if (delegated.Count == 0) return;
        _ = Task.Run(async () =>
        {
            foreach (var taskId in delegated)
            {
                await CancelTaskTreeAsync(taskId, "The delegating session's turn was stopped by the user.", CancellationToken.None).ConfigureAwait(false);
            }
        });
    }

    public async Task<bool> CancelTaskAsync(string taskId, string reason, CancellationToken ct = default) =>
        await CancelTaskTreeAsync(taskId, reason, ct).ConfigureAwait(false) > 0;

    /// <summary>
    /// Settles the tasks that <paramref name="targetItemId"/> owed an answer: the one it was last asked about
    /// (<paramref name="allOpenTasks"/> false) or every open one (true, used when the session is removed).
    /// </summary>
    private void AbortTasksForTarget(string targetItemId, string reason, bool failed, bool allOpenTasks, bool wakeCaller)
    {
        var newStatus = failed ? CollabTaskStatus.Failed : CollabTaskStatus.Cancelled;
        var threadStatus = failed ? CollabThreadStatus.Blocked : CollabThreadStatus.Cancelled;
        List<(string CallerId, string? ThreadId, string Title, int Round, int MaxHops, bool Awaiting)> notify = [];
        var responderLabel = _index.FindItem(targetItemId) is { } responder ? FormatCallerLabel(responder) : targetItemId;

        lock (_ledger.Gate)
        {
            var drained = _ledger.TakePendingForAbort(targetItemId, allOpenTasks);
            if (drained.Count == 0 && !allOpenTasks) return;

            foreach (var task in _ledger.Tasks.Values)
            {
                if (task.TargetItemId != targetItemId || task.IsFinished) continue;
                var owed = allOpenTasks
                    || drained.Any(p =>
                        (p.TaskId is not null && task.TaskId == p.TaskId)
                        || (p.ThreadId is not null && task.ThreadId == p.ThreadId));
                if (!owed) continue;
                _ledger.SetTaskStatus(task, newStatus, failed ? "Failed" : "Stopped", reason);
            }

            // Close the thread(s) the target owed a reply on, recording why, so the other side sees the outcome.
            var threadIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in drained)
            {
                if (p.ThreadId is { } pendingThread) threadIds.Add(pendingThread);
            }
            if (allOpenTasks)
            {
                foreach (var th in _ledger.Threads.Values)
                {
                    if (!th.IsClosed && th.ParticipantItemIds.Contains(targetItemId, StringComparer.Ordinal)) threadIds.Add(th.ThreadId);
                }
            }
            foreach (var threadId in threadIds)
            {
                if (!_ledger.Threads.TryGetValue(threadId, out var thread) || thread.IsClosed) continue;
                var callerId = drained.FirstOrDefault(p => p.ThreadId == threadId)?.CallerItemId
                    ?? ResolveReplyRecipientLocked(thread, targetItemId);
                thread.Status = threadStatus;
                _ledger.AppendMessage(thread, new CollabMessageRecord
                {
                    MessageId = _ledger.NextId("msg"),
                    ThreadId = threadId,
                    FromItemId = targetItemId,
                    FromLabel = responderLabel,
                    ToItemId = callerId,
                    Round = thread.Hops,
                    Text = reason,
                    Status = threadStatus.ToKey(),
                });
                if (callerId is not null && callerId != "user")
                {
                    var awaiting = _ledger.Waiters.Any(w => w.CallerItemId == callerId && w.Matches(threadId, []));
                    notify.Add((callerId, threadId, thread.Title, thread.Hops, thread.MaxHops, awaiting));
                }
            }
        }

        _ledger.SignalWaiters();
        RaiseChanged(targetItemId);
        foreach (var n in notify)
        {
            RaiseChanged(n.CallerId);
            if (!wakeCaller || n.Awaiting || !_policy.WakeCallerOnReply) continue;
            var envelope = CollabEnvelope.Reply(n.ThreadId!, n.Title, responderLabel, n.Round, n.MaxHops, threadStatus.ToKey(), reason);
            _ = Task.Run(() => DeliverEnvelopeAsync(n.CallerId, envelope, n.ThreadId, taskId: null, expectReply: false, callerItemId: targetItemId, CancellationToken.None));
        }
    }

    /// <summary>Cancels <paramref name="rootTaskId"/> and everything its (spawned) delegate delegated further. Returns how many tasks were cancelled.</summary>
    private Task<int> CancelTaskTreeAsync(string rootTaskId, string reason, CancellationToken ct) =>
        Task.FromResult(CancelTaskTree(rootTaskId, reason, ct));

    private int CancelTaskTree(string rootTaskId, string reason, CancellationToken ct)
    {
        var count = 0;
        var queue = new Queue<(string TaskId, string Reason)>();
        queue.Enqueue((rootTaskId, reason));
        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (queue.TryDequeue(out var next))
        {
            if (!visited.Add(next.TaskId)) continue;
            CollabTaskRecord task;
            bool interrupt;
            List<string> nested;
            lock (_ledger.Gate)
            {
                if (!_ledger.Tasks.TryGetValue(next.TaskId, out var found) || found.IsFinished) continue;
                task = found;
                _ledger.SetTaskStatus(task, CollabTaskStatus.Cancelled, "Cancelled", next.Reason);

                var wasOwed = _ledger.RemovePendingMatching(task.TargetItemId, task.TaskId, task.ThreadId);

                if (task.ThreadId is { } tid && _ledger.Threads.TryGetValue(tid, out var thread) && !thread.IsClosed)
                {
                    _ledger.SetThreadStatus(thread, CollabThreadStatus.Cancelled);
                }

                // A spawned child exists only for this task: stop it and everything it delegated. A pre-existing session
                // that was merely asked something is only interrupted if it is still working on that request.
                var isDedicatedChild = _index.FindItem(task.TargetItemId)?.ParentItemId == task.CallerItemId;
                interrupt = wasOwed || isDedicatedChild;
                nested = isDedicatedChild
                    ? _ledger.Tasks.Values.Where(t => t.CallerItemId == task.TargetItemId && !t.IsFinished).Select(t => t.TaskId).ToList()
                    : [];
            }

            count++;
            foreach (var id in nested) queue.Enqueue((id, "Its delegating task was cancelled."));
            ct.ThrowIfCancellationRequested();
            StopDelegate(task, interrupt);
            RaiseChanged(task.CallerItemId);
            RaiseChanged(task.TargetItemId);
        }

        if (count > 0) _ledger.SignalWaiters();
        return count;
    }

    private void StopDelegate(CollabTaskRecord task, bool interrupt)
    {
        if (_index.FindItem(task.TargetItemId) is not { } item || _index.FindProjectOf(task.TargetItemId) is not { } project) return;
        if (item.ParentItemId == task.CallerItemId && item.Launch is not null)
        {
            item.Launch.Prompt = null;
        }
        if (_sessions.TryAttach(project, item) is not { } bridge) return;
        bridge.CancelQueuedCollabPrompts(task.ThreadId, task.TaskId);
        if (!interrupt) return;
        // Don't wait out the agent's cancel grace period inside an MCP call.
        _ = Task.Run(async () =>
        {
            try { await bridge.CancelActiveTurnAsync().ConfigureAwait(false); }
            catch { /* best effort */ }
        }, CancellationToken.None);
    }

    /* ==================== Helpers ==================== */

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
        if (_sessions.Find(itemId) is { } gui)
        {
            lock (_ledger.Gate)
            {
                if (_ledger.Approvals.Any(kv => kv.Key.Child == itemId && !kv.Value.IsResolved)) return "waiting_approval";
            }
            return gui.IsBusy ? "working" : (gui.IsConnected ? "idle" : "disconnected");
        }
        return (_activity?.Of(itemId) ?? ItemActivity.Idle) switch
        {
            ItemActivity.Working => "working",
            ItemActivity.Waiting => "waiting_approval",
            ItemActivity.Done => "done",
            ItemActivity.Error => "error",
            _ => "idle",
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
        if (e.Kind == ProjectChangeKind.Items && e.Item is { } removed && _index.FindItem(removed.Id) is null)
        {
            _cliInjector?.CleanupItem(removed.Id);
            _sessions.Remove(removed.Id);
            ClearChildApproval(removed.Id);
            // A removed session can never answer: settle every open task it owed, so its delegator stops waiting on it.
            AbortTasksForTarget(removed.Id, "The delegate session was removed before it answered.", failed: false, allOpenTasks: true, wakeCaller: false);
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

    private void RaiseChanged(string? itemId)
    {
        _ledger.SchedulePersist();
        Changed?.Invoke(itemId);
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
        _index.Dispose();
        _ledger.SignalWaiters();
        _ledger.Dispose();
        _sessions.Dispose();
    }
}
