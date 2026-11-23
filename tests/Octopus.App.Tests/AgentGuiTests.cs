using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.Acp.Dialects;
using Octopus.Acp.Process;
using Octopus.Acp.Protocol;
using Octopus.Agents;
using Octopus.Agents.Sessions;
using Octopus.App.Activity;
using Octopus.App.AgentGui;
using Octopus.App.Composition;
using Octopus.App.Launcher;
using Octopus.App.Services;
using Octopus.App.Settings.Agents;
using Octopus.App.Terminals;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Platform;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>Deterministic in-memory ACP session for headless GUI tests (never spawns real CLIs).</summary>
public sealed class FakeAcpSession : IAcpSession
{
    public FakeAcpSession(AcpLaunchPlan plan, IAcpDialect dialect, AcpSessionCallbacks callbacks)
    {
        Plan = plan;
        Dialect = dialect;
        Callbacks = callbacks;
    }

    public AcpLaunchPlan Plan { get; }
    public AcpSessionCallbacks Callbacks { get; }
    public List<(string Text, IReadOnlyList<AcpPromptAttachment>? Attachments)> SentPrompts { get; } = [];
    public List<string> SetModes { get; } = [];
    public List<string> SetModels { get; } = [];
    public List<(string Id, string Value)> SetConfigs { get; } = [];
    public List<(string Id, bool Value)> SetBoolConfigs { get; } = [];
    public int CancelCount { get; private set; }

    public Func<FakeAcpSession, string, IReadOnlyList<AcpPromptAttachment>?, CancellationToken, Task<AcpPromptResult>>? PromptHandler { get; set; }

    public event Action<AcpRuntimeEvent>? EventReceived;

    public bool IsConnected { get; set; } = true;
    public string? SessionId { get; set; } = "fake-acp-session-1";
    public AcpInitializeResult? Initialized { get; set; } = new()
    {
        ProtocolVersion = 1,
        AgentCapabilities = new() { PromptCapabilities = new() { Image = true } },
    };
    public AcpSessionModeState? Modes { get; set; }
    public List<AcpSessionConfigOption> MutableConfigOptions { get; set; } = [];
    public IReadOnlyList<AcpSessionConfigOption> ConfigOptions => MutableConfigOptions;
    public AcpSessionModelState? Models { get; set; }
    public List<AcpAvailableCommand> MutableCommands { get; set; } = [];
    public IReadOnlyList<AcpAvailableCommand> AvailableCommands => MutableCommands;
    public IAcpDialect Dialect { get; }
    public string SourceLabel => Plan.SourceLabel;
    public string StderrText { get; set; } = "";

    public string GetStderrSnapshot() => StderrText;

    public void Emit(AcpRuntimeEvent evt) => EventReceived?.Invoke(evt);

    public Task<AcpSessionStartResult> StartAsync(
        string workingDirectory,
        IReadOnlyList<string>? additionalDirectories = null,
        string? resumeSessionId = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AcpSessionStartResult
        {
            Kind = string.IsNullOrEmpty(resumeSessionId) ? "new" : "load",
            SessionId = SessionId ?? "fake-acp-session-1",
            Modes = Modes,
            Models = Models,
            ConfigOptions = [.. MutableConfigOptions],
        });

    public Task<AcpPromptResult> PromptAsync(
        string text,
        IReadOnlyList<AcpPromptAttachment>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        SentPrompts.Add((text, attachments));
        if (PromptHandler is not null)
        {
            return PromptHandler(this, text, attachments, cancellationToken);
        }
        return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
    }

    public Task CancelAsync()
    {
        CancelCount++;
        return Task.CompletedTask;
    }

    public Task SetModeAsync(string modeId, CancellationToken cancellationToken = default)
    {
        SetModes.Add(modeId);
        return Task.CompletedTask;
    }

