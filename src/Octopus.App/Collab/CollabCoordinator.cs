using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Threading;
using Octopus.Agents;
using Octopus.Agents.Signals;
using Octopus.App.Activity;
using Octopus.App.Services;
using Octopus.App.Services.Workspaces;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
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

    /// <summary>Reports a live permission approval request from a spawned child agent so its parent can approve in-place.</summary>
    void ReportChildApproval(CollabBubbledApproval approval);

    /// <summary>Clears any pending bubbled approval for <paramref name="childItemId"/> (resolved or cancelled).</summary>
    void ClearChildApproval(string childItemId, string? approvalId = null);

    IReadOnlyList<CollabBubbledApproval> GetBubbledApprovalsForParent(string parentItemId);

    /// <summary>Lists peer sessions visible from <paramref name="callerItemId"/> (current project or merged group).</summary>
    IReadOnlyList<CollabPeerInfo> ListPeers(string callerItemId, string scope = "group");

    /// <summary>Lists installed agents eligible for delegation (with search aliases such as <c>agy</c> / <c>反重力</c>).</summary>
    IReadOnlyList<CollabAvailableAgentInfo> ListAvailableAgents();

    /// <summary>Lists direct child <see cref="ProjectItem"/>s spawned by <paramref name="parentItemId"/>.</summary>
    IReadOnlyList<ProjectItem> GetSpawnedChildren(string parentItemId);

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

public sealed class CollabCoordinator : ICollabCoordinator, ICollabToolHandler, IDisposable
{
    private const int MaxReplySummaryChars = 3500;
    private const int MaxTerminalReadChars = 4000;

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
    private readonly IAgentCatalog _catalog;
    private readonly IAgentScanner _scanner;
    private readonly ISettingsStore _settings;
    private readonly CollabMcpServer _server;
    private readonly CollabCliMcpInjector? _cliInjector;
    private readonly IItemActivityService? _activity;
    private readonly ISignalHub? _signals;
    private readonly TerminalSessionRegistry? _terminalSessions;
    private readonly Func<ITabContentRegistry?>? _tabRegistryFactory;
    private readonly Func<IWorkspaceManager?>? _workspaceManagerFactory;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, ICollabSessionBridge> _guiSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CollabTaskRecord> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CollabThreadRecord> _threads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CollabBubbledApproval> _childApprovals = new(StringComparer.Ordinal);
    // Tracks pending implicit-reply target per session so finishing a turn without calling reply_thread still replies cleanly.
    private readonly Dictionary<string, PendingExpectation> _pendingExpectations = new(StringComparer.Ordinal);
    private readonly List<EventWaiter> _eventWaiters = [];
    private readonly IDisposable? _signalSub;
    private int _seq;
    private bool _disposed;

    private sealed record EventWaiter(
        string CallerItemId,
        HashSet<string> FilterIds,
        TaskCompletionSource<bool> Tcs)
    {
        public bool Matches(string threadId, IReadOnlyCollection<string> taskIds)
        {
            if (FilterIds.Count == 0) return true;
            if (FilterIds.Contains(threadId)) return true;
            foreach (var taskId in taskIds)
            {
                if (FilterIds.Contains(taskId)) return true;
            }
            return false;
        }
    }

