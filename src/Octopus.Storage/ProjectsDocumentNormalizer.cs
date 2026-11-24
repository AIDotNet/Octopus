using Octopus.Core.Projects;

namespace Octopus.Storage;

internal static class ProjectsDocumentNormalizer
{
    public static ProjectsDocument Normalize(ProjectsDocument? document)
    {
        document ??= new ProjectsDocument();
        document.Projects ??= [];
        var projectIds = new HashSet<string>();
        document.Projects.RemoveAll(p => p is null || !projectIds.Add(p.Id ?? ""));
        var itemIds = new HashSet<string>();
        foreach (var project in document.Projects)
        {
            project.Items ??= [];
            project.Items.RemoveAll(i => i is null || !itemIds.Add(i.Id ?? ""));
            if (project.ActiveItemId is not null && project.Items.All(i => i.Id != project.ActiveItemId))
                project.ActiveItemId = null;
            project.Workspaces ??= [];
            var workspaceIds = new HashSet<string>();
            project.Workspaces.RemoveAll(w => w is null || string.IsNullOrWhiteSpace(w.Id) || string.IsNullOrWhiteSpace(w.Path) || !workspaceIds.Add(w.Id));
            foreach (var workspace in project.Workspaces)
            {
                workspace.Name ??= "";
                workspace.Branch ??= "";
            }
            foreach (var item in project.Items)
            {
                if (item.WorkspaceId is not null && !workspaceIds.Contains(item.WorkspaceId))
                {
                    item.WorkspaceId = null;
                }
            }
            // A tunnel without an id cannot be started or told apart; a repeated id would make two rows one.
            project.Tunnels ??= [];
            var tunnelIds = new HashSet<string>();
            project.Tunnels.RemoveAll(t => t is null || string.IsNullOrEmpty(t.Id) || !tunnelIds.Add(t.Id));
            project.AgentAccounts ??= [];
            foreach (var key in project.AgentAccounts.Keys.Where(k => string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(project.AgentAccounts[k])).ToList())
            {
                project.AgentAccounts.Remove(key);
            }
        }
        if (document.ActiveProjectId is not null && !projectIds.Contains(document.ActiveProjectId))
            document.ActiveProjectId = null;

        document.Groups ??= [];
        var groupIds = new HashSet<string>();
        document.Groups.RemoveAll(g => g is null || !groupIds.Add(g.Id ?? ""));
        foreach (var group in document.Groups)
        {
            group.Name ??= "";
        }
        foreach (var project in document.Projects)
        {
            if (project.GroupId is not null && !groupIds.Contains(project.GroupId))
                project.GroupId = null;
        }
        if (document.ExpandedGroupId is not null && !groupIds.Contains(document.ExpandedGroupId))
            document.ExpandedGroupId = null;

        if (!string.IsNullOrWhiteSpace(document.ActiveDeviceKey))
            document.ActiveDeviceKey = DeviceKeys.Normalize(document.ActiveDeviceKey);
        else
            document.ActiveDeviceKey = null;

        document.LastProjectByDevice ??= [];
        foreach (var key in document.LastProjectByDevice.Keys.Where(k => string.IsNullOrWhiteSpace(k) || !projectIds.Contains(document.LastProjectByDevice[k])).ToList())
        {
            document.LastProjectByDevice.Remove(key);
        }

        document.DeviceAppearances ??= [];
        foreach (var key in document.DeviceAppearances.Keys.Where(k => string.IsNullOrWhiteSpace(k) || document.DeviceAppearances[k] is null).ToList())
        {
            document.DeviceAppearances.Remove(key);
        }
        return document;
    }
}
