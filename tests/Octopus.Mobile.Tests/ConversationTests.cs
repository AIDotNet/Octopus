using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.Mobile.Common;
using Octopus.Mobile.Conversations;
using Octopus.Mobile.Files;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Sessions;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Protocol;
using Octopus.Remote.Tests;
using TKCode.Arc.Mobile;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>
/// Unit and headless UI tests for G-A (ACP GUI Conversation Page): timeline applier, Markdown block parser,
/// demo conversation flows, option pickers, cancellation, and keyboard avoidance.
/// </summary>
public class ConversationTests
{
    private sealed class StubConversation(RemoteSession session) : RemoteConversation(session)
    {
        public override Task SendAsync(string text, IReadOnlyList<string>? attachments = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task RespondAsync(ConversationPermissionEntry card, string optionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task AnswerAsync(ConversationQuestionEntry card, IReadOnlyDictionary<string, IReadOnlyList<string>> answers, IReadOnlyDictionary<string, string> freeText, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task SetOptionAsync(ConversationOptionKind kind, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static StubConversation CreateStub()
    {
        var host = new RemoteHost("h1", "Mac", HostLink.Lan, isPaired: true);
        var project = new RemoteProject(host, "p1", "Octopus", "~/code/Octopus");
        var session = new RemoteSession(project, "s1", SessionKind.Agent, "Codex") { IsGui = true };
        return new StubConversation(session);
    }

    private static (ConversationPage Page, ConversationViewModel ViewModel, RemoteSession Session) OpenDemoConversation(Phone phone)
    {
        phone.Workspace.ConversationStep = TimeSpan.FromMilliseconds(40);
        var station = phone.Workspace.Hosts.Single(host => host.Id == "station");
        var gui = station.Projects[0].Sessions.Single(session => session.IsGui);
        phone.Shell.ShowHost(station);
        phone.Shell.ShowSession(gui);
        Assert.True(Phone.Until(() => phone.Top<ConversationPage>().DataContext is ConversationViewModel { IsLoaded: true }));
        var page = phone.Top<ConversationPage>();
        return (page, (ConversationViewModel)page.DataContext!, gui);
    }

    private static double BottomOf(Control control, Visual root) => control.TranslatePoint(new Point(0, control.Bounds.Height), root)!.Value.Y;

    // ---- ConversationTimeline unit tests ----

    [Fact]
    public void Timeline_applies_snapshot_ordering_after_id_removal_and_preserves_ui_state()
    {
        using var conv = CreateStub();
        var timeline = conv.Timeline;

        var snapshotResult = timeline.ApplySnapshot(new AcpSnapshotMessage
        {
            ItemId = "s1",
            Sequence = 10,
            State = AcpStates.Working,
            StatusDetail = "Running tool",
            AgentName = "Codex",
            HasEarlier = true,
            QueuedCount = 1,
            Entries =
            [
                new() { Id = "u1", Kind = AcpEntryKinds.User, Text = "hello", Attachments = ["a.png"] },
                new()
                {
                    Id = "a1", Kind = AcpEntryKinds.Assistant, Text = "first", Thought = "thinking", IsStreaming = true,
                    Tools = [new() { Id = "t1", Title = "edit", Kind = "edit", Status = "in_progress", Diff = ["+line"] }],
                },
            ],
            Composer = new()
            {
                Models = [new() { Id = "m1", Label = "Model 1" }, new() { Id = "m2", Label = "Model 2" }],
                Model = "m1",
            },
        });

        Assert.Equal(ConversationUpdateResult.Applied, snapshotResult);
        Assert.Equal(10, timeline.Sequence);
        Assert.Equal(ConversationState.Working, conv.State);
        Assert.Equal("Running tool", conv.StatusDetail);
        Assert.True(conv.HasEarlier);
        Assert.Equal(1, conv.QueuedCount);
        Assert.Equal(["u1", "a1"], conv.Entries.Select(e => e.Id));

        // Expand thought & tool on the UI object; subsequent upserts for the same ids must preserve these flags.
        var assistant = Assert.IsType<ConversationAssistantEntry>(conv.Entries[1]);
        assistant.IsThoughtExpanded = true;
        assistant.Tools[0].IsExpanded = true;

        // Insert a permission card after u1 via AfterId, update a1 in-place, and add u2 at the end.
        var updateResult = timeline.Apply(new AcpUpdateMessage
        {
            Sequence = 11,
            Upserts =
            [
                new() { Id = "p1", Kind = AcpEntryKinds.Permission, AfterId = "u1", Permission = new() { Title = "Allow?" } },
                new()
                {
                    Id = "a1", Kind = AcpEntryKinds.Assistant, Text = "updated", Thought = "thinking", IsStreaming = false, Duration = "4s",
                    Tools = [new() { Id = "t1", Title = "edit", Kind = "edit", Status = "completed", Diff = ["+line"] }],
                },
                new() { Id = "u2", Kind = AcpEntryKinds.User, Text = "next", AfterId = "missing-id" },
            ],
        });

        Assert.Equal(ConversationUpdateResult.Applied, updateResult);
        Assert.Equal(["u1", "p1", "a1", "u2"], conv.Entries.Select(e => e.Id));
        Assert.Same(assistant, conv.Entries[2]);
        Assert.True(assistant.IsThoughtExpanded);
        Assert.True(assistant.Tools[0].IsExpanded);
        Assert.Equal("updated", assistant.Text);
        Assert.Equal("completed", assistant.Tools[0].Status);
        // Partial update did not touch State / StatusDetail / QueuedCount.
        Assert.Equal(ConversationState.Working, conv.State);
        Assert.Equal("Running tool", conv.StatusDetail);
        Assert.Equal(1, conv.QueuedCount);

        // Removal + state transition clears stale StatusDetail and updates QueuedCount.
        var removeResult = timeline.Apply(new AcpUpdateMessage
        {
            Sequence = 12,
            Removed = ["p1"],
            State = AcpStates.Ready,
            QueuedCount = 0,
        });

        Assert.Equal(ConversationUpdateResult.Applied, removeResult);
        Assert.Equal(["u1", "a1", "u2"], conv.Entries.Select(e => e.Id));
        Assert.Equal(ConversationState.Ready, conv.State);
        Assert.Null(conv.StatusDetail);
        Assert.Equal(0, conv.QueuedCount);
    }

    [Fact]
    public void Timeline_handles_early_updates_duplicates_and_sequence_gaps()
    {
        using var conv = CreateStub();
        var timeline = conv.Timeline;

        // Arrives before snapshot -> queued.
        Assert.Equal(ConversationUpdateResult.Queued, timeline.Apply(new AcpUpdateMessage
        {
            Sequence = 6,
            Upserts = [new() { Id = "u2", Kind = AcpEntryKinds.User, Text = "early" }],
        }));
        Assert.Empty(conv.Entries);

        // Snapshot at 5 replays queued update 6 automatically.
        Assert.Equal(ConversationUpdateResult.Applied, timeline.ApplySnapshot(new AcpSnapshotMessage
        {
            Sequence = 5,
            State = AcpStates.Ready,
            Entries = [new() { Id = "u1", Kind = AcpEntryKinds.User, Text = "initial" }],
        }));
        Assert.Equal(6, timeline.Sequence);
        Assert.Equal(["u1", "u2"], conv.Entries.Select(e => e.Id));

        // Duplicate / stale sequence -> ignored.
        Assert.Equal(ConversationUpdateResult.Ignored, timeline.Apply(new AcpUpdateMessage { Sequence = 6, State = AcpStates.Error }));
        Assert.Equal(ConversationState.Ready, conv.State);

        // Gap (jumped from 6 to 9) -> resets HasSnapshot and reports Gap.
        Assert.Equal(ConversationUpdateResult.Gap, timeline.Apply(new AcpUpdateMessage { Sequence = 9, State = AcpStates.Working }));
        Assert.False(timeline.HasSnapshot);
        Assert.Equal(ConversationState.Ready, conv.State);
    }

    [Fact]
    public void Markdown_parser_splits_headings_lists_quotes_paragraphs_and_open_code_fences()
    {
        var blocks = ConversationMarkdown.Parse("## Title\n\nHello **world**\n\n- bullet\n1. first\n> quote\n```cs\nvar x = 1;");
        Assert.Collection(blocks,
            b => { Assert.Equal(ConversationTextKind.Heading, b.Kind); Assert.Equal("Title", b.Text); },
            b => { Assert.Equal(ConversationTextKind.Paragraph, b.Kind); Assert.Equal("Hello world", b.Text); },
            b => { Assert.Equal(ConversationTextKind.ListItem, b.Kind); Assert.Equal("•", b.Marker); Assert.Equal("bullet", b.Text); },
            b => { Assert.Equal(ConversationTextKind.ListItem, b.Kind); Assert.Equal("1.", b.Marker); Assert.Equal("first", b.Text); },
            b => { Assert.Equal(ConversationTextKind.Quote, b.Kind); Assert.Equal("quote", b.Text); },
            b => { Assert.Equal(ConversationTextKind.Code, b.Kind); Assert.Equal("var x = 1;", b.Text); });
    }

    // ---- Demo-driven ConversationPage tests ----

    [AvaloniaFact]
    public void Opening_demo_gui_session_shows_timeline_menu_and_expands_thought_and_diff()
    {
        using var phone = new Phone();
        var (page, vm, _) = OpenDemoConversation(phone);

        Assert.Equal("接口文档整理", vm.Title);
        Assert.Equal(ConversationState.Waiting, vm.State);
        Assert.Equal("等你确认", vm.StatusText);
        Assert.True(vm.HasEarlier);
        Assert.True(vm.CanStop);
        Assert.Equal(5, vm.Entries.Count);

        var assistant = vm.Entries.OfType<ConversationAssistantEntry>().First();
        Assert.True(assistant.HasWorkArea);
        Assert.Equal(4, assistant.WorkStepCount); // 1 thought + 3 tools folded into one unified work area
        Assert.False(assistant.IsWorkExpanded);
        Assert.Null(assistant.ExpandedWorkSteps); // collapsed by default -> zero step controls!
        Assert.Null(assistant.LiveTicker);

        // Tap unified work area to lazy-mount pure-text steps, then tap the edit step to lazy-mount its diff.
        vm.ToggleWorkCommand.Execute(assistant);
        Assert.True(assistant.IsWorkExpanded);
        Assert.NotNull(assistant.ExpandedWorkSteps);
        Assert.Equal(4, assistant.ExpandedWorkSteps!.Count);

        var editStep = assistant.ExpandedWorkSteps.Single(step => step.IsTool && step.Tool!.HasDiff);
        Assert.Null(editStep.ExpandedDetail);
        vm.ToggleWorkStepCommand.Execute(editStep);
        Assert.True(editStep.IsExpanded);
        Assert.Same(editStep, editStep.ExpandedDetail);
        Assert.True(editStep.Tool!.IsExpanded);

        // Collapsing the work area releases expanded steps and resets child expansion flags.
        vm.ToggleWorkCommand.Execute(assistant);
        Assert.False(assistant.IsWorkExpanded);
        Assert.Null(assistant.ExpandedWorkSteps);
        Assert.False(editStep.IsExpanded);
        Assert.Null(editStep.ExpandedDetail);

        // "⋯" menu starts with reload conversation + files/changes, and has no restart for GUI sessions.
        var overlays = page.FindControl<SessionActionOverlays>("Overlays")!;
        vm.OpenMenuCommand.Execute(null);
        Phone.Pump();
        Assert.True(overlays.IsMenuOpen);
        Assert.Equal(["重新载入对话", "浏览文件", "查看改动", "重命名", "用相同设置再开一个", "会话详情", "关闭会话"], overlays.MenuLabels);
    }

    [Fact]
    public void Assistant_work_area_shows_3line_sliding_ticker_while_streaming_and_auto_collapses_on_finish()
    {
        using var conv = CreateStub();
        var timeline = conv.Timeline;

        timeline.ApplySnapshot(new AcpSnapshotMessage
        {
            ItemId = "s1",
            Sequence = 1,
            State = AcpStates.Working,
            Entries =
            [
                new()
                {
                    Id = "a1",
                    Kind = AcpEntryKinds.Assistant,
                    Thought = "Checking routes and tests",
                    IsStreaming = true,
                    Tools =
                    [
                        new() { Id = "t1", Title = "Search routes", Kind = "search", Status = "completed" },
                        new() { Id = "t2", Title = "Read api.ts", Kind = "read", Status = "completed" },
                        new() { Id = "t3", Title = "dotnet test", Kind = "execute", Status = "in_progress" },
                        new() { Id = "t4", Title = "bad-cmd", Kind = "execute", Status = "completed", ExitCode = 1 },
                    ],
                },
            ],
        });

        var turn = Assert.IsType<ConversationAssistantEntry>(Assert.Single(conv.Entries));
        Assert.Equal(5, turn.WorkStepCount);
        Assert.True(turn.HasWorkFailures);
        Assert.True(turn.ShowLiveTicker);
        Assert.NotNull(turn.LiveTicker);
        Assert.True(turn.LiveTicker!.Line0.IsVisible);
        Assert.True(turn.LiveTicker.Line1.IsVisible);
        Assert.True(turn.LiveTicker.Line2.IsVisible);
        Assert.True(turn.LiveTicker.Line0.RowOpacity < turn.LiveTicker.Line1.RowOpacity);
        Assert.True(turn.LiveTicker.Line1.RowOpacity < turn.LiveTicker.Line2.RowOpacity);
        Assert.True(turn.LiveTicker.Line2.IsRunning);
        Assert.Equal("dotnet test", turn.LiveTicker.Line2.Target);

        // Manually expand while streaming -> ticker hides, steps materialize.
        turn.IsWorkExpanded = true;
        Assert.False(turn.ShowLiveTicker);
        Assert.Null(turn.LiveTicker);
        Assert.NotNull(turn.ExpandedWorkSteps);

        // Turn finishes -> auto-collapses work area and unmounts ticker & expanded step list!
        timeline.Apply(new AcpUpdateMessage
        {
            Sequence = 2,
            State = AcpStates.Ready,
            Upserts =
            [
                new()
                {
                    Id = "a1",
                    Kind = AcpEntryKinds.Assistant,
                    Thought = "Checking routes and tests",
                    Text = "All done.",
                    IsStreaming = false,
                    Duration = "5s",
                    Tools =
                    [
                        new() { Id = "t1", Title = "Search routes", Kind = "search", Status = "completed" },
                        new() { Id = "t2", Title = "Read api.ts", Kind = "read", Status = "completed" },
                        new() { Id = "t3", Title = "dotnet test", Kind = "execute", Status = "completed" },
                        new() { Id = "t4", Title = "bad-cmd", Kind = "execute", Status = "completed", ExitCode = 1 },
                    ],
                },
            ],
        });

        Assert.False(turn.IsStreaming);
        Assert.False(turn.IsWorkExpanded);
        Assert.Null(turn.ExpandedWorkSteps);
        Assert.False(turn.ShowLiveTicker);
        Assert.Null(turn.LiveTicker);
        Assert.Equal("All done.", turn.Text);
    }

    [AvaloniaFact]
    public void Answering_permission_and_question_cards_updates_the_timeline()
    {
        using var phone = new Phone();
        var (_, vm, session) = OpenDemoConversation(phone);

        var permission = vm.Entries.OfType<ConversationPermissionEntry>().Single();
        Assert.True(permission.IsOpen);
        var allowOnce = permission.Options.Single(option => option.IsAllow);

        vm.RespondCommand.Execute(allowOnce);
        Assert.True(Phone.Until(() => permission.IsResolved && vm.State == ConversationState.Ready));
        Assert.Equal("允许一次", permission.ResolvedSummary);

        // Trigger a question card, select single + multi + free text, and submit.
        phone.Workspace.Conversations[session.Id].Ask();
        Assert.True(Phone.Until(() => vm.State == ConversationState.Waiting && vm.Entries.OfType<ConversationQuestionEntry>().Any()));
        var question = vm.Entries.OfType<ConversationQuestionEntry>().Single();
        vm.ToggleChoiceCommand.Execute(question.Fields[0].Choices[0]);
        vm.ToggleChoiceCommand.Execute(question.Fields[1].Choices[1]);
        question.Fields[1].FreeText = "加鉴权说明";

        vm.SubmitQuestionCommand.Execute(question);
        Assert.True(Phone.Until(() => question.IsResolved && vm.State == ConversationState.Ready));
        Assert.Contains("按模块", question.ResolvedSummary);
        Assert.Contains("错误码表", question.ResolvedSummary);
        Assert.Contains("加鉴权说明", question.ResolvedSummary);
    }

    [AvaloniaFact]
    public void Sending_prompt_queues_while_waiting_and_cancel_stops_the_turn()
    {
        using var phone = new Phone();
        var (_, vm, session) = OpenDemoConversation(phone);

        // While waiting on permission, sending queues the prompt and shows the queued badge.
        vm.PromptText = "顺便检查一下链接";
        Assert.True(vm.CanSend);
        vm.SendCommand.Execute(null);
        Assert.True(Phone.Until(() => vm.QueuedCount == 1));
        Assert.Equal("1 条排队中", vm.QueuedText);
        Assert.Equal("", vm.PromptText);

        // Cancel resolves pending permission, clears queue, and returns to Ready.
        vm.CancelCommand.Execute(null);
        Assert.True(Phone.Until(() => vm.State == ConversationState.Ready && vm.QueuedCount == 0));
        Assert.False(vm.CanStop);

        // Now Ready: sending starts a new turn and produces an assistant reply.
        var countBefore = vm.Entries.Count;
        vm.PromptText = "再加一节鉴权";
        vm.SendCommand.Execute(null);
        Assert.True(Phone.Until(() => vm.Entries.Count >= countBefore + 2 && vm.State == ConversationState.Ready));
        Assert.Contains("再加一节鉴权", phone.Workspace.Conversations[session.Id].Sent);
        var lastAssistant = vm.Entries.OfType<ConversationAssistantEntry>().Last();
        Assert.Contains("再加一节鉴权", lastAssistant.Text);
    }

    [AvaloniaFact]
    public void Choosing_model_mode_and_effort_updates_the_composer()
    {
        using var phone = new Phone();
        var (page, vm, _) = OpenDemoConversation(phone);

        vm.OpenModelPickerCommand.Execute(null);
        Phone.Pump();
        Assert.True(page.IsOptionSheetOpen);
        Assert.Equal(["✓ GPT-6", "GPT-6 mini"], page.OptionSheetLabels);

        vm.SelectOptionCommand.Execute(new ConversationOptionChoice(ConversationOptionKind.Model, "gpt-6-mini"));
        Assert.True(Phone.Until(() => vm.Composer.Model == "gpt-6-mini"));
        Assert.Equal("GPT-6 mini", vm.Composer.ModelLabel);

        vm.SelectOptionCommand.Execute(new ConversationOptionChoice(ConversationOptionKind.Mode, "auto-edit"));
        Assert.True(Phone.Until(() => vm.Composer.Mode == "auto-edit"));
        Assert.Equal("自动编辑", vm.Composer.ModeLabel);

        vm.SelectOptionCommand.Execute(new ConversationOptionChoice(ConversationOptionKind.Effort, "low"));
        Assert.True(Phone.Until(() => vm.Composer.Effort == "low"));
        Assert.Equal("低", vm.Composer.EffortLabel);
    }

    [AvaloniaFact]
    public void The_composer_stays_above_the_keyboard()
    {
        using var phone = new Phone();
        var keyboard = new KeyboardInsetSource();
        TKCode.Arc.Mobile.Page.SetKeyboardInsetSource(phone.Shell, keyboard);
        var (page, _, _) = OpenDemoConversation(phone);
        var composerBar = page.FindControl<Control>("ComposerBar")!;
        var promptField = page.FindControl<Control>("PromptField")!;

        const double inset = 300;
        keyboard.Set(inset, TimeSpan.Zero);
        Phone.Settle(600);

        var top = phone.Window.Height - inset;
        Assert.True(BottomOf(composerBar, phone.Window) <= top + 1,
            $"the composer bar ends at {BottomOf(composerBar, phone.Window)}, the keyboard starts at {top}");
        Assert.True(BottomOf(promptField, phone.Window) <= top, "the prompt field is above the keyboard");
        Assert.True(promptField.IsEffectivelyVisible);
    }

    // ---- Protocol-level end-to-end tests against a real RemoteHostService + FakeHostWorkspace ----

    [AvaloniaFact]
    public async Task Remote_conversation_attaches_streams_prompts_answers_permissions_and_cancels_over_the_protocol()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        var octopus = studio.Workspace.AddProject("Octopus", "/home/ada/code/Octopus");
        studio.Workspace.Files["/home/ada/code/Octopus/README.md"] = System.Text.Encoding.UTF8.GetBytes("# Octopus\n");
        var guiItem = studio.Workspace.AddItem(octopus, "接口文档", "claude", surface: ItemSurfaces.Gui);
        var hostConv = studio.Workspace.ConversationOf(guiItem.Id);

        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        phone.Pair(studio);
        phone.Model<HostsViewModel>().Online.Single().OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostPage));
        var hostVm = phone.Model<HostViewModel>();
        Assert.True(Phone.Until(() => hostVm.Host.Projects.Count == 1 && hostVm.Host.Projects[0].Sessions.Count == 1));
        var gui = hostVm.Host.Projects[0].Sessions.Single();
        Assert.True(gui.IsGui);
        Assert.True(phone.Remote.Workspace.SupportsConversations(hostVm.Host));

        // Opening the GUI session opens ConversationPage and attaches over acp/attach.
        phone.Shell.ShowSession(gui);
        Assert.True(Phone.Until(() => phone.Shell.Top is ConversationPage));
        var vm = phone.Model<ConversationViewModel>();
        Assert.True(Phone.Until(() => vm.IsLoaded && vm.Entries.Count == 3), "initial snapshot loaded: " + vm.ErrorText);
        Assert.Equal(ConversationState.Waiting, vm.State);
        Assert.Equal(1, hostConv.Viewers);

        // Answer the permission card over acp/respond.
        var permission = vm.Entries.OfType<ConversationPermissionEntry>().Single();
        vm.RespondCommand.Execute(permission.Options.Single(o => o.Id == "allow_once"));
        Assert.True(Phone.Until(() => permission.IsResolved && vm.State == ConversationState.Ready));
        Assert.True(hostConv.Responses.TryDequeue(out var response));
        Assert.Equal((guiItem.Id, permission.Id, "allow_once"), (response.ItemId, response.EntryId, response.OptionId));

        // Change model over acp/setOption.
        vm.SelectOptionCommand.Execute(new ConversationOptionChoice(ConversationOptionKind.Model, "sonnet-5"));
        Assert.True(Phone.Until(() => vm.Composer.Model == "sonnet-5"));
        Assert.True(hostConv.OptionChanges.TryDequeue(out var opt));
        Assert.Equal((AcpOptionKinds.Model, "sonnet-5"), (opt.Kind, opt.Value));

        // Send a prompt over acp/prompt and observe streamed updates over acp/update.
        vm.PromptText = "补上错误码";
        vm.SendCommand.Execute(null);
        Assert.True(Phone.Until(() => vm.Entries.Count == 5 && vm.State == ConversationState.Working));
        Assert.True(hostConv.Prompts.TryDequeue(out var prompt));
        Assert.Equal("补上错误码", prompt.Text);
        Assert.Contains(vm.Entries.OfType<ConversationAssistantEntry>(), a => a.Text.Contains("收到：补上错误码"));

        // Cancel over acp/cancel.
        vm.CancelCommand.Execute(null);
        Assert.True(Phone.Until(() => vm.State == ConversationState.Ready && hostConv.CancelCount == 1));

        // The conversation "⋯" menu can also jump straight to project files.
        var page = phone.Top<ConversationPage>();
        var overlays = page.FindControl<SessionActionOverlays>("Overlays")!;
        vm.OpenMenuCommand.Execute(null);
        Phone.Pump();
        Assert.Contains("浏览文件", overlays.MenuLabels);
        Assert.Contains("查看改动", overlays.MenuLabels);
        phone.Shell.ShowFiles(gui.Project, RemoteFolders.WorkspacePathOf(gui));
        Assert.True(Phone.Until(() => phone.Shell.Top is FilesPage));
        var filesVm = phone.Model<FilesViewModel>();
        Assert.True(Phone.Until(() => !filesVm.IsLoading && filesVm.Entries.Any(e => e.Name == "README.md")));
        phone.Shell.Back();
        Assert.True(Phone.Until(() => phone.Shell.Top is ConversationPage));

        // Leaving the conversation page detaches on the host.
        phone.Shell.Back();
        Assert.True(Phone.Until(() => phone.Shell.Top is HostPage));
        Assert.True(Phone.Until(() => hostConv.Viewers == 0), "host attachment disposed on detach");
    }

    [AvaloniaFact]
    public void Entering_and_returning_to_mobile_conversation_keeps_scroll_at_bottom()
    {
        using var phone = new Phone();
        var (page, vm, session) = OpenDemoConversation(phone);

        // Resolve the initial demo permission card so prompts execute onto the timeline instead of queueing.
        var permission = vm.Entries.OfType<ConversationPermissionEntry>().Single();
        vm.RespondCommand.Execute(permission.Options.Single(option => option.IsAllow));
        Assert.True(Phone.Until(() => vm.State == ConversationState.Ready));

        // Append enough multi-line messages so the mobile conversation overflows the viewport.
        for (var i = 1; i <= 8; i++)
        {
            vm.PromptText = $"第 {i} 轮排查：请检查滚动条状态并输出详细结论。\n细节 A：确认重新进入会话时滚动条自动位于底部。\n细节 B：确认返回页面时也能保持底部。";
            var before = vm.Entries.Count;
            vm.SendCommand.Execute(null);
            Assert.True(Phone.Until(() => !vm.IsSending && vm.Entries.Count > before));
            if (vm.CanStop)
            {
                vm.CancelCommand.Execute(null);
                Assert.True(Phone.Until(() => vm.State == ConversationState.Ready));
            }
            Phone.Pump();
        }

        var scroller = page.FindControl<ScrollViewer>("TimelineScroll")!;
        Assert.True(Phone.Until(() => scroller.ScrollBarMaximum.Y > 200),
            $"Expected overflowing mobile timeline, max={scroller.ScrollBarMaximum.Y}");
        Phone.Pump();
        Assert.True(scroller.ScrollBarMaximum.Y - scroller.Offset.Y <= 2.0,
            $"Expected initial/sent timeline at bottom, offset={scroller.Offset.Y}, max={scroller.ScrollBarMaximum.Y}");

        // 1) Scroll up into history, then send a new prompt: must force-scroll back to the very bottom.
        scroller.Offset = new Avalonia.Vector(0, 40);
        Phone.Pump();
        Assert.True(scroller.ScrollBarMaximum.Y - scroller.Offset.Y > 100);

        vm.PromptText = "回到最底部的新消息";
        var beforeSend = vm.Entries.Count;
        vm.SendCommand.Execute(null);
        Assert.True(Phone.Until(() => !vm.IsSending && vm.Entries.Count > beforeSend));
        if (vm.CanStop)
        {
            vm.CancelCommand.Execute(null);
            Assert.True(Phone.Until(() => vm.State == ConversationState.Ready));
        }
        Assert.True(Phone.Until(() => scroller.ScrollBarMaximum.Y - scroller.Offset.Y <= 2.0),
            $"Sending while scrolled up must scroll mobile GUI to bottom, offset={scroller.Offset.Y}, max={scroller.ScrollBarMaximum.Y}");

        // 2) Scroll up into history, push another page (Files), and pop back: must return to the very bottom.
        scroller.Offset = new Avalonia.Vector(0, 40);
        Phone.Pump();
        Assert.True(scroller.ScrollBarMaximum.Y - scroller.Offset.Y > 100);

        phone.Shell.ShowFiles(session.Project, RemoteFolders.WorkspacePathOf(session));
        Assert.True(Phone.Until(() => phone.Shell.Top is FilesPage));
        phone.Shell.Back();
        Assert.True(Phone.Until(() => phone.Shell.Top is ConversationPage));
        Assert.True(Phone.Until(() => scroller.ScrollBarMaximum.Y - scroller.Offset.Y <= 2.0),
            $"Returning from sub-page must scroll mobile GUI to bottom, offset={scroller.Offset.Y}, max={scroller.ScrollBarMaximum.Y}");

        // 3) Leave conversation completely and re-enter it: must land at the very bottom.
        scroller.Offset = new Avalonia.Vector(0, 40);
        Phone.Pump();
        phone.Shell.Back();
        Assert.True(Phone.Until(() => phone.Shell.Top is HostPage));

        phone.Shell.ShowSession(session);
        Assert.True(Phone.Until(() => phone.Shell.Top is ConversationPage));
        var reopenedPage = phone.Top<ConversationPage>();
        Assert.True(Phone.Until(() => reopenedPage.DataContext is ConversationViewModel { IsLoaded: true }));
        var reopenedScroller = reopenedPage.FindControl<ScrollViewer>("TimelineScroll")!;
        Assert.True(Phone.Until(() => reopenedScroller.ScrollBarMaximum.Y > 0 && reopenedScroller.ScrollBarMaximum.Y - reopenedScroller.Offset.Y <= 2.0),
            $"Re-entering mobile GUI must scroll to bottom, offset={reopenedScroller.Offset.Y}, max={reopenedScroller.ScrollBarMaximum.Y}");
    }

    [AvaloniaFact]
    public async Task Older_host_without_acp_gui_capability_opens_session_details_with_notice()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay, advertiseAcpGui: false);
        var octopus = studio.Workspace.AddProject("Octopus", "/home/ada/code/Octopus");
        studio.Workspace.AddItem(octopus, "接口文档", "claude", surface: ItemSurfaces.Gui);

        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        phone.Pair(studio);
        phone.Model<HostsViewModel>().Online.Single().OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostPage));
        var hostVm = phone.Model<HostViewModel>();
        Assert.True(Phone.Until(() => hostVm.Host.Projects.Count == 1 && hostVm.Host.Projects[0].Sessions.Count == 1));
        var gui = hostVm.Host.Projects[0].Sessions.Single();
        Assert.False(phone.Remote.Workspace.SupportsConversations(hostVm.Host));

        phone.Shell.ShowSession(gui);
        Assert.True(Phone.Until(() => phone.Shell.Top is SessionDetailPage));
        var page = phone.Top<SessionDetailPage>();
        var details = phone.Model<SessionDetailViewModel>();
        Assert.False(details.CanOpenConversation);
        Assert.True(details.ShowGuiNotice);
        Assert.True(page.FindControl<Control>("GuiNotice")!.IsVisible);
        Assert.False(page.FindControl<Control>("OpenConversationRow")!.IsVisible);
    }
}
