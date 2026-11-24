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
    public Func<FakeAcpSession, Task>? CancelHandler { get; set; }

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

    /// <summary>Runs during <see cref="StartAsync"/> (e.g. to emit session/load history replay).</summary>
    public Action<FakeAcpSession, string?>? StartHandler { get; set; }

    public Task<AcpSessionStartResult> StartAsync(
        string workingDirectory,
        IReadOnlyList<string>? additionalDirectories = null,
        string? resumeSessionId = null,
        CancellationToken cancellationToken = default)
    {
        StartHandler?.Invoke(this, resumeSessionId);
        return Task.FromResult(new AcpSessionStartResult
        {
            Kind = string.IsNullOrEmpty(resumeSessionId) ? "new" : "load",
            SessionId = SessionId ?? "fake-acp-session-1",
            Modes = Modes,
            Models = Models,
            ConfigOptions = [.. MutableConfigOptions],
        });
    }

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
        return CancelHandler is not null ? CancelHandler(this) : Task.CompletedTask;
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
    public async Task Assistant_turn_caps_expanded_thought_height_folds_completed_commands_and_shows_live_status()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-acp-polish-").FullName;
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);

        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(tempDir, "acp-gui-polish");
        var opener = AppHost.Get<IItemOpener>();
        var registry = AppHost.Get<ITabContentRegistry>();

        var turnGate = new TaskCompletionSource<bool>();
        fakeFactory.ConfigureNext = session =>
        {
            session.PromptHandler = async (s, _, _, _) =>
            {
                s.Emit(new AcpTextDeltaEvent("reasoning", string.Join("\n", Enumerable.Range(1, 80).Select(i => $"Thought line {i}: investigating architecture details...")), null));
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate { ToolCallId = "cmd1", Kind = "execute", Title = "ls -la", Command = "ls -la", Status = "completed", ExitCode = 0 }));
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate { ToolCallId = "cmd2", Kind = "execute", Title = "ls -la src", Command = "ls -la src", Status = "completed", ExitCode = 0 }));
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate { ToolCallId = "cmd3", Kind = "execute", Title = "dotnet test", Command = "dotnet test", Status = "in_progress" }));
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate { ToolCallId = "cmd4", Kind = "execute", Title = "bad-cmd", Command = "bad-cmd", Status = "completed", ExitCode = 1 }));
                await turnGate.Task;
                return new AcpPromptResult { StopReason = "end_turn" };
            };
        };

        var item = opener.OpenAgent("opencode")!;
        var view = Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(project, item));
        var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);
        var window = new Window { Width = 960, Height = 720, Content = view };
        window.Show();
        try
        {
            await WaitUntilAsync(() => vm.State == AgentGuiState.Ready);
            vm.InputText = "Inspect repo";
            var sendTask = vm.SendPromptAsync();

            await WaitUntilAsync(() => vm.Timeline.OfType<AssistantTurnItem>().Any(t => t.ToolCalls.Count == 4));
            var turn = vm.Timeline.OfType<AssistantTurnItem>().Single();

            // 1) Completed successful bash commands (>= 2) fold into CommandTools while running & non-zero exit stay in PrimaryTools.
            Assert.True(turn.HasCommandGroup);
            Assert.Equal(2, turn.CommandTools.Count);
            Assert.Equal(["cmd1", "cmd2"], turn.CommandTools.Select(c => c.ToolCallId));
            Assert.Equal(["cmd3", "cmd4"], turn.PrimaryTools.Select(c => c.ToolCallId));
            Assert.False(string.IsNullOrWhiteSpace(turn.CommandGroupSummary));

            // 2) Live status feedback is visible while streaming, rotates playful copy, hides when waiting, and hides on completion.
            Assert.True(turn.ShowLiveStatus);
            var firstPhrase = turn.LiveStatusText;
            Assert.False(string.IsNullOrWhiteSpace(firstPhrase));
            turn.AdvanceLiveStatus();
            Assert.NotEqual(firstPhrase, turn.LiveStatusText);
            turn.SetWaitingForInput(true);
            Assert.False(turn.ShowLiveStatus);
            turn.SetWaitingForInput(false);
            Assert.True(turn.ShowLiveStatus);

            // 3) Expanded Thinking block is wrapped in a height-capped ScrollArea (MaxHeight <= 240).
            turn.ToggleThoughtCommand.Execute(null);
            Assert.True(turn.IsThoughtExpanded);
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var thoughtScroll = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view)
                .OfType<TKCode.Arc.ScrollArea>()
                .FirstOrDefault(s => s.Classes.Contains("thoughtScroll"));
            Assert.NotNull(thoughtScroll);
            Assert.InRange(thoughtScroll!.MaxHeight, 100, 260);

            turnGate.TrySetResult(true);
            await sendTask;
            Assert.False(turn.IsStreaming);
            Assert.False(turn.ShowLiveStatus);
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
    public void Assistant_turn_creates_independent_thought_blocks_across_tools_messages_and_message_ids()
    {
        var turn = new AssistantTurnItem("agy", "Antigravity CLI");
        try
        {
            // Step 1: initial thought split across 2 streaming deltas -> single ThoughtBlock #1
            turn.AppendThought("First part ");
            turn.AppendThought("of thought 1.");
            var t1 = Assert.IsType<ThoughtBlockViewModel>(Assert.Single(turn.Blocks));
            Assert.Equal("First part of thought 1.", t1.Text);

            // Step 2: tool burst arrives -> creates ToolRunBlock #1
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "tc1", Kind = "execute", Title = "ls -la", Command = "ls -la", Status = "in_progress" });
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "tc2", Kind = "execute", Title = "ls src", Command = "ls src", Status = "completed", ExitCode = 0 });
            Assert.Equal(2, turn.Blocks.Count);
            var run1 = Assert.IsType<ToolRunBlockViewModel>(turn.Blocks[1]);
            Assert.Equal(2, run1.ToolCalls.Count);

            // Step 3: subsequent reasoning after tools -> MUST create an independent ThoughtBlock #2 (never appended to Thought #1!)
            turn.AppendThought("Second thought after tools.", messageId: "m2");
            Assert.Equal(3, turn.Blocks.Count);
            var t2 = Assert.IsType<ThoughtBlockViewModel>(turn.Blocks[2]);
            Assert.Equal("First part of thought 1.", t1.Text);
            Assert.Equal("Second thought after tools.", t2.Text);

            // Late completion update for tc1 updates run1 in place and does NOT split ThoughtBlock #2!
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "tc1", Status = "completed", ExitCode = 0 });
            Assert.Equal(3, turn.Blocks.Count);
            Assert.True(run1.HasCommandGroup);
            Assert.Equal(2, run1.CommandTools.Count);

            turn.AppendThought(" Still thought 2.", messageId: "m2");
            Assert.Equal(3, turn.Blocks.Count);
            Assert.Equal("Second thought after tools. Still thought 2.", t2.Text);

            // Step 4: new reasoning with a different messageId -> independent ThoughtBlock #3
            turn.AppendThought("Third thought with new messageId.", messageId: "m3");
            Assert.Equal(4, turn.Blocks.Count);
            var t3 = Assert.IsType<ThoughtBlockViewModel>(turn.Blocks[3]);
            Assert.Equal("Third thought with new messageId.", t3.Text);

            // Independent expand/collapse state per thought block
            t2.ToggleCommand.Execute(null);
            Assert.False(t1.IsExpanded);
            Assert.True(t2.IsExpanded);
            Assert.False(t3.IsExpanded);

            // Step 5: final markdown response -> MarkdownBlock at the end
            turn.AppendMessage("Final answer.");
            Assert.Equal(5, turn.Blocks.Count);
            var md = Assert.IsType<MarkdownBlockViewModel>(turn.Blocks[4]);
            Assert.Equal("Final answer.", md.Text);
            Assert.Equal("Final answer.", turn.MarkdownText);
        }
        finally
        {
            turn.Complete();
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

    [AvaloniaFact]
    public async Task SchemeD_orchestration_handles_subagents_claude_workflow_background_bash_and_bash_style2()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-acp-scheme-d-").FullName;
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);

        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(tempDir, "acp-scheme-d");
        var opener = AppHost.Get<IItemOpener>();
        var registry = AppHost.Get<ITabContentRegistry>();
        var subagentService = AppHost.Get<ISubagentService>();

        var multilinePython = "python3 -c '\nimport urllib.request, json\npackages = [\n  \"Avalonia\",\n  \"TKCode.Arc\",\n  \"MiniPty\",\n]\nfor p in packages:\n  print(p)\n'";
        string? approvedPlanOption = null;

        fakeFactory.ConfigureNext = session =>
        {
            session.PromptHandler = async (s, _, _, ct) =>
            {
                // 1. Claude Code TodoWrite workflow tasks
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate
                {
                    ToolCallId = "todo-1",
                    WorkflowAction = "todo_write",
                    WorkflowTasks =
                    [
                        new AcpPlanEntry { Id = "1", Content = "Scan protocol", Status = "completed", Owner = "Explore" },
                        new AcpPlanEntry { Id = "2", Content = "Implement Scheme D HUD", Status = "in_progress", ActiveForm = "Building HUD capsules", Owner = "general-purpose", BlockedBy = ["1"] },
                    ],
                }));

                // 2. ExitPlanMode Plan Approval Gate
                approvedPlanOption = await s.Callbacks.OnPermissionRequest!(new AcpPermissionRequest
                {
                    RequestId = "plan-gate-1",
                    SessionId = s.SessionId!,
                    IsPlanApproval = true,
                    PlanMarkdown = "## Plan\n- Wire SubAgents\n- Track Background Bash",
                    Options =
                    [
                        new AcpPermissionOption { OptionId = "approve_auto", Name = "Approve & Accept Edits", Kind = "allow_once" },
                    ],
                }, ct);

                // 3. Large multiline Bash command (Style 2)
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate
                {
                    ToolCallId = "bash-big-1",
                    Kind = "execute",
                    Title = multilinePython,
                    Command = multilinePython,
                    Description = "Check latest NuGet package versions",
                    Status = "completed",
                    Stdout = "Avalonia -> 11.3.2\nTKCode.Arc -> 1.4.8",
                    ExitCode = 0,
                }));

                // 4. Parallel SubAgents + nested child tool call
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate
                {
                    ToolCallId = "sub-1",
                    IsSubagentLaunch = true,
                    SubagentType = "Explore",
                    Description = "Audit ACP event models",
                    SubagentModel = "haiku",
                    Status = "completed",
                    TextOutputs = ["Found 4 extension points."],
                }));
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate
                {
                    ToolCallId = "sub-2",
                    IsSubagentLaunch = true,
                    SubagentType = "general-purpose",
                    Description = "Build floating HUD capsules",
                    SubagentModel = "opus",
                    WorktreeIsolation = "worktree",
                    Status = "in_progress",
                }));
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate
                {
                    ToolCallId = "sub-2-step-1",
                    ParentToolCallId = "sub-2",
                    Name = "Edit",
                    Kind = "edit",
                    Title = "Edit AgentGuiTabView.axaml",
                    Status = "in_progress",
                }));

                // 5. Background Bash launch + follow-up TaskOutput
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate
                {
                    ToolCallId = "bg-1",
                    Kind = "execute",
                    Command = "npm run dev -- --port 5173",
                    Description = "Start Vite dev server",
                    IsBackground = true,
                    BackgroundTaskId = "bg_vite",
                    Status = "completed",
                    Stdout = "Command running in background with ID: bg_vite",
                }));
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate
                {
                    ToolCallId = "bg-out-1",
                    WorkflowAction = "background_output",
                    BackgroundTaskId = "bg_vite",
                    Stdout = "VITE ready in 280ms\nLocal: http://localhost:5173/",
                }));

                return new AcpPromptResult { StopReason = "end_turn" };
            };
        };

        var item = opener.OpenAgent("opencode")!;
        var view = Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(project, item));
        var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);
        var window = new Window { Width = 1100, Height = 760, Content = view };
        window.Show();
        try
        {
            await WaitUntilAsync(() => vm.State == AgentGuiState.Ready);
            vm.InputText = "Implement Scheme D";
            var sendTask = vm.SendPromptAsync();

            // Plan Approval Gate card appears and advances workflow on approval.
            await WaitUntilAsync(() => vm.Timeline.OfType<PlanApprovalCardItem>().Any());
            var planCard = vm.Timeline.OfType<PlanApprovalCardItem>().Single();
            Assert.Equal(WorkflowPhase.PlanGate, vm.CurrentPhase);
            planCard.Actions[0].ChooseCommand.Execute(null);
            Assert.True(planCard.IsResolved);

            await sendTask;
            Assert.Equal("approve_auto", approvedPlanOption);

            // Workflow tasks & HUD capsule state
            Assert.True(vm.HasWorkflowTasks);
            Assert.Equal(2, vm.WorkflowTasks.Count);
            Assert.Contains("1/2", vm.HudWorkflowText);
            Assert.True(vm.HasAnyHudItem);
            vm.ToggleWorkflowHudCommand.Execute(null);
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsWorkflowHudOpen);

            // SubAgents + nested child steps + sidebar sync
            Assert.Equal(2, vm.Subagents.Count);
            var runningSub = vm.Subagents[1];
            Assert.Single(runningSub.Steps);
            Assert.Equal("Edit", runningSub.Steps[0].ToolName);
            Assert.Equal(2, subagentService.Of(item.Id).Count);

            // Swimlane card rendered in timeline; clicking a SubAgent opens Slide-Over Drawer
            Assert.Contains(vm.Timeline, t => t is WorkflowSwimlaneItem);
            vm.SelectSubagentCommand.Execute(runningSub);
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(vm.HasSelectedSubagent);
            Assert.Same(runningSub, vm.SelectedSubagent);

            // Background Bash tracked across turn with detected port :5173 & live output
            var bgShell = Assert.Single(vm.BackgroundShells);
            Assert.Equal(":5173", bgShell.DetectedPort);
            Assert.True(bgShell.IsRunning);
            Assert.True(await vm.HasRunningChildAsync());
            vm.ToggleShellsHudCommand.Execute(null);
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsShellsHudOpen);
            bgShell.StopCommand.Execute(null);
            Assert.False(bgShell.IsRunning);

            // Bash Style 2: multiline python script stays strictly single-line in header & opens full-script modal
            var turn = vm.Timeline.OfType<AssistantTurnItem>().Single();
            var bigBash = Assert.Single(turn.ToolCalls, t => t.ToolCallId == "bash-big-1");
            Assert.DoesNotContain('\n', bigBash.DisplayTitle);
            Assert.Equal("Check latest NuGet package versions", bigBash.DisplayTitle);
            Assert.True(bigBash.IsLargeCommand);
            Assert.Equal("python3", bigBash.ScriptLanguage);
            Assert.True(bigBash.CommandLineCount >= 9);
            Assert.True(bigBash.CanOpenScriptPreview);
            bigBash.ToggleExpandedCommand.Execute(null);
            bigBash.OpenScriptPreviewCommand.Execute(null);
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(vm.ScriptPreview.IsOpen);
            Assert.Equal(bigBash.CommandLineCount, vm.ScriptPreview.Lines.Count);
            vm.ScriptPreview.CloseCommand.Execute(null);
            Assert.False(vm.ScriptPreview.IsOpen);

            // Topology view switches cleanly
            vm.SetTopologyViewCommand.Execute(null);
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsTopologyView);
            vm.SetChatViewCommand.Execute(null);
            Assert.False(vm.IsTopologyView);
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

    private sealed class DummyMigrationDialog : Octopus.App.Explorer.Sessions.ISessionMigrationDialog
    {
        public Task<Octopus.App.Explorer.Sessions.SessionMigrationChoice?> AskAsync(Octopus.App.Explorer.Sessions.SessionMigrationFormViewModel form) =>
            Task.FromResult<Octopus.App.Explorer.Sessions.SessionMigrationChoice?>(null);
    }

    [AvaloniaFact]
    public async Task Reopened_gui_tab_rebuilds_timeline_from_session_load_replay_without_duplicating_on_reconnect()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-acp-replay-").FullName;
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);

        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(tempDir, "acp-gui-replay");
        var opener = AppHost.Get<IItemOpener>();
        var registry = AppHost.Get<ITabContentRegistry>();

        var starts = 0;
        fakeFactory.ConfigureNext = session =>
        {
            session.StartHandler = (s, _) =>
            {
                starts++;
                s.Emit(new AcpUserMessageEvent("Fix the ", "u1") { IsReplay = true });
                s.Emit(new AcpUserMessageEvent("login bug", "u1") { IsReplay = true });
                s.Emit(new AcpTextDeltaEvent("reasoning", "Looking at auth.", null) { IsReplay = true });
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate { ToolCallId = "tc1", Kind = "read", Title = "Read auth.cs", Status = "completed" }) { IsReplay = true });
                s.Emit(new AcpTextDeltaEvent("assistant", "Fixed the null check.", "m1") { IsReplay = true });
                s.Emit(new AcpUserMessageEvent("Thanks, add a test", "u2") { IsReplay = true });
                s.Emit(new AcpTextDeltaEvent("assistant", "Added LoginTests.", "m2") { IsReplay = true });
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
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Equal(4, vm.Timeline.Count);
            var u1 = Assert.IsType<UserTurnItem>(vm.Timeline[0]);
            Assert.Equal("Fix the login bug", u1.Text);
            var a1 = Assert.IsType<AssistantTurnItem>(vm.Timeline[1]);
            Assert.False(a1.IsStreaming);
            Assert.Contains("Fixed the null check.", a1.MarkdownText);
            Assert.True(a1.HasThought);
            var u2 = Assert.IsType<UserTurnItem>(vm.Timeline[2]);
            Assert.Equal("Thanks, add a test", u2.Text);
            var a2 = Assert.IsType<AssistantTurnItem>(vm.Timeline[3]);
            Assert.False(a2.IsStreaming);
            Assert.False(a2.HasDuration);
            Assert.False(vm.ShowEmptyState);

            // Replayed prompts feed composer history navigation.
            Assert.True(vm.NavigateHistoryUp());
            Assert.Equal("Thanks, add a test", vm.InputText);
            vm.NavigateHistoryDown();

            // Reconnecting a tab that already shows history must not duplicate it.
            await vm.RestartAsync();
            await WaitUntilAsync(() => vm.State == AgentGuiState.Ready && starts == 2);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(4, vm.Timeline.Count);
        }
        finally
        {
            fakeFactory.ConfigureNext = null;
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Queue_tray_supports_send_now_edit_reorder_remove_and_pause_on_manual_stop()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-acp-queue-").FullName;
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);

        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(tempDir, "acp-gui-queue");
        var opener = AppHost.Get<IItemOpener>();
        var registry = AppHost.Get<ITabContentRegistry>();

        var events = new List<string>();
        var activeTurns = 0;
        var maxConcurrentTurns = 0;
        TaskCompletionSource<AcpPromptResult>? currentGate = null;

        fakeFactory.ConfigureNext = session =>
        {
            session.PromptHandler = async (s, prompt, _, ct) =>
            {
                var concurrent = Interlocked.Increment(ref activeTurns);
                if (concurrent > maxConcurrentTurns) maxConcurrentTurns = concurrent;
                events.Add($"start:{prompt}");
                try
                {
                    if (prompt.StartsWith("Hold", StringComparison.Ordinal))
                    {
                        var gate = new TaskCompletionSource<AcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                        currentGate = gate;
                        using var reg = ct.Register(() => gate.TrySetCanceled(ct));
                        var result = await gate.Task;
                        events.Add($"end:{prompt}:{result.StopReason}");
                        return result;
                    }
                    s.Emit(new AcpTextDeltaEvent("assistant", $"Done: {prompt}", null));
                    events.Add($"end:{prompt}:end_turn");
                    return new AcpPromptResult { StopReason = "end_turn" };
                }
                finally
                {
                    Interlocked.Decrement(ref activeTurns);
                }
            };
            session.CancelHandler = _ =>
            {
                events.Add("cancel");
                currentGate?.TrySetResult(new AcpPromptResult { StopReason = "cancelled" });
                return Task.CompletedTask;
            };
        };

        var item = opener.OpenAgent("opencode")!;
        var view = Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(project, item));
        var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);
        var window = new Window { Width = 960, Height = 700, Content = view };
        window.Show();
        try
        {
            await WaitUntilAsync(() => vm.State == AgentGuiState.Ready);

            // 1. Start a holding turn so the agent stays busy while we manipulate the queue tray.
            vm.InputText = "Hold 1";
            var turn1Task = vm.SendPromptAsync();
            await WaitUntilAsync(() => vm.State == AgentGuiState.Working);

            var sendNowButton = view.FindControl<TKCode.Arc.Button>("SendNowButton");
            Assert.NotNull(sendNowButton);
            Assert.True(sendNowButton!.IsVisible);
            Assert.False(vm.CanSendNow);

            // Queue 3 prompts (first one carries an attachment).
            vm.AddAttachment(new AcpPromptAttachment { Kind = "file", Path = Path.Combine(tempDir, "README.md"), DisplayName = "README.md" });
            vm.InputText = "Task A\nsecond line";
            Assert.True(vm.CanSend);
            Assert.True(vm.CanSendNow);
            Assert.True(vm.SendPromptCommand.CanExecute(null));
            Assert.True(vm.SendPromptNowCommand.CanExecute(null));
            await vm.SendPromptAsync();

            vm.InputText = "Task B";
            await vm.SendPromptAsync();
            vm.InputText = "Task C";
            await vm.SendPromptAsync();

            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Equal(3, vm.QueuedPrompts.Count);
            Assert.Equal([1, 2, 3], vm.QueuedPrompts.Select(q => q.Position));
            Assert.Equal([false, true, true], vm.QueuedPrompts.Select(q => q.CanMoveUp));
            Assert.Equal([true, true, false], vm.QueuedPrompts.Select(q => q.CanMoveDown));
            Assert.True(vm.QueuedPrompts[0].HasAttachments);
            Assert.Equal("Task A second line", vm.QueuedPrompts[0].PreviewText);

            // Collapse/expand preview works cleanly.
            vm.ToggleQueueExpandedCommand.Execute(null);
            Assert.False(vm.IsQueueExpanded);
            Assert.True(vm.ShowCollapsedQueuePreview);
            Assert.Equal("Task A second line", vm.FirstQueuedPreview);
            vm.ToggleQueueExpandedCommand.Execute(null);
            Assert.True(vm.IsQueueExpanded);

            // Reorder: move Task C up -> [A, C, B], then remove Task B -> [A, C].
            vm.QueuedPrompts[2].MoveUpCommand.Execute(null);
            Assert.Equal(["Task A\nsecond line", "Task C", "Task B"], vm.QueuedPrompts.Select(q => q.Text));
            Assert.Equal([1, 2, 3], vm.QueuedPrompts.Select(q => q.Position));
            vm.QueuedPrompts[2].RemoveCommand.Execute(null);
            Assert.Equal(["Task A\nsecond line", "Task C"], vm.QueuedPrompts.Select(q => q.Text));

            // Edit Task A back into composer while preserving an unsent composer draft!
            vm.InputText = "Draft D";
            vm.QueuedPrompts[0].EditCommand.Execute(null);
            Assert.Equal("Task A\nsecond line", vm.InputText);
            Assert.Single(vm.Attachments);
            Assert.Equal(["Task C", "Draft D"], vm.QueuedPrompts.Select(q => q.Text));

            // 2. Immediate Send from composer via Cmd/Ctrl+Enter keyboard shortcut:
            // gracefully cancels Hold 1, sends Urgent first, then drains [Task C, Draft D].
            vm.InputText = "Urgent from composer";
            var composerInput = view.FindControl<Avalonia.Controls.TextBox>("ComposerInput")!;
            var sendNowMod = OperatingSystem.IsMacOS() ? Avalonia.Input.KeyModifiers.Meta : Avalonia.Input.KeyModifiers.Control;
            composerInput.RaiseEvent(new Avalonia.Input.KeyEventArgs
            {
                RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
                Key = Avalonia.Input.Key.Enter,
                KeyModifiers = sendNowMod,
            });
            await turn1Task;
            await WaitUntilAsync(() => vm.State == AgentGuiState.Ready && !vm.HasQueuedPrompts);

            Assert.Equal(1, maxConcurrentTurns);
            Assert.False(vm.HasQueuedPrompts);
            Assert.Equal(AgentGuiState.Ready, vm.State);
            Assert.Equal(
                [
                    "start:Hold 1",
                    "cancel",
                    "end:Hold 1:cancelled",
                    "start:Urgent from composer",
                    "end:Urgent from composer:end_turn",
                    "start:Task C",
                    "end:Task C:end_turn",
                    "start:Draft D",
                    "end:Draft D:end_turn",
                ],
                events);

            // 3. Immediate Send on a specific queued item (#2) interrupts current turn and runs #2 before #1.
            events.Clear();
            vm.InputText = "Hold 2";
            var turn2Task = vm.SendPromptAsync();
            await WaitUntilAsync(() => vm.State == AgentGuiState.Working);

            vm.InputText = "Queued 1";
            await vm.SendPromptAsync();
            vm.InputText = "Queued 2";
            await vm.SendPromptAsync();

            var secondQueued = vm.QueuedPrompts[1];
            await secondQueued.SendNowCommand.ExecuteAsync(null);
            await turn2Task;

            Assert.Equal(
                [
                    "start:Hold 2",
                    "cancel",
                    "end:Hold 2:cancelled",
                    "start:Queued 2",
                    "end:Queued 2:end_turn",
                    "start:Queued 1",
                    "end:Queued 1:end_turn",
                ],
                events);

            // 4. Manual Stop pauses queue draining instead of auto-sending remaining queued items; Resume drains them.
            events.Clear();
            vm.InputText = "Hold 3";
            var turn3Task = vm.SendPromptAsync();
            await WaitUntilAsync(() => vm.State == AgentGuiState.Working);

            vm.InputText = "Paused 1";
            await vm.SendPromptAsync();
            vm.InputText = "Paused 2";
            await vm.SendPromptAsync();

            await vm.CancelTurnAsync();
            await turn3Task;

            Assert.Equal(AgentGuiState.Ready, vm.State);
            Assert.True(vm.IsQueuePaused);
            Assert.True(vm.ShowResumeQueueButton);
            Assert.Equal(["Paused 1", "Paused 2"], vm.QueuedPrompts.Select(q => q.Text));
            Assert.Equal(["start:Hold 3", "cancel", "end:Hold 3:cancelled"], events);

            await vm.ResumeQueueAsync();
            Assert.False(vm.IsQueuePaused);
            Assert.False(vm.HasQueuedPrompts);
            Assert.Equal(
                [
                    "start:Hold 3",
                    "cancel",
                    "end:Hold 3:cancelled",
                    "start:Paused 1",
                    "end:Paused 1:end_turn",
                    "start:Paused 2",
                    "end:Paused 2:end_turn",
                ],
                events);
            Assert.Equal(1, maxConcurrentTurns);
        }
        finally
        {
            fakeFactory.ConfigureNext = null;
            await vm.CloseAsync();
            vm.Dispose();
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Send_now_works_during_waiting_approval_and_falls_back_when_agent_ignores_cancel()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-acp-waiting-").FullName;
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);

        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(tempDir, "acp-gui-waiting");
        var opener = AppHost.Get<IItemOpener>();
        var registry = AppHost.Get<ITabContentRegistry>();

        var prompts = new List<string>();
        CancellationTokenSource? incomingCts = null;

        fakeFactory.ConfigureNext = session =>
        {
            session.PromptHandler = async (s, prompt, _, ct) =>
            {
                prompts.Add(prompt);
                if (prompt == "Ask question")
                {
                    incomingCts = new CancellationTokenSource();
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, incomingCts.Token);
                    await s.Callbacks.OnQuestionRequest!(new AcpQuestionRequest
                    {
                        RequestId = "q-wait",
                        Title = "Need input",
                        Questions = [new AcpQuestionItem { Id = "a", Prompt = "Proceed?", AllowFreeText = true }],
                    }, linked.Token);
                    // Simulate an agent returning JSON-RPC -32800 (RequestCancelled) on cancel.
                    throw new Octopus.Acp.Transport.AcpRpcException(-32800, "Request cancelled");
                }
                if (prompt == "Stuck agent")
                {
                    // Ignores CancelAsync(); only completes when local _turnCts force-cancels after CancelGraceTimeout!
                    await Task.Delay(Timeout.Infinite, ct);
                }
                s.Emit(new AcpTextDeltaEvent("assistant", $"Handled: {prompt}", null));
                return new AcpPromptResult { StopReason = "end_turn" };
            };
            session.CancelHandler = _ =>
            {
                incomingCts?.Cancel();
                return Task.CompletedTask;
            };
        };

        var item = opener.OpenAgent("opencode")!;
        var view = Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(project, item));
        var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);
        vm.CancelGraceTimeout = TimeSpan.FromMilliseconds(80);
        var window = new Window { Width = 920, Height = 640, Content = view };
        window.Show();
        try
        {
            await WaitUntilAsync(() => vm.State == AgentGuiState.Ready);

            // 1. Active turn enters Waiting (open QuestionCard).
            vm.InputText = "Ask question";
            var waitTurnTask = vm.SendPromptAsync();
            await WaitUntilAsync(() => vm.State == AgentGuiState.Waiting && vm.Timeline.OfType<QuestionCardItem>().Any());

            // Regular SendPromptAsync during Waiting MUST queue instead of firing an overlapping prompt!
            vm.InputText = "Queued while waiting";
            await vm.SendPromptAsync();
            Assert.Single(prompts);
            Assert.Single(vm.QueuedPrompts);

            // SendPromptNowAsync interrupts Waiting, cancels the question card, ignores -32800 cleanly, and runs urgent + queued!
            vm.InputText = "Interrupt waiting";
            await vm.SendPromptNowAsync();
            await waitTurnTask;

            var qCard = vm.Timeline.OfType<QuestionCardItem>().Single();
            Assert.True(qCard.IsResolved);
            Assert.Equal(["Ask question", "Interrupt waiting", "Queued while waiting"], prompts);
            Assert.All(vm.Timeline.OfType<AssistantTurnItem>(), t => Assert.False(t.HasError));

            // 2. Non-compliant agent ignores session/cancel -> grace timeout force-cancels and still sends urgent prompt!
            prompts.Clear();
            vm.InputText = "Stuck agent";
            var stuckTask = vm.SendPromptAsync();
            await WaitUntilAsync(() => vm.State == AgentGuiState.Working);

            vm.InputText = "Force interrupt";
            await vm.SendPromptNowAsync();
            await stuckTask;

            Assert.Equal(["Stuck agent", "Force interrupt"], prompts);
            Assert.Equal(AgentGuiState.Ready, vm.State);
            Assert.All(vm.Timeline.OfType<AssistantTurnItem>(), t => Assert.False(t.HasError));
        }
        finally
        {
            fakeFactory.ConfigureNext = null;
            await vm.CloseAsync();
            vm.Dispose();
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
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
