using Octopus.Core.Projects;

namespace Octopus.App.Collab.Sessions;

/// <summary>
/// GUI conversations in projects of paired computers, followed over the remote protocol (<see cref="RemoteCollabSession"/>).
/// </summary>
internal sealed class RemoteSessionProvider(
    CollabSessions sessions,
    Func<ICollabRemoteHosts?> remotes,
    ICollabCoordinator coordinator,
    Action<string> changed) : ICollabSessionProvider
{
    public bool Handles(Project project, ProjectItem item) => ProjectLocation.IsOnRemoteHost(project);

    public ICollabSessionBridge? TryAttach(Project project, ProjectItem item) => null; // following one means connecting

    public async Task<ICollabSessionBridge?> EnsureAsync(Project project, ProjectItem item, CancellationToken ct)
    {
        if (remotes() is not { } hosts || project.RemoteHost is not { Length: > 0 } hostId) return null;
        ICollabRemoteHost host;
        try
        {
            host = await hosts.ConnectAsync(hostId, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
        if (!host.SupportsAcpGui) return null;
        var followed = await FollowAsync(host, hosts.HostName(hostId), project, item.Id, item.AgentId ?? "terminal", item.DisplayTitle, item.ParentItemId, fresh: false, ct).ConfigureAwait(false);
        return followed ? sessions.Find(item.Id) : null;
    }

    /// <summary>
    /// Follows a conversation on <paramref name="host"/>. <paramref name="fresh"/>: just started for a delegated task, so an
    /// answer already in its first snapshot is that task's. Returns false when it cannot be followed (a terminal item,
    /// one that vanished).
    /// </summary>
    public async Task<bool> FollowAsync(
        ICollabRemoteHost host,
        string hostName,
        Project project,
        string itemId,
        string agentId,
        string title,
        string? parentItemId,
        bool fresh,
        CancellationToken ct)
    {
        var session = new RemoteCollabSession(host, hostName, itemId, agentId, title, $"{hostName}:{project.Path}", parentItemId, fresh, coordinator, closed =>
        {
            if (sessions.Unregister(closed)) changed(closed.ItemId);
        });
        if (!ReferenceEquals(sessions.Adopt(session), session)) return true; // followed concurrently
        try
        {
            await session.StartAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            sessions.Unregister(session);
            return false;
        }
    }

    public void Dispose()
    {
    }
}
