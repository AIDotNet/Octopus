using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.Acp.Protocol;
using Octopus.Agents;
using Octopus.App.Activity;
using Octopus.App.AgentGui;
using Octopus.App.Collab;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Remote;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Remote.Client;
using Octopus.Remote.Host;
using Octopus.Remote.Protocol;
using Octopus.Remote.Security;
using Octopus.Remote.Transport;
using Octopus.Storage;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>
/// Permanent regression tests for remote collaboration correlation (#41 review follow-up, independent of BUG-06):
/// scoping fresh-launch fallback to the original launch only, preserving turn attribution across prompt text
/// truncation (&gt;32 KiB) and snapshot trimming, isolating reopened same-task rounds via per-delivery identity, and
/// preserving safe behavior with older hosts that do not emit structured correlation metadata.
/// </summary>
public sealed class CollabRemoteCorrelationTests
{
    private sealed class StubParentBridge(ProjectItem item, string cwd) : ICollabSessionBridge
    {
        private readonly object _gate = new();
        public string ItemId => item.Id;
        public string AgentId => item.AgentId ?? "claude";
        public string DisplayTitle => item.DisplayTitle;
        public string WorkingDirectory => cwd;
        public bool IsBusy => false;
        public bool IsConnected => true;
        public string? CurrentStatusDetail => null;
        public List<string> DeliveredPrompts { get; } = [];

        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default)
        {
            lock (_gate) DeliveredPrompts.Add(promptText);
            return Task.FromResult(true);
        }

