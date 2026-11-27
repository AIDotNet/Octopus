using Octopus.Agents;
using Octopus.Agents.Sessions;
using Octopus.App.Services;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Core.Terminals;
using Octopus.Platform;
using Octopus.Storage;
using Octopus.Terminal;

namespace Octopus.App.Tests;

/// <summary>A terminal connection that never produces output and never exits on its own: tests never start a real pty.</summary>
public sealed class FakeTerminalConnection(string? foreground = null) : ITerminalConnection
{
    private readonly TaskCompletionSource<TerminalExit> _exit = new();

    /// <summary>True once the process was ended (hangup or dispose).</summary>
    public bool IsEnded { get; private set; }
    public int ProcessId => 4242;
    public Task<TerminalExit> Exited => _exit.Task;

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // No output; the stream ends when the process does.
        await Task.WhenAny(Task.Delay(Timeout.Infinite, cancellationToken), _exit.Task).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    /// <summary>Everything written to the program, as text, in order.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Written { get; } = new();

    public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        Written.Enqueue(System.Text.Encoding.UTF8.GetString(input.Span));
        return ValueTask.CompletedTask;
    }
    /// <summary>How often the terminal changed the size of the pty.</summary>
    public int Resizes { get; private set; }
    public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight)
    {
        Resizes++;
        return ValueTask.CompletedTask;
    }

    public ValueTask SignalAsync(TerminalSignal signal)
    {
        IsEnded = true;
        _exit.TrySetResult(new TerminalExit(0, null));
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult(foreground);

    /// <summary>The process ends by itself with this exit code.</summary>
    public void Exit(int code) => _exit.TrySetResult(new TerminalExit(code, null));

    public ValueTask DisposeAsync()
    {
        IsEnded = true;
        _exit.TrySetResult(new TerminalExit(0, null));
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeTerminalConnectionFactory : ITerminalConnectionFactory
{
    public List<LaunchSpec> Started { get; } = [];
    public List<FakeTerminalConnection> Connections { get; } = [];

    /// <summary>The foreground program the next connections report: null means just the shell (nothing to confirm).</summary>
    public string? Foreground { get; set; }

    public ITerminalConnection Start(LaunchSpec spec, int columns, int rows)
    {
        Started.Add(spec);
        var connection = new FakeTerminalConnection(Foreground);
        Connections.Add(connection);
        return connection;
    }
}

/// <summary>A scanner that reports fixed installations instead of looking at the machine.</summary>
public sealed class FakeAgentScanner(params string[] installed) : IAgentScanner
{
    public AgentScanResult Current { get; } = new()
    {
        Installations = installed.Select(id => new AgentInstallation { AgentId = id, Path = "/usr/local/bin/" + id }).ToList(),
    };

    public bool IsScanning => false;
    public event EventHandler? Changed { add { } remove { } }
    public Task LoadCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<AgentScanResult> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
    public AgentInstallation? Find(string agentId) => Current.Installations.FirstOrDefault(i => i.AgentId == agentId);
}

/// <summary>An installer that never runs a process: every agent with an install hint gets a plan.</summary>
public sealed class FakeAgentInstaller : IAgentInstaller
{
    public List<AgentInstallPlan> Installed { get; } = [];
    public List<AgentInstallPlan> InstalledElevated { get; } = [];
    public AgentInstallResult Result { get; set; } = new(true, "");
    public AgentInstallResult ElevatedResult { get; set; } = new(true, "");
    public InstallElevation Elevation { get; set; } = InstallElevation.None;
    public bool IsScript { get; set; }
    public bool CanElevateInBackground { get; set; }
    /// <summary>Called on a successful install, e.g. to make a fake scanner find the agent.</summary>
    public Action<AgentInstallPlan>? OnInstalled { get; set; }

    public Task<AgentInstallPlan?> PlanAsync(AgentDefinition definition, CancellationToken cancellationToken = default) =>
        PlanAsync(definition.Id, definition.Detect.InstallHints, cancellationToken);

    public Task<AgentInstallPlan?> PlanAsync(string id, IReadOnlyList<AgentInstallHint> hints, CancellationToken cancellationToken = default) =>
        Task.FromResult(hints.FirstOrDefault() is { } hint
            ? new AgentInstallPlan(id, hint, hint.Command.Split(' ')[0], "/bin/bash", ["-c", hint.Command]) { Elevation = Elevation, IsScript = IsScript }
            : null);

    public Task<AgentInstallResult> InstallAsync(AgentInstallPlan plan, CancellationToken cancellationToken = default)
    {
        Installed.Add(plan);
        if (Result.Succeeded) OnInstalled?.Invoke(plan);
        return Task.FromResult(Result);
    }

    public Task<AgentInstallResult> InstallElevatedAsync(AgentInstallPlan plan, CancellationToken cancellationToken = default)
    {
        InstalledElevated.Add(plan);
        if (ElevatedResult.Succeeded) OnInstalled?.Invoke(plan);
        return Task.FromResult(ElevatedResult);
    }

    public AgentCommand BuildTerminalCommand(AgentInstallPlan plan, bool elevated) =>
        new("/bin/bash", ["-c", (elevated ? "sudo " : "") + plan.CommandLine], new Dictionary<string, string>());
}

public sealed class FakeDialogService : IDialogService
{
    public bool Answer { get; set; } = true;
    public int Asked { get; private set; }
    public string? LastTitle { get; private set; }
    public string? LastDescription { get; private set; }
    public CloseWindowPromptResult CloseWindowPrompt { get; set; } = new(CloseWindowDecision.Quit, false);
    public int CloseWindowPromptAsked { get; private set; }
    public NewProjectChoice? NewProject { get; set; }
    public NewProjectChoice? LastNewProjectInitial { get; private set; }

    public Task<CloseWindowPromptResult> AskCloseWindowActionAsync()
    {
        CloseWindowPromptAsked++;
        return Task.FromResult(CloseWindowPrompt);
    }

    public Task<NewProjectChoice?> AskNewProjectAsync(NewProjectChoice? initial = null)
    {
        LastNewProjectInitial = initial;
        return Task.FromResult(NewProject);
    }

    /// <summary>The answer of the project icon dialog; null: dismissed.</summary>
    public ProjectAppearanceChoice? Appearance { get; set; }
    public Task<ProjectAppearanceChoice?> AskProjectAppearanceAsync(Octopus.Core.Projects.Project project) => Task.FromResult(Appearance);

    /// <summary>The answer of the device icon dialog; null: dismissed.</summary>
    public ProjectAppearanceChoice? DeviceAppearance { get; set; }
    public string? LastDeviceAppearanceKey { get; private set; }
    public Task<ProjectAppearanceChoice?> AskDeviceAppearanceAsync(
        string deviceKey,
        string deviceName,
        string subtitle,
        Octopus.Core.Projects.DeviceAppearance? current,
        string defaultColor,
        string? defaultIcon)
    {
        LastDeviceAppearanceKey = deviceKey;
        return Task.FromResult(DeviceAppearance);
    }

    /// <summary>Fills the agent editor as a person would; false cancels. Null cancels too.</summary>
    public Func<Octopus.App.Settings.Agents.AgentEditorModel, bool>? AgentEditor { get; set; }
    public Task<Octopus.Core.Agents.AgentDefinition?> AskAgentDefinitionAsync(Octopus.App.Settings.Agents.AgentEditorModel model) =>
        Task.FromResult(AgentEditor?.Invoke(model) == true ? model.Build() : null);

    /// <summary>What the tunnel dialog answers; null: dismissed. The host and the tunnel it was asked about are kept.</summary>
    public Octopus.Core.Projects.TunnelSpec? NewTunnel { get; set; }
    public Octopus.Core.Projects.TunnelSpec? LastTunnelAsked { get; private set; }
    public string? LastTunnelHost { get; private set; }
    public int TunnelsAsked { get; private set; }

    public Task<Octopus.Core.Projects.TunnelSpec?> AskTunnelAsync(Octopus.Core.Projects.TunnelSpec? initial, string host)
    {
        TunnelsAsked++;
        LastTunnelAsked = initial;
        LastTunnelHost = host;
        return Task.FromResult(NewTunnel);
    }

    public Octopus.Ssh.SshHostDraft? NewSshHost { get; set; }

    public Task<Octopus.Ssh.SshHostDraft?> AskNewSshHostAsync() => Task.FromResult(NewSshHost);

    public Octopus.Ssh.SshHostDraft? EditedSshHost { get; set; }
    public Octopus.Ssh.SshHost? LastSshHostEdited { get; private set; }

    public Task<Octopus.Ssh.SshHostDraft?> AskEditSshHostAsync(Octopus.Ssh.SshHost host)
    {
        LastSshHostEdited = host;
        return Task.FromResult(EditedSshHost);
    }

    public string? NameAnswer { get; set; }
    public string? LastNameLabel { get; private set; }

    public Task<string?> AskNameAsync(string title, string label, string? initial, string confirmText)
    {
        LastNameLabel = label;
        return Task.FromResult(NameAnswer);
    }

    public bool LoginResult { get; set; } = true;
    public int LoginsRun { get; private set; }
    public LaunchSpec? LastLoginSpec { get; private set; }
    public Func<LaunchSpec, Task<bool>>? OnRunAccountLogin { get; set; }

    public Task<bool> RunAccountLoginAsync(string title, string description, LaunchSpec spec, bool autoCloseOnSuccess = true)
    {
        LoginsRun++;
        LastLoginSpec = spec;
        return OnRunAccountLogin is not null ? OnRunAccountLogin(spec) : Task.FromResult(LoginResult);
    }

    public Octopus.App.Services.Workspaces.CreateWorkspaceRequest? NextWorkspaceRequest { get; set; }
    public string? LastWorkspaceError { get; private set; }
    public bool LastWorkspaceDialogCalledOnUiThread { get; private set; }

    public Task<Octopus.App.Services.Workspaces.CreateWorkspaceRequest?> AskCreateWorkspaceAsync(
        Project project,
        string repositoryRoot,
        IReadOnlyList<Octopus.Git.GitBranch> branches,
        IReadOnlyList<Octopus.Git.GitWorktree> worktrees,
        Octopus.App.Services.Workspaces.IGitHubTaskProvider taskProvider,
        Func<string, string> computePath,
        string homeDirectory,
        string? defaultAgentName,
        Octopus.App.Services.Workspaces.CreateWorkspaceRequest? initial = null,
        string? initialError = null)
    {
        LastWorkspaceDialogCalledOnUiThread = Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
        LastWorkspaceError = initialError;
        var next = NextWorkspaceRequest;
        NextWorkspaceRequest = null;
        return Task.FromResult(next);
    }

    public Octopus.App.Services.Workspaces.WorkspaceCleanupRunSummary? NextInspectSummary { get; set; }
    public int InspectWorkspacesAsked { get; private set; }

    public Task<Octopus.App.Services.Workspaces.WorkspaceCleanupRunSummary?> ShowInspectWorkspacesAsync(
        Octopus.App.Services.Workspaces.IWorkspaceCleanupService cleanup,
        IProjectService projects,
        Octopus.Platform.IFileOpener? fileOpener = null)
    {
        InspectWorkspacesAsked++;
        var next = NextInspectSummary;
        NextInspectSummary = null;
        return Task.FromResult(next);
    }

    public Octopus.App.Bots.NewBotRequest? NextBotRequest { get; set; }
    public string? LastBotTemplateId { get; private set; }
    public int NewBotAsked { get; private set; }

    public Task<Octopus.App.Bots.NewBotRequest?> AskNewBotAsync(string? initialTemplateId = null)
    {
        NewBotAsked++;
        LastBotTemplateId = initialTemplateId;
        var next = NextBotRequest;
        NextBotRequest = null;
        return Task.FromResult(next);
    }

    public int ConnectorCatalogShown { get; private set; }
    public string? LastConnectorCatalogBotId { get; private set; }

    public Task ShowConnectorCatalogAsync(Octopus.Core.Bots.BotDefinition bot, Octopus.App.Bots.IBotService bots)
    {
        ConnectorCatalogShown++;
        LastConnectorCatalogBotId = bot.Id;
        return Task.CompletedTask;
    }

    public Octopus.App.Bots.BotAvatarChoice? NextBotAvatar { get; set; }

    public Task<Octopus.App.Bots.BotAvatarChoice?> AskBotAvatarAsync(Octopus.Core.Bots.BotDefinition bot)
    {
        var next = NextBotAvatar;
        NextBotAvatar = null;
        return Task.FromResult(next);
    }

    public int BotShareShown { get; private set; }

    public Task ShowBotShareAsync(Octopus.Core.Bots.BotDefinition bot, Octopus.App.Bots.IBotService bots)
    {
        BotShareShown++;
        return Task.CompletedTask;
    }

    public bool ConfirmBotImportAnswer { get; set; } = true;
    public Octopus.Core.Bots.BotSharePayload? LastBotImportPayload { get; private set; }

    public Task<bool> ConfirmBotImportAsync(Octopus.Core.Bots.BotSharePayload payload)
    {
        LastBotImportPayload = payload;
        return Task.FromResult(ConfirmBotImportAnswer);
    }

    public Task<bool> ConfirmAsync(string title, string description, string confirmText, bool isDanger = false)
    {
        Asked++;
        LastTitle = title;
        LastDescription = description;
        return Task.FromResult(Answer);
    }
}

public sealed class FakePickerService : IPickerService
{
    public string? Folder { get; set; }
    public string? File { get; set; }
    public Task<string?> PickFolderAsync() => Task.FromResult(Folder);
    public Task<string?> PickFileAsync(string? startDirectory) => Task.FromResult(File);
    public string? Image { get; set; }
    public Task<string?> PickImageAsync() => Task.FromResult(Image);
}

/// <summary>A project store that counts its writes: the exit test checks that closing saved.</summary>
public sealed class RecordingProjectStore : IProjectStore
{
    private ProjectsDocument _document = new();

    public int Saves { get; private set; }

    public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_document);

    public Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken = default)
    {
        Saves++;
        _document = document;
        return Task.CompletedTask;
    }
}

