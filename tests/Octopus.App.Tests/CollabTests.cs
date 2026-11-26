using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Octopus.Agents;
using Octopus.App.Collab;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Settings.Agents;
using Octopus.App.Shell;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

public sealed class CollabTests
{
    private sealed class FakeGuiBridge(string itemId, string agentId, string title, string cwd) : ICollabSessionBridge
    {
        public string ItemId => itemId;
        public string AgentId => agentId;
        public string DisplayTitle => title;
        public string WorkingDirectory => cwd;
        public bool IsBusy { get; set; }
        public bool IsConnected { get; set; } = true;
        public string? CurrentStatusDetail { get; set; }
        public List<string> DeliveredPrompts { get; } = [];
        public List<(string? ThreadId, string? TaskId)> CancelledLookups { get; } = [];
        public string Summary { get; set; } = "Recent summary from fake GUI bridge.";

        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default)
        {
            DeliveredPrompts.Add(promptText);
            return Task.FromResult(true);
        }

        public int CancelQueuedCollabPrompts(string? threadId = null, string? taskId = null)
        {
            CancelledLookups.Add((threadId, taskId));
            return 1;
        }

        public string ReadRecentSummary(int maxTurns = 5) => Summary;
    }

    [AvaloniaFact, FreshApp]
    public async Task Local_mcp_gateway_authenticates_per_session_token_and_serves_collab_tools()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-collab-mcp-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var claude = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Backend Auth", Surface = AgentSurface.Gui };
        var codexPeer = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Frontend Login", Surface = AgentSurface.Gui };
        projects.AddItem(project, claude);
        projects.AddItem(project, codexPeer, activate: false);

        var coordinator = AppHost.Get<ICollabCoordinator>();
        var server = coordinator.Server;
        Assert.True(server.Start());
        Assert.False(string.IsNullOrWhiteSpace(server.EndpointUrl));

        var token = server.IssueToken(claude.Id);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });

        // Unauthorized call without Bearer token must be rejected with 401.
        using (var unauthReq = new HttpRequestMessage(HttpMethod.Post, server.EndpointUrl)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        })
        {
            using var unauthRes = await http.SendAsync(unauthReq, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthRes.StatusCode);
        }

        // tools/list returns all 6 collaboration primitives.
        var listResp = await PostMcpAsync(http, server.EndpointUrl!, token, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", ct);
        var toolNames = listResp["result"]!["tools"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("list_peers", toolNames);
        Assert.Contains("read_session", toolNames);
        Assert.Contains("delegate_task", toolNames);
        Assert.Contains("send_message", toolNames);
        Assert.Contains("reply_thread", toolNames);
        Assert.Contains("await_events", toolNames);

        // list_peers sees the sibling Codex session in the same project.
        var peersResp = await PostMcpAsync(http, server.EndpointUrl!, token, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_peers","arguments":{}}}""", ct);
        var peersText = peersResp["result"]!["content"]![0]!["text"]!.GetValue<string>();
        var peersJson = JsonNode.Parse(peersText)!.AsObject();
        var activeIds = peersJson["active_sessions"]!.AsArray().Select(n => n!["session_id"]!.GetValue<string>()).ToList();
        Assert.Contains(claude.Id, activeIds);
        Assert.Contains(codexPeer.Id, activeIds);
    }

    [AvaloniaFact, FreshApp]
    public async Task Delegating_spawns_nested_child_under_parent_in_sidebar_with_default_shared_and_override_isolation()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-collab-tree-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var settings = AppHost.Get<ISettingsStore>();
        var project = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Main Claude", Surface = AgentSurface.Gui };
        projects.AddItem(project, parent);

        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        var parentBridge = new FakeGuiBridge(parent.Id, "claude", parent.Title, dir);
        coordinator.RegisterGuiSession(parentBridge);

        // Default global isolation must be Shared.
        Assert.Equal(CollabIsolationMode.Shared, settings.Current.Agents.Collab.DefaultIsolation);

        // Delegate to "codex" with no isolation override -> uses default Shared & nests under parent.
        var res1 = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject
            {
                ["target"] = "codex",
                ["title"] = "Write JWT tests",
                ["goal"] = "Add unit tests for JWT refresh token rotation.",
                ["surface"] = "gui",
            },
            ct);
        var body1 = JsonNode.Parse(res1["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        var codexChildId = body1["session_id"]!.GetValue<string>();
        Assert.Equal("shared", body1["isolation"]!.GetValue<string>());

        // Delegate to Chinese alias "反重力" with explicit read_only override -> resolves to agy in plan mode.
        var res2 = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject
            {
                ["target"] = "反重力",
                ["title"] = "Security Audit",
                ["goal"] = "Audit AuthController for privilege escalation.",
                ["isolation"] = "read_only",
                ["surface"] = "gui",
            },
            ct);
        var body2 = JsonNode.Parse(res2["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        var agyChildId = body2["session_id"]!.GetValue<string>();
        Assert.Equal("agy", body2["agent_id"]!.GetValue<string>());
        Assert.Equal("read_only", body2["isolation"]!.GetValue<string>());

        var agyItem = projects.FindItem(agyChildId)!;
        Assert.Equal(parent.Id, agyItem.ParentItemId);
        Assert.Equal(PermissionModes.Plan, agyItem.Launch?.Permission);

        // Verify Scheme C sidebar nests BOTH spawned children directly under the parent row (not duplicated at root).
        Frames.Pump();
        var panel = AppHost.Get<ShellViewModel>().Panel;
        Assert.Single(panel.Items);
        var parentRow = panel.Items[0];
        Assert.Equal(parent.Id, parentRow.Id);
        Assert.True(parentRow.HasSpawnedChildren);
        Assert.Equal(2, parentRow.SpawnedChildrenCount);
        Assert.Equal([codexChildId, agyChildId], parentRow.SpawnedChildren.Select(c => c.Id).ToArray());
        Assert.Equal("ro", parentRow.SpawnedChildren[1].IsolationBadge);

        // Collapsing parent children hides sub-rows while keeping count pill intact.
        parentRow.ToggleChildrenExpandedCommand.Execute(null);
        Assert.False(parentRow.ShowSpawnedChildren);
        parentRow.ToggleChildrenExpandedCommand.Execute(null);
        Assert.True(parentRow.ShowSpawnedChildren);

        // Implicit turn completion on child automatically completes the task and wakes up the parent session!
        // Re-register our spy bridge after UI pump (or inspect the real tab's queue) so wakeup delivery is deterministic.
        coordinator.RegisterGuiSession(parentBridge);
        coordinator.NotifyTurnCompleted(codexChildId, "All 14 JWT unit tests passed.");
        for (var i = 0; i < 20 && parentBridge.DeliveredPrompts.Count == 0; i++)
        {
            await Task.Delay(25, ct);
        }
        Assert.Contains(parentBridge.DeliveredPrompts, p => p.Contains("All 14 JWT unit tests passed.", StringComparison.Ordinal));
    }

    [AvaloniaFact, FreshApp]
    public void Agent_gui_shows_scheme_c_collab_ribbon_and_unified_at_mention_picker()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-collab-ui-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var claude = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Main Claude", Surface = AgentSurface.Gui };
        var codexChild = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Child Codex", Surface = AgentSurface.Gui, ParentItemId = claude.Id, CollabIsolation = "worktree" };
        var peer = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "agy", Title = "Peer Agy", Surface = AgentSurface.Gui };
        projects.AddItem(project, claude);
        projects.AddItem(project, codexChild, activate: false);
        projects.AddItem(project, peer, activate: false);

        var coordinator = AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();

        var tabRegistry = AppHost.Get<Octopus.App.Workspace.ITabContentRegistry>();
        var control = tabRegistry.GetOrCreate(project, claude);
        var vm = Assert.IsType<Octopus.App.AgentGui.AgentGuiTabViewModel>(control.DataContext);
        Frames.Pump();

        // Scheme C top Collab Ribbon shows self + direct spawned child with 'wt' badge.
        Assert.True(vm.HasCollabTopology);
        Assert.Contains(vm.CollabRibbonNodes, n => n.Kind == "self" && n.Id == claude.Id);
        Assert.Contains(vm.CollabRibbonNodes, n => n.Kind == "child" && n.Id == codexChild.Id && n.Meta == "wt");

        // Unified '@' mention popup finds active peer sessions AND installed agents by Chinese alias ("反重力").
        vm.InputText = "@Peer";
        Frames.Pump();
        Assert.True(vm.IsFilePopupOpen);
        Assert.Contains(vm.FilteredFiles, m => m.IsSession && m.TargetId == peer.Id);

        // Selecting a session mention attaches a session chip and exposes the Direct Relay route toggle.
        var sessionSuggestion = vm.FilteredFiles.First(m => m.IsSession && m.TargetId == peer.Id);
        vm.InsertFileMention(sessionSuggestion);
        Assert.True(vm.HasSessionMention);
        Assert.Contains(vm.Attachments, c => c.Attachment.Kind == "session" && c.Attachment.Path == peer.Id);
    }

    [AvaloniaFact, FreshApp]
    public async Task Cross_session_negotiation_enforces_hop_budget_and_supports_user_interjection_and_permission_bubbling()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-collab-nego-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.Agents.Collab.MaxNegotiationHops = 2);

        var project = projects.AddProject(dir);
        var claude = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Backend", Surface = AgentSurface.Gui };
        var codex = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Frontend", Surface = AgentSurface.Gui };
        projects.AddItem(project, claude);
        projects.AddItem(project, codex, activate: false);

        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        var claudeBridge = new FakeGuiBridge(claude.Id, "claude", claude.Title, dir);
        var codexBridge = new FakeGuiBridge(codex.Id, "codex", codex.Title, dir);
        coordinator.RegisterGuiSession(claudeBridge);
        coordinator.RegisterGuiSession(codexBridge);

        // Round 1: Claude sends negotiation message to existing Codex session.
        var send1 = await coordinator.CallToolAsync(
            claude.Id,
            "send_message",
            new JsonObject
            {
                ["target_session_id"] = codex.Id,
                ["title"] = "API contract",
                ["message"] = "Should refreshToken be camelCase?",
            },
            ct);
        var s1Body = JsonNode.Parse(send1["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        var threadId = s1Body["thread_id"]!.GetValue<string>();
        Assert.Equal(1, s1Body["round"]!.GetValue<int>());
        Assert.Single(codexBridge.DeliveredPrompts);

        // Round 2: Codex replies explicitly via reply_thread -> hits hop budget (2/2) and wakes Claude.
        var reply2 = await coordinator.CallToolAsync(
            codex.Id,
            "reply_thread",
            new JsonObject
            {
                ["thread_id"] = threadId,
                ["message"] = "Yes, camelCase works cleanly for web interceptors.",
                ["status"] = "continue",
            },
            ct);
        Assert.Null(reply2["isError"]);
        Assert.Single(claudeBridge.DeliveredPrompts);

        // Round 3: Exceeding hop limit is blocked by loop guard until user extends budget.
        var blocked = await coordinator.CallToolAsync(
            claude.Id,
            "send_message",
            new JsonObject
            {
                ["target_session_id"] = codex.Id,
                ["thread_id"] = threadId,
                ["message"] = "One more ping",
            },
            ct);
        Assert.True(blocked["isError"]?.GetValue<bool>());

        // User interjects and grants +4 rounds -> broadcasts to both participants and re-activates thread.
        var interjected = await coordinator.InterjectInThreadAsync(threadId, "Use camelCase and ship it.", extendBudget: true, ct);
        Assert.True(interjected);
        Assert.Equal(2, claudeBridge.DeliveredPrompts.Count);
        Assert.Equal(2, codexBridge.DeliveredPrompts.Count);
        Assert.Equal(6, coordinator.FindThread(threadId)!.MaxHops);

        // Permission bubbling: child approval surfaces on parent and resolves child callback cleanly.
        bool? approvedDecision = null;
        coordinator.ReportChildApproval(new CollabBubbledApproval(
            "apr-test",
            claude.Id,
            codex.Id,
            "codex",
            "Child Codex",
            "Bash",
            "dotnet test",
            null,
            allow => approvedDecision = allow));
        var bubbled = coordinator.GetBubbledApprovalsForParent(claude.Id);
        Assert.Single(bubbled);
        Assert.True(bubbled[0].TryRespond(allow: true));
        Assert.True(approvedDecision);
    }

    [AvaloniaFact, FreshApp]
    public void Terminal_cli_mcp_injector_plans_claude_and_codex_arguments_and_cleans_up()
    {
        var server = AppHost.Get<CollabMcpServer>();
        Assert.True(server.Start());
        var injector = AppHost.Get<CollabCliMcpInjector>();
        var catalog = AppHost.Get<IAgentCatalog>();

        // Create a dummy forwarder file so File.Exists succeeds in unit tests.
        var tmpDir = Directory.CreateTempSubdirectory("octopus-fwd-").FullName;
        var fakeForwarder = Path.Combine(tmpDir, "octopus-signal");
        File.WriteAllText(fakeForwarder, "");

        var claudeItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude CLI" };
        var claudeAttach = injector.Plan(catalog.Find("claude")!, claudeItem, fakeForwarder);
        Assert.NotNull(claudeAttach);
        Assert.Equal(2, claudeAttach!.Arguments.Count);
        Assert.Equal("--mcp-config", claudeAttach.Arguments[0]);
        Assert.True(File.Exists(claudeAttach.Arguments[1]));
        var writtenJson = File.ReadAllText(claudeAttach.Arguments[1]);
        Assert.Contains("octopus-collab", writtenJson, StringComparison.Ordinal);
        Assert.Contains("--mcp", writtenJson, StringComparison.Ordinal);

        var codexItem = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Codex CLI" };
        var codexAttach = injector.Plan(catalog.Find("codex")!, codexItem, fakeForwarder);
        Assert.NotNull(codexAttach);
        Assert.Contains(codexAttach!.Arguments, a => a.Contains("mcp_servers.octopus_collab.command=", StringComparison.Ordinal));
        Assert.True(codexAttach.Environment.ContainsKey(CollabMcpServer.TokenEnvVariable));

        // Cleanup removes temporary Claude MCP config file and revokes session token.
        var cfgPath = claudeAttach.Arguments[1];
        injector.CleanupItem(claudeItem.Id);
        Assert.False(File.Exists(cfgPath));
    }

    [AvaloniaFact, FreshApp]
    public void Global_agent_settings_expose_default_isolation_and_persist_changes()
    {
        var settings = AppHost.Get<ISettingsStore>();
        using var vm = AppHost.Get<AgentsSettingsViewModel>();

        Assert.True(vm.CollabEnabled);
        Assert.Equal(nameof(CollabIsolationMode.Shared), vm.CollabDefaultIsolationKey);
        Assert.True(vm.CollabEnableTerminalBridge);

        vm.CollabDefaultIsolationKey = nameof(CollabIsolationMode.Worktree);
        Assert.Equal(CollabIsolationMode.Worktree, settings.Current.Agents.Collab.DefaultIsolation);

        vm.CollabMaxHopsKey = "10";
        Assert.Equal(10, settings.Current.Agents.Collab.MaxNegotiationHops);
    }

    [AvaloniaFact, FreshApp]
    public async Task Await_events_suppresses_duplicate_wakeup_and_cancels_stale_queued_wakeups()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-collab-dedup-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "agy", Title = "Calculate 1+1", Surface = AgentSurface.Gui };
        projects.AddItem(project, parent);

        var coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        coordinator.Server.Start();
        var parentBridge = new FakeGuiBridge(parent.Id, "agy", parent.Title, dir);
        coordinator.RegisterGuiSession(parentBridge);

        // 1. Parent delegates task and immediately blocks in await_events.
        var delegateRes = await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject
            {
                ["target"] = "claude",
                ["title"] = "计算 1+1",
                ["goal"] = "计算 1+1=? 并直接回复结果。",
                ["surface"] = "gui",
            },
            ct);
        var delegateBody = JsonNode.Parse(delegateRes["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        var childId = delegateBody["session_id"]!.GetValue<string>();
        var threadId = delegateBody["thread_id"]!.GetValue<string>();

        coordinator.RegisterGuiSession(parentBridge);
        var awaitTask = coordinator.CallToolAsync(
            parent.Id,
            "await_events",
            new JsonObject
            {
                ["ids"] = new JsonArray(threadId),
                ["timeout_seconds"] = 10,
            },
            ct);

        // Give await_events a moment to register its waiter before the child replies.
        await Task.Delay(30, ct);

        var replyRes = await coordinator.CallToolAsync(
            childId,
            "reply_thread",
            new JsonObject
            {
                ["thread_id"] = threadId,
                ["message"] = "1 + 1 = 2",
                ["status"] = "agreed",
            },
            ct);
        Assert.Null(replyRes["isError"]);

        var awaitRes = await awaitTask;
        var awaitBody = JsonNode.Parse(awaitRes["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        Assert.Equal("agreed", awaitBody["threads"]![0]!["status"]!.GetValue<string>());
        Assert.Equal("1 + 1 = 2", awaitBody["threads"]![0]!["latest_message"]!.GetValue<string>());

        // Because await_events delivered the reply inline, no duplicate wakeup envelope was injected into parent.
        Assert.Empty(parentBridge.DeliveredPrompts);
        Assert.Contains(parentBridge.CancelledLookups, c => c.ThreadId == threadId);

        // 2. If a wakeup was queued while parent wasn't awaiting, consuming it via read_session cancels the stale queued prompt.
        var readRes = await coordinator.CallToolAsync(
            parent.Id,
            "read_session",
            new JsonObject { ["target_session_id"] = childId },
            ct);
        Assert.Null(readRes["isError"]);
        Assert.True(parentBridge.CancelledLookups.Count(c => c.ThreadId == threadId) >= 2);
    }

    [AvaloniaFact, FreshApp]
    public async Task Initial_collab_prompt_dispatches_after_off_ui_connect_without_sticking_in_input_box()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-collab-init-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);

        var fakeFactory = (FakeAcpSessionFactory)AppHost.Get<Octopus.Acp.Process.IAcpSessionFactory>();
        FakeAcpSession? created = null;
        var promptReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fakeFactory.ConfigureNext = s =>
        {
            created = s;
            // Force ApplyInitialLaunchPreferencesAsync to yield onto a thread-pool thread,
            // reproducing the real-world ConfigureAwait(false) race before initial prompt dispatch.
            s.Models = new Octopus.Acp.Protocol.AcpSessionModelState
            {
                CurrentModelId = "default-model",
                AvailableModels =
                [
                    new Octopus.Acp.Protocol.AcpModelInfo { ModelId = "default-model", Name = "Default" },
                    new Octopus.Acp.Protocol.AcpModelInfo { ModelId = "sonnet", Name = "Sonnet" },
                ],
            };
            s.SetModelHandler = async (_, _, token) => await Task.Run(() => Task.Delay(15, token), token).ConfigureAwait(false);
            s.PromptHandler = (_, text, _, _) =>
            {
                promptReceived.TrySetResult(text);
                return Task.FromResult(new Octopus.Acp.Protocol.AcpPromptResult { StopReason = "end_turn" });
            };
        };

        var collabPrompt = "[Octopus Collab Task #task_1_8a24 · Thread #t_2_a0e3 · Delegated by @Antigravity CLI · Calculate 1+1]\n"
            + "计算 1+1=? 并直接回复结果。\n\n"
            + "When finished (or if you need clarification from the delegating agent), either call `reply_thread(thread_id=\"t_2_a0e3\", message=..., status=\"agreed\"|\"continue\")` or simply conclude with a concise summary — Octopus will automatically report your final response back.";

        var child = new ProjectItem
        {
            Kind = ProjectItemKind.Agent,
            AgentId = "claude",
            Title = "计算 1+1",
            Surface = AgentSurface.Gui,
            Launch = new AgentLaunchOptions
            {
                Model = "sonnet",
                Prompt = collabPrompt,
            },
        };
        projects.AddItem(project, child);

        var tabRegistry = AppHost.Get<Octopus.App.Workspace.ITabContentRegistry>();
        var control = tabRegistry.GetOrCreate(project, child);
        var vm = Assert.IsType<Octopus.App.AgentGui.AgentGuiTabViewModel>(control.DataContext);

        await vm.ConnectAsync();
        Frames.Pump();

        var sent = await promptReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(collabPrompt, sent);
        Assert.NotNull(created);
        Assert.Single(created!.SentPrompts);
        Assert.Equal("", vm.InputText);
        // Delegated child keeps its clean title rather than overwriting it with "[Octopus Collab Task ...".
        Assert.Equal("计算 1+1", child.Title);

        // Timeline renders the injected collab task as a distinct collab strip, not a regular user bubble.
        var userTurn = Assert.IsType<Octopus.App.AgentGui.UserTurnItem>(vm.Timeline[0]);
        Assert.True(userTurn.IsCollabMessage);
        Assert.False(userTurn.IsRegularUserMessage);
        Assert.False(userTurn.CanReuse);
        Assert.StartsWith("Collab Task #task_1_8a24", userTurn.CollabHeader, StringComparison.Ordinal);
        Assert.Equal("计算 1+1=? 并直接回复结果。", userTurn.DisplayText);
    }

    [AvaloniaFact, FreshApp]
    public void User_turn_item_parses_collab_reply_and_leaves_regular_user_messages_untouched()
    {
        var regular = new Octopus.App.AgentGui.UserTurnItem("计算一下1+1=?", onReuse: _ => { });
        Assert.False(regular.IsCollabMessage);
        Assert.True(regular.IsRegularUserMessage);
        Assert.True(regular.CanReuse);
        Assert.Equal("计算一下1+1=?", regular.DisplayText);

        var wakeup = "[Octopus Collab Reply · From @Claude Code · 1+1 计算 · Thread #t_2_a0e3 (计算 1+1) · Round 2/6 · Status: agreed]\n"
            + "1 + 1 = 2\n\n"
            + "(The other session marked this thread as agreed/completed; summarize or integrate the result for the user unless further work is needed.)";
        var collab = new Octopus.App.AgentGui.UserTurnItem(wakeup, onReuse: _ => { });
        Assert.True(collab.IsCollabMessage);
        Assert.False(collab.IsRegularUserMessage);
        Assert.False(collab.CanReuse);
        Assert.StartsWith("Collab Reply · From @Claude Code", collab.CollabHeader, StringComparison.Ordinal);
        Assert.Equal("1 + 1 = 2", collab.DisplayText);
    }

    private static async Task<JsonObject> PostMcpAsync(HttpClient http, string url, string token, string json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(body)!.AsObject();
    }
}
