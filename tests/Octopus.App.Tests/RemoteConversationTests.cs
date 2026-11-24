using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.Acp.Protocol;
using Octopus.Agents;
using Octopus.App.Activity;
using Octopus.App.AgentGui;
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
/// A GUI conversation of this app shown on a device and driven from it (design/ui/移动端功能扩展方案.html ④, G-A): the
/// real tab view model with a made-up ACP session on the host side, the real client on the device side, joined in memory.
/// </summary>
public class RemoteConversationTests
{
    private sealed class HostSide : IAsyncDisposable
    {
        public DeviceIdentity Identity { get; } = DeviceIdentity.Create();
        public PairedPeerStore Devices { get; } = new(null);
        public HostWorkspace Workspace { get; }
        public RemoteHostService Service { get; }
        public IProjectService Projects { get; } = AppHost.Get<IProjectService>();
        public ITabContentRegistry Tabs { get; } = AppHost.Get<ITabContentRegistry>();

        public HostSide()
        {
            Workspace = new HostWorkspace(Projects, AppHost.Get<IItemLaunchService>(), AppHost.Get<ITerminalConnectionFactory>(), AppHost.Get<TerminalSessionRegistry>(), Tabs,
                AppHost.Get<IItemActivityService>(), AppHost.Get<IAgentCatalog>(), AppHost.Get<IAgentScanner>(), AppHost.Get<ISettingsStore>(), AppHost.Get<IShellResolver>(),
                AppHost.Get<Octopus.FileSystem.IFileTreeService>(), AppHost.Get<Octopus.Git.IGitService>(), AppHost.Get<Octopus.App.Common.PathComparer>());
            Service = new RemoteHostService(Identity, () => new DeviceDescription("Studio", DeviceKind.Desktop), Devices, Workspace, Hello);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            Workspace.Dispose();
            Identity.Dispose();
        }
    }

    private static readonly HelloMessage Hello = new()
    {
        AppVersion = "test",
        Capabilities = [RemoteMethods.CapabilityWorkspace, RemoteMethods.CapabilityAcpGui],
        TerminalBuild = "build-1",
    };

    private static readonly DeviceDescription Phone = new("Phone", DeviceKind.Phone);

    /// <summary>What the device knows of one conversation: the snapshot with every update applied, as a device would.</summary>
    private sealed class Mirror
    {
        private readonly object _gate = new();
        private readonly List<string> _order = [];
        private readonly Dictionary<string, AcpEntryMessage> _entries = [];
        private readonly List<AcpUpdateMessage> _updates = [];
        private string _state = "";
        private AcpComposerMessage _composer = new();

        public Mirror(RemoteHostClient client, string itemId)
        {
            client.AcpUpdated += update =>
            {
                if (update.ItemId != itemId) return;
                lock (_gate)
                {
                    _updates.Add(update);
                    if (Snapshot is not null) Apply(update);
                }
            };
        }

        public AcpSnapshotMessage? Snapshot { get; private set; }

        public void Begin(AcpSnapshotMessage snapshot)
        {
            lock (_gate)
            {
                Snapshot = snapshot;
                _order.Clear();
                _entries.Clear();
                foreach (var entry in snapshot.Entries)
                {
                    _order.Add(entry.Id);
                    _entries[entry.Id] = entry;
                }
                _state = snapshot.State;
                _composer = snapshot.Composer;
                // Updates that overtook the answer to the attach.
                foreach (var update in _updates.Where(u => u.Sequence > snapshot.Sequence)) Apply(update);
            }
        }

        private void Apply(AcpUpdateMessage update)
        {
            foreach (var id in update.Removed)
            {
                _order.Remove(id);
                _entries.Remove(id);
            }
            foreach (var entry in update.Upserts)
            {
                if (!_entries.ContainsKey(entry.Id))
                {
                    var at = entry.AfterId is { } after && _order.IndexOf(after) is var index and >= 0 ? index + 1 : _order.Count;
                    _order.Insert(at, entry.Id);
                }
                _entries[entry.Id] = entry;
            }
            if (update.State is { } state) _state = state;
            if (update.Composer is { } composer) _composer = composer;
        }

