using Octopus.Agents;
using Octopus.Agents.Accounts;
using Octopus.Agents.Providers;
using Octopus.Agents.Signals;
using Octopus.App.Collab;
using Octopus.App.Common;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Core.Terminals;
using Octopus.Platform;
using Octopus.Storage;
using Octopus.Terminal;

namespace Octopus.App.Services;

public sealed class ItemLaunchService(
    ISettingsStore settings,
    IAgentCatalog catalog,
    IAgentScanner scanner,
    IAgentCommandCompiler compiler,
    ITerminalLaunchPlanner planner,
    IProjectService projects,
    ISignalHub? signals = null,
    ISignalAttachPlanner? attach = null,
    ILoginEnvironment? login = null,
    RemoteFiles? remote = null,
    Octopus.Ssh.ISshHostCatalog? hosts = null,
    ISystemProxy? systemProxy = null,
    PathComparer? paths = null,
    IAgentAccountService? accounts = null,
    CollabCliMcpInjector? collabInjector = null,
    IAgentBackendResolver? backends = null,
    ProviderMaterializer? materializer = null) : IItemLaunchService
{
    /// <summary>Points at an <c>octopus-signal</c> build when it is not next to the app (development).</summary>
    public const string ForwarderVariable = "OCTOPUS_SIGNAL_FORWARDER";

    public bool Resumes(ProjectItem item)
    {
        if (item.Kind != ProjectItemKind.Agent || item.AgentId is null)
        {
            return false;
        }
        var project = projects.FindProjectOf(item.Id);
        // An agent on another computer was never stopped by this app's restart: there is nothing to resume.
        if (project is not null && ProjectLocation.IsOnRemoteHost(project))
        {
            return false;
        }
        var definition = catalog.Find(item.AgentId);
        if (definition is null)
        {
            return false;
        }
        var restored = projects.WasRestored(item.Id);
        if (restored && !settings.Current.Terminal.ResumeAgentsOnRestart)
        {
            return false;
        }
        if (HasUsableSessionId(item, definition))
        {
            return true;
        }
        if (definition.Capabilities.Resume?.ContinueLatest is not { Count: > 0 })
        {
            return false;
        }
        if (item.Launch?.Resume == true)
        {
            return true;
        }
        return restored && (project is null || IsContinueLatestOwner(project, item, definition));
    }

    /// <summary>What a tab of a project on another computer is started with: the program runs there, nothing is launched here.</summary>
    public static LaunchSpec RemoteSession { get; } = new("octopus-remote-session", [], null, new Dictionary<string, string>());

    public LaunchSpec Plan(Project project, ProjectItem item, bool allowResume = true) =>
        PlanCore(project, item, allowResume, useLease: false, leaseId: null).Spec;

    public TerminalLaunch PlanLaunch(Project project, ProjectItem item, bool allowResume = true, string? leaseId = null) =>
        PlanCore(project, item, allowResume, useLease: true, leaseId: leaseId);

    private TerminalLaunch PlanCore(Project project, ProjectItem item, bool allowResume, bool useLease, string? leaseId)
    {
        // The host plans and runs it (with its own agents, settings and hooks); this side only shows its terminal.
        if (ProjectLocation.IsOnRemoteHost(project)) return new TerminalLaunch(RemoteSession);
        var current = settings.Current;
        var cwd = EffectiveWorkingDirectory(project, item);
        if (item.Kind != ProjectItemKind.Agent)
        {
            // The terminal shares its connection with the folder listing: a password given here serves both.
            // The debug log is what the terminal shows of the connection while it is made (see SshConnectProgress).
            if (project.SshHost is not { } host)
            {
                return new TerminalLaunch(planner.PlanShell(cwd, current.Terminal.Shell));
            }
            var known = hosts?.Current.Hosts.FirstOrDefault(h => h.Alias == host);
            var shellSpec = known?.Protocol switch
            {
                Octopus.Ssh.SshHostProtocol.Mosh => planner.PlanMoshShell(host, cwd),
                Octopus.Ssh.SshHostProtocol.Telnet => planner.PlanTelnet(known.HostName, known.Port),
                _ => WithAsciiAlias(planner.PlanRemoteShell(host, cwd, current.Terminal.Shell, remote is null ? null : [.. remote.TerminalOptions, .. remote.StartConnectLog(item.Id)])),
            };
            return new TerminalLaunch(shellSpec);
        }

        if (!ProjectLocation.CanRunAgents(project))
        {
            throw new InvalidOperationException("Agents do not run in a project on an SSH host.");
        }
        var definition = (item.AgentId is null ? null : catalog.Find(item.AgentId)) ?? throw new InvalidOperationException($"Unknown agent '{item.AgentId}'.");
        // The prompt is for the first start only; a restored tab continues its session instead (when enabled).
        var restored = projects.WasRestored(item.Id);
        var canResume = allowResume && (!restored || current.Terminal.ResumeAgentsOnRestart);
        var sessionId = canResume && HasUsableSessionId(item, definition) ? item.Launch!.SessionId : null;
        var continueLatest = canResume
            && sessionId is null
            && definition.Capabilities.Resume?.ContinueLatest is { Count: > 0 }
            && (item.Launch?.Resume == true || (restored && IsContinueLatestOwner(project, item, definition)));
        var launch = new AgentLaunchOptions
        {
            Model = item.Launch?.Model,
            Effort = item.Launch?.Effort,
            Fast = item.Launch?.Fast,
            Permission = item.Launch?.Permission,
            AccountId = item.Launch?.AccountId,
            BackendRef = item.Launch?.BackendRef,
            Prompt = restored ? null : item.Launch?.Prompt,
            Resume = continueLatest,
            SessionId = sessionId,
            Env = item.Launch?.Env,
        };
        var profile = current.Agents.ProfileOf(definition.Id);
        IReadOnlyDictionary<string, string>? backendEnv = null;
        IReadOnlyList<string>? unsetEnv = null;
        string? modelPrefix = null;
        if (item.Launch?.TunnelProvider is { } tunnelSpec
            && !string.IsNullOrWhiteSpace(item.Launch.TunnelLoopbackBaseUrl)
            && materializer is not null)
        {
            var chosenModel = !string.IsNullOrWhiteSpace(launch.Model) ? launch.Model : profile.Model;
            var materialized = materializer.MaterializeTunnel(definition, tunnelSpec, item.Launch.TunnelLoopbackBaseUrl!, chosenModel);
            backendEnv = materialized.Environment;
            unsetEnv = materialized.UnsetEnvironment;
            modelPrefix = materialized.ModelPrefix;
        }
        else if (backends is not null)
        {
            var resolved = backends.ResolveAsync(definition, profile, project, launch).GetAwaiter().GetResult();
            backendEnv = resolved.Environment;
            unsetEnv = resolved.UnsetEnvironment;
            modelPrefix = resolved.ModelPrefix;
        }
        else if (accounts is not null)
        {
            var resolvedAccount = accounts.Resolve(definition, profile, project, launch);
            backendEnv = resolvedAccount.Environment;
            unsetEnv = resolvedAccount.UnsetEnvironment;
        }
        var request = new AgentCompileRequest(
            definition,
            profile,
            launch,
            current.Agents.DefaultPermission,
            scanner.Find(definition.Id)?.Path,
            AdditionalDirectories: ResolveAdditionalDirectories(project, cwd),
            AccountEnvironment: backendEnv,
            UnsetEnvironment: unsetEnv,
            ModelPrefix: modelPrefix);
        var command = compiler.Compile(request);
        // The signal channel (design/plugins/11-补充能力.md §1): status line, hooks and notify reach Octopus through
        // the forwarder. Nothing of the user's own configuration is changed; without a hub or forwarder the agent
        // simply starts as before.
        var signal = Attach(definition, item, cwd, command);
        var collab = current.Agents.Collab is { Enabled: true, EnableTerminalBridge: true }
            ? (useLease
                ? collabInjector?.PlanLease(definition, item, ResolveForwarderPath(), leaseId)
                : collabInjector?.Plan(definition, item, ResolveForwarderPath()))
            : null;
        try
        {
            if (signal is not null || collab is not null)
            {
                var mergedArgs = new List<string>();
                var mergedEnv = new Dictionary<string, string>(StringComparer.Ordinal);
                if (signal is not null)
                {
                    mergedArgs.AddRange(signal.Arguments);
                    foreach (var (k, v) in signal.Environment) mergedEnv[k] = v;
                }
                if (collab is not null)
                {
                    mergedArgs.AddRange(collab.Arguments);
                    foreach (var (k, v) in collab.Environment) mergedEnv[k] = v;
                }
                command = compiler.Compile(request with { Signals = new SignalAttachment(mergedArgs, mergedEnv) });
            }
            var spec = planner.PlanAgent(UseSystemProxy(command, current.Agents.UseSystemProxy), cwd, current.Terminal.Shell);
            return new TerminalLaunch(spec, useLease ? collab?.Lease : null);
        }
        catch
        {
            if (useLease)
            {
                collab?.Lease?.Dispose();
            }
            throw;
        }
    }

    private static string EffectiveWorkingDirectory(Project project, ProjectItem item)
    {
        var cwd = string.IsNullOrWhiteSpace(item.WorkingDirectory) ? project.Path : item.WorkingDirectory;
        // A deleted worktree directory falls back to the project root so restored tabs still open cleanly.
        if (ProjectLocation.IsOnThisComputer(project) && !Directory.Exists(cwd) && Directory.Exists(project.Path))
        {
            return project.Path;
        }
        return cwd;
    }

    private static bool HasUsableSessionId(ProjectItem item, AgentDefinition definition) =>
        !string.IsNullOrWhiteSpace(item.Launch?.SessionId) && definition.Capabilities.Resume?.ById is { Count: > 0 };

    /// <summary>
    /// When several restored terminal tabs of the same agent share a working directory and none has an exact session id,
    /// only one of them (the active tab, else the last one) gets <c>continueLatest</c> so they do not all attach to
    /// the same conversation.
    /// </summary>
    private bool IsContinueLatestOwner(Project project, ProjectItem item, AgentDefinition definition)
    {
        var comparer = paths ?? new PathComparer(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows());
        var targetCwd = EffectiveWorkingDirectory(project, item);
        var candidates = project.Items.Where(i =>
            i.Kind == ProjectItemKind.Agent
            && i.Surface != AgentSurface.Gui
            && string.Equals(i.AgentId, item.AgentId, StringComparison.Ordinal)
            && projects.WasRestored(i.Id)
            && !HasUsableSessionId(i, definition)
            && comparer.Equals(EffectiveWorkingDirectory(project, i), targetCwd)).ToList();
        if (candidates.Count <= 1)
        {
            return true;
        }
        var active = candidates.FirstOrDefault(i => i.Id == project.ActiveItemId);
        return (active ?? candidates[^1]).Id == item.Id;
    }

    /// <summary>
    /// Other existing local project directories in the same group when the group merges workspaces; null otherwise.
    /// Excludes <paramref name="cwd"/> and deduplicates by platform path rules.
    /// </summary>
    private IReadOnlyList<string>? ResolveAdditionalDirectories(Project project, string cwd)
    {
        if (!ProjectLocation.IsOnThisComputer(project) || project.GroupId is not { } groupId)
        {
            return null;
        }
        var group = projects.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group is null || !group.MergeWorkspaces)
        {
            return null;
        }
        var comparer = paths ?? new PathComparer(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows());
        var seen = new HashSet<string>(comparer);
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            seen.Add(cwd);
        }
        var result = new List<string>();
        foreach (var member in projects.Projects)
        {
            if (member.GroupId != groupId || !ProjectLocation.IsOnThisComputer(member))
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(member.Path) || !seen.Add(member.Path) || !Directory.Exists(member.Path))
            {
                continue;
            }
            result.Add(member.Path);
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>ssh does not take an alias like <c>香港主服务器</c> on its command line; it is given an ASCII name for it (see <see cref="Octopus.Ssh.SshAlias"/>).</summary>
    private static LaunchSpec WithAsciiAlias(LaunchSpec spec) => spec with { Arguments = [.. Octopus.Ssh.SshAlias.Prepare(spec.Arguments)] };

    /// <summary>
    /// Adds the system proxy as <c>HTTP_PROXY</c>, <c>HTTPS_PROXY</c> and <c>NO_PROXY</c> (command-line agents read the
    /// environment, not the system settings). A proxy the user already set, in the profile, the login environment or
    /// the process environment, is left alone.
    /// </summary>
    private AgentCommand UseSystemProxy(AgentCommand command, bool enabled)
    {
        if (!enabled || systemProxy?.Resolve() is not { } proxy)
        {
            return command;
        }
        var environment = BaseEnvironment();
        foreach (var (key, value) in command.Environment) environment[key] = value;
        var added = AgentProxyEnvironment.Compute(proxy, environment);
        if (added.Count == 0)
        {
            return command;
        }
        var merged = new Dictionary<string, string>(command.Environment, StringComparer.Ordinal);
        foreach (var (key, value) in added) merged[key] = value;
        return command with { Environment = merged };
    }

    /// <summary>The login environment, read once at start-up; until then the process environment stands in.</summary>
    private Dictionary<string, string> BaseEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (login?.GetAsync() is { IsCompletedSuccessfully: true } ready)
        {
            foreach (var (key, value) in ready.Result) environment[key] = value;
        }
        else
        {
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string key && entry.Value is string value) environment[key] = value;
            }
        }
        return environment;
    }

    private SignalAttachment? Attach(AgentDefinition definition, ProjectItem item, string cwd, AgentCommand command)
    {
        if (definition.Signals is null || signals?.Endpoint is not { } endpoint || attach is null || ForwarderPath() is not { } forwarder)
        {
            return null;
        }
        var environment = BaseEnvironment();
        foreach (var (key, value) in command.Environment) environment[key] = value;
        var plan = attach.Plan(new SignalAttachRequest(definition, item.Id, forwarder, endpoint, scanner.Find(definition.Id)?.Version, environment, cwd));
        return plan.Arguments.Count == 0 && plan.Environment.Count == 0 ? null : plan;
    }

    internal static string? ResolveForwarderPath() => ForwarderPath();

    private static string? ForwarderPath()
    {
        var name = OperatingSystem.IsWindows() ? "octopus-signal.exe" : "octopus-signal";
        var beside = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(beside)) return beside;
        return Environment.GetEnvironmentVariable(ForwarderVariable) is { Length: > 0 } configured && File.Exists(configured) ? configured : null;
    }
}
