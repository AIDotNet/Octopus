using Octopus.Agents;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Ssh;
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
    PathComparer paths,
    RemoteFiles remote) : IItemOpener
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
            // On a host the tab says where it is: the shell of this computer is not the one it runs.
            Title = project.SshHost ?? (info ?? shells.GetDefaultShell()).Name,
        };
        projects.AddItem(project, item);
        return item;
    }

    public ProjectItem? OpenAgent(string agentId, AgentLaunchOptions? launch = null)
    {
        var project = projects.Current;
        var definition = catalog.Find(agentId);
        // Agents need this computer's files, programs and hooks, so they do not start in a project on an SSH host.
        if (project is null || definition is null || project.SshHost is not null)
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

    public ProjectItem? OpenView(string viewId, string title, string? state = null)
    {
        var project = projects.Current;
        if (project is null)
        {
            return null;
        }
        var item = new ProjectItem { Kind = ProjectItemKind.Plugin, ViewId = viewId, Title = title, ViewState = state };
        projects.AddItem(project, item);
        return item;
    }

    /// <summary>
    /// A file of the project's SSH host: it is copied to this computer (read-only) and opened as a tab that shows the path
    /// on the host. An open tab of the same file is activated. Null when the project is not on a host or the copy failed.
    /// </summary>
    public async Task<ProjectItem?> OpenRemoteFileAsync(string remotePath)
    {
        var project = projects.Current;
        if (project?.SshHost is not { } host)
        {
            return null;
        }
        // An open tab of the file is shown as it is: a new copy would replace the edits that are not saved yet.
        var existing = project.Items.FirstOrDefault(i => i.Kind == ProjectItemKind.File && i.RemotePath == remotePath);
        if (existing is not null)
        {
            projects.ActivateItem(existing.Id);
            return existing;
        }
        try
        {
            var copy = await remote.CopyAsync(host, remotePath);
            var name = remotePath.Split('/').LastOrDefault(s => s.Length > 0) ?? remotePath;
            var item = new ProjectItem { Kind = ProjectItemKind.File, FilePath = copy, RemotePath = remotePath, Title = name };
            projects.AddItem(project, item);
            return item;
        }
        catch (Exception e) when (e is SshCommandException or IOException)
        {
            var reason = e is SshFileTooLargeException
                ? AppLangResources.Format(AppLangResourceKind.RemoteFileTooLarge, RemoteFiles.MaxCopyBytes / (1024 * 1024))
                : e.Message;
            await dialogs.ConfirmAsync(
                AppLangResources.Get(AppLangResourceKind.RemoteOpenFailed),
                reason,
                AppLangResources.Get(AppLangResourceKind.RemoteOpenFailedClose));
            return null;
        }
    }

    public async Task<ProjectItem?> PickAndOpenFileAsync()
    {
        var project = projects.Current;
        // A file on an SSH host is not a file of this computer: the picker would look in the wrong place.
        if (project is null || project.SshHost is not null)
        {
            return null;
        }
        var path = await pickers.PickFileAsync(project.Path);
        return path is null ? null : OpenFile(path);
    }

    public async Task<Project?> PickAndAddProjectAsync()
    {
        var choice = await dialogs.AskNewProjectAsync();
        return choice is null ? null : projects.AddProject(choice.Path, choice.Name.Length == 0 ? null : choice.Name, choice.SshHost);
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
