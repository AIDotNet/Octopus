using Octopus.Agents;
using Octopus.Agents.Signals;
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
    Octopus.Ssh.ISshHostCatalog? hosts = null) : IItemLaunchService
{
    /// <summary>Points at an <c>octopus-signal</c> build when it is not next to the app (development).</summary>
    public const string ForwarderVariable = "OCTOPUS_SIGNAL_FORWARDER";

    public bool Resumes(ProjectItem item) =>
        item.Kind == ProjectItemKind.Agent && projects.WasRestored(item.Id) && settings.Current.Terminal.ResumeAgentsOnRestart;

    public LaunchSpec Plan(Project project, ProjectItem item, bool allowResume = true)
    {
        var current = settings.Current;
        var cwd = item.WorkingDirectory ?? project.Path;
        if (item.Kind != ProjectItemKind.Agent)
        {
            // The terminal shares its connection with the folder listing: a password given here serves both.
            // The debug log is what the terminal shows of the connection while it is made (see SshConnectProgress).
            if (project.SshHost is not { } host)
            {
                return planner.PlanShell(cwd, current.Terminal.Shell);
            }
            var known = hosts?.Current.Hosts.FirstOrDefault(h => h.Alias == host);
            return known?.Protocol switch
            {
                Octopus.Ssh.SshHostProtocol.Mosh => planner.PlanMoshShell(host, cwd),
                Octopus.Ssh.SshHostProtocol.Telnet => planner.PlanTelnet(known.HostName, known.Port),
                _ => planner.PlanRemoteShell(host, cwd, current.Terminal.Shell, remote is null ? null : [.. remote.TerminalOptions, .. remote.StartConnectLog(item.Id)]),
            };
        }

        if (project.SshHost is not null)
        {
            throw new InvalidOperationException("Agents do not run in a project on an SSH host.");
        }
        var definition = item.AgentId is null ? null : catalog.Find(item.AgentId);
        if (definition is null)
        {
            throw new InvalidOperationException($"Unknown agent '{item.AgentId}'.");
        }
        // The prompt is for the first start only; a restored tab continues the last session instead.
        var launch = new AgentLaunchOptions
        {
            Model = item.Launch?.Model,
            Effort = item.Launch?.Effort,
            Fast = item.Launch?.Fast,
            Prompt = item.Launch?.Prompt,
            Resume = allowResume && Resumes(item),
        };
        var request = new AgentCompileRequest(
            definition,
            current.Agents.ProfileOf(definition.Id),
            launch,
            current.Agents.DefaultPermission,
            scanner.Find(definition.Id)?.Path);
        var command = compiler.Compile(request);
        // The signal channel (design/plugins/11-补充能力.md §1): status line, hooks and notify reach Octopus through
        // the forwarder. Nothing of the user's own configuration is changed; without a hub or forwarder the agent
        // simply starts as before.
        if (Attach(definition, item, cwd, command) is { } signal)
        {
            command = compiler.Compile(request with { Signals = signal });
        }
        return planner.PlanAgent(command, cwd, current.Terminal.Shell);
    }

    private SignalAttachment? Attach(AgentDefinition definition, ProjectItem item, string cwd, AgentCommand command)
    {
        if (definition.Signals is null || signals?.Endpoint is not { } endpoint || attach is null || ForwarderPath() is not { } forwarder)
        {
            return null;
        }
        // The login environment is read once at start-up; until then the process environment stands in.
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
        foreach (var (key, value) in command.Environment) environment[key] = value;
        var plan = attach.Plan(new SignalAttachRequest(definition, item.Id, forwarder, endpoint, scanner.Find(definition.Id)?.Version, environment, cwd));
        return plan.Arguments.Count == 0 && plan.Environment.Count == 0 ? null : plan;
    }

    private static string? ForwarderPath()
    {
        var name = OperatingSystem.IsWindows() ? "octopus-signal.exe" : "octopus-signal";
        var beside = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(beside)) return beside;
        return Environment.GetEnvironmentVariable(ForwarderVariable) is { Length: > 0 } configured && File.Exists(configured) ? configured : null;
    }
}
