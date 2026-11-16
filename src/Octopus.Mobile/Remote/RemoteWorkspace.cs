using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Octopus.Core.Terminals;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Client;
using Octopus.Remote.Protocol;
using Octopus.Remote.Relay;
using Octopus.Remote.Security;
using Octopus.Terminal;

namespace Octopus.Mobile.Remote;

/// <summary>
/// The account's computers as the pages show them, kept in step with the real ones: the service says which computers
/// the account has, each paired one is connected to (through the relay) and says its projects, items and what they are
/// doing. Everything the pages bind to changes on the UI thread; the network happens elsewhere.
/// </summary>
internal sealed class RemoteWorkspace : IRemoteWorkspace, IDisposable
{
    private readonly RelaySession _session;
    private readonly RemoteHostConnections _connections;
    // UI thread only.
    private readonly Dictionary<string, HostState> _states = new(StringComparer.Ordinal);
    private int _refreshing;
    private bool _disposed;

    /// <summary>What is known of one computer beyond what the pages show.</summary>
    private sealed class HostState(RemoteHost host)
    {
        public RemoteHost Host { get; } = host;
        /// <summary>The service lists it as connected right now.</summary>
        public bool AccountOnline;
        /// <summary>The host's agents by id: the pages choose by label, the host is told the id.</summary>
        public Dictionary<string, AgentMessage> Agents = new(StringComparer.Ordinal);
        /// <summary>The terminals that are open, by item.</summary>
        public Dictionary<string, RemoteTerminalView> Views { get; } = new(StringComparer.Ordinal);
        /// <summary>The connection whose projects are shown.</summary>
        public RemoteHostClient? Loaded;
        public int Loads;
    }

    public RemoteWorkspace(RelaySession session, RemoteHostConnections connections)
    {
        (_session, _connections) = (session, connections);
        connections.StateChanged += OnStateChanged;
        connections.WorkspaceChanged += OnWorkspaceChanged;
        connections.ItemChanged += OnItemChanged;
        session.Changed += OnAccountChanged;
    }

    public ObservableCollection<RemoteHost> Hosts { get; } = [];
    public bool MirrorsTerminals => true;

