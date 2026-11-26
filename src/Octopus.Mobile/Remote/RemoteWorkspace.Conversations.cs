using System.Collections.Concurrent;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Client;
using Octopus.Remote.Protocol;

namespace Octopus.Mobile.Remote;

// GUI conversations on a real host (AcpMessages.cs): attach for a snapshot, then follow acp/update by sequence number.
// A gap (updates were missed) or a new connection to the host means attaching again; the last page that lets go of a
// conversation detaches from it.
internal sealed partial class RemoteWorkspace
{
    // UI thread only: the conversations pages show, and the connections whose acp/update events are listened to.
    private readonly List<HostConversation> _conversations = [];
    private readonly Dictionary<RemoteHostClient, (string HostId, Action<AcpUpdateMessage> Listener)> _acpListeners = new(ReferenceEqualityComparer.Instance);
    // Coalesces bursts of acp/update frames (e.g. after returning from mobile background) into bounded UI drains.
    private readonly ConcurrentQueue<(RemoteHostClient Client, AcpUpdateMessage Update)> _pendingAcpUpdates = new();
    private int _acpDrainScheduled;
    private bool _followsConversationHosts;

    public bool SupportsConversations(RemoteHost host) => _connections.ClientOf(host.Id)?.SupportsAcpGui == true;