        public string ReadRecentSummary(int maxTurns = 5) => "";
    }

    private sealed class FakeRemoteHosts : ICollabRemoteHosts
    {
        public Dictionary<string, ICollabRemoteHost> Hosts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);

        public OlderFakeRemoteHost AddOlderFake(string hostId, string name)
        {
            var host = new OlderFakeRemoteHost(name);
            Hosts[hostId] = host;
            Names[hostId] = name;
            return host;
        }

        public void AddHost(string hostId, string name, ICollabRemoteHost host)
        {
            Hosts[hostId] = host;
            Names[hostId] = name;
        }

        public Task<ICollabRemoteHost> ConnectAsync(string hostId, CancellationToken ct) =>
            Hosts.TryGetValue(hostId, out var host)
                ? Task.FromResult(host)
                : Task.FromException<ICollabRemoteHost>(new IOException("offline"));

        public string HostName(string hostId) => Names.TryGetValue(hostId, out var name) ? name : hostId;
        public void RequestSync(string hostId) { }
    }

    /// <summary>Simulates an older peer host that does not understand or emit structured correlation fields.</summary>
    private sealed class OlderFakeRemoteHost(string name) : ICollabRemoteHost
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);
        private int _items;

        public string Name => name;
        public bool SupportsAcpGui => true;
        public Task Completion => _closed.Task;
        public event Action<AcpUpdateMessage>? AcpUpdated;
        public List<CreateItemMessage> Created { get; } = [];
        public List<(string ItemId, string Text)> Prompts { get; } = [];
        public ConcurrentQueue<string> Cancels { get; } = new();
        public Dictionary<string, AcpSnapshotMessage> Snapshots { get; } = new(StringComparer.Ordinal);
        public Func<string, CancellationToken, Task<AcpSnapshotMessage>>? OnAttach { get; set; }

        public Task<ItemMessage> CreateItemAsync(CreateItemMessage request, CancellationToken ct)
        {
            Created.Add(request);
            var id = $"old-remote-{++_items}";
            return Task.FromResult(new ItemMessage
            {
                Id = id,
                ProjectId = request.ProjectId,
                Kind = request.Kind,
                AgentId = request.AgentId,
                Title = request.Title ?? request.AgentId ?? "",
            });
        }

        public Task<AcpSnapshotMessage> AttachConversationAsync(string itemId, CancellationToken ct)
        {
            if (OnAttach is { } handler) return handler(itemId, ct);
            var snapshot = Snapshots.TryGetValue(itemId, out var configured)
                ? configured
                : new AcpSnapshotMessage { ItemId = itemId, Sequence = 0, State = AcpStates.Connecting };
            _sequences[itemId] = snapshot.Sequence;
            return Task.FromResult(snapshot);
        }

        public Task DetachConversationAsync(string itemId, CancellationToken ct) => Task.CompletedTask;

        public Task SendPromptAsync(string itemId, string text, CancellationToken ct)
        {
            Prompts.Add((itemId, text));
            return Task.CompletedTask;
        }

        public Task CancelTurnAsync(string itemId, CancellationToken ct)
        {
            Cancels.Enqueue(itemId);
            return Task.CompletedTask;
        }

        public Task RespondAsync(AcpRespondMessage answer, CancellationToken ct) => Task.CompletedTask;

        public void Push(string itemId, string? state, params AcpEntryMessage[] upserts)
        {
            var seq = _sequences[itemId] = _sequences.GetValueOrDefault(itemId) + 1;
            AcpUpdated?.Invoke(new AcpUpdateMessage
            {
                ItemId = itemId,
                Sequence = seq,
                State = state,
                Upserts = [.. upserts],
            });
        }

        public void PushRaw(AcpUpdateMessage update)
        {
            _sequences[update.ItemId] = Math.Max(_sequences.GetValueOrDefault(update.ItemId), update.Sequence);
            AcpUpdated?.Invoke(update);
        }
    }

    private sealed class LoopbackRemoteHostAdapter : ICollabRemoteHost
    {
        private readonly ICollabRemoteHost _inner;
        private readonly object _gate = new();
        private long _lastEmittedSeq;
        private long _seqOffset;
        public List<AcpUpdateMessage> ObservedUpdates { get; } = [];
        public Func<AcpSnapshotMessage, AcpSnapshotMessage>? TransformSnapshot { get; set; }
        public Func<AcpUpdateMessage, bool>? SuppressUpdate { get; set; }
        public Action<AcpUpdateMessage>? AfterDelivery { get; set; }
        public int UnscopedCancelRequests;
        public int ScopedCancelRequests;

        public LoopbackRemoteHostAdapter(ICollabRemoteHost inner)
        {
            _inner = inner;
            _inner.AcpUpdated += update =>
            {
                AcpUpdateMessage forwarded;
                lock (_gate)
                {
                    ObservedUpdates.Add(update);
                    forwarded = _seqOffset == 0
                        ? update
                        : new AcpUpdateMessage
                        {
                            ItemId = update.ItemId,
                            Sequence = update.Sequence + _seqOffset,
                            Upserts = update.Upserts,
                            Removed = update.Removed,
                            State = update.State,
                            StatusDetail = update.StatusDetail,
                            ErrorText = update.ErrorText,
                            QueuedCount = update.QueuedCount,
                            Composer = update.Composer,
                            Tasks = update.Tasks,
                        };
                    if (forwarded.Sequence > _lastEmittedSeq) _lastEmittedSeq = forwarded.Sequence;
                }
                if (SuppressUpdate?.Invoke(forwarded) == true) return;
                AcpUpdated?.Invoke(forwarded);
                AfterDelivery?.Invoke(forwarded);
            };
        }

        public bool SupportsAcpGui => _inner.SupportsAcpGui;
        public bool SupportsScopedCancel => _inner.SupportsScopedCancel;
        public Task Completion => _inner.Completion;
        public event Action<AcpUpdateMessage>? AcpUpdated;

        public Task<ItemMessage> CreateItemAsync(CreateItemMessage request, CancellationToken ct) => _inner.CreateItemAsync(request, ct);

        public async Task<AcpSnapshotMessage> AttachConversationAsync(string itemId, CancellationToken ct)
        {
            var snapshot = await _inner.AttachConversationAsync(itemId, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _seqOffset = 0;
                if (snapshot.Sequence > _lastEmittedSeq) _lastEmittedSeq = snapshot.Sequence;
            }
            return TransformSnapshot is { } transform ? transform(snapshot) : snapshot;
        }

        public Task DetachConversationAsync(string itemId, CancellationToken ct) => _inner.DetachConversationAsync(itemId, ct);
        public Task SendPromptAsync(string itemId, string text, CancellationToken ct) => _inner.SendPromptAsync(itemId, text, ct);
        public Task SendPromptAsync(string itemId, string text, string? collabDeliveryId, CancellationToken ct) => _inner.SendPromptAsync(itemId, text, collabDeliveryId, ct);
        public Task CancelTurnAsync(string itemId, CancellationToken ct)
        {
            Interlocked.Increment(ref UnscopedCancelRequests);
            return _inner.CancelTurnAsync(itemId, ct);
        }
        public Task CancelTurnAsync(string itemId, string? collabThreadId, string? collabTaskId, string? collabDeliveryId, CancellationToken ct)
        {
            Interlocked.Increment(ref ScopedCancelRequests);
            return _inner.CancelTurnAsync(itemId, collabThreadId, collabTaskId, collabDeliveryId, ct);
        }
        public Task RespondAsync(AcpRespondMessage answer, CancellationToken ct) => _inner.RespondAsync(answer, ct);

        public void InjectSyntheticUpdate(string itemId, string? state, params AcpEntryMessage[] upserts)
        {
            long next;
            lock (_gate)
            {
                _seqOffset++;
                next = ++_lastEmittedSeq;
            }
            AcpUpdated?.Invoke(new AcpUpdateMessage
            {
                ItemId = itemId,
                Sequence = next,
                State = state,
                Upserts = [.. upserts],
            });
        }

        public void InjectGapUpdate(string itemId)
        {
            long jump;
            lock (_gate) jump = _lastEmittedSeq + 5;
            AcpUpdated?.Invoke(new AcpUpdateMessage
            {
                ItemId = itemId,
                Sequence = jump,
                State = AcpStates.Ready,
            });
        }
    }

    private sealed class LoopbackHostSide : IAsyncDisposable
    {
        public DeviceIdentity Identity { get; } = DeviceIdentity.Create();
        public PairedPeerStore Devices { get; } = new(null);
        public ProjectService Projects { get; }
        public ITabContentRegistry Tabs { get; } = AppHost.Get<ITabContentRegistry>();
        public HostWorkspace Workspace { get; }
        public RemoteHostService Service { get; }

        public LoopbackHostSide(ProjectService? projects = null, bool advertiseCollabCancel = true)
        {
            Projects = projects ?? new ProjectService(new InMemoryProjectStore(), AppHost.Get<PathComparer>());
            Workspace = new HostWorkspace(
                Projects,
                AppHost.Get<IItemLaunchService>(),
                AppHost.Get<ITerminalConnectionFactory>(),
                AppHost.Get<TerminalSessionRegistry>(),
                Tabs,
                AppHost.Get<IItemActivityService>(),
                AppHost.Get<IAgentCatalog>(),
                AppHost.Get<IAgentScanner>(),
                AppHost.Get<ISettingsStore>(),
                AppHost.Get<IShellResolver>(),
                AppHost.Get<Octopus.FileSystem.IFileTreeService>(),
                AppHost.Get<Octopus.Git.IGitService>(),
                AppHost.Get<PathComparer>());
            List<string> capabilities = [RemoteMethods.CapabilityWorkspace, RemoteMethods.CapabilityAcpGui];
            if (advertiseCollabCancel)
            {
                capabilities.Add(RemoteMethods.CapabilityAcpCollabCancel);
            }
            Service = new RemoteHostService(
                Identity,
                () => new DeviceDescription("Loopback Host", DeviceKind.Desktop),
                Devices,
                Workspace,
                new HelloMessage
                {
                    AppVersion = "test",
                    Capabilities = capabilities,
                    TerminalBuild = "build-1",
                });
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            Workspace.Dispose();
            Identity.Dispose();
        }
    }

    private sealed class DirectClientHost(RemoteHostClient client) : ICollabRemoteHost
    {
        public bool SupportsAcpGui => client.SupportsAcpGui;
        public bool SupportsScopedCancel => client.SupportsAcpCollabCancel;
        public Task Completion => client.Completion;
        public event Action<AcpUpdateMessage>? AcpUpdated
        {
            add => client.AcpUpdated += value;
            remove => client.AcpUpdated -= value;
        }

        public Task<ItemMessage> CreateItemAsync(CreateItemMessage request, CancellationToken ct) => client.CreateItemAsync(request, ct);
        public Task<AcpSnapshotMessage> AttachConversationAsync(string itemId, CancellationToken ct) => client.AttachConversationAsync(itemId, ct);
        public Task DetachConversationAsync(string itemId, CancellationToken ct) => client.DetachConversationAsync(itemId, ct);
        public Task SendPromptAsync(string itemId, string text, CancellationToken ct) => client.SendPromptAsync(itemId, text, null, ct);
        public Task SendPromptAsync(string itemId, string text, string? collabDeliveryId, CancellationToken ct) =>
            client.SendPromptAsync(itemId, text, attachments: null, mentions: null, immediate: false, collabDeliveryId, ct);
        public Task CancelTurnAsync(string itemId, CancellationToken ct) => client.CancelTurnAsync(itemId, ct);
        public Task CancelTurnAsync(string itemId, string? collabThreadId, string? collabTaskId, string? collabDeliveryId, CancellationToken ct) =>
            client.CancelTurnAsync(itemId, collabThreadId, collabTaskId, collabDeliveryId, ct);
        public Task RespondAsync(AcpRespondMessage answer, CancellationToken ct) => client.RespondAsync(answer, ct);
    }

    private static JsonObject Body(JsonObject toolResult) =>
        JsonNode.Parse(toolResult["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

    private static AcpEntryMessage UserEntry(string id, string text) =>
        new() { Id = id, Kind = AcpEntryKinds.User, Text = text };

    private static AcpEntryMessage AssistantEntry(string id, string? text, bool streaming = false) =>
        new() { Id = id, Kind = AcpEntryKinds.Assistant, Text = text, IsStreaming = streaming };

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, int timeoutMs = 4000, string? message = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, ct);
        }
        Assert.True(condition(), message ?? "Condition was not met in time.");
    }

    private static async Task<T> WaitTaskAsync<T>(Task<T> task, CancellationToken ct, int timeoutMs = 6000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!task.IsCompleted && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, ct);
        }
        Assert.True(task.IsCompleted, "Operation did not finish in time.");
        return await task;
    }

    private static async Task WaitTaskAsync(Task task, CancellationToken ct, int timeoutMs = 6000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!task.IsCompleted && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, ct);
        }
        Assert.True(task.IsCompleted, "Operation did not finish in time.");
        await task;
    }

    private static async Task<(CollabCoordinator Coordinator, ProjectItem Parent, Project RemoteProject, OlderFakeRemoteHost Host)> SetUpOlderFakeRemoteAsync(string prefix)
    {
        await Task.Yield();
        var dir = Directory.CreateTempSubdirectory(prefix).FullName;
        var projects = AppHost.Get<IProjectService>();
        var local = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Lead", Surface = AgentSurface.Gui };
        projects.AddItem(local, parent);

        var remote = projects.AddProject(Directory.CreateTempSubdirectory(prefix + "remote-").FullName);
        remote.RemoteHost = "old-host";
        remote.RemoteId = "rp-old";
        remote.Name = "Old Host Repo";
        projects.SetCurrent(local.Id);

        var fakes = new FakeRemoteHosts();
        var host = fakes.AddOlderFake("old-host", "Older Host");
        var coordinator = new CollabCoordinator(
            projects,
            AppHost.Get<IAgentCatalog>(),
            AppHost.Get<IAgentScanner>(),
            AppHost.Get<ISettingsStore>(),
            new CollabMcpServer(),
            remoteHostsFactory: () => fakes);
        coordinator.RegisterGuiSession(new StubParentBridge(parent, dir));
        return (coordinator, parent, remote, host);
    }

    private static async Task<(
        LoopbackHostSide HostSide,
        DeviceIdentity ClientDevice,
        RemoteHostClient Client,
        LoopbackRemoteHostAdapter Adapter,
        CollabCoordinator Coordinator,
        ProjectItem Parent,
        Project HostProject,
        Project MirroredRemoteProject)> SetUpLoopbackAsync(string prefix, CancellationToken ct)
    {
        var hostSide = new LoopbackHostSide();
        var clientDevice = DeviceIdentity.Create();
        var deviceDesc = new DeviceDescription("Local Desktop", DeviceKind.Desktop);
        hostSide.Devices.Add(clientDevice.PublicKey, deviceDesc);

        var hostProject = hostSide.Projects.AddProject(Directory.CreateTempSubdirectory(prefix + "host-").FullName);
        var (a, b) = InMemoryTransport.CreatePair();
        _ = hostSide.Service.ServeAsync(b);
        var client = await WaitTaskAsync(
            RemoteHostClient.ConnectAsync(
                a,
                clientDevice,
                deviceDesc,
                PeerExpectation.Paired(hostSide.Identity.PublicKey),
                new HelloMessage
                {
                    AppVersion = "test",
                    Capabilities = [RemoteMethods.CapabilityWorkspace, RemoteMethods.CapabilityAcpGui, RemoteMethods.CapabilityAcpCollabCancel],
                    TerminalBuild = "build-1",
                }),
            ct);

        var adapter = new LoopbackRemoteHostAdapter(new DirectClientHost(client));
        var localDir = Directory.CreateTempSubdirectory(prefix + "local-").FullName;
        var localProjects = AppHost.Get<IProjectService>();
        var localProject = localProjects.AddProject(localDir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Lead", Surface = AgentSurface.Gui };
        localProjects.AddItem(localProject, parent);

        var mirrored = localProjects.AddProject(Directory.CreateTempSubdirectory(prefix + "mirror-").FullName);
        mirrored.RemoteHost = "loopback-host";
        mirrored.RemoteId = hostProject.Id;
        mirrored.Name = "Host Repo";
        localProjects.SetCurrent(localProject.Id);

        var hosts = new FakeRemoteHosts();
        hosts.AddHost("loopback-host", "Loopback Host", adapter);

        var coordinator = new CollabCoordinator(
            localProjects,
            AppHost.Get<IAgentCatalog>(),
            AppHost.Get<IAgentScanner>(),
            AppHost.Get<ISettingsStore>(),
            new CollabMcpServer(),
            remoteHostsFactory: () => hosts);
        coordinator.RegisterGuiSession(new StubParentBridge(parent, localDir));
        return (hostSide, clientDevice, client, adapter, coordinator, parent, hostProject, mirrored);
    }

    [AvaloniaFact, FreshApp]
    public async Task Fresh_remote_child_retains_fallback_only_for_original_launch_not_later_ordinary_turns()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, remote, host) = await SetUpOlderFakeRemoteAsync("octopus-r41-fresh-reuse-");
        using var _ = coordinator;

        // 1. Fresh delegation creates a remote child and completes its original launch job.
        var first = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["title"] = "Initial job", ["goal"] = "Run initial job", ["project_id"] = remote.Id },
            ct));
        var taskA = first["task_id"]!.GetValue<string>();
        var childId = first["session_id"]!.GetValue<string>();
        var promptA = Assert.Single(host.Created).Prompt!;

        host.Push(childId, AcpStates.Ready, UserEntry("u1", promptA), AssistantEntry("a1", "initial result"));
        await WaitUntilAsync(() => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("initial result", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);

        // 2. Reuse that same remote child for a second delegated task B.
        var second = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = childId, ["goal"] = "Follow-up task B" },
            ct));
        var taskB = second["task_id"]!.GetValue<string>();
        var promptB = Assert.Single(host.Prompts).Text;

        // 3. An ordinary non-collab user/assistant pair on the remote tab must NOT settle task B.
        host.Push(childId, AcpStates.Ready, UserEntry("u-ord", "what branch is checked out?"), AssistantEntry("a-ord", "synthetic unrelated result"));
        await Task.Delay(80, ct);

        var bAfterOrdinary = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB);
        Assert.Equal(CollabTaskStatus.Running, bAfterOrdinary.Status);
        Assert.Null(bAfterOrdinary.ResultSummary);

        // 4. Task B's actual turn settles task B with its own result.
        host.Push(childId, AcpStates.Ready, UserEntry("u-b", promptB), AssistantEntry("a-b", "real task B answer"));
        await WaitUntilAsync(() => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("real task B answer", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Fresh_remote_child_one_shot_fallback_settles_legacy_launch_and_expires_after_cancel()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, remote, host) = await SetUpOlderFakeRemoteAsync("octopus-r41-fresh-oneshot-");
        using var _ = coordinator;

        // Part A: Legacy host echoes plain text (no collab envelope) for the original launch only.
        var launch1 = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["title"] = "Legacy launch", ["goal"] = "Plain launch", ["project_id"] = remote.Id },
            ct));
        var task1 = launch1["task_id"]!.GetValue<string>();
        var child1 = launch1["session_id"]!.GetValue<string>();

        host.Push(child1, AcpStates.Ready, UserEntry("u1", "Plain launch"), AssistantEntry("a1", "legacy launch answer"));
        await WaitUntilAsync(() => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task1).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("legacy launch answer", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task1).ResultSummary);

        // Reusing child1: ordinary turn must NOT settle task 2.
        var task2 = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = child1, ["goal"] = "Second task on legacy child" },
            ct))["task_id"]!.GetValue<string>();

        host.Push(child1, AcpStates.Ready, UserEntry("u2", "human note"), AssistantEntry("a2", "unrelated note answer"));
        await Task.Delay(80, ct);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task2).Status);

        // Part B: Fresh launch cancelled via cancel_task must NOT allow a later ordinary turn to settle a later task.
        var launch3 = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["title"] = "Cancelled launch", ["goal"] = "Abort me", ["project_id"] = remote.Id },
            ct));
        var task3 = launch3["task_id"]!.GetValue<string>();
        var child2 = launch3["session_id"]!.GetValue<string>();
        await coordinator.CallToolAsync(parent.Id, "cancel_task", new JsonObject { ["task_id"] = task3 }, ct);
        Assert.Equal(CollabTaskStatus.Cancelled, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task3).Status);

        var task4 = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = child2, ["goal"] = "Task after cancelled launch" },
            ct))["task_id"]!.GetValue<string>();

        host.Push(child2, AcpStates.Ready, UserEntry("u-ord2", "ordinary question"), AssistantEntry("a-ord2", "unrelated answer 2"));
        await Task.Delay(80, ct);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task4).Status);
    }

    [AvaloniaFact, FreshApp]
    public async Task Loopback_reused_remote_session_preserves_exact_task_attribution_when_long_prompt_exceeds_text_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = AppHost.Get<FakeAcpSessionFactory>();
        var turnNumber = 0;
        fakes.ConfigureNext = session =>
        {
            session.PromptHandler = (s, _, _, _) =>
            {
                var n = Interlocked.Increment(ref turnNumber);
                var reply = n == 1 ? "Long job A finished" : "Short job B finished";
                s.Emit(new AcpTextDeltaEvent("assistant", reply, $"msg-long-{n}"));
                return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
            };
        };

        var (hostSide, clientDevice, client, _, coordinator, parent, hostProject, mirrored) = await SetUpLoopbackAsync("octopus-r41-long-", ct);
        await using var hostScope = hostSide;
        using var devScope = clientDevice;
        await using var clientScope = client;
        using var coordScope = coordinator;

        // Pre-existing remote GUI session (reused path: Fresh == false).
        var hostItem = new ProjectItem { Id = "ops-long-1", Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Ops Long", Surface = AgentSurface.Gui };
        hostSide.Projects.AddItem(hostProject, hostItem, activate: false);
        var mirroredItem = new ProjectItem { Id = hostItem.Id, Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Ops Long", Surface = AgentSurface.Gui };
        AppHost.Get<IProjectService>().AddItem(mirrored, mirroredItem, activate: false);

        // Ensure host tab is connected and Ready before delegating.
        var hostModel = Assert.IsType<AgentGuiTabViewModel>(hostSide.Tabs.GetOrCreate(hostProject, hostItem).DataContext);
        await WaitTaskAsync(hostModel.ConnectAsync(), ct);

        // Goal > AcpLimits.TextChars (32,768), so HostConversations.Tail drops the leading [Octopus Collab ...] header.
        var longGoal = "LONG-GOAL-START " + new string('x', AcpLimits.TextChars + 1024) + " LONG-GOAL-END";
        var resA = Body(await WaitTaskAsync(
            coordinator.CallToolAsync(parent.Id, "delegate_task", new JsonObject { ["target"] = hostItem.Id, ["goal"] = longGoal }, ct),
            ct));
        var taskA = resA["task_id"]!.GetValue<string>();

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status == CollabTaskStatus.Completed,
            ct,
            timeoutMs: 4000,
            message: "Long (>32 KiB) delegated prompt on a reused remote session must retain exact task correlation despite display text truncation.");

        Assert.Equal("Long job A finished", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);

        // Verify a follow-up short task on the same reused session also settles cleanly.
        var resB = Body(await WaitTaskAsync(
            coordinator.CallToolAsync(parent.Id, "delegate_task", new JsonObject { ["target"] = hostItem.Id, ["goal"] = "Short job B" }, ct),
            ct));
        var taskB = resB["task_id"]!.GetValue<string>();

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status == CollabTaskStatus.Completed,
            ct,
            timeoutMs: 4000);
        Assert.Equal("Short job B finished", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Loopback_remote_session_preserves_attribution_when_snapshot_truncation_omits_user_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = AppHost.Get<FakeAcpSessionFactory>();
        var turnFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fakes.ConfigureNext = session =>
        {
            session.PromptHandler = (s, _, _, _) =>
            {
                s.Emit(new AcpTextDeltaEvent("assistant", "Answer surviving snapshot trim", "msg-trim-1"));
                turnFinished.TrySetResult();
                return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
            };
        };

        var (hostSide, clientDevice, client, adapter, coordinator, parent, hostProject, mirrored) = await SetUpLoopbackAsync("octopus-r41-snap-", ct);
        await using var hostScope = hostSide;
        using var devScope = clientDevice;
        await using var clientScope = client;
        using var coordScope = coordinator;

        var hostItem = new ProjectItem { Id = "ops-snap-1", Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Ops Snap", Surface = AgentSurface.Gui };
        hostSide.Projects.AddItem(hostProject, hostItem, activate: false);
        AppHost.Get<IProjectService>().AddItem(mirrored, new ProjectItem { Id = hostItem.Id, Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Ops Snap", Surface = AgentSurface.Gui }, activate: false);

        var hostModel = Assert.IsType<AgentGuiTabViewModel>(hostSide.Tabs.GetOrCreate(hostProject, hostItem).DataContext);
        await WaitTaskAsync(hostModel.ConnectAsync(), ct);

        // Suppress live updates for the turn so delivery happens strictly via a reattach snapshot whose preceding User entry was trimmed (HasEarlier = true).
        adapter.SuppressUpdate = _ => true;
        adapter.TransformSnapshot = snap =>
        {
            var finishedAssistant = snap.Entries.Where(e => e.Kind == AcpEntryKinds.Assistant && !e.IsStreaming).TakeLast(1).ToList();
            if (finishedAssistant.Count == 0) return snap;
            snap.Entries = finishedAssistant;
            snap.HasEarlier = true;
            return snap;
        };

        var delegated = Body(await WaitTaskAsync(
            coordinator.CallToolAsync(parent.Id, "delegate_task", new JsonObject { ["target"] = hostItem.Id, ["goal"] = "Trimmed snapshot task" }, ct),
            ct));
        var taskId = delegated["task_id"]!.GetValue<string>();
        await WaitTaskAsync(turnFinished.Task, ct);

        // Wait until host timeline finishes streaming, confirm live updates were suppressed, then force a sequence gap so RemoteCollabSession reattaches to the trimmed snapshot.
        await WaitUntilAsync(
            () => hostModel.State == AgentGuiState.Ready
                && hostModel.Timeline.OfType<AssistantTurnItem>().Any(a => !a.IsStreaming && a.MarkdownText.Contains("Answer surviving snapshot trim", StringComparison.Ordinal)),
            ct);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status);

        adapter.InjectGapUpdate(hostItem.Id);

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status == CollabTaskStatus.Completed,
            ct,
            timeoutMs: 4000,
            message: "Assistant entry carrying structured correlation must settle the task even when snapshot trimming omits the preceding User entry.");

        Assert.Equal("Answer surviving snapshot trim", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Loopback_reopened_same_task_ignores_stale_first_round_and_settles_on_second_round()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = AppHost.Get<FakeAcpSessionFactory>();
        var turnNumber = 0;
        var turn2Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTurn2 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        fakes.ConfigureNext = session =>
        {
            session.PromptHandler = async (s, _, _, token) =>
            {
                var n = Interlocked.Increment(ref turnNumber);
                if (n == 1)
                {
                    s.Emit(new AcpTextDeltaEvent("assistant", "Round 1 needs choice", "msg-r1"));
                    return new AcpPromptResult { StopReason = "end_turn" };
                }
                turn2Started.TrySetResult();
                var text = await releaseTurn2.Task.WaitAsync(token);
                s.Emit(new AcpTextDeltaEvent("assistant", text, "msg-r2"));
                return new AcpPromptResult { StopReason = "end_turn" };
            };
        };

        var (hostSide, clientDevice, client, adapter, coordinator, parent, hostProject, mirrored) = await SetUpLoopbackAsync("octopus-r41-reopen-", ct);
        await using var hostScope = hostSide;
        using var devScope = clientDevice;
        await using var clientScope = client;
        using var coordScope = coordinator;

        var hostItem = new ProjectItem { Id = "ops-reopen-1", Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Ops Reopen", Surface = AgentSurface.Gui };
        hostSide.Projects.AddItem(hostProject, hostItem, activate: false);
        AppHost.Get<IProjectService>().AddItem(mirrored, new ProjectItem { Id = hostItem.Id, Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Ops Reopen", Surface = AgentSurface.Gui }, activate: false);

        var hostModel = Assert.IsType<AgentGuiTabViewModel>(hostSide.Tabs.GetOrCreate(hostProject, hostItem).DataContext);
        await WaitTaskAsync(hostModel.ConnectAsync(), ct);

        // Round 1 completes.
        var first = Body(await WaitTaskAsync(
            coordinator.CallToolAsync(parent.Id, "delegate_task", new JsonObject { ["target"] = hostItem.Id, ["goal"] = "Pick storage" }, ct),
            ct));
        var taskId = first["task_id"]!.GetValue<string>();
        var threadId = first["thread_id"]!.GetValue<string>();

        await WaitUntilAsync(() => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("Round 1 needs choice", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).ResultSummary);

        // Capture the real wire User + Assistant entries emitted for Round 1.
        AcpEntryMessage? round1User = null;
        AcpEntryMessage? round1Assistant = null;
        foreach (var upd in adapter.ObservedUpdates)
        {
            foreach (var entry in upd.Upserts)
            {
                if (entry.Kind == AcpEntryKinds.User) round1User = entry;
                if (entry.Kind == AcpEntryKinds.Assistant && !entry.IsStreaming) round1Assistant = entry;
            }
        }
        Assert.NotNull(round1User);
        Assert.NotNull(round1Assistant);

        // Parent reopens the SAME thread/task for Round 2.
        var reopen = await WaitTaskAsync(
            coordinator.CallToolAsync(
                parent.Id,
                "send_message",
                new JsonObject { ["target_session_id"] = hostItem.Id, ["thread_id"] = threadId, ["message"] = "Use SQLite" },
                ct),
            ct);
        Assert.Null(reopen["isError"]);
        await WaitTaskAsync(turn2Started.Task, ct);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status);

        // Simulate a stale/late Round 1 completion carrying Round 1's wire identity arriving while Round 2 is active.
        var staleUser = JsonSerializerClone(round1User!);
        staleUser.Id = "u-stale-r1";
        staleUser.AfterId = null;
        var staleAssistant = JsonSerializerClone(round1Assistant!);
        staleAssistant.Id = "a-stale-r1";
        staleAssistant.AfterId = null;
        staleAssistant.Text = "stale round 1 wrap-up";
        staleAssistant.IsStreaming = false;
        adapter.InjectSyntheticUpdate(hostItem.Id, AcpStates.Ready, staleUser, staleAssistant);
        await Task.Delay(80, ct);

        // Reopened Round 2 must NOT be completed by Round 1's stale delivery identity!
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status);

        // Release Round 2 -> settles cleanly with Round 2's actual answer.
        releaseTurn2.TrySetResult("Done with SQLite");
        await WaitUntilAsync(() => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("Done with SQLite", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Older_remote_host_without_structured_metadata_handles_short_prompts_and_never_misattributes_truncated_or_ordinary_turns()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, remote, host) = await SetUpOlderFakeRemoteAsync("octopus-r41-old-host-");
        using var _ = coordinator;

        var projects = AppHost.Get<IProjectService>();
        var ops = new ProjectItem { Id = "ops-old-1", Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Old Ops" };
        projects.AddItem(remote, ops, activate: false);
        host.Snapshots[ops.Id] = new AcpSnapshotMessage { ItemId = ops.Id, Sequence = 1, State = AcpStates.Ready };

        var taskShort = Body(await coordinator.CallToolAsync(parent.Id, "delegate_task", new JsonObject { ["target"] = ops.Id, ["goal"] = "Short task" }, ct))["task_id"]!.GetValue<string>();
        var longGoal = "OLD-LONG " + new string('z', AcpLimits.TextChars + 1024);
        var taskLong = Body(await coordinator.CallToolAsync(parent.Id, "delegate_task", new JsonObject { ["target"] = ops.Id, ["goal"] = longGoal }, ct))["task_id"]!.GetValue<string>();
        Assert.Equal(2, host.Prompts.Count);
        var promptShort = host.Prompts[0].Text;
        var promptLong = host.Prompts[1].Text;

        // 1. Ordinary human turn on older host must leave both tasks Running.
        host.Push(ops.Id, AcpStates.Ready, UserEntry("u-human", "uptime?"), AssistantEntry("a-human", "up 3 days"));
        await Task.Delay(50, ct);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskShort).Status);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskLong).Status);

        // 2. Older host truncates long prompt with Tail (losing the header) and emits no structured metadata:
        //    identity is unknowable, so it must NEVER consume unrelated taskShort.
        var truncatedLongUser = HostConversations.Tail(promptLong, AcpLimits.TextChars)!;
        host.Push(ops.Id, AcpStates.Ready, UserEntry("u-long-trunc", truncatedLongUser), AssistantEntry("a-long-trunc", "long answer lost header"));
        await Task.Delay(50, ct);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskShort).Status);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskLong).Status);

        // 3. Orphan assistant entry (user entry omitted from snapshot) with no structured metadata must NEVER consume taskShort.
        host.Push(ops.Id, AcpStates.Ready, AssistantEntry("a-orphan", "orphan answer without user"));
        await Task.Delay(50, ct);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskShort).Status);

        // 4. Short untruncated prompt still correlates cleanly on older host.
        host.Push(ops.Id, AcpStates.Ready, UserEntry("u-short", promptShort), AssistantEntry("a-short", "short task succeeded"));
        await WaitUntilAsync(() => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskShort).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("short task succeeded", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskShort).ResultSummary);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskLong).Status);
    }

    private static async Task<(
        LoopbackHostSide HostSide,
        DeviceIdentity ClientDevice,
        RemoteHostClient Client,
        LoopbackRemoteHostAdapter Adapter,
        ProjectService OriginProjects,
        CollabCoordinator OriginCoordinator,
        ProjectItem Parent,
        Project HostProject,
        Project MirroredRemoteProject)> SetUpCrossHostLoopbackAsync(string prefix, CancellationToken ct, bool hostSupportsCollabCancel = true)
    {
        var hostSide = new LoopbackHostSide((ProjectService)AppHost.Get<IProjectService>(), advertiseCollabCancel: hostSupportsCollabCancel);
        var clientDevice = DeviceIdentity.Create();
        var deviceDesc = new DeviceDescription("Local Desktop", DeviceKind.Desktop);
        hostSide.Devices.Add(clientDevice.PublicKey, deviceDesc);

        var hostProject = hostSide.Projects.AddProject(Directory.CreateTempSubdirectory(prefix + "host-").FullName);
        var (a, b) = InMemoryTransport.CreatePair();
        _ = hostSide.Service.ServeAsync(b);
        var client = await WaitTaskAsync(
            RemoteHostClient.ConnectAsync(
                a,
                clientDevice,
                deviceDesc,
                PeerExpectation.Paired(hostSide.Identity.PublicKey),
                new HelloMessage
                {
                    AppVersion = "test",
                    Capabilities = [RemoteMethods.CapabilityWorkspace, RemoteMethods.CapabilityAcpGui, RemoteMethods.CapabilityAcpCollabCancel],
                    TerminalBuild = "build-1",
                }),
            ct);

        var adapter = new LoopbackRemoteHostAdapter(new DirectClientHost(client));
        var localDir = Directory.CreateTempSubdirectory(prefix + "local-").FullName;
        var originProjects = new ProjectService(new InMemoryProjectStore(), AppHost.Get<PathComparer>());
        var localProject = originProjects.AddProject(localDir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Lead", Surface = AgentSurface.Gui };
        originProjects.AddItem(localProject, parent);

        var mirrored = originProjects.AddProject(Directory.CreateTempSubdirectory(prefix + "mirror-").FullName);
        mirrored.RemoteHost = "loopback-host";
        mirrored.RemoteId = hostProject.Id;
        mirrored.Name = "Host Repo";
        originProjects.SetCurrent(localProject.Id);

        var hosts = new FakeRemoteHosts();
        hosts.AddHost("loopback-host", "Loopback Host", adapter);

        var originCoordinator = new CollabCoordinator(
            originProjects,
            AppHost.Get<IAgentCatalog>(),
            AppHost.Get<IAgentScanner>(),
            AppHost.Get<ISettingsStore>(),
            new CollabMcpServer(),
            remoteHostsFactory: () => hosts);
        originCoordinator.RegisterGuiSession(new StubParentBridge(parent, localDir));
        return (hostSide, clientDevice, client, adapter, originProjects, originCoordinator, parent, hostProject, mirrored);
    }

    [AvaloniaFact, FreshApp]
    public async Task Host_local_leaf_stays_running_while_origin_sees_remote_middle_idle()
    {
        var ct = TestContext.Current.CancellationToken;
        var (side, device, client, _, originProjects, origin, root, hp, mirror) = await SetUpCrossHostLoopbackAsync("octopus-cross-defer-", ct);
        await using var hostScope = side;
        using var devScope = device;
        await using var cliScope = client;
        using var coordScope = origin;

        var receiving = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        Assert.NotSame(origin, receiving);

        var b = new ProjectItem { Id = "mid", Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Remote middle", Surface = AgentSurface.Gui };
        var c = new ProjectItem { Id = "leaf", Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Remote-local leaf", Surface = AgentSurface.Gui };
        side.Projects.AddItem(hp, b, activate: false);
        side.Projects.AddItem(hp, c, activate: false);
        originProjects.AddItem(mirror, new ProjectItem { Id = b.Id, Kind = b.Kind, AgentId = b.AgentId, Surface = b.Surface, Title = b.Title }, activate: false);
        receiving.RegisterGuiSession(new StubParentBridge(c, hp.Path));

        var leaf = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        AppHost.Get<FakeAcpSessionFactory>().ConfigureNext = session => session.PromptHandler = async (s, _, _, token) =>
        {
            var sent = await receiving.CallToolAsync(b.Id, "delegate_task", new JsonObject { ["target"] = c.Id, ["goal"] = "unfinished host-side work" }, token);
            Assert.Null(sent["isError"]);
            leaf.TrySetResult(Body(sent)["task_id"]!.GetValue<string>());
            s.Emit(new AcpTextDeltaEvent("assistant", "I delegated to C and am waiting", "msg-wait"));
            return new AcpPromptResult { StopReason = "end_turn" };
        };

        var vm = Assert.IsType<AgentGuiTabViewModel>(side.Tabs.GetOrCreate(hp, b).DataContext);
        await WaitTaskAsync(vm.ConnectAsync(), ct);
        var turnDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.TurnFinished += (_, _, _, _, _) => turnDone.TrySetResult();

        var sentTask = await WaitTaskAsync(origin.CallToolAsync(root.Id, "delegate_task", new JsonObject { ["target"] = b.Id, ["goal"] = "coordinate C" }, ct), ct);
        Assert.Null(sentTask["isError"]);
        var parentId = Body(sentTask)["task_id"]!.GetValue<string>();
        var leafId = await WaitTaskAsync(leaf.Task, ct);
        await WaitTaskAsync(turnDone.Task, ct);
        await Task.Delay(300, ct);
        Frames.Pump();

        var parent = origin.GetTasksForItem(root.Id).Single(t => t.TaskId == parentId);
        var child = receiving.GetTasksForItem(c.Id).Single(t => t.TaskId == leafId);
        Assert.Equal(CollabTaskStatus.Running, child.Status);
        Assert.Equal(CollabTaskStatus.Running, parent.Status);
        Assert.Null(parent.ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Remote_middle_defers_only_for_owned_unfinished_host_children_and_fails_on_disconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var (side, device, client, _, originProjects, origin, root, hp, mirror) = await SetUpCrossHostLoopbackAsync("octopus-cross-scope-", ct);
        await using var hostScope = side;
        using var devScope = device;
        await using var cliScope = client;
        using var coordScope = origin;

        var receiving = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        var b = new ProjectItem { Id = "mid-scope", Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Remote middle", Surface = AgentSurface.Gui };
        var cHuman = new ProjectItem { Id = "leaf-human", Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Host human leaf", Surface = AgentSurface.Gui };
        var cOwned = new ProjectItem { Id = "leaf-owned", Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Host owned leaf", Surface = AgentSurface.Gui };
        side.Projects.AddItem(hp, b, activate: false);
        side.Projects.AddItem(hp, cHuman, activate: false);
        side.Projects.AddItem(hp, cOwned, activate: false);
        originProjects.AddItem(mirror, new ProjectItem { Id = b.Id, Kind = b.Kind, AgentId = b.AgentId, Surface = b.Surface, Title = b.Title }, activate: false);
        receiving.RegisterGuiSession(new StubParentBridge(cHuman, hp.Path));
        receiving.RegisterGuiSession(new StubParentBridge(cOwned, hp.Path));

        var turnNumber = 0;
        var turn3Done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AppHost.Get<FakeAcpSessionFactory>().ConfigureNext = session => session.PromptHandler = async (s, _, _, token) =>
        {
            var n = Interlocked.Increment(ref turnNumber);
            if (n == 1)
            {
                // Turn 1: Host human prompt delegates an unrelated child on host.
                var humanRes = await receiving.CallToolAsync(b.Id, "delegate_task", new JsonObject { ["target"] = cHuman.Id, ["goal"] = "host human work" }, token);
                Assert.Null(humanRes["isError"]);
                s.Emit(new AcpTextDeltaEvent("assistant", "Started host human child", "m1"));
                return new AcpPromptResult { StopReason = "end_turn" };
            }
            if (n == 2)
            {
                // Turn 2: Origin leaf task delegates nothing -> must complete despite unrelated cHuman still running.
                s.Emit(new AcpTextDeltaEvent("assistant", "Direct leaf answer", "m2"));
                return new AcpPromptResult { StopReason = "end_turn" };
            }
            // Turn 3: Origin nested task delegates to cOwned -> must defer while cOwned is running.
            var ownedRes = await receiving.CallToolAsync(b.Id, "delegate_task", new JsonObject { ["target"] = cOwned.Id, ["goal"] = "owned host child" }, token);
            Assert.Null(ownedRes["isError"]);
            s.Emit(new AcpTextDeltaEvent("assistant", "Waiting on owned host child", "m3"));
            return new AcpPromptResult { StopReason = "end_turn" };
        };

        var vm = Assert.IsType<AgentGuiTabViewModel>(side.Tabs.GetOrCreate(hp, b).DataContext);
        await WaitTaskAsync(vm.ConnectAsync(), ct);
        vm.TurnFinished += (_, _, _, _, _) =>
        {
            if (Volatile.Read(ref turnNumber) >= 3) turn3Done.TrySetResult();
        };

        // Turn 1 (human on host) starts unrelated cHuman task.
        vm.InputText = "Run host human task";
        await WaitTaskAsync(vm.SendPromptAsync(), ct);
        Assert.Equal(CollabTaskStatus.Running, Assert.Single(receiving.GetTasksForItem(cHuman.Id)).Status);

        // Turn 2 (remote leaf task) completes normally even though cHuman is still Running.
        var leafRes = await WaitTaskAsync(origin.CallToolAsync(root.Id, "delegate_task", new JsonObject { ["target"] = b.Id, ["goal"] = "remote leaf" }, ct), ct);
        Assert.Null(leafRes["isError"]);
        var leafTaskId = Body(leafRes)["task_id"]!.GetValue<string>();
        await WaitUntilAsync(() => origin.GetTasksForItem(root.Id).Single(t => t.TaskId == leafTaskId).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("Direct leaf answer", origin.GetTasksForItem(root.Id).Single(t => t.TaskId == leafTaskId).ResultSummary);

        // Turn 3 (remote nested task) defers because cOwned is still Running.
        var nestedRes = await WaitTaskAsync(origin.CallToolAsync(root.Id, "delegate_task", new JsonObject { ["target"] = b.Id, ["goal"] = "remote nested" }, ct), ct);
        Assert.Null(nestedRes["isError"]);
        var nestedTaskId = Body(nestedRes)["task_id"]!.GetValue<string>();
        await WaitTaskAsync(turn3Done.Task, ct);
        await Task.Delay(300, ct);
        Frames.Pump();
        Assert.Equal(CollabTaskStatus.Running, origin.GetTasksForItem(root.Id).Single(t => t.TaskId == nestedTaskId).Status);

        // If the remote host disconnects while deferred, the origin task fails instead of hanging forever.
        await client.DisposeAsync();
        await WaitUntilAsync(() => origin.GetTasksForItem(root.Id).Single(t => t.TaskId == nestedTaskId).Status == CollabTaskStatus.Failed, ct);
    }

    [AvaloniaFact, FreshApp]
    public async Task First_truncated_snapshot_cannot_attribute_later_human_answer_to_launch_and_exposes_uncertainty()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, remote, host) = await SetUpOlderFakeRemoteAsync("octopus-r41-truncated-first-");
        using var cleanup = coordinator;

        // 1. Truncated first snapshot (HasEarlier = true): uncorrelated later human turn must NOT settle launch,
        //    and the unrecoverable launch must expose uncertainty (Failed) with no implicit re-execution.
        host.OnAttach = (id, _) => Task.FromResult(new AcpSnapshotMessage
        {
            ItemId = id,
            Sequence = 60,
            State = AcpStates.Ready,
            HasEarlier = true,
            Entries =
            [
                UserEntry("later-human", "How many files?"),
                AssistantEntry("later-answer", "human-only answer", streaming: false),
            ],
        });

        var res = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["goal"] = "Independent launch", ["project_id"] = remote.Id },
            ct));
        var taskId = res["task_id"]!.GetValue<string>();
        var threadId = res["thread_id"]!.GetValue<string>();

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).IsFinished,
            ct,
            timeoutMs: 2000,
            message: "Unrecoverable launch from truncated initial snapshot must settle with uncertainty.");

        var actual = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId);
        Assert.NotEqual("human-only answer", actual.ResultSummary);
        Assert.Equal(CollabTaskStatus.Failed, actual.Status);
        Assert.Equal(CollabThreadStatus.Blocked, coordinator.FindThread(threadId)!.Status);
        Assert.Single(host.Created);
        Assert.Empty(host.Prompts);

        // 2. Positive control: short COMPLETE first snapshot (HasEarlier = false) on an older host preserves launch fallback.
        host.OnAttach = (id, _) => Task.FromResult(new AcpSnapshotMessage
        {
            ItemId = id,
            Sequence = 2,
            State = AcpStates.Ready,
            HasEarlier = false,
            Entries =
            [
                UserEntry("first-plain", "Short plain launch"),
                AssistantEntry("first-answer", "complete legacy answer", streaming: false),
            ],
        });

        var completeTaskId = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["goal"] = "Short plain launch", ["project_id"] = remote.Id },
            ct))["task_id"]!.GetValue<string>();

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == completeTaskId).Status == CollabTaskStatus.Completed,
            ct,
            timeoutMs: 2000,
            message: "Complete non-truncated first snapshot on an older host must still preserve legacy launch fallback.");
        Assert.Equal("complete legacy answer", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == completeTaskId).ResultSummary);
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(19)]
    [InlineData(20)]
    public async Task Resnapshot_after_later_manual_turns_settles_completed_delivery(int laterTurns)
    {
        var ct = TestContext.Current.CancellationToken;
        AppHost.Get<FakeAcpSessionFactory>().ConfigureNext = session => session.PromptHandler = (s, text, _, _) =>
        {
            var reply = text.StartsWith("manual-", StringComparison.Ordinal)
                ? text
                : text.Contains("follow-up after eviction", StringComparison.Ordinal)
                    ? "follow-up succeeded"
                    : "delegated answer";
            s.Emit(new AcpTextDeltaEvent("assistant", reply, Guid.NewGuid().ToString("N")));
            return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
        };

        var (host, device, client, adapter, coordinator, parent, hp, mp) = await SetUpLoopbackAsync("octopus-r41-evict-tail-", ct);
        await using var hostScope = host;
        using var deviceScope = device;
        await using var clientScope = client;
        using var coordScope = coordinator;

        var item = new ProjectItem { Id = "remote-existing", Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Worker", Surface = AgentSurface.Gui };
        host.Projects.AddItem(hp, item, activate: false);
        AppHost.Get<IProjectService>().AddItem(mp, new ProjectItem { Id = item.Id, Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Worker", Surface = AgentSurface.Gui }, activate: false);
        var model = Assert.IsType<AgentGuiTabViewModel>(host.Tabs.GetOrCreate(hp, item).DataContext);
        await WaitTaskAsync(model.ConnectAsync(), ct);

        adapter.SuppressUpdate = _ => true;
        var res = Body(await WaitTaskAsync(
            coordinator.CallToolAsync(parent.Id, "delegate_task", new JsonObject { ["target"] = item.Id, ["goal"] = "normal delivery" }, ct),
            ct));
        var task = res["task_id"]!.GetValue<string>();
        var thread = res["thread_id"]!.GetValue<string>();

        await WaitUntilAsync(
            () => model.State == AgentGuiState.Ready && model.Timeline.OfType<AssistantTurnItem>().Any(t => !t.IsStreaming && t.MarkdownText.Contains("delegated answer", StringComparison.Ordinal)),
            ct);
        for (var i = 0; i < laterTurns; i++)
        {
            model.InputText = "manual-" + i;
            await WaitTaskAsync(model.SendPromptAsync(), ct);
        }

        var snapshotSeen = new TaskCompletionSource<AcpSnapshotMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.TransformSnapshot = s =>
        {
            snapshotSeen.TrySetResult(s);
            return s;
        };
        adapter.InjectGapUpdate(item.Id);
        var snapshot = await WaitTaskAsync(snapshotSeen.Task, ct);
        Assert.Equal(40, snapshot.Entries.Count);
        Assert.Equal(laterTurns == 20, snapshot.HasEarlier);

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task).IsFinished,
            ct,
            timeoutMs: 3000,
            message: $"Expected delivery to settle after resnapshot (laterTurns={laterTurns}, hasEarlier={snapshot.HasEarlier}).");

        var actual = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task);
        if (laterTurns == 19)
        {
            Assert.Equal(CollabTaskStatus.Completed, actual.Status);
            Assert.Equal("delegated answer", actual.ResultSummary);
        }
        else
        {
            Assert.Equal(CollabTaskStatus.Failed, actual.Status);
            Assert.Equal(CollabThreadStatus.Blocked, coordinator.FindThread(thread)!.Status);
            Assert.DoesNotContain("manual-", actual.ResultSummary ?? "", StringComparison.Ordinal);
        }
        // No implicit re-execution of the evicted delivery.
        Assert.Equal(1 + laterTurns, model.Timeline.OfType<UserTurnItem>().Count());

        // Subsequent delegation on the same session after a truncated snapshot must still complete normally.
        adapter.SuppressUpdate = null;
        adapter.TransformSnapshot = null;
        var followUpTask = Body(await WaitTaskAsync(
            coordinator.CallToolAsync(parent.Id, "delegate_task", new JsonObject { ["target"] = item.Id, ["goal"] = "follow-up after eviction" }, ct),
            ct))["task_id"]!.GetValue<string>();
        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == followUpTask).Status == CollabTaskStatus.Completed,
            ct,
            timeoutMs: 3000);
        Assert.Equal("follow-up succeeded", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == followUpTask).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Older_host_evicted_delivery_on_truncated_resnapshot_exposes_uncertainty_without_reexecution()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, remote, host) = await SetUpOlderFakeRemoteAsync("octopus-r41-old-evict-");
        using var cleanup = coordinator;

        var projects = AppHost.Get<IProjectService>();
        var ops = new ProjectItem { Id = "ops-old-evict", Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Old Evict" };
        projects.AddItem(remote, ops, activate: false);

        var attachCount = 0;
        host.OnAttach = (id, _) =>
        {
            var call = Interlocked.Increment(ref attachCount);
            if (call == 1)
            {
                return Task.FromResult(new AcpSnapshotMessage { ItemId = id, Sequence = 1, State = AcpStates.Ready });
            }
            return Task.FromResult(new AcpSnapshotMessage
            {
                ItemId = id,
                Sequence = 50,
                State = AcpStates.Ready,
                HasEarlier = true,
                QueuedCount = 0,
                Entries =
                [
                    UserEntry("u-human-later", "status?"),
                    AssistantEntry("a-human-later", "all good", streaming: false),
                ],
            });
        };

        var taskA = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = ops.Id, ["goal"] = "Old host evicted task" },
            ct))["task_id"]!.GetValue<string>();
        Assert.Single(host.Prompts);

        // Force sequence gap -> truncated snapshot without Task A.
        host.PushRaw(new AcpUpdateMessage { ItemId = ops.Id, Sequence = 50, State = AcpStates.Ready });

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).IsFinished,
            ct,
            timeoutMs: 2000,
            message: "Older host truncated resnapshot missing a prior delivery must expose uncertainty.");

        var settledA = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA);
        Assert.Equal(CollabTaskStatus.Failed, settledA.Status);
        Assert.NotEqual("all good", settledA.ResultSummary);
        Assert.Single(host.Prompts); // No implicit re-execution.

        // A new short task delegated afterward on the same older host still correlates and completes.
        var taskB = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = ops.Id, ["goal"] = "Short task B after eviction" },
            ct))["task_id"]!.GetValue<string>();
        var promptB = host.Prompts[1].Text;
        host.Push(ops.Id, AcpStates.Ready, UserEntry("u-b", promptB), AssistantEntry("a-b", "task B succeeded", streaming: false));

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status == CollabTaskStatus.Completed,
            ct,
            timeoutMs: 2000);
        Assert.Equal("task B succeeded", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Deferred_remote_middle_survives_manual_turns_snapshot_trim_and_reconnect_until_terminal_event_or_disconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var (side, device, client, adapter, originProjects, origin, root, hp, mirror) = await SetUpCrossHostLoopbackAsync("octopus-cross-defer-trim-", ct);
        await using var hostScope = side;
        using var devScope = device;
        await using var cliScope = client;
        using var coordScope = origin;

        var receiving = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        var b = new ProjectItem { Id = "mid-trim", Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Remote middle", Surface = AgentSurface.Gui };
        var leaf1 = new ProjectItem { Id = "leaf-trim-1", Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Host leaf 1", Surface = AgentSurface.Gui };
        var leaf2 = new ProjectItem { Id = "leaf-trim-2", Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Host leaf 2", Surface = AgentSurface.Gui };
        side.Projects.AddItem(hp, b, activate: false);
        side.Projects.AddItem(hp, leaf1, activate: false);
        side.Projects.AddItem(hp, leaf2, activate: false);
        originProjects.AddItem(mirror, new ProjectItem { Id = b.Id, Kind = b.Kind, AgentId = b.AgentId, Surface = b.Surface, Title = b.Title }, activate: false);
        receiving.RegisterGuiSession(new StubParentBridge(leaf1, hp.Path));
        receiving.RegisterGuiSession(new StubParentBridge(leaf2, hp.Path));

        AppHost.Get<FakeAcpSessionFactory>().ConfigureNext = session => session.PromptHandler = async (s, text, _, token) =>
        {
            if (text.Contains("coordinate leaf 1", StringComparison.Ordinal))
            {
                var r1 = await receiving.CallToolAsync(b.Id, "delegate_task", new JsonObject { ["target"] = leaf1.Id, ["goal"] = "child work 1" }, token);
                Assert.Null(r1["isError"]);
                s.Emit(new AcpTextDeltaEvent("assistant", "Delegated to leaf 1 and waiting", Guid.NewGuid().ToString("N")));
                return new AcpPromptResult { StopReason = "end_turn" };
            }
            if (text.Contains("coordinate leaf 2", StringComparison.Ordinal))
            {
                var r2 = await receiving.CallToolAsync(b.Id, "delegate_task", new JsonObject { ["target"] = leaf2.Id, ["goal"] = "child work 2" }, token);
                Assert.Null(r2["isError"]);
                s.Emit(new AcpTextDeltaEvent("assistant", "Delegated to leaf 2 and waiting", Guid.NewGuid().ToString("N")));
                return new AcpPromptResult { StopReason = "end_turn" };
            }
            s.Emit(new AcpTextDeltaEvent("assistant", text, Guid.NewGuid().ToString("N")));
            return new AcpPromptResult { StopReason = "end_turn" };
        };

        var vm = Assert.IsType<AgentGuiTabViewModel>(side.Tabs.GetOrCreate(hp, b).DataContext);
        await WaitTaskAsync(vm.ConnectAsync(), ct);

        // 1. Delegate two identified tasks to remote middle B; each starts a running host-side child and defers.
        var res1 = Body(await WaitTaskAsync(origin.CallToolAsync(root.Id, "delegate_task", new JsonObject { ["target"] = b.Id, ["goal"] = "coordinate leaf 1" }, ct), ct));
        var task1 = res1["task_id"]!.GetValue<string>();
        var thread1 = res1["thread_id"]!.GetValue<string>();
        await WaitUntilAsync(
            () => origin.GetTasksForItem(root.Id).Single(t => t.TaskId == task1).LatestActivity == "Waiting for delegated work",
            ct,
            timeoutMs: 4000,
            message: "First deferred middle task must reach deferred waiting state.");

        var res2 = Body(await WaitTaskAsync(origin.CallToolAsync(root.Id, "delegate_task", new JsonObject { ["target"] = b.Id, ["goal"] = "coordinate leaf 2" }, ct), ct));
        var task2 = res2["task_id"]!.GetValue<string>();
        await WaitUntilAsync(
            () => origin.GetTasksForItem(root.Id).Single(t => t.TaskId == task2).LatestActivity == "Waiting for delegated work",
            ct,
            timeoutMs: 4000,
            message: "Second deferred middle task must reach deferred waiting state.");

        // Capture the real wire correlation for task 1 so we can deliver a genuine terminal event later.
        AcpCollabCorrelationMessage? task1Collab = null;
        foreach (var upd in adapter.ObservedUpdates)
        {
            foreach (var entry in upd.Upserts)
            {
                if (entry.Kind == AcpEntryKinds.Assistant && entry.Collab is { Deferred: true } c && c.TaskId == task1)
                {
                    task1Collab = c;
                }
            }
        }
        Assert.NotNull(task1Collab);
        Assert.False(string.IsNullOrWhiteSpace(task1Collab!.DeliveryId));

        // 2. Run 21 ordinary manual turns on B so both deferred turns are evicted past the 40-entry snapshot window.
        for (var i = 0; i < 21; i++)
        {
            vm.InputText = "manual-ordinary-" + i;
            await WaitTaskAsync(vm.SendPromptAsync(), ct);
        }
        await WaitUntilAsync(() => vm.State == AgentGuiState.Ready && vm.QueuedCount == 0, ct);

        // 3. Force a reconnect (sequence gap -> resnapshot) with HasEarlier=true, State=Ready, QueuedCount=0, 0 correlated entries.
        var snapshotSeen = new TaskCompletionSource<AcpSnapshotMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.TransformSnapshot = s =>
        {
            snapshotSeen.TrySetResult(s);
            return s;
        };
        adapter.InjectGapUpdate(b.Id);
        var snapshot = await WaitTaskAsync(snapshotSeen.Task, ct);
        Assert.Equal(40, snapshot.Entries.Count);
        Assert.True(snapshot.HasEarlier);
        Assert.Equal(AcpStates.Ready, snapshot.State);
        Assert.Equal(0, snapshot.QueuedCount);
        Assert.Equal(0, snapshot.Entries.Count(e => e.Collab?.DeliveryId != null));

        // Push an extra idle Ready update after reconnect as well.
        adapter.InjectSyntheticUpdate(b.Id, AcpStates.Ready);
        await Task.Delay(200, ct);
        Frames.Pump();

        // Both identified deferred tasks must survive snapshot trimming, idle Ready with empty queue, manual turns, and reconnect.
        var t1AfterReconnect = origin.GetTasksForItem(root.Id).Single(t => t.TaskId == task1);
        var t2AfterReconnect = origin.GetTasksForItem(root.Id).Single(t => t.TaskId == task2);
        Assert.Equal(CollabTaskStatus.Running, t1AfterReconnect.Status);
        Assert.Equal("Waiting for delegated work", t1AfterReconnect.LatestActivity);
        Assert.Null(t1AfterReconnect.ResultSummary);
        Assert.Equal(CollabTaskStatus.Running, t2AfterReconnect.Status);
        Assert.Equal("Waiting for delegated work", t2AfterReconnect.LatestActivity);
        Assert.Null(t2AfterReconnect.ResultSummary);

        // 4. Genuine terminal event for task 1 settles only task 1, leaving task 2 deferred and Running.
        var terminalAssistant = AssistantEntry("a-terminal-1", "Final answer after host child completed", streaming: false);
        terminalAssistant.Collab = new AcpCollabCorrelationMessage
        {
            ThreadId = thread1,
            TaskId = task1,
            DeliveryId = task1Collab.DeliveryId,
            ExpectsReply = true,
            Deferred = false,
        };
        adapter.InjectSyntheticUpdate(b.Id, AcpStates.Ready, terminalAssistant);

        await WaitUntilAsync(
            () => origin.GetTasksForItem(root.Id).Single(t => t.TaskId == task1).Status == CollabTaskStatus.Completed,
            ct,
            timeoutMs: 3000,
            message: "Genuine terminal event must complete the surviving deferred task.");
        Assert.Equal("Final answer after host child completed", origin.GetTasksForItem(root.Id).Single(t => t.TaskId == task1).ResultSummary);
        Assert.Equal(CollabTaskStatus.Running, origin.GetTasksForItem(root.Id).Single(t => t.TaskId == task2).Status);

        // 5. Disconnect settles the remaining surviving deferred task as Failed instead of hanging.
        await client.DisposeAsync();
        await WaitUntilAsync(
            () => origin.GetTasksForItem(root.Id).Single(t => t.TaskId == task2).Status == CollabTaskStatus.Failed,
            ct,
            timeoutMs: 3000,
            message: "Disconnect must settle the surviving deferred task as Failed.");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evicted_delivery_with_retained_same_thread_interjection_exposes_uncertainty(bool deferredOnHost)
    {
        var ct = TestContext.Current.CancellationToken;
        var (side, device, client, adapter, originProjects, origin, root, hp, mirror) = await SetUpCrossHostLoopbackAsync("octopus-r41-fyi-evict-", ct);
        await using var hostScope = side;
        using var devScope = device;
        await using var cliScope = client;
        using var coordScope = origin;

        var receiving = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        var b = new ProjectItem { Id = "mid-fyi", Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Middle", Surface = AgentSurface.Gui };
        var c = new ProjectItem { Id = "leaf-fyi", Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Leaf", Surface = AgentSurface.Gui };
        side.Projects.AddItem(hp, b, activate: false);
        side.Projects.AddItem(hp, c, activate: false);
        originProjects.AddItem(mirror, new ProjectItem { Id = b.Id, Kind = b.Kind, AgentId = b.AgentId, Surface = b.Surface, Title = b.Title }, activate: false);
        receiving.RegisterGuiSession(new StubParentBridge(c, hp.Path));

        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = session => session.PromptHandler = async (s, text, _, token) =>
        {
            var output = "ordinary answer";
            if (text.Contains("coordinate host leaf", StringComparison.Ordinal))
            {
                var rr = await receiving.CallToolAsync(b.Id, "delegate_task", new JsonObject { ["target"] = c.Id, ["goal"] = "leaf work" }, token);
                Assert.Null(rr["isError"]);
                output = "waiting on my leaf";
            }
            s.Emit(new AcpTextDeltaEvent("assistant", output, Guid.NewGuid().ToString("N")));
            return new AcpPromptResult { StopReason = "end_turn" };
        };

        var vm = Assert.IsType<AgentGuiTabViewModel>(side.Tabs.GetOrCreate(hp, b).DataContext);
        await WaitTaskAsync(vm.ConnectAsync(), ct);
        var acp = factory.Created.Last();

        adapter.SuppressUpdate = _ => true;
        var goal = deferredOnHost ? "coordinate host leaf" : "report directly";
        var res = Body(await WaitTaskAsync(origin.CallToolAsync(root.Id, "delegate_task", new JsonObject { ["target"] = b.Id, ["goal"] = goal }, ct), ct));
        var task = res["task_id"]!.GetValue<string>();
        var thread = res["thread_id"]!.GetValue<string>();

        await WaitUntilAsync(
            () => vm.State == AgentGuiState.Ready && vm.Timeline.OfType<AssistantTurnItem>().Any(t => !t.IsStreaming && t.CollabCorrelation?.Deferred == deferredOnHost),
            ct);

        for (var i = 0; i < 20; i++)
        {
            vm.InputText = "manual-" + i;
            await WaitTaskAsync(vm.SendPromptAsync(), ct);
        }

        // Send a same-thread informational interjection (ExpectsReply=false, no delivery ID) that stays in the 40-entry snapshot.
        Assert.True(await WaitTaskAsync(origin.InterjectInThreadAsync(thread, "Operator FYI on this thread", ct: ct), ct));
        await WaitUntilAsync(
            () => vm.State == AgentGuiState.Ready && vm.Timeline.OfType<AssistantTurnItem>().Last().CollabCorrelation is { ExpectsReply: false },
            ct);

        var snapTcs = new TaskCompletionSource<AcpSnapshotMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.TransformSnapshot = s =>
        {
            snapTcs.TrySetResult(s);
            return s;
        };
        adapter.InjectGapUpdate(b.Id);
        var snapshot = await WaitTaskAsync(snapTcs.Task, ct);
        Assert.Equal(40, snapshot.Entries.Count);
        Assert.True(snapshot.HasEarlier);
        Assert.Equal(0, snapshot.QueuedCount);
        Assert.Equal(AcpStates.Ready, snapshot.State);
        Assert.DoesNotContain(snapshot.Entries, e => e.Collab?.DeliveryId != null);
        Assert.Equal(2, snapshot.Entries.Count(e => e.Collab is { ExpectsReply: false } col && col.ThreadId == thread));
        Assert.DoesNotContain(snapshot.Entries, e => e.Collab?.ExpectsReply == true);
        Assert.Equal(22, acp.SentPrompts.Count);

        await WaitUntilAsync(
            () => origin.GetTasksForItem(root.Id).Single(t => t.TaskId == task).Status == CollabTaskStatus.Failed,
            ct,
            timeoutMs: 3000,
            message: $"Evicted delivery (deferredOnHost={deferredOnHost}) must settle as Failed even when same-thread ExpectsReply=false FYI entries remain in snapshot.");

        var actual = origin.GetTasksForItem(root.Id).Single(t => t.TaskId == task);
        Assert.Contains("truncated", actual.ResultSummary ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CollabThreadStatus.Blocked, origin.FindThread(thread)!.Status);
        Assert.Equal(22, acp.SentPrompts.Count); // No implicit re-execution.
    }

    [AvaloniaFact, FreshApp]
    public async Task Cancelling_observed_deferred_remote_task_does_not_stop_later_manual_turn_on_host()
    {
        var ct = TestContext.Current.CancellationToken;
        var (side, device, client, _, originProjects, origin, root, hp, mirror) = await SetUpCrossHostLoopbackAsync("octopus-bug07-remote-cancel-", ct);
        await using var hostScope = side;
        using var devScope = device;
        await using var cliScope = client;
        using var coordScope = origin;

        var receiving = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        var b = new ProjectItem { Id = "mid-cancel", Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "Middle", Surface = AgentSurface.Gui };
        var c = new ProjectItem { Id = "leaf-cancel", Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Leaf", Surface = AgentSurface.Gui };
        side.Projects.AddItem(hp, b, activate: false);
        side.Projects.AddItem(hp, c, activate: false);
        originProjects.AddItem(mirror, new ProjectItem { Id = b.Id, Kind = b.Kind, AgentId = b.AgentId, Surface = b.Surface, Title = b.Title }, activate: false);
        receiving.RegisterGuiSession(new StubParentBridge(c, hp.Path));

        var factory = AppHost.Get<FakeAcpSessionFactory>();
        var manualEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manualDone = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCollabEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCollabDone = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.ConfigureNext = session =>
        {
            session.CancelHandler = _ =>
            {
                if (manualEntered.Task.IsCompleted && !manualDone.Task.IsCompleted)
                {
                    manualDone.TrySetResult(new AcpPromptResult { StopReason = "cancelled" });
                }
                if (activeCollabEntered.Task.IsCompleted && !activeCollabDone.Task.IsCompleted)
                {
                    activeCollabDone.TrySetResult(new AcpPromptResult { StopReason = "cancelled" });
                }
                return Task.CompletedTask;
            };
            session.PromptHandler = async (s, text, _, token) =>
            {
                if (text.Contains("coordinate host leaf", StringComparison.Ordinal))
                {
                    var rr = await receiving.CallToolAsync(b.Id, "delegate_task", new JsonObject { ["target"] = c.Id, ["goal"] = "leaf work" }, token);
                    Assert.Null(rr["isError"]);
                    s.Emit(new AcpTextDeltaEvent("assistant", "waiting on my leaf", Guid.NewGuid().ToString("N")));
                    return new AcpPromptResult { StopReason = "end_turn" };
                }
                if (text == "unrelated gated manual turn")
                {
                    manualEntered.TrySetResult();
                    var res = await manualDone.Task.WaitAsync(token);
                    s.Emit(new AcpTextDeltaEvent("assistant", "manual answer", Guid.NewGuid().ToString("N")));
                    return res;
                }
                if (text.Contains("cancel active remote turn", StringComparison.Ordinal))
                {
                    s.Emit(new AcpTextDeltaEvent("assistant", "working on active turn...", Guid.NewGuid().ToString("N")));
                    activeCollabEntered.TrySetResult();
                    return await activeCollabDone.Task.WaitAsync(token);
                }
                s.Emit(new AcpTextDeltaEvent("assistant", "ordinary answer", Guid.NewGuid().ToString("N")));
                return new AcpPromptResult { StopReason = "end_turn" };
            };
        };

        var vm = Assert.IsType<AgentGuiTabViewModel>(side.Tabs.GetOrCreate(hp, b).DataContext);
        await WaitTaskAsync(vm.ConnectAsync(), ct);
        var acp = factory.Created.Last();

        // 1. Delegate to remote middle B -> host delegates to leaf C and defers.
        var res = Body(await WaitTaskAsync(origin.CallToolAsync(root.Id, "delegate_task", new JsonObject { ["target"] = b.Id, ["goal"] = "coordinate host leaf" }, ct), ct));
        var task = res["task_id"]!.GetValue<string>();
        await WaitUntilAsync(
            () => origin.GetTasksForItem(root.Id).Single(t => t.TaskId == task).LatestActivity == "Waiting for delegated work",
            ct,
            timeoutMs: 4000);

        // 2. Human starts an unrelated manual turn on host GUI B, then origin cancels the deferred task.
        vm.InputText = "unrelated gated manual turn";
        var manual = vm.SendPromptAsync();
        await WaitTaskAsync(manualEntered.Task, ct);

        var cancel = Body(await WaitTaskAsync(origin.CallToolAsync(root.Id, "cancel_task", new JsonObject { ["task_id"] = task }, ct), ct));
        Assert.Equal("cancelled", cancel["status"]!.GetValue<string>());

        for (var i = 0; i < 40 && !manual.IsCompleted; i++)
        {
            Frames.Pump();
            await Task.Delay(10, ct);
        }
        var stops = acp.CancelCount;
        var stopReason = vm.Timeline.OfType<AssistantTurnItem>().Last().StopReason;
        manualDone.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitTaskAsync(manual, ct);

        Assert.Equal(CollabTaskStatus.Cancelled, origin.GetTasksForItem(root.Id).Single(t => t.TaskId == task).Status);
        Assert.Equal(CollabTaskStatus.Running, Assert.Single(receiving.GetTasksForItem(c.Id)).Status);
        Assert.Equal(0, stops);
        Assert.NotEqual("cancelled", stopReason);

        // 3. Positive control: cancelling a genuinely active correlated remote turn still forwards CancelTurnAsync to the host.
        var activeRes = Body(await WaitTaskAsync(origin.CallToolAsync(root.Id, "delegate_task", new JsonObject { ["target"] = b.Id, ["goal"] = "cancel active remote turn" }, ct), ct));
        var activeTask = activeRes["task_id"]!.GetValue<string>();
        await WaitTaskAsync(activeCollabEntered.Task, ct);
        await WaitUntilAsync(() => vm.State == AgentGuiState.Working, ct);
        await Task.Delay(150, ct);
        Frames.Pump();

        var cancelActive = Body(await WaitTaskAsync(origin.CallToolAsync(root.Id, "cancel_task", new JsonObject { ["task_id"] = activeTask }, ct), ct));
        Assert.Equal("cancelled", cancelActive["status"]!.GetValue<string>());
        await WaitUntilAsync(() => acp.CancelCount == 1, ct, timeoutMs: 3000, message: "Cancelling an active identified remote turn must still cancel the host turn.");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData("still-active")]
    [InlineData("completed-unseen")]
    [InlineData("deferred-unseen")]
    [InlineData("deferred-seen")]
    public async Task Remote_cancel_checks_original_delivery_on_host_without_stopping_later_manual_turn(string variant)
    {
        var ct = TestContext.Current.CancellationToken;
        var (side, device, client, adapter, originProjects, origin, root, hp, mirror) = await SetUpCrossHostLoopbackAsync("octopus-bug07-host-cancel-", ct);
        await using var hostScope = side;
        using var devScope = device;
        await using var cliScope = client;
        using var coordScope = origin;

        var hostCoord = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        var mid = new ProjectItem { Id = "bug07-mid", Title = "Middle", Kind = ProjectItemKind.Agent, AgentId = "opencode", Surface = AgentSurface.Gui };
        var leaf = new ProjectItem { Id = "bug07-leaf", Title = "Leaf", Kind = ProjectItemKind.Agent, AgentId = "claude", Surface = AgentSurface.Gui };
        side.Projects.AddItem(hp, mid, activate: false);
        side.Projects.AddItem(hp, leaf, activate: false);
        originProjects.AddItem(mirror, new ProjectItem { Id = mid.Id, Title = mid.Title, Kind = mid.Kind, AgentId = mid.AgentId, Surface = mid.Surface }, activate: false);
        hostCoord.RegisterGuiSession(new StubParentBridge(leaf, hp.Path));

        var activeDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelivery = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manualEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseManual = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = acp =>
        {
            acp.CancelHandler = _ =>
            {
                var stop = new AcpPromptResult { StopReason = "cancelled" };
                if (manualEntered.Task.IsCompleted) releaseManual.TrySetResult(stop);
                else releaseDelivery.TrySetResult(stop);
                return Task.CompletedTask;
            };
            acp.PromptHandler = async (s, text, _, token) =>
            {
                if (text == "later manual")
                {
                    s.Emit(new AcpTextDeltaEvent("assistant", "manual still working", "human"));
                    manualEntered.TrySetResult();
                    return await releaseManual.Task.WaitAsync(token);
                }
                if (variant.StartsWith("deferred", StringComparison.Ordinal))
                {
                    var child = await hostCoord.CallToolAsync(mid.Id, "delegate_task", new JsonObject { ["target"] = leaf.Id, ["goal"] = "work at leaf" }, token);
                    Assert.Null(child["isError"]);
                }
                s.Emit(new AcpTextDeltaEvent("assistant", "original working", "original"));
                return await releaseDelivery.Task.WaitAsync(token);
            };
        };

        var vm = Assert.IsType<AgentGuiTabViewModel>(side.Tabs.GetOrCreate(hp, mid).DataContext);
        await WaitTaskAsync(vm.ConnectAsync(), ct);
        var acp = factory.Created.Last();

        adapter.AfterDelivery = update =>
        {
            if (update.Upserts.Any(e => e.Kind == AcpEntryKinds.Assistant && e.IsStreaming && e.Collab is { ExpectsReply: true, DeliveryId: not null } && e.Text == "original working"))
            {
                activeDelivered.TrySetResult();
            }
        };

        var launch = Body(await WaitTaskAsync(origin.CallToolAsync(root.Id, "delegate_task", new JsonObject { ["target"] = mid.Id, ["goal"] = "original delegated work" }, ct), ct));
        var taskId = launch["task_id"]!.GetValue<string>();
        var state = () => origin.GetTasksForItem(root.Id).Single(t => t.TaskId == taskId);
        await WaitTaskAsync(activeDelivered.Task, ct);

        var withheld = 0;
        Task? manual = null;
        if (variant != "still-active")
        {
            if (variant != "deferred-seen")
            {
                adapter.SuppressUpdate = _ => { Interlocked.Increment(ref withheld); return true; };
            }
            releaseDelivery.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
            await WaitUntilAsync(
                () => vm.State == AgentGuiState.Ready
                    && vm.Timeline.OfType<AssistantTurnItem>().Last().CollabCorrelation?.Deferred == variant.StartsWith("deferred", StringComparison.Ordinal),
                ct);
            if (variant == "deferred-seen")
            {
                await WaitUntilAsync(() => state().LatestActivity == "Waiting for delegated work", ct);
                adapter.SuppressUpdate = _ => { Interlocked.Increment(ref withheld); return true; };
            }
            vm.InputText = "later manual";
            manual = vm.SendPromptAsync();
            await WaitTaskAsync(manualEntered.Task, ct);
            await WaitUntilAsync(() => Volatile.Read(ref withheld) > 0, ct);
            Assert.Equal(variant == "deferred-seen", state().LatestActivity == "Waiting for delegated work");
        }

        var stopped = Body(await WaitTaskAsync(origin.CallToolAsync(root.Id, "cancel_task", new JsonObject { ["task_id"] = taskId }, ct), ct));
        Assert.Equal("cancelled", stopped["status"]!.GetValue<string>());
        Assert.Null(stopped["host_interrupt_skipped"]);
        Assert.Equal(CollabTaskStatus.Cancelled, state().Status);

        if (manual is null)
        {
            await WaitUntilAsync(() => acp.CancelCount == 1 && vm.State == AgentGuiState.Ready, ct);
            Assert.Equal("cancelled", vm.Timeline.OfType<AssistantTurnItem>().Last().StopReason);
            return;
        }

        for (var i = 0; i < 50 && !manual.IsCompleted; i++)
        {
            Frames.Pump();
            await Task.Delay(10, ct);
        }
        var releasedNormally = releaseManual.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitTaskAsync(manual, ct);
        var reason = vm.Timeline.OfType<AssistantTurnItem>().Last().StopReason;
        var leafStatus = hostCoord.GetTasksForItem(leaf.Id).SingleOrDefault()?.Status;
        if (variant.StartsWith("deferred", StringComparison.Ordinal))
        {
            Assert.Equal(CollabTaskStatus.Running, leafStatus);
        }
        if (variant == "deferred-seen")
        {
            Assert.Equal(0, adapter.ScopedCancelRequests);
        }
        Assert.True(
            releasedNormally && acp.CancelCount == 0 && adapter.UnscopedCancelRequests == 0 && reason != "cancelled",
            $"{variant}: origin={state().Status}, unscopedCancels={adapter.UnscopedCancelRequests}, scopedCancels={adapter.ScopedCancelRequests}, hostAcpStops={acp.CancelCount}, manualStopReason={reason}, leaf={leafStatus}, withheld={withheld}");
    }

    [AvaloniaFact, FreshApp]
    public async Task Direct_human_remote_stop_and_wire_compatibility_remain_usable_while_mismatched_delivery_cancel_is_ignored()
    {
        var ct = TestContext.Current.CancellationToken;
        // Wire compatibility: unscoped AcpCancelMessage serializes identically to AcpRefMessage and deserializes from it.
        var unscopedJson = System.Text.Json.JsonSerializer.Serialize(new AcpCancelMessage { ItemId = "item-1" }, RemoteJsonContext.Default.AcpCancelMessage);
        var refJson = System.Text.Json.JsonSerializer.Serialize(new AcpRefMessage { ItemId = "item-1" }, RemoteJsonContext.Default.AcpRefMessage);
        Assert.Equal(refJson, unscopedJson);
        var fromLegacyRef = System.Text.Json.JsonSerializer.Deserialize(refJson, RemoteJsonContext.Default.AcpCancelMessage)!;
        Assert.Equal("item-1", fromLegacyRef.ItemId);
        Assert.Null(fromLegacyRef.CollabThreadId);
        Assert.Null(fromLegacyRef.CollabTaskId);
        Assert.Null(fromLegacyRef.CollabDeliveryId);

        var (side, device, client, _, _, origin, _, hp, _) = await SetUpCrossHostLoopbackAsync("octopus-bug07-direct-stop-", ct);
        await using var hostScope = side;
        using var devScope = device;
        await using var cliScope = client;
        using var coordScope = origin;
        Assert.True(client.SupportsAcpCollabCancel);

        var hostItem = new ProjectItem { Id = "bug07-direct", Title = "Direct", Kind = ProjectItemKind.Agent, AgentId = "opencode", Surface = AgentSurface.Gui };
        side.Projects.AddItem(hp, hostItem, activate: false);

        var turnEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTurn = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = acp =>
        {
            acp.CancelHandler = _ =>
            {
                releaseTurn.TrySetResult(new AcpPromptResult { StopReason = "cancelled" });
                return Task.CompletedTask;
            };
            acp.PromptHandler = async (s, _, _, token) =>
            {
                s.Emit(new AcpTextDeltaEvent("assistant", "working", Guid.NewGuid().ToString("N")));
                turnEntered.TrySetResult();
                return await releaseTurn.Task.WaitAsync(token);
            };
        };

        var vm = Assert.IsType<AgentGuiTabViewModel>(side.Tabs.GetOrCreate(hp, hostItem).DataContext);
        await WaitTaskAsync(vm.ConnectAsync(), ct);
        var acp = factory.Created.Last();

        // Start a remote collab prompt with a canonical delegation envelope for delivery "del-2" on the host.
        await WaitTaskAsync(
            client.SendPromptAsync(
                hostItem.Id,
                CollabEnvelope.Delegation("task-r2", "thread-r2", "Origin", "Work round 2", [], "Origin"),
                attachments: null,
                mentions: null,
                immediate: false,
                collabDeliveryId: "del-2",
                cancellationToken: ct),
            ct);
        await WaitTaskAsync(turnEntered.Task, ct);
        var activeTurnCollab = vm.Timeline.OfType<UserTurnItem>().Last().CollabCorrelation;
        Assert.NotNull(activeTurnCollab);
        Assert.Equal("thread-r2", activeTurnCollab.ThreadId);
        Assert.Equal("task-r2", activeTurnCollab.TaskId);
        Assert.Equal("del-2", activeTurnCollab.DeliveryId);
        Assert.True(activeTurnCollab.ExpectsReply);

        // A stale scoped cancel for round 1 ("del-1") on the SAME thread/task must NOT interrupt round 2 ("del-2").
        await WaitTaskAsync(client.CancelTurnAsync(hostItem.Id, "thread-r2", "task-r2", "del-1", ct), ct);
        for (var i = 0; i < 25; i++)
        {
            Frames.Pump();
            await Task.Delay(10, ct);
        }
        Assert.Equal(0, acp.CancelCount);

        // Direct human Stop (no collab identity) still cancels the active turn immediately.
        await WaitTaskAsync(client.CancelTurnAsync(hostItem.Id, ct), ct);
        await WaitUntilAsync(() => acp.CancelCount == 1 && vm.State == AgentGuiState.Ready, ct);
        Assert.Equal("cancelled", vm.Timeline.OfType<AssistantTurnItem>().Last().StopReason);
    }

    [AvaloniaFact, FreshApp]
    public async Task Older_host_without_collab_cancel_capability_stays_wire_compatible_and_conservative()
    {
        var ct = TestContext.Current.CancellationToken;
        var (side, device, client, adapter, originProjects, origin, root, hp, mirror) =
            await SetUpCrossHostLoopbackAsync("octopus-bug07-old-peer-", ct, hostSupportsCollabCancel: false);
        await using var hostScope = side;
        using var devScope = device;
        await using var cliScope = client;
        using var coordScope = origin;
        Assert.False(client.SupportsAcpCollabCancel);
        Assert.False(adapter.SupportsScopedCancel);

        var hostItem = new ProjectItem { Id = "bug07-old-host", Title = "Old Host", Kind = ProjectItemKind.Agent, AgentId = "opencode", Surface = AgentSurface.Gui };
        side.Projects.AddItem(hp, hostItem, activate: false);
        originProjects.AddItem(mirror, new ProjectItem { Id = hostItem.Id, Title = hostItem.Title, Kind = hostItem.Kind, AgentId = hostItem.AgentId, Surface = hostItem.Surface }, activate: false);

        var manualEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseManual = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = acp =>
        {
            acp.CancelHandler = _ =>
            {
                releaseManual.TrySetResult(new AcpPromptResult { StopReason = "cancelled" });
                return Task.CompletedTask;
            };
            acp.PromptHandler = async (s, text, _, token) =>
            {
                if (text == "manual work")
                {
                    s.Emit(new AcpTextDeltaEvent("assistant", "human working", "human"));
                    manualEntered.TrySetResult();
                    return await releaseManual.Task.WaitAsync(token);
                }
                s.Emit(new AcpTextDeltaEvent("assistant", "done", "orig"));
                return new AcpPromptResult { StopReason = "end_turn" };
            };
        };

        var vm = Assert.IsType<AgentGuiTabViewModel>(side.Tabs.GetOrCreate(hp, hostItem).DataContext);
        await WaitTaskAsync(vm.ConnectAsync(), ct);
        var acp = factory.Created.Last();

        // Direct human Stop works on an older host without CapabilityAcpCollabCancel.
        vm.InputText = "manual work";
        var manual = vm.SendPromptAsync();
        await WaitTaskAsync(manualEntered.Task, ct);
        await WaitTaskAsync(client.CancelTurnAsync(hostItem.Id, ct), ct);
        await WaitTaskAsync(manual, ct);
        Assert.Equal(1, acp.CancelCount);
        Assert.Equal("cancelled", vm.Timeline.OfType<AssistantTurnItem>().Last().StopReason);
    }

    [AvaloniaTheory, FreshApp]
    [InlineData("completed-unseen")]
    [InlineData("deferred-unseen")]
    [InlineData("still-active")]
    [InlineData("deferred-seen")]
    public async Task Older_host_without_collab_cancel_declines_unscoped_task_cancel_even_when_stale_mirror_matches_but_preserves_explicit_user_stop(string variant)
    {
        var ct = TestContext.Current.CancellationToken;
        var (side, device, client, adapter, originProjects, origin, root, hp, mirror) =
            await SetUpCrossHostLoopbackAsync("octopus-bug07-old-replay-", ct, hostSupportsCollabCancel: false);
        await using var hostScope = side;
        using var devScope = device;
        await using var cliScope = client;
        using var coordScope = origin;
        Assert.False(client.SupportsAcpCollabCancel);
        Assert.False(adapter.SupportsScopedCancel);

        var hostCoord = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        var mid = new ProjectItem { Id = "bug07-old-mid", Title = "Old Middle", Kind = ProjectItemKind.Agent, AgentId = "opencode", Surface = AgentSurface.Gui };
        var leaf = new ProjectItem { Id = "bug07-old-leaf", Title = "Old Leaf", Kind = ProjectItemKind.Agent, AgentId = "claude", Surface = AgentSurface.Gui };
        side.Projects.AddItem(hp, mid, activate: false);
        side.Projects.AddItem(hp, leaf, activate: false);
        originProjects.AddItem(mirror, new ProjectItem { Id = mid.Id, Title = mid.Title, Kind = mid.Kind, AgentId = mid.AgentId, Surface = mid.Surface }, activate: false);
        hostCoord.RegisterGuiSession(new StubParentBridge(leaf, hp.Path));

        var activeDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelivery = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manualEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseManual = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = acp =>
        {
            acp.CancelHandler = _ =>
            {
                var stop = new AcpPromptResult { StopReason = "cancelled" };
                if (manualEntered.Task.IsCompleted) releaseManual.TrySetResult(stop);
                else releaseDelivery.TrySetResult(stop);
                return Task.CompletedTask;
            };
            acp.PromptHandler = async (s, text, _, token) =>
            {
                if (text == "later manual")
                {
                    s.Emit(new AcpTextDeltaEvent("assistant", "manual still working", "human"));
                    manualEntered.TrySetResult();
                    return await releaseManual.Task.WaitAsync(token);
                }
                if (variant.StartsWith("deferred", StringComparison.Ordinal))
                {
                    var child = await hostCoord.CallToolAsync(mid.Id, "delegate_task", new JsonObject { ["target"] = leaf.Id, ["goal"] = "work at leaf" }, token);
                    Assert.Null(child["isError"]);
                }
                s.Emit(new AcpTextDeltaEvent("assistant", "original working", "original"));
                return await releaseDelivery.Task.WaitAsync(token);
            };
        };

        var vm = Assert.IsType<AgentGuiTabViewModel>(side.Tabs.GetOrCreate(hp, mid).DataContext);
        await WaitTaskAsync(vm.ConnectAsync(), ct);
        var acp = factory.Created.Last();

        adapter.AfterDelivery = update =>
        {
            if (update.Upserts.Any(e => e.Kind == AcpEntryKinds.Assistant && e.IsStreaming && e.Collab is { ExpectsReply: true, DeliveryId: not null } && e.Text == "original working"))
            {
                activeDelivered.TrySetResult();
            }
        };

        var launch = Body(await WaitTaskAsync(origin.CallToolAsync(root.Id, "delegate_task", new JsonObject { ["target"] = mid.Id, ["goal"] = "original delegated work" }, ct), ct));
        var taskId = launch["task_id"]!.GetValue<string>();
        var threadId = launch["thread_id"]!.GetValue<string>();
        var state = () => origin.GetTasksForItem(root.Id).Single(t => t.TaskId == taskId);
        await WaitTaskAsync(activeDelivered.Task, ct);

        var withheld = 0;
        Task? manual = null;
        if (variant != "still-active")
        {
            if (variant != "deferred-seen")
            {
                adapter.SuppressUpdate = _ => { Interlocked.Increment(ref withheld); return true; };
            }
            releaseDelivery.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
            await WaitUntilAsync(
                () => vm.State == AgentGuiState.Ready
                    && vm.Timeline.OfType<AssistantTurnItem>().Last().CollabCorrelation?.Deferred == variant.StartsWith("deferred", StringComparison.Ordinal),
                ct);
            if (variant == "deferred-seen")
            {
                await WaitUntilAsync(() => state().LatestActivity == "Waiting for delegated work", ct);
                adapter.SuppressUpdate = _ => { Interlocked.Increment(ref withheld); return true; };
            }
            vm.InputText = "later manual";
            manual = vm.SendPromptAsync();
            await WaitTaskAsync(manualEntered.Task, ct);
            await WaitUntilAsync(() => Volatile.Read(ref withheld) > 0, ct);
        }

        // Task-directed cancel_task on an older host without acp.collabCancel preserves local bookkeeping
        // and declines the unscoped host interrupt rather than risking an unrelated human turn.
        var stopped = Body(await WaitTaskAsync(origin.CallToolAsync(root.Id, "cancel_task", new JsonObject { ["task_id"] = taskId }, ct), ct));
        Assert.Equal("cancelled", stopped["status"]!.GetValue<string>());
        Assert.Equal(CollabTaskStatus.Cancelled, state().Status);
        Assert.Equal(CollabThreadStatus.Cancelled, origin.FindThread(threadId)!.Status);

        if (variant == "deferred-seen")
        {
            Assert.Null(stopped["host_interrupt_skipped"]);
        }
        else
        {
            Assert.True(stopped["host_interrupt_skipped"]?.GetValue<bool>() == true);
            Assert.Contains("may still be running", stopped["note"]?.GetValue<string>() ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Contains("may still be running", state().ResultSummary ?? "", StringComparison.OrdinalIgnoreCase);
        }

        if (manual is null)
        {
            // Give any errant RPC time to arrive: automatic cancel_task must NOT send an unscoped stop to an older peer.
            for (var i = 0; i < 30; i++)
            {
                Frames.Pump();
                await Task.Delay(10, ct);
            }
            Assert.Equal(0, adapter.UnscopedCancelRequests);
            Assert.Equal(0, adapter.ScopedCancelRequests);
            Assert.Equal(0, acp.CancelCount);

            // Explicit user Stop on the same older peer still sends unscoped acp/cancel and stops the active turn.
            await WaitTaskAsync(client.CancelTurnAsync(mid.Id, ct), ct);
            await WaitUntilAsync(() => acp.CancelCount == 1 && vm.State == AgentGuiState.Ready, ct);
            Assert.Equal("cancelled", vm.Timeline.OfType<AssistantTurnItem>().Last().StopReason);
            return;
        }

        for (var i = 0; i < 50 && !manual.IsCompleted; i++)
        {
            Frames.Pump();
            await Task.Delay(10, ct);
        }
        var releasedNormally = releaseManual.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitTaskAsync(manual, ct);
        var reason = vm.Timeline.OfType<AssistantTurnItem>().Last().StopReason;
        var leafStatus = hostCoord.GetTasksForItem(leaf.Id).SingleOrDefault()?.Status;
        if (variant.StartsWith("deferred", StringComparison.Ordinal))
        {
            Assert.Equal(CollabTaskStatus.Running, leafStatus);
        }
        Assert.True(
            releasedNormally && acp.CancelCount == 0 && adapter.UnscopedCancelRequests == 0 && adapter.ScopedCancelRequests == 0 && reason != "cancelled",
            $"{variant}: origin={state().Status}, unscopedCancels={adapter.UnscopedCancelRequests}, scopedCancels={adapter.ScopedCancelRequests}, hostAcpStops={acp.CancelCount}, manualStopReason={reason}, leaf={leafStatus}, withheld={withheld}");
    }

    [AvaloniaTheory, FreshApp]
    [InlineData("short")]
    [InlineData("lost-header")]
    [InlineData("ordinary")]
    public async Task Legacy_remote_cancel_stays_conservative_without_host_identity_check(string presented)
    {
        var ct = TestContext.Current.CancellationToken;
        var (origin, root, project, host) = await SetUpOlderFakeRemoteAsync("octopus-bug07-legacy-cancel-");
        using var cleanup = origin;
        var existing = new ProjectItem { Id = "legacy-cancel", Title = "Old peer", Kind = ProjectItemKind.Agent, AgentId = "opencode", Surface = AgentSurface.Gui };
        AppHost.Get<IProjectService>().AddItem(project, existing, activate: false);
        var goal = presented == "lost-header" ? new string('x', AcpLimits.TextChars + 100) : "short gated work";
        var res = Body(await WaitTaskAsync(origin.CallToolAsync(root.Id, "delegate_task", new JsonObject { ["target"] = existing.Id, ["goal"] = goal }, ct), ct));
        var task = res["task_id"]!.GetValue<string>();
        var thread = res["thread_id"]!.GetValue<string>();
        var submitted = Assert.Single(host.Prompts).Text;
        var text = presented switch
        {
            "ordinary" => "human turn",
            "lost-header" => HostConversations.Tail(submitted, AcpLimits.TextChars)!,
            _ => submitted,
        };
        Assert.Equal(presented == "short", CollabEnvelope.TryExtractCorrelation(text)?.ExpectsReply == true);
        host.Push(existing.Id, AcpStates.Working, UserEntry("u", text), AssistantEntry("a", "streaming", streaming: true));
        var cancel = Body(await WaitTaskAsync(origin.CallToolAsync(root.Id, "cancel_task", new JsonObject { ["task_id"] = task }, ct), ct));
        Assert.Equal("cancelled", cancel["status"]!.GetValue<string>());
        Assert.True(cancel["host_interrupt_skipped"]?.GetValue<bool>() == true);
        Assert.Contains("may still be running", cancel["note"]?.GetValue<string>() ?? "", StringComparison.OrdinalIgnoreCase);
        var taskState = origin.GetTasksForItem(root.Id).Single(t => t.TaskId == task);
        Assert.Equal(CollabTaskStatus.Cancelled, taskState.Status);
        Assert.Contains("may still be running", taskState.ResultSummary ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CollabThreadStatus.Cancelled, origin.FindThread(thread)!.Status);

        // Even when the mirror displays a matching short envelope, an older host without acp.collabCancel cannot
        // prove host-time turn ownership, so automatic cancel_task declines the unscoped host interrupt.
        for (var i = 0; i < 30 && host.Cancels.IsEmpty; i++)
        {
            Frames.Pump();
            await Task.Delay(10, ct);
        }
        Assert.Empty(host.Cancels);

        // Explicit user Stop (unscoped, no collab identity) on the same older peer still forwards acp/cancel.
        var session = new RemoteCollabSession(host, "Older Host", existing.Id, "opencode", existing.Title, project.Path, root.Id, fresh: false, origin, _ => { });
        using var sessionScope = session;
        await session.CancelActiveTurnAsync();
        Assert.Equal(existing.Id, Assert.Single(host.Cancels));
    }

    /// <summary>
    /// Host-side scoped cancel identity matrix on a capable peer:
    /// - When a cancel request names a delivery ID, the active turn must actually carry and match that delivery ID
    ///   (never falling through to thread/task when a newer round on the same thread/task omitted its delivery ID).
    /// - Delivery-ID equality takes precedence when both sides carry a delivery ID.
    /// - Partial-identity compatibility: when a request omits deliveryId, matching thread/task (or thread-only on a
    ///   <see cref="CollabEnvelope.Message"/> turn that has no task ID) is permitted; informational notices and manual
    ///   turns are spared; direct human Stop and all-null requests cancel unconditionally.
    /// </summary>
    [AvaloniaTheory, FreshApp]
    [InlineData("reply", "exact", true)]
    [InlineData("reply", "wrong-everything", false)]
    [InlineData("reply", "prior-round", false)]
    [InlineData("reply", "delivery-only", true)]
    [InlineData("reply", "wrong-delivery-only", false)]
    [InlineData("reply", "right-delivery-other-thread-and-task", true)]
    [InlineData("reply", "thread-task-only", true)]
    [InlineData("reply", "same-thread-wrong-task", false)]
    [InlineData("reply", "thread-only", true)]
    [InlineData("reply", "task-only", true)]
    [InlineData("reply", "wrong-thread-only", false)]
    [InlineData("reply", "wrong-task-only", false)]
    [InlineData("reply", "all-null", true)]
    [InlineData("thread-only-reply", "same-thread-wrong-task", true)]
    [InlineData("reply-no-delivery", "prior-round", false)]
    [InlineData("reply-no-delivery", "wrong-delivery-only", false)]
    [InlineData("notice", "thread-only", false)]
    [InlineData("manual", "exact", false)]
    [InlineData("manual", "all-null", true)]
    [InlineData("manual", "human-stop", true)]
    public async Task Host_scoped_cancel_checks_exact_delivery_and_partial_identity_compatibility(string active, string request, bool expectStop)
    {
        var ct = TestContext.Current.CancellationToken;
        var (side, device, client, _, _, origin, _, project, _) = await SetUpCrossHostLoopbackAsync("octopus-bug07-host-id-", ct);
        await using var hostScope = side;
        using var devScope = device;
        await using var cliScope = client;
        using var coordScope = origin;
        Assert.True(client.SupportsAcpCollabCancel);

        var item = new ProjectItem { Id = "bug07-id-gui", Title = "Identity Host", Kind = ProjectItemKind.Agent, AgentId = "opencode", Surface = AgentSurface.Gui };
        side.Projects.AddItem(project, item, activate: false);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = AppHost.Get<FakeAcpSessionFactory>();
        factory.ConfigureNext = acp =>
        {
            acp.CancelHandler = _ =>
            {
                release.TrySetResult(new AcpPromptResult { StopReason = "cancelled" });
                return Task.CompletedTask;
            };
            acp.PromptHandler = async (session, text, _, token) =>
            {
                if (text.Contains("first round", StringComparison.Ordinal))
                {
                    return new AcpPromptResult { StopReason = "end_turn" };
                }
                session.Emit(new AcpTextDeltaEvent("assistant", "second round active", "second"));
                entered.TrySetResult();
                return await release.Task.WaitAsync(token);
            };
        };

        var vm = Assert.IsType<AgentGuiTabViewModel>(side.Tabs.GetOrCreate(project, item).DataContext);
        await WaitTaskAsync(vm.ConnectAsync(), ct);
        var acp = factory.Created.Last();

        const string threadId = "thread-1";
        const string taskId = "task-1";
        const string oldDelivery = "delivery-1";
        const string currentDelivery = "delivery-2";

        Task SendPromptAsync(string text, string? deliveryId) => client.SendPromptAsync(
            item.Id,
            text,
            attachments: null,
            mentions: null,
            immediate: false,
            collabDeliveryId: deliveryId,
            cancellationToken: ct);

        if (request == "prior-round")
        {
            await WaitTaskAsync(SendPromptAsync(CollabEnvelope.Delegation(taskId, threadId, "Origin", "first round", [], "Origin"), oldDelivery), ct);
            await WaitUntilAsync(() => acp.SentPrompts.Count == 1 && vm.State == AgentGuiState.Ready, ct);
        }

        var prompt = active switch
        {
            "notice" => CollabEnvelope.Interjection(threadId, "guidance"),
            "manual" => "manual prompt",
            "thread-only-reply" => CollabEnvelope.Message(threadId, "Origin", 2, 5, "continue", expectReply: true, toRemote: true),
            _ => CollabEnvelope.Delegation(taskId, threadId, "Origin", "second round", [], "Origin"),
        };
        var activeDelivery = active is "reply" or "thread-only-reply" ? currentDelivery : null;
        await WaitTaskAsync(SendPromptAsync(prompt, activeDelivery), ct);
        await WaitTaskAsync(entered.Task, ct);

        var (reqThread, reqTask, reqDelivery) = request switch
        {
            "exact" => (threadId, taskId, currentDelivery),
            "prior-round" => (threadId, taskId, oldDelivery),
            "wrong-everything" => ("other-thread", "other-task", oldDelivery),
            "right-delivery-other-thread-and-task" => ("other-thread", "other-task", currentDelivery),
            "delivery-only" => (null, null, currentDelivery),
            "wrong-delivery-only" => (null, null, oldDelivery),
            "thread-task-only" => (threadId, taskId, null),
            "same-thread-wrong-task" => (threadId, "other-task", null),
            "thread-only" => (threadId, null, null),
            "task-only" => (null, taskId, null),
            "wrong-thread-only" => ("other-thread", null, null),
            "wrong-task-only" => (null, "other-task", null),
            "human-stop" or "all-null" => (null, null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };

        await WaitTaskAsync(
            request == "human-stop"
                ? client.CancelTurnAsync(item.Id, ct)
                : client.CancelTurnAsync(item.Id, reqThread, reqTask, reqDelivery, ct),
            ct);

        var releasedNormally = release.TrySetResult(new AcpPromptResult { StopReason = "end_turn" });
        await WaitUntilAsync(() => vm.State == AgentGuiState.Ready, ct);
        var reason = vm.Timeline.OfType<AssistantTurnItem>().Last().StopReason;
        Assert.True(
            releasedNormally == !expectStop && acp.CancelCount == (expectStop ? 1 : 0) && (reason == "cancelled") == expectStop,
            $"active={active}, request={request}: hostAcpStops={acp.CancelCount}, releasedNormally={releasedNormally}, stopReason={reason}");
    }

    private static AcpEntryMessage JsonSerializerClone(AcpEntryMessage source)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(source, RemoteJsonContext.Default.AcpEntryMessage);
        return System.Text.Json.JsonSerializer.Deserialize(json, RemoteJsonContext.Default.AcpEntryMessage)!;
    }
}
