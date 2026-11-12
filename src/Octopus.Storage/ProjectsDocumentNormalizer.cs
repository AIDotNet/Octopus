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
            // A tunnel without an id cannot be started or told apart; a repeated id would make two rows one.
            project.Tunnels ??= [];
            var tunnelIds = new HashSet<string>();
            project.Tunnels.RemoveAll(t => t is null || string.IsNullOrEmpty(t.Id) || !tunnelIds.Add(t.Id));
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
        return document;
    }
}