    private sealed record PendingExpectation(
        string CallerItemId,
        string? TaskId,
        string? ThreadId,
        int TerminalBaselineLength,
        DateTimeOffset DispatchedAt);

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
        Func<IWorkspaceManager?>? workspaceManagerFactory = null)
    {
        _projects = projects;
        _catalog = catalog;
        _scanner = scanner;
        _settings = settings;
        _server = server;
        _cliInjector = cliInjector;
        _activity = activity;
        _signals = signals;
        _terminalSessions = terminalSessions;
        _tabRegistryFactory = tabRegistryFactory;
        _workspaceManagerFactory = workspaceManagerFactory;

        _server.AttachHandler(this);
        _projects.Changed += OnProjectsChanged;
        _signalSub = _signals?.Subscribe(null, null, OnTerminalSignal);
    }

    public event Action<string?>? Changed;

    public CollabMcpServer Server => _server;

    public bool IsEnabled => _settings.Current.Agents.Collab.Enabled && _server.IsRunning;

    public AcpMcpServerConfig? BuildAcpMcpServer(string itemId)
    {
        if (!IsEnabled) return null;
        return _server.BuildAcpServerConfig(itemId, ItemLaunchService.ResolveForwarderPath());
    }

    public void RegisterGuiSession(ICollabSessionBridge bridge)
    {
        _guiSessions[bridge.ItemId] = bridge;
        RaiseChanged(bridge.ItemId);
    }

    public void UnregisterGuiSession(string itemId)
    {
        _guiSessions.TryRemove(itemId, out _);
        ClearChildApproval(itemId);
        RaiseChanged(itemId);
    }

    public void ReportChildApproval(CollabBubbledApproval approval)
    {
        lock (_gate)
        {
            _childApprovals[approval.ChildItemId] = approval;
            foreach (var task in _tasks.Values)
            {
                if (task.TargetItemId == approval.ChildItemId && task.Status == CollabTaskStatus.Running)
                {
                    task.Status = CollabTaskStatus.WaitingApproval;
                    task.LatestActivity = $"Waiting approval: {approval.ToolTitle}";
                }
            }
        }
        SignalEventWaiters();
        RaiseChanged(approval.ParentItemId);
    }

    public void ClearChildApproval(string childItemId, string? approvalId = null)
    {
        string? parentId = null;
        lock (_gate)
        {
            if (_childApprovals.TryGetValue(childItemId, out var existing)
                && (approvalId is null || existing.ApprovalId == approvalId))
            {
                parentId = existing.ParentItemId;
                _childApprovals.Remove(childItemId);
                foreach (var task in _tasks.Values)
                {
                    if (task.TargetItemId == childItemId && task.Status == CollabTaskStatus.WaitingApproval)
                    {
                        task.Status = CollabTaskStatus.Running;
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
        lock (_gate)
        {
            return _childApprovals.Values
                .Where(a => a.ParentItemId == parentItemId && !a.IsResolved)
                .ToList();
        }
    }

    public IReadOnlyList<ProjectItem> GetSpawnedChildren(string parentItemId)
    {
        var project = _projects.FindProjectOf(parentItemId);
        if (project is null) return [];
        return project.Items
            .Where(i => string.Equals(i.ParentItemId, parentItemId, StringComparison.Ordinal))
            .ToList();
    }

    public IReadOnlyList<CollabThreadRecord> GetThreadsForItem(string itemId)
    {
        lock (_gate)
        {
            return _threads.Values
                .Where(t => t.ParticipantItemIds.Contains(itemId, StringComparer.Ordinal))
                .OrderByDescending(t => t.UpdatedAt)
                .ToList();
        }
    }

    public IReadOnlyList<CollabTaskRecord> GetTasksForItem(string itemId)
    {
        lock (_gate)
        {
            return _tasks.Values
                .Where(t => t.CallerItemId == itemId || t.TargetItemId == itemId)
                .OrderByDescending(t => t.CreatedAt)
                .ToList();
        }
    }

    public CollabThreadRecord? FindThread(string threadId)
    {
        lock (_gate)
        {
            return _threads.GetValueOrDefault(threadId);
        }
    }

    public IReadOnlyList<CollabPeerInfo> ListPeers(string callerItemId, string scope = "group")
    {
        var visibleProjects = ResolveVisibleProjects(callerItemId, scope);
        var result = new List<CollabPeerInfo>();
        foreach (var project in visibleProjects)
        {
            foreach (var item in project.Items)
            {
                if (item.Kind is not (ProjectItemKind.Agent or ProjectItemKind.Terminal)) continue;
                var agentId = item.AgentId ?? "terminal";
                var agentName = item.AgentId is not null ? (_catalog.Find(item.AgentId)?.Name ?? item.AgentId) : "Terminal";
                var surface = item.Kind == ProjectItemKind.Agent && item.Surface == AgentSurface.Gui ? "gui" : "terminal";
                var status = DescribeStatus(item.Id);
                var cwd = string.IsNullOrWhiteSpace(item.WorkingDirectory) ? project.Path : item.WorkingDirectory!;
                var branch = item.WorkspaceId is { } wid ? _projects.FindWorkspace(project, wid)?.Branch : null;
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
        return result;
    }

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
                var item = _projects.FindItem(m.TargetId);
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
        var labels = targets.Select(id => _projects.FindItem(id) is { } item ? "@" + FormatCallerLabel(item) : id).ToList();
        // Every delivery counts as a hop; widen the new thread's budget so the fan-out itself does not eat the replies'.
        var defaultMaxHops = Math.Clamp(_settings.Current.Agents.Collab.MaxNegotiationHops, CollabSettings.MinNegotiationHops, CollabSettings.MaxAllowedNegotiationHops);
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
        lock (_gate)
        {
            if (!_threads.TryGetValue(threadId, out var thread)) return false;
            if (extendBudget)
            {
                thread.MaxHops = Math.Min(CollabSettings.MaxAllowedNegotiationHops, thread.MaxHops + 4);
                if (thread.Status == "paused_budget") thread.Status = "active";
            }
            round = thread.Hops;
            thread.Messages.Add(new CollabMessageRecord
            {
                MessageId = NextId("msg"),
                ThreadId = threadId,
                FromItemId = "user",
                FromLabel = "User",
                Round = round,
                Text = text.Trim(),
                IsUserInterjection = true,
            });
            thread.UpdatedAt = DateTimeOffset.UtcNow;
            participants = [.. thread.ParticipantItemIds];
        }

        var envelope = $"[Octopus Collab · User Interjection · Thread #{threadId}]\n{text.Trim()}\n(Please follow the user's guidance and continue or conclude the thread via reply_thread.)";
        foreach (var pid in participants)
        {
            await DeliverEnvelopeAsync(pid, envelope, threadId, taskId: null, expectReply: false, callerItemId: "user", ct).ConfigureAwait(false);
        }
        SignalEventWaiters();
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

        return toolName switch
        {
            "list_peers" => HandleListPeers(callerItemId, arguments),
            "read_session" => HandleReadSession(callerItemId, arguments),
            "delegate_task" => await HandleDelegateTaskAsync(callerItemId, arguments, cancellationToken).ConfigureAwait(false),
            "send_message" => await HandleSendMessageAsync(callerItemId, arguments, cancellationToken).ConfigureAwait(false),
            "reply_thread" => await HandleReplyThreadAsync(callerItemId, arguments, cancellationToken).ConfigureAwait(false),
            "await_events" => await HandleAwaitEventsAsync(callerItemId, arguments, cancellationToken).ConfigureAwait(false),
            _ => ToolError($"Unknown collaboration tool: {toolName}"),
        };
    }

    private JsonObject HandleListPeers(string callerItemId, JsonObject? args)
    {
        var scope = Str(args, "scope") ?? "group";
        var peers = ListPeers(callerItemId, scope);
        var agents = ListAvailableAgents();

        var peersArr = new JsonArray();
        foreach (var p in peers)
        {
            peersArr.Add((JsonNode)new JsonObject
            {
                ["session_id"] = p.ItemId,
                ["is_self"] = p.ItemId == callerItemId,
                ["agent_id"] = p.AgentId,
                ["agent_name"] = p.AgentName,
                ["title"] = p.Title,
                ["surface"] = p.Surface,
                ["status"] = p.Status,
                ["working_directory"] = p.WorkingDirectory,
                ["branch"] = p.Branch,
                ["parent_session_id"] = p.ParentItemId,
                ["isolation"] = p.Isolation,
                ["summary"] = p.Summary,
            });
        }

        var agentsArr = new JsonArray();
        foreach (var a in agents)
        {
            var aliasArr = new JsonArray();
            foreach (var al in a.Aliases) aliasArr.Add((JsonNode)JsonValue.Create(al));
            agentsArr.Add((JsonNode)new JsonObject
            {
                ["agent_id"] = a.AgentId,
                ["name"] = a.Name,
                ["supports_acp"] = a.SupportsAcp,
                ["default_surface"] = a.DefaultSurface,
                ["aliases"] = aliasArr,
            });
        }

        var payload = new JsonObject
        {
            ["caller_session_id"] = callerItemId,
            ["default_isolation"] = IsolationToKey(_settings.Current.Agents.Collab.DefaultIsolation),
            ["active_sessions"] = peersArr,
            ["available_agents"] = agentsArr,
        };
        return ToolOk(payload);
    }

    private JsonObject HandleReadSession(string callerItemId, JsonObject? args)
    {
        var targetId = Str(args, "target_session_id");
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return ToolError("target_session_id is required.");
        }
        if (!IsPeerVisible(callerItemId, targetId))
        {
            return ToolError($"Session '{targetId}' is not found in your current project or merged workspace group.");
        }

        var maxTurns = Math.Clamp(Int(args, "max_turns") ?? 5, 1, 20);
        var item = _projects.FindItem(targetId)!;
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

    private async Task<JsonObject> HandleDelegateTaskAsync(string callerItemId, JsonObject? args, CancellationToken ct)
    {
        var target = Str(args, "target");
        var goal = Str(args, "goal");
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(goal))
        {
            return ToolError("Both 'target' and 'goal' are required.");
        }

        var collabCfg = _settings.Current.Agents.Collab;
        var depth = ComputeDelegationDepth(callerItemId);
        if (depth >= collabCfg.MaxDelegationDepth)
        {
            return ToolError($"Delegation depth limit ({collabCfg.MaxDelegationDepth}) reached; complete the task directly or ask the user.");
        }

        // Case 1: Target matches an existing visible session id -> route directly into that session.
        if (_projects.FindItem(target) is { } existingItem && IsPeerVisible(callerItemId, existingItem.Id))
        {
            if (existingItem.Id == callerItemId)
            {
                return ToolError("Cannot delegate a task to yourself.");
            }
            var title = Str(args, "title") ?? TruncateTitle(goal);
            var taskId = NextId("task");
            var sent = await SendMessageInternalAsync(
                callerItemId,
                existingItem.Id,
                FormatTaskBody(taskId, goal, ReadStringList(args, "context_files")),
                threadId: null,
                title: title,
                expectReply: true,
                maxTurnsOverride: null,
                isDirectUserRelay: false,
                ct).ConfigureAwait(false);
            if (!sent.Succeeded)
            {
                return ToolError(sent.Error ?? "Failed to deliver task to existing session.");
            }
            var taskRecord = new CollabTaskRecord
            {
                TaskId = taskId,
                CallerItemId = callerItemId,
                TargetItemId = existingItem.Id,
                TargetAgentId = existingItem.AgentId ?? "terminal",
                Title = title,
                Goal = goal,
                Isolation = CollabIsolationMode.Shared,
                ThreadId = sent.Thread?.ThreadId,
                Status = CollabTaskStatus.Running,
                LatestActivity = "Dispatched to existing session",
            };
            lock (_gate) { _tasks[taskId] = taskRecord; }
            RaiseChanged(callerItemId);
            return ToolOk(new JsonObject
            {
                ["task_id"] = taskId,
                ["session_id"] = existingItem.Id,
                ["thread_id"] = sent.Thread?.ThreadId,
                ["reused_existing_session"] = true,
                ["status"] = "running",
            });
        }

        // Case 2: Spawn a new child agent session nested under callerItemId.
        var agentId = ResolveAgentAlias(target);
        if (agentId is null || _catalog.Find(agentId) is not { } definition)
        {
            return ToolError($"Unknown target session or agent '{target}'. Call list_peers to see valid session_ids and available agents.");
        }

        var callerProject = _projects.FindProjectOf(callerItemId);
        var callerItem = _projects.FindItem(callerItemId);
        if (callerProject is null || callerItem is null)
        {
            return ToolError("Caller session project could not be resolved.");
        }
        if (!ProjectLocation.CanRunAgents(callerProject))
        {
            return ToolError("Agents cannot be spawned inside this remote/SSH project.");
        }

        var isolation = ParseIsolation(Str(args, "isolation")) ?? collabCfg.DefaultIsolation;
        var isolationKey = IsolationToKey(isolation);
        var requestedSurface = ParseSurface(Str(args, "surface"));
        var resolvedSurface = AgentSurfaceResolver.Resolve(
            definition,
            _settings.Current.Agents,
            requestedSurface,
            acpAvailable: ProjectLocation.IsOnThisComputer(callerProject));

        if (resolvedSurface == AgentSurface.Terminal && !collabCfg.EnableTerminalBridge)
        {
            return ToolError($"Agent '{definition.Name}' resolves to Terminal mode, but Terminal CLI collaboration bridge is disabled in settings.");
        }

        var taskIdNew = NextId("task");
        var threadIdNew = NextId("t");
        var taskTitle = Str(args, "title")?.Trim();
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
        var contextFiles = ReadStringList(args, "context_files");
        var initialPrompt = BuildDelegationPrompt(taskIdNew, threadIdNew, callerLabel, isolation, worktreeBranch, goal, contextFiles);

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
                Model = Str(args, "model"),
                Effort = Str(args, "effort"),
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
            MaxHops = Math.Clamp(collabCfg.MaxNegotiationHops, CollabSettings.MinNegotiationHops, CollabSettings.MaxAllowedNegotiationHops),
            Messages =
            [
                new CollabMessageRecord
                {
                    MessageId = NextId("msg"),
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

        lock (_gate)
        {
            _threads[threadIdNew] = thread;
            _tasks[taskIdNew] = record;
            _pendingExpectations[childItem.Id] = new PendingExpectation(callerItemId, taskIdNew, threadIdNew, 0, DateTimeOffset.UtcNow);
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

    private async Task<JsonObject> HandleSendMessageAsync(string callerItemId, JsonObject? args, CancellationToken ct)
    {
        var targetId = Str(args, "target_session_id");
        var message = Str(args, "message");
        if (string.IsNullOrWhiteSpace(targetId) || string.IsNullOrWhiteSpace(message))
        {
            return ToolError("Both 'target_session_id' and 'message' are required.");
        }
        var threadId = Str(args, "thread_id");
        var title = Str(args, "title");
        var expectReply = Bool(args, "expect_reply") ?? true;
        var maxTurns = Int(args, "max_turns");

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

    private async Task<JsonObject> HandleReplyThreadAsync(string callerItemId, JsonObject? args, CancellationToken ct)
    {
        var threadId = Str(args, "thread_id");
        var message = Str(args, "message");
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(message))
        {
            return ToolError("Both 'thread_id' and 'message' are required.");
        }
        var status = (Str(args, "status") ?? "continue").Trim().ToLowerInvariant();
        if (status is not ("continue" or "agreed" or "blocked")) status = "continue";

        var result = await RecordReplyAndWakeAsync(callerItemId, threadId, message.Trim(), status, isImplicitFallback: false, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return ToolError(result.Error ?? "Failed to reply to thread.");
        }

        return ToolOk(new JsonObject
        {
            ["thread_id"] = threadId,
            ["round"] = result.Round,
            ["status"] = status,
            ["delivered_to"] = result.RecipientItemId,
        });
    }

    private async Task<JsonObject> HandleAwaitEventsAsync(string callerItemId, JsonObject? args, CancellationToken ct)
    {
        var ids = new HashSet<string>(ReadStringList(args, "ids"), StringComparer.Ordinal);
        var timeoutSec = Math.Clamp(Int(args, "timeout_seconds") ?? 25, 1, 45);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSec);

        while (true)
        {
            var snapshot = BuildAwaitSnapshot(callerItemId, ids, out var hasFinishedOrReplied, out var consumedThreadIds, out var consumedTaskIds);
            if (hasFinishedOrReplied || DateTimeOffset.UtcNow >= deadline)
            {
                if (hasFinishedOrReplied)
                {
                    CancelStaleWakeups(callerItemId, consumedThreadIds, consumedTaskIds);
                }
                return ToolOk(snapshot);
            }

            EventWaiter waiter;
            lock (_gate)
            {
                waiter = new EventWaiter(
                    callerItemId,
                    ids,
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                _eventWaiters.Add(waiter);
            }
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                lock (_gate) { _eventWaiters.Remove(waiter); }
                return ToolOk(snapshot);
            }
            try
            {
                await waiter.Tcs.Task.WaitAsync(remaining, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                var finalSnap = BuildAwaitSnapshot(callerItemId, ids, out var timedOutWithUpdate, out var finalThreads, out var finalTasks);
                if (timedOutWithUpdate)
                {
                    CancelStaleWakeups(callerItemId, finalThreads, finalTasks);
                }
                return ToolOk(finalSnap);
            }
            finally
            {
                lock (_gate) { _eventWaiters.Remove(waiter); }
            }
        }
    }

    private JsonObject BuildAwaitSnapshot(
        string callerItemId,
        HashSet<string> filterIds,
        out bool hasTerminalUpdate,
        out List<string> consumedThreadIds,
        out List<string> consumedTaskIds)
    {
        hasTerminalUpdate = false;
        consumedThreadIds = [];
        consumedTaskIds = [];
        var tasksArr = new JsonArray();
        var threadsArr = new JsonArray();

        lock (_gate)
        {
            foreach (var t in _tasks.Values)
            {
                if (t.CallerItemId != callerItemId) continue;
                if (filterIds.Count > 0 && !filterIds.Contains(t.TaskId) && (t.ThreadId is null || !filterIds.Contains(t.ThreadId))) continue;
                if (t.Status is CollabTaskStatus.Completed or CollabTaskStatus.Failed or CollabTaskStatus.Cancelled)
                {
                    hasTerminalUpdate = true;
                    consumedTaskIds.Add(t.TaskId);
                    if (t.ThreadId is { Length: > 0 } tid) consumedThreadIds.Add(tid);
                }
                tasksArr.Add((JsonNode)new JsonObject
                {
                    ["task_id"] = t.TaskId,
                    ["session_id"] = t.TargetItemId,
                    ["agent_id"] = t.TargetAgentId,
                    ["title"] = t.Title,
                    ["status"] = t.Status.ToString().ToLowerInvariant(),
                    ["latest_activity"] = t.LatestActivity,
                    ["result"] = t.ResultSummary,
                });
            }

            foreach (var th in _threads.Values)
            {
                if (!th.ParticipantItemIds.Contains(callerItemId, StringComparer.Ordinal)) continue;
                if (filterIds.Count > 0 && !filterIds.Contains(th.ThreadId)) continue;
                var latest = th.Messages.LastOrDefault();
                if (latest is not null && latest.FromItemId != callerItemId)
                {
                    hasTerminalUpdate = true;
                    consumedThreadIds.Add(th.ThreadId);
                }
                threadsArr.Add((JsonNode)new JsonObject
                {
                    ["thread_id"] = th.ThreadId,
                    ["title"] = th.Title,
                    ["status"] = th.Status,
                    ["hops"] = th.Hops,
                    ["max_hops"] = th.MaxHops,
                    ["latest_from"] = latest?.FromLabel,
                    ["latest_message"] = latest?.Text,
                });
            }
        }

        return new JsonObject
        {
            ["tasks"] = tasksArr,
            ["threads"] = threadsArr,
        };
    }

    private void CancelStaleWakeups(string callerItemId, IReadOnlyCollection<string> threadIds, IReadOnlyCollection<string> taskIds)
    {
        if (!_guiSessions.TryGetValue(callerItemId, out var gui)) return;
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
        if (!_guiSessions.TryGetValue(callerItemId, out var gui)) return;
        List<string> threadIds = [];
        List<string> taskIds = [];
        lock (_gate)
        {
            foreach (var t in _tasks.Values)
            {
                if (t.CallerItemId == callerItemId
                    && t.TargetItemId == peerItemId
                    && t.Status is CollabTaskStatus.Completed or CollabTaskStatus.Failed or CollabTaskStatus.Cancelled)
                {
                    taskIds.Add(t.TaskId);
                    if (t.ThreadId is { Length: > 0 } tid) threadIds.Add(tid);
                }
            }
            foreach (var th in _threads.Values)
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
        CancellationToken ct)
    {
        if (callerItemId == targetItemId)
        {
            return (false, null, "Cannot send a collaboration message to yourself.");
        }
        if (!IsPeerVisible(callerItemId, targetItemId))
        {
            return (false, null, $"Target session '{targetItemId}' is not visible in this project/group.");
        }

        var callerItem = _projects.FindItem(callerItemId);
        var targetItem = _projects.FindItem(targetItemId);
        if (callerItem is null || targetItem is null)
        {
            return (false, null, "Source or target session no longer exists.");
        }

        var callerLabel = isDirectUserRelay ? $"User (via {FormatCallerLabel(callerItem)})" : FormatCallerLabel(callerItem);
        var targetLabel = FormatCallerLabel(targetItem);
        var defaultMaxHops = Math.Clamp(_settings.Current.Agents.Collab.MaxNegotiationHops, CollabSettings.MinNegotiationHops, CollabSettings.MaxAllowedNegotiationHops);

        CollabThreadRecord thread;
        int round;
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(threadId) && _threads.TryGetValue(threadId, out var existing))
            {
                thread = existing;
                if (!thread.ParticipantItemIds.Contains(callerItemId, StringComparer.Ordinal)) thread.ParticipantItemIds.Add(callerItemId);
                if (!thread.ParticipantItemIds.Contains(targetItemId, StringComparer.Ordinal)) thread.ParticipantItemIds.Add(targetItemId);
            }
            else
            {
                var newId = string.IsNullOrWhiteSpace(threadId) ? NextId("t") : threadId;
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
                _threads[newId] = thread;
            }

            if (thread.Hops >= thread.MaxHops)
            {
                thread.Status = "paused_budget";
                RaiseChanged(callerItemId);
                return (false, thread, $"Negotiation thread '{thread.ThreadId}' reached its hop budget ({thread.Hops}/{thread.MaxHops}). Ask the user to review or extend the thread.");
            }

            thread.Hops++;
            round = thread.Hops;
            thread.Status = "active";
            thread.Messages.Add(new CollabMessageRecord
            {
                MessageId = NextId("msg"),
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
            thread.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var envelope = BuildMessageEnvelope(thread.ThreadId, callerLabel, round, thread.MaxHops, message, expectReply);
        var delivered = await DeliverEnvelopeAsync(targetItemId, envelope, thread.ThreadId, taskId: null, expectReply, callerItemId, ct).ConfigureAwait(false);
        if (!delivered)
        {
            return (false, thread, $"Could not deliver message to '{targetLabel}' (session not running).");
        }

        SignalEventWaiters();
        RaiseChanged(callerItemId);
        RaiseChanged(targetItemId);
        return (true, thread, null);
    }

    private async Task<(bool Succeeded, int Round, string? RecipientItemId, string? Error)> RecordReplyAndWakeAsync(
        string responderItemId,
        string threadId,
        string message,
        string status,
        bool isImplicitFallback,
        CancellationToken ct)
    {
        var responderItem = _projects.FindItem(responderItemId);
        var responderLabel = responderItem is not null ? FormatCallerLabel(responderItem) : responderItemId;
        string? recipientId = null;
        int round;
        int maxHops;
        string threadTitle;

        var callerActivelyAwaiting = false;
        lock (_gate)
        {
            if (!_threads.TryGetValue(threadId, out var thread))
            {
                return (false, 0, null, $"Unknown collaboration thread '{threadId}'.");
            }
            // Clear pending implicit expectation so an explicit reply_thread won't fire again at end_turn.
            if (!isImplicitFallback && _pendingExpectations.TryGetValue(responderItemId, out var pending) && pending.ThreadId == threadId)
            {
                _pendingExpectations.Remove(responderItemId);
            }

            if (thread.Hops >= thread.MaxHops && status == "continue")
            {
                thread.Status = "paused_budget";
            }
            else
            {
                thread.Status = status == "agreed" ? "agreed" : status == "blocked" ? "blocked" : "active";
            }

            thread.Hops++;
            round = thread.Hops;
            maxHops = thread.MaxHops;
            threadTitle = thread.Title;
            recipientId = thread.ParticipantItemIds.FirstOrDefault(id => id != responderItemId);

            thread.Messages.Add(new CollabMessageRecord
            {
                MessageId = NextId("msg"),
                ThreadId = threadId,
                FromItemId = responderItemId,
                FromLabel = responderLabel,
                FromAgentId = responderItem?.AgentId,
                ToItemId = recipientId,
                ToLabel = recipientId is not null && _projects.FindItem(recipientId) is { } rec ? FormatCallerLabel(rec) : null,
                Round = round,
                Text = TruncateBody(message, MaxReplySummaryChars),
                Status = status,
            });
            thread.UpdatedAt = DateTimeOffset.UtcNow;

            List<string> matchingTaskIds = [];
            foreach (var task in _tasks.Values)
            {
                if (task.ThreadId == threadId && task.TargetItemId == responderItemId)
                {
                    task.Status = status == "blocked" ? CollabTaskStatus.Failed : CollabTaskStatus.Completed;
                    task.ResultSummary = TruncateBody(message, MaxReplySummaryChars);
                    task.LatestActivity = status == "agreed" ? "Agreed & completed" : "Replied to parent";
                    task.CompletedAt = DateTimeOffset.UtcNow;
                    matchingTaskIds.Add(task.TaskId);
                }
            }

            // If the recipient is already blocked in await_events watching this thread/task,
            // SignalEventWaiters() will deliver the reply inline — skip queueing a duplicate wakeup turn.
            if (recipientId is not null)
            {
                callerActivelyAwaiting = _eventWaiters.Any(w =>
                    w.CallerItemId == recipientId && w.Matches(threadId, matchingTaskIds));
            }
        }

        SignalEventWaiters();
        RaiseChanged(responderItemId);
        if (recipientId is not null) RaiseChanged(recipientId);

        if (recipientId is not null && !callerActivelyAwaiting && _settings.Current.Agents.Collab.AutoWakeupCaller)
        {
            var wakeup = BuildWakeupEnvelope(threadId, threadTitle, responderLabel, round, maxHops, status, TruncateBody(message, MaxReplySummaryChars));
            var shouldExpectFollowUp = status == "continue" && round < maxHops;
            await DeliverEnvelopeAsync(recipientId, wakeup, threadId, taskId: null, expectReply: shouldExpectFollowUp, callerItemId: responderItemId, ct).ConfigureAwait(false);
        }

        return (true, round, recipientId, null);
    }

    public void NotifyTurnCompleted(string itemId, string? finalAssistantText)
    {
        PendingExpectation? pending;
        lock (_gate)
        {
            if (!_pendingExpectations.TryGetValue(itemId, out pending)) return;
            _pendingExpectations.Remove(itemId);
        }

        var summary = string.IsNullOrWhiteSpace(finalAssistantText)
            ? "(Target session finished its turn with no final text output.)"
            : TruncateBody(finalAssistantText.Trim(), MaxReplySummaryChars);

        if (pending.ThreadId is { } tid)
        {
            _ = Task.Run(() => RecordReplyAndWakeAsync(itemId, tid, summary, status: "agreed", isImplicitFallback: true, CancellationToken.None));
            return;
        }

        if (pending.TaskId is { } taskId)
        {
            lock (_gate)
            {
                if (_tasks.TryGetValue(taskId, out var task))
                {
                    task.Status = CollabTaskStatus.Completed;
                    task.ResultSummary = summary;
                    task.LatestActivity = "Completed";
                    task.CompletedAt = DateTimeOffset.UtcNow;
                }
            }
            SignalEventWaiters();
            RaiseChanged(pending.CallerItemId);
            RaiseChanged(itemId);
        }
    }

    private void OnTerminalSignal(AgentSignal signal)
    {
        if (string.IsNullOrWhiteSpace(signal.ItemId)) return;
        var item = _projects.FindItem(signal.ItemId);
        if (item is null || item.Surface == AgentSurface.Gui) return;
        var def = item.AgentId is not null ? _catalog.Find(item.AgentId) : null;
        if (def is null) return;
        var mapped = SignalStatusMap.Resolve(def, signal);
        if (mapped != SignalStatus.Done) return;

        PendingExpectation? pending;
        lock (_gate)
        {
            if (!_pendingExpectations.TryGetValue(signal.ItemId, out pending)) return;
        }
        // Extract new terminal output delta after baseline and treat as implicit terminal turn completion.
        var fullText = _terminalSessions?.Find(signal.ItemId)?.ReadText() ?? "";
        var delta = pending.TerminalBaselineLength < fullText.Length
            ? fullText[pending.TerminalBaselineLength..].Trim()
            : fullText.Trim();
        if (delta.Length > MaxReplySummaryChars)
        {
            delta = "…\n" + delta[^MaxReplySummaryChars..];
        }
        NotifyTurnCompleted(signal.ItemId, string.IsNullOrWhiteSpace(delta) ? "Terminal session reported turn completion." : delta);
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
        var targetItem = _projects.FindItem(targetItemId);
        var targetProject = _projects.FindProjectOf(targetItemId);
        if (targetItem is null || targetProject is null) return false;

        // Ensure the tab control/runtime exists if it was never opened yet.
        if (!_guiSessions.ContainsKey(targetItemId) && targetItem.Surface == AgentSurface.Gui)
        {
            await InvokeOnUiAsync(() => _tabRegistryFactory?.Invoke()?.GetOrCreate(targetProject, targetItem)).ConfigureAwait(false);
        }

        if (_guiSessions.TryGetValue(targetItemId, out var guiBridge))
        {
            if (expectReply)
            {
                lock (_gate)
                {
                    _pendingExpectations[targetItemId] = new PendingExpectation(callerItemId, taskId, threadId, 0, DateTimeOffset.UtcNow);
                }
            }
            return await guiBridge.DeliverCollabPromptAsync(envelope, threadId, taskId, ct).ConfigureAwait(false);
        }

        // Terminal PTY bridge path.
        if (!_settings.Current.Agents.Collab.EnableTerminalBridge || _terminalSessions is null)
        {
            return false;
        }

        var term = _terminalSessions.Find(targetItemId);
        if (term is null)
        {
            await InvokeOnUiAsync(() => _tabRegistryFactory?.Invoke()?.GetOrCreate(targetProject, targetItem)).ConfigureAwait(false);
            // Wait briefly for the terminal tab to attach & spawn its PTY.
            for (var i = 0; i < 15 && term is null; i++)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                term = _terminalSessions.Find(targetItemId);
            }
        }
        if (term is null || !term.IsRunning) return false;

        var baselineLen = term.ReadText().Length;
        if (expectReply)
        {
            lock (_gate)
            {
                _pendingExpectations[targetItemId] = new PendingExpectation(callerItemId, taskId, threadId, baselineLen, DateTimeOffset.UtcNow);
            }
        }

        // Collapse newlines into clean single submission or wrap in bracketed paste when supported by the CLI TUI.
        var singleBlock = envelope.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var modes = term.CachedInputModes;
        if (modes.BracketedPaste)
        {
            term.Send($"\x1b[200~{singleBlock}\x1b[201~");
            await Task.Delay(60, ct).ConfigureAwait(false);
            term.Send("\r");
        }
        else
        {
            var inline = singleBlock.Replace("\n", " ", StringComparison.Ordinal);
            term.Send(inline + "\r");
        }
        return true;
    }

    /* ==================== Helpers ==================== */

    private IReadOnlyList<Project> ResolveVisibleProjects(string callerItemId, string scope)
    {
        var current = _projects.FindProjectOf(callerItemId) ?? _projects.Current;
        if (current is null) return [];
        if (string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase) || current.GroupId is null)
        {
            return [current];
        }
        var group = _projects.Groups.FirstOrDefault(g => g.Id == current.GroupId);
        if (group is null || !group.MergeWorkspaces)
        {
            return [current];
        }
        return _projects.Projects
            .Where(p => p.GroupId == group.Id && ProjectLocation.IsOnThisComputer(p))
            .ToList();
    }

    private bool IsPeerVisible(string callerItemId, string targetItemId)
    {
        var visibleProjects = ResolveVisibleProjects(callerItemId, "group");
        return visibleProjects.Any(p => p.Items.Any(i => i.Id == targetItemId));
    }

    private int ComputeDelegationDepth(string itemId)
    {
        var depth = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cur = _projects.FindItem(itemId);
        while (cur?.ParentItemId is { Length: > 0 } parentId && seen.Add(parentId) && depth < 16)
        {
            depth++;
            cur = _projects.FindItem(parentId);
        }
        return depth;
    }

    private string DescribeStatus(string itemId)
    {
        if (_guiSessions.TryGetValue(itemId, out var gui))
        {
            lock (_gate)
            {
                if (_childApprovals.TryGetValue(itemId, out var apr) && !apr.IsResolved) return "waiting_approval";
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
        lock (_gate)
        {
            var task = _tasks.Values.LastOrDefault(t => t.TargetItemId == itemId);
            if (!string.IsNullOrWhiteSpace(task?.LatestActivity)) return task.LatestActivity;
        }
        if (_guiSessions.TryGetValue(itemId, out var gui) && !string.IsNullOrWhiteSpace(gui.CurrentStatusDetail))
        {
            return gui.CurrentStatusDetail;
        }
        return null;
    }

    private string ReadSessionContent(ProjectItem item, int maxTurns)
    {
        if (_guiSessions.TryGetValue(item.Id, out var gui))
        {
            return gui.ReadRecentSummary(maxTurns);
        }
        if (_terminalSessions?.Find(item.Id) is { } term)
        {
            var text = term.ReadText().Trim();
            if (text.Length == 0) return "(Terminal buffer is currently empty.)";
            return text.Length > MaxTerminalReadChars ? "…\n" + text[^MaxTerminalReadChars..] : text;
        }
        return "(Session is not active in memory yet.)";
    }

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (e.Kind == ProjectChangeKind.Items && e.Item is { } removed && _projects.FindItem(removed.Id) is null)
        {
            _cliInjector?.CleanupItem(removed.Id);
            _guiSessions.TryRemove(removed.Id, out _);
            ClearChildApproval(removed.Id);
            lock (_gate)
            {
                _pendingExpectations.Remove(removed.Id);
            }
            RaiseChanged(removed.ParentItemId);
        }
    }

    private string FormatCallerLabel(ProjectItem item)
    {
        var agent = item.AgentId is not null ? (_catalog.Find(item.AgentId)?.Name ?? item.AgentId) : "Terminal";
        return string.Equals(item.DisplayTitle, agent, StringComparison.OrdinalIgnoreCase)
            ? agent
            : $"{agent} · {item.DisplayTitle}";
    }

    private static string BuildDelegationPrompt(
        string taskId,
        string threadId,
        string callerLabel,
        CollabIsolationMode isolation,
        string? worktreeBranch,
        string goal,
        IReadOnlyList<string> contextFiles)
    {
        var sb = new StringBuilder();
        sb.Append("[Octopus Collab Task #").Append(taskId)
          .Append(" · Thread #").Append(threadId)
          .Append(" · Delegated by @").Append(callerLabel);
        if (worktreeBranch is not null)
        {
            sb.Append(" · Git Worktree: ").Append(worktreeBranch);
        }
        else if (isolation == CollabIsolationMode.ReadOnly)
        {
            sb.Append(" · Read-Only Mode");
        }
        sb.AppendLine("]");
        sb.AppendLine(goal.Trim());
        if (contextFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Relevant files:");
            foreach (var f in contextFiles) sb.Append("- ").AppendLine(f);
        }
        sb.AppendLine();
        sb.Append("When finished (or if you need clarification from the delegating agent), either call `reply_thread(thread_id=\"")
          .Append(threadId)
          .Append("\", message=..., status=\"agreed\"|\"continue\")` or simply conclude with a concise summary — Octopus will automatically report your final response back.");
        return sb.ToString();
    }

    private static string BuildMessageEnvelope(string threadId, string senderLabel, int round, int maxHops, string message, bool expectReply)
    {
        var sb = new StringBuilder();
        sb.Append("[Octopus Collab · From @").Append(senderLabel)
          .Append(" · Thread #").Append(threadId)
          .Append(" · Round ").Append(round).Append('/').Append(maxHops).AppendLine("]");
        sb.AppendLine(message.Trim());
        if (expectReply)
        {
            sb.AppendLine();
            sb.Append("(Reply via `reply_thread(thread_id=\"").Append(threadId)
              .Append("\", message=..., status=\"continue\"|\"agreed\")` or conclude this turn with your answer for automatic relay.)");
        }
        return sb.ToString();
    }

    private static string BuildWakeupEnvelope(string threadId, string threadTitle, string responderLabel, int round, int maxHops, string status, string message)
    {
        var sb = new StringBuilder();
        sb.Append("[Octopus Collab Reply · From @").Append(responderLabel)
          .Append(" · Thread #").Append(threadId).Append(" (").Append(threadTitle).Append(')')
          .Append(" · Round ").Append(round).Append('/').Append(maxHops)
          .Append(" · Status: ").Append(status).AppendLine("]");
        sb.AppendLine(message);
        if (status == "agreed")
        {
            sb.AppendLine();
            sb.Append("(The other session marked this thread as agreed/completed; summarize or integrate the result for the user unless further work is needed.)");
        }
        return sb.ToString();
    }

    private static string FormatTaskBody(string taskId, string goal, IReadOnlyList<string> contextFiles)
    {
        var sb = new StringBuilder();
        sb.Append("[Task #").Append(taskId).AppendLine("]");
        sb.AppendLine(goal.Trim());
        if (contextFiles.Count > 0)
        {
            sb.AppendLine("Relevant files: " + string.Join(", ", contextFiles));
        }
        return sb.ToString();
    }

    private static Task InvokeOnUiAsync(Action action)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    private void SignalEventWaiters()
    {
        List<EventWaiter> copy;
        lock (_gate)
        {
            if (_eventWaiters.Count == 0) return;
            copy = [.. _eventWaiters];
        }
        foreach (var w in copy) w.Tcs.TrySetResult(true);
    }

    private void RaiseChanged(string? itemId) => Changed?.Invoke(itemId);

    private string NextId(string prefix) => $"{prefix}_{Interlocked.Increment(ref _seq)}_{Guid.NewGuid().ToString("N")[..4]}";

    private static JsonObject ToolOk(JsonObject payload) => new()
    {
        ["content"] = new JsonArray
        {
            (JsonNode)new JsonObject
            {
                ["type"] = "text",
                ["text"] = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
            },
        },
    };

    private static JsonObject ToolError(string message) => new()
    {
        ["isError"] = true,
        ["content"] = new JsonArray
        {
            (JsonNode)new JsonObject { ["type"] = "text", ["text"] = message },
        },
    };

    private static string? Str(JsonObject? obj, string key) =>
        obj is not null && obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonObject? obj, string key) =>
        obj is not null && obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;

    private static bool? Bool(JsonObject? obj, string key) =>
        obj is not null && obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static List<string> ReadStringList(JsonObject? obj, string key)
    {
        var list = new List<string>();
        if (obj is not null && obj.TryGetPropertyValue(key, out var node) && node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                {
                    list.Add(s.Trim());
                }
            }
        }
        return list;
    }

    private static CollabIsolationMode? ParseIsolation(string? raw) => raw?.Trim().ToLowerInvariant() switch
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
        _signalSub?.Dispose();
        SignalEventWaiters();
    }
}
