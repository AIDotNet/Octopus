using Octopus.Agents;
using Octopus.Agents.Accounts;
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
    RemoteFiles remote,
    Octopus.App.Remote.RemoteProjects? remoteProjects = null,
    Octopus.App.Remote.RemoteClientRuntime? remoteHosts = null,
    Octopus.App.Remote.RemoteProjectAccess? access = null,
    IAgentAccountService? accounts = null) : IItemOpener
{
    public ProjectItem? OpenTerminal()
    {
        var project = projects.Current;
        if (project is null)
        {
            return null;
        }
        if (ProjectLocation.IsOnRemoteHost(project))
        {
            // Made on the host; it shows up here (and comes to the front) once the host has it.
            _ = CreateOnHostAsync(project, null, null);
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
        if (project is not null && ProjectLocation.IsOnRemoteHost(project))
        {
            // The host's own agent of that id, with the host's settings; only what was chosen here goes along.
            _ = CreateOnHostAsync(project, agentId, launch);
            return null;
        }
        var definition = catalog.Find(agentId);
        // Agents need this computer's files, programs and hooks, so they do not start in a project on an SSH host.
        if (project is null || definition is null || !ProjectLocation.CanRunAgents(project))
        {
            return null;
        }
        // For globalKeyring agents (Antigravity CLI), ensure the active keychain snapshot matches the resolved account before the PTY starts.
        if (accounts is not null && definition.Accounts is { Mode: AccountModes.GlobalKeyring })
        {
            var profile = settings.Current.Agents.ProfileOf(definition.Id);
            var resolved = accounts.Resolve(definition, profile, project, launch);
            if (resolved.Account is { } targetAccount && accounts.GetActiveGlobalAccountId(definition.Id) != targetAccount.Id)
            {
                try
                {
                    accounts.SwitchGlobalKeyringAsync(definition, targetAccount.Id).GetAwaiter().GetResult();
                }
                catch
                {
                    // If the snapshot is missing or keychain refuses, still open the terminal so the CLI can report or re-login.
                }
            }
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
        // An SSH host's tree, or that of a computer running Octopus: either way the file is copied here and written back.
        var tree = access?.TreeOf(project) ?? (project?.SshHost is { } alias ? remote.TreeFor(alias) : null);
        if (project is null || tree is null || Octopus.App.Remote.RemoteProjectAccess.MachineOf(project) is not { } host)
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
            var copy = await remote.CopyAsync(tree, host, remotePath);
            var name = remotePath.Split('/').LastOrDefault(s => s.Length > 0) ?? remotePath;
            var item = new ProjectItem { Kind = ProjectItemKind.File, FilePath = copy, RemotePath = remotePath, Title = name };
            projects.AddItem(project, item);
            return item;
        }
        catch (Exception e) when (e is SshCommandException or IOException)
        {
            var reason = e is SshFileTooLargeException or Octopus.FileSystem.RemoteFileTooLargeException
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
        if (project is null || !ProjectLocation.IsOnThisComputer(project))
        {
            return null;
        }
        var path = await pickers.PickFileAsync(project.Path);
        return path is null ? null : OpenFile(path);
    }

    public async Task<Project?> PickAndAddProjectAsync()
    {
        var choice = await dialogs.AskNewProjectAsync();
        if (choice is null) return null;
        if (choice.RemoteHost is { } hostId && remoteProjects is not null)
        {
            try
            {
                return await remoteProjects.AddAsync(hostId, choice.Path, choice.Name.Length == 0 ? null : choice.Name);
            }
            catch (Exception e) when (e is IOException or Octopus.Remote.Protocol.RemoteException or Octopus.Remote.Security.HandshakeException)
            {
                await dialogs.ConfirmAsync(AppLangResources.Get(AppLangResourceKind.RemoteProjectFailed), e.Message, AppLangResources.Get(AppLangResourceKind.RemoteOk));
                return null;
            }
        }
        return projects.AddProject(choice.Path, choice.Name.Length == 0 ? null : choice.Name, choice.SshHost);
    }

    private async Task CreateOnHostAsync(Project project, string? agentId, AgentLaunchOptions? launch)
    {
        if (remoteProjects is null) return;
        try
        {
            await remoteProjects.CreateItemAsync(project, agentId, launch);
        }
        catch (Exception e) when (e is IOException or Octopus.Remote.Protocol.RemoteException)
        {
            await dialogs.ConfirmAsync(AppLangResources.Get(AppLangResourceKind.RemoteItemFailed), e.Message, AppLangResources.Get(AppLangResourceKind.RemoteOk));
        }
    }

    public ProjectItem? OpenDefaultAgent()
    {
        if (projects.Current is { RemoteHost: { } host } && remoteHosts is not null)
        {
            // The host's default agent, or its first one.
            var agents = remoteHosts.AgentsOf(host);
            var chosen = agents.FirstOrDefault(a => a.IsDefault) ?? agents.FirstOrDefault();
            return chosen is null ? null : OpenAgent(chosen.Id, null);
        }
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
