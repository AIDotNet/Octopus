using Octopus.Agents;
using Octopus.App.Common;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Storage;

namespace Octopus.App.Services;

public sealed class ItemOpener(
    IProjectService projects,
    IAgentCatalog catalog,
    IAgentScanner scanner,
    ISettingsStore settings,
    IPickerService pickers,
    IDialogService dialogs,
    IShellResolver shells,
    PathComparer paths) : IItemOpener
{
    public ProjectItem? OpenTerminal()
    {
        var project = projects.Current;
        if (project is null)
        {
            return null;
        }
        var chosen = settings.Current.Terminal.Shell;
        var info = string.IsNullOrEmpty(chosen) ? null : shells.Describe(chosen);
        var item = new ProjectItem
        {
            Kind = ProjectItemKind.Terminal,
            Title = (info ?? shells.GetDefaultShell()).Name,
        };
        projects.AddItem(project, item);
        return item;
    }

    public ProjectItem? OpenAgent(string agentId, AgentLaunchOptions? launch = null)
    {
        var project = projects.Current;
        var definition = catalog.Find(agentId);
        if (project is null || definition is null)
        {
            return null;
        }
        var item = new ProjectItem
        {
            Kind = ProjectItemKind.Agent,
            AgentId = agentId,
            Title = definition.Name,
            Launch = launch,
        };
        projects.AddItem(project, item);
        return item;
    }

    public ProjectItem? OpenFile(string path)
    {
        var project = projects.Current;
        if (project is null)
        {
            return null;
        }
        var existing = project.Items.FirstOrDefault(i => i.Kind == ProjectItemKind.File && paths.Equals(i.FilePath, path));
        if (existing is not null)
        {
            projects.ActivateItem(existing.Id);
            return existing;
        }
        var item = new ProjectItem { Kind = ProjectItemKind.File, FilePath = path, Title = Path.GetFileName(path) };
        projects.AddItem(project, item);
        return item;
    }

    public async Task<ProjectItem?> PickAndOpenFileAsync()
    {
        var project = projects.Current;
        if (project is null)
        {
            return null;
        }
        var path = await pickers.PickFileAsync(project.Path);
        return path is null ? null : OpenFile(path);
    }

    public async Task<Project?> PickAndAddProjectAsync()
    {
        var choice = await dialogs.AskNewProjectAsync();
        return choice is null ? null : projects.AddProject(choice.Path, choice.Name.Length == 0 ? null : choice.Name);
    }

    public ProjectItem? OpenDefaultAgent()
    {
        var id = settings.Current.Agents.DefaultAgentId;
        if (id is null || scanner.Find(id) is null || !settings.Current.Agents.ProfileOf(id).Enabled)
        {
            id = scanner.Current.Installations
                .Select(i => i.AgentId)
                .FirstOrDefault(a => catalog.Find(a) is not null && settings.Current.Agents.ProfileOf(a).Enabled);
        }
        return id is null ? null : OpenAgent(id, null);
    }
}
