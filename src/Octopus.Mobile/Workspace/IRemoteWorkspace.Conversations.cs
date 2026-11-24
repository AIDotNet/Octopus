namespace Octopus.Mobile.Workspace;

// GUI conversations (design/ui/移动端功能扩展方案.html, G-A): the host keeps the agent's conversation; the phone shows a
// flattened copy of its timeline and sends prompts, answers and choices back.
public partial interface IRemoteWorkspace
{
    /// <summary>
    /// True when the phone can show the GUI conversations of this host (its Octopus speaks <c>acp.gui</c>). Without it
    /// a GUI session only has its details page, with a note that the computer's Octopus is too old.
    /// </summary>
    bool SupportsConversations(RemoteHost host) => false;

    /// <summary>
    /// Attaches to the GUI conversation of a session: the result shows the host's timeline and stays in step with it
    /// until it is disposed. Throws <see cref="IOException"/> (with words for the person) when the host cannot.
    /// </summary>
    Task<RemoteConversation> OpenConversationAsync(RemoteSession session, CancellationToken cancellationToken = default) =>
        throw new IOException("GUI conversations are not supported.");
}