/// <summary>Sessions without reading any agent's folders: tests never look at the person's own (~/.claude, ~/.codex).</summary>
public sealed class FakeAgentSessionService : IAgentSessionService
{
    public const string Team = "/home/u/.claude";
    public const string Pro = "/home/u/.rec/claude/accounts/51691/config";

    public Dictionary<string, AgentSessionGroup> Groups { get; } = [];
    /// <summary>Optional per-path overrides keyed by <c>(AgentId, NormalizedPath)</c> for multi-workspace session tests.</summary>
    public Dictionary<(string AgentId, string Path), AgentSessionGroup> GroupsByPath { get; } = [];
    public SessionMigrationResult NextResult { get; set; } = new(SessionMigrationKind.Handoff, Prompt: "# Handoff");
    public SessionMigrationRequest? LastRequest { get; private set; }

    public bool CanRead(string agentId) => agentId is "claude" or "codex";

    public Task<IReadOnlyList<AgentSessionAccount>> AccountsAsync(AgentDefinition agent, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgentSessionAccount>>(agent.Id == "claude"
            ? [new(Team, "Team", true, []), new(Pro, "Pro", false, [])]
            : [new("/home/u/.codex", "codex", true, [])]);

    public Task<AgentSessionGroup> ListAsync(AgentDefinition agent, string projectPath, CancellationToken cancellationToken = default)
    {
        var normalized = projectPath.TrimEnd('/', '\\');
        var match = GroupsByPath.FirstOrDefault(kv =>
            kv.Key.AgentId == agent.Id
            && string.Equals(kv.Key.Path.TrimEnd('/', '\\'), normalized, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
        if (match.Value is not null) return Task.FromResult(match.Value);
        return Task.FromResult(Groups.GetValueOrDefault(agent.Id) ?? new AgentSessionGroup(agent, CanRead(agent.Id), []));
    }

    public Task<SessionTranscript> ReadAsync(AgentSessionInfo session, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SessionTranscript(session, []));

    public IReadOnlyList<SessionMigrationKind> MigrationKinds(string sourceAgentId, AgentDefinition target) =>
        target.Id == sourceAgentId ? [SessionMigrationKind.Native] : target.Id == "codex" ? [SessionMigrationKind.Handoff] : [];

    public Task<SessionMigrationResult> MigrateAsync(AgentSessionInfo session, SessionMigrationKind kind, SessionMigrationRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(NextResult);
    }

    public Dictionary<string, string>? AccountEnvironment(AgentDefinition agent, string accountDirectory, IReadOnlyDictionary<string, string>? inherited) =>
        accountDirectory == Team ? null : new() { ["CLAUDE_CONFIG_DIR"] = accountDirectory };
}

/// <summary>Deterministic in-memory model discovery: tests never read the host's ~/.codex, ~/.claude, or network.</summary>
public sealed class FakeModelDiscovery : IModelDiscovery
{
    public Dictionary<string, IReadOnlyList<AgentOption>> ModelsByAgent { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string AgentId, string Executable, IReadOnlyDictionary<string, string>? Env)> Calls { get; } = [];

    public Task<IReadOnlyList<AgentOption>> DiscoverAsync(
        AgentDefinition definition,
        string executable,
        bool refresh = false,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        Calls.Add((definition.Id, executable, environmentOverrides));
        return Task.FromResult<IReadOnlyList<AgentOption>>(
            ModelsByAgent.TryGetValue(definition.Id, out var list) ? list : []);
    }
}

/// <summary>In-memory auto-start service: tests never register real OS login items.</summary>
public sealed class FakeAutoStartService : IAutoStartService
{
    public bool IsSupported { get; set; } = true;
    public bool Enabled { get; set; }
    public List<bool> Calls { get; } = [];

    public bool IsEnabled() => IsSupported && Enabled;

    public bool SetEnabled(bool enabled)
    {
        if (!IsSupported)
        {
            return false;
        }
        Enabled = enabled;
        Calls.Add(enabled);
        return true;
    }
}
