using Octopus.Agents;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Core.Terminals;
using Octopus.Storage;
using Octopus.Terminal;

namespace Octopus.App.Services;

public sealed class ItemLaunchService(
    ISettingsStore settings,
    IAgentCatalog catalog,
    IAgentScanner scanner,
    IAgentCommandCompiler compiler,
    ITerminalLaunchPlanner planner,
    IProjectService projects) : IItemLaunchService
{
    public LaunchSpec Plan(Project project, ProjectItem item)
    {
        var current = settings.Current;
        var cwd = item.WorkingDirectory ?? project.Path;
        if (item.Kind != ProjectItemKind.Agent)
        {
            return planner.PlanShell(cwd, current.Terminal.Shell);
        }

        var definition = item.AgentId is null ? null : catalog.Find(item.AgentId);
        if (definition is null)
        {
            throw new InvalidOperationException($"Unknown agent '{item.AgentId}'.");
        }
        // The prompt is for the first start only; a restored tab continues the last session instead.
        var restored = projects.WasRestored(item.Id);
        var launch = new AgentLaunchOptions
        {
            Model = item.Launch?.Model,
            Effort = item.Launch?.Effort,
            Fast = item.Launch?.Fast,
            Prompt = item.Launch?.Prompt,
            Resume = restored && current.Terminal.ResumeAgentsOnRestart,
        };
        var request = new AgentCompileRequest(
            definition,
            current.Agents.ProfileOf(definition.Id),
            launch,
            current.Agents.DefaultPermission,
            scanner.Find(definition.Id)?.Path);
        return planner.PlanAgent(compiler.Compile(request), cwd, current.Terminal.Shell);
    }
}
