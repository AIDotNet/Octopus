using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.Acp.Protocol;
using Octopus.Agents;
using Octopus.App.Activity;
using Octopus.App.AgentGui;
using Octopus.App.Collab;
using Octopus.App.Collab.Ledger;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

public sealed class CollabRibbonInvalidationTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(5);

    private sealed class RibbonObserver : IDisposable
    {
        private readonly AgentGuiTabViewModel _vm;

        public RibbonObserver(AgentGuiTabViewModel vm)
        {
            _vm = vm;
            _vm.CollabRibbonNodes.CollectionChanged += OnRibbonChanged;
            _vm.BubbledApprovals.CollectionChanged += OnApprovalsChanged;
            _vm.PropertyChanged += OnPropertyChanged;
        }

        public int RibbonResets, RibbonNodeAdds, ApprovalResets, ApprovalAdds;
        public int HasTopologyNotifies, HasApprovalsNotifies, ActiveThreadNotifies;

        public int TotalInvalidationSignals =>
            RibbonResets + RibbonNodeAdds + ApprovalResets + ApprovalAdds + HasTopologyNotifies + HasApprovalsNotifies + ActiveThreadNotifies;

        public void ResetCounters() =>
            RibbonResets = RibbonNodeAdds = ApprovalResets = ApprovalAdds = HasTopologyNotifies = HasApprovalsNotifies = ActiveThreadNotifies = 0;

        private void OnRibbonChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) Interlocked.Increment(ref RibbonResets);
            else if (e.Action == NotifyCollectionChangedAction.Add) Interlocked.Add(ref RibbonNodeAdds, e.NewItems?.Count ?? 1);
        }

        private void OnApprovalsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) Interlocked.Increment(ref ApprovalResets);
            else if (e.Action == NotifyCollectionChangedAction.Add) Interlocked.Add(ref ApprovalAdds, e.NewItems?.Count ?? 1);
        }

        private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(AgentGuiTabViewModel.HasCollabTopology): Interlocked.Increment(ref HasTopologyNotifies); break;
                case nameof(AgentGuiTabViewModel.HasBubbledApprovals): Interlocked.Increment(ref HasApprovalsNotifies); break;
                case nameof(AgentGuiTabViewModel.ActiveCollabThread): Interlocked.Increment(ref ActiveThreadNotifies); break;
            }
        }

        public void Dispose()
        {
            _vm.CollabRibbonNodes.CollectionChanged -= OnRibbonChanged;
            _vm.BubbledApprovals.CollectionChanged -= OnApprovalsChanged;
            _vm.PropertyChanged -= OnPropertyChanged;
        }
    }

    private sealed class RibbonTestFixture : IAsyncDisposable
    {
        private readonly List<string> _tempDirs = [];
        private readonly List<RibbonObserver> _observers = [];

        public RibbonTestFixture()
        {
            Projects = AppHost.Get<IProjectService>();
            TabRegistry = AppHost.Get<ITabContentRegistry>();
            Coordinator = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
            Factory = AppHost.Get<FakeAcpSessionFactory>();
            Settings = AppHost.Get<ISettingsStore>();
            Coordinator.Server.Start();
        }

        public IProjectService Projects { get; }
        public ITabContentRegistry TabRegistry { get; }
        public CollabCoordinator Coordinator { get; }
        public FakeAcpSessionFactory Factory { get; }
        public ISettingsStore Settings { get; }

        public Project AddProject(string prefix)
        {
            var dir = Directory.CreateTempSubdirectory(prefix).FullName;
            _tempDirs.Add(dir);
            return Projects.AddProject(dir);
        }

        public (ProjectItem Item, AgentGuiTabViewModel Vm) CreateGuiTab(
            Project project, string title, string? parentItemId = null, string? isolation = null)
        {
            var item = new ProjectItem
            {
                Kind = ProjectItemKind.Agent,
                AgentId = "opencode",
                Title = title,
                Surface = AgentSurface.Gui,
                ParentItemId = parentItemId,
                CollabIsolation = isolation,
            };
            Projects.AddItem(project, item, activate: false);
            return (item, FindGuiVm(project, item.Id));
        }

        public AgentGuiTabViewModel FindGuiVm(Project project, string itemId) =>
            Assert.IsType<AgentGuiTabViewModel>(TabRegistry.GetOrCreate(project, Projects.FindItem(itemId)!).DataContext);

        public RibbonObserver Observe(AgentGuiTabViewModel vm)
        {
            var obs = new RibbonObserver(vm);
            _observers.Add(obs);
            return obs;
        }

        public async Task<JsonObject> CallToolOkAsync(string callerId, string tool, JsonObject args, CancellationToken ct)
        {
            var result = await Coordinator.CallToolAsync(callerId, tool, args, ct);
            Assert.Null(result["isError"]);
            return JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        }

        public (Task Entered, TaskCompletionSource<AcpPromptResult> Release) GateNextPrompt()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Factory.ConfigureNext = session =>
            {
                Factory.ConfigureNext = null;
                session.PromptHandler = (_, _, _, promptCt) =>
                {
                    entered.TrySetResult();
                    return release.Task.WaitAsync(promptCt);
                };
            };
            return (entered.Task, release);
        }

        public async Task<(Task Entered, TaskCompletionSource<AcpPromptResult> Release)> ConnectGatedAsync(AgentGuiTabViewModel vm)
        {
            var gate = GateNextPrompt();
            await vm.ConnectAsync();
            return gate;
        }

        public static Task NextTurnFinished(AgentGuiTabViewModel vm)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<string, string?, string?, bool, string?>? handler = null;
            handler = (_, _, _, _, _) => { vm.TurnFinished -= handler; tcs.TrySetResult(); };
            vm.TurnFinished += handler;
            return tcs.Task;
        }

        public async Task<(string ChildId, string ThreadId, Task TurnDone, TaskCompletionSource<AcpPromptResult> Release)> DelegateGuiGatedAsync(
            Project project, string parentId, string title, string goal, CancellationToken ct)
        {
            var (entered, release) = GateNextPrompt();
            var body = await CallToolOkAsync(parentId, "delegate_task", new JsonObject
            {
                ["target"] = "opencode",
                ["title"] = title,
                ["goal"] = goal,
                ["surface"] = "gui",
            }, ct);
            var childId = body["session_id"]!.GetValue<string>();
            var threadId = body["thread_id"]!.GetValue<string>();
            var turnDone = NextTurnFinished(FindGuiVm(project, childId));
            await entered.WaitAsync(EventTimeout, ct);
            Frames.Pump();
            return (childId, threadId, turnDone, release);
        }

        public async Task<string> SendPeerAndSettleAsync(
            ProjectItem fromItem, AgentGuiTabViewModel fromVm, ProjectItem toItem, AgentGuiTabViewModel toVm,
            string title, string message, CancellationToken ct, bool expectReply = true)
        {
            var toDone = NextTurnFinished(toVm);
            var fromWakeDone = expectReply ? NextTurnFinished(fromVm) : Task.CompletedTask;
            var args = new JsonObject
            {
                ["target_session_id"] = toItem.Id,
                ["title"] = title,
                ["message"] = message,
            };
            if (!expectReply) args["expect_reply"] = false;
            var body = await CallToolOkAsync(fromItem.Id, "send_message", args, ct);
            await Task.WhenAll(toDone, fromWakeDone).WaitAsync(EventTimeout, ct);
            Frames.Pump();
            return body["thread_id"]!.GetValue<string>();
        }

        public async Task AssertApprovalBubbleAndClearAsync(
            AgentGuiTabViewModel parentVm, string parentId, string childId, string childTitle,
            string approvalId, string tool, string detail, CancellationToken ct, bool background = false)
        {
            var approval = new CollabBubbledApproval(approvalId, parentId, childId, "opencode", childTitle, tool, detail, null, _ => { });
            if (background) await Task.Run(() => Coordinator.ReportChildApproval(approval), ct);
            else Coordinator.ReportChildApproval(approval);
            Frames.Pump();
            Assert.Single(parentVm.BubbledApprovals);
            Assert.Contains(parentVm.CollabRibbonNodes, n => n.Kind == "child" && n.Id == childId && n.Status == "waiting_approval");

            if (background) await Task.Run(() => Coordinator.ClearChildApproval(childId, approvalId), ct);
            else Coordinator.ClearChildApproval(childId, approvalId);
            Frames.Pump();
            Assert.Empty(parentVm.BubbledApprovals);
        }

        public async Task WaitForChangedAsync(Func<bool> condition, CancellationToken ct)
        {
            if (condition()) { Frames.Pump(); return; }
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(string? _) { if (condition()) tcs.TrySetResult(); }
            Coordinator.Changed += OnChanged;
            try
            {
                if (!condition()) await tcs.Task.WaitAsync(EventTimeout, ct);
            }
            finally
            {
                Coordinator.Changed -= OnChanged;
            }
            Frames.Pump();
            Assert.True(condition());
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var obs in _observers) obs.Dispose();
            _observers.Clear();
            Factory.ConfigureNext = null;
            await TabRegistry.ReleaseAllAsync();
            Frames.Pump();
            foreach (var dir in _tempDirs)
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    private static void AssertSameNodes(CollabRibbonNodeViewModel[]? expected, IList<CollabRibbonNodeViewModel>? actual)
    {
        if (expected is null || actual is null) return;
        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < expected.Length; i++) Assert.Same(expected[i], actual[i]);
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task Unrelated_sessions_do_not_rebuild_ribbons_across_1_5_10_representative_sessions(int unrelatedCount)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fx = new RibbonTestFixture();
        var mainProject = fx.AddProject($"octopus-ribbon-main-{unrelatedCount}-");
        var otherProject = fx.AddProject($"octopus-ribbon-other-{unrelatedCount}-");

        var (activeParentItem, activeParentVm) = fx.CreateGuiTab(mainProject, "ActiveParent");
        var (activePeerItem, activePeerVm) = fx.CreateGuiTab(mainProject, "ActivePeer");

        // N representative unrelated sessions: same-project non-participants, cross-project non-participants,
        // plus (for N >= 5) an independent parent/child pair and an independent peer pair with an open thread draft.
        var unrelatedTabs = new List<AgentGuiTabViewModel>();
        AgentGuiTabViewModel? indieParentVm = null, indieChildVm = null, indiePeerAVm = null, indiePeerBVm = null;

        if (unrelatedCount == 1)
        {
            unrelatedTabs.Add(fx.CreateGuiTab(mainProject, "SameProjectNonParticipant-0").Vm);
        }
        else
        {
            var (ipItem, ipVm) = fx.CreateGuiTab(mainProject, "IndependentParent");
            var (_, icVm) = fx.CreateGuiTab(mainProject, "IndependentChild", parentItemId: ipItem.Id, isolation: "worktree");
            indieParentVm = ipVm;
            indieChildVm = icVm;
            unrelatedTabs.AddRange([ipVm, icVm]);

            var (paItem, paVm) = fx.CreateGuiTab(mainProject, "IndependentPeerA");
            var (pbItem, pbVm) = fx.CreateGuiTab(mainProject, "IndependentPeerB");
            indiePeerAVm = paVm;
            indiePeerBVm = pbVm;
            unrelatedTabs.AddRange([paVm, pbVm]);

            // Settle the independent peer pair's own initial turn + implicit reply before attaching observers
            // so their legitimate setup notifications do not race into the active-conversation measurement window.
            await fx.SendPeerAndSettleAsync(paItem, paVm, pbItem, pbVm, "Independent negotiation", "Keep our own thread intact.", ct);

            Assert.Single(paVm.CollabRibbonNodes, n => n.Kind == "peer").ActivateCommand.Execute(null);
            Frames.Pump();
            Assert.NotNull(paVm.ActiveCollabThread);
            paVm.ActiveCollabThread!.InterjectText = "unsent draft";

            for (var i = unrelatedTabs.Count; i < unrelatedCount; i++)
            {
                var inMain = i % 2 == 0;
                unrelatedTabs.Add(fx.CreateGuiTab(inMain ? mainProject : otherProject, $"NonParticipant-{i}").Vm);
            }
        }
        Frames.Pump();

        var indieParentInitialNodes = indieParentVm?.CollabRibbonNodes.ToArray();
        var indieChildInitialNodes = indieChildVm?.CollabRibbonNodes.ToArray();
        var indiePeerAInitialThread = indiePeerAVm?.ActiveCollabThread;
        var indiePeerBInitialNodes = indiePeerBVm?.CollabRibbonNodes.ToArray();

        var activeParentObs = fx.Observe(activeParentVm);
        var activePeerObs = fx.Observe(activePeerVm);
        var unrelatedObservers = unrelatedTabs.Select(fx.Observe).ToList();
        var collabChangedEvents = 0;
        void OnChanged(string? _) => Interlocked.Increment(ref collabChangedEvents);
        fx.Coordinator.Changed += OnChanged;

        try
        {
            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();

            // 1. ActiveParent delegates to a newly spawned GUI child (gated mid-turn until explicit reply).
            var (childId, delegateThreadId, childTurnDone, childRelease) =
                await fx.DelegateGuiGatedAsync(mainProject, activeParentItem.Id, "ActiveChild", "Complete delegated work.", ct);

            // 2. ActiveChild bubbles and clears a permission approval on ActiveParent.
            await fx.AssertApprovalBubbleAndClearAsync(activeParentVm, activeParentItem.Id, childId, "ActiveChild", "apr-1", "Bash", "dotnet test", ct);

            // 3. ActiveChild explicitly replies to ActiveParent, then finishes its gated turn.
            var parentWakeDone = RibbonTestFixture.NextTurnFinished(activeParentVm);
            await fx.CallToolOkAsync(childId, "reply_thread", new JsonObject
            {
                ["thread_id"] = delegateThreadId,
                ["message"] = "Delegated work finished.",
                ["status"] = "agreed",
            }, ct);
            childRelease.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
            await Task.WhenAll(parentWakeDone, childTurnDone).WaitAsync(EventTimeout, ct);
            Frames.Pump();

            // 4. ActiveParent exchanges a peer message with ActivePeer and awaits full turn settlement.
            await fx.SendPeerAndSettleAsync(activeParentItem, activeParentVm, activePeerItem, activePeerVm, "Active peer sync", "Syncing API shape.", ct);

            sw.Stop();
            var allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            var totalUnrelatedResets = unrelatedObservers.Sum(o => o.RibbonResets);
            var totalUnrelatedNodeAdds = unrelatedObservers.Sum(o => o.RibbonNodeAdds);
            var totalUnrelatedApprovalResets = unrelatedObservers.Sum(o => o.ApprovalResets);
            var totalUnrelatedPropNotifies = unrelatedObservers.Sum(o => o.HasTopologyNotifies + o.HasApprovalsNotifies + o.ActiveThreadNotifies);

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"PERF-01_MEASURE unrelated={unrelatedCount} changed_events={collabChangedEvents} " +
                $"active_parent_resets={activeParentObs.RibbonResets} active_peer_resets={activePeerObs.RibbonResets} " +
                $"unrelated_ribbon_resets={totalUnrelatedResets} unrelated_node_adds={totalUnrelatedNodeAdds} " +
                $"unrelated_approval_resets={totalUnrelatedApprovalResets} unrelated_prop_notifies={totalUnrelatedPropNotifies} " +
                $"elapsed_ms={sw.Elapsed.TotalMilliseconds:F2} thread_alloc_bytes={allocDelta}");

            Assert.True(activeParentVm.HasCollabTopology);
            Assert.Contains(activeParentVm.CollabRibbonNodes, n => n.Kind == "child" && n.Id == childId);
            Assert.Contains(activeParentVm.CollabRibbonNodes, n => n.Kind == "peer");
            Assert.True(activePeerVm.HasCollabTopology);
            Assert.Contains(activePeerVm.CollabRibbonNodes, n => n.Kind == "peer");

            Assert.Equal(0, totalUnrelatedResets);
            Assert.Equal(0, totalUnrelatedNodeAdds);
            Assert.Equal(0, totalUnrelatedApprovalResets);
            Assert.Equal(0, totalUnrelatedPropNotifies);

            AssertSameNodes(indieParentInitialNodes, indieParentVm?.CollabRibbonNodes);
            AssertSameNodes(indieChildInitialNodes, indieChildVm?.CollabRibbonNodes);
            AssertSameNodes(indiePeerBInitialNodes, indiePeerBVm?.CollabRibbonNodes);
            if (indiePeerAVm is not null && indiePeerAInitialThread is not null)
            {
                Assert.Same(indiePeerAInitialThread, indiePeerAVm.ActiveCollabThread);
                Assert.Equal("unsent draft", indiePeerAVm.ActiveCollabThread!.InterjectText);
            }
        }
        finally
        {
            fx.Coordinator.Changed -= OnChanged;
        }
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task Parent_child_recalculation_depth_and_sibling_isolation_for_1_5_10_children(int childCount)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fx = new RibbonTestFixture();
        var project = fx.AddProject($"octopus-ribbon-depth-{childCount}-");
        var (parentItem, parentVm) = fx.CreateGuiTab(project, "Parent");
        var (_, bystanderVm) = fx.CreateGuiTab(project, "SameProjectBystander");
        var children = Enumerable.Range(0, childCount)
            .Select(i => fx.CreateGuiTab(project, $"Child-{i}", parentItemId: parentItem.Id))
            .ToList();
        Frames.Pump();

        var parentObs = fx.Observe(parentVm);
        var bystanderObs = fx.Observe(bystanderVm);
        var childObservers = children.Select(c => fx.Observe(c.Vm)).ToList();

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        await fx.AssertApprovalBubbleAndClearAsync(parentVm, parentItem.Id, children[0].Item.Id, children[0].Item.Title, "apr-depth", "Edit", "src/file.cs", ct);

        fx.Coordinator.UnregisterGuiSession(children[0].Item.Id);
        Frames.Pump();

        sw.Stop();
        var allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        var siblingResets = childObservers.Skip(1).Sum(o => o.RibbonResets);
        var siblingNodeAdds = childObservers.Skip(1).Sum(o => o.RibbonNodeAdds);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"PERF-01_DEPTH children={childCount} parent_resets={parentObs.RibbonResets} parent_node_adds={parentObs.RibbonNodeAdds} " +
            $"bystander_resets={bystanderObs.RibbonResets} sibling_resets={siblingResets} sibling_node_adds={siblingNodeAdds} " +
            $"elapsed_ms={sw.Elapsed.TotalMilliseconds:F2} thread_alloc_bytes={allocDelta}");

        Assert.True(parentObs.RibbonResets >= 3);
        Assert.Equal(0, bystanderObs.TotalInvalidationSignals);

        foreach (var obs in childObservers) obs.ResetCounters();
        bystanderObs.ResetCounters();
        parentObs.ResetCounters();

        fx.Coordinator.RegisterGuiSession(children[0].Vm);
        Frames.Pump();

        Assert.Equal(1, parentObs.RibbonResets);
        Assert.Equal(0, bystanderObs.TotalInvalidationSignals);
        Assert.Equal(0, childObservers.Skip(1).Sum(o => o.TotalInvalidationSignals));
    }

    [AvaloniaFact]
    public async Task Related_conversations_grandparent_peer_budget_3way_broadcast_and_removal_still_update()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fx = new RibbonTestFixture();
        fx.Settings.Update(s => s.Agents.Collab.MaxNegotiationHops = 2);

        var project = fx.AddProject("octopus-ribbon-related-");
        var (gpItem, gpVm) = fx.CreateGuiTab(project, "Grandparent");
        var (parentItem, parentVm) = fx.CreateGuiTab(project, "Parent", parentItemId: gpItem.Id);
        var (peerAItem, peerAVm) = fx.CreateGuiTab(project, "PeerA");
        var (peerBItem, peerBVm) = fx.CreateGuiTab(project, "PeerB");
        var (peerCItem, peerCVm) = fx.CreateGuiTab(project, "PeerC");
        var (_, unrelatedVm) = fx.CreateGuiTab(project, "Unrelated");
        Frames.Pump();

        var unrelatedObs = fx.Observe(unrelatedVm);

        // 1. Parent delegates to Grandchild (depth 2, gated until removal): Parent & Grandparent update; Unrelated stays at 0.
        var (gcId, _, _, _) = await fx.DelegateGuiGatedAsync(project, parentItem.Id, "Grandchild", "Nested subtask", ct);
        Assert.Contains(parentVm.CollabRibbonNodes, n => n.Kind == "parent" && n.Id == gpItem.Id);
        Assert.Contains(parentVm.CollabRibbonNodes, n => n.Kind == "child" && n.Id == gcId);
        Assert.Contains(gpVm.CollabRibbonNodes, n => n.Kind == "child" && n.Id == parentItem.Id);
        Assert.Equal(0, unrelatedObs.TotalInvalidationSignals);

        // 2. PeerA <-> PeerB reaches hop budget (only RaiseChanged(peerA) fires): BOTH PeerA and PeerB see PausedBudget.
        var negoThreadId = await fx.SendPeerAndSettleAsync(peerAItem, peerAVm, peerBItem, peerBVm, "Budgeted negotiation", "Round 1", ct, expectReply: false);
        Assert.Single(peerBVm.CollabRibbonNodes, n => n.Kind == "peer" && n.Id == negoThreadId).ActivateCommand.Execute(null);
        Frames.Pump();
        Assert.False(Assert.IsType<CollabThreadPanelViewModel>(peerBVm.ActiveCollabThread).IsBudgetPaused);

        var peerARound2Done = RibbonTestFixture.NextTurnFinished(peerAVm);
        await fx.CallToolOkAsync(peerBItem.Id, "reply_thread", new JsonObject { ["thread_id"] = negoThreadId, ["message"] = "Round 2", ["status"] = "continue" }, ct);
        await peerARound2Done.WaitAsync(EventTimeout, ct);
        Frames.Pump();

        var blocked = await fx.Coordinator.CallToolAsync(peerAItem.Id, "send_message", new JsonObject { ["target_session_id"] = peerBItem.Id, ["thread_id"] = negoThreadId, ["message"] = "Round 3 blocked" }, ct);
        Assert.True(blocked["isError"]?.GetValue<bool>());
        Frames.Pump();

        Assert.Contains(peerAVm.CollabRibbonNodes, n => n.Id == negoThreadId && n.Status == "waiting_approval");
        Assert.Contains(peerBVm.CollabRibbonNodes, n => n.Id == negoThreadId && n.Status == "waiting_approval");
        Assert.True(peerBVm.ActiveCollabThread!.IsBudgetPaused);
        Assert.Equal(0, unrelatedObs.TotalInvalidationSignals);

        // 3. Targeted refresh via InterjectInThreadAsync updates PeerB's open panel & extends budget without waking Unrelated.
        var (aInterject, bInterject) = (RibbonTestFixture.NextTurnFinished(peerAVm), RibbonTestFixture.NextTurnFinished(peerBVm));
        Assert.True(await fx.Coordinator.InterjectInThreadAsync(negoThreadId, "Continue and finalize.", extendBudget: true, ct));
        await Task.WhenAll(aInterject, bInterject).WaitAsync(EventTimeout, ct);
        Frames.Pump();

        Assert.False(peerBVm.ActiveCollabThread!.IsBudgetPaused);
        Assert.Contains(peerBVm.ActiveCollabThread.Messages, m => m.IsUserInterjection && m.Text == "Continue and finalize.");
        Assert.Equal(0, unrelatedObs.TotalInvalidationSignals);

        // 4. 3-way broadcast (PeerA -> PeerB & PeerC): PeerB reply raises Changed(PeerB)/Changed(PeerA), PeerC still updates.
        fx.Settings.Update(s => s.Agents.Collab.MaxNegotiationHops = 6);
        var (bBroadcast, cBroadcast) = (RibbonTestFixture.NextTurnFinished(peerBVm), RibbonTestFixture.NextTurnFinished(peerCVm));
        var broadcast = await fx.Coordinator.SendUserBroadcastAsync(peerAItem.Id, [peerBItem.Id, peerCItem.Id], "Three-way sync", ct);
        Assert.NotNull(broadcast);
        await Task.WhenAll(bBroadcast, cBroadcast).WaitAsync(EventTimeout, ct);
        Frames.Pump();

        Assert.Single(peerCVm.CollabRibbonNodes, n => n.Kind == "peer" && n.Id == broadcast!.ThreadId).ActivateCommand.Execute(null);
        Frames.Pump();
        var initialBroadcastMsgCount = Assert.IsType<CollabThreadPanelViewModel>(peerCVm.ActiveCollabThread).Messages.Count;

        var peerAWake = RibbonTestFixture.NextTurnFinished(peerAVm);
        await fx.CallToolOkAsync(peerBItem.Id, "reply_thread", new JsonObject { ["thread_id"] = broadcast!.ThreadId, ["message"] = "PeerB update for everyone", ["status"] = "agreed" }, ct);
        await peerAWake.WaitAsync(EventTimeout, ct);
        Frames.Pump();

        Assert.True(peerCVm.ActiveCollabThread!.Messages.Count > initialBroadcastMsgCount);
        Assert.Contains(peerCVm.ActiveCollabThread.Messages, m => m.Text == "PeerB update for everyone");
        Assert.Equal(0, unrelatedObs.TotalInvalidationSignals);

        // 5. Removing Grandchild updates Parent's ribbon without disturbing Unrelated.
        fx.Projects.RemoveItem(gcId);
        Frames.Pump();
        Assert.DoesNotContain(parentVm.CollabRibbonNodes, n => n.Id == gcId);
        Assert.Equal(0, unrelatedObs.TotalInvalidationSignals);
    }

    [AvaloniaFact]
    public async Task Turn_safe_notifications_keep_unrelated_active_turn_quiet_and_deliver_background_updates_safely()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fx = new RibbonTestFixture();
        var project = fx.AddProject("octopus-ribbon-turnsafe-");
        var (parentItem, parentVm) = fx.CreateGuiTab(project, "BusyParent");
        var (childItem, childVm) = fx.CreateGuiTab(project, "WorkerChild", parentItemId: parentItem.Id);
        var (_, unrelatedBusyVm) = fx.CreateGuiTab(project, "UnrelatedBusy");
        var (_, disposedVm) = fx.CreateGuiTab(project, "DisposedTab");

        var (parentTurnEntered, parentTurnRelease) = await fx.ConnectGatedAsync(parentVm);
        var (_, childTurnRelease) = await fx.ConnectGatedAsync(childVm);
        var (unrelatedTurnEntered, unrelatedTurnRelease) = await fx.ConnectGatedAsync(unrelatedBusyVm);
        Frames.Pump();

        parentVm.InputText = "Parent active turn";
        var parentSendTask = parentVm.SendPromptAsync();
        await parentTurnEntered.WaitAsync(EventTimeout, ct);

        unrelatedBusyVm.InputText = "Unrelated active turn";
        var unrelatedSendTask = unrelatedBusyVm.SendPromptAsync();
        await unrelatedTurnEntered.WaitAsync(EventTimeout, ct);
        Frames.Pump();

        Assert.True(parentVm.IsBusy && parentVm.State == AgentGuiState.Working);
        Assert.True(unrelatedBusyVm.IsBusy && unrelatedBusyVm.State == AgentGuiState.Working);

        disposedVm.Dispose();
        Frames.Pump();

        var parentObs = fx.Observe(parentVm);
        var unrelatedBusyObs = fx.Observe(unrelatedBusyVm);
        var disposedObs = fx.Observe(disposedVm);

        // Delegate and bubble/clear approval from background threads while BusyParent and UnrelatedBusy are mid-turn.
        var childTurnDone = RibbonTestFixture.NextTurnFinished(childVm);
        var delegateBody = await Task.Run(() => fx.CallToolOkAsync(parentItem.Id, "delegate_task", new JsonObject
        {
            ["target"] = childItem.Id,
            ["goal"] = "Run while parent and unrelated tab are busy",
        }, ct), ct);
        var taskId = delegateBody["task_id"]!.GetValue<string>();

        await fx.AssertApprovalBubbleAndClearAsync(parentVm, parentItem.Id, childItem.Id, childItem.Title, "apr-bg", "Bash", "echo ok", ct, background: true);
        Assert.True(parentVm.IsBusy && parentVm.State == AgentGuiState.Working);

        // Complete the child's real ACP turn -> NotifyTurnCompleted -> Task.Run(RecordReplyAndWakeAsync) -> background RaiseChanged.
        childTurnRelease.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await childTurnDone.WaitAsync(EventTimeout, ct);
        await fx.WaitForChangedAsync(
            () => fx.Coordinator.GetTasksForItem(parentItem.Id).Any(t => t.TaskId == taskId && t.Status == CollabTaskStatus.Completed),
            ct);

        Assert.True(parentVm.IsBusy && parentVm.State == AgentGuiState.Working);
        Assert.True(parentObs.RibbonResets > 0);
        Assert.True(unrelatedBusyVm.IsBusy && unrelatedBusyVm.State == AgentGuiState.Working);
        Assert.Equal(0, unrelatedBusyObs.TotalInvalidationSignals);
        Assert.Equal(0, disposedObs.TotalInvalidationSignals);

        parentTurnRelease.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        unrelatedTurnRelease.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await Task.WhenAll(parentSendTask, unrelatedSendTask).WaitAsync(EventTimeout, ct);
        Frames.Pump();

        Assert.Equal(AgentGuiState.Ready, parentVm.State);
        Assert.Equal(AgentGuiState.Ready, unrelatedBusyVm.State);
        Assert.Equal(0, unrelatedBusyObs.TotalInvalidationSignals);
    }

    [AvaloniaFact]
    public async Task Removing_unrelated_top_level_item_preserves_open_panel_and_draft()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fx = new RibbonTestFixture();
        var project = fx.AddProject("octopus-ribbon-unrelated-remove-");
        var (a, av) = fx.CreateGuiTab(project, "PeerA");
        var (b, bv) = fx.CreateGuiTab(project, "PeerB");
        var (_, uv) = fx.CreateGuiTab(project, "UnrelatedObserver");
        var removed = new ProjectItem { Title = "UnrelatedTopLevel", Kind = ProjectItemKind.Agent, AgentId = "opencode" };
        fx.Projects.AddItem(project, removed, activate: false);

        var tid = await fx.SendPeerAndSettleAsync(a, av, b, bv, "Independent negotiation", "hello", ct);
        Assert.Single(av.CollabRibbonNodes, n => n.Id == tid).ActivateCommand.Execute(null);
        Frames.Pump();

        var initialPanel = Assert.IsType<CollabThreadPanelViewModel>(av.ActiveCollabThread);
        initialPanel.InterjectText = "draft with unsaved work";
        var obsA = fx.Observe(av);
        var obsU = fx.Observe(uv);

        // Removing an unrelated top-level item (ParentItemId == null) must NOT broadcast-reset A or erase its draft.
        fx.Projects.RemoveItem(removed.Id);
        Frames.Pump();

        Assert.Equal(0, obsA.TotalInvalidationSignals);
        Assert.Equal(0, obsU.TotalInvalidationSignals);
        Assert.Same(initialPanel, av.ActiveCollabThread);
        Assert.Equal("draft with unsaved work", av.ActiveCollabThread!.InterjectText);

        // Removing the related top-level peer B still refreshes A and drops B's peer capsule without disturbing U.
        fx.Projects.RemoveItem(b.Id);
        Frames.Pump();

        Assert.DoesNotContain(av.CollabRibbonNodes, n => n.Id == tid);
        Assert.Equal(0, obsU.TotalInvalidationSignals);
    }

    [AvaloniaFact]
    public async Task Removing_project_drops_its_unopened_child_from_remaining_ribbon()
    {
        await using var fx = new RibbonTestFixture();
        var home = fx.AddProject("octopus-ribbon-parent-");
        var external = fx.AddProject("octopus-ribbon-external-");
        var (_, unrelatedVm) = fx.CreateGuiTab(home, "Unrelated");
        var (parentItem, parentVm) = fx.CreateGuiTab(home, "Parent");
        var child = new ProjectItem
        {
            Title = "External child",
            Kind = ProjectItemKind.Agent,
            AgentId = "opencode",
            ParentItemId = parentItem.Id,
        };
        fx.Projects.AddItem(external, child, activate: false);
        fx.Coordinator.RegisterGuiSession(parentVm);
        Frames.Pump();
        Assert.Contains(parentVm.CollabRibbonNodes, n => n.Id == child.Id);

        var unrelatedObs = fx.Observe(unrelatedVm);

        // Removing the child's project must notify surviving parents directly without refreshing unrelated tabs.
        fx.Projects.RemoveProject(external.Id);
        Frames.Pump();

        Assert.DoesNotContain(fx.Coordinator.GetSpawnedChildren(parentItem.Id), x => x.Id == child.Id);
        Assert.DoesNotContain(parentVm.CollabRibbonNodes, n => n.Id == child.Id);
        Assert.Equal(0, unrelatedObs.TotalInvalidationSignals);

        // Even after a later unrelated registration, the deleted child stays gone.
        fx.Coordinator.RegisterGuiSession(unrelatedVm);
        Frames.Pump();
        Assert.DoesNotContain(parentVm.CollabRibbonNodes, n => n.Id == child.Id);
    }

    [AvaloniaFact]
    public async Task Burst_coalesces_and_rebuilds_latest_approval_snapshot()
    {
        await using var fx = new RibbonTestFixture();
        var project = fx.AddProject("octopus-ribbon-burst-");
        var (parentItem, parentVm) = fx.CreateGuiTab(project, "Parent");
        var (childItem, _) = fx.CreateGuiTab(project, "Child", parentItemId: parentItem.Id);
        var (_, unrelatedVm) = fx.CreateGuiTab(project, "Observer");
        Frames.Pump();

        var parentObs = fx.Observe(parentVm);
        var unrelatedObs = fx.Observe(unrelatedVm);

        var produced = Task.Run(() => Parallel.For(0, 32, i =>
        {
            var id = "key-" + i;
            fx.Coordinator.ReportChildApproval(new CollabBubbledApproval(id, parentItem.Id, childItem.Id, "opencode", "Child", "tool", "detail", null, _ => { }));
            if (i % 2 == 0) fx.Coordinator.ClearChildApproval(childItem.Id, id);
        }));
        // Hold the UI dispatcher while the background burst runs so coalescing collapses all 48 updates into 1 refresh.
        Assert.True(produced.Wait(EventTimeout));
        Frames.Pump();

        Assert.Equal(1, parentObs.RibbonResets);
        Assert.Equal(0, unrelatedObs.TotalInvalidationSignals);
        Assert.Equal(16, parentVm.BubbledApprovals.Count);
    }

    [AvaloniaFact]
    public async Task Unrelated_relevance_lookup_stays_constant_and_accurate_across_growing_live_history()
    {
        await using var fx = new RibbonTestFixture();
        var project = fx.AddProject("octopus-ribbon-history-");
        var (_, activeVm) = fx.CreateGuiTab(project, "Active");
        var unrelatedObservers = Enumerable.Range(0, 10)
            .Select(i => fx.Observe(fx.CreateGuiTab(project, $"Unrelated-{i}").Vm))
            .ToList();
        Frames.Pump();

        var ledger = (CollabLedger)typeof(CollabCoordinator)
            .GetField("_ledger", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(fx.Coordinator)!;

        double elapsed10kMs = 0;
        long alloc10k = 0;
        foreach (var size in new[] { 0, 300, 3_000, 10_000 })
        {
            lock (ledger.Gate)
            {
                for (var i = ledger.Threads.Count; i < size; i++)
                {
                    ledger.Add(new CollabThreadRecord
                    {
                        ThreadId = "hist-" + i,
                        Title = "archived",
                        InitiatorItemId = "x",
                        ParticipantItemIds = ["x", "y"],
                        Status = CollabThreadStatus.Agreed,
                    });
                }
            }

            Assert.False(fx.Coordinator.IsRelevantChange("other", "changed"));
            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            for (var n = 0; n < 400; n++)
            {
                if (fx.Coordinator.IsRelevantChange("other", "changed"))
                {
                    throw new InvalidOperationException("Unexpected relevance hit for unrelated ids.");
                }
            }
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"PERF-01_HISTORY threads={size} lookups=400 elapsed_ms={elapsedMs:F2} allocated_bytes={allocDelta}");
            if (size == 10_000)
            {
                elapsed10kMs = elapsedMs;
                alloc10k = allocDelta;
            }
        }

        // O(1) indexed negative lookup across 10,000 live threads completes in well under 5 ms with zero allocations
        // (vs ~75-90 ms linear full-history scan before indexing).
        Assert.True(elapsed10kMs < 5.0, $"Expected constant-time negative lookup across 10,000 threads (<5 ms for 400 lookups), got {elapsed10kMs:F2} ms.");
        Assert.Equal(0, alloc10k);

        // Verify live index stays accurate with zero stale state across add, remove, approval add/clear, and targeted dispatch.
        var liveThread = new CollabThreadRecord
        {
            ThreadId = "live-rel",
            Title = "live",
            InitiatorItemId = "other",
            ParticipantItemIds = ["other", "changed"],
            Status = CollabThreadStatus.Active,
        };
        lock (ledger.Gate) ledger.Add(liveThread);
        Assert.True(fx.Coordinator.IsRelevantChange("other", "changed"));

        lock (ledger.Gate) Assert.True(ledger.RemoveThread("live-rel"));
        Assert.False(fx.Coordinator.IsRelevantChange("other", "changed"));

        fx.Coordinator.ReportChildApproval(new CollabBubbledApproval("apr-hist", "other", "changed", "opencode", "Child", "Bash", "cmd", null, _ => { }));
        Assert.True(fx.Coordinator.IsRelevantChange("other", "changed"));
        fx.Coordinator.ClearChildApproval("changed", "apr-hist");
        Assert.False(fx.Coordinator.IsRelevantChange("other", "changed"));

        // Targeted notification with 10,000 historical threads and 10 unrelated tabs triggers zero unrelated signals.
        foreach (var obs in unrelatedObservers) obs.ResetCounters();
        fx.Coordinator.RegisterGuiSession(activeVm);
        Frames.Pump();
        Assert.Equal(0, unrelatedObservers.Sum(o => o.TotalInvalidationSignals));
    }

    [AvaloniaFact]
    public async Task Clearing_new_approval_after_session_loss_returns_subsequent_task_to_running()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fx = new RibbonTestFixture();
        var project = fx.AddProject("octopus-ribbon-abort-approval-");
        var (parent, parentVm) = fx.CreateGuiTab(project, "Parent");
        var (child, childVm) = fx.CreateGuiTab(project, "Child", parentItemId: parent.Id);
        var (_, unrelatedVm) = fx.CreateGuiTab(project, "UnrelatedObserver");
        Frames.Pump();

        var unrelatedObs = fx.Observe(unrelatedVm);
        var firstGate = await fx.ConnectGatedAsync(childVm);

        async Task<string> DelegateAsync(string goal) =>
            (await fx.CallToolOkAsync(parent.Id, "delegate_task", new JsonObject
            {
                ["target"] = child.Id,
                ["goal"] = goal,
            }, ct))["task_id"]!.GetValue<string>();

        void RaiseApproval(string key) =>
            fx.Coordinator.ReportChildApproval(new CollabBubbledApproval(key, parent.Id, child.Id, "opencode", "Child", "Bash", "dotnet test", null, _ => { }));

        var firstTaskId = await DelegateAsync("first turn");
        await firstGate.Entered.WaitAsync(EventTimeout, ct);
        RaiseApproval("apr-before-loss");
        Frames.Pump();

        Assert.Equal(CollabTaskStatus.WaitingApproval, fx.Coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == firstTaskId).Status);
        Assert.Single(parentVm.BubbledApprovals);

        // Session loss aborts pending work and must clear both approval records and the ledger's approval parent count index.
        fx.Coordinator.NotifySessionAbortedAllPending(child.Id, "process exited", failed: true);
        Frames.Pump();

        Assert.True(fx.Coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == firstTaskId).IsFinished);
        Assert.Empty(fx.Coordinator.GetBubbledApprovalsForParent(parent.Id));
        Assert.Empty(parentVm.BubbledApprovals);

        var firstFinished = RibbonTestFixture.NextTurnFinished(childVm);
        firstGate.Release.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await firstFinished.WaitAsync(EventTimeout, ct);
        Frames.Pump();

        // Next turn raises a fresh approval and clears it; task must transition back from WaitingApproval to Running.
        var secondGate = await fx.ConnectGatedAsync(childVm);
        var secondTaskId = await DelegateAsync("second turn");
        await secondGate.Entered.WaitAsync(EventTimeout, ct);
        RaiseApproval("apr-after-loss");
        Frames.Pump();

        Assert.Equal(CollabTaskStatus.WaitingApproval, fx.Coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == secondTaskId).Status);
        Assert.Single(parentVm.BubbledApprovals);

        fx.Coordinator.ClearChildApproval(child.Id, "apr-after-loss");
        Frames.Pump();

        var secondStatus = fx.Coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == secondTaskId).Status;
        var secondFinished = RibbonTestFixture.NextTurnFinished(childVm);
        secondGate.Release.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await secondFinished.WaitAsync(EventTimeout, ct);
        Frames.Pump();

        Assert.Empty(fx.Coordinator.GetBubbledApprovalsForParent(parent.Id));
        Assert.Empty(parentVm.BubbledApprovals);
        Assert.Equal(CollabTaskStatus.Running, secondStatus);
        Assert.Equal(0, unrelatedObs.TotalInvalidationSignals);
    }

    [AvaloniaFact]
    public async Task Independent_thread_interjection_and_related_refresh_preserve_open_panel_draft()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fx = new RibbonTestFixture();
        var project = fx.AddProject("octopus-ribbon-interject-draft-");
        var (a, av) = fx.CreateGuiTab(project, "PeerA");
        var (b, bv) = fx.CreateGuiTab(project, "PeerB");
        var (c, cv) = fx.CreateGuiTab(project, "PeerC");
        var (d, dv) = fx.CreateGuiTab(project, "PeerD");

        var ab = await fx.SendPeerAndSettleAsync(a, av, b, bv, "AB", "hello AB", ct);
        var cd = await fx.SendPeerAndSettleAsync(c, cv, d, dv, "CD", "hello CD", ct);

        Assert.Single(av.CollabRibbonNodes, n => n.Id == ab).ActivateCommand.Execute(null);
        Assert.Single(cv.CollabRibbonNodes, n => n.Id == cd).ActivateCommand.Execute(null);
        Frames.Pump();

        var initialPanelA = Assert.IsType<CollabThreadPanelViewModel>(av.ActiveCollabThread);
        var initialPanelC = Assert.IsType<CollabThreadPanelViewModel>(cv.ActiveCollabThread);
        initialPanelA.InterjectText = "unfinished note";
        var obsA = fx.Observe(av);
        var obsB = fx.Observe(bv);

        // 1. Interjecting in CD notifies CD participants without invalidating AB or erasing A's draft.
        var (cDone, dDone) = (RibbonTestFixture.NextTurnFinished(cv), RibbonTestFixture.NextTurnFinished(dv));
        Assert.True(await fx.Coordinator.InterjectInThreadAsync(cd, "follow up CD", extendBudget: false, ct));
        await Task.WhenAll(cDone, dDone).WaitAsync(EventTimeout, ct);
        Frames.Pump();

        Assert.Equal(0, obsA.TotalInvalidationSignals);
        Assert.Equal(0, obsB.TotalInvalidationSignals);
        Assert.Same(initialPanelA, av.ActiveCollabThread);
        Assert.Equal("unfinished note", av.ActiveCollabThread!.InterjectText);
        Assert.Same(initialPanelC, cv.ActiveCollabThread);
        Assert.Contains(cv.ActiveCollabThread!.Messages, m => m.IsUserInterjection && m.Text == "follow up CD");

        // 2. A necessary refresh on A (a new reply arriving on the open AB thread) updates messages while preserving A's draft.
        var aWakeDone = RibbonTestFixture.NextTurnFinished(av);
        await fx.CallToolOkAsync(b.Id, "reply_thread", new JsonObject
        {
            ["thread_id"] = ab,
            ["message"] = "peer update while drafting",
            ["status"] = "continue",
        }, ct);
        await aWakeDone.WaitAsync(EventTimeout, ct);
        Frames.Pump();

        Assert.Same(initialPanelA, av.ActiveCollabThread);
        Assert.Equal("unfinished note", av.ActiveCollabThread!.InterjectText);
        Assert.Contains(av.ActiveCollabThread.Messages, m => m.Text == "peer update while drafting");

        // 3. Explicitly sending the draft from the panel clears InterjectText and appends the user interjection.
        var (aSentDone, bSentDone) = (RibbonTestFixture.NextTurnFinished(av), RibbonTestFixture.NextTurnFinished(bv));
        await av.ActiveCollabThread.SendInterjectCommand.ExecuteAsync(null);
        await Task.WhenAll(aSentDone, bSentDone).WaitAsync(EventTimeout, ct);
        Frames.Pump();

        Assert.Same(initialPanelA, av.ActiveCollabThread);
        Assert.Equal("", av.ActiveCollabThread!.InterjectText);
        Assert.Contains(av.ActiveCollabThread.Messages, m => m.IsUserInterjection && m.Text == "unfinished note");
    }

    [AvaloniaFact]
    public async Task Nonparticipant_parent_observes_child_working_after_panel_interjection_while_unrelated_tabs_stay_quiet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fx = new RibbonTestFixture();
        var project = fx.AddProject("octopus-ribbon-interject-parent-badge-");
        var (parent, parentVm) = fx.CreateGuiTab(project, "Parent");
        var (childA, childAVm) = fx.CreateGuiTab(project, "ChildA", parentItemId: parent.Id);
        var (peerB, peerBVm) = fx.CreateGuiTab(project, "PeerB");
        var (peerE, peerEVm) = fx.CreateGuiTab(project, "PeerE");
        var (peerC, peerCVm) = fx.CreateGuiTab(project, "PeerC");
        var (peerD, peerDVm) = fx.CreateGuiTab(project, "PeerD");
        var (_, bystanderVm) = fx.CreateGuiTab(project, "Bystander");

        await childAVm.ConnectAsync();
        var childAcp = fx.Factory.Created.Last();
        await peerBVm.ConnectAsync();

        var ab = await fx.SendPeerAndSettleAsync(childA, childAVm, peerB, peerBVm, "AB", "hello AB", ct);
        var ae = await fx.SendPeerAndSettleAsync(childA, childAVm, peerE, peerEVm, "AE", "hello AE", ct);
        var cd = await fx.SendPeerAndSettleAsync(peerC, peerCVm, peerD, peerDVm, "CD", "hello CD", ct);

        Assert.Single(childAVm.CollabRibbonNodes, n => n.Id == ab).ActivateCommand.Execute(null);
        Assert.Single(peerEVm.CollabRibbonNodes, n => n.Id == ae).ActivateCommand.Execute(null);
        Assert.Single(peerCVm.CollabRibbonNodes, n => n.Id == cd).ActivateCommand.Execute(null);
        fx.Coordinator.RegisterGuiSession(childAVm);
        Frames.Pump();

        string ChildBadge() => Assert.Single(parentVm.CollabRibbonNodes, n => n.Id == childA.Id && n.Kind == "child").Status;
        string RelatedPeerBadge() => Assert.Single(peerEVm.CollabRibbonNodes, n => n.Id == ae && n.Kind == "peer").Status;
        Assert.NotEqual("working", ChildBadge());

        var initialPanelE = Assert.IsType<CollabThreadPanelViewModel>(peerEVm.ActiveCollabThread);
        initialPanelE.InterjectText = "related peer draft";
        var initialPanelC = Assert.IsType<CollabThreadPanelViewModel>(peerCVm.ActiveCollabThread);
        initialPanelC.InterjectText = "unrelated draft";

        var obsC = fx.Observe(peerCVm);
        var obsD = fx.Observe(peerDVm);
        var obsBystander = fx.Observe(bystanderVm);

        var gate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        childAcp.PromptHandler = (_, _, _, token) =>
        {
            entered.TrySetResult();
            return gate.Task.WaitAsync(token);
        };

        var childDone = RibbonTestFixture.NextTurnFinished(childAVm);
        var peerBDone = RibbonTestFixture.NextTurnFinished(peerBVm);
        var panelA = Assert.IsType<CollabThreadPanelViewModel>(childAVm.ActiveCollabThread);
        panelA.InterjectText = "please investigate";
        await panelA.SendInterjectCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(EventTimeout, ct);
        Frames.Pump();

        var observedChildBadge = ChildBadge();
        var observedRelatedPeerBadge = RelatedPeerBadge();
        var observedActivity = AppHost.Get<IItemActivityService>().Of(childA.Id);

        gate.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await Task.WhenAll(childDone, peerBDone).WaitAsync(EventTimeout, ct);
        Frames.Pump();

        Assert.Equal(ItemActivity.Working, observedActivity);
        Assert.Equal("working", observedChildBadge);
        Assert.Equal("working", observedRelatedPeerBadge);
        Assert.Same(initialPanelE, peerEVm.ActiveCollabThread);
        Assert.Equal("related peer draft", peerEVm.ActiveCollabThread!.InterjectText);
        Assert.Equal(0, obsC.TotalInvalidationSignals);
        Assert.Equal(0, obsD.TotalInvalidationSignals);
        Assert.Equal(0, obsBystander.TotalInvalidationSignals);
        Assert.Same(initialPanelC, peerCVm.ActiveCollabThread);
        Assert.Equal("unrelated draft", peerCVm.ActiveCollabThread!.InterjectText);
    }

    [AvaloniaFact]
    public async Task Nonparticipant_parent_tracks_child_done_working_done_across_one_way_outcomes_without_reregistration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fx = new RibbonTestFixture();
        var activity = AppHost.Get<IItemActivityService>();
        var project = fx.AddProject("octopus-ribbon-terminal-activity-");
        var (parent, parentVm) = fx.CreateGuiTab(project, "Parent");
        var (childA, childAVm) = fx.CreateGuiTab(project, "ChildA", parentItemId: parent.Id);
        var (peerB, peerBVm) = fx.CreateGuiTab(project, "PeerB");
        var (peerE, peerEVm) = fx.CreateGuiTab(project, "PeerE");
        var (peerC, peerCVm) = fx.CreateGuiTab(project, "PeerC");
        var (peerD, peerDVm) = fx.CreateGuiTab(project, "PeerD");
        var (_, bystanderVm) = fx.CreateGuiTab(project, "Bystander");

        await childAVm.ConnectAsync();
        var childAcp = fx.Factory.Created.Last();
        await peerBVm.ConnectAsync();

        var ab = await fx.SendPeerAndSettleAsync(childA, childAVm, peerB, peerBVm, "AB", "hello AB", ct);
        var ae = await fx.SendPeerAndSettleAsync(childA, childAVm, peerE, peerEVm, "AE", "hello AE", ct);
        var cd = await fx.SendPeerAndSettleAsync(peerC, peerCVm, peerD, peerDVm, "CD", "hello CD", ct);

        Assert.Single(childAVm.CollabRibbonNodes, n => n.Id == ab).ActivateCommand.Execute(null);
        Assert.Single(peerEVm.CollabRibbonNodes, n => n.Id == ae).ActivateCommand.Execute(null);
        Assert.Single(peerCVm.CollabRibbonNodes, n => n.Id == cd).ActivateCommand.Execute(null);
        Frames.Pump();

        string ChildBadge() => Assert.Single(parentVm.CollabRibbonNodes, n => n.Id == childA.Id && n.Kind == "child").Status;
        string RelatedPeerBadge() => Assert.Single(peerEVm.CollabRibbonNodes, n => n.Id == ae && n.Kind == "peer").Status;

        Assert.Equal(ItemActivity.Done, activity.Of(childA.Id));
        Assert.Equal("done", ChildBadge());
        Assert.Equal("done", RelatedPeerBadge());

        var initialPanelE = Assert.IsType<CollabThreadPanelViewModel>(peerEVm.ActiveCollabThread);
        initialPanelE.InterjectText = "related peer draft";
        var initialPanelC = Assert.IsType<CollabThreadPanelViewModel>(peerCVm.ActiveCollabThread);
        initialPanelC.InterjectText = "unrelated draft";

        var initialTaskCount = fx.Coordinator.GetTasksForItem(parent.Id).Count;
        var parentObs = fx.Observe(parentVm);
        var obsC = fx.Observe(peerCVm);
        var obsD = fx.Observe(peerDVm);
        var obsBystander = fx.Observe(bystanderVm);

        // 1. One-way panel interjection: done -> working -> done without a separate RegisterGuiSession.
        var interjectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interjectGate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        childAcp.PromptHandler = (_, _, _, token) =>
        {
            interjectEntered.TrySetResult();
            return interjectGate.Task.WaitAsync(token);
        };

        var childDone1 = RibbonTestFixture.NextTurnFinished(childAVm);
        var peerBDone1 = RibbonTestFixture.NextTurnFinished(peerBVm);
        var panelA = Assert.IsType<CollabThreadPanelViewModel>(childAVm.ActiveCollabThread);
        panelA.InterjectText = "check terminal badge";
        await panelA.SendInterjectCommand.ExecuteAsync(null);
        await interjectEntered.Task.WaitAsync(EventTimeout, ct);
        Frames.Pump();

        Assert.Equal(ItemActivity.Working, activity.Of(childA.Id));
        Assert.Equal("working", ChildBadge());
        Assert.Equal("working", RelatedPeerBadge());

        interjectGate.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await Task.WhenAll(childDone1, peerBDone1).WaitAsync(EventTimeout, ct);
        Frames.Pump();

        Assert.Equal(AgentGuiState.Ready, childAVm.State);
        Assert.Equal(ItemActivity.Done, activity.Of(childA.Id));
        Assert.Equal("done", ChildBadge());
        Assert.Equal("done", RelatedPeerBadge());
        Assert.Equal(0, parentObs.RibbonResets);
        Assert.Equal(initialTaskCount, fx.Coordinator.GetTasksForItem(parent.Id).Count);
        Assert.Same(initialPanelE, peerEVm.ActiveCollabThread);
        Assert.Equal("related peer draft", peerEVm.ActiveCollabThread!.InterjectText);
        Assert.Equal(0, obsC.TotalInvalidationSignals);
        Assert.Equal(0, obsD.TotalInvalidationSignals);
        Assert.Equal(0, obsBystander.TotalInvalidationSignals);

        // 2. Ordinary one-way ACP turn stopped by user: done -> working -> done without reregistration.
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopGate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        childAcp.PromptHandler = (_, _, _, token) =>
        {
            stopEntered.TrySetResult();
            return stopGate.Task.WaitAsync(token);
        };
        var stopDone = RibbonTestFixture.NextTurnFinished(childAVm);
        childAVm.InputText = "ordinary turn to stop";
        var stopSend = childAVm.SendPromptAsync();
        await stopEntered.Task.WaitAsync(EventTimeout, ct);
        Frames.Pump();
        Assert.Equal("working", ChildBadge());

        await childAVm.CancelTurnCommand.ExecuteAsync(null);
        await Task.WhenAll(stopSend, stopDone).WaitAsync(EventTimeout, ct);
        Frames.Pump();
        Assert.Equal(AgentGuiState.Ready, childAVm.State);
        Assert.Equal(ItemActivity.Done, activity.Of(childA.Id));
        Assert.Equal("done", ChildBadge());

        // 3. Ordinary one-way ACP turn ending with an error: done -> working -> done without reregistration.
        var errEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errGate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        childAcp.PromptHandler = (_, _, _, token) =>
        {
            errEntered.TrySetResult();
            return errGate.Task.WaitAsync(token);
        };
        var errDone = RibbonTestFixture.NextTurnFinished(childAVm);
        childAVm.InputText = "ordinary turn that errors";
        var errSend = childAVm.SendPromptAsync();
        await errEntered.Task.WaitAsync(EventTimeout, ct);
        Frames.Pump();
        Assert.Equal("working", ChildBadge());

        errGate.TrySetException(new InvalidOperationException("simulated acp failure"));
        await Task.WhenAll(errSend, errDone).WaitAsync(EventTimeout, ct);
        Frames.Pump();
        Assert.Equal(AgentGuiState.Ready, childAVm.State);
        Assert.Equal(ItemActivity.Done, activity.Of(childA.Id));
        Assert.Equal("done", ChildBadge());

        // 4. Immediately queued follow-up turn: stays working across replacement and settles to done after final turn.
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstGate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        childAcp.PromptHandler = (_, _, _, token) =>
        {
            var n = Interlocked.Increment(ref callCount);
            if (n == 1)
            {
                firstEntered.TrySetResult();
                return firstGate.Task.WaitAsync(token);
            }
            secondEntered.TrySetResult();
            return secondGate.Task.WaitAsync(token);
        };

        childAVm.InputText = "queued turn 1";
        var queuedSend1 = childAVm.SendPromptAsync();
        await firstEntered.Task.WaitAsync(EventTimeout, ct);
        Frames.Pump();
        Assert.Equal("working", ChildBadge());

        childAVm.InputText = "queued turn 2";
        await childAVm.SendPromptAsync();
        Assert.Single(childAVm.QueuedPrompts);

        firstGate.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await secondEntered.Task.WaitAsync(EventTimeout, ct);
        Frames.Pump();
        Assert.Equal(ItemActivity.Working, activity.Of(childA.Id));
        Assert.Equal("working", ChildBadge());

        var finalDone = RibbonTestFixture.NextTurnFinished(childAVm);
        secondGate.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await Task.WhenAll(queuedSend1, finalDone).WaitAsync(EventTimeout, ct);
        Frames.Pump();

        Assert.Equal(AgentGuiState.Ready, childAVm.State);
        Assert.Equal(ItemActivity.Done, activity.Of(childA.Id));
        Assert.Equal("done", ChildBadge());
        Assert.Equal("done", RelatedPeerBadge());
        Assert.Equal(initialTaskCount, fx.Coordinator.GetTasksForItem(parent.Id).Count);
        Assert.Same(initialPanelE, peerEVm.ActiveCollabThread);
        Assert.Equal("related peer draft", peerEVm.ActiveCollabThread!.InterjectText);
        Assert.Equal(0, obsC.TotalInvalidationSignals);
        Assert.Equal(0, obsD.TotalInvalidationSignals);
        Assert.Equal(0, obsBystander.TotalInvalidationSignals);
        Assert.Same(initialPanelC, peerCVm.ActiveCollabThread);
        Assert.Equal("unrelated draft", peerCVm.ActiveCollabThread!.InterjectText);

        // 5. After disposing an observing parent tab, further child activity transitions deliver zero signals to it.
        parentVm.Dispose();
        Frames.Pump();
        parentObs.ResetCounters();
        childAcp.PromptHandler = null;
        var postDisposeDone = RibbonTestFixture.NextTurnFinished(childAVm);
        childAVm.InputText = "post-dispose turn";
        await childAVm.SendPromptAsync();
        await postDisposeDone.WaitAsync(EventTimeout, ct);
        Frames.Pump();
        Assert.Equal(0, parentObs.TotalInvalidationSignals);
        Assert.Equal(0, obsC.TotalInvalidationSignals);
        Assert.Equal(0, obsD.TotalInvalidationSignals);
        Assert.Equal(0, obsBystander.TotalInvalidationSignals);
    }

    [AvaloniaFact]
    public void Disposing_coordinator_stops_activity_observer_notifications()
    {
        var projects = AppHost.Get<IProjectService>();
        var catalog = AppHost.Get<IAgentCatalog>();
        var scanner = AppHost.Get<IAgentScanner>();
        var settings = AppHost.Get<ISettingsStore>();
        var activity = AppHost.Get<IItemActivityService>();

        var tempDir = Directory.CreateTempSubdirectory("octopus-ribbon-coord-dispose-").FullName;
        try
        {
            var project = projects.AddProject(tempDir);
            var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Parent", Surface = AgentSurface.Gui };
            projects.AddItem(project, parent, activate: false);
            var child = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Child", Surface = AgentSurface.Gui, ParentItemId = parent.Id };
            projects.AddItem(project, child, activate: false);

            var observed = 0;
            using var server = new CollabMcpServer();
            var coordinator = new CollabCoordinator(projects, catalog, scanner, settings, server, activity: activity);
            using var sub = coordinator.SubscribeItemChanges(parent.Id, _ => Interlocked.Increment(ref observed));

            activity.ReportStatus(child.Id, ItemActivity.Working);
            Assert.Equal(1, observed);

            coordinator.Dispose();
            activity.ReportStatus(child.Id, ItemActivity.Idle);
            Assert.Equal(1, observed);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task Observer_isolation_for_registration_activity_transitions_and_interjection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fx = new RibbonTestFixture();
        var project = fx.AddProject("octopus-ribbon-observer-isolation-");
        var (p, _) = fx.CreateGuiTab(project, "Parent");
        var (a, av) = fx.CreateGuiTab(project, "ChildA", parentItemId: p.Id);
        var (b, bv) = fx.CreateGuiTab(project, "PeerB");
        var (child, _) = fx.CreateGuiTab(project, "Grandchild", parentItemId: a.Id);
        var (sibling, _) = fx.CreateGuiTab(project, "Sibling", parentItemId: p.Id);
        var (third, tv) = fx.CreateGuiTab(project, "SharedPeer");
        var (isolated, _) = fx.CreateGuiTab(project, "Isolated");

        var ab = await fx.SendPeerAndSettleAsync(a, av, b, bv, "AB", "ab", ct);
        await fx.SendPeerAndSettleAsync(a, av, third, tv, "AT", "at", ct);
        await fx.SendPeerAndSettleAsync(b, bv, third, tv, "BT", "bt", ct);
        Frames.Pump();

        var events = new List<(string Observer, string? Hint)>();
        var general = new List<string?>();
        var subscriptions = new[] { p, a, b, child, sibling, third, isolated }
            .Select(item => fx.Coordinator.SubscribeItemChanges(item.Id, hint => events.Add((item.Id, hint))))
            .ToList();
        void ObserveGeneral(string? hint) => general.Add(hint);
        fx.Coordinator.Changed += ObserveGeneral;

        try
        {
            // 1. Ordinary registration of A notifies exact related set once each with hint A.
            fx.Coordinator.RegisterGuiSession(av);
            Assert.Equal(new[] { p.Id, a.Id, b.Id, child.Id, third.Id }.Order().ToArray(), events.Select(e => e.Observer).Order().ToArray());
            Assert.All(events, e => Assert.Equal(a.Id, e.Hint));
            Assert.Equal([a.Id], general);

            // 2. Interjection through completion notifies only related observers (never sibling or isolated),
            //    delivers exactly start + finish updates to nonparticipant parent P, and emits only [A, B] on Changed.
            events.Clear();
            general.Clear();
            var da = RibbonTestFixture.NextTurnFinished(av);
            var db = RibbonTestFixture.NextTurnFinished(bv);
            Assert.True(await fx.Coordinator.InterjectInThreadAsync(ab, "observe", ct: ct));
            await Task.WhenAll(da, db).WaitAsync(EventTimeout, ct);
            Frames.Pump();

            Assert.DoesNotContain(events, e => e.Observer == sibling.Id || e.Observer == isolated.Id);
            Assert.Equal(new[] { p.Id, a.Id, b.Id, child.Id, third.Id }.Order().ToArray(), events.Select(e => e.Observer).Distinct().Order().ToArray());
            Assert.Equal(2, events.Count(e => e.Observer == p.Id));
            Assert.All(events.Where(e => e.Observer == p.Id), e => Assert.Equal(a.Id, e.Hint));
            Assert.Equal(new[] { a.Id, b.Id }.Order().ToArray(), general.Order().ToArray());
        }
        finally
        {
            foreach (var sub in subscriptions) sub.Dispose();
            fx.Coordinator.Changed -= ObserveGeneral;
        }
    }

    [AvaloniaFact]
    public async Task Self_chip_updates_across_overlapping_ordinary_turns_without_full_resets()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fx = new RibbonTestFixture();
        var activity = AppHost.Get<IItemActivityService>();
        var project = fx.AddProject("octopus-ribbon-self-overlap-");
        var (a, av) = fx.CreateGuiTab(project, "PeerA");
        var (b, bv) = fx.CreateGuiTab(project, "PeerB");
        var (_, uv) = fx.CreateGuiTab(project, "Unrelated");

        await av.ConnectAsync();
        var acpA = fx.Factory.Created.Last();
        await bv.ConnectAsync();
        var acpB = fx.Factory.Created.Last();

        var ab = await fx.SendPeerAndSettleAsync(a, av, b, bv, "AB", "hello", ct);
        Assert.Single(av.CollabRibbonNodes, n => n.Id == ab).ActivateCommand.Execute(null);
        Frames.Pump();

        CollabRibbonNodeViewModel SelfNode(AgentGuiTabViewModel vm) => Assert.Single(vm.CollabRibbonNodes, n => n.Kind == "self");
        var selfA = SelfNode(av);
        var selfB = SelfNode(bv);
        Assert.Equal("idle", selfA.Status);
        Assert.False(selfA.IsWorking);
        Assert.Equal("idle", selfB.Status);

        var obsA = fx.Observe(av);
        var obsB = fx.Observe(bv);
        var obsU = fx.Observe(uv);

        var gateA = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        acpA.PromptHandler = (_, _, _, token) => gateA.Task.WaitAsync(token);
        acpB.PromptHandler = (_, _, _, token) => gateB.Task.WaitAsync(token);

        av.InputText = "ordinary A";
        var sendA = av.SendPromptAsync();
        bv.InputText = "ordinary B";
        var sendB = bv.SendPromptAsync();
        Frames.Pump();

        Assert.Equal(AgentGuiState.Working, av.State);
        Assert.Equal(AgentGuiState.Working, bv.State);
        Assert.True(SelfNode(av).IsWorking);
        Assert.True(SelfNode(bv).IsWorking);

        // B finishes while A is still working: B's self chip returns to idle while A stays working.
        gateB.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await sendB.WaitAsync(EventTimeout, ct);
        Frames.Pump();

        Assert.Equal(AgentGuiState.Ready, bv.State);
        Assert.Equal("idle", SelfNode(bv).Status);
        Assert.False(SelfNode(bv).IsWorking);
        Assert.True(SelfNode(av).IsWorking);
        Assert.Equal("done", Assert.Single(av.CollabRibbonNodes, n => n.Id == ab).Status);

        // A finishes last: A's own chip must return to idle (not stay spinning).
        gateA.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await sendA.WaitAsync(EventTimeout, ct);
        Frames.Pump();

        Assert.Equal(AgentGuiState.Ready, av.State);
        Assert.Equal(ItemActivity.Done, activity.Of(a.Id));
        Assert.Equal("idle", SelfNode(av).Status);
        Assert.False(SelfNode(av).IsWorking);
        Assert.Same(selfA, SelfNode(av));
        Assert.Same(selfB, SelfNode(bv));
        Assert.Equal(0, obsA.RibbonResets);
        Assert.Equal(0, obsB.RibbonResets);
        Assert.Equal(0, obsU.TotalInvalidationSignals);
    }

    [AvaloniaFact]
    public async Task Interjection_avoids_duplicate_sender_reset_while_updating_badges_and_preserving_drafts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fx = new RibbonTestFixture();
        var activity = AppHost.Get<IItemActivityService>();
        var project = fx.AddProject("octopus-ribbon-interject-dedup-");
        var (p, pv) = fx.CreateGuiTab(project, "Parent");
        var (a, av) = fx.CreateGuiTab(project, "A", parentItemId: p.Id);
        var (b, bv) = fx.CreateGuiTab(project, "B");
        var (g, gv) = fx.CreateGuiTab(project, "Grandchild", parentItemId: a.Id);
        var (t, tv) = fx.CreateGuiTab(project, "SharedPeer");
        var (_, sv) = fx.CreateGuiTab(project, "Sibling", parentItemId: p.Id);
        var (u, uv) = fx.CreateGuiTab(project, "UnrelatedU");
        var (v, vv) = fx.CreateGuiTab(project, "UnrelatedV");

        await av.ConnectAsync();
        var acpA = fx.Factory.Created.Last();
        await bv.ConnectAsync();
        var acpB = fx.Factory.Created.Last();

        var ab = await fx.SendPeerAndSettleAsync(a, av, b, bv, "AB", "ab", ct);
        var at = await fx.SendPeerAndSettleAsync(a, av, t, tv, "AT", "at", ct);
        await fx.SendPeerAndSettleAsync(b, bv, t, tv, "BT", "bt", ct);
        var uvThread = await fx.SendPeerAndSettleAsync(u, uv, v, vv, "UV", "uv", ct);

        Assert.Single(av.CollabRibbonNodes, n => n.Id == ab).ActivateCommand.Execute(null);
        Assert.Single(bv.CollabRibbonNodes, n => n.Id == ab).ActivateCommand.Execute(null);
        Assert.Single(tv.CollabRibbonNodes, n => n.Id == at).ActivateCommand.Execute(null);
        Assert.Single(uv.CollabRibbonNodes, n => n.Id == uvThread).ActivateCommand.Execute(null);
        Frames.Pump();

        var panelA = Assert.IsType<CollabThreadPanelViewModel>(av.ActiveCollabThread);
        var panelB = Assert.IsType<CollabThreadPanelViewModel>(bv.ActiveCollabThread);
        var panelT = Assert.IsType<CollabThreadPanelViewModel>(tv.ActiveCollabThread);
        var panelU = Assert.IsType<CollabThreadPanelViewModel>(uv.ActiveCollabThread);
        panelB.InterjectText = "B draft";
        panelT.InterjectText = "T draft";
        panelU.InterjectText = "U draft";
        var initialMsgCount = panelB.Messages.Count;

        var obsP = fx.Observe(pv);
        var obsA = fx.Observe(av);
        var obsB = fx.Observe(bv);
        var obsG = fx.Observe(gv);
        var obsT = fx.Observe(tv);
        var obsS = fx.Observe(sv);
        var obsU = fx.Observe(uv);
        var obsV = fx.Observe(vv);

        var gateA = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        acpA.PromptHandler = (_, _, _, token) => gateA.Task.WaitAsync(token);
        acpB.PromptHandler = (_, _, _, token) => gateB.Task.WaitAsync(token);

        var doneA = RibbonTestFixture.NextTurnFinished(av);
        var doneB = RibbonTestFixture.NextTurnFinished(bv);
        panelA.InterjectText = "one";
        await panelA.SendInterjectCommand.ExecuteAsync(null);
        Frames.Pump();

        // Exactly one full reset on each participant for the appended thread message, and zero full resets on activity-only observers.
        Assert.Equal(1, obsA.RibbonResets);
        Assert.Equal(1, obsB.RibbonResets);
        Assert.Equal(0, obsP.RibbonResets);
        Assert.Equal(0, obsG.RibbonResets);
        Assert.Equal(0, obsT.RibbonResets);
        Assert.Equal(0, obsS.TotalInvalidationSignals);
        Assert.Equal(0, obsU.TotalInvalidationSignals);
        Assert.Equal(0, obsV.TotalInvalidationSignals);
        Assert.Equal("working", Assert.Single(pv.CollabRibbonNodes, n => n.Id == a.Id).Status);
        Assert.Equal("working", Assert.Single(gv.CollabRibbonNodes, n => n.Id == a.Id).Status);
        Assert.Equal("working", Assert.Single(av.CollabRibbonNodes, n => n.Kind == "self").Status);
        Assert.Equal("working", Assert.Single(bv.CollabRibbonNodes, n => n.Kind == "self").Status);
        Assert.Equal("one", panelA.Messages.Last().Text);
        Assert.Equal("one", panelB.Messages.Last().Text);

        // Equal activity reports produce zero resets.
        obsA.ResetCounters();
        obsB.ResetCounters();
        obsP.ResetCounters();
        for (var i = 0; i < 10; i++)
        {
            activity.ReportStatus(a.Id, ItemActivity.Working);
            activity.ReportRemote(b.Id, ItemActivity.Working);
        }
        Frames.Pump();
        Assert.Equal(0, obsA.RibbonResets + obsB.RibbonResets + obsP.RibbonResets);

        // Finishing B then A updates both self chips and all related badges in place with zero full resets.
        gateB.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await doneB.WaitAsync(EventTimeout, ct);
        Frames.Pump();
        Assert.Equal("idle", Assert.Single(bv.CollabRibbonNodes, n => n.Kind == "self").Status);
        Assert.Equal("working", Assert.Single(av.CollabRibbonNodes, n => n.Kind == "self").Status);

        gateA.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await doneA.WaitAsync(EventTimeout, ct);
        Frames.Pump();
        Assert.Equal("idle", Assert.Single(av.CollabRibbonNodes, n => n.Kind == "self").Status);
        Assert.Equal("done", Assert.Single(pv.CollabRibbonNodes, n => n.Id == a.Id).Status);
        Assert.Equal("done", Assert.Single(gv.CollabRibbonNodes, n => n.Id == a.Id).Status);
        Assert.Equal(0, obsA.RibbonResets + obsB.RibbonResets + obsP.RibbonResets + obsG.RibbonResets + obsT.RibbonResets);

        // While both A and B are already busy, a second interjection still refreshes both participant panels once and preserves drafts.
        gateA = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateB = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        acpA.PromptHandler = (_, _, _, token) => gateA.Task.WaitAsync(token);
        acpB.PromptHandler = (_, _, _, token) => gateB.Task.WaitAsync(token);
        av.InputText = "busy A";
        var userA = av.SendPromptAsync();
        bv.InputText = "busy B";
        var userB = bv.SendPromptAsync();
        Frames.Pump();

        obsA.ResetCounters();
        obsB.ResetCounters();
        panelA.InterjectText = "two";
        await panelA.SendInterjectCommand.ExecuteAsync(null);
        Frames.Pump();

        Assert.Equal(1, obsA.RibbonResets);
        Assert.Equal(1, obsB.RibbonResets);
        Assert.Equal(initialMsgCount + 2, panelA.Messages.Count);
        Assert.Equal(initialMsgCount + 2, panelB.Messages.Count);
        Assert.Equal("", panelA.InterjectText);
        Assert.Equal("B draft", panelB.InterjectText);
        Assert.Equal("T draft", panelT.InterjectText);
        Assert.Equal("U draft", panelU.InterjectText);
        Assert.Same(panelA, av.ActiveCollabThread);
        Assert.Same(panelB, bv.ActiveCollabThread);
        Assert.Same(panelT, tv.ActiveCollabThread);
        Assert.Same(panelU, uv.ActiveCollabThread);
        Assert.Equal(0, obsS.TotalInvalidationSignals);
        Assert.Equal(0, obsU.TotalInvalidationSignals);
        Assert.Equal(0, obsV.TotalInvalidationSignals);

        gateA.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        gateB.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await Task.WhenAll(userA, userB).WaitAsync(EventTimeout, ct);
        Frames.Pump();
        Assert.Equal("idle", Assert.Single(av.CollabRibbonNodes, n => n.Kind == "self").Status);
        Assert.Equal("idle", Assert.Single(bv.CollabRibbonNodes, n => n.Kind == "self").Status);
    }

    [AvaloniaFact]
    public async Task Mounted_parent_and_peer_chips_update_on_first_prompt_and_rest_renames_without_resets()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fx = new RibbonTestFixture();
        var activity = AppHost.Get<IItemActivityService>();
        var project = fx.AddProject("octopus-ribbon-mounted-titles-");
        var (p, pv) = fx.CreateGuiTab(project, "Parent");
        var (a, av) = fx.CreateGuiTab(project, "A", parentItemId: p.Id);
        var (b, bv) = fx.CreateGuiTab(project, "B");
        var (g, gv) = fx.CreateGuiTab(project, "Grandchild", parentItemId: a.Id);
        var (_, sv) = fx.CreateGuiTab(project, "Sibling", parentItemId: p.Id);
        var (u, uv) = fx.CreateGuiTab(project, "UnrelatedU");
        var (v, vv) = fx.CreateGuiTab(project, "UnrelatedV");

        await pv.ConnectAsync();
        await av.ConnectAsync();
        var acpA = fx.Factory.Created.Last();
        await bv.ConnectAsync();

        var ab = await fx.SendPeerAndSettleAsync(a, av, b, bv, "AB", "ab", ct);
        var uvThread = await fx.SendPeerAndSettleAsync(u, uv, v, vv, "UV", "uv", ct);

        Assert.Single(bv.CollabRibbonNodes, n => n.Id == ab).ActivateCommand.Execute(null);
        Assert.Single(uv.CollabRibbonNodes, n => n.Id == uvThread).ActivateCommand.Execute(null);
        Frames.Pump();

        var panelB = Assert.IsType<CollabThreadPanelViewModel>(bv.ActiveCollabThread);
        var panelU = Assert.IsType<CollabThreadPanelViewModel>(uv.ActiveCollabThread);
        panelB.InterjectText = "B draft";
        panelU.InterjectText = "U draft";

        static Window Mount(AgentGuiTabViewModel vm)
        {
            var window = new Window { Content = new AgentGuiTabView { DataContext = vm }, Width = 980, Height = 720 };
            window.Show();
            Frames.Pump();
            return window;
        }

        static string ChipText(Window window, string kind, string id)
        {
            Frames.Pump();
            var chip = window.GetVisualDescendants()
                .OfType<Button>()
                .Single(btn => btn.DataContext is CollabRibbonNodeViewModel node && node.Kind == kind && node.Id == id);
            return chip.GetVisualDescendants()
                .OfType<TextBlock>()
                .Single(t => t.MaxWidth == 145)
                .Text!;
        }

        var wp = Mount(pv);
        var wb = Mount(bv);
        var wa = Mount(av);
        var wg = Mount(gv);
        try
        {
            var parentChildNode = Assert.Single(pv.CollabRibbonNodes, n => n.Kind == "child" && n.Id == a.Id);
            var peerNode = Assert.Single(bv.CollabRibbonNodes, n => n.Kind == "peer" && n.Id == ab);
            var selfNode = Assert.Single(av.CollabRibbonNodes, n => n.Kind == "self" && n.Id == a.Id);
            var grandParentNode = Assert.Single(gv.CollabRibbonNodes, n => n.Kind == "parent" && n.Id == a.Id);

            Assert.True(peerNode.IsCurrent);
            Assert.Equal("A", ChipText(wp, "child", a.Id));
            Assert.Equal("A", ChipText(wb, "peer", ab));
            Assert.Equal("A", ChipText(wa, "self", a.Id));
            Assert.Equal("A", ChipText(wg, "parent", a.Id));

            var labelUiChecks = new List<bool>();
            var labelChanges = 0;
            void TrackLabel(object? _, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(CollabRibbonNodeViewModel.Label))
                {
                    Interlocked.Increment(ref labelChanges);
                    labelUiChecks.Add(Dispatcher.UIThread.CheckAccess());
                }
            }
            parentChildNode.PropertyChanged += TrackLabel;
            peerNode.PropertyChanged += TrackLabel;
            selfNode.PropertyChanged += TrackLabel;
            grandParentNode.PropertyChanged += TrackLabel;

            var obsP = fx.Observe(pv);
            var obsA = fx.Observe(av);
            var obsB = fx.Observe(bv);
            var obsG = fx.Observe(gv);
            var obsS = fx.Observe(sv);
            var obsU = fx.Observe(uv);
            var obsV = fx.Observe(vv);

            try
            {
                // 1. First ordinary prompt updates mounted child, peer, self, and parent-ref chips in place with zero resets.
                var gate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                acpA.PromptHandler = (_, _, _, token) => gate.Task.WaitAsync(token);
                av.InputText = "Compare the release notes";
                var send = av.SendPromptAsync();

                Assert.Equal("Compare the release notes", a.DisplayTitle);
                Assert.Equal("Compare the release notes", ChipText(wp, "child", a.Id));
                Assert.Equal("Compare the release notes", ChipText(wb, "peer", ab));
                Assert.Equal("Compare the release notes", ChipText(wa, "self", a.Id));
                Assert.Equal("Compare the release notes", ChipText(wg, "parent", a.Id));

                gate.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
                await send.WaitAsync(EventTimeout, ct);

                Assert.Equal("Compare the release notes", ChipText(wp, "child", a.Id));
                Assert.Equal("Compare the release notes", ChipText(wb, "peer", ab));
                Assert.Equal("Compare the release notes", ChipText(wa, "self", a.Id));
                Assert.Equal("Compare the release notes", ChipText(wg, "parent", a.Id));
                Assert.Equal(0, obsP.RibbonResets + obsA.RibbonResets + obsB.RibbonResets + obsG.RibbonResets);
                Assert.Equal(0, obsS.TotalInvalidationSignals + obsU.TotalInvalidationSignals + obsV.TotalInvalidationSignals);
                Assert.Same(parentChildNode, Assert.Single(pv.CollabRibbonNodes, n => n.Kind == "child" && n.Id == a.Id));
                Assert.Same(peerNode, Assert.Single(bv.CollabRibbonNodes, n => n.Kind == "peer" && n.Id == ab));
                Assert.True(peerNode.IsCurrent);
                Assert.Same(panelB, bv.ActiveCollabThread);
                Assert.Equal("B draft", bv.ActiveCollabThread!.InterjectText);
                Assert.Same(panelU, uv.ActiveCollabThread);
                Assert.Equal("U draft", uv.ActiveCollabThread!.InterjectText);

                // 2. Title change at rest (no activity transition) updates all mounted chips with zero resets and zero activity events.
                var activityEvents = 0;
                void CountActivity(object? _, string __) => Interlocked.Increment(ref activityEvents);
                activity.Changed += CountActivity;
                try
                {
                    var statusBefore = (parentChildNode.Status, peerNode.Status, selfNode.Status, grandParentNode.Status);
                    labelChanges = 0;
                    fx.Projects.RenameItem(a.Id, "Release Review");

                    Assert.Equal("Release Review", ChipText(wp, "child", a.Id));
                    Assert.Equal("Release Review", ChipText(wb, "peer", ab));
                    Assert.Equal("Release Review", ChipText(wa, "self", a.Id));
                    Assert.Equal("Release Review", ChipText(wg, "parent", a.Id));
                    Assert.Equal(0, activityEvents);
                    Assert.Equal(4, labelChanges);
                    Assert.Equal(statusBefore, (parentChildNode.Status, peerNode.Status, selfNode.Status, grandParentNode.Status));
                    Assert.Equal(0, obsP.RibbonResets + obsA.RibbonResets + obsB.RibbonResets + obsG.RibbonResets);
                    Assert.Equal(0, obsS.TotalInvalidationSignals + obsU.TotalInvalidationSignals + obsV.TotalInvalidationSignals);

                    // 3. Repeat identical rename and overridden underlying title emit zero Label notifications and zero resets.
                    labelChanges = 0;
                    fx.Projects.RenameItem(a.Id, "Release Review");
                    fx.Projects.SetItemTitle(a.Id, "Underlying Title");
                    Frames.Pump();

                    Assert.Equal(0, labelChanges);
                    Assert.Equal("Release Review", ChipText(wp, "child", a.Id));
                    Assert.Equal("Release Review", ChipText(wb, "peer", ab));
                    Assert.Equal(0, obsP.RibbonResets + obsA.RibbonResets + obsB.RibbonResets + obsG.RibbonResets);

                    // Clearing the custom title reveals the underlying title in place with zero resets.
                    fx.Projects.RenameItem(a.Id, null);
                    Assert.Equal("Underlying Title", ChipText(wp, "child", a.Id));
                    Assert.Equal("Underlying Title", ChipText(wb, "peer", ab));
                    Assert.Equal("Underlying Title", ChipText(wa, "self", a.Id));
                    Assert.Equal("Underlying Title", ChipText(wg, "parent", a.Id));
                    Assert.Equal(4, labelChanges);
                    Assert.Equal(0, obsP.RibbonResets + obsA.RibbonResets + obsB.RibbonResets + obsG.RibbonResets);
                }
                finally
                {
                    activity.Changed -= CountActivity;
                }

                // 4. Title change during a live bubbled approval updates mounted chip text while preserving approval object & waiting badge.
                var approval = new CollabBubbledApproval("apr-title", p.Id, a.Id, "opencode", "Underlying Title", "Bash", "dotnet test", null, _ => { });
                fx.Coordinator.ReportChildApproval(approval);
                Frames.Pump();
                var approvalVm = Assert.Single(pv.BubbledApprovals);
                var approvalChildNode = Assert.Single(pv.CollabRibbonNodes, n => n.Kind == "child" && n.Id == a.Id);
                Assert.Equal("waiting_approval", approvalChildNode.Status);

                obsP.ResetCounters();
                obsS.ResetCounters();
                fx.Projects.RenameItem(a.Id, "Approved Child");
                Assert.Equal("Approved Child", ChipText(wp, "child", a.Id));
                Assert.Equal("waiting_approval", approvalChildNode.Status);
                Assert.Same(approvalVm, Assert.Single(pv.BubbledApprovals));
                Assert.Equal(0, obsP.RibbonResets);
                Assert.Equal(0, obsP.ApprovalResets);
                Assert.Equal(0, obsS.TotalInvalidationSignals);

                fx.Coordinator.ClearChildApproval(a.Id, "apr-title");
                Frames.Pump();
                Assert.Empty(pv.BubbledApprovals);

                // 5. After observer tab disposal, further renames deliver zero resets and zero Label notifications to the disposed tab.
                var heldChildNode = Assert.Single(pv.CollabRibbonNodes, n => n.Kind == "child" && n.Id == a.Id);
                var lateLabelChanges = 0;
                heldChildNode.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(CollabRibbonNodeViewModel.Label))
                    {
                        Interlocked.Increment(ref lateLabelChanges);
                    }
                };
                pv.Dispose();
                Frames.Pump();
                obsP.ResetCounters();
                obsB.ResetCounters();
                obsS.ResetCounters();

                fx.Projects.RenameItem(a.Id, "After Parent Dispose");
                Frames.Pump();

                Assert.Equal(0, lateLabelChanges);
                Assert.Equal(0, obsP.TotalInvalidationSignals);
                Assert.Equal("Approved Child", heldChildNode.Label);
                Assert.Equal("After Parent Dispose", ChipText(wb, "peer", ab));
                Assert.Equal(0, obsB.RibbonResets);
                Assert.True(peerNode.IsCurrent);
                Assert.Same(panelB, bv.ActiveCollabThread);
                Assert.Equal("B draft", bv.ActiveCollabThread!.InterjectText);
                Assert.Equal(0, obsS.TotalInvalidationSignals + obsU.TotalInvalidationSignals + obsV.TotalInvalidationSignals);
                Assert.NotEmpty(labelUiChecks);
                Assert.All(labelUiChecks, Assert.True);
            }
            finally
            {
                parentChildNode.PropertyChanged -= TrackLabel;
                peerNode.PropertyChanged -= TrackLabel;
                selfNode.PropertyChanged -= TrackLabel;
                grandParentNode.PropertyChanged -= TrackLabel;
            }
        }
        finally
        {
            wg.Close();
            wa.Close();
            wb.Close();
            wp.Close();
        }
    }

    [AvaloniaFact]
    public void Disposing_coordinator_stops_title_observer_notifications()
    {
        var projects = AppHost.Get<IProjectService>();
        var catalog = AppHost.Get<IAgentCatalog>();
        var scanner = AppHost.Get<IAgentScanner>();
        var settings = AppHost.Get<ISettingsStore>();
        var activity = AppHost.Get<IItemActivityService>();

        var tempDir = Directory.CreateTempSubdirectory("octopus-ribbon-title-coord-dispose-").FullName;
        try
        {
            var project = projects.AddProject(tempDir);
            var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Parent", Surface = AgentSurface.Gui };
            projects.AddItem(project, parent, activate: false);
            var child = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Child", Surface = AgentSurface.Gui, ParentItemId = parent.Id };
            projects.AddItem(project, child, activate: false);

            var observed = 0;
            using var server = new CollabMcpServer();
            var coordinator = new CollabCoordinator(projects, catalog, scanner, settings, server, activity: activity);
            using var sub = coordinator.SubscribeItemChanges(parent.Id, _ => Interlocked.Increment(ref observed));

            projects.RenameItem(child.Id, "Renamed Child");
            Assert.Equal(1, observed);

            coordinator.Dispose();
            projects.RenameItem(child.Id, "Renamed After Dispose");
            Assert.Equal(1, observed);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