    public async Task<RemoteConversation> OpenConversationAsync(RemoteSession session, CancellationToken cancellationToken = default)
    {
        var host = session.Project.Host;
        RemoteHostClient client;
        try
        {
            client = _connections.ClientOf(host.Id) ?? await _connections.ConnectAsync(host.Id).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
        if (!client.SupportsAcpGui) throw new IOException(MobileLangResources.Get(MobileLangResourceKind.ConversationUnsupportedHost));
        var conversation = await UiAsync(() =>
        {
            var created = new HostConversation(this, session) { Client = client };
            _conversations.Add(created);
            FollowConversationHosts();
            Listen(host.Id, client);
            return created;
        }).ConfigureAwait(false);
        try
        {
            await AttachAsync(conversation, client, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            conversation.Dispose();
            throw;
        }
        return conversation;
    }

    /// <summary>Asks the host for a fresh snapshot and shows it; one attach per conversation at a time (asked again meanwhile: once more after).</summary>
    private async Task AttachAsync(HostConversation conversation, RemoteHostClient client, CancellationToken cancellationToken = default)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(CallTimeout);
            var snapshot = await client.AttachConversationAsync(conversation.Session.Id, limit.Token).ConfigureAwait(false);
            var result = await UiAsync(() =>
            {
                // The page left, or the connection was replaced while the answer was on its way: that one counts.
                if (conversation.IsDisposed || !ReferenceEquals(conversation.Client, client)) return ConversationUpdateResult.Ignored;
                return conversation.Timeline.ApplySnapshot(snapshot);
            }).ConfigureAwait(false);
            if (result == ConversationUpdateResult.Gap) ReattachSoon(conversation);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The conversation did not answer in time.", e);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    /// <summary>After returning from background, reattach any open conversation that was left connecting, errored, or on a stale client.</summary>
    private void ReattachOpenConversationsIfNeeded() => Ui(() =>
    {
        foreach (var conversation in _conversations.ToList())
        {
            if (conversation.IsDisposed || conversation.Attaching) continue;
            var currentClient = _connections.ClientOf(conversation.Session.Project.Host.Id);
            if (currentClient is null) continue;
            if (!ReferenceEquals(conversation.Client, currentClient) || conversation.State is ConversationState.Error or ConversationState.Connecting)
            {
                ReattachSoon(conversation);
            }
        }
    });

    // In the background: what goes wrong shows in the conversation's state line.
    private void ReattachSoon(HostConversation conversation) => Ui(() =>
    {
        if (conversation.IsDisposed) return;
        if (conversation.Attaching)
        {
            conversation.AttachAgain = true;
            return;
        }
        if (_connections.ClientOf(conversation.Session.Project.Host.Id) is not { } client)
        {
            // No connection now: the connections make one again by themselves, and its arrival attaches (OnConversationHostChanged).
            ShowConnecting(conversation);
            return;
        }
        conversation.Attaching = true;
        conversation.Client = client;
        conversation.Timeline.Reset();
        Listen(conversation.Session.Project.Host.Id, client);
        _ = ReattachAsync(conversation, client);
    });

    private async Task ReattachAsync(HostConversation conversation, RemoteHostClient client)
    {
        try
        {
            await AttachAsync(conversation, client).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Ui(() =>
            {
                if (conversation.IsDisposed) return;
                conversation.State = ConversationState.Error;
                conversation.ErrorText = e.Message;
            });
        }
        finally
        {
            Ui(() =>
            {
                conversation.Attaching = false;
                if (!conversation.AttachAgain || conversation.IsDisposed) return;
                conversation.AttachAgain = false;
                ReattachSoon(conversation);
            });
        }
    }

    private static void ShowConnecting(HostConversation conversation)
    {
        conversation.Timeline.Reset();
        conversation.State = ConversationState.Connecting;
        conversation.StatusDetail = null;
    }

    // ---- events ----

    // Each connection's acp/update events, routed by item to the conversations of that host.
    // Queued and drained on the UI thread in batches so bursts after background wakeups never freeze the UI.
    private void Listen(string hostId, RemoteHostClient client)
    {
        if (_acpListeners.ContainsKey(client)) return;
        Action<AcpUpdateMessage> listener = update =>
        {
            _pendingAcpUpdates.Enqueue((client, update));
            if (Interlocked.Exchange(ref _acpDrainScheduled, 1) == 0)
            {
                Ui(DrainAcpUpdates);
            }
        };
        _acpListeners[client] = (hostId, listener);
        client.AcpUpdated += listener;
    }

    private void DrainAcpUpdates()
    {
        const int maxPerTick = 64;
        var processed = 0;
        while (processed < maxPerTick && _pendingAcpUpdates.TryDequeue(out var item))
        {
            OnAcpUpdate(item.Client, item.Update);
            processed++;
        }
        Volatile.Write(ref _acpDrainScheduled, 0);
        if (!_pendingAcpUpdates.IsEmpty && Interlocked.Exchange(ref _acpDrainScheduled, 1) == 0)
        {
            Ui(DrainAcpUpdates);
        }
    }

    private void OnAcpUpdate(RemoteHostClient client, AcpUpdateMessage update)
    {
        foreach (var conversation in _conversations.ToList())
        {
            if (!ReferenceEquals(conversation.Client, client) || conversation.Session.Id != update.ItemId) continue;
            if (conversation.Timeline.Apply(update) == ConversationUpdateResult.Gap) ReattachSoon(conversation);
        }
    }

    private void FollowConversationHosts()
    {
        if (_followsConversationHosts) return;
        _followsConversationHosts = true;
        _connections.StateChanged += OnConversationHostChanged;
    }

    // A host connected again (a new connection) or went away: its conversations attach again, or wait for it.
    private void OnConversationHostChanged(string hostId) => Ui(() =>
    {
        var client = _connections.ClientOf(hostId);
        foreach (var conversation in _conversations.Where(known => known.Session.Project.Host.Id == hostId).ToList())
        {
            if (client is null)
            {
                conversation.Client = null;
                ShowConnecting(conversation);
            }
            else if (!ReferenceEquals(conversation.Client, client))
            {
                ReattachSoon(conversation);
            }
        }
        ForgetUnusedListeners();
    });

    private void ForgetUnusedListeners()
    {
        foreach (var (client, (hostId, listener)) in _acpListeners.ToList())
        {
            if (_conversations.Any(conversation => ReferenceEquals(conversation.Client, client)) && ReferenceEquals(_connections.ClientOf(hostId), client)) continue;
            client.AcpUpdated -= listener;
            _acpListeners.Remove(client);
        }
    }

    private void Release(HostConversation conversation)
    {
        Ui(() =>
        {
            _conversations.Remove(conversation);
            var client = conversation.Client;
            // Another page may still show the same conversation: then the host keeps sending.
            if (client is not null && !_conversations.Any(other => ReferenceEquals(other.Client, client) && other.Session.Id == conversation.Session.Id))
            {
                _ = DetachQuietlyAsync(client, conversation.Session.Id);
            }
            conversation.Client = null;
            ForgetUnusedListeners();
            if (_conversations.Count == 0 && _followsConversationHosts)
            {
                _followsConversationHosts = false;
                _connections.StateChanged -= OnConversationHostChanged;
            }
        });
    }

    private static async Task DetachQuietlyAsync(RemoteHostClient client, string itemId)
    {
        // A connection that is gone has nothing to detach from.
        try { await client.DetachConversationAsync(itemId).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or RemoteException or ObjectDisposedException or OperationCanceledException) { }
    }

    /// <summary>Lets go of every conversation when the workspace goes away.</summary>
    private void DisposeConversations()
    {
        foreach (var conversation in _conversations.ToList()) conversation.Dispose();
    }

    /// <summary>A GUI conversation of a real host; the calls go over the host's connection by the item's id.</summary>
    private sealed class HostConversation(RemoteWorkspace workspace, RemoteSession session) : RemoteConversation(session)
    {
        /// <summary>The connection it is attached over; null while there is none. UI thread only.</summary>
        public RemoteHostClient? Client;
        public bool Attaching;
        public bool AttachAgain;
        public bool IsDisposed { get; private set; }

        private RemoteHost Host => Session.Project.Host;

        public override Task SendAsync(string text, IReadOnlyList<string>? attachments = null, CancellationToken cancellationToken = default) =>
            workspace.WithClientAsync(Host, client => client.SendPromptAsync(Session.Id, text, attachments, cancellationToken));

        public override Task CancelAsync(CancellationToken cancellationToken = default) =>
            workspace.WithClientAsync(Host, client => client.CancelTurnAsync(Session.Id, cancellationToken));

        public override Task RespondAsync(ConversationPermissionEntry card, string optionId, CancellationToken cancellationToken = default) =>
            workspace.WithClientAsync(Host, client => client.RespondAsync(new AcpRespondMessage
            {
                ItemId = Session.Id,
                EntryId = card.Id,
                OptionId = optionId,
            }, cancellationToken));

        public override Task AnswerAsync(ConversationQuestionEntry card, IReadOnlyDictionary<string, IReadOnlyList<string>> answers,
            IReadOnlyDictionary<string, string> freeText, CancellationToken cancellationToken = default) =>
            workspace.WithClientAsync(Host, client => client.RespondAsync(new AcpRespondMessage
            {
                ItemId = Session.Id,
                EntryId = card.Id,
                Answers = answers.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal),
                FreeText = freeText.Count > 0 ? freeText.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) : null,
            }, cancellationToken));

        public override Task SetOptionAsync(ConversationOptionKind kind, string value, CancellationToken cancellationToken = default) =>
            workspace.WithClientAsync(Host, client => client.SetConversationOptionAsync(Session.Id, ConversationTimeline.KindOf(kind), value, cancellationToken));

        public override Task ReloadAsync(CancellationToken cancellationToken = default)
        {
            workspace.ReattachSoon(this);
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            workspace.Release(this);
            base.Dispose();
        }
    }
}