    public async Task RefreshAsync()
    {
        // One at a time: the page asks on a timer and whenever it comes back into view.
        if (_disposed || Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            IReadOnlyList<RelayDevice>? devices = null;
            try
            {
                var relay = await _session.GetAsync().ConfigureAwait(false);
                if (relay is null)
                {
                    Ui(Clear);
                    return;
                }
                try
                {
                    devices = await RelayAccountClient.ListDevicesAsync(relay).ConfigureAwait(false);
                }
                catch (RelayException e) when (e.Failure == RelayFailure.Unauthorized)
                {
                    // The token ran out, or this phone was signed out: the session finds out which (and says so).
                    relay = await _session.GetAsync(fresh: true).ConfigureAwait(false);
                    if (relay is null) return;
                    devices = await RelayAccountClient.ListDevicesAsync(relay).ConfigureAwait(false);
                }
            }
            catch (RelayException)
            {
                // The service cannot be reached: what is shown stays, and the paired hosts are still tried.
            }
            var paired = _connections.Hosts.Peers;
            await UiAsync(() => Merge(paired, devices)).ConfigureAwait(false);
            foreach (var peer in paired)
            {
                if (devices is null || devices.Any(device => device.Id == peer.Id && device.Online)) _ = ConnectQuietlyAsync(peer.Id);
            }
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    public async Task<RemoteSession> CreateSessionAsync(RemoteProject project, NewSessionRequest request, CancellationToken cancellationToken = default)
    {
        var hostId = project.Host.Id;
        var agents = await UiAsync(() => _states.GetValueOrDefault(hostId)?.Agents).ConfigureAwait(false);
        var agent = request.AgentId is null ? null : agents?.GetValueOrDefault(request.AgentId);
        try
        {
            var client = _connections.ClientOf(hostId) ?? await _connections.ConnectAsync(hostId).ConfigureAwait(false);
            var item = await client.CreateItemAsync(new CreateItemMessage
            {
                ProjectId = project.Id,
                Kind = agent is null ? ItemKinds.Terminal : ItemKinds.Agent,
                AgentId = agent?.Id,
                // The pages show labels; the host is told ids.
                Model = agent?.Models.FirstOrDefault(model => model.Label == request.Model)?.Id,
                Effort = agent?.Efforts.FirstOrDefault(effort => effort.Label == request.Effort)?.Id,
                Prompt = agent is null ? null : request.Prompt,
            }, cancellationToken).ConfigureAwait(false);
            return await UiAsync(() =>
            {
                // The host's "something changed" may have brought the new item here already.
                if (project.Sessions.FirstOrDefault(session => session.Id == item.Id) is { } known) return known;
                var session = NewSession(_states.GetValueOrDefault(hostId), project, item);
                project.Sessions.Add(session);
                return session;
            }).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public ITerminalConnectionFactory OpenTerminal(RemoteSession session)
    {
        var host = session.Project.Host;
        if (_connections.ClientOf(host.Id) is not { } client || !_states.TryGetValue(host.Id, out var state))
        {
            return new NoteFactory("The connection to the computer was lost.");
        }
        var view = client.ViewTerminal(session.Id);
        // Who decides is read when the page is updated, not taken from the event: the events come from the answers to this
        // view's calls and from the output on different threads, and can reach the UI thread in another order than
        // they were raised (a "not yours" from before the claim landed after it, on a slow machine).
        view.SizeChanged += (columns, rows, _) => Ui(() =>
        {
            session.Columns = columns;
            session.Rows = rows;
            session.SizeOwner = view.OwnsSize ? null : host.Name;
        });
        state.Views[session.Id] = view;
        return view.Factory;
    }

    public Task ClaimSizeAsync(RemoteSession session, int columns, int rows) =>
        ViewOf(session)?.ClaimSizeAsync(columns, rows) ?? Task.CompletedTask;

    public Task ReleaseSizeAsync(RemoteSession session) => ViewOf(session)?.ReleaseSizeAsync() ?? Task.CompletedTask;

    public PairingAttempt BeginPairing(RemoteHost host)
    {
        var cancel = new CancellationTokenSource();
        var code = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        return new PairingAttempt(code.Task, PairAsync(host, code, cancel.Token), cancel.Cancel);
    }

    public void Dispose()
    {
        _disposed = true;
        _connections.StateChanged -= OnStateChanged;
        _connections.WorkspaceChanged -= OnWorkspaceChanged;
        _connections.ItemChanged -= OnItemChanged;
        _session.Changed -= OnAccountChanged;
    }

    private async Task<PairingOutcome> PairAsync(RemoteHost host, TaskCompletionSource<string> code, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            // The phone reaches a host through the relay; the number both sides show is what says it is the right one.
            var peer = await _connections.PairAsync(RemoteHostConnections.RelayAddress(host.Id).ToString(), shown => code.TrySetResult(shown), cancellationToken).ConfigureAwait(false);
            if (peer.Id != host.Id)
            {
                // Another computer than the one that was asked for answered: not kept.
                await _connections.RemoveHostAsync(peer.Id).ConfigureAwait(false);
                return PairingOutcome.Refused;
            }
            Ui(() => host.IsPaired = true);
            _ = RefreshAsync();
            return PairingOutcome.Paired;
        }
        catch (HandshakeException e)
        {
            return e.Status == HandshakeStatus.PairingClosed ? PairingOutcome.NotPairing : PairingOutcome.Refused;
        }
        catch (OperationCanceledException)
        {
            return PairingOutcome.Cancelled;
        }
        catch (Exception e) when (e is IOException or RemoteException or FormatException)
        {
            return PairingOutcome.Unreachable;
        }
        finally
        {
            code.TrySetCanceled();
        }
    }

    private RemoteTerminalView? ViewOf(RemoteSession session) =>
        _states.GetValueOrDefault(session.Project.Host.Id)?.Views.GetValueOrDefault(session.Id);

    private async Task ConnectQuietlyAsync(string hostId)
    {
        // A host that cannot be reached now is tried again by the connections themselves; its row says "offline" meanwhile.
        try { await _connections.ConnectAsync(hostId).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    // ---- what the service and the paired-hosts file say, into the list (UI thread) ----

    private void Merge(IReadOnlyList<PairedPeer> paired, IReadOnlyList<RelayDevice>? devices)
    {
        var wanted = new Dictionary<string, (string Name, bool Paired, bool Online, DateTimeOffset? Seen)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var peer in paired)
        {
            wanted[peer.Id] = (peer.Name, true, false, peer.LastSeenAt);
            order.Add(peer.Id);
        }
        // The account's other computers; phones are never hosts, and this phone is not its own.
        foreach (var device in devices ?? [])
        {
            if (device.Current || device.Kind != "desktop") continue;
            if (wanted.TryGetValue(device.Id, out var known))
            {
                wanted[device.Id] = known with { Online = device.Online, Seen = Later(known.Seen, device.LastSeenAt) };
            }
            else
            {
                wanted[device.Id] = (device.Name, false, device.Online, device.LastSeenAt);
                order.Add(device.Id);
            }
        }
        if (devices is not null)
        {
            foreach (var gone in _states.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
            {
                Hosts.Remove(_states[gone].Host);
                _states.Remove(gone);
            }
        }
        foreach (var id in order)
        {
            var (name, isPaired, online, seen) = wanted[id];
            if (!_states.TryGetValue(id, out var state))
            {
                _states[id] = state = new HostState(new RemoteHost(id, name, HostLink.Offline, isPaired));
                Hosts.Add(state.Host);
            }
            state.Host.Name = name;
            state.Host.IsPaired = isPaired;
            state.Host.LastSeen = seen;
            if (devices is not null) state.AccountOnline = online;
            UpdateLink(state);
        }
    }

    private void Clear()
    {
        Hosts.Clear();
        _states.Clear();
    }

    // How the row says the computer is reached: a live connection counts; before pairing, what the service says.
    private void UpdateLink(HostState state)
    {
        var id = state.Host.Id;
        state.Host.Link = _connections.StateOf(id) == RemoteHostState.Connected
            ? _connections.IsThroughRelay(id) ? HostLink.Relay : HostLink.Lan
            : !state.Host.IsPaired && state.AccountOnline ? HostLink.Relay : HostLink.Offline;
    }

    // ---- what a connected host says ----

    private void OnStateChanged(string hostId) => Ui(() =>
    {
        if (!_states.TryGetValue(hostId, out var state)) return;
        UpdateLink(state);
        var client = _connections.ClientOf(hostId);
        if (client is null)
        {
            state.Loaded = null;
            state.Views.Clear();
            return;
        }
        if (!ReferenceEquals(state.Loaded, client))
        {
            state.Loaded = client;
            _ = LoadAsync(hostId, client, state);
        }
    });

    private void OnWorkspaceChanged(string hostId) => Ui(() =>
    {
        if (_states.TryGetValue(hostId, out var state) && _connections.ClientOf(hostId) is { } client) _ = LoadAsync(hostId, client, state);
    });

    private void OnItemChanged(string hostId, ItemMessage item) => Ui(() =>
    {
        if (!_states.TryGetValue(hostId, out var state)) return;
        var session = state.Host.Projects.FirstOrDefault(project => project.Id == item.ProjectId)?.Sessions.FirstOrDefault(known => known.Id == item.Id);
        if (session is not null)
        {
            session.Title = item.Title;
            session.Activity = ActivityOf(item.Activity);
        }
        else if (_connections.ClientOf(hostId) is { } client)
        {
            // An item this phone has not heard of yet: the lists are read again.
            _ = LoadAsync(hostId, client, state);
        }
    });

    private void OnAccountChanged()
    {
        if (_session.Account is not null)
        {
            _ = RefreshAsync();
            return;
        }
        // Signed out: nothing of the account stays on the screen, and no connection stays open. Who is paired is kept.
        _ = _connections.DisconnectAllAsync();
        Ui(Clear);
    }

    private async Task LoadAsync(string hostId, RemoteHostClient client, HostState state)
    {
        // Only the latest reading is shown: an older one that arrives late is dropped.
        var load = Interlocked.Increment(ref state.Loads);
        try
        {
            // The agents first: a session's row is named and colored after its agent.
            var agents = await client.ListAgentsAsync().ConfigureAwait(false);
            var projects = await client.ListProjectsAsync().ConfigureAwait(false);
            var items = new Dictionary<string, IReadOnlyList<ItemMessage>>(StringComparer.Ordinal);
            foreach (var project in projects) items[project.Id] = await client.ListItemsAsync(project.Id).ConfigureAwait(false);
            Ui(() =>
            {
                if (load != Volatile.Read(ref state.Loads) || _states.GetValueOrDefault(hostId) != state) return;
                if (!agents.Select(agent => agent.Id).SequenceEqual(state.Host.Agents.Select(agent => agent.Id)))
                {
                    state.Agents = agents.ToDictionary(agent => agent.Id, StringComparer.Ordinal);
                    state.Host.Agents = [.. agents.Select(agent => new AgentOption(agent.Id, agent.Name, InitialOf(agent.Name), ColorOf(agent.Id),
                        [.. agent.Models.Select(model => model.Label)], [.. agent.Efforts.Select(effort => effort.Label)]))];
                }
                Apply(state, projects, items);
            });
        }
        catch (Exception e) when (e is IOException or RemoteException)
        {
        }
    }

    // The host's projects and items into the page's: what is still there keeps its object (a page may be showing it).
    private void Apply(HostState state, IReadOnlyList<ProjectMessage> projects, Dictionary<string, IReadOnlyList<ItemMessage>> items)
    {
        var host = state.Host;
        var target = projects.Select(message =>
            host.Projects.FirstOrDefault(known => known.Id == message.Id && known.Name == message.Name && known.Path == message.Path)
            ?? new RemoteProject(host, message.Id, message.Name, message.Path)).ToList();
        foreach (var project in target)
        {
            var sessions = items.GetValueOrDefault(project.Id, []).Select(item =>
            {
                if (project.Sessions.FirstOrDefault(known => known.Id == item.Id) is not { } session) return NewSession(state, project, item);
                session.Title = item.Title;
                session.Activity = ActivityOf(item.Activity);
                return session;
            }).ToList();
            Replace(project.Sessions, sessions);
        }
        Replace(host.Projects, target);
    }

    private static void Replace<T>(ObservableCollection<T> collection, List<T> target) where T : class
    {
        if (collection.SequenceEqual(target, ReferenceEqualityComparer.Instance)) return;
        collection.Clear();
        foreach (var item in target) collection.Add(item);
    }

    private static RemoteSession NewSession(HostState? state, RemoteProject project, ItemMessage item)
    {
        var agent = item.AgentId is null ? null : state?.Agents.GetValueOrDefault(item.AgentId);
        var isAgent = item.Kind == ItemKinds.Agent;
        return new RemoteSession(project, item.Id, isAgent ? SessionKind.Agent : SessionKind.Terminal, item.Title)
        {
            AgentId = item.AgentId,
            // Model and effort as the host labels them; the agent's name when neither was chosen.
            Detail = !isAgent ? null : string.Join(" · ", new[]
            {
                agent?.Models.FirstOrDefault(model => model.Id == item.Model)?.Label ?? item.Model,
                agent?.Efforts.FirstOrDefault(effort => effort.Id == item.Effort)?.Label ?? item.Effort,
            }.Where(part => !string.IsNullOrEmpty(part))) is { Length: > 0 } detail ? detail : agent?.Name ?? item.AgentId,
            Activity = ActivityOf(item.Activity),
            SizeOwner = project.Host.Name,
        };
    }

    private static SessionActivity ActivityOf(string activity) => activity switch
    {
        ItemActivities.Working => SessionActivity.Working,
        ItemActivities.Waiting => SessionActivity.Waiting,
        ItemActivities.Error => SessionActivity.Error,
        _ => SessionActivity.Idle,
    };

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset b) => a is { } known && known > b ? known : b;

    private static string InitialOf(string name) => name.Trim() is { Length: > 0 } trimmed ? char.ToUpperInvariant(trimmed[0]).ToString() : "?";

    // The squares of the agents the app knows by name keep their colors; any other gets one of a few, always the same one.
    private static string ColorOf(string agentId) => agentId switch
    {
        "claude" => "#D97757",
        "codex" => "#1F9D74",
        "gemini" => "#4C7FE0",
        "opencode" => "#8B6FD6",
        _ => Fallbacks[(int)((uint)StableHash(agentId) % (uint)Fallbacks.Length)],
    };

    private static readonly string[] Fallbacks = ["#5B8DEF", "#C2783E", "#3FA796", "#B5609E", "#7A8B45"];

    private static int StableHash(string text)
    {
        var hash = 17;
        foreach (var ch in text) hash = unchecked(hash * 31 + ch);
        return hash;
    }

    // ---- to the UI thread ----

    private static void Ui(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private static Task UiAsync(Action action) => UiAsync(() =>
    {
        action();
        return true;
    });

    private static async Task<T> UiAsync<T>(Func<T> read) =>
        Dispatcher.UIThread.CheckAccess() ? read() : await Dispatcher.UIThread.InvokeAsync(read);

    /// <summary>What a terminal page is started with when there is no connection to its host: one line saying so.</summary>
    private sealed class NoteFactory(string note) : ITerminalConnectionFactory
    {
        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows) => new Note(note);

        private sealed class Note(string note) : ITerminalConnection
        {
            private readonly TaskCompletionSource<TerminalExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int ProcessId => 0;
            public Task<TerminalExit> Exited => _exit.Task;

            public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                yield return System.Text.Encoding.UTF8.GetBytes("\u001b[31m" + note + "\u001b[0m\r\n");
                _exit.TrySetResult(new TerminalExit(null, null));
            }

            public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
            public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight) => ValueTask.CompletedTask;
            public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;
            public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);

            public ValueTask DisposeAsync()
            {
                _exit.TrySetResult(new TerminalExit(null, null));
                return ValueTask.CompletedTask;
            }
        }
    }
}