        public string State { get { lock (_gate) return _state; } }
        public AcpComposerMessage Composer { get { lock (_gate) return _composer; } }
        public List<AcpEntryMessage> Entries { get { lock (_gate) return [.. _order.Select(id => _entries[id])]; } }
        public List<AcpUpdateMessage> Updates { get { lock (_gate) return [.. _updates]; } }
        public AcpEntryMessage? Last(string kind) => Entries.LastOrDefault(e => e.Kind == kind);
    }

    private static async Task<RemoteHostClient> Connect(HostSide host, DeviceIdentity device)
    {
        var (a, b) = InMemoryTransport.CreatePair();
        _ = host.Service.ServeAsync(b);
        return await Wait(RemoteHostClient.ConnectAsync(a, device, Phone, PeerExpectation.Paired(host.Identity.PublicKey), Hello));
    }

    /// <summary>Waits while the UI thread keeps running what the host posts to it (the test itself is on that thread).</summary>
    private static async Task<T> Wait<T>(Task<T> task, int milliseconds = 8000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!task.IsCompleted && Environment.TickCount64 < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(task.IsCompleted, "The operation did not finish in time.");
        return await task;
    }

    private static async Task Wait(Task task, int milliseconds = 8000) => await Wait(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }), milliseconds);

    private static async Task Until(Func<bool> done, string what, int milliseconds = 8000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!done() && Environment.TickCount64 < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(done(), what);
    }

    private static async Task Pump(int milliseconds)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    private static ProjectItem AddGuiAgent(HostSide host, Project project)
    {
        var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "接口文档", Surface = AgentSurface.Gui };
        host.Projects.AddItem(project, item, activate: false);
        return item;
    }

    private static AgentGuiTabViewModel ModelOf(HostSide host, Project project, ProjectItem item) =>
        Assert.IsType<AgentGuiTabViewModel>(host.Tabs.GetOrCreate(project, item).DataContext);

    private static void OfferModels(FakeAcpSession session) => session.Models = new AcpSessionModelState
    {
        CurrentModelId = "claude-sonnet-4",
        AvailableModels =
        [
            new AcpModelInfo { ModelId = "claude-sonnet-4", Name = "Claude Sonnet 4" },
            new AcpModelInfo { ModelId = "gpt-5", Name = "GPT-5" },
        ],
    };

    [AvaloniaFact]
    public async Task A_gui_conversation_is_shown_on_a_device_and_a_prompt_from_it_streams_back_in_order()
    {
        var fakes = AppHost.Get<FakeAcpSessionFactory>();
        var rest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fakes.ConfigureNext = session =>
        {
            OfferModels(session);
            session.PromptHandler = async (s, prompt, _, _) =>
            {
                if (prompt != "Say hello") return new AcpPromptResult { StopReason = "end_turn" };
                s.Emit(new AcpTextDeltaEvent("reasoning", "Greeting the person", null));
                s.Emit(new AcpTextDeltaEvent("assistant", "Hel", null));
                await rest.Task;
                s.Emit(new AcpTextDeltaEvent("assistant", "lo", null));
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate
                {
                    ToolCallId = "tc-1",
                    Kind = "execute",
                    Title = "Run echo",
                    Command = "echo hi",
                    Status = "completed",
                    Stdout = new string('x', 10_000) + "tail",
                }));
                return new AcpPromptResult { StopReason = "end_turn" };
            };
        };
        await using var host = new HostSide();
        using var device = DeviceIdentity.Create();
        host.Devices.Add(device.PublicKey, Phone);
        var project = host.Projects.AddProject(Directory.CreateTempSubdirectory("octopus-acp-remote-").FullName);
        var item = AddGuiAgent(host, project);
        try
        {
            await using var client = await Connect(host, device);
            Assert.True(client.SupportsAcpGui);
            var mirror = new Mirror(client, item.Id);

            var snapshot = await Wait(client.AttachConversationAsync(item.Id));
            mirror.Begin(snapshot);
            Assert.Equal(item.Id, snapshot.ItemId);
            Assert.Empty(snapshot.Entries);
            Assert.False(snapshot.HasEarlier);
            var model = ModelOf(host, project, item);
            Assert.Equal(model.AgentName, snapshot.AgentName);
            await Until(() => mirror.State == AcpStates.Ready, "the conversation becomes ready on the device");
            await Until(() => mirror.Composer.Models.Count == 2 && mirror.Composer.Model == "claude-sonnet-4", "the device sees the models above the input");

            // The person at the computer is typing something of their own meanwhile.
            model.InputText = "my own draft";
            await Wait(client.SendPromptAsync(item.Id, "Say hello"));

            await Until(() => mirror.Last(AcpEntryKinds.Assistant) is { Text: "Hel", IsStreaming: true }, "the answer streams to the device");
            Assert.Equal(AcpStates.Working, mirror.State);
            var entries = mirror.Entries;
            Assert.Equal(AcpEntryKinds.User, entries[^2].Kind);
            Assert.Equal("Say hello", entries[^2].Text);
            Assert.Equal("Greeting the person", entries[^1].Thought);
            Assert.Equal("my own draft", model.InputText);
            Assert.Equal("Say hello", Assert.IsType<UserTurnItem>(model.Timeline[0]).Text);

            rest.SetResult();
            await Until(() => mirror.Last(AcpEntryKinds.Assistant) is { Text: "Hello", IsStreaming: false } && mirror.State == AcpStates.Ready, "the turn ends on the device");
            var tool = Assert.Single(mirror.Last(AcpEntryKinds.Assistant)!.Tools!);
            Assert.Equal("echo hi", tool.Command);
            Assert.Equal(AcpLimits.ToolOutputChars, tool.Output!.Length);
            Assert.EndsWith("tail", tool.Output);

            // One number after the other, starting right after the snapshot's.
            var numbers = mirror.Updates.Select(u => u.Sequence).ToList();
            // Changes are coalesced every 100 ms, so how many updates arrive depends on timing. The consecutive
            // sequence numbers and the streaming assertions above are what prove ordered delivery.
            Assert.NotEmpty(numbers);
            Assert.Equal(Enumerable.Range(1, numbers.Count).Select(n => snapshot.Sequence + n), numbers);

            // A choice above the input made on the device is the tab's choice.
            await Wait(client.SetConversationOptionAsync(item.Id, AcpOptionKinds.Model, "gpt-5"));
            Assert.Equal("gpt-5", model.Composer.SelectedModelId);
            Assert.Contains("gpt-5", fakes.Created[^1].SetModels);
            await Until(() => mirror.Composer.Model == "gpt-5", "the device hears of the new model");
            var refused = await Assert.ThrowsAsync<RemoteException>(() => Wait(client.SetConversationOptionAsync(item.Id, AcpOptionKinds.Model, "nope")));
            Assert.Equal(RemoteProtocol.InvalidParams, refused.Code);

            // Attaching again starts over from a fresh snapshot with what is there now, without rewinding Sequence.
            var lastBeforeReattach = mirror.Updates[^1].Sequence;
            var again = await Wait(client.AttachConversationAsync(item.Id));
            Assert.True(again.Sequence >= lastBeforeReattach);
            Assert.Equal(mirror.Entries.Select(e => e.Id), again.Entries.Select(e => e.Id));
            Assert.Equal("Hello", again.Entries[^1].Text);
            mirror.Begin(again);
            await Wait(client.SendPromptAsync(item.Id, "Follow up"));
            await Until(() => mirror.Entries.Any(e => e.Kind == AcpEntryKinds.User && e.Text == "Follow up") && mirror.State == AcpStates.Ready, "updates after re-attach continue monotonically");
            var afterReattach = mirror.Updates.Where(u => u.Sequence > again.Sequence).Select(u => u.Sequence).ToList();
            Assert.NotEmpty(afterReattach);
            Assert.Equal(Enumerable.Range(1, afterReattach.Count).Select(n => again.Sequence + n), afterReattach);

            // Detached: the conversation goes on at the computer, the device hears nothing of it.
            await Wait(client.DetachConversationAsync(item.Id));
            var heard = mirror.Updates.Count;
            var timelineBefore = model.Timeline.Count;
            model.InputText = "From the computer";
            await model.SendPromptAsync();
            await Until(() => model.Timeline.Count == timelineBefore + 2 && model.State == AgentGuiState.Ready, "the computer's own turn ends");
            await Pump(400);
            Assert.Equal(heard, mirror.Updates.Count);
        }
        finally
        {
            fakes.ConfigureNext = null;
            await host.Tabs.ReleaseAsync(item.Id);
        }
    }

    [AvaloniaFact]
    public async Task Cards_answered_on_a_device_close_on_the_computer_and_a_turn_is_stopped_from_it()
    {
        var fakes = AppHost.Get<FakeAcpSessionFactory>();
        string? allowed = null;
        IReadOnlyDictionary<string, IReadOnlyList<string>>? answered = null;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fakes.ConfigureNext = session =>
        {
            session.CancelHandler = _ =>
            {
                cancelled.TrySetResult();
                return Task.CompletedTask;
            };
            session.PromptHandler = async (s, _, _, ct) =>
            {
                allowed = await s.Callbacks.OnPermissionRequest!(new AcpPermissionRequest
                {
                    RequestId = "p1",
                    SessionId = s.SessionId!,
                    ToolCall = new AcpToolCallUpdate { ToolCallId = "tc-run", Kind = "execute", Title = "Run dotnet test", Command = "dotnet test" },
                    Options =
                    [
                        new AcpPermissionOption { OptionId = "allow", Name = "Allow once", Kind = "allow_once" },
                        new AcpPermissionOption { OptionId = "deny", Name = "Reject", Kind = "reject_once" },
                    ],
                }, ct);
                answered = await s.Callbacks.OnQuestionRequest!(new AcpQuestionRequest
                {
                    RequestId = "q1",
                    Title = "Choose design",
                    Questions =
                    [
                        new AcpQuestionItem
                        {
                            Id = "style",
                            Prompt = "Pick GUI style",
                            Options = [new AcpQuestionOption { Id = "A", Label = "Style A" }, new AcpQuestionOption { Id = "B", Label = "Style B" }],
                        },
                    ],
                }, ct);
                s.Emit(new AcpTextDeltaEvent("assistant", "Working on it", null));
                await cancelled.Task;
                return new AcpPromptResult { StopReason = "cancelled" };
            };
        };
        await using var host = new HostSide();
        using var device = DeviceIdentity.Create();
        host.Devices.Add(device.PublicKey, Phone);
        var project = host.Projects.AddProject(Directory.CreateTempSubdirectory("octopus-acp-remote-").FullName);
        var item = AddGuiAgent(host, project);
        try
        {
            await using var client = await Connect(host, device);
            var mirror = new Mirror(client, item.Id);
            mirror.Begin(await Wait(client.AttachConversationAsync(item.Id)));
            await Until(() => mirror.State == AcpStates.Ready, "the conversation becomes ready on the device");
            var model = ModelOf(host, project, item);

            await Wait(client.SendPromptAsync(item.Id, "Run the tests"));
            await Until(() => mirror.Last(AcpEntryKinds.Permission) is { Permission.IsResolved: false } && mirror.State == AcpStates.Waiting, "the permission card reaches the device");
            var card = mirror.Last(AcpEntryKinds.Permission)!;
            Assert.Equal("Run dotnet test", card.Permission!.Title);
            Assert.Equal("dotnet test", card.Permission.Detail);
            Assert.Equal(["allow_once", "reject_once"], card.Permission.Options.Select(o => o.Detail));

            var unknown = await Assert.ThrowsAsync<RemoteException>(() => Wait(client.RespondAsync(new AcpRespondMessage { ItemId = item.Id, EntryId = "nope", OptionId = "allow" })));
            Assert.Equal(RemoteMethods.NotFound, unknown.Code);
            await Wait(client.RespondAsync(new AcpRespondMessage { ItemId = item.Id, EntryId = card.Id, OptionId = "allow" }));
            Assert.Equal("allow", allowed);
            // The computer's card closes with the device's answer, and leaves the timeline as it does when clicked.
            await Until(() => !model.Timeline.OfType<PermissionCardItem>().Any(), "the card leaves the computer's timeline");
            await Until(() => mirror.Entries.All(e => e.Id != card.Id), "the card leaves the device's copy");

            await Until(() => mirror.Last(AcpEntryKinds.Question) is { Question.IsResolved: false }, "the question reaches the device");
            var question = mirror.Last(AcpEntryKinds.Question)!;
            Assert.Equal(["A", "B"], question.Question!.Fields.Single().Choices.Select(c => c.Id));
            await Wait(client.RespondAsync(new AcpRespondMessage
            {
                ItemId = item.Id,
                EntryId = question.Id,
                Answers = new() { ["style"] = ["B"] },
            }));
            Assert.Equal(["B"], answered!["style"]);
            Assert.True(model.Timeline.OfType<QuestionCardItem>().Single().IsResolved);
            await Until(() => mirror.Last(AcpEntryKinds.Question) is { Question.IsResolved: true } && mirror.State == AcpStates.Working, "the answered question shows as such on the device");
            // A second answer to a card answered already changes nothing.
            await Wait(client.RespondAsync(new AcpRespondMessage { ItemId = item.Id, EntryId = question.Id, Answers = new() { ["style"] = ["A"] } }));
            Assert.Equal(["B"], answered!["style"]);

            await Until(() => mirror.Last(AcpEntryKinds.Assistant) is { Text: "Working on it" }, "the answer streams on");
            await Wait(client.CancelTurnAsync(item.Id));
            await Until(() => mirror.State == AcpStates.Ready && mirror.Last(AcpEntryKinds.Assistant) is { IsStreaming: false }, "the stopped turn ends on the device");
            Assert.Equal(1, fakes.Created[^1].CancelCount);
            Assert.Equal(AgentGuiState.Ready, model.State);
        }
        finally
        {
            fakes.ConfigureNext = null;
            await host.Tabs.ReleaseAsync(item.Id);
        }
    }

    [AvaloniaFact]
    public async Task Only_gui_conversations_can_be_attached_and_unknown_items_are_not_found()
    {
        await using var host = new HostSide();
        using var device = DeviceIdentity.Create();
        host.Devices.Add(device.PublicKey, Phone);
        var project = host.Projects.AddProject(Directory.CreateTempSubdirectory("octopus-acp-remote-").FullName);
        var cli = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "重构" };
        var shell = new ProjectItem { Kind = ProjectItemKind.Terminal, Title = "zsh" };
        host.Projects.AddItem(project, cli, activate: false);
        host.Projects.AddItem(project, shell, activate: false);
        await using var client = await Connect(host, device);

        foreach (var id in new[] { cli.Id, shell.Id })
        {
            Assert.Equal(RemoteMethods.NotAllowed, (await Assert.ThrowsAsync<RemoteException>(() => Wait(client.AttachConversationAsync(id)))).Code);
            Assert.Equal(RemoteMethods.NotAllowed, (await Assert.ThrowsAsync<RemoteException>(() => Wait(client.SendPromptAsync(id, "hi")))).Code);
            Assert.Equal(RemoteMethods.NotAllowed, (await Assert.ThrowsAsync<RemoteException>(() => Wait(client.CancelTurnAsync(id)))).Code);
        }
        // Nothing was started for them: a GUI tab would have started the agent.
        Assert.Null(host.Tabs.Find(cli.Id));
        Assert.Equal(RemoteMethods.NotFound, (await Assert.ThrowsAsync<RemoteException>(() => Wait(client.AttachConversationAsync("nope")))).Code);
        Assert.Equal(RemoteMethods.NotFound, (await Assert.ThrowsAsync<RemoteException>(() => Wait(client.RespondAsync(new AcpRespondMessage { ItemId = "nope", EntryId = "e1", OptionId = "x" })))).Code);
        Assert.Equal(RemoteMethods.NotFound, (await Assert.ThrowsAsync<RemoteException>(() => Wait(client.SetConversationOptionAsync("nope", AcpOptionKinds.Model, "x")))).Code);
        // Detaching what is not attached is harmless.
        await Wait(client.DetachConversationAsync(cli.Id));
    }

    [AvaloniaFact]
    public async Task Prompt_attachments_are_validated_against_the_project_and_free_text_questions_clear_default_choices()
    {
        var fakes = AppHost.Get<FakeAcpSessionFactory>();
        IReadOnlyList<AcpPromptAttachment>? receivedAttachments = null;
        IReadOnlyDictionary<string, IReadOnlyList<string>>? answered = null;
        fakes.ConfigureNext = session =>
        {
            session.PromptHandler = async (s, _, attachments, ct) =>
            {
                receivedAttachments = attachments;
                answered = await s.Callbacks.OnQuestionRequest!(new AcpQuestionRequest
                {
                    RequestId = "q-free",
                    Title = "Pick or type",
                    Questions =
                    [
                        new AcpQuestionItem
                        {
                            Id = "approach",
                            Prompt = "How should we proceed?",
                            AllowFreeText = true,
                            Options = [new AcpQuestionOption { Id = "default", Label = "Default" }, new AcpQuestionOption { Id = "alt", Label = "Alternative" }],
                        },
                    ],
                }, ct);
                return new AcpPromptResult { StopReason = "end_turn" };
            };
        };
        await using var host = new HostSide();
        using var device = DeviceIdentity.Create();
        host.Devices.Add(device.PublicKey, Phone);
        var projectDir = Directory.CreateTempSubdirectory("octopus-acp-remote-").FullName;
        var outsideDir = Directory.CreateTempSubdirectory("octopus-acp-outside-").FullName;
        var insideFile = Path.Combine(projectDir, "notes.txt");
        var outsideFile = Path.Combine(outsideDir, "secret.txt");
        await File.WriteAllTextAsync(insideFile, "hello");
        await File.WriteAllTextAsync(outsideFile, "secret");
        var project = host.Projects.AddProject(projectDir);
        var item = AddGuiAgent(host, project);
        try
        {
            await using var client = await Connect(host, device);
            var mirror = new Mirror(client, item.Id);
            mirror.Begin(await Wait(client.AttachConversationAsync(item.Id)));
            await Until(() => mirror.State == AcpStates.Ready, "the conversation becomes ready");

            // Files outside the project are refused; missing files inside it are not found.
            Assert.Equal(RemoteMethods.NotAllowed, (await Assert.ThrowsAsync<RemoteException>(() => Wait(client.SendPromptAsync(item.Id, "read", [outsideFile])))).Code);
            Assert.Equal(RemoteMethods.NotFound, (await Assert.ThrowsAsync<RemoteException>(() => Wait(client.SendPromptAsync(item.Id, "read", [Path.Combine(projectDir, "missing.txt")])))).Code);

            // A file inside the project goes through and reaches both the session and the device's timeline.
            await Wait(client.SendPromptAsync(item.Id, "Check this", [insideFile]));
            await Until(() => mirror.Last(AcpEntryKinds.Question) is { Question.IsResolved: false }, "the free-text question reaches the device");
            var userEntry = mirror.Last(AcpEntryKinds.User)!;
            Assert.Equal(["notes.txt"], userEntry.Attachments);
            Assert.Single(receivedAttachments!);
            Assert.Equal(insideFile, receivedAttachments![0].Path);

            // Answering with free text alone does not also submit the constructor's default first choice.
            var question = mirror.Last(AcpEntryKinds.Question)!;
            await Wait(client.RespondAsync(new AcpRespondMessage
            {
                ItemId = item.Id,
                EntryId = question.Id,
                FreeText = new() { ["approach"] = "Custom plan" },
            }));
            await Until(() => mirror.State == AcpStates.Ready, "the turn ends after free-text answer");
            Assert.Equal(["Custom plan"], answered!["approach"]);
        }
        finally
        {
            fakes.ConfigureNext = null;
            await host.Tabs.ReleaseAsync(item.Id);
        }
    }

    [Fact]
    public void Long_texts_keep_their_end_and_are_marked_as_cut()
    {
        Assert.Equal("short", HostConversations.Tail("short", 10));
        var cut = HostConversations.Tail("0123456789", 5)!;
        Assert.Equal("…6789", cut);
        // Never half of a character that takes two.
        var emoji = HostConversations.Tail("ab\U0001F600cd", 4)!;
        Assert.False(char.IsLowSurrogate(emoji[1]));
    }
}
