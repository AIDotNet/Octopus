using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;

namespace Octopus.App.Collab.Sessions;

/// <summary>
/// Agents running as an ACP GUI tab on this computer. The tab's view model is the bridge and registers itself when it
/// starts; reaching one only means making sure its tab exists.
/// </summary>
internal sealed class LocalGuiSessionProvider(CollabSessions sessions, Func<ITabContentRegistry?>? tabs) : ICollabSessionProvider
{
    public bool Handles(Project project, ProjectItem item) =>
        !ProjectLocation.IsOnRemoteHost(project) && item.Surface == AgentSurface.Gui;

    public ICollabSessionBridge? TryAttach(Project project, ProjectItem item) => null; // only a registered tab counts

    public async Task<ICollabSessionBridge?> EnsureAsync(Project project, ProjectItem item, CancellationToken ct)
    {
        await CollabUi.InvokeAsync(() => tabs?.Invoke()?.GetOrCreate(project, item)).ConfigureAwait(false);
        return sessions.Find(item.Id);
    }

    public void Dispose()
    {
    }
}
