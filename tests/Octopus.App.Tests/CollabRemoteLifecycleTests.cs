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
/// Permanent regression tests for remote collaboration lifecycle handling in <see cref="RemoteCollabSession"/> (BUG-06):
/// sequence-gap reattach recovery vs permanent failure, early updates racing reattach, partial-text errors, host-side
/// turn cancellation vs normal idle, coalesced session errors, and real in-memory loopback wire behavior.
/// </summary>
public sealed class CollabRemoteLifecycleTests
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

        public List<string> SnapshotPrompts()
        {
            lock (_gate) return [.. DeliveredPrompts];
        }

        public string ReadRecentSummary(int maxTurns = 5) => "";
    }

    private sealed class FakeRemoteHosts : ICollabRemoteHosts
    {
        public Dictionary<string, ICollabRemoteHost> Hosts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);

        public FakeRemoteHost AddFake(string hostId, string name)
        {
            var host = new FakeRemoteHost(name);
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

    private sealed class FakeRemoteHost(string name) : ICollabRemoteHost
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);
        private int _items;
        private int _attachCalls;

        public string Name => name;
        public bool SupportsAcpGui => true;
        public Task Completion => _closed.Task;
        public event Action<AcpUpdateMessage>? AcpUpdated;
        public List<CreateItemMessage> Created { get; } = [];
        public List<(string ItemId, string Text)> Prompts { get; } = [];
        public ConcurrentQueue<string> Cancels { get; } = new();
        public Dictionary<string, AcpSnapshotMessage> Snapshots { get; } = new(StringComparer.Ordinal);
        public Func<string, int, CancellationToken, Task<AcpSnapshotMessage>>? OnAttach { get; set; }
        public int AttachCallCount => Volatile.Read(ref _attachCalls);
        public Func<ItemMessage, CancellationToken, Task>? BeforeCreateReply { get; set; }

        public async Task<ItemMessage> CreateItemAsync(CreateItemMessage request, CancellationToken ct)
        {
            Created.Add(request);
            var id = $"remote-life-{++_items}";
            var item = new ItemMessage
            {
                Id = id,
                ProjectId = request.ProjectId,
                Kind = request.Kind,
                AgentId = request.AgentId,
                Title = request.Title ?? request.AgentId ?? "",
            };
            if (BeforeCreateReply is { } callback)
            {
                await callback(item, ct);
            }
            return item;
        }

        public Task<AcpSnapshotMessage> AttachConversationAsync(string itemId, CancellationToken ct)
        {
            var callNumber = Interlocked.Increment(ref _attachCalls);
            if (OnAttach is { } handler)
            {
                return handler(itemId, callNumber, ct);
            }
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

        public void PushWithError(string itemId, string? state, string? errorText, params AcpEntryMessage[] upserts)
        {
            var seq = _sequences[itemId] = _sequences.GetValueOrDefault(itemId) + 1;
            AcpUpdated?.Invoke(new AcpUpdateMessage
            {
                ItemId = itemId,
                Sequence = seq,
                State = state,
                ErrorText = errorText,
                Upserts = [.. upserts],
            });
        }

        public void PushRaw(AcpUpdateMessage update)
        {
            _sequences[update.ItemId] = Math.Max(_sequences.GetValueOrDefault(update.ItemId), update.Sequence);
            AcpUpdated?.Invoke(update);
        }

        public void Disconnect() => _closed.TrySetResult();
    }

    private sealed class LoopbackRemoteHostAdapter : ICollabRemoteHost
    {
        private readonly RemoteHostClient _client;

        public LoopbackRemoteHostAdapter(RemoteHostClient client)
        {
            _client = client;
            _client.AcpUpdated += update => AcpUpdated?.Invoke(update);
        }

        public bool SupportsAcpGui => _client.SupportsAcpGui;
        public Task Completion => _client.Completion;
        public event Action<AcpUpdateMessage>? AcpUpdated;
        public int CreatedCount;
        public int PromptCount;

        public Task<ItemMessage> CreateItemAsync(CreateItemMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref CreatedCount);
            return _client.CreateItemAsync(request, ct);
        }

        public Task<AcpSnapshotMessage> AttachConversationAsync(string itemId, CancellationToken ct) =>
            _client.AttachConversationAsync(itemId, ct);

        public Task DetachConversationAsync(string itemId, CancellationToken ct) =>
            _client.DetachConversationAsync(itemId, ct);

        public Task SendPromptAsync(string itemId, string text, CancellationToken ct)
        {
            Interlocked.Increment(ref PromptCount);
            return _client.SendPromptAsync(itemId, text, null, ct);
        }

        public Task CancelTurnAsync(string itemId, CancellationToken ct) => _client.CancelTurnAsync(itemId, ct);
        public Task RespondAsync(AcpRespondMessage answer, CancellationToken ct) => _client.RespondAsync(answer, ct);
    }

    private sealed class LoopbackHostSide : IAsyncDisposable
    {
        public DeviceIdentity Identity { get; } = DeviceIdentity.Create();
        public PairedPeerStore Devices { get; } = new(null);
        public ProjectService Projects { get; } = new(new InMemoryProjectStore(), AppHost.Get<PathComparer>());
        public ITabContentRegistry Tabs { get; } = AppHost.Get<ITabContentRegistry>();
        public HostWorkspace Workspace { get; }
        public RemoteHostService Service { get; }

        public LoopbackHostSide()
        {
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
            Service = new RemoteHostService(
                Identity,
                () => new DeviceDescription("Loopback Host", DeviceKind.Desktop),
                Devices,
                Workspace,
                new HelloMessage
                {
                    AppVersion = "test",
                    Capabilities = [RemoteMethods.CapabilityWorkspace, RemoteMethods.CapabilityAcpGui],
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

    private static JsonObject Body(JsonObject toolResult) =>
        JsonNode.Parse(toolResult["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

    private static AcpEntryMessage UserEntry(string id, string text) =>
        new() { Id = id, Kind = AcpEntryKinds.User, Text = text };

    private static AcpEntryMessage AssistantEntry(string id, string? text, bool streaming = false, string? errorText = null) =>
        new() { Id = id, Kind = AcpEntryKinds.Assistant, Text = text, IsStreaming = streaming, ErrorText = errorText };

    private static AcpEntryMessage PermissionCard(string id, string title) => new()
    {
        Id = id,
        Kind = AcpEntryKinds.Permission,
        Permission = new AcpPermissionMessage
        {
            Title = title,
            Options =
            [
                new AcpOptionMessage { Id = "o-allow", Label = "Allow", Detail = "allow_once" },
                new AcpOptionMessage { Id = "o-reject", Label = "Reject", Detail = "reject_once" },
            ],
        },
    };

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

    private static async Task<(CollabCoordinator Coordinator, ProjectItem Parent, StubParentBridge ParentBridge, Project RemoteProject, FakeRemoteHost Host)> SetUpFakeRemoteAsync(string prefix)
    {
        await Task.Yield();
        var dir = Directory.CreateTempSubdirectory(prefix).FullName;
        var projects = AppHost.Get<IProjectService>();
        var local = projects.AddProject(dir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Lead", Surface = AgentSurface.Gui };
        projects.AddItem(local, parent);

        var remote = projects.AddProject(Directory.CreateTempSubdirectory(prefix + "remote-").FullName);
        remote.RemoteHost = "host-1";
        remote.RemoteId = "rp-1";
        remote.Name = "Remote Repo";
        projects.SetCurrent(local.Id);

        var fakes = new FakeRemoteHosts();
        var host = fakes.AddFake("host-1", "Build Server");
        var coordinator = new CollabCoordinator(
            projects,
            AppHost.Get<IAgentCatalog>(),
            AppHost.Get<IAgentScanner>(),
            AppHost.Get<ISettingsStore>(),
            new CollabMcpServer(),
            remoteHostsFactory: () => fakes);
        var bridge = new StubParentBridge(parent, dir);
        coordinator.RegisterGuiSession(bridge);
        return (coordinator, parent, bridge, remote, host);
    }

    [AvaloniaFact, FreshApp]
    public async Task Sequence_gap_with_exhausted_transient_reattach_fails_task_clears_approvals_and_never_reexecutes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, parentBridge, remote, host) = await SetUpFakeRemoteAsync("octopus-bug06-exhaust-");
        using var _ = coordinator;

        host.OnAttach = (itemId, call, _) =>
        {
            if (call == 1)
            {
                return Task.FromResult(new AcpSnapshotMessage { ItemId = itemId, Sequence = 0, State = AcpStates.Connecting });
            }
            return Task.FromException<AcpSnapshotMessage>(new IOException($"reattach failed #{call}"));
        };

        var delegated = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["title"] = "Run suite", ["goal"] = "Run integration tests", ["project_id"] = remote.Id },
            ct));
        var taskId = delegated["task_id"]!.GetValue<string>();
        var threadId = delegated["thread_id"]!.GetValue<string>();
        var childId = delegated["session_id"]!.GetValue<string>();
        var createdPrompt = Assert.Single(host.Created).Prompt!;

        // Normal seq=1 with an open permission card bubbles approval to parent.
        host.Push(childId, AcpStates.Waiting, UserEntry("u1", createdPrompt), AssistantEntry("a1", "working...", streaming: true), PermissionCard("p1", "dotnet test"));
        Assert.Single(coordinator.GetBubbledApprovalsForParent(parent.Id));
        Assert.Equal(CollabTaskStatus.WaitingApproval, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status);

        // Sequence gap: jump from seq=1 to seq=3 (skipping seq=2) while every reattach throws IOException.
        host.PushRaw(new AcpUpdateMessage
        {
            ItemId = childId,
            Sequence = 3,
            State = AcpStates.Ready,
            Upserts = [AssistantEntry("a1", "missed output", streaming: false)],
        });

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status == CollabTaskStatus.Failed,
            ct,
            timeoutMs: 3000,
            message: "Task must transition to Failed when bounded reattach retries are exhausted.");

        var failedTask = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId);
        Assert.Equal(CollabTaskStatus.Failed, failedTask.Status);
        Assert.Contains("Build Server", failedTask.ResultSummary ?? "", StringComparison.Ordinal);
        Assert.Equal(CollabThreadStatus.Blocked, coordinator.FindThread(threadId)!.Status);
        Assert.Empty(coordinator.GetBubbledApprovalsForParent(parent.Id));

        // Bounded transient retries happened (>1 total reattach attempt, bounded <= 4 total attach calls), and worker work was never re-executed.
        Assert.InRange(host.AttachCallCount, 3, 5);
        Assert.Single(host.Created);
        Assert.Empty(host.Prompts);

        await WaitUntilAsync(
            () => parentBridge.SnapshotPrompts().Any(p => p.Contains("Status: blocked", StringComparison.Ordinal)),
            ct,
            timeoutMs: 2000,
            message: "Parent must be notified when remote reattach permanently fails.");
    }

    [AvaloniaFact, FreshApp]
    public async Task Sequence_gap_with_permanent_reattach_error_fails_fast_without_retrying_or_reexecuting()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, _, remote, host) = await SetUpFakeRemoteAsync("octopus-bug06-perm-");
        using var _ = coordinator;

        host.OnAttach = (itemId, call, _) =>
        {
            if (call == 1)
            {
                return Task.FromResult(new AcpSnapshotMessage { ItemId = itemId, Sequence = 0, State = AcpStates.Connecting });
            }
            return Task.FromException<AcpSnapshotMessage>(new RemoteException(RemoteMethods.NotFound, "The item does not exist on the host."));
        };

        var delegated = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["title"] = "Build", ["goal"] = "Build app", ["project_id"] = remote.Id },
            ct));
        var taskId = delegated["task_id"]!.GetValue<string>();
        var childId = delegated["session_id"]!.GetValue<string>();
        var createdPrompt = Assert.Single(host.Created).Prompt!;

        host.Push(childId, AcpStates.Working, UserEntry("u1", createdPrompt), AssistantEntry("a1", "compiling...", streaming: true));

        // Gap triggers reattach, which returns permanent RemoteMethods.NotFound.
        host.PushRaw(new AcpUpdateMessage { ItemId = childId, Sequence = 3, State = AcpStates.Working });

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status == CollabTaskStatus.Failed,
            ct,
            timeoutMs: 2000,
            message: "Task must fail immediately on permanent RemoteMethods.NotFound reattach error.");

        var failedTask = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId);
        Assert.Contains("does not exist", failedTask.ResultSummary ?? "", StringComparison.OrdinalIgnoreCase);
        // Initial attach (1) + single failed reattach (2): no retries on permanent error.
        Assert.Equal(2, host.AttachCallCount);
        Assert.Single(host.Created);
        Assert.Empty(host.Prompts);
    }

    [AvaloniaFact, FreshApp]
    public async Task Sequence_gap_with_transient_reattach_failure_recovers_on_retry_and_completes_without_reexecuting()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, parentBridge, remote, host) = await SetUpFakeRemoteAsync("octopus-bug06-recover-");
        using var _ = coordinator;

        string? createdPrompt = null;
        host.OnAttach = (itemId, call, _) =>
        {
            if (call == 1)
            {
                return Task.FromResult(new AcpSnapshotMessage { ItemId = itemId, Sequence = 0, State = AcpStates.Connecting });
            }
            if (call == 2)
            {
                return Task.FromException<AcpSnapshotMessage>(new IOException("temporary socket blip"));
            }
            return Task.FromResult(new AcpSnapshotMessage
            {
                ItemId = itemId,
                Sequence = 3,
                State = AcpStates.Ready,
                Entries = [UserEntry("u1", createdPrompt!), AssistantEntry("a1", "Recovered final result", streaming: false)],
            });
        };

        var delegated = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["title"] = "Verify", ["goal"] = "Verify build", ["project_id"] = remote.Id },
            ct));
        var taskId = delegated["task_id"]!.GetValue<string>();
        var childId = delegated["session_id"]!.GetValue<string>();
        createdPrompt = Assert.Single(host.Created).Prompt!;

        host.Push(childId, AcpStates.Working, UserEntry("u1", createdPrompt), AssistantEntry("a1", "starting...", streaming: true));
        host.PushRaw(new AcpUpdateMessage { ItemId = childId, Sequence = 3, State = AcpStates.Ready });

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status == CollabTaskStatus.Completed,
            ct,
            timeoutMs: 3000,
            message: "Transient reattach error should recover on bounded retry and complete the task.");

        var task = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId);
        Assert.Equal("Recovered final result", task.ResultSummary);
        Assert.Equal(3, host.AttachCallCount);
        Assert.Single(host.Created);
        Assert.Empty(host.Prompts);
        await WaitUntilAsync(
            () => parentBridge.SnapshotPrompts().Any(p => p.Contains("Recovered final result", StringComparison.Ordinal)),
            ct,
            timeoutMs: 2000);
    }

    [AvaloniaFact, FreshApp]
    public async Task Update_arriving_while_reattach_snapshot_is_in_flight_is_replayed_not_dropped()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, _, remote, host) = await SetUpFakeRemoteAsync("octopus-bug06-race-");
        using var _ = coordinator;

        var reattachStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reattachSnapshot = new TaskCompletionSource<AcpSnapshotMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnAttach = (itemId, call, _) =>
        {
            if (call == 1)
            {
                return Task.FromResult(new AcpSnapshotMessage { ItemId = itemId, Sequence = 0, State = AcpStates.Connecting });
            }
            reattachStarted.TrySetResult();
            return reattachSnapshot.Task;
        };

        var delegated = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["title"] = "Race", ["goal"] = "Check race", ["project_id"] = remote.Id },
            ct));
        var taskId = delegated["task_id"]!.GetValue<string>();
        var childId = delegated["session_id"]!.GetValue<string>();
        var createdPrompt = Assert.Single(host.Created).Prompt!;

        host.Push(childId, AcpStates.Working, UserEntry("u1", createdPrompt), AssistantEntry("a1", "step 1", streaming: true));
        host.PushRaw(new AcpUpdateMessage { ItemId = childId, Sequence = 3, State = AcpStates.Working });
        await reattachStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);

        // seq=4 (the final completion update) arrives BEFORE the reattach snapshot (seq=3) returns.
        host.PushRaw(new AcpUpdateMessage
        {
            ItemId = childId,
            Sequence = 4,
            State = AcpStates.Ready,
            Upserts = [AssistantEntry("a1", "Raced completion answer", streaming: false)],
        });

        reattachSnapshot.TrySetResult(new AcpSnapshotMessage
        {
            ItemId = childId,
            Sequence = 3,
            State = AcpStates.Working,
            Entries = [UserEntry("u1", createdPrompt), AssistantEntry("a1", "step 2", streaming: true)],
        });

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status == CollabTaskStatus.Completed,
            ct,
            timeoutMs: 2000,
            message: "Update arriving during in-flight reattach must be replayed after snapshot instead of dropped.");

        Assert.Equal("Raced completion answer", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).ResultSummary);
        Assert.Equal(2, host.AttachCallCount);
        Assert.Single(host.Created);
        Assert.Empty(host.Prompts);
    }

    [AvaloniaFact, FreshApp]
    public async Task Remote_turn_with_partial_text_and_error_text_fails_instead_of_completing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, _, remote, host) = await SetUpFakeRemoteAsync("octopus-bug06-partial-err-");
        using var _ = coordinator;

        var delegated = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["title"] = "Analyze", ["goal"] = "Analyze logs", ["project_id"] = remote.Id },
            ct));
        var taskId = delegated["task_id"]!.GetValue<string>();
        var threadId = delegated["thread_id"]!.GetValue<string>();
        var childId = delegated["session_id"]!.GetValue<string>();
        var createdPrompt = Assert.Single(host.Created).Prompt!;

        host.Push(childId, AcpStates.Working, UserEntry("u1", createdPrompt), AssistantEntry("a1", "Halfway through analysis...", streaming: true));
        host.Push(childId, AcpStates.Ready, AssistantEntry("a1", "Halfway through analysis...", streaming: false, errorText: "upstream provider 503"));

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).IsFinished,
            ct,
            timeoutMs: 2000);

        var task = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId);
        Assert.Equal(CollabTaskStatus.Failed, task.Status);
        Assert.Contains("upstream provider 503", task.ResultSummary ?? "", StringComparison.Ordinal);
        Assert.Equal(CollabThreadStatus.Blocked, coordinator.FindThread(threadId)!.Status);
    }

    [AvaloniaFact, FreshApp]
    public async Task Coalesced_completed_turn_and_session_error_settles_both_and_reconnect_does_not_replay_aborted_turn()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, _, remote, host) = await SetUpFakeRemoteAsync("octopus-bug06-coalesced-err-");
        using var _ = coordinator;

        var projects = AppHost.Get<IProjectService>();
        var ops = new ProjectItem { Id = "ops-coalesced-1", Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Ops" };
        projects.AddItem(remote, ops, activate: false);
        host.Snapshots[ops.Id] = new AcpSnapshotMessage { ItemId = ops.Id, Sequence = 1, State = AcpStates.Ready };

        var taskA = Body(await coordinator.CallToolAsync(parent.Id, "delegate_task", new JsonObject { ["target"] = ops.Id, ["goal"] = "Remote A" }, ct))["task_id"]!.GetValue<string>();
        var taskB = Body(await coordinator.CallToolAsync(parent.Id, "delegate_task", new JsonObject { ["target"] = ops.Id, ["goal"] = "Remote B" }, ct))["task_id"]!.GetValue<string>();
        Assert.Equal(2, host.Prompts.Count);
        var promptA = host.Prompts[0].Text;
        var promptB = host.Prompts[1].Text;

        // Normal idle check before any task finishes: duplicate seq + Ready heartbeat + parent idle must keep both tasks running.
        host.PushRaw(new AcpUpdateMessage { ItemId = ops.Id, Sequence = 1, State = AcpStates.Ready });
        host.Push(ops.Id, AcpStates.Ready);
        coordinator.NotifyTurnCompleted(parent.Id, "Parent turn ended normally while remote works.");
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status);

        // Coalesced update: turn A completed cleanly, then turn B started and crashed the agent into AcpStates.Error.
        host.PushWithError(
            ops.Id,
            AcpStates.Error,
            "agent process crashed",
            UserEntry("u-a", promptA),
            AssistantEntry("a-a", "A succeeded", streaming: false),
            UserEntry("u-b", promptB),
            AssistantEntry("a-b", "B partial before crash", streaming: false));

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).Status == CollabTaskStatus.Completed
                && coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).Status == CollabTaskStatus.Failed,
            ct,
            timeoutMs: 2000,
            message: "Coalesced update must complete A AND fail B on AcpStates.Error.");

        Assert.Equal("A succeeded", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskA).ResultSummary);
        Assert.Contains("agent process crashed", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskB).ResultSummary ?? "", StringComparison.Ordinal);

        // Session later reconnects to Ready, and a fresh task C is delegated: aborted a-b must never be replayed.
        host.PushWithError(ops.Id, AcpStates.Ready, "");
        var taskC = Body(await coordinator.CallToolAsync(parent.Id, "delegate_task", new JsonObject { ["target"] = ops.Id, ["goal"] = "Remote C" }, ct))["task_id"]!.GetValue<string>();
        host.Push(ops.Id, AcpStates.Ready);
        await Task.Delay(80, ct);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskC).Status);
    }

    [AvaloniaFact, FreshApp]
    public async Task Disconnect_during_reattach_backoff_settles_once_and_clears_approvals()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, parentBridge, remote, host) = await SetUpFakeRemoteAsync("octopus-bug06-disc-reattach-");
        using var _ = coordinator;

        var firstReattachFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnAttach = (itemId, call, _) =>
        {
            if (call == 1)
            {
                return Task.FromResult(new AcpSnapshotMessage { ItemId = itemId, Sequence = 0, State = AcpStates.Connecting });
            }
            firstReattachFailed.TrySetResult();
            return Task.FromException<AcpSnapshotMessage>(new IOException("transient before disconnect"));
        };

        var delegated = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["title"] = "Deploy", ["goal"] = "Check deploy", ["project_id"] = remote.Id },
            ct));
        var taskId = delegated["task_id"]!.GetValue<string>();
        var childId = delegated["session_id"]!.GetValue<string>();
        var createdPrompt = Assert.Single(host.Created).Prompt!;

        host.Push(childId, AcpStates.Waiting, UserEntry("u1", createdPrompt), AssistantEntry("a1", "waiting...", streaming: true), PermissionCard("p1", "deploy"));
        Assert.Single(coordinator.GetBubbledApprovalsForParent(parent.Id));

        host.PushRaw(new AcpUpdateMessage { ItemId = childId, Sequence = 3, State = AcpStates.Working });
        await firstReattachFailed.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);

        host.Disconnect();

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId).Status == CollabTaskStatus.Failed,
            ct,
            timeoutMs: 2000);

        var task = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == taskId);
        Assert.Contains("Lost the connection to Build Server", task.ResultSummary ?? "", StringComparison.Ordinal);
        Assert.Empty(coordinator.GetBubbledApprovalsForParent(parent.Id));

        await WaitUntilAsync(() => parentBridge.SnapshotPrompts().Count == 1, ct, timeoutMs: 2000);
        await Task.Delay(250, ct);
        Assert.Single(parentBridge.SnapshotPrompts());
    }

    [AvaloniaFact, FreshApp]
    public async Task Loopback_remote_host_distinguishes_host_stop_turn_error_disconnect_and_normal_idle()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = AppHost.Get<FakeAcpSessionFactory>();

        var turnCount = 0;
        var turn1Streaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn1Cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn2Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn2Fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn3Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn3Hold = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        fakes.ConfigureNext = session =>
        {
            session.CancelHandler = _ =>
            {
                turn1Cancelled.TrySetResult();
                return Task.CompletedTask;
            };
            session.PromptHandler = async (s, _, _, token) =>
            {
                var n = Interlocked.Increment(ref turnCount);
                if (n == 1)
                {
                    s.Emit(new AcpTextDeltaEvent("assistant", "Partial before host stop", "msg-1"));
                    turn1Streaming.TrySetResult();
                    await turn1Cancelled.Task.WaitAsync(token);
                    return new AcpPromptResult { StopReason = "cancelled" };
                }
                if (n == 2)
                {
                    s.Emit(new AcpTextDeltaEvent("assistant", "Partial before turn error", "msg-2"));
                    turn2Started.TrySetResult();
                    await turn2Fail.Task.WaitAsync(token);
                    throw new InvalidOperationException("model overloaded");
                }
                s.Emit(new AcpTextDeltaEvent("assistant", "Still running when link drops", "msg-3"));
                turn3Started.TrySetResult();
                return await turn3Hold.Task.WaitAsync(token);
            };
        };

        await using var hostSide = new LoopbackHostSide();
        using var clientDevice = DeviceIdentity.Create();
        var deviceDesc = new DeviceDescription("Local Desktop", DeviceKind.Desktop);
        hostSide.Devices.Add(clientDevice.PublicKey, deviceDesc);

        var hostProject = hostSide.Projects.AddProject(Directory.CreateTempSubdirectory("octopus-loopback-host-").FullName);
        var (a, b) = InMemoryTransport.CreatePair();
        _ = hostSide.Service.ServeAsync(b);
        await using var client = await WaitTaskAsync(
            RemoteHostClient.ConnectAsync(
                a,
                clientDevice,
                deviceDesc,
                PeerExpectation.Paired(hostSide.Identity.PublicKey),
                new HelloMessage
                {
                    AppVersion = "test",
                    Capabilities = [RemoteMethods.CapabilityWorkspace, RemoteMethods.CapabilityAcpGui],
                    TerminalBuild = "build-1",
                }),
            ct);

        var adapter = new LoopbackRemoteHostAdapter(client);
        var localDir = Directory.CreateTempSubdirectory("octopus-loopback-local-").FullName;
        var localProjects = AppHost.Get<IProjectService>();
        var localProject = localProjects.AddProject(localDir);
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Lead", Surface = AgentSurface.Gui };
        localProjects.AddItem(localProject, parent);

        var mirroredRemoteProject = localProjects.AddProject(Directory.CreateTempSubdirectory("octopus-loopback-mirror-").FullName);
        mirroredRemoteProject.RemoteHost = "loopback-host";
        mirroredRemoteProject.RemoteId = hostProject.Id;
        mirroredRemoteProject.Name = "Host Repo";
        localProjects.SetCurrent(localProject.Id);

        var remoteHosts = new FakeRemoteHosts();
        remoteHosts.AddHost("loopback-host", "Loopback Host", adapter);

        using var coordinator = new CollabCoordinator(
            localProjects,
            AppHost.Get<IAgentCatalog>(),
            AppHost.Get<IAgentScanner>(),
            AppHost.Get<ISettingsStore>(),
            new CollabMcpServer(),
            remoteHostsFactory: () => remoteHosts);
        var parentBridge = new StubParentBridge(parent, localDir);
        coordinator.RegisterGuiSession(parentBridge);

        // 1. Delegate to loopback host; ordinary local parent idle keeps remote work running.
        var delegated1 = Body(await WaitTaskAsync(
            coordinator.CallToolAsync(
                parent.Id,
                "delegate_task",
                new JsonObject { ["target"] = "opencode", ["title"] = "Loopback Job", ["goal"] = "Run job 1", ["project_id"] = mirroredRemoteProject.Id },
                ct),
            ct));
        var task1 = delegated1["task_id"]!.GetValue<string>();
        var thread1 = delegated1["thread_id"]!.GetValue<string>();
        var remoteItemId = delegated1["session_id"]!.GetValue<string>();

        await WaitTaskAsync(turn1Streaming.Task, ct);
        coordinator.NotifyTurnCompleted(parent.Id, "Parent idle while remote runs");
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task1).Status);

        // User stops the turn on the host side -> task must become Cancelled (not Completed!).
        var hostItem = hostSide.Projects.FindItem(remoteItemId)!;
        var hostModel = Assert.IsType<AgentGuiTabViewModel>(hostSide.Tabs.GetOrCreate(hostProject, hostItem).DataContext);
        await WaitTaskAsync(hostModel.CancelTurnAsync(), ct);

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task1).IsFinished,
            ct,
            timeoutMs: 4000,
            message: "Host-stopped remote turn must settle the delegated task.");

        var settled1 = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task1);
        Assert.Equal(CollabTaskStatus.Cancelled, settled1.Status);
        Assert.Equal(CollabThreadStatus.Cancelled, coordinator.FindThread(thread1)!.Status);
        Assert.Equal(1, Volatile.Read(ref adapter.CreatedCount));
        Assert.Equal(0, Volatile.Read(ref adapter.PromptCount));

        // 2. Second delegation to the same remote session: host turn emits partial text then throws -> Failed (not Completed!).
        var delegated2 = Body(await WaitTaskAsync(
            coordinator.CallToolAsync(
                parent.Id,
                "delegate_task",
                new JsonObject { ["target"] = remoteItemId, ["goal"] = "Run job 2" },
                ct),
            ct));
        var task2 = delegated2["task_id"]!.GetValue<string>();
        var thread2 = delegated2["thread_id"]!.GetValue<string>();
        await WaitTaskAsync(turn2Started.Task, ct);
        turn2Fail.TrySetResult();

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task2).IsFinished,
            ct,
            timeoutMs: 4000,
            message: "Remote turn throwing after partial text must settle.");

        var settled2 = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task2);
        Assert.Equal(CollabTaskStatus.Failed, settled2.Status);
        Assert.Contains("model overloaded", settled2.ResultSummary ?? "", StringComparison.Ordinal);
        Assert.Equal(CollabThreadStatus.Blocked, coordinator.FindThread(thread2)!.Status);

        // 3. Third delegation: loopback transport drops mid-turn -> Failed with honest disconnect reason.
        var delegated3 = Body(await WaitTaskAsync(
            coordinator.CallToolAsync(
                parent.Id,
                "delegate_task",
                new JsonObject { ["target"] = remoteItemId, ["goal"] = "Run job 3" },
                ct),
            ct));
        var task3 = delegated3["task_id"]!.GetValue<string>();
        await WaitTaskAsync(turn3Started.Task, ct);

        await client.DisposeAsync();

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task3).Status == CollabTaskStatus.Failed,
            ct,
            timeoutMs: 4000,
            message: "Dropping the loopback remote connection must fail active remote task.");

        var settled3 = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task3);
        Assert.Contains("Lost the connection to Loopback Host", settled3.ResultSummary ?? "", StringComparison.Ordinal);
        turn3Hold.TrySetCanceled();
    }

    [AvaloniaFact, FreshApp]
    public async Task Overflow_during_resnapshot_resynchronizes_final_delivery()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, _, remote, host) = await SetUpFakeRemoteAsync("octopus-bug06-replay-cap-");
        using var cleanup = coordinator;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<AcpSnapshotMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        AcpEntryMessage? final = null;
        host.OnAttach = (id, n, _) =>
        {
            if (n == 1) return Task.FromResult(new AcpSnapshotMessage { ItemId = id, Sequence = 0, State = AcpStates.Ready });
            if (n == 2)
            {
                entered.TrySetResult();
                return gate.Task;
            }
            return Task.FromResult(new AcpSnapshotMessage { ItemId = id, Sequence = 260, State = AcpStates.Ready, Entries = [final!] });
        };

        var res = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["goal"] = "Finish once", ["project_id"] = remote.Id },
            ct));
        var child = res["session_id"]!.GetValue<string>();
        var task = res["task_id"]!.GetValue<string>();
        final = AssistantEntry("answer", "final answer", streaming: false);
        final.Collab = new AcpCollabCorrelationMessage { DeliveryId = Assert.Single(host.Created).CollabDeliveryId, ExpectsReply = true };

        host.PushRaw(new AcpUpdateMessage { ItemId = child, Sequence = 3, State = AcpStates.Working });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        for (var seq = 4; seq < 260; seq++)
        {
            host.PushRaw(new AcpUpdateMessage { ItemId = child, Sequence = seq, State = AcpStates.Working });
        }
        host.PushRaw(new AcpUpdateMessage { ItemId = child, Sequence = 260, State = AcpStates.Ready, Upserts = [final] });
        gate.SetResult(new AcpSnapshotMessage { ItemId = child, Sequence = 3, State = AcpStates.Working });

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task).Status == CollabTaskStatus.Completed,
            ct,
            timeoutMs: 3000,
            message: $"Expected overflow during resnapshot to resynchronize final delivery (attaches={host.AttachCallCount}).");

        var actual = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task);
        Assert.Equal("final answer", actual.ResultSummary);
        Assert.Equal(3, host.AttachCallCount);
        Assert.Single(host.Created);
        Assert.Empty(host.Prompts);
    }

    [AvaloniaFact, FreshApp]
    public async Task Repeated_overflow_during_resnapshot_fails_bounded_without_reexecuting()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, _, remote, host) = await SetUpFakeRemoteAsync("octopus-bug06-replay-exhaust-");
        using var cleanup = coordinator;
        long baseSeq = 3;
        host.OnAttach = (id, n, _) =>
        {
            if (n == 1)
            {
                return Task.FromResult(new AcpSnapshotMessage { ItemId = id, Sequence = 0, State = AcpStates.Ready });
            }
            var snapSeq = baseSeq;
            baseSeq += 300;
            // Flood more than 256 updates while this attach snapshot is returning a stale sequence.
            for (var seq = snapSeq + 1; seq <= snapSeq + 260; seq++)
            {
                host.PushRaw(new AcpUpdateMessage { ItemId = id, Sequence = seq, State = AcpStates.Working });
            }
            return Task.FromResult(new AcpSnapshotMessage { ItemId = id, Sequence = snapSeq, State = AcpStates.Working });
        };

        var res = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["goal"] = "Flood test", ["project_id"] = remote.Id },
            ct));
        var child = res["session_id"]!.GetValue<string>();
        var task = res["task_id"]!.GetValue<string>();
        var thread = res["thread_id"]!.GetValue<string>();

        host.PushRaw(new AcpUpdateMessage { ItemId = child, Sequence = 3, State = AcpStates.Working });

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task).Status == CollabTaskStatus.Failed,
            ct,
            timeoutMs: 3000,
            message: "Repeated replay buffer overflow must terminate with synchronization failure instead of sticking in Running.");

        var actual = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task);
        Assert.Equal(CollabThreadStatus.Blocked, coordinator.FindThread(thread)!.Status);
        Assert.Contains("Build Server", actual.ResultSummary ?? "", StringComparison.Ordinal);
        Assert.InRange(host.AttachCallCount, 3, 5);
        Assert.Single(host.Created);
        Assert.Empty(host.Prompts);
    }

    [AvaloniaFact, FreshApp]
    public async Task Cancelled_initial_attach_allows_a_new_follow_and_answer()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, _, remote, host) = await SetUpFakeRemoteAsync("octopus-bug06-cancel-attach-");
        using var cleanup = coordinator;
        var item = new ProjectItem { Id = "existing-remote", Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Worker" };
        AppHost.Get<IProjectService>().AddItem(remote, item, activate: false);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var entering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnAttach = async (id, n, token) =>
        {
            if (n == 1)
            {
                entering.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return new AcpSnapshotMessage { ItemId = id, State = AcpStates.Ready, Sequence = 0 };
        };

        var first = coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = item.Id, ["goal"] = "cancel me" },
            cancel.Token);
        await entering.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        cancel.Cancel();
        try { await first; } catch (OperationCanceledException) { }

        var second = Body(await coordinator.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = item.Id, ["goal"] = "second" },
            ct));
        var task = second["task_id"]!.GetValue<string>();
        host.Push(item.Id, AcpStates.Ready, UserEntry("u", host.Prompts.Last().Text), AssistantEntry("a", "second result", streaming: false));

        await WaitUntilAsync(
            () => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task).Status == CollabTaskStatus.Completed,
            ct,
            timeoutMs: 2000,
            message: $"Expected retry after cancelled initial attach to complete (attaches={host.AttachCallCount}, prompts={host.Prompts.Count}).");

        var state = coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task);
        Assert.Equal("second result", state.ResultSummary);
        Assert.Equal(2, host.AttachCallCount);
        Assert.Single(host.Prompts);
    }

    [AvaloniaFact, FreshApp]
    public async Task Removed_timeline_entries_release_dedup_history_while_protecting_pending_deliveries()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coordinator, parent, _, remote, host) = await SetUpFakeRemoteAsync("octopus-bug06-dedup-bound-");
        using var cleanup = coordinator;

        // 1. Removed entries must release dedup IDs instead of growing without bound.
        var session = new RemoteCollabSession(host, "fake", "history", "codex", "History", remote.Path, parent.Id, false, coordinator, _ => { });
        using var close = session;
        await session.StartAsync(ct);
        for (var i = 1; i <= 1000; i++)
        {
            var update = new AcpUpdateMessage
            {
                ItemId = "history",
                Sequence = i,
                State = AcpStates.Ready,
                Upserts = [UserEntry("u" + i, "manual"), AssistantEntry("a" + i, "ordinary", streaming: false)],
            };
            if (i > 1)
            {
                update.Removed.Add("u" + (i - 1));
                update.Removed.Add("a" + (i - 1));
            }
            host.PushRaw(update);
        }
        var seen = (HashSet<string>)typeof(RemoteCollabSession)
            .GetField("_reportedAssistantIds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(session)!;
        Assert.True(seen.Count < 1000, $"All {seen.Count} IDs retained after removals");

        // 2. Append-only history (no removals) also stays bounded while still preventing replay of earlier reported turns onto a pending delivery.
        var existing = new ProjectItem { Id = "history-live", Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "Live History" };
        AppHost.Get<IProjectService>().AddItem(remote, existing, activate: false);
        host.Snapshots[existing.Id] = new AcpSnapshotMessage { ItemId = existing.Id, Sequence = 0, State = AcpStates.Ready };

        var firstRes = Body(await coordinator.CallToolAsync(parent.Id, "delegate_task", new JsonObject { ["target"] = existing.Id, ["goal"] = "Task 1" }, ct));
        var task1 = firstRes["task_id"]!.GetValue<string>();
        var thread1 = firstRes["thread_id"]!.GetValue<string>();
        var prompt1 = host.Prompts[0].Text;
        host.Push(existing.Id, AcpStates.Ready, UserEntry("u-t1", prompt1), AssistantEntry("a-t1", "task 1 answer", streaming: false));
        await WaitUntilAsync(() => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task1).Status == CollabTaskStatus.Completed, ct);

        // Reopen the same thread/task for round 2 (pending delivery waiting for its own answer).
        await coordinator.CallToolAsync(parent.Id, "send_message", new JsonObject { ["target_session_id"] = existing.Id, ["thread_id"] = thread1, ["message"] = "Round 2" }, ct);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task1).Status);

        // Push 300 ordinary turns WITHOUT Removed; old turn a-t1 must never replay onto Round 2.
        for (var i = 1; i <= 300; i++)
        {
            host.Push(existing.Id, AcpStates.Ready, UserEntry("u-ord-" + i, "ordinary " + i), AssistantEntry("a-ord-" + i, "ordinary reply " + i, streaming: false));
        }
        await Task.Delay(80, ct);
        Assert.Equal(CollabTaskStatus.Running, coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task1).Status);

        // Round 2's real answer completes it cleanly.
        host.Push(existing.Id, AcpStates.Ready, UserEntry("u-t2", host.Prompts[1].Text), AssistantEntry("a-t2", "round 2 answer", streaming: false));
        await WaitUntilAsync(() => coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task1).Status == CollabTaskStatus.Completed, ct);
        Assert.Equal("round 2 answer", coordinator.GetTasksForItem(parent.Id).Single(t => t.TaskId == task1).ResultSummary);
    }

    [AvaloniaFact, FreshApp]
    public async Task Concurrent_reuse_of_just_created_child_preserves_launch_answer()
    {
        var ct = TestContext.Current.CancellationToken;
        var (origin, root, _, project, host) = await SetUpFakeRemoteAsync("octopus-bug06-concurrent-attach-");
        using var cleanup = origin;
        var entered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnAttach = async (id, n, token) =>
        {
            var finished = AssistantEntry("a-launch", "launch result", streaming: false);
            finished.Collab = new AcpCollabCorrelationMessage
            {
                DeliveryId = Assert.Single(host.Created).CollabDeliveryId,
                ExpectsReply = true,
            };
            var snapshot = new AcpSnapshotMessage { ItemId = id, Sequence = 1, State = AcpStates.Ready, Entries = [finished] };
            if (n == 1)
            {
                entered.TrySetResult(id);
                await release.Task.WaitAsync(token);
            }
            return snapshot;
        };

        var first = origin.CallToolAsync(
            root.Id,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["goal"] = "first launch", ["project_id"] = project.Id },
            ct);
        var item = await WaitTaskAsync(entered.Task, ct);

        var second = Body(await WaitTaskAsync(
            origin.CallToolAsync(root.Id, "delegate_task", new JsonObject { ["target"] = item, ["goal"] = "second caller reuses mirror" }, ct),
            ct));
        release.TrySetResult();

        var launched = Body(await WaitTaskAsync(first, ct));
        var firstTask = launched["task_id"]!.GetValue<string>();
        var secondTask = second["task_id"]!.GetValue<string>();

        host.PushRaw(new AcpUpdateMessage
        {
            ItemId = item,
            Sequence = 2,
            State = AcpStates.Ready,
            Upserts = [UserEntry("u2", Assert.Single(host.Prompts).Text), AssistantEntry("a2", "second result", streaming: false)],
        });

        await WaitUntilAsync(
            () => origin.GetTasksForItem(root.Id).Single(t => t.TaskId == secondTask).Status == CollabTaskStatus.Completed,
            ct,
            timeoutMs: 2000);
        await WaitUntilAsync(
            () => origin.GetTasksForItem(root.Id).Single(t => t.TaskId == firstTask).Status == CollabTaskStatus.Completed,
            ct,
            timeoutMs: 2000,
            message: $"Expected concurrent reuse during initial attach to preserve launch answer (attaches={host.AttachCallCount}).");

        var original = origin.GetTasksForItem(root.Id).Single(t => t.TaskId == firstTask);
        Assert.Equal("launch result", original.ResultSummary);
        Assert.Equal("second result", origin.GetTasksForItem(root.Id).Single(t => t.TaskId == secondTask).ResultSummary);
        Assert.Equal(1, host.AttachCallCount);
        Assert.Single(host.Created);
        Assert.Single(host.Prompts);
    }

    [AvaloniaTheory, FreshApp]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_attach_failure_with_launch_owner_settles_accepted_followup_without_duplication(bool cancelFirstCaller)
    {
        var ct = TestContext.Current.CancellationToken;
        var (origin, root, _, project, host) = await SetUpFakeRemoteAsync("octopus-bug06-shared-launch-owner-");
        using var cleanup = origin;
        using var firstCaller = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attachEntered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnAttach = async (id, n, token) =>
        {
            if (n == 1)
            {
                attachEntered.TrySetResult(id);
                await release.Task.WaitAsync(token);
                throw new RemoteException(RemoteMethods.NotFound, "Conversation is unavailable");
            }
            return new AcpSnapshotMessage { ItemId = id, Sequence = 0, State = AcpStates.Ready };
        };

        var first = origin.CallToolAsync(
            root.Id,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["goal"] = "launch work", ["project_id"] = project.Id },
            firstCaller.Token);
        var itemId = await WaitTaskAsync(attachEntered.Task, ct);

        var second = Body(await WaitTaskAsync(
            origin.CallToolAsync(root.Id, "delegate_task", new JsonObject { ["target"] = itemId, ["goal"] = "separate accepted delivery" }, ct),
            ct));
        var secondTask = second["task_id"]!.GetValue<string>();
        Assert.Equal(CollabTaskStatus.Running, origin.GetTasksForItem(root.Id).Single(t => t.TaskId == secondTask).Status);
        var accepted = Assert.Single(host.Prompts);

        if (cancelFirstCaller)
        {
            firstCaller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitTaskAsync(first, ct));
            await WaitUntilAsync(() => host.AttachCallCount >= 2, ct);
        }
        else
        {
            release.TrySetResult();
            var firstReport = Body(await WaitTaskAsync(first, ct));
            Assert.Equal("failed", firstReport["status"]!.GetValue<string>());
        }

        var launch = origin.GetTasksForItem(root.Id).Single(t => t.TaskId != secondTask);
        Assert.Equal(CollabTaskStatus.Failed, launch.Status);

        var secondState = () => origin.GetTasksForItem(root.Id).Single(t => t.TaskId == secondTask);
        if (cancelFirstCaller)
        {
            host.Push(itemId, AcpStates.Ready, UserEntry("u-2", accepted.Text), AssistantEntry("a-2", "accepted follow-up answer"));
            await WaitUntilAsync(() => secondState().IsFinished, ct, 2000);
            Assert.Equal(CollabTaskStatus.Completed, secondState().Status);
            Assert.Equal("accepted follow-up answer", secondState().ResultSummary);
        }
        else
        {
            // Must settle visibly without requiring a host disconnect.
            await WaitUntilAsync(() => secondState().IsFinished, ct, 2000,
                $"Expected accepted follow-up to fail visibly after shared attach rejection (status={secondState().Status}).");
            Assert.Equal(CollabTaskStatus.Failed, secondState().Status);
            Assert.Contains("could not be followed", secondState().ResultSummary ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, host.AttachCallCount);
        }

        Assert.Single(host.Created);
        Assert.Single(host.Prompts);
    }

    [AvaloniaTheory, FreshApp]
    [InlineData("snapshot-first")]
    [InlineData("create-first")]
    [InlineData("snapshot-first-legacy")]
    [InlineData("create-first-legacy")]
    [InlineData("reject")]
    [InlineData("cancel-reuse-owner")]
    public async Task Shared_attach_with_reuse_owner_handles_all_orderings_legacy_envelopes_and_rejection(string order)
    {
        var ct = TestContext.Current.CancellationToken;
        var (origin, root, _, project, host) = await SetUpFakeRemoteAsync("octopus-bug06-shared-reuse-owner-");
        using var cleanup = origin;
        using var reuseCaller = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var createdItem = new TaskCompletionSource<ItemMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCreateReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attachEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var structured = !order.EndsWith("-legacy", StringComparison.Ordinal);

        host.BeforeCreateReply = async (item, token) =>
        {
            createdItem.TrySetResult(item);
            await allowCreateReply.Task.WaitAsync(token);
        };
        host.OnAttach = async (id, n, token) =>
        {
            if (n == 1)
            {
                attachEntered.TrySetResult();
                await allowSnapshot.Task.WaitAsync(token);
                if (order == "reject")
                {
                    throw new RemoteException(RemoteMethods.NotFound, "Conversation is unavailable");
                }
            }
            var initial = Assert.Single(host.Created);
            var user = UserEntry("u1", initial.Prompt!);
            var assistant = AssistantEntry("a1", "launch's own result");
            if (structured)
            {
                var correlation = new AcpCollabCorrelationMessage { DeliveryId = initial.CollabDeliveryId, ExpectsReply = true };
                user.Collab = correlation;
                assistant.Collab = correlation;
            }
            return new AcpSnapshotMessage { ItemId = id, Sequence = 1, State = AcpStates.Ready, Entries = [user, assistant] };
        };

        var launch = origin.CallToolAsync(
            root.Id,
            "delegate_task",
            new JsonObject { ["target"] = "codex", ["goal"] = "launch work", ["project_id"] = project.Id },
            ct);
        var remoteItem = await WaitTaskAsync(createdItem.Task, ct);

        // Simulate sidebar sync mirroring the child before CreateItemAsync replies.
        AppHost.Get<IProjectService>().AddItem(
            project,
            new ProjectItem
            {
                Id = remoteItem.Id,
                Kind = ProjectItemKind.Agent,
                AgentId = remoteItem.AgentId,
                Title = remoteItem.Title,
                Surface = AgentSurface.Gui,
            },
            activate: false);

        var reuse = origin.CallToolAsync(
            root.Id,
            "delegate_task",
            new JsonObject { ["target"] = remoteItem.Id, ["goal"] = "reuse work" },
            reuseCaller.Token);
        await WaitTaskAsync(attachEntered.Task, ct);

        if (order is "snapshot-first" or "snapshot-first-legacy")
        {
            allowSnapshot.TrySetResult();
            await WaitTaskAsync(reuse, ct);
            allowCreateReply.TrySetResult();
        }
        else
        {
            allowCreateReply.TrySetResult();
            var launchedNow = Body(await WaitTaskAsync(launch, ct));
            Assert.Equal("running", launchedNow["status"]!.GetValue<string>());
            if (order == "cancel-reuse-owner")
            {
                reuseCaller.Cancel();
            }
            else
            {
                allowSnapshot.TrySetResult();
            }
        }

        var launchReply = Body(await WaitTaskAsync(launch, ct));
        var launchTask = launchReply["task_id"]!.GetValue<string>();
        var launchState = () => origin.GetTasksForItem(root.Id).Single(t => t.TaskId == launchTask);

        if (order == "reject")
        {
            var reuseRaw = await WaitTaskAsync(reuse, ct);
            Assert.True(reuseRaw["isError"]?.GetValue<bool>() == true);
            Assert.Empty(host.Prompts);
            await WaitUntilAsync(() => launchState().IsFinished, ct, 2000,
                $"Expected accepted launch to fail visibly after reuse-owner attach rejection (status={launchState().Status}).");
            Assert.Equal(CollabTaskStatus.Failed, launchState().Status);
            Assert.Equal(1, host.AttachCallCount);
            Assert.Single(host.Created);
            return;
        }

        if (order == "cancel-reuse-owner")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitTaskAsync(reuse, ct));
            Assert.Empty(host.Prompts);
            await WaitUntilAsync(() => host.AttachCallCount >= 2 && launchState().IsFinished, ct, 2000,
                $"Expected launch to recover via reattach after reuse-owner cancellation (status={launchState().Status}, attaches={host.AttachCallCount}).");
            Assert.Equal(CollabTaskStatus.Completed, launchState().Status);
            Assert.Equal("launch's own result", launchState().ResultSummary);
            Assert.Single(host.Created);
            return;
        }

        var reuseReply = Body(await WaitTaskAsync(reuse, ct));
        var reuseTask = reuseReply["task_id"]!.GetValue<string>();
        var reuseState = () => origin.GetTasksForItem(root.Id).Single(t => t.TaskId == reuseTask);
        var reusePrompt = Assert.Single(host.Prompts);
        host.PushRaw(new AcpUpdateMessage
        {
            ItemId = remoteItem.Id,
            Sequence = 2,
            State = AcpStates.Ready,
            Upserts = [UserEntry("u2", reusePrompt.Text), AssistantEntry("a2", "reuse's own result")],
        });

        await WaitUntilAsync(() => reuseState().IsFinished && launchState().IsFinished, ct, 2000,
            $"order={order}: launch={launchState().Status}({launchState().ResultSummary}), reuse={reuseState().Status}({reuseState().ResultSummary})");
        Assert.Equal(CollabTaskStatus.Completed, launchState().Status);
        Assert.Equal("launch's own result", launchState().ResultSummary);
        Assert.Equal(CollabTaskStatus.Completed, reuseState().Status);
        Assert.Equal("reuse's own result", reuseState().ResultSummary);
        Assert.Equal(1, host.AttachCallCount);
        Assert.Single(host.Created);
        Assert.Single(host.Prompts);
    }
}
