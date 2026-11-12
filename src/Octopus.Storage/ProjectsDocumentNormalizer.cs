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
        }
        if (document.ActiveProjectId is not null && !projectIds.Contains(document.ActiveProjectId))
            document.ActiveProjectId = null;
        return document;
    }
}