    public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        SetModels.Add(modelId);
        return Task.CompletedTask;
    }

    public Task SetConfigOptionAsync(string configId, string value, CancellationToken cancellationToken = default)
    {
        SetConfigs.Add((configId, value));
        return Task.CompletedTask;
    }

    public Task SetBooleanConfigOptionAsync(string configId, bool value, CancellationToken cancellationToken = default)
    {
        SetBoolConfigs.Add((configId, value));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeAcpSessionFactory(AcpDialectRegistry dialects) : IAcpSessionFactory
{
    public List<FakeAcpSession> Created { get; } = [];
    public Action<FakeAcpSession>? ConfigureNext { get; set; }

    public IAcpSession Create(AcpLaunchPlan plan, AcpSessionCallbacks callbacks)
    {
        var session = new FakeAcpSession(plan, dialects.Resolve(plan.DialectId), callbacks);
        ConfigureNext?.Invoke(session);
        Created.Add(session);
        return session;
    }
}

public class AgentGuiTests
{
    [AvaloniaFact]
    public void Agents_settings_hosts_dual_surface_cards_and_locks_non_acp_agents_to_terminal()
    {
        var settings = AppHost.Get<ISettingsStore>();
        var catalog = AppHost.Get<IAgentCatalog>();
        var scanner = new FakeAgentScanner("opencode", "aider");
        var vm = new AgentsSettingsViewModel(
            settings,
            catalog,
            scanner,
            AppHost.Get<IAgentCommandCompiler>(),
            AppHost.Get<IModelDiscovery>(),
            AppHost.Get<IFileOpener>(),
            AppHost.Get<IPathPicker>());

        var view = new AgentsPageView { DataContext = vm };
        var window = new Window { Width = 960, Height = 800, Content = view };
        window.Show();
        try
        {
            // Defaults to Terminal; clicking the big GUI card switches DefaultSurface to Gui.
            Assert.True(vm.IsTerminalDefault);
            Assert.False(vm.IsGuiDefault);

            vm.SelectGuiSurfaceCommand.Execute(null);
            Assert.Equal(AgentSurface.Gui, settings.Current.Agents.DefaultSurface);
            Assert.True(vm.IsGuiDefault);

            vm.AcpAdapterPolicyKey = nameof(AcpAdapterPolicy.NativeOnly);
            Assert.Equal(AcpAdapterPolicy.NativeOnly, settings.Current.Agents.AcpAdapterPolicy);

            vm.AcpEnableFormElicitation = false;
            Assert.False(settings.Current.Agents.AcpEnableFormElicitation);

            // Installed rows: opencode supports ACP (Follow/GUI/Terminal); aider does NOT (locked to Terminal).
            var opencodeRow = Assert.Single(vm.Installed, r => r.Id == "opencode");
            var aiderRow = Assert.Single(vm.Installed, r => r.Id == "aider");

            Assert.True(opencodeRow.SupportsAcp);
            Assert.Equal("follow", opencodeRow.SurfaceKey);
            opencodeRow.SurfaceKey = "terminal";
            Assert.Equal(AgentSurface.Terminal, settings.Current.Agents.ProfileOf("opencode").Surface);

            Assert.False(aiderRow.SupportsAcp);
            Assert.Equal("terminal", aiderRow.SurfaceKey);
            aiderRow.SurfaceKey = "gui"; // must be ignored for non-ACP agents
            Assert.Null(settings.Current.Agents.ProfileOf("aider").Surface);
            Assert.Equal("terminal", aiderRow.SurfaceKey);
        }
        finally
        {
            vm.Dispose();
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Launcher_offers_dual_buttons_for_acp_agents_and_terminal_only_for_non_acp()
    {
        var catalog = AppHost.Get<IAgentCatalog>();
        var compiler = AppHost.Get<IAgentCommandCompiler>();
        var discovery = AppHost.Get<IModelDiscovery>();

        AgentLaunchOptions? launchedOpencode = null;
        var opencodePanel = new AgentLaunchViewModel(
            catalog.Find("opencode")!,
            new AgentProfile(),
            PermissionModes.Default,
            compiler,
            discovery,
            "/usr/local/bin/opencode",
            opts => launchedOpencode = opts,
            defaultSurface: AgentSurface.Gui);

        Assert.True(opencodePanel.ShowDualLaunchButtons);
        Assert.False(opencodePanel.ShowTerminalOnlyHint);
        opencodePanel.LaunchCommand.Execute(null);
        Assert.Equal(AgentSurface.Gui, launchedOpencode!.Surface);

        opencodePanel.LaunchSecondaryCommand.Execute(null);
        Assert.Equal(AgentSurface.Terminal, launchedOpencode!.Surface);
        opencodePanel.Dispose();

        // Non-ACP agent: aider NEVER offers GUI launch.
        AgentLaunchOptions? launchedAider = null;
        var aiderPanel = new AgentLaunchViewModel(
            catalog.Find("aider")!,
            new AgentProfile(),
            PermissionModes.Default,
            compiler,
            discovery,
            "/usr/local/bin/aider",
            opts => launchedAider = opts,
            defaultSurface: AgentSurface.Gui);

        Assert.False(aiderPanel.ShowDualLaunchButtons);
        Assert.True(aiderPanel.ShowTerminalOnlyHint);
        Assert.Equal(AgentSurface.Terminal, aiderPanel.DefaultSurface);
        aiderPanel.LaunchGuiCommand.Execute(null);
        Assert.Equal(AgentSurface.Terminal, launchedAider!.Surface);
        aiderPanel.Dispose();
    }

    [AvaloniaFact]
    public async Task Tab_registry_routes_acp_gui_to_AgentGuiTabView_and_forces_non_acp_to_TerminalTabView()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-acp-route-").FullName;
        try
        {
            var settings = AppHost.Get<ISettingsStore>();
            settings.Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);

            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(tempDir, "acp-route-test");
            var opener = AppHost.Get<IItemOpener>();
            var registry = AppHost.Get<ITabContentRegistry>();

            var opencodeItem = opener.OpenAgent("opencode")!;
            Assert.Equal(AgentSurface.Gui, opencodeItem.Surface);
            var opencodeControl = registry.GetOrCreate(project, opencodeItem);
            var guiTab = Assert.IsType<AgentGuiTabView>(opencodeControl);

            // Even with DefaultSurface=Gui and explicit Gui override, aider MUST open in Terminal.
            var aiderItem = opener.OpenAgent("aider", new AgentLaunchOptions { Surface = AgentSurface.Gui })!;
            Assert.Equal(AgentSurface.Terminal, aiderItem.Surface);
            var aiderControl = registry.GetOrCreate(project, aiderItem);
            var termTab = Assert.IsType<TerminalTabView>(aiderControl);

            await guiTab.CloseAsync();
            (guiTab.DataContext as IDisposable)?.Dispose();
            await termTab.CloseAsync();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task StyleA_agent_gui_streams_thought_diff_markdown_permission_question_and_adapts_composer_per_agent()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-acp-stream-").FullName;
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);

        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(tempDir, "acp-gui-stream");
        var opener = AppHost.Get<IItemOpener>();
        var registry = AppHost.Get<ITabContentRegistry>();

        string? resolvedPerm = null;
        IReadOnlyDictionary<string, IReadOnlyList<string>>? resolvedQuestion = null;

        fakeFactory.ConfigureNext = session =>
        {
            session.Initialized = new AcpInitializeResult
            {
                ProtocolVersion = 1,
                AgentCapabilities = new() { PromptCapabilities = new() { Image = true } },
            };
            session.Modes = new AcpSessionModeState
            {
                CurrentModeId = "build",
                AvailableModes =
                [
                    new AcpSessionMode { Id = "build", Name = "Build" },
                    new AcpSessionMode { Id = "plan", Name = "Plan" },
                ],
            };
            session.Models = new AcpSessionModelState
            {
                CurrentModelId = "claude-sonnet-4",
                AvailableModels =
                [
                    new AcpModelInfo { ModelId = "claude-sonnet-4", Name = "Claude Sonnet 4" },
                    new AcpModelInfo { ModelId = "gpt-5", Name = "GPT-5" },
                ],
            };
            session.MutableCommands =
            [
                new AcpAvailableCommand { Name = "compact", Description = "Compact context" },
            ];
            session.PromptHandler = async (s, prompt, attachments, ct) =>
            {
                s.Emit(new AcpTextDeltaEvent("reasoning", "Checking the repository layout...", null));
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate
                {
                    ToolCallId = "tc-edit-1",
                    Kind = "edit",
                    Title = "Edit App.cs",
                    Status = "completed",
                    Diffs = [new AcpToolDiff { Path = "App.cs", OldText = "var x = 1;", NewText = "var x = 2;" }],
                }));
                s.Emit(new AcpUsageEvent(new AcpContextUsage { Used = 12000, Size = 200000 }));

                resolvedPerm = await s.Callbacks.OnPermissionRequest!(new AcpPermissionRequest
                {
                    RequestId = "p1",
                    SessionId = s.SessionId!,
                    ToolCall = new AcpToolCallUpdate { ToolCallId = "tc-run", Kind = "execute", Title = "Run dotnet test", Command = "dotnet test" },
                    Options = [new AcpPermissionOption { OptionId = "allow_once", Name = "Allow once", Kind = "allow_once" }],
                }, ct);

                resolvedQuestion = await s.Callbacks.OnQuestionRequest!(new AcpQuestionRequest
                {
                    RequestId = "q1",
                    Title = "Choose design",
                    Questions =
                    [
                        new AcpQuestionItem
                        {
                            Id = "style",
                            Prompt = "Pick GUI style",
                            Options =
                            [
                                new AcpQuestionOption { Id = "A", Label = "Style A" },
                                new AcpQuestionOption { Id = "B", Label = "Style B" },
                            ],
                        },
                    ],
                }, ct);

                s.Emit(new AcpTextDeltaEvent("assistant", "All updated in **App.cs**!", null));
                return new AcpPromptResult { StopReason = "end_turn" };
            };
        };

        var item = opener.OpenAgent("opencode")!;
        var view = Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(project, item));
        var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);

        var window = new Window { Width = 1000, Height = 720, Content = view };
        window.Show();
        try
        {
            await WaitUntilAsync(() => vm.State == AgentGuiState.Ready);

            // Dynamic composer for OpenCode: /, @, Image, Mode, Permission, Model visible; Fast & Thinking hidden.
            Assert.True(vm.Composer.SupportsSlashCommands);
            Assert.True(vm.Composer.SupportsFileMentions);
            Assert.True(vm.Composer.SupportsImageAttachments);
            Assert.True(vm.Composer.ShowModeSelector);
            Assert.True(vm.Composer.ShowPermissionSelector);
            Assert.True(vm.Composer.ShowModelSelector);
            Assert.False(vm.Composer.ShowFastToggle);
            Assert.False(vm.Composer.ShowThinkingToggle);

            // Verify composer model popover opens via its trigger and closes cleanly on selection.
            var modelPopover = view.FindControl<TKCode.Arc.Popover>("ModelPickerPopover")!;
            var modelTrigger = view.FindControl<Avalonia.Controls.Button>("ModelSelector")!;
            modelTrigger.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(modelPopover.IsOpen);
            vm.Composer.FilteredModelChoices[1].SelectCommand.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(modelPopover.IsOpen);
            Assert.Equal("gpt-5", vm.Composer.SelectedModelId);
            Assert.Equal("GPT-5", vm.Composer.SelectedModelLabel);

            // Send a prompt and resolve permission + question cards interactively.
            vm.InputText = "Refactor App.cs";
            var sendTask = vm.SendPromptAsync();

            await WaitUntilAsync(() => vm.Timeline.OfType<PermissionCardItem>().Any());
            var permCard = vm.Timeline.OfType<PermissionCardItem>().Single();
            Assert.Equal(AgentGuiState.Waiting, vm.State);
            permCard.Actions[0].ChooseCommand.Execute(null);
            Assert.True(permCard.IsResolved);
            // Resolved permission card smoothly dismisses and disappears from the timeline.
            await WaitUntilAsync(() => !vm.Timeline.OfType<PermissionCardItem>().Any());

            await WaitUntilAsync(() => vm.Timeline.OfType<QuestionCardItem>().Any());
            var qCard = vm.Timeline.OfType<QuestionCardItem>().Single();
            qCard.SubmitCommand.Execute(null);
            Assert.True(qCard.IsResolved);

            await sendTask;

            Assert.Equal("allow_once", resolvedPerm);
            Assert.Equal(["A"], resolvedQuestion!["style"]);
            Assert.Equal(AgentGuiState.Ready, vm.State);
            Assert.True(vm.HasContextUsage);
            Assert.Contains("12k", vm.ContextUsageText);

            var assistantTurn = vm.Timeline.OfType<AssistantTurnItem>().Single();
            Assert.Contains("Checking the repository", assistantTurn.ThoughtText);
            Assert.Contains("App.cs", assistantTurn.MarkdownText);
            var tool = Assert.Single(assistantTurn.ToolCalls);
            Assert.True(tool.HasDiff);
            Assert.Equal("+1 -1", tool.DiffStatsText);

            // Continue in Terminal opens a new Terminal item for the same agent.
            vm.ContinueInTerminalCommand.Execute(null);
            var terminalItem = project.Items.Last();
            Assert.Equal(AgentSurface.Terminal, terminalItem.Surface);
            Assert.Equal("opencode", terminalItem.AgentId);

            // Verify dynamic composer adapts cleanly to a completely different agent:
            // 1) Cline: DropsPromptImages=true, no modes, no slash commands, no fast/thinking.
            var catalog = AppHost.Get<IAgentCatalog>();
            var dialects = AppHost.Get<AcpDialectRegistry>();
            var clineComposer = new AgentComposerCapabilities(
                catalog.Find("cline")!,
                dialects.Resolve("cline"),
                PermissionModes.Default,
                null, null, null);
            clineComposer.UpdateFromSession(
                new AcpInitializeResult { AgentCapabilities = new() { PromptCapabilities = new() { Image = true } } },
                modes: null,
                models: null,
                configOptions: [],
                commands: []);
            Assert.False(clineComposer.SupportsImageAttachments); // Cline dialect drops images -> button hidden!
            Assert.False(clineComposer.SupportsSlashCommands);
            Assert.False(clineComposer.ShowModeSelector);
            Assert.False(clineComposer.ShowFastToggle);
            Assert.False(clineComposer.ShowThinkingToggle);

            // 2) Cursor: exposes fast + thinking booleans + thought_level + model config options.
            var cursorComposer = new AgentComposerCapabilities(
                catalog.Find("cursor")!,
                dialects.Resolve("cursor"),
                PermissionModes.Default,
                null, null, null);
            cursorComposer.UpdateFromSession(
                new AcpInitializeResult { AgentCapabilities = new() { PromptCapabilities = new() { Image = true } } },
                modes: null,
                models: null,
                configOptions:
                [
                    new AcpSessionConfigOption
                    {
                        Id = "thought_level",
                        Category = "thought_level",
                        CurrentValue = "high",
                        Options = [new() { Value = "low", Name = "Low" }, new() { Value = "high", Name = "High" }],
                    },
                    new AcpSessionConfigOption { Id = "fast", Type = "boolean", CurrentBoolValue = true },
                    new AcpSessionConfigOption { Id = "thinking", Type = "boolean", CurrentBoolValue = true },
                ],
                commands: []);
            Assert.True(cursorComposer.ShowEffortSelector);
            Assert.True(cursorComposer.ShowFastToggle);
            Assert.True(cursorComposer.ShowThinkingToggle);
            Assert.True(cursorComposer.FastMode);
            Assert.True(cursorComposer.ThinkingEnabled);

            // 3) Per-model effort narrowing: Claude narrows haiku to low/medium/high.
            var claudeComposer = new AgentComposerCapabilities(
                catalog.Find("claude")!,
                dialects.Resolve("claude-acp"),
                PermissionModes.Default,
                "opus", "max", null);
            Assert.Equal(5, claudeComposer.EffortOptions.Count);
            claudeComposer.SelectedModelId = "haiku";
            Assert.Equal(3, claudeComposer.EffortOptions.Count);
            Assert.Equal("low", claudeComposer.SelectedEffortId);

            // 4) Antigravity live ACP session: models embed effort (High/XHigh/Low), no live thought_level configOption
            // -> dead Effort selector MUST be hidden, model search enabled & filtering works!
            var agyComposer = new AgentComposerCapabilities(
                catalog.Find("agy")!,
                dialects.Resolve("antigravity"),
                PermissionModes.Full,
                null, null, null);
            agyComposer.UpdateFromSession(
                new AcpInitializeResult { AgentCapabilities = new() { PromptCapabilities = new() { Image = true } } },
                modes: new AcpSessionModeState
                {
                    CurrentModeId = "default",
                    AvailableModes =
                    [
                        new AcpSessionMode { Id = "default", Name = "Default" },
                        new AcpSessionMode { Id = "accept-edits", Name = "Accept edits" },
                        new AcpSessionMode { Id = "auto", Name = "Auto" },
                        new AcpSessionMode { Id = "plan", Name = "Plan" },
                    ],
                },
                models: new AcpSessionModelState
                {
                    CurrentModelId = "gemini-4-neon-high",
                    AvailableModels =
                    [
                        new AcpModelInfo { ModelId = "gemini-4-neon-high", Name = "Gemini 4 Neon (High)" },
                        new AcpModelInfo { ModelId = "gemini-4-neon-xhigh", Name = "Gemini 4 Neon (XHigh)" },
                        new AcpModelInfo { ModelId = "gemini-4-neon-medium", Name = "Gemini 4 Neon (Medium)" },
                        new AcpModelInfo { ModelId = "gemini-4-neon-low", Name = "Gemini 4 Neon (Low)" },
                        new AcpModelInfo { ModelId = "gemini-4-argon-high", Name = "Gemini 4 Argon (High)" },
                        new AcpModelInfo { ModelId = "gemini-4-hydrogen-high", Name = "Gemini 4 Hydrogen (High)" },
                        new AcpModelInfo { ModelId = "claude-opus-5-5-high", Name = "Claude Opus 5.5 (High)" },
                    ],
                },
                configOptions: [],
                commands: [],
                preserveConfiguredPermission: true);
            Assert.False(agyComposer.ShowModeSelector); // collapsed into Permission
            Assert.Equal(PermissionModes.Full, agyComposer.SelectedPermissionId); // server default must NOT wipe user's Full permission!
            Assert.False(agyComposer.ShowEffortSelector); // no live effort option -> hidden!
            Assert.True(agyComposer.ShowModelSelector);
            Assert.True(agyComposer.ShowModelSearch);
            Assert.Equal("Gemini 4 Neon (High)", agyComposer.SelectedModelLabel);
            agyComposer.ModelSearchQuery = "hydrogen";
            Assert.Single(agyComposer.FilteredModelChoices);
            Assert.Equal("Gemini 4 Hydrogen (High)", agyComposer.FilteredModelChoices[0].Label);
            // Switching model after session init must NOT resurrect the dead catalog Effort selector.
            agyComposer.SelectedModelId = "gemini-4-hydrogen-high";
            Assert.False(agyComposer.ShowEffortSelector);
        }
        finally
        {
            await vm.CloseAsync();
            vm.Dispose();
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Compact_gui_supports_history_picker_navigation_smart_scroll_queuing_and_image_gating()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-acp-ux-").FullName;
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);

        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(tempDir, "acp-gui-ux");
        var opener = AppHost.Get<IItemOpener>();
        var registry = AppHost.Get<ITabContentRegistry>();

        var firstTurnGate = new TaskCompletionSource<bool>();
        var promptCalls = new List<string>();

        fakeFactory.ConfigureNext = session =>
        {
            session.Initialized = new AcpInitializeResult
            {
                ProtocolVersion = 1,
                AgentCapabilities = new() { PromptCapabilities = new() { Image = true } },
            };
            session.MutableCommands =
            [
                new AcpAvailableCommand { Name = "compact", Description = "Compact context" },
                new AcpAvailableCommand { Name = "review", Description = "Review diff" },
            ];
            session.PromptHandler = async (s, prompt, _, _) =>
            {
                promptCalls.Add(prompt);
                if (promptCalls.Count == 1)
                {
                    await firstTurnGate.Task;
                }
                s.Emit(new AcpTextDeltaEvent("assistant", $"Reply to: {prompt}", null));
                return new AcpPromptResult { StopReason = "end_turn" };
            };
        };

        var item = opener.OpenAgent("opencode")!;
        var view = Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(project, item));
        var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);
        var window = new Window { Width = 920, Height = 640, Content = view };
        window.Show();
        try
        {
            await WaitUntilAsync(() => vm.State == AgentGuiState.Ready);

            // Picker keyboard navigation (Up/Down/Commit)
            vm.InputText = "/";
            Assert.True(vm.IsCommandPopupOpen);
            Assert.Equal(2, vm.FilteredCommands.Count);
            Assert.True(vm.FilteredCommands[0].IsSelected);
            Assert.True(vm.MovePickerSelection(1));
            Assert.True(vm.FilteredCommands[1].IsSelected);
            Assert.True(vm.CommitSelectedPickerItem());
            Assert.Equal("/review ", vm.InputText);
            Assert.False(vm.IsCommandPopupOpen);

            // Clipboard image bytes attach cleanly when supported
            Assert.True(vm.AttachImageBytes([1, 2, 3, 4], "clip.png"));
            Assert.Single(vm.Attachments);

            // Queueing follow-up message while active turn is busy
            vm.InputText = "First task";
            var firstTask = vm.SendPromptAsync();
            await WaitUntilAsync(() => vm.State == AgentGuiState.Working);

            // Smart scroll unpin shows JumpToBottom button
            vm.IsPinnedToBottom = false;
            Assert.True(vm.ShowJumpToBottom);
            vm.JumpToBottomCommand.Execute(null);
            Assert.True(vm.IsPinnedToBottom);
            Assert.False(vm.ShowJumpToBottom);

            // Send second prompt while busy -> queued, then auto-dispatched once first completes!
            vm.InputText = "Second queued task";
            await vm.SendPromptAsync();
            Assert.True(vm.HasQueuedPrompts);
            Assert.Single(vm.QueuedPrompts);

            firstTurnGate.TrySetResult(true);
            await firstTask;

            Assert.Equal(["First task", "Second queued task"], promptCalls);
            Assert.False(vm.HasQueuedPrompts);

            // Prompt history Up/Down with draft preservation
            vm.InputText = "draft in progress";
            Assert.True(vm.NavigateHistoryUp());
            Assert.Equal("Second queued task", vm.InputText);
            Assert.True(vm.NavigateHistoryUp());
            Assert.Equal("First task", vm.InputText);
            Assert.True(vm.NavigateHistoryDown());
            Assert.Equal("Second queued task", vm.InputText);
            Assert.True(vm.NavigateHistoryDown());
            Assert.Equal("draft in progress", vm.InputText);

            // ReusePrompt loads a past user turn back into the composer.
            var firstUserTurn = vm.Timeline.OfType<UserTurnItem>().First();
            Assert.True(firstUserTurn.CanReuse);
            firstUserTurn.ReuseCommand.Execute(null);
            Assert.Equal("First task", vm.InputText);

            // Exploration burst grouping folds >= 2 completed read/search tools while keeping edit/running prominent.
            // Crucially, running tools can be expanded immediately during execution and survive subsequent tool additions in place.
            var lastAssistant = vm.Timeline.OfType<AssistantTurnItem>().Last();
            var resetEvents = 0;
            lastAssistant.PrimaryTools.CollectionChanged += (_, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resetEvents++;
            };
            lastAssistant.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "run1", Kind = "execute", Title = "dotnet build", Status = "in_progress" });
            var runningTool = Assert.Single(lastAssistant.PrimaryTools);
            Assert.True(runningTool.CanExpand);
            Assert.True(runningTool.IsRunningWithoutOutput);
            runningTool.ToggleExpandedCommand.Execute(null);
            Assert.True(runningTool.IsExpanded);

            lastAssistant.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "r1", Kind = "read", Title = "Read A.cs", Status = "completed" });
            Assert.False(lastAssistant.HasExplorationGroup);
            Assert.Equal(2, lastAssistant.PrimaryTools.Count);

            lastAssistant.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "s1", Kind = "search", Title = "Grep Foo", Status = "completed" });
            lastAssistant.UpsertToolCall(new AcpToolCallUpdate
            {
                ToolCallId = "e1",
                Kind = "edit",
                Title = "Edit A.cs",
                Status = "completed",
                Diffs = [new AcpToolDiff { Path = "A.cs", OldText = "a", NewText = "b" }],
            });
            Assert.Equal(0, resetEvents); // in-place SyncCollection NEVER clears PrimaryTools during execution!
            Assert.True(runningTool.IsExpanded);
            Assert.True(lastAssistant.HasExplorationGroup);
            Assert.Equal(2, lastAssistant.ExplorationTools.Count);
            Assert.Equal(2, lastAssistant.PrimaryTools.Count);
            Assert.Equal("e1", lastAssistant.PrimaryTools[1].ToolCallId);
            Assert.True(lastAssistant.PrimaryTools[1].CanOpenDiff);

            // Usage cost formatting & NewSession reset.
            fakeFactory.Created[^1].Emit(new AcpUsageEvent(new AcpContextUsage { Used = 8000, Size = 100000, CostAmount = 0.04, CostCurrency = "USD" }));
            await WaitUntilAsync(() => vm.ContextUsageText.Contains("$0.04"));

            await vm.NewSessionAsync();
            Assert.Empty(vm.Timeline);
            Assert.False(vm.HasContextUsage);
        }
        finally
        {
            await vm.CloseAsync();
            vm.Dispose();
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Sessions_panel_tracks_live_acp_sessions_and_offers_gui_resume_only_for_acp_agents()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-acp-sess-").FullName;
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(tempDir, "acp-sessions-test");
        var sessionsService = AppHost.Get<FakeAgentSessionService>();
        var catalog = AppHost.Get<IAgentCatalog>();

        var now = DateTimeOffset.UtcNow;
        sessionsService.Groups["opencode"] = new AgentSessionGroup(
            catalog.Find("opencode")!,
            true,
            [
                new AgentSessionAccount("/tmp/oc", "Default", true,
                [
                    new AgentSessionInfo("opencode", "/tmp/oc", "oc-sess-1", "Refactor parser", now, now, 6, null, null, "/tmp/oc/1.json"),
                    new AgentSessionInfo("opencode", "/tmp/oc", "oc-sess-2", "Investigate crash", now.AddMinutes(-5), now.AddMinutes(-5), 4, null, null, "/tmp/oc/2.json"),
                ]),
            ]);
        sessionsService.Groups["aider"] = new AgentSessionGroup(
            catalog.Find("aider")!,
            true,
            [
                new AgentSessionAccount("/tmp/aider", "Default", true,
                [
                    new AgentSessionInfo("aider", "/tmp/aider", "ai-sess-1", "CLI only session", now, now, 2, null, null, "/tmp/aider/1.json"),
                ]),
            ]);

        var opener = AppHost.Get<IItemOpener>();
        var liveGui = opener.OpenAgent("opencode", new AgentLaunchOptions { Surface = AgentSurface.Gui })!;
        liveGui.AcpSessionId = "oc-sess-1";

        var panel = new Octopus.App.Explorer.Sessions.SessionsPanelViewModel(
            sessionsService,
            catalog,
            new FakeAgentScanner("opencode", "aider"),
            AppHost.Get<ISettingsStore>(),
            projects,
            opener,
            new DummyMigrationDialog(),
            AppHost.Get<IDialogService>(),
            AppHost.Get<IAppPaths>());
        panel.SetProject(project.Path);
        panel.IsActive = true;
        await panel.LoadAsync();

        var agentRow = Assert.Single(panel.Agents, a => a.AgentId == "opencode");
        agentRow.OpenCommand.Execute(null);
        var rows = panel.Groups.SelectMany(g => g.Rows).ToList();
        var liveRow = Assert.Single(rows, r => r.Info.SessionId == "oc-sess-1");
        var savedRow = Assert.Single(rows, r => r.Info.SessionId == "oc-sess-2");

        Assert.True(liveRow.IsLive);
        Assert.False(liveRow.CanContinueInAlternateSurface);

        Assert.False(savedRow.IsLive);
        Assert.True(savedRow.CanContinueInAlternateSurface);
        Assert.True(savedRow.AlternateSurfaceIsGui); // default surface is Terminal -> alternate is GUI (ACP)!

        await savedRow.ContinueAlternateCommand.ExecuteAsync(null);
        var resumed = project.Items.Last();
        Assert.Equal("opencode", resumed.AgentId);
        Assert.Equal(AgentSurface.Gui, resumed.Surface);
        Assert.Equal("oc-sess-2", resumed.Launch?.SessionId);

        // Non-ACP agent (aider) NEVER offers GUI resume in the Sessions panel.
        panel.BackCommand.Execute(null);
        var aiderAgentRow = Assert.Single(panel.Agents, a => a.AgentId == "aider");
        aiderAgentRow.OpenCommand.Execute(null);
            var aiderRow = Assert.Single(panel.Groups.SelectMany(g => g.Rows));
            Assert.False(aiderRow.CanContinueInAlternateSurface);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }

    private sealed class DummyMigrationDialog : Octopus.App.Explorer.Sessions.ISessionMigrationDialog
    {
        public Task<Octopus.App.Explorer.Sessions.SessionMigrationChoice?> AskAsync(Octopus.App.Explorer.Sessions.SessionMigrationFormViewModel form) =>
            Task.FromResult<Octopus.App.Explorer.Sessions.SessionMigrationChoice?>(null);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for headless UI condition.");
            }
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
    }
}
