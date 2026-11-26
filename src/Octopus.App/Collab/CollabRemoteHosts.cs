using System.Runtime.CompilerServices;
using Octopus.App.Remote;
using Octopus.Remote.Client;
using Octopus.Remote.Protocol;

namespace Octopus.App.Collab;

/// <summary>
/// The paired Octopus hosts the collaboration bus can reach. Cross-host delegation only uses messages hosts already
/// answer for phones (<c>item/create</c> and <c>acp/*</c>), so it needs nothing new from the host.
/// </summary>
public interface ICollabRemoteHosts
{
    /// <summary>The connection to <paramref name="hostId"/>, made when there is none. Throws when it cannot be reached.</summary>
    Task<ICollabRemoteHost> ConnectAsync(string hostId, CancellationToken ct);

    string HostName(string hostId);

    /// <summary>Asks the sidebar to bring that host's projects in line (a delegate was started there).</summary>
    void RequestSync(string hostId);
}

/// <summary>One paired host, as far as the collaboration bus uses it.</summary>
public interface ICollabRemoteHost
{
    /// <summary>The host shows its GUI conversations (<c>acp.gui</c>); without it a delegate cannot be followed.</summary>
    bool SupportsAcpGui { get; }

    /// <summary>Completes when the connection ends.</summary>
    Task Completion { get; }

    event Action<AcpUpdateMessage>? AcpUpdated;

    Task<ItemMessage> CreateItemAsync(CreateItemMessage request, CancellationToken ct);
    Task<AcpSnapshotMessage> AttachConversationAsync(string itemId, CancellationToken ct);
    Task DetachConversationAsync(string itemId, CancellationToken ct);
    Task SendPromptAsync(string itemId, string text, CancellationToken ct);
    Task CancelTurnAsync(string itemId, CancellationToken ct);
    Task RespondAsync(AcpRespondMessage answer, CancellationToken ct);
}

/// <summary>The real hosts: the desktop's device-side connections (<see cref="RemoteClientRuntime"/>).</summary>
public sealed class RuntimeCollabRemoteHosts(RemoteClientRuntime runtime, RemoteProjects? projects) : ICollabRemoteHosts
{
    // One adapter per live client, so every remote session of a host shares one AcpUpdated subscription.
    private readonly ConditionalWeakTable<RemoteHostClient, ClientAdapter> _adapters = new();

    public async Task<ICollabRemoteHost> ConnectAsync(string hostId, CancellationToken ct)
    {
        var client = await runtime.ConnectAsync(hostId).WaitAsync(ct).ConfigureAwait(false);
        return _adapters.GetValue(client, c => new ClientAdapter(c));
    }

    public string HostName(string hostId) => runtime.HostName(hostId);

    public void RequestSync(string hostId)
    {
        if (projects is null) return;
        _ = Task.Run(async () =>
        {
            try { await projects.SyncHostAsync(hostId).ConfigureAwait(false); }
            catch { /* the sidebar catches up on the host's next change */ }
        });
    }

    private sealed class ClientAdapter : ICollabRemoteHost
    {
        private readonly RemoteHostClient _client;

        public ClientAdapter(RemoteHostClient client)
        {
            _client = client;
            _client.AcpUpdated += update => AcpUpdated?.Invoke(update);
        }

        public bool SupportsAcpGui => _client.SupportsAcpGui;
        public Task Completion => _client.Completion;
        public event Action<AcpUpdateMessage>? AcpUpdated;

        public Task<ItemMessage> CreateItemAsync(CreateItemMessage request, CancellationToken ct) => _client.CreateItemAsync(request, ct);
        public Task<AcpSnapshotMessage> AttachConversationAsync(string itemId, CancellationToken ct) => _client.AttachConversationAsync(itemId, ct);
        public Task DetachConversationAsync(string itemId, CancellationToken ct) => _client.DetachConversationAsync(itemId, ct);
        public Task SendPromptAsync(string itemId, string text, CancellationToken ct) => _client.SendPromptAsync(itemId, text, null, ct);
        public Task CancelTurnAsync(string itemId, CancellationToken ct) => _client.CancelTurnAsync(itemId, ct);
        public Task RespondAsync(AcpRespondMessage answer, CancellationToken ct) => _client.RespondAsync(answer, ct);
    }
}
