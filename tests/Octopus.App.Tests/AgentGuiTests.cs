using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.Acp.Client;
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
using Octopus.Ticker;
using TKCode.Arc.Motion;
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
    public IReadOnlyList<string>? LastAdditionalDirectories { get; private set; }
    public List<string> AuthenticatedMethods { get; } = [];
    public int LogoutCount { get; private set; }
    public List<string> DeletedSessions { get; } = [];
    public int CancelCount { get; private set; }

    public Func<FakeAcpSession, string, IReadOnlyList<AcpPromptAttachment>?, CancellationToken, Task<AcpPromptResult>>? PromptHandler { get; set; }
    public Func<FakeAcpSession, Task>? CancelHandler { get; set; }
    public Func<FakeAcpSession, string, CancellationToken, Task>? SetModelHandler { get; set; }
    public Func<FakeAcpSession, string, string, CancellationToken, Task>? SetConfigOptionHandler { get; set; }
    public Func<FakeAcpSession, string?, string?, CancellationToken, Task<AcpSessionListResult>>? ListSessionsHandler { get; set; }
    public Func<FakeAcpSession, string?, string?, CancellationToken, Task<AcpSessionStartResult>>? ForkSessionHandler { get; set; }
    public Func<string, AcpTerminalSnapshot?>? TerminalSnapshotHandler { get; set; }

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
        LastAdditionalDirectories = additionalDirectories;
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
        return SetModelHandler is not null ? SetModelHandler(this, modelId, cancellationToken) : Task.CompletedTask;
    }

    public Task SetConfigOptionAsync(string configId, string value, CancellationToken cancellationToken = default)
    {
        SetConfigs.Add((configId, value));
        return SetConfigOptionHandler is not null ? SetConfigOptionHandler(this, configId, value, cancellationToken) : Task.CompletedTask;
    }

    public Task SetBooleanConfigOptionAsync(string configId, bool value, CancellationToken cancellationToken = default)
    {
        SetBoolConfigs.Add((configId, value));
        return Task.CompletedTask;
    }

    public Task AuthenticateAsync(string methodId, IReadOnlyDictionary<string, string>? meta = null, CancellationToken cancellationToken = default)
    {
        AuthenticatedMethods.Add(methodId);
        return Task.CompletedTask;
    }

    public Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        LogoutCount++;
        return Task.CompletedTask;
    }

    public Task<AcpSessionListResult> ListSessionsAsync(string? workingDirectory = null, string? cursor = null, CancellationToken cancellationToken = default) =>
        ListSessionsHandler is not null
            ? ListSessionsHandler(this, workingDirectory, cursor, cancellationToken)
            : Task.FromResult(new AcpSessionListResult());

    public Task<AcpSessionStartResult> ForkSessionAsync(string? workingDirectory = null, string? cursorId = null, CancellationToken cancellationToken = default) =>
        ForkSessionHandler is not null
            ? ForkSessionHandler(this, workingDirectory, cursorId, cancellationToken)
            : Task.FromResult(new AcpSessionStartResult { Kind = "fork", SessionId = $"{SessionId}-fork" });

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        DeletedSessions.Add(sessionId);
        return Task.CompletedTask;
    }

    public AcpTerminalSnapshot? TryGetTerminalSnapshot(string terminalId) =>
        TerminalSnapshotHandler?.Invoke(terminalId);

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

            // Single unified work area folds prior intermediate text + all tools in chronological order with lazy expansion.
            var lastAssistant = vm.Timeline.OfType<AssistantTurnItem>().Last();
            lastAssistant.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "run1", Kind = "execute", Title = "dotnet build", Status = "in_progress" });
            lastAssistant.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "r1", Kind = "read", Title = "Read A.cs", Status = "completed" });
            lastAssistant.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "s1", Kind = "search", Title = "Grep Foo", Status = "completed" });
            lastAssistant.UpsertToolCall(new AcpToolCallUpdate
            {
                ToolCallId = "e1",
                Kind = "edit",
                Title = "Edit A.cs",
                Status = "completed",
                Diffs = [new AcpToolDiff { Path = "A.cs", OldText = "a", NewText = "b" }],
            });
            Assert.True(lastAssistant.HasWorkArea);
            Assert.Equal(5, lastAssistant.WorkStepCount); // 1 folded prior text + 4 tool calls
            Assert.Null(lastAssistant.ExpandedWorkSteps); // collapsed by default -> zero mounted step controls!
            var editTool = Assert.Single(lastAssistant.ToolCalls, t => t.ToolCallId == "e1");
            Assert.True(editTool.CanOpenDiff);
            Assert.Equal("+1 -1", editTool.DiffStatsText);

            // Manual expand materializes pure-text steps on demand and releases them when collapsed.
            lastAssistant.ToggleWorkExpandedCommand.Execute(null);
            Assert.True(lastAssistant.IsWorkExpanded);
            Assert.NotNull(lastAssistant.ExpandedWorkSteps);
            Assert.Equal(5, lastAssistant.ExpandedWorkSteps!.Count);
            lastAssistant.ToggleWorkExpandedCommand.Execute(null);
            Assert.False(lastAssistant.IsWorkExpanded);
            Assert.Null(lastAssistant.ExpandedWorkSteps);

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
    public async Task Assistant_turn_shows_3line_sliding_ticker_auto_collapses_on_complete_and_lazy_mounts_details()
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
                s.Emit(new AcpTextDeltaEvent("assistant", "All checks reviewed.", null));
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

            // 1) Unified work area tracks all 5 steps (1 thought + 4 commands) & highlights failed non-zero exit.
            Assert.True(turn.HasWorkArea);
            Assert.Equal(5, turn.WorkStepCount);
            Assert.True(turn.HasWorkFailures);
            Assert.False(string.IsNullOrWhiteSpace(turn.WorkHeaderCounts));

            // 2) Scheme C fixed 5-line sliding live ticker shows all 5 steps (4 completed + 1 running at bottom) with fading opacity.
            Assert.True(turn.ShowLiveTicker);
            Assert.NotNull(turn.LiveTicker);
            Assert.True(turn.LiveTicker!.Line0.IsVisible);
            Assert.True(turn.LiveTicker.Line1.IsVisible);
            Assert.True(turn.LiveTicker.Line2.IsVisible);
            Assert.True(turn.LiveTicker.Line3.IsVisible);
            Assert.True(turn.LiveTicker.Line4.IsVisible);
            Assert.True(turn.LiveTicker.Line0.RowOpacity < turn.LiveTicker.Line1.RowOpacity);
            Assert.True(turn.LiveTicker.Line1.RowOpacity < turn.LiveTicker.Line2.RowOpacity);
            Assert.True(turn.LiveTicker.Line2.RowOpacity < turn.LiveTicker.Line3.RowOpacity);
            Assert.True(turn.LiveTicker.Line3.RowOpacity < turn.LiveTicker.Line4.RowOpacity);
            Assert.True(turn.LiveTicker.Line4.IsRunning);
            Assert.Equal("dotnet test", turn.LiveTicker.Line4.Target);

            // Reassuring bottom live loading status with rotating i18n whimsical phrases is visible while streaming.
            Assert.True(turn.ShowLiveStatus);
            var firstPhrase = turn.LiveStatusText;
            Assert.False(string.IsNullOrWhiteSpace(firstPhrase));
            turn.AdvanceLiveStatus();
            Assert.NotEqual(firstPhrase, turn.LiveStatusText);

            // Waiting for user input hides the spinning ticker and bottom live status, and shows waiting badge.
            turn.SetWaitingForInput(true);
            Assert.False(turn.ShowLiveTicker);
            Assert.False(turn.ShowLiveStatus);
            Assert.Null(turn.LiveTicker);
            Assert.True(turn.ShowWaitingBadge);
            turn.SetWaitingForInput(false);
            Assert.True(turn.ShowLiveTicker);
            Assert.True(turn.ShowLiveStatus);
            Assert.NotNull(turn.LiveTicker);

            // 3) Manually expanding work area hides ticker and lazy-mounts pure-text steps; expanding thought caps height <= 240.
            turn.ToggleWorkExpandedCommand.Execute(null);
            Assert.True(turn.IsWorkExpanded);
            Assert.False(turn.ShowLiveTicker);
            Assert.Null(turn.LiveTicker);
            var thoughtStep = Assert.Single(turn.ExpandedWorkSteps!, s => s.IsThought);
            thoughtStep.ToggleExpandedCommand.Execute(null);
            Assert.True(thoughtStep.IsExpanded);
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var thoughtScroll = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view)
                .OfType<TKCode.Arc.ScrollArea>()
                .FirstOrDefault(s => s.Classes.Contains("thoughtScroll"));
            Assert.NotNull(thoughtScroll);
            Assert.InRange(thoughtScroll!.MaxHeight, 100, 240);

            // 4) Completing the turn auto-collapses the entire work area and unmounts ticker & expanded step controls!
            turnGate.TrySetResult(true);
            await sendTask;
            Assert.False(turn.IsStreaming);
            Assert.False(turn.IsWorkExpanded);
            Assert.Null(turn.ExpandedWorkSteps);
            Assert.False(turn.ShowLiveTicker);
            Assert.False(turn.ShowLiveStatus);
            Assert.Null(turn.LiveTicker);
            Assert.Contains("All checks reviewed.", turn.MarkdownText);
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
    public void Live_ticker_slides_rows_upward_with_spring_motion_and_respects_reduced_motion()
    {
        var turn = new AssistantTurnItem("codex", "Codex");
        var host = new AgentGuiTabView();
        var tickerHost = new ContentControl { Content = turn.LiveTicker };
        // Mount inside AgentGuiTabView's DataTemplates so the real desktop ticker template & WorkTickerViewport run.
        var container = new StackPanel();
        container.DataTemplates.AddRange(host.DataTemplates);
        container.Children.Add(tickerHost);

        var window = new Window { Width = 640, Height = 300, Content = container };
        window.Show();
        try
        {
            static bool StepUntil(Func<bool> done, Action? onFrame = null, int milliseconds = 3000)
            {
                var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
                while (DateTime.UtcNow < end)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    onFrame?.Invoke();
                    if (done()) return true;
                    Thread.Sleep(16);
                }
                return false;
            }

            // Start with 1 step (Preparing -> Step 1) and settle.
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "t1", Kind = "edit", Title = "File A.cs", Status = "completed" });
            tickerHost.Content = turn.LiveTicker;
            Frames.Pump();
            window.UpdateLayout();

            var viewport = tickerHost.GetVisualDescendants().OfType<WorkTickerViewport>().Single();
            Assert.True(StepUntil(() => !viewport.IsRunning));
            Assert.True(viewport.MeasuredRowHeight > 8);
            var rowH = viewport.MeasuredRowHeight;
            var stepY = rowH + viewport.Spacing;
            Assert.Equal(rowH, viewport.Bounds.Height, 1.0);

            // Grow 1 -> 2 -> 3 -> 4 -> 5 rows smoothly.
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "t2", Kind = "edit", Title = "File B.cs", Status = "completed" });
            var growthHeights = new List<double>();
            Assert.True(StepUntil(() => !viewport.IsRunning, () => growthHeights.Add(viewport.Bounds.Height)));
            var twoRowHeight = 2 * rowH + viewport.Spacing;
            Assert.Contains(growthHeights, h => h > rowH + 0.5 && h < twoRowHeight - 0.5);
            Assert.Equal(twoRowHeight, viewport.Bounds.Height, 1.0);

            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "t3", Kind = "edit", Title = "File C.cs", Status = "completed" });
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "t4", Kind = "edit", Title = "File D.cs", Status = "completed" });
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "t5", Kind = "edit", Title = "File E.cs", Status = "in_progress" });
            Assert.True(StepUntil(() => !viewport.IsRunning));
            var fiveRowHeight = 5 * rowH + 4 * viewport.Spacing;
            Assert.Equal(fiveRowHeight, viewport.Bounds.Height, 1.0);
            Assert.Equal(0.0, viewport.GetSlotOffsetY(0), 0.5);
            Assert.Equal(stepY, viewport.GetSlotOffsetY(1), 0.5);
            Assert.Equal(2 * stepY, viewport.GetSlotOffsetY(2), 0.5);
            Assert.Equal(3 * stepY, viewport.GetSlotOffsetY(3), 0.5);
            Assert.Equal(4 * stepY, viewport.GetSlotOffsetY(4), 0.5);

            // In-place update of t5 must NOT trigger a slide animation.
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "t5", Kind = "edit", Title = "File E.cs", Status = "completed" });
            Assert.False(viewport.IsRunning);
            Assert.Equal(0.0, viewport.RemainingShift, 0.01);

            // 5 -> 5 upward shift: t6 enters at bottom, t1 exits at top with smooth upward slide and constant height.
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "t6", Kind = "edit", Title = "File F.cs", Status = "in_progress" });
            Assert.True(viewport.IsRunning);
            Assert.True(viewport.IsExitVisible(0));
            Assert.Equal("File A.cs", turn.LiveTicker!.Exit0.Target);

            var line0Ys = new List<double>();
            var exit0Ys = new List<double>();
            var heights = new List<double>();
            Assert.True(StepUntil(
                () => !viewport.IsRunning,
                () =>
                {
                    line0Ys.Add(viewport.GetSlotOffsetY(0));
                    if (viewport.IsExitVisible(0)) exit0Ys.Add(viewport.GetExitOffsetY(0));
                    heights.Add(viewport.Bounds.Height);
                }));

            Assert.True(line0Ys.Count >= 3, $"expected smooth upward frames, got {line0Ys.Count}");
            Assert.Contains(line0Ys, y => y > 0.5 && y < stepY - 0.5);
            Assert.Contains(exit0Ys, y => y < -0.5 && y > -stepY + 0.5);
            Assert.All(heights, h => Assert.Equal(fiveRowHeight, h, 1.0));

            // Settled: exiting ghost hidden and slots rest at exact positions & 5-row opacities.
            Assert.False(viewport.IsExitVisible(0));
            Assert.False(turn.LiveTicker.Exit0.IsVisible);
            Assert.Equal(0.0, viewport.GetSlotOffsetY(0), 0.5);
            Assert.Equal(stepY, viewport.GetSlotOffsetY(1), 0.5);
            Assert.Equal(2 * stepY, viewport.GetSlotOffsetY(2), 0.5);
            Assert.Equal(3 * stepY, viewport.GetSlotOffsetY(3), 0.5);
            Assert.Equal(4 * stepY, viewport.GetSlotOffsetY(4), 0.5);
            Assert.Equal(0.28, viewport.GetSlotOpacity(0), 0.02);
            Assert.Equal(0.40, viewport.GetSlotOpacity(1), 0.02);
            Assert.Equal(0.56, viewport.GetSlotOpacity(2), 0.02);
            Assert.Equal(0.76, viewport.GetSlotOpacity(3), 0.02);
            Assert.Equal(1.00, viewport.GetSlotOpacity(4), 0.02);

            // The ArcSpinner on the newly shifted bottom row MUST actually spin (EffectiveViewport refreshed after slide)!
            var activeSpinner = viewport.GetVisualDescendants()
                .OfType<TKCode.Arc.Primitives.ArcSpinner>()
                .First(s => s.IsVisible && s.IsEffectivelyVisible);
            var startAngle = activeSpinner.Angle;
            Assert.True(StepUntil(() => activeSpinner.IsSpinning && Math.Abs(activeSpinner.Angle - startAngle) > 1.0, milliseconds: 1500),
                $"Expected bottom row ArcSpinner to spin after 5->5 shift (IsSpinning={activeSpinner.IsSpinning}, start={startAngle}, now={activeSpinner.Angle}).");

            // Active thought updates inside the bottom ticker slot animate smoothly on prefix continuation AND line switch.
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "t6", Kind = "edit", Title = "File F.cs", Status = "completed" });
            turn.AppendThought("Initial observation: checking tokens");
            Assert.True(StepUntil(() => !viewport.IsRunning));

            var thoughtTarget = viewport.GetVisualDescendants()
                .OfType<TickerTargetText>()
                .Last(t => t.AnimateUpdates);
            Assert.Equal("Initial observation: checking tokens", thoughtTarget.CurrentDisplayText);

            // 1) Prefix continuation triggers smooth streaming tail reveal.
            turn.AppendThought(" and layout viewport");
            Assert.True(thoughtTarget.IsRevealAnimating);
            Assert.True(StepUntil(() => !thoughtTarget.IsRevealAnimating));

            // 2) Newline switch triggers smooth vertical roll + crossfade.
            turn.AppendThought("\nNext step: verify spinner rotation.");
            Assert.True(thoughtTarget.IsSwapAnimating);
            Assert.True(StepUntil(() => !thoughtTarget.IsSwapAnimating));
            Assert.Equal("Next step: verify spinner rotation.", thoughtTarget.CurrentDisplayText);

            // Mid-flight rapid shift: t7 starts sliding, then t8 arrives mid-flight without jumping backwards.
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "t7", Kind = "edit", Title = "File G.cs", Status = "in_progress" });
            Assert.True(StepUntil(() => viewport.RemainingShift < 0.75 && viewport.RemainingShift > 0.15));
            var midVisualYOfSlot1 = viewport.GetSlotOffsetY(1);

            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "t7", Kind = "edit", Title = "File G.cs", Status = "completed" });
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "t8", Kind = "edit", Title = "File H.cs", Status = "in_progress" });
            // Slot 1 shifted to slot 0; its visual Y on screen stays continuous!
            Assert.Equal(midVisualYOfSlot1, viewport.GetSlotOffsetY(0), 1.5);
            Assert.True(StepUntil(() => !viewport.IsRunning));
            Assert.Equal(0.0, viewport.GetSlotOffsetY(0), 0.5);

            // Motion off: shifts and thought updates settle immediately without waiting for spring frames.
            ArcMotion.SetLevel(window, ArcMotionLevel.Off);
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "t8", Kind = "edit", Title = "File H.cs", Status = "completed" });
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "t9", Kind = "edit", Title = "File I.cs", Status = "in_progress" });
            Assert.False(viewport.IsRunning);
            Assert.False(viewport.IsExitVisible(0));
            Assert.Equal(0.0, viewport.GetSlotOffsetY(0), 0.5);
            Assert.Equal("File I.cs", turn.LiveTicker.Line4.Target);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            turn.Complete();
        }
    }

    [AvaloniaFact]
    public void Assistant_turn_records_independent_thought_steps_across_tools_and_message_ids_inside_single_work_area()
    {
        var turn = new AssistantTurnItem("agy", "Antigravity CLI");
        try
        {
            // Step 1: initial thought split across 2 streaming deltas -> single thought step #1
            turn.AppendThought("First part ");
            turn.AppendThought("of thought 1.");
            var t1 = Assert.Single(turn.WorkSteps);
            Assert.True(t1.IsThought);
            Assert.Equal("First part of thought 1.", t1.ThoughtText);

            // Step 2: tool burst arrives -> finishes thought #1 and appends tool steps to the same work area
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "tc1", Kind = "execute", Title = "ls -la", Command = "ls -la", Status = "in_progress" });
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "tc2", Kind = "execute", Title = "ls src", Command = "ls src", Status = "completed", ExitCode = 0 });
            Assert.Equal(3, turn.WorkStepCount);
            Assert.False(t1.IsRunning);

            // Step 3: subsequent reasoning after tools -> creates an independent thought step #2 (never merged into #1!)
            turn.AppendThought("Second thought after tools.", messageId: "m2");
            Assert.Equal(4, turn.WorkStepCount);
            var t2 = turn.WorkSteps[3];
            Assert.True(t2.IsThought);
            Assert.Equal("First part of thought 1.", t1.ThoughtText);
            Assert.Equal("Second thought after tools.", t2.ThoughtText);

            // Late completion update for tc1 updates in place without duplicating steps.
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "tc1", Status = "completed", ExitCode = 0 });
            Assert.Equal(4, turn.WorkStepCount);

            // New reasoning with a different messageId -> independent thought step #3
            turn.AppendThought("Third thought with new messageId.", messageId: "m3");
            Assert.Equal(5, turn.WorkStepCount);
            var t3 = turn.WorkSteps[4];
            Assert.Equal("Third thought with new messageId.", t3.ThoughtText);

            // Independent expand/collapse state per step (details lazy-mounted only when expanded)
            Assert.Null(t2.ExpandedDetail);
            t2.ToggleExpandedCommand.Execute(null);
            Assert.False(t1.IsExpanded);
            Assert.True(t2.IsExpanded);
            Assert.Same(t2, t2.ExpandedDetail);
            Assert.False(t3.IsExpanded);

            // Step 4: final markdown response collapses live ticker immediately and populates single MarkdownText
            turn.AppendMessage("Final answer.");
            Assert.False(turn.ShowLiveTicker);
            Assert.Equal("Final answer.", turn.MarkdownText);
            Assert.NotNull(turn.ResponseMarkdown);
        }
        finally
        {
            turn.Complete();
        }
    }

    [AvaloniaFact]
    public void Assistant_turn_folds_intermediate_text_into_work_area_without_counting_text_and_keeps_only_final_text_outside()
    {
        var turn = new AssistantTurnItem("codex", "Codex");
        try
        {
            // Intermediate text 1 -> Tool 1 -> Intermediate text 2 -> Tool 2 -> Final output text
            turn.AppendMessage("Let me inspect the repository first.");
            Assert.Equal("Let me inspect the repository first.", turn.MarkdownText);

            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "r1", Kind = "read", Title = "Read Program.cs", Status = "in_progress" });
            Assert.False(turn.HasMarkdown);
            Assert.Equal("", turn.MarkdownText);
            Assert.Equal(2, turn.WorkSteps.Count);
            Assert.True(turn.WorkSteps[0].IsText);
            Assert.Equal("Let me inspect the repository first.", turn.WorkSteps[0].NoteText);
            Assert.Equal("", turn.WorkSteps[0].Verb);
            Assert.True(turn.WorkSteps[1].IsTool);
            // Live ticker skips folded text steps and shows only actual work operations.
            Assert.NotNull(turn.LiveTicker);
            Assert.True(turn.LiveTicker!.Line0.IsVisible);
            Assert.Equal("Read Program.cs", turn.LiveTicker.Line0.Target);
            Assert.False(turn.LiveTicker.Line1.IsVisible);

            // In-place update of existing tool does not duplicate or alter folded text.
            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "r1", Status = "completed" });

            turn.AppendMessage("Found the issue, updating now.");
            Assert.Equal("Found the issue, updating now.", turn.MarkdownText);

            turn.UpsertToolCall(new AcpToolCallUpdate { ToolCallId = "e1", Kind = "edit", Title = "Edit Program.cs", Status = "completed" });
            Assert.False(turn.HasMarkdown);
            Assert.Equal(4, turn.WorkSteps.Count);
            Assert.True(turn.WorkSteps[2].IsText);
            Assert.Equal("Found the issue, updating now.", turn.WorkSteps[2].NoteText);
            Assert.True(turn.WorkSteps[3].IsTool);

            turn.AppendMessage("All fixed and verified.");
            turn.Complete();

            // Final display: collapsed work area (counting ONLY the 2 tools, never text) + final output text underneath.
            Assert.True(turn.HasWorkArea);
            Assert.False(turn.IsWorkExpanded);
            Assert.Equal("All fixed and verified.", turn.MarkdownText);
            Assert.Equal("All fixed and verified.", turn.CopyReplyValue);
            Assert.Contains("2", turn.WorkHeaderTitle);
            Assert.DoesNotContain("4", turn.WorkHeaderTitle);
            Assert.Equal(4, turn.WorkSteps.Count);

            // Expanding work area reveals folded text steps in chronological order with lazy detail expansion.
            turn.ToggleWorkExpandedCommand.Execute(null);
            Assert.NotNull(turn.ExpandedWorkSteps);
            Assert.Equal([WorkStepKind.Text, WorkStepKind.Tool, WorkStepKind.Text, WorkStepKind.Tool],
                turn.ExpandedWorkSteps!.Select(s => s.StepKind));
            Assert.Null(turn.ExpandedWorkSteps[0].ExpandedDetail);
            turn.ExpandedWorkSteps[0].ToggleExpandedCommand.Execute(null);
            Assert.Same(turn.ExpandedWorkSteps[0], turn.ExpandedWorkSteps[0].ExpandedDetail);
        }
        finally
        {
            if (turn.IsStreaming) turn.Complete();
        }
    }

    [AvaloniaFact]
    public void User_turn_places_action_buttons_below_the_bubble()
    {
        var userTurn = new UserTurnItem("1.默认打开\n2. 一并实现", null, _ => { });
        var host = new AgentGuiTabView();
        var presenter = new ContentControl { Content = userTurn };
        var container = new StackPanel();
        container.DataTemplates.AddRange(host.DataTemplates);
        container.Children.Add(presenter);

        var window = new Window { Width = 720, Height = 300, Content = container };
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var bubble = presenter.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("userBubble"));
            var actions = presenter.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Classes.Contains("turnActions"));

            // Actions must be a sibling below the bubble, NOT nested inside the bubble.
            Assert.DoesNotContain(actions, bubble.GetVisualDescendants());
            var bubbleBottom = Avalonia.VisualExtensions.TranslatePoint(bubble, new Avalonia.Point(0, bubble.Bounds.Height), window)!.Value.Y;
            var actionsTop = Avalonia.VisualExtensions.TranslatePoint(actions, new Avalonia.Point(0, 0), window)!.Value.Y;
            Assert.True(actionsTop >= bubbleBottom - 0.5, $"Expected actionsTop ({actionsTop}) >= bubbleBottom ({bubbleBottom})");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
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
            // Docked Activity Tray surfaces workflow tasks, SubAgents & Background Bash without polluting the timeline
            Assert.True(vm.HasWorkflowTasks);
            Assert.Equal(2, vm.WorkflowTasks.Count);
            Assert.Contains("1/2", vm.ActivityTraySummary);
            Assert.True(vm.HasActivityTray);
            Assert.True(vm.IsMultiActivityTray);
            Assert.False(vm.IsSingleBackgroundShell);
            vm.ToggleActivityTrayCommand.Execute(null);
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsActivityTrayExpanded);

            // SubAgents + nested child steps + sidebar sync
            Assert.Equal(2, vm.Subagents.Count);
            var runningSub = vm.Subagents[1];
            Assert.Single(runningSub.Steps);
            Assert.Equal("Edit", runningSub.Steps[0].ToolName);
            Assert.Equal(2, subagentService.Of(item.Id).Count);

            // Clicking a SubAgent opens Slide-Over Drawer (no inline swimlane card in timeline)
            vm.SelectSubagentCommand.Execute(runningSub);
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(vm.HasSelectedSubagent);
            Assert.Same(runningSub, vm.SelectedSubagent);

            // Background Bash tracked across turn with detected port :5173 & inline expandable live output
            var bgShell = Assert.Single(vm.BackgroundShells);
            Assert.Equal(":5173", bgShell.DetectedPort);
            Assert.True(bgShell.IsRunning);
            Assert.True(await vm.HasRunningChildAsync());
            bgShell.ToggleLogsCommand.Execute(null);
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(bgShell.IsLogsExpanded);
            Assert.True(vm.HasExpandedBackgroundShellLogs);
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

            // Escape / collapse closes inline background shell logs cleanly
            vm.CollapseExpandedShellLogs();
            Assert.False(bgShell.IsLogsExpanded);
            Assert.False(vm.HasExpandedBackgroundShellLogs);
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

            // Only a single SendButton is rendered in the composer (no separate Stop / SendNow buttons).
            Assert.Null(view.FindControl<TKCode.Arc.Button>("StopButton"));
            Assert.Null(view.FindControl<TKCode.Arc.Button>("SendNowButton"));
            var sendButton = view.FindControl<TKCode.Arc.Button>("SendButton");
            var loadingVisual = view.FindControl<Avalonia.Controls.Panel>("SendButtonLoadingVisual");
            var sendIcon = view.FindControl<TKCode.Arc.Icons.Lucide.LucideIcon>("SendButtonSendIcon");
            Assert.NotNull(sendButton);
            Assert.NotNull(loadingVisual);
            Assert.NotNull(sendIcon);

            // Idle + empty composer: shows send icon (disabled).
            Assert.False(vm.ShowComposerLoading);
            Assert.False(loadingVisual!.IsVisible);
            Assert.True(sendIcon!.IsVisible);
            Assert.False(vm.ComposerActionCommand.CanExecute(null));

            // 1. Start a holding turn so the agent stays busy while we manipulate the queue tray.
            vm.InputText = "Hold 1";
            Assert.False(vm.ShowComposerLoading);
            Assert.True(vm.ComposerActionCommand.CanExecute(null));
            var turn1Task = vm.ComposerActionCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => vm.State == AgentGuiState.Working);

            // Active turn + empty composer: single button switches to loading state (still clickable to stop).
            Assert.True(sendButton!.IsVisible);
            Assert.True(vm.ShowComposerLoading);
            Assert.True(loadingVisual.IsVisible);
            Assert.False(sendIcon.IsVisible);
            Assert.True(vm.ComposerActionCommand.CanExecute(null));
            Assert.False(vm.CanSend);
            Assert.False(vm.CanSendNow);

            // Queue 3 prompts (first one carries an attachment). Typing/attaching switches the single button
            // back to send state and clicking it enqueues the prompt by default.
            vm.AddAttachment(new AcpPromptAttachment { Kind = "file", Path = Path.Combine(tempDir, "README.md"), DisplayName = "README.md" });
            Assert.False(vm.ShowComposerLoading);
            Assert.False(loadingVisual.IsVisible);
            Assert.True(sendIcon.IsVisible);
            vm.InputText = "Task A\nsecond line";
            Assert.True(vm.CanSend);
            Assert.True(vm.CanSendNow);
            Assert.True(vm.SendPromptCommand.CanExecute(null));
            Assert.True(vm.SendPromptNowCommand.CanExecute(null));
            Assert.True(vm.ComposerActionCommand.CanExecute(null));
            await vm.ComposerActionCommand.ExecuteAsync(null);

            // Input cleared after queueing -> single button returns to loading state.
            Assert.True(vm.ShowComposerLoading);
            Assert.True(loadingVisual.IsVisible);
            Assert.False(sendIcon.IsVisible);

            vm.InputText = "Task B";
            await vm.ComposerActionCommand.ExecuteAsync(null);
            vm.InputText = "Task C";
            await vm.ComposerActionCommand.ExecuteAsync(null);

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

            // Clicking the single button while composer is empty (loading state) cancels the active turn and pauses queue draining.
            Assert.True(vm.ShowComposerLoading);
            await vm.ComposerActionCommand.ExecuteAsync(null);
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

    [AvaloniaFact, FreshApp]
    public async Task Acp_gui_tab_connects_lazily_persists_session_id_and_respects_resume_setting()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-acp-persist-").FullName;
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);

        var store = AppHost.Get<IProjectStore>();
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(tempDir, "acp-persist");
        var opener = AppHost.Get<IItemOpener>();
        var registry = AppHost.Get<ITabContentRegistry>();

        var firstResumeArg = "unset";
        fakeFactory.ConfigureNext = session =>
        {
            session.SessionId = "acp-persisted-42";
            session.StartHandler = (_, resumeId) => firstResumeArg = resumeId;
        };

        var item = opener.OpenAgent("opencode")!;
        var view = Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(project, item));
        var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);

        // Before attaching to visual tree, no ACP session should be spawned (lazy start for background tabs).
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Empty(fakeFactory.Created);

        var window = new Window { Width = 920, Height = 640, Content = view };
        window.Show();
        try
        {
            await WaitUntilAsync(() => vm.State == AgentGuiState.Ready);
            Assert.Single(fakeFactory.Created);
            Assert.Null(firstResumeArg);
            Assert.Equal("acp-persisted-42", item.AcpSessionId);

            // Flushing persists the agent item and its ACP session id.
            await projects.FlushAsync();
            var saved = await store.LoadAsync();
            var savedItem = Assert.Single(saved.Projects.Single(p => p.Id == project.Id).Items);
            Assert.Equal("acp-persisted-42", savedItem.AcpSessionId);
        }
        finally
        {
            fakeFactory.ConfigureNext = null;
            await vm.CloseAsync();
            vm.Dispose();
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        // Simulate restart: reload project from store, keep ResumeAgentsOnRestart = true -> passes saved session id.
        await registry.ReleaseAllAsync();
        await projects.LoadAsync();
        var reloadedProject = projects.Projects.Single(p => p.Id == project.Id);
        var restoredItem = Assert.Single(reloadedProject.Items);
        Assert.True(projects.WasRestored(restoredItem.Id));

        var resumedArg = "unset";
        fakeFactory.ConfigureNext = session =>
        {
            session.SessionId = "acp-persisted-42";
            session.StartHandler = (_, resumeId) => resumedArg = resumeId;
        };
        var restoredView = Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(reloadedProject, restoredItem));
        var restoredVm = Assert.IsType<AgentGuiTabViewModel>(restoredView.DataContext);
        var window2 = new Window { Width = 920, Height = 640, Content = restoredView };
        window2.Show();
        try
        {
            await WaitUntilAsync(() => restoredVm.State == AgentGuiState.Ready);
            Assert.Equal("acp-persisted-42", resumedArg);
        }
        finally
        {
            fakeFactory.ConfigureNext = null;
            await restoredVm.CloseAsync();
            restoredVm.Dispose();
            window2.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        // Simulate restart with ResumeAgentsOnRestart = false -> restored tab starts fresh (resumeSessionId is null).
        await registry.ReleaseAllAsync();
        settings.Update(s => s.Terminal.ResumeAgentsOnRestart = false);
        await projects.LoadAsync();
        var reloadedProject2 = projects.Projects.Single(p => p.Id == project.Id);
        var restoredItem2 = Assert.Single(reloadedProject2.Items);

        var freshArg = "unset";
        fakeFactory.ConfigureNext = session =>
        {
            session.SessionId = "acp-fresh-99";
            session.StartHandler = (_, resumeId) => freshArg = resumeId;
        };
        var freshView = Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(reloadedProject2, restoredItem2));
        var freshVm = Assert.IsType<AgentGuiTabViewModel>(freshView.DataContext);
        var window3 = new Window { Width = 920, Height = 640, Content = freshView };
        window3.Show();
        try
        {
            await WaitUntilAsync(() => freshVm.State == AgentGuiState.Ready);
            Assert.Null(freshArg);
            Assert.Equal("acp-fresh-99", restoredItem2.AcpSessionId);
        }
        finally
        {
            fakeFactory.ConfigureNext = null;
            settings.Update(s => s.Terminal.ResumeAgentsOnRestart = true);
            await freshVm.CloseAsync();
            freshVm.Dispose();
            window3.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Antigravity_gui_tracks_async_invoke_subagent_update_steps_and_live_transcript_progress()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-agy-gui-sub-").FullName;
        var agyDataDir = Path.Combine(tempDir, "agy-data");
        var parentSessionId = "agy-parent-1";
        var sub1ConvId = "agy-child-1";
        var sub2ConvId = "agy-child-2";

        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);

        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(tempDir, "agy-subagents");
        var opener = AppHost.Get<IItemOpener>();
        var registry = AppHost.Get<ITabContentRegistry>();
        var subagentService = AppHost.Get<ISubagentService>();
        var agyDialect = new AcpDialectRegistry().Resolve("antigravity");

        var releasePrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launchesEmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeAcpSession? capturedSession = null;

        fakeFactory.ConfigureNext = session =>
        {
            capturedSession = session;
            session.SessionId = parentSessionId;
            session.PromptHandler = async (s, _, _, ct) =>
            {
                using var stepsDoc = JsonDocument.Parse("""
                    {
                      "toolCallId": "call_steps",
                      "name": "update_steps",
                      "title": "Run update_steps",
                      "status": "completed",
                      "rawInput": {
                        "steps": [
                          { "status": "done", "title": "Inspect worktrees" },
                          { "status": "in_progress", "title": "Finish host bridge" }
                        ]
                      }
                    }
                    """);
                s.Emit(new AcpToolCallEvent(AcpProtocolParser.ParseToolCallUpdate(stepsDoc.RootElement, agyDialect)));

                using var sub1Doc = JsonDocument.Parse("""
                    {
                      "toolCallId": "call_sub_1",
                      "name": "invoke_subagent",
                      "title": "Run invoke_subagent",
                      "status": "completed",
                      "rawInput": {
                        "TypeName": "self",
                        "Role": "Host ACP Bridge Engineer",
                        "Model": "inherit",
                        "Workspace": "share"
                      }
                    }
                    """);
                s.Emit(new AcpToolCallEvent(AcpProtocolParser.ParseToolCallUpdate(sub1Doc.RootElement, agyDialect)));

                using var sub2Doc = JsonDocument.Parse("""
                    {
                      "toolCallId": "call_sub_2",
                      "name": "invoke_subagent",
                      "title": "Run invoke_subagent",
                      "status": "completed",
                      "rawInput": {
                        "TypeName": "research",
                        "Role": "Mobile UI Engineer",
                        "Model": "flash_lite"
                      }
                    }
                    """);
                s.Emit(new AcpToolCallEvent(AcpProtocolParser.ParseToolCallUpdate(sub2Doc.RootElement, agyDialect)));
                s.Emit(new AcpTextDeltaEvent("assistant", "已启动 2 个并行子代理。", "m1"));

                launchesEmitted.TrySetResult();
                await releasePrompt.Task.WaitAsync(ct);
                return new AcpPromptResult { StopReason = "end_turn" };
            };
        };

        var item = opener.OpenAgent("agy")!;
        var view = Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(project, item));
        var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);
        vm.AgyAppDataDirOverride = agyDataDir;
        var window = new Window { Width = 1100, Height = 760, Content = view };
        window.Show();
        try
        {
            await WaitUntilAsync(() => vm.State == AgentGuiState.Ready);
            vm.InputText = "Continue worktrees";
            var sendTask = vm.SendPromptAsync();
            await launchesEmitted.Task;
            await WaitUntilAsync(() => vm.Subagents.Count == 2);

            // Workflow tasks from update_steps are visible, and both async invoke_subagent calls remain Running
            // even though their ACP tool_call status already reported "completed".
            Assert.Equal(2, vm.WorkflowTasks.Count);
            Assert.True(vm.WorkflowTasks[0].IsCompleted);
            Assert.True(vm.WorkflowTasks[1].IsInProgress);
            Assert.True(vm.HasActivityTray);
            Assert.True(vm.HasSubagents);
            Assert.True(vm.HasRunningSubagents);
            Assert.All(vm.Subagents, s => Assert.True(s.IsRunning));
            Assert.Equal("Host ACP Bridge Engineer", vm.Subagents[0].DisplayTitle);
            Assert.Equal("share", vm.Subagents[0].WorktreeIsolation);
            Assert.Equal("flash_lite", vm.Subagents[1].Model);
            Assert.Equal(2, subagentService.Of(item.Id).Count);

            // Simulate Antigravity writing parent spawn records and subagent #1's own tool calls to disk.
            var parentTranscript = Octopus.Agents.Subagents.AgySubagentTranscript.TranscriptPathFor(parentSessionId, agyDataDir);
            var sub1Transcript = Octopus.Agents.Subagents.AgySubagentTranscript.TranscriptPathFor(sub1ConvId, agyDataDir);
            var sub2Transcript = Octopus.Agents.Subagents.AgySubagentTranscript.TranscriptPathFor(sub2ConvId, agyDataDir);
            Directory.CreateDirectory(Path.GetDirectoryName(parentTranscript)!);
            Directory.CreateDirectory(Path.GetDirectoryName(sub1Transcript)!);
            Directory.CreateDirectory(Path.GetDirectoryName(sub2Transcript)!);

            File.WriteAllLines(parentTranscript,
            [
                JsonSerializer.Serialize(new
                {
                    type = "GENERIC",
                    status = "DONE",
                    content = $"Created the following subagent:\n{{\n  \"conversationId\": \"{sub1ConvId}\",\n  \"logAbsoluteUri\": \"{new Uri(sub1Transcript).AbsoluteUri}\"\n}}",
                }),
                JsonSerializer.Serialize(new
                {
                    type = "GENERIC",
                    status = "DONE",
                    content = $"Created the following subagent:\n{{\n  \"conversationId\": \"{sub2ConvId}\",\n  \"logAbsoluteUri\": \"{new Uri(sub2Transcript).AbsoluteUri}\"\n}}",
                }),
            ]);
            File.WriteAllLines(sub1Transcript,
            [
                """{"type":"PLANNER_RESPONSE","status":"DONE","tool_calls":[{"name":"read_file","args":{"Path":"\"/repo/HostConversations.cs\""}},{"name":"run_command","args":{"CommandLine":"\"dotnet test\"","toolSummary":"\"Run RemoteConversationTests\""}}],"content":""}""",
                """{"type":"GENERIC","status":"DONE","content":"ok"}""",
            ]);

            await vm.PollAgySubagentsNowAsync();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Equal(sub1ConvId, vm.Subagents[0].ConversationId);
            Assert.Equal(2, vm.Subagents[0].Steps.Count);
            Assert.True(vm.Subagents[0].Steps[0].IsCompleted);
            Assert.True(vm.Subagents[0].Steps[1].IsRunning);
            Assert.Contains("Run RemoteConversationTests", vm.Subagents[0].LastAction);

            // Parent receives subagent #1's completion message, and calls manage_subagents kill on subagent #2.
            File.AppendAllLines(parentTranscript,
            [
                JsonSerializer.Serialize(new
                {
                    type = "SYSTEM_MESSAGE",
                    status = "DONE",
                    content = $"<SYSTEM_MESSAGE>\n[Message] timestamp=2026-11-24T09:33:37Z sender={sub1ConvId} priority=MESSAGE_PRIORITY_HIGH content=Host bridge complete and 60/60 tests passed.\n</SYSTEM_MESSAGE>",
                }),
            ]);
            await vm.PollAgySubagentsNowAsync();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.True(vm.Subagents[0].IsDone);
            Assert.Contains("60/60 tests passed", vm.Subagents[0].Summary);
            Assert.All(vm.Subagents[0].Steps, st => Assert.True(st.IsCompleted));

            using (var killDoc = JsonDocument.Parse($$"""
                {
                  "toolCallId": "call_kill_2",
                  "name": "manage_subagents",
                  "status": "completed",
                  "rawInput": { "Action": "kill", "ConversationIds": ["{{sub2ConvId}}"] }
                }
                """))
            {
                capturedSession!.Emit(new AcpToolCallEvent(AcpProtocolParser.ParseToolCallUpdate(killDoc.RootElement, agyDialect)));
            }
            await WaitUntilAsync(() => vm.Subagents[1].IsInterrupted);
            Assert.False(vm.HasRunningSubagents);

            releasePrompt.TrySetResult();
            await sendTask;
        }
        finally
        {
            fakeFactory.ConfigureNext = null;
            releasePrompt.TrySetResult();
            await vm.CloseAsync();
            vm.Dispose();
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Performance_optimizations_strip_base64_bound_tool_outputs_freeze_markdown_and_lazy_mount_collapse()
    {
        // 1. UserTurnItem strips Base64Data from sent image attachments so payloads are not pinned in history.
        var imgAtt = new AcpPromptAttachment
        {
            Kind = "image",
            Path = "/tmp/shot.png",
            DisplayName = "shot.png",
            MimeType = "image/png",
            Base64Data = new string('B', 64 * 1024),
        };
        var userTurn = new UserTurnItem("Look at this", [imgAtt]);
        var chip = Assert.Single(userTurn.Attachments);
        Assert.Equal("shot.png", chip.Label);
        Assert.Null(chip.Attachment.Base64Data);
        // Original attachment passed to PromptAsync remains intact.
        Assert.NotNull(imgAtt.Base64Data);

        // 2. ToolCallItemViewModel truncates oversized stdout/output while preserving both head and tail.
        var tool = new ToolCallItemViewModel("tc-huge");
        var hugeStdout = "HEAD_MARKER\n" + new string('x', 80_000) + "\nTAIL_MARKER";
        tool.Apply(new AcpToolCallUpdate { ToolCallId = "tc-huge", Kind = "execute", Status = "completed", Stdout = hugeStdout });
        Assert.NotNull(tool.Stdout);
        Assert.True(tool.Stdout!.Length <= ToolCallItemViewModel.MaxOutputChars + 64);
        Assert.StartsWith("HEAD_MARKER", tool.Stdout);
        Assert.EndsWith("TAIL_MARKER", tool.Stdout);

        // 3. MarkdownBlockViewModel freezes completed paragraphs without splitting open fenced code blocks.
        var md = new MarkdownBlockViewModel("");
        var para1 = "Paragraph one " + new string('a', 300);
        var fenced = "```csharp\nvar x = 1;\n\nvar y = 2;\n```";
        var para2 = "Final paragraph " + new string('b', 200);
        md.Append(para1 + "\n\n" + fenced + "\n\n" + para2);
        Assert.NotEmpty(md.FrozenChunks);
        Assert.Equal(para1 + "\n\n" + fenced + "\n\n" + para2, md.Text);
        // Open fence must not be split across frozen/live chunks.
        var mdOpenFence = new MarkdownBlockViewModel("");
        var openCode = "```python\n" + new string('z', 500) + "\n\nprint(1)";
        mdOpenFence.Append(openCode);
        Assert.Empty(mdOpenFence.FrozenChunks);
        Assert.Equal(openCode, mdOpenFence.LiveText);

        // 4. SmoothCollapse lazy-template mode builds zero children when collapsed, mounts on expand,
        // and unmounts once the collapse transition finishes.
        var builtCount = 0;
        var collapse = new Octopus.App.Common.SmoothCollapse
        {
            IsExpanded = false,
            LazyData = "payload",
            ContentTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object?>((_, _) =>
            {
                builtCount++;
                return new TextBlock { Text = "Lazy child" };
            }),
        };
        var window = new Window { Width = 400, Height = 300, Content = collapse };
        window.Show();
        try
        {
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Null(collapse.Content);
            Assert.Equal(0, builtCount);

            collapse.IsExpanded = true;
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal("payload", collapse.Content);
            Assert.Equal(1, builtCount);

            collapse.IsExpanded = false;
            await WaitUntilAsync(() => collapse.Content is null, timeoutMs: 1500);
            Assert.Null(collapse.Content);
        }
        finally
        {
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Single_background_bash_uses_compact_docked_tray_without_empty_subagent_counts()
    {
        const string longCmd = "dotnet run --project /Users/token/.gemini/antigravity-cli/brain/42664567-1010-4c5f-b316-e8701486150f/scratch/TestRunner/TestRunner.csproj";
        Assert.Equal("dotnet run --project …/TestRunner.csproj", BackgroundBashViewModel.FormatCompactCommand(longCmd));

        var tempDir = Directory.CreateTempSubdirectory("octopus-single-bg-").FullName;
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);

        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(tempDir, "single-bg-tray");
        var opener = AppHost.Get<IItemOpener>();
        var registry = AppHost.Get<ITabContentRegistry>();

        fakeFactory.ConfigureNext = session =>
        {
            session.PromptHandler = (s, _, _, _) =>
            {
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate
                {
                    ToolCallId = "bg-only-1",
                    Kind = "execute",
                    Title = longCmd,
                    Command = longCmd,
                    IsBackground = true,
                    BackgroundTaskId = "task-only-1",
                    Status = "in_progress",
                    Stdout = "Build succeeded. Watching emulator...",
                }));
                return Task.FromResult(new AcpPromptResult { StopReason = "end_turn" });
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
            vm.InputText = "Run watcher";
            await vm.SendPromptAsync();
            await WaitUntilAsync(() => vm.HasBackgroundShells);

            Assert.True(vm.HasActivityTray);
            Assert.True(vm.IsSingleBackgroundShell);
            Assert.False(vm.IsMultiActivityTray);
            Assert.False(vm.IsActivityTrayExpanded);
            Assert.True(vm.ShowCollapsedSingleShellActions);
            Assert.True(vm.ShowCollapsedSingleShellStop);
            Assert.DoesNotContain("0/0", vm.ActivityTraySummary);

            var shell = Assert.Single(vm.BackgroundShells);
            Assert.Equal("dotnet run --project …/TestRunner.csproj", shell.CompactCommand);
            Assert.Equal("Build succeeded. Watching emulator...", shell.LastLogLine);

            vm.OpenSingleShellLogsCommand.Execute(null);
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsActivityTrayExpanded);
            Assert.True(shell.IsLogsExpanded);
            Assert.False(vm.ShowCollapsedSingleShellActions);

            shell.StopCommand.Execute(null);
            vm.ToggleActivityTrayCommand.Execute(null);
            Assert.False(vm.IsActivityTrayExpanded);
            Assert.False(vm.ShowCollapsedSingleShellStop);
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
    public async Task Codex_gui_shows_discovered_and_custom_third_party_models_applies_launch_model_and_switches_via_reconnect()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-codex-3p-").FullName;
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var discovery = AppHost.Get<FakeModelDiscovery>();
        var settings = AppHost.Get<ISettingsStore>();
        var installer = AppHost.Get<AcpAdapterInstaller>();

        // Seed a cached fake codex-acp executable so AcpLaunchResolver resolves offline without npm/npx.
        var binDir = Path.Combine(installer.AdapterRoot("codex"), "npm", "node_modules", ".bin");
        Directory.CreateDirectory(binDir);
        await File.WriteAllTextAsync(
            Path.Combine(binDir, OperatingSystem.IsWindows() ? "codex-acp.cmd" : "codex-acp"),
            "#!/bin/sh\n");

        settings.Update(s =>
        {
            s.Agents.DefaultSurface = AgentSurface.Gui;
            s.Agents.Profiles["codex"] = new AgentProfile
            {
                Env = new Dictionary<string, string>
                {
                    ["OPENAI_BASE_URL"] = "http://127.0.0.1:3425/backend-api/codex",
                    ["OPENAI_API_KEY"] = "sk-magpie-test",
                },
            };
        });

        discovery.ModelsByAgent["codex"] =
        [
            new AgentOption { Id = "deepseek-v4-pro", Label = "DeepSeek-V4-Pro · DeepSeek" },
            new AgentOption { Id = "gemini-4-argon", Label = "Gemini 4 Argon · 4sapi" },
        ];

        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(tempDir, "codex-third-party");
        var opener = AppHost.Get<IItemOpener>();
        var registry = AppHost.Get<ITabContentRegistry>();

        fakeFactory.ConfigureNext = session =>
        {
            // Mirror real codex-acp behavior: honors CODEX_CONFIG["model"] on start/resume,
            // and throws -32602 Invalid params when set_config_option requests a model outside its built-in catalog.
            var activeModel = "gpt-6.2-sol";
            if (session.Plan.Environment.TryGetValue("CODEX_CONFIG", out var cfgJson))
            {
                using var doc = JsonDocument.Parse(cfgJson);
                if (doc.RootElement.TryGetProperty("model", out var mEl) && mEl.GetString() is { Length: > 0 } m)
                {
                    activeModel = m;
                }
            }
            var options = new List<AcpConfigSelectOption>
            {
                new() { Value = "gpt-6.2-sol", Name = "GPT-6.2 Sol" },
                new() { Value = "gpt-6.2-astra", Name = "GPT-6.2 Astra" },
            };
            if (options.All(o => o.Value != activeModel))
            {
                options.Insert(0, new AcpConfigSelectOption { Value = activeModel, Name = activeModel });
            }
            session.SessionId = "codex-thread-1";
            session.MutableConfigOptions =
            [
                new AcpSessionConfigOption
                {
                    Id = "model",
                    Category = "model",
                    Name = "Model",
                    Type = "select",
                    CurrentValue = activeModel,
                    Options = options,
                },
                new AcpSessionConfigOption
                {
                    Id = "reasoning_effort",
                    Category = "thought_level",
                    Name = "Reasoning effort",
                    Type = "select",
                    CurrentValue = "medium",
                    Options =
                    [
                        new AcpConfigSelectOption { Value = "medium", Name = "Medium" },
                        new AcpConfigSelectOption { Value = "high", Name = "High" },
                    ],
                },
            ];
            session.SetConfigOptionHandler = (_, cfgId, value, _) =>
            {
                if (cfgId == "model" && value is not ("gpt-6.2-sol" or "gpt-6.2-astra") && value != activeModel)
                {
                    throw new Octopus.Acp.Transport.AcpRpcException(-32602, "Invalid params");
                }
                return Task.CompletedTask;
            };
        };

        var item = opener.OpenAgent("codex", new AgentLaunchOptions
        {
            Model = "deepseek-v4-pro",
            Effort = "high",
            Surface = AgentSurface.Gui,
        })!;
        var view = Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(project, item));
        var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);
        var window = new Window { Width = 1100, Height = 760, Content = view };
        window.Show();
        try
        {
            await WaitUntilAsync(() => vm.State == AgentGuiState.Ready);
            // 1. Initial launch passed third-party env + CODEX_CONFIG with chosen model & effort!
            var firstSession = Assert.Single(fakeFactory.Created);
            Assert.Equal("http://127.0.0.1:3425/backend-api/codex", firstSession.Plan.Environment["OPENAI_BASE_URL"]);
            Assert.Equal("sk-magpie-test", firstSession.Plan.Environment["OPENAI_API_KEY"]);
            Assert.Contains("\"model\":\"deepseek-v4-pro\"", firstSession.Plan.Environment["CODEX_CONFIG"]);
            Assert.Equal("deepseek-v4-pro", vm.Composer.SelectedModelId);
            Assert.Contains(("reasoning_effort", "high"), firstSession.SetConfigs);

            // 2. Discovered third-party models appear in the GUI model picker (even alongside codex-acp's built-ins)!
            await WaitUntilAsync(() => vm.Composer.ModelChoices.Any(m => m.Id == "gemini-4-argon"));
            Assert.True(vm.Composer.ShowModelSearch);
            Assert.Equal("gemini-4-argon", vm.Composer.ModelChoices[0].Id);

            // 3. Switching to a custom third-party model typed in the GUI picker triggers seamless reconnect with new CODEX_CONFIG!
            vm.Composer.ModelSearchQuery = "my-custom-3p-model";
            Assert.True(vm.Composer.CanUseCustomModelQuery);
            Assert.False(vm.Composer.ShowNoModelMatches);
            Assert.True(vm.Composer.CommitModelSearch());

            await WaitUntilAsync(() => fakeFactory.Created.Count == 2 && vm.State == AgentGuiState.Ready);
            var secondSession = fakeFactory.Created[1];
            Assert.Contains("\"model\":\"my-custom-3p-model\"", secondSession.Plan.Environment["CODEX_CONFIG"]);
            Assert.Equal("my-custom-3p-model", vm.Composer.SelectedModelId);
            Assert.Equal("my-custom-3p-model", item.Launch!.Model);
            Assert.False(vm.HasErrorBanner);
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
    public async Task Failed_model_switch_reverts_composer_selection_and_surfaces_error()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-model-err-").FullName;
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);

        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(tempDir, "model-err");
        var opener = AppHost.Get<IItemOpener>();
        var registry = AppHost.Get<ITabContentRegistry>();

        fakeFactory.ConfigureNext = session =>
        {
            session.Models = new AcpSessionModelState
            {
                CurrentModelId = "claude-sonnet-4",
                AvailableModels =
                [
                    new AcpModelInfo { ModelId = "claude-sonnet-4", Name = "Claude Sonnet 4" },
                    new AcpModelInfo { ModelId = "broken-model", Name = "Broken Model" },
                ],
            };
            session.SetModelHandler = (_, id, _) =>
            {
                if (id == "broken-model")
                {
                    throw new Octopus.Acp.Transport.AcpRpcException(-32602, "Unknown model broken-model");
                }
                return Task.CompletedTask;
            };
        };

        var item = opener.OpenAgent("opencode", new AgentLaunchOptions { Surface = AgentSurface.Gui })!;
        var view = Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(project, item));
        var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);
        var window = new Window { Width = 1100, Height = 760, Content = view };
        window.Show();
        try
        {
            await WaitUntilAsync(() => vm.State == AgentGuiState.Ready);
            Assert.Equal("claude-sonnet-4", vm.Composer.SelectedModelId);

            vm.Composer.FilteredModelChoices[1].SelectCommand.Execute(null);
            await WaitUntilAsync(() => vm.HasErrorBanner);

            Assert.Equal("claude-sonnet-4", vm.Composer.SelectedModelId);
            Assert.Contains("Unknown model broken-model", vm.ErrorBanner);
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
    public async Task Streaming_after_new_prompt_stays_pinned_at_bottom_and_single_jump_reaches_true_end()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-scroll-pin-").FullName;
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);

        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(tempDir, "scroll-pin");
        var opener = AppHost.Get<IItemOpener>();
        var registry = AppHost.Get<ITabContentRegistry>();

        var turnGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeAcpSession? activeSession = null;

        fakeFactory.ConfigureNext = session =>
        {
            activeSession = session;
            session.StartHandler = (s, _) =>
            {
                // Replay 10 variable-height historical turns so total height far exceeds viewport height.
                for (var i = 1; i <= 10; i++)
                {
                    s.Emit(new AcpUserMessageEvent($"Historical prompt #{i}", $"u-{i}") { IsReplay = true });
                    var paragraphs = string.Join("\n\n", Enumerable.Range(1, 4).Select(p =>
                        $"Historical turn {i}, paragraph {p}: reviewing module architecture, state transitions, and layout invariants across multiple lines so each turn has realistic height."));
                    s.Emit(new AcpTextDeltaEvent("assistant", paragraphs, $"a-{i}") { IsReplay = true });
                }
            };
            session.PromptHandler = async (_, _, _, ct) =>
            {
                await turnGate.Task.WaitAsync(ct);
                return new AcpPromptResult { StopReason = "end_turn" };
            };
        };

        var item = opener.OpenAgent("opencode")!;
        var view = Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(project, item));
        var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);
        var window = new Window { Width = 1100, Height = 640, Content = view };
        window.Show();
        try
        {
            await WaitUntilAsync(() => vm.State == AgentGuiState.Ready && vm.Timeline.Count == 20);
            PumpLayout(window);

            var scroller = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view)
                .OfType<TKCode.Arc.ScrollArea>()
                .First(s => s.Name == "PART_ScrollViewer");

            Assert.True(scroller.ScrollBarMaximum.Y > 600, $"Expected tall timeline, got ScrollBarMaximum.Y={scroller.ScrollBarMaximum.Y}");
            Assert.True(vm.IsPinnedToBottom);
            Assert.False(vm.ShowJumpToBottom);
            Assert.True(scroller.ScrollBarMaximum.Y - scroller.Offset.Y <= 2.0,
                $"Expected replay to land at bottom, offset={scroller.Offset.Y}, max={scroller.ScrollBarMaximum.Y}");

            // Submit a new prompt and stream output in multiple steps: scrollbar must never drift upward into history.
            vm.InputText = "Please inspect the layout and explain every detail.";
            var sendTask = vm.SendPromptAsync();
            PumpLayout(window);

            var previousOffsetY = scroller.Offset.Y;
            Assert.True(vm.IsPinnedToBottom, "Submitting a new prompt must remain pinned to bottom.");
            Assert.False(vm.ShowJumpToBottom);

            for (var chunk = 1; chunk <= 8; chunk++)
            {
                var block = $"\n\n### Section {chunk}\n"
                    + string.Join("\n", Enumerable.Range(1, 6).Select(line =>
                        $"- Streaming output item {chunk}.{line}: verifying vertical scroll stability while new content grows."));
                activeSession!.Emit(new AcpTextDeltaEvent("assistant", block, "live-msg"));
                await Task.Delay(50);
                PumpLayout(window);

                Assert.True(vm.IsPinnedToBottom,
                    $"Lost bottom pin at chunk {chunk}: offset={scroller.Offset.Y}, max={scroller.ScrollBarMaximum.Y}, extent={scroller.Extent.Height}");
                Assert.False(vm.ShowJumpToBottom, $"Jump-to-bottom appeared unexpectedly at chunk {chunk}.");
                Assert.True(scroller.Offset.Y >= previousOffsetY - 1.0 || scroller.ScrollBarMaximum.Y - scroller.Offset.Y <= 2.0,
                    $"Scrollbar drifted upward at chunk {chunk}: prev={previousOffsetY}, cur={scroller.Offset.Y}, max={scroller.ScrollBarMaximum.Y}");
                Assert.True(scroller.ScrollBarMaximum.Y - scroller.Offset.Y <= 2.0,
                    $"Not at bottom at chunk {chunk}: offset={scroller.Offset.Y}, max={scroller.ScrollBarMaximum.Y}");
                previousOffsetY = scroller.Offset.Y;
            }

            // Now user explicitly scrolls up into historical conversation while streaming continues:
            // viewport must stay in history and NOT yank user back down.
            scroller.Offset = new Avalonia.Vector(0, 120);
            PumpLayout(window);
            Assert.False(vm.IsPinnedToBottom, "Scrolling up into history must unpin auto-scroll.");
            Assert.True(vm.ShowJumpToBottom);

            var pinnedHistoryOffset = scroller.Offset.Y;
            activeSession!.Emit(new AcpTextDeltaEvent("assistant", "\n\nMore streamed output while user reads history.\nLine 2\nLine 3\nLine 4", "live-msg"));
            await Task.Delay(50);
            PumpLayout(window);
            Assert.False(vm.IsPinnedToBottom, "Streaming while unpinned must not force re-pin.");
            Assert.True(Math.Abs(scroller.Offset.Y - pinnedHistoryOffset) <= 2.0,
                $"Expected history offset to stay at {pinnedHistoryOffset}, got {scroller.Offset.Y}");

            // Finish turn while user is still scrolled up, then click "Jump to latest" ONCE:
            // must reach the true bottom in a single click (not 4 clicks).
            turnGate.TrySetResult(true);
            await sendTask;
            PumpLayout(window);

            vm.JumpToBottomCommand.Execute(null);
            PumpLayout(window);

            Assert.True(vm.IsPinnedToBottom, "Single JumpToBottom click must pin to bottom.");
            Assert.False(vm.ShowJumpToBottom, "JumpToBottom button must disappear after a single click.");
            Assert.True(scroller.ScrollBarMaximum.Y - scroller.Offset.Y <= 2.0,
                $"Single JumpToBottom click must reach true bottom, offset={scroller.Offset.Y}, max={scroller.ScrollBarMaximum.Y}");

            // Re-entering the GUI tab after scrolling up must restore the scrollbar to the very bottom.
            scroller.Offset = new Avalonia.Vector(0, 80);
            PumpLayout(window);
            Assert.False(vm.IsPinnedToBottom);

            window.Content = null;
            PumpLayout(window);
            window.Content = view;
            PumpLayout(window);

            Assert.True(vm.IsPinnedToBottom, "Re-entering the GUI tab must pin to bottom.");
            Assert.False(vm.ShowJumpToBottom);
            Assert.True(scroller.ScrollBarMaximum.Y - scroller.Offset.Y <= 2.0,
                $"Re-entering the GUI tab must scroll to the very bottom, offset={scroller.Offset.Y}, max={scroller.ScrollBarMaximum.Y}");
        }
        finally
        {
            turnGate.TrySetResult(true);
            await vm.CloseAsync();
            vm.Dispose();
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Agent_gui_renders_codex_and_acp_generated_images_with_ImageReveal_and_opens_files()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-acp-imggen-").FullName;
        var savedImagePath = Path.Combine(tempDir, "saved-render.png");
        var previewBytes = Files.TestImages.Png(160, 90);
        var finalBytes = Files.TestImages.Png(320, 180);
        var squareBytes = Files.TestImages.Png(128, 128);
        await File.WriteAllBytesAsync(savedImagePath, finalBytes);

        var previewB64 = Convert.ToBase64String(previewBytes);
        var finalB64 = Convert.ToBase64String(finalBytes);
        var squareB64 = Convert.ToBase64String(squareBytes);

        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var settings = AppHost.Get<ISettingsStore>();
        var installer = AppHost.Get<AcpAdapterInstaller>();
        // Seed a cached fake codex-acp executable so AcpLaunchResolver resolves offline without npm/npx.
        var binDir = Path.Combine(installer.AdapterRoot("codex"), "npm", "node_modules", ".bin");
        Directory.CreateDirectory(binDir);
        await File.WriteAllTextAsync(
            Path.Combine(binDir, OperatingSystem.IsWindows() ? "codex-acp.cmd" : "codex-acp"),
            "#!/bin/sh\n");

        settings.Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);

        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(tempDir, "acp-image-gen");
        var opener = AppHost.Get<IItemOpener>();
        var registry = AppHost.Get<ITabContentRegistry>();

        var step1Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var step2Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fakeFactory.ConfigureNext = session =>
        {
            // Replay a historical turn with a direct ACP image block -> settles immediately with Status == null.
            session.StartHandler = (s, _) =>
            {
                s.Emit(new AcpUserMessageEvent("Draw a square icon", "u-replay") { IsReplay = true });
                s.Emit(new AcpImageEvent(new AcpImageContent
                {
                    Base64Data = squareB64,
                    MimeType = "image/png",
                    AltText = "Replayed icon",
                }, "a-replay")
                { IsReplay = true });
            };

            session.PromptHandler = async (s, _, _, ct) =>
            {
                // 1) Queued Codex image_generation tool call
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate
                {
                    ToolCallId = "ig_live_1",
                    Kind = "image",
                    Name = "image_generation",
                    Title = "Generating image",
                    Status = "pending",
                    IsImageGeneration = true,
                    ImagePrompt = "Minimalist sunset workspace",
                    ImageAspectRatio = 16d / 9d,
                }));
                await step1Gate.Task.WaitAsync(ct);

                // 2) Streaming partial preview + explicit progress
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate
                {
                    ToolCallId = "ig_live_1",
                    Kind = "image",
                    Name = "image_generation",
                    Title = "Generating image",
                    Status = "in_progress",
                    IsImageGeneration = true,
                    ImagePrompt = "Minimalist sunset workspace",
                    ImageProgress = 0.45,
                    Images = [new AcpImageContent { Base64Data = previewB64, MimeType = "image/png" }],
                }));
                await step2Gate.Task.WaitAsync(ct);

                // 3) Completed image with saved local file path + failed second image + direct ACP image chunk
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate
                {
                    ToolCallId = "ig_live_1",
                    Kind = "image",
                    Name = "image_generation",
                    Title = "Generating image",
                    Status = "completed",
                    IsImageGeneration = true,
                    ImagePrompt = "Minimalist sunset workspace (revised)",
                    Images = [new AcpImageContent { FilePath = savedImagePath, MimeType = "image/png" }],
                }));
                s.Emit(new AcpToolCallEvent(new AcpToolCallUpdate
                {
                    ToolCallId = "ig_failed_2",
                    Kind = "image",
                    Name = "image_generation",
                    Title = "Generate blocked image",
                    Status = "failed",
                    IsImageGeneration = true,
                    ImagePrompt = "Blocked prompt",
                    Stderr = "Content policy rejected",
                }));
                s.Emit(new AcpImageEvent(new AcpImageContent
                {
                    Base64Data = finalB64,
                    MimeType = "image/png",
                    AltText = "Inline PNG result",
                }, "live-msg-1"));
                s.Emit(new AcpTextDeltaEvent("assistant", "Here are your generated images.", "live-msg-1"));

                await finishGate.Task.WaitAsync(ct);
                return new AcpPromptResult { StopReason = "end_turn" };
            };
        };

        var item = opener.OpenAgent("codex")!;
        var view = Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(project, item));
        var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);
        var window = new Window { Width = 1040, Height = 760, Content = view };
        window.Show();
        try
        {
            await WaitUntilAsync(() => vm.State == AgentGuiState.Ready && vm.Timeline.OfType<AssistantTurnItem>().Any());
            PumpLayout(window);

            // Replayed historical image settled cleanly (no stuck spinner: RevealStatus == null, Source != null).
            var replayedTurn = vm.Timeline.OfType<AssistantTurnItem>().First();
            Assert.True(replayedTurn.HasGeneratedImages);
            var replayedImg = Assert.Single(replayedTurn.GeneratedImages);
            Assert.Null(replayedImg.RevealStatus);
            Assert.NotNull(replayedImg.Source);
            Assert.Equal("Replayed icon", replayedImg.AltText);

            // Start a live turn with progressive Codex image generation.
            vm.InputText = "Generate a sunset workspace illustration";
            var sendTask = vm.SendPromptAsync();

            await WaitUntilAsync(() => vm.Timeline.OfType<AssistantTurnItem>().Count() == 2
                && vm.Timeline.OfType<AssistantTurnItem>().Last().GeneratedImages.Count == 1);
            PumpLayout(window);

            var liveTurn = vm.Timeline.OfType<AssistantTurnItem>().Last();
            var genVm = Assert.Single(liveTurn.GeneratedImages);
            Assert.Equal(TKCode.Arc.ImageRevealStatus.Queued, genVm.RevealStatus);
            Assert.Equal("Minimalist sunset workspace", genVm.Prompt);
            Assert.Equal(16d / 9d, genVm.AspectRatio, precision: 2);

            // Verify TKCode.Arc.ImageReveal controls are actually instantiated in the visual tree.
            var revealControls = view.GetVisualDescendants().OfType<TKCode.Arc.ImageReveal>().ToList();
            Assert.True(revealControls.Count >= 2, $"Expected at least 2 ImageReveal controls in visual tree, found {revealControls.Count}");

            // Advance to partial streaming preview.
            step1Gate.TrySetResult();
            await WaitUntilAsync(() => genVm.RevealStatus == TKCode.Arc.ImageRevealStatus.Generating && genVm.Source is not null);
            PumpLayout(window);
            Assert.Equal(0.45, genVm.Progress!.Value, precision: 3);
            Assert.NotNull(genVm.Source);

            // Advance to completion + failed card + inline Base64 image.
            step2Gate.TrySetResult();
            await WaitUntilAsync(() => liveTurn.GeneratedImages.Count == 3 && genVm.RevealStatus == TKCode.Arc.ImageRevealStatus.Done);
            PumpLayout(window);

            Assert.Equal("Minimalist sunset workspace (revised)", genVm.Prompt);
            Assert.Equal(savedImagePath, genVm.FilePath);
            Assert.NotNull(genVm.Source);
            Assert.Equal(16d / 9d, genVm.AspectRatio, precision: 2);

            var failedVm = Assert.Single(liveTurn.GeneratedImages, i => i.Id == "ig_failed_2");
            Assert.Equal(TKCode.Arc.ImageRevealStatus.Error, failedVm.RevealStatus);
            Assert.Equal("Content policy rejected", failedVm.ErrorMessage);

            var inlineVm = liveTurn.GeneratedImages[2];
            Assert.Equal(TKCode.Arc.ImageRevealStatus.Done, inlineVm.RevealStatus);
            Assert.NotNull(inlineVm.Source);
            Assert.Equal("Inline PNG result", inlineVm.AltText);

            // Finish turn: Work Area collapses, yet generated images remain visible outside the collapsed Work Area.
            finishGate.TrySetResult();
            await sendTask;
            PumpLayout(window);

            Assert.False(liveTurn.IsWorkExpanded);
            Assert.True(liveTurn.HasGeneratedImages);
            var finalReveals = view.GetVisualDescendants().OfType<TKCode.Arc.ImageReveal>().ToList();
            Assert.True(finalReveals.Count >= 4, $"Expected 4 ImageReveal controls after completion, found {finalReveals.Count}");
            Assert.Contains(finalReveals, r => r.Status == TKCode.Arc.ImageRevealStatus.Done && r.Source is not null);
            Assert.Contains(finalReveals, r => r.Status == TKCode.Arc.ImageRevealStatus.Error);

            // Clicking OpenImageCommand on a file-backed image opens it in the project workspace.
            Assert.True(genVm.CanOpenImage);
            genVm.OpenImageCommand.Execute(null);
            PumpLayout(window);
            Assert.Contains(project.Items, i => i.FilePath == savedImagePath);

            // Clicking OpenImageCommand on an inline Base64 image materializes a temp PNG and opens it.
            Assert.True(inlineVm.CanOpenImage);
            var itemsBeforeInlineOpen = project.Items.Count;
            inlineVm.OpenImageCommand.Execute(null);
            PumpLayout(window);
            Assert.True(project.Items.Count > itemsBeforeInlineOpen);
            var openedTempPath = project.Items[^1].FilePath;
            Assert.False(string.IsNullOrEmpty(openedTempPath));
            Assert.True(File.Exists(openedTempPath));
            Assert.EndsWith(".png", openedTempPath, StringComparison.OrdinalIgnoreCase);
            try { File.Delete(openedTempPath!); } catch (IOException) { }
        }
        finally
        {
            step1Gate.TrySetResult();
            step2Gate.TrySetResult();
            finishGate.TrySetResult();
            await vm.CloseAsync();
            vm.Dispose();
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public void Agents_settings_exposes_acp_url_elicitation_and_opt_in_client_fs_terminal_toggles()
    {
        var settings = AppHost.Get<ISettingsStore>();
        var catalog = AppHost.Get<IAgentCatalog>();
        var scanner = new FakeAgentScanner("opencode");
        var vm = new AgentsSettingsViewModel(
            settings,
            catalog,
            scanner,
            AppHost.Get<IAgentCommandCompiler>(),
            AppHost.Get<IModelDiscovery>(),
            AppHost.Get<IFileOpener>(),
            AppHost.Get<IPathPicker>());

        var origUrl = settings.Current.Agents.AcpEnableUrlElicitation;
        var origFs = settings.Current.Agents.AcpEnableClientFileSystem;
        var origTerm = settings.Current.Agents.AcpEnableClientTerminal;
        try
        {
            // Defaults: URL elicitation ON, client fs/* and terminal/* OFF by default.
            Assert.True(vm.AcpEnableUrlElicitation);
            Assert.False(vm.AcpEnableClientFileSystem);
            Assert.False(vm.AcpEnableClientTerminal);

            vm.AcpEnableUrlElicitation = false;
            vm.AcpEnableClientFileSystem = true;
            vm.AcpEnableClientTerminal = true;

            Assert.False(settings.Current.Agents.AcpEnableUrlElicitation);
            Assert.True(settings.Current.Agents.AcpEnableClientFileSystem);
            Assert.True(settings.Current.Agents.AcpEnableClientTerminal);
        }
        finally
        {
            settings.Update(s =>
            {
                s.Agents.AcpEnableUrlElicitation = origUrl;
                s.Agents.AcpEnableClientFileSystem = origFs;
                s.Agents.AcpEnableClientTerminal = origTerm;
            });
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Agent_gui_wires_additional_dirs_custom_options_url_elicitation_decline_fork_history_logout_stop_reason_and_compaction()
    {
        var dirA = Directory.CreateTempSubdirectory("octopus-acp-ext-a-").FullName;
        var dirB = Directory.CreateTempSubdirectory("octopus-acp-ext-b-").FullName;
        var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
        var settings = AppHost.Get<ISettingsStore>();
        settings.Update(s =>
        {
            s.Agents.DefaultSurface = AgentSurface.Gui;
            s.Agents.AcpEnableUrlElicitation = true;
            s.Agents.AcpEnableClientFileSystem = false;
            s.Agents.AcpEnableClientTerminal = false;
        });

        var projects = AppHost.Get<IProjectService>();
        var projB = projects.AddProject(dirB, "acp-ext-b");
        var projA = projects.AddProject(dirA, "acp-ext-a");
        var group = projects.AddGroup("MergedACP", [projA.Id, projB.Id]);
        projects.SetGroupMergeWorkspaces(group.Id, true);

        var opener = AppHost.Get<IItemOpener>();
        var registry = AppHost.Get<ITabContentRegistry>();

        AcpElicitationAction? observedUrlAction = null;
        AcpFormElicitationResponse? observedFormResponse = null;

        fakeFactory.ConfigureNext = session =>
        {
            session.SessionId = "sess-ext-1";
            session.Initialized = new AcpInitializeResult
            {
                ProtocolVersion = 2,
                AgentCapabilities = new()
                {
                    LoadSession = true,
                    PromptCapabilities = new() { Image = true },
                    AuthCapabilities = new() { Logout = true },
                    SessionCapabilities = new()
                    {
                        SupportsList = true,
                        SupportsFork = true,
                        SupportsDelete = true,
                        SupportsAdditionalDirectories = true,
                        SupportsCompaction = true,
                    },
                },
                AuthMethods =
                [
                    new AcpAuthMethod { Id = "oauth", Name = "Sign in with OAuth", Type = "agent" },
                ],
            };
            session.MutableConfigOptions =
            [
                new AcpSessionConfigOption
                {
                    Id = "sandbox_profile",
                    Name = "Sandbox",
                    Type = "select",
                    CurrentValue = "strict",
                    Options = [new() { Value = "strict", Name = "Strict" }, new() { Value = "relaxed", Name = "Relaxed" }],
                },
                new AcpSessionConfigOption
                {
                    Id = "verbose_trace",
                    Name = "Verbose Trace",
                    Type = "boolean",
                    CurrentBoolValue = false,
                },
            ];
            session.ListSessionsHandler = (_, _, _, _) => Task.FromResult(new AcpSessionListResult
            {
                Sessions =
                [
                    new AcpSessionInfo { SessionId = "sess-ext-1", Title = "Current Session", Cwd = dirA },
                    new AcpSessionInfo { SessionId = "sess-old-2", Title = "Older Session", Cwd = dirA },
                ],
            });
            session.ForkSessionHandler = (_, _, _, _) => Task.FromResult(new AcpSessionStartResult
            {
                Kind = "fork",
                SessionId = "sess-forked-99",
            });
            session.PromptHandler = async (s, _, _, ct) =>
            {
                s.Emit(new AcpPlanEvent([new AcpPlanEntry { Content = "Step 1", Status = "in_progress" }]));
                s.Emit(new AcpPlanRemovedEvent());
                s.Emit(new AcpCompactionEvent(new AcpCompactionUpdate { Status = "completed", Summary = "Context compacted" }));

                observedUrlAction = await s.Callbacks.OnUrlElicitationRequest!(new AcpUrlElicitationRequest
                {
                    RequestId = "url-1",
                    ElicitationId = "el-url-gui-1",
                    Url = "https://oauth.example.com/authorize",
                    Message = "Please authorize",
                }, ct);

                observedFormResponse = await s.Callbacks.OnFormElicitationRequest!(new AcpQuestionRequest
                {
                    RequestId = "form-1",
                    Title = "Optional survey",
                    Questions =
                    [
                        new AcpQuestionItem { Id = "note", Prompt = "Any note?", AllowFreeText = true, DefaultValue = "hello" },
                    ],
                }, ct);

                s.Emit(new AcpTextDeltaEvent("assistant", "Partial output...", null));
                return new AcpPromptResult
                {
                    StopReason = "max_tokens",
                    Usage = new AcpUsage { InputTokens = 3000, OutputTokens = 1200, TotalTokens = 4200 },
                };
            };
        };

        var item = opener.OpenAgent("opencode")!;
        var view = Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(projA, item));
        var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);
        var window = new Window { Width = 1000, Height = 720, Content = view };
        window.Show();
        try
        {
            await WaitUntilAsync(() => vm.State == AgentGuiState.Ready);
            var session = fakeFactory.Created[^1];

            // 1. Merged workspaces forwarded as additionalDirectories & client fs/terminal default OFF.
            Assert.NotNull(session.LastAdditionalDirectories);
            Assert.Contains(dirB, session.LastAdditionalDirectories!);
            Assert.False(session.Callbacks.EnableClientFileSystem);
            Assert.False(session.Callbacks.EnableClientTerminal);
            Assert.True(session.Callbacks.EnableUrlElicitation);

            // 2. Generic custom configOptions populated in Composer.
            Assert.True(vm.Composer.ShowCustomOptionsButton);
            Assert.Equal(2, vm.Composer.CustomConfigOptions.Count);
            var selectOpt = Assert.Single(vm.Composer.CustomConfigOptions, o => o.Id == "sandbox_profile");
            var boolOpt = Assert.Single(vm.Composer.CustomConfigOptions, o => o.Id == "verbose_trace");
            selectOpt.SelectedValue = "relaxed";
            boolOpt.BoolValue = true;
            await WaitUntilAsync(() => session.SetConfigs.Contains(("sandbox_profile", "relaxed"))
                && session.SetBoolConfigs.Contains(("verbose_trace", true)));

            // 3. Prompt turn: plan_removed, compaction, URL elicitation auto-complete, Form Decline, stopReason & fallback Usage.
            vm.InputText = "Run extended turn";
            var promptTask = vm.SendPromptAsync();

            await WaitUntilAsync(() => vm.Timeline.OfType<UrlElicitationCardItem>().Any());
            var urlCard = vm.Timeline.OfType<UrlElicitationCardItem>().Single();
            Assert.Equal("https://oauth.example.com/authorize", urlCard.Url);
            session.Emit(new AcpElicitationCompletedEvent("el-url-gui-1"));
            await WaitUntilAsync(() => urlCard.IsResolved);

            await WaitUntilAsync(() => vm.Timeline.OfType<QuestionCardItem>().Any());
            var qCard = vm.Timeline.OfType<QuestionCardItem>().Single();
            Assert.Equal("hello", qCard.Fields[0].FreeText);
            qCard.DeclineCommand.Execute(null);

            await promptTask;

            Assert.Equal(AcpElicitationAction.Accept, observedUrlAction);
            Assert.Equal(AcpElicitationAction.Decline, observedFormResponse!.Action);
            Assert.Equal(4200, vm.ContextUsed);

            var turn = vm.Timeline.OfType<AssistantTurnItem>().Single();
            Assert.Empty(turn.PlanEntries);
            Assert.True(turn.HasCompactionNotice);
            Assert.Contains("Context compacted", turn.CompactionNotice);
            Assert.True(turn.HasStopReasonNotice);

            // 4. Session History (list + delete), Fork, and Logout.
            Assert.True(vm.CanListSessions);
            Assert.True(vm.CanForkSession);
            Assert.True(vm.CanLogout);

            await vm.RefreshSessionHistoryAsync();
            Assert.Equal(2, vm.RemoteSessions.Count);
            var oldRow = Assert.Single(vm.RemoteSessions, r => r.SessionId == "sess-old-2");
            Assert.True(oldRow.CanDelete);
            await oldRow.DeleteCommand.ExecuteAsync(null);
            Assert.Contains("sess-old-2", session.DeletedSessions);
            Assert.Single(vm.RemoteSessions);

            var itemsBeforeFork = projA.Items.Count;
            await vm.ForkSessionAsync();
            Assert.Equal(itemsBeforeFork + 1, projA.Items.Count);
            Assert.Equal("sess-forked-99", projA.Items[^1].Launch?.SessionId);

            await vm.LogoutAsync();
            Assert.Equal(1, session.LogoutCount);
            Assert.True(vm.IsAuthRequired);
            Assert.Single(vm.AuthMethods, a => a.Id == "oauth");
        }
        finally
        {
            await vm.CloseAsync();
            vm.Dispose();
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            try { Directory.Delete(dirA, true); } catch (IOException) { }
            try { Directory.Delete(dirB, true); } catch (IOException) { }
        }
    }

    private static void PumpLayout(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
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
