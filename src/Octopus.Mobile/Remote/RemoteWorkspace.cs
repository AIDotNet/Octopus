using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Octopus.Core.Terminals;
using Octopus.Mobile.Common;
using Octopus.Mobile.Diagnostics;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Client;
using Octopus.Remote.Protocol;
using Octopus.Remote.Relay;
using Octopus.Remote.Security;
using Octopus.Terminal;

namespace Octopus.Mobile.Remote;

/// <summary>
/// The computers as the pages show them, kept in step with the real ones. Signed in, the service says which computers
/// the account has; with or without an account, each computer this phone is paired with is connected to (directly at
/// the addresses it was reached at before, else through the relay when signed in) and says its projects, items and
/// what they are doing. Everything the pages bind to changes on the UI thread; the network happens elsewhere.
/// </summary>
internal sealed partial class RemoteWorkspace : IRemoteWorkspace, IDisposable
{
    /// <summary>How long a computer named by its address has to answer before it counts as out of reach.</summary>
    private static readonly TimeSpan ReachLimit = TimeSpan.FromSeconds(15);
    /// <summary>Maximum time a remote workspace RPC waits before failing rather than stalling on a dead socket.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    private readonly RelaySession _session;
    private readonly RemoteHostConnections _connections;
    private readonly MobileSettings _settings;
    private readonly MobileProjectIconCache _iconCache;
    // In-flight icon downloads keyed by "{hostId}\0{projectId}\0{imageKey}" so rapid workspace reloads never double-fetch.
    private readonly HashSet<string> _loadingIcons = new(StringComparer.Ordinal);
    // UI thread only.
    private readonly Dictionary<string, HostState> _states = new(StringComparer.Ordinal);
    private int _refreshing;
    private int _refreshAgain;
    private volatile bool _isBackgrounded;
    private long _backgroundedAtTick;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _dirtyHostsWhileBackgrounded = new(StringComparer.Ordinal);
    private int _resumeInFlight;
    private int _resumeAgain;
    private volatile bool _isResuming;
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

    public RemoteWorkspace(RelaySession session, RemoteHostConnections connections, MobileSettings settings, MobileProjectIconCache? iconCache = null)
    {
        (_session, _connections, _settings, _iconCache) = (session, connections, settings, iconCache ?? new MobileProjectIconCache(null));
        connections.StateChanged += OnStateChanged;
        connections.WorkspaceChanged += OnWorkspaceChanged;
        connections.ItemChanged += OnItemChanged;
        session.Changed += OnAccountChanged;
        settings.LocalOnlyChanged += OnModeChanged;
    }

    public ObservableCollection<RemoteHost> Hosts { get; } = [];
    public ObservableCollection<AccountDevice> OtherDevices { get; } = [];
    public bool MirrorsTerminals => true;
    public bool HasPairedHosts => _connections.Hosts.Peers.Count > 0;
    public bool IsResuming => _isResuming;
    public event Action<bool>? ResumingChanged;

    public bool SupportsProjectGroups(RemoteHost host) =>
        _connections.ClientOf(host.Id) is { SupportsProjectGroups: true };

    public void OnAppBackgrounded()
    {
        _isBackgrounded = true;
        Volatile.Write(ref _backgroundedAtTick, Environment.TickCount64);
        if (MobilePreferences.Current.SuspendStreamInBackground)
        {
            PauseConversationsForBackground();
        }
    }

    public void OnAppForegrounded()
    {
        _isBackgrounded = false;
        if (_disposed) return;
        if (Interlocked.Exchange(ref _resumeInFlight, 1) == 1)
        {
            Volatile.Write(ref _resumeAgain, 1);
            return;
        }
        ResumeAfterBackgroundLoopAsync().SafeFireAndForget("RESUME");
    }

    private async Task ResumeAfterBackgroundLoopAsync()
    {
        SetResuming(true);
        try
        {
            do
            {
                Volatile.Write(ref _resumeAgain, 0);
                await ResumeAfterBackgroundOnceAsync().ConfigureAwait(false);
            }
            while (!_disposed && !_isBackgrounded && Volatile.Read(ref _resumeAgain) == 1);
        }
        finally
        {
            Volatile.Write(ref _resumeInFlight, 0);
            SetResuming(false);
        }
    }

    private async Task ResumeAfterBackgroundOnceAsync()
    {
        try
        {
            var backgroundedAt = Interlocked.Exchange(ref _backgroundedAtTick, 0);
            var longBackground = backgroundedAt > 0 && Environment.TickCount64 - backgroundedAt >= 15_000;
            DrainAndDiscardStaleAcpUpdates();
            await _connections.ProbeAndReconnectAsync(TimeSpan.FromSeconds(2.5)).ConfigureAwait(false);
            if (_disposed || _isBackgrounded) return;
            await RefreshAsync().ConfigureAwait(false);
            if (_disposed || _isBackgrounded) return;
            await ReloadStaleConnectedHostsAsync(reloadAllConnected: longBackground).ConfigureAwait(false);
            if (_disposed || _isBackgrounded) return;
            ReattachOpenConversationsIfNeeded(forceBackgroundPaused: MobilePreferences.Current.SnapshotResyncOnResume);
        }
        catch (Exception ex)
        {
            // Background resume recovery is best-effort and must never crash the app.
            MobileDiagnostics.Error("RESUME", "Foreground recovery pass failed", ex);
        }
    }

    /// <summary>
    /// Reloads connected hosts whose workspace/item change pushes were skipped while backgrounded (or all connected
    /// hosts after a long background pause) so returning to foreground never displays stale project/session lists.
    /// </summary>
    private async Task ReloadStaleConnectedHostsAsync(bool reloadAllConnected)
    {
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hostId in _dirtyHostsWhileBackgrounded.Keys)
        {
            if (_dirtyHostsWhileBackgrounded.TryRemove(hostId, out _)) dirty.Add(hostId);
        }
        if (!reloadAllConnected && dirty.Count == 0) return;

        var targets = await UiAsync(() =>
        {
            var list = new List<(string HostId, RemoteHostClient Client, HostState State)>();
            foreach (var (hostId, state) in _states)
            {
                if (!reloadAllConnected && !dirty.Contains(hostId)) continue;
                if (_connections.ClientOf(hostId) is { } client)
                {
                    list.Add((hostId, client, state));
                }
            }
            return list;
        }).ConfigureAwait(false);

        if (targets.Count == 0 || _disposed || _isBackgrounded) return;
        await Task.WhenAll(targets.Select(t => LoadAsync(t.HostId, t.Client, t.State))).ConfigureAwait(false);
    }

    private void SetResuming(bool resuming)
    {
        if (_isResuming == resuming) return;
        _isResuming = resuming;
        Ui(() => ResumingChanged?.Invoke(resuming));
    }

    public async Task RefreshAsync()
    {
        // One at a time: the page asks on a timer and whenever it comes back into view. What is asked for while one
        // runs is not dropped (a computer was just paired, the account just changed): it runs once more afterwards.
        if (_disposed) return;
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            Volatile.Write(ref _refreshAgain, 1);
            return;
        }
        try
        {
            do
            {
                Volatile.Write(ref _refreshAgain, 0);
                await RefreshOnceAsync().ConfigureAwait(false);
            }
            while (!_disposed && Volatile.Read(ref _refreshAgain) == 1);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private async Task RefreshOnceAsync()
    {
        IReadOnlyList<RelayDevice>? devices = null;
        try
        {
            var relay = await _session.GetAsync().ConfigureAwait(false);
            if (relay is null)
            {
                if (!_settings.LocalOnly)
                {
                    Ui(ClearUnlessShown);
                    return;
                }
                // No account: the service is not asked anything, and the list is what this phone paired with itself.
                devices = [];
            }
            else
            {
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
        }
        catch (RelayException)
        {
            // The service cannot be reached: what is shown stays, and the paired hosts are still tried.
        }
        var paired = _connections.Hosts.Peers;
        await UiAsync(() => Merge(paired, devices)).ConfigureAwait(false);
        foreach (var peer in paired)
        {
            // A computer that was reached directly before is tried there whatever the service says (it may not be in
            // the account at all); one only known through the relay is tried when the service says it is there.
            if (devices is null || peer.Addresses.Count > 0 || devices.Any(device => device.Id == peer.Id && device.Online)) _ = ConnectQuietlyAsync(peer.Id);
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
                WorkspaceId = request.WorkspaceId,
                InjectTaskPrompt = request.InjectTaskPrompt,
                Surface = agent is null
                    ? null
                    : request.LaunchAsGui is true
                        ? ItemSurfaces.Gui
                        : request.LaunchAsGui is false
                            ? ItemSurfaces.Terminal
                            : null,
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

    public async Task<RemoteWorkspaceOptions> GetWorkspaceOptionsAsync(RemoteProject project, CancellationToken cancellationToken = default)
    {
        var hostId = project.Host.Id;
        try
        {
            var client = _connections.ClientOf(hostId) ?? await _connections.ConnectAsync(hostId).ConfigureAwait(false);
            if (!client.SupportsWorkspaces) return RemoteWorkspaceOptions.Empty;
            var options = await client.GetWorkspaceOptionsAsync(project.Id, cancellationToken).ConfigureAwait(false);
            return new RemoteWorkspaceOptions(
                options.IsGitRepository,
                options.DefaultBranch,
                [.. options.Branches.Select(b => new RemoteBranchOption(b.Name, b.IsRemote, b.IsCurrent, b.OccupiedBy))],
                options.GhAvailable,
                [.. options.Issues.Select(t => new RemoteTaskOption(t.Kind, t.Key, t.Title, t.Url, t.Body, t.SuggestedBranch, t.HeadBranch))],
                [.. options.PullRequests.Select(t => new RemoteTaskOption(t.Kind, t.Key, t.Title, t.Url, t.Body, t.SuggestedBranch, t.HeadBranch))],
                options.WorkspacesDirectory);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task<RemoteBranchWorkspace> CreateWorkspaceAsync(RemoteProject project, CreateBranchWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        var hostId = project.Host.Id;
        try
        {
            var client = _connections.ClientOf(hostId) ?? await _connections.ConnectAsync(hostId).ConfigureAwait(false);
            if (!client.SupportsWorkspaces)
            {
                throw new IOException(MobileLangResources.Get(MobileLangResourceKind.WorkspaceUnsupportedHost));
            }
            var created = await client.CreateWorkspaceAsync(new CreateWorkspaceMessage
            {
                ProjectId = project.Id,
                CreateBranch = request.CreateBranch,
                BranchName = request.BranchName,
                BaseRef = request.BaseRef,
                IsRemoteBranch = request.IsRemoteBranch,
                TaskKind = request.TaskKind,
                TaskKey = request.TaskKey,
                TaskTitle = request.TaskTitle,
                TaskUrl = request.TaskUrl,
                TaskBody = request.TaskBody,
            }, cancellationToken).ConfigureAwait(false);
            var mapped = MapWorkspace(created);
            return await UiAsync(() =>
            {
                if (project.Workspaces.FirstOrDefault(ws => ws.Id == mapped.Id) is { } existing) return existing;
                project.Workspaces.Add(mapped);
                return mapped;
            }).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public Task RenameSessionAsync(RemoteSession session, string? title, CancellationToken cancellationToken = default) =>
        WithClientAsync(session.Project.Host, async client =>
        {
            var trimmed = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            await client.RenameItemAsync(session.Id, trimmed, cancellationToken).ConfigureAwait(false);
            // The host's "item changed" brings the title too; a new one shows at once. Cleared, only the host knows the default.
            if (trimmed is not null) await UiAsync(() => session.Title = trimmed).ConfigureAwait(false);
        });

    public Task CloseSessionAsync(RemoteSession session, CancellationToken cancellationToken = default) =>
        WithClientAsync(session.Project.Host, async client =>
        {
            await client.RemoveItemAsync(session.Id, cancellationToken).ConfigureAwait(false);
            await UiAsync(() => session.Project.Sessions.Remove(session)).ConfigureAwait(false);
        });

    public Task RestartSessionAsync(RemoteSession session, CancellationToken cancellationToken = default) =>
        WithClientAsync(session.Project.Host, client => client.RestartItemAsync(session.Id, cancellationToken));

    public async Task<RemoteFolderListing> BrowseFolderAsync(RemoteHost host, string? path, CancellationToken cancellationToken = default)
    {
        BrowseFolderResultMessage? page = null;
        await WithClientAsync(host, async client =>
        {
            if (!client.SupportsBrowse) throw new IOException(MobileLangResources.Get(MobileLangResourceKind.FolderUnsupportedHost));
            // Null is the host's home, empty its list of drives; a folder that went away shows the nearest one above it.
            page = await client.BrowseFolderAsync(string.IsNullOrEmpty(path) ? null : path, listRoots: path is { Length: 0 },
                fallbackToParent: path is { Length: > 0 }, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return new RemoteFolderListing(page!.Path, page.Parent, page.Separator, page.Home,
            [.. page.Roots.Select(FolderOf)], [.. page.Entries.Where(entry => !entry.IsHidden).Select(FolderOf)]);
    }

    public async Task<string> CreateFolderAsync(RemoteHost host, string parentPath, string name, CancellationToken cancellationToken = default)
    {
        var created = "";
        await WithClientAsync(host, async client =>
            created = await client.BrowseCreateDirectoryAsync(parentPath, name.Trim(), cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        return created;
    }

    public async Task<RemoteProject> CreateProjectAsync(RemoteHost host, string path, CancellationToken cancellationToken = default)
    {
        ProjectMessage? message = null;
        await WithClientAsync(host, async client =>
            message = await client.CreateProjectAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        return await UiAsync(() =>
        {
            // The host's "something changed" may have brought it here already.
            if (host.Projects.FirstOrDefault(known => known.Id == message!.Id) is { } known) return known;
            var project = new RemoteProject(host, message!.Id, message.Name, message.Path)
            {
                Color = message.Color,
                Icon = message.Icon,
                ImageKey = message.Image,
                GroupId = message.GroupId,
                IsGitRepository = message.IsGitRepository,
                DefaultBranch = message.DefaultBranch,
            };
            host.Projects.Add(project);
            if (_connections.ClientOf(host.Id) is { } client)
            {
                SyncProjectImage(host.Id, client, project, message.Image);
            }
            return project;
        }).ConfigureAwait(false);
    }

    public async Task<RemoteProjectGroup> CreateProjectGroupAsync(
        RemoteHost host,
        string name,
        string? color = null,
        bool mergeWorkspaces = false,
        IReadOnlyList<string>? projectIds = null,
        CancellationToken cancellationToken = default)
    {
        ProjectGroupMessage? created = null;
        await WithClientAsync(host, async client =>
        {
            if (!client.SupportsProjectGroups) throw new IOException("This computer needs an update to manage project groups.");
            created = await client.CreateProjectGroupAsync(new CreateProjectGroupMessage
            {
                Name = name.Trim(),
                Color = string.IsNullOrWhiteSpace(color) ? null : color.Trim(),
                MergeWorkspaces = mergeWorkspaces,
                ProjectIds = projectIds is { Count: > 0 } ? [.. projectIds] : null,
            }, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return await UiAsync(() => UpsertProjectGroup(host, created!)).ConfigureAwait(false);
    }

    public async Task<RemoteProjectGroup> UpdateProjectGroupAsync(
        RemoteProjectGroup group,
        string? name = null,
        string? color = null,
        bool clearColor = false,
        bool? mergeWorkspaces = null,
        CancellationToken cancellationToken = default)
    {
        ProjectGroupMessage? updated = null;
        await WithClientAsync(group.Host, async client =>
        {
            if (!client.SupportsProjectGroups) throw new IOException("This computer needs an update to manage project groups.");
            updated = await client.UpdateProjectGroupAsync(new UpdateProjectGroupMessage
            {
                GroupId = group.Id,
                Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                Color = clearColor ? null : color,
                ClearColor = clearColor,
                MergeWorkspaces = mergeWorkspaces,
            }, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return await UiAsync(() => UpsertProjectGroup(group.Host, updated!)).ConfigureAwait(false);
    }

    public Task DeleteProjectGroupAsync(RemoteProjectGroup group, CancellationToken cancellationToken = default) =>
        WithClientAsync(group.Host, async client =>
        {
            if (!client.SupportsProjectGroups) throw new IOException("This computer needs an update to manage project groups.");
            await client.DeleteProjectGroupAsync(group.Id, cancellationToken).ConfigureAwait(false);
            await UiAsync(() =>
            {
                foreach (var project in group.Host.Projects)
                {
                    if (string.Equals(project.GroupId, group.Id, StringComparison.Ordinal))
                    {
                        project.GroupId = null;
                    }
                }
                group.Host.ProjectGroups.Remove(group);
            }).ConfigureAwait(false);
        });

    public Task SetProjectGroupAsync(RemoteProject project, string? groupId, CancellationToken cancellationToken = default) =>
        WithClientAsync(project.Host, async client =>
        {
            if (!client.SupportsProjectGroups) throw new IOException("This computer needs an update to manage project groups.");
            var normalized = string.IsNullOrWhiteSpace(groupId) ? null : groupId;
            await client.SetProjectGroupAsync(project.Id, normalized, cancellationToken).ConfigureAwait(false);
            await UiAsync(() => project.GroupId = normalized).ConfigureAwait(false);
        });

    private static RemoteProjectGroup UpsertProjectGroup(RemoteHost host, ProjectGroupMessage msg)
    {
        var existing = host.ProjectGroups.FirstOrDefault(g => string.Equals(g.Id, msg.Id, StringComparison.Ordinal));
        var group = existing ?? new RemoteProjectGroup(host, msg.Id, msg.Name);
        group.Name = msg.Name;
        group.Color = msg.Color;
        group.MergeWorkspaces = msg.MergeWorkspaces;
        if (existing is null)
        {
            host.ProjectGroups.Add(group);
        }
        return group;
    }

    private static RemoteFolderEntry FolderOf(BrowseEntryMessage entry) => new(entry.Name, entry.Path, entry.Kind, entry.IsExistingProject);

    /// <summary>Runs a call on the host's connection (making one when there is none); what the host refuses comes back as an <see cref="IOException"/> with its words.</summary>
    private async Task WithClientAsync(RemoteHost host, Func<RemoteHostClient, Task> call)
    {
        try
        {
            var client = _connections.ClientOf(host.Id) ?? await _connections.ConnectAsync(host.Id).WaitAsync(CallTimeout).ConfigureAwait(false);
            await call(client).WaitAsync(CallTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            throw new IOException("The computer did not answer in time.", e);
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

    public void CloseTerminal(RemoteSession session)
    {
        if (_states.TryGetValue(session.Project.Host.Id, out var state)) state.Views.Remove(session.Id);
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

    public PairingAttempt AddHost(string target)
    {
        var cancel = new CancellationTokenSource();
        var code = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? lastError = null;
        return new PairingAttempt(code.Task, AddAsync(target, code, error => Volatile.Write(ref lastError, error), cancel.Token), cancel.Cancel, () => Volatile.Read(ref lastError));
    }

    public async Task<AccessOutcome> AccessWithPasswordAsync(RemoteHost host, string password, CancellationToken cancellationToken = default)
    {
        if (AccessPassword.Normalize(password) is not { } normalized) return AccessOutcome.NotAPassword;
        try
        {
            // Through the relay to the device id the account's list named; the computer proves the password back.
            await _connections.AccessWithPasswordAsync(host.Id, normalized, cancellationToken).ConfigureAwait(false);
        }
        catch (HandshakeException e) when (e.Failure == HandshakeFailure.WrongPeer)
        {
            return AccessOutcome.NotConfirmed;
        }
        catch (HandshakeException e) when (e.Failure == HandshakeFailure.Rejected)
        {
            return e.Status switch
            {
                HandshakeStatus.WrongPassword => AccessOutcome.WrongPassword,
                HandshakeStatus.PasswordLocked => AccessOutcome.Locked,
                HandshakeStatus.PairingClosed => AccessOutcome.PasswordOff,
                _ => AccessOutcome.Unreachable,
            };
        }
        catch (HandshakeException e) when (e.Failure == HandshakeFailure.Authentication)
        {
            return AccessOutcome.NotConfirmed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AccessOutcome.Cancelled;
        }
        catch (ArgumentException)
        {
            return AccessOutcome.NotAPassword;
        }
        catch (Exception e) when (e is HandshakeException or IOException or RemoteException or OperationCanceledException or ObjectDisposedException
                                      or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException)
        {
            return AccessOutcome.Unreachable;
        }
        // Remembered like a paired computer and connected already: its row moves to the online ones at once.
        await UiAsync(() =>
        {
            host.IsPaired = true;
            host.AcceptsPassword = false;
            if (_states.TryGetValue(host.Id, out var state)) Sync(state);
        }).ConfigureAwait(false);
        _ = RefreshAsync();
        return AccessOutcome.Granted;
    }

    public void Dispose()
    {
        _disposed = true;
        _connections.StateChanged -= OnStateChanged;
        _connections.WorkspaceChanged -= OnWorkspaceChanged;
        _connections.ItemChanged -= OnItemChanged;
        _session.Changed -= OnAccountChanged;
        _settings.LocalOnlyChanged -= OnModeChanged;
        // GUI conversations (RemoteWorkspace.Conversations.cs).
        DisposeConversations();
    }

    // The same pairing as the desktop's "add a host": to an address the person typed (then both sides show a number),
    // or by a pairing link (then the link's token stands for the number). No service has a part in it.
    private async Task<PairingOutcome> AddAsync(string target, TaskCompletionSource<string> code, Action<string?> setError, CancellationToken cancellationToken)
    {
        await Task.Yield();
        // An address nothing answers at is given up on after a while. Once the computer answered, the person there
        // takes as long as they take to allow.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ReachLimit);
        try
        {
            await _connections.PairAsync(target, shown =>
            {
                limit.CancelAfter(Timeout.InfiniteTimeSpan);
                code.TrySetResult(shown);
            }, limit.Token).ConfigureAwait(false);
            _ = RefreshAsync();
            return PairingOutcome.Paired;
        }
        catch (FormatException)
        {
            return PairingLink.TryParse(target) is { Addresses.Count: 0 }
                ? PairingOutcome.PairingLinkNoAddress
                : PairingOutcome.NotAnAddress;
        }
        catch (HandshakeException e)
        {
            return e.Status == HandshakeStatus.PairingClosed ? PairingOutcome.NotPairing : PairingOutcome.Refused;
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested) setError($"Timed out after {(int)ReachLimit.TotalSeconds}s");
            return cancellationToken.IsCancellationRequested ? PairingOutcome.Cancelled : PairingOutcome.Unreachable;
        }
        catch (Exception e) when (e is IOException or RemoteException or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException)
        {
            setError(RelayHttp.DescribeError(e));
            return PairingOutcome.Unreachable;
        }
        finally
        {
            code.TrySetCanceled();
        }
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
        var wanted = new Dictionary<string, (string Name, bool Paired, bool Online, DateTimeOffset? Seen, bool Password, string? Platform)>(StringComparer.Ordinal);
        var order = new List<string>();
        var others = new List<AccountDevice>();
        foreach (var peer in paired)
        {
            wanted[peer.Id] = (peer.Name, true, false, peer.LastSeenAt, false, null);
            order.Add(peer.Id);
        }
        // The account's other computers. Phones are never hosts, and this phone is not its own: they are only named.
        foreach (var device in devices ?? [])
        {
            if (device.Current || device.Kind != "desktop")
            {
                others.Add(new AccountDevice(device.Id, device.Name, device.Current, device.Online, device.LastSeenAt, device.Platform));
                continue;
            }
            if (wanted.TryGetValue(device.Id, out var known))
            {
                wanted[device.Id] = known with { Online = device.Online, Seen = Later(known.Seen, device.LastSeenAt), Platform = device.Platform };
            }
            else
            {
                wanted[device.Id] = (device.Name, false, device.Online, device.LastSeenAt, device.PasswordAccess, device.Platform);
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
            // This phone first, then the others as the service orders them.
            ReplaceValues(OtherDevices, [.. others.OrderByDescending(device => device.IsCurrent)]);
        }
        foreach (var id in order)
        {
            var (name, isPaired, online, seen, password, platform) = wanted[id];
            if (!_states.TryGetValue(id, out var state))
            {
                _states[id] = state = new HostState(new RemoteHost(id, name, HostLink.Offline, isPaired));
                Hosts.Add(state.Host);
            }
            state.Host.Name = name;
            state.Host.IsPaired = isPaired;
            state.Host.LastSeen = seen;
            state.Host.Platform = platform ?? state.Host.Platform;
            // What the service says, while it says anything: password access only means something before pairing.
            if (devices is not null) state.Host.AcceptsPassword = !isPaired && online && password;
            if (devices is not null) state.AccountOnline = online;
            // A computer paired a moment ago is connected already, before it had a row here.
            Sync(state);
        }
    }

    // Decided when it runs, not when it was asked for: by then the person may have signed in, or chosen to use the app
    // without an account (a computer was just added), and the list may already be the one to show.
    private void ClearUnlessShown()
    {
        if (_session.Account is not null || _settings.LocalOnly) return;
        Hosts.Clear();
        _states.Clear();
        OtherDevices.Clear();
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
        if (_states.TryGetValue(hostId, out var state)) Sync(state);
    });

    // The row in step with the connection: how the computer is reached, and its projects read from a connection that is new.
    private void Sync(HostState state)
    {
        var hostId = state.Host.Id;
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
    }

    private void OnWorkspaceChanged(string hostId)
    {
        if (_isBackgrounded)
        {
            _dirtyHostsWhileBackgrounded[hostId] = 0;
            return;
        }
        Ui(() =>
        {
            if (_states.TryGetValue(hostId, out var state) && _connections.ClientOf(hostId) is { } client) _ = LoadAsync(hostId, client, state);
        });
    }

    private void OnItemChanged(string hostId, ItemMessage item)
    {
        if (_isBackgrounded)
        {
            _dirtyHostsWhileBackgrounded[hostId] = 0;
            return;
        }
        Ui(() =>
        {
            if (!_states.TryGetValue(hostId, out var state)) return;
            var session = state.Host.Projects.FirstOrDefault(project => project.Id == item.ProjectId)?.Sessions.FirstOrDefault(known => known.Id == item.Id);
            if (session is not null)
            {
                session.Title = item.Title;
                session.Activity = ActivityOf(item.Activity);
                ApplyOrigin(session, item);
            }
            else if (_connections.ClientOf(hostId) is { } client)
            {
                // An item this phone has not heard of yet: the lists are read again.
                _ = LoadAsync(hostId, client, state);
            }
        });
    }

    private void OnAccountChanged()
    {
        if (_session.Account is not null)
        {
            _ = RefreshAsync();
            return;
        }
        // Signed out: nothing of the account stays on the screen, and no connection stays open. Who is paired is kept.
        _ = _connections.DisconnectAllAsync();
        // Used without an account before signing in: back to the computers this phone can reach by itself (reading
        // them takes the account's other computers off the list).
        if (_settings.LocalOnly) _ = RefreshAsync();
        else Ui(ClearUnlessShown);
    }

    // The person chose to use the app without an account, or went back to choosing.
    private void OnModeChanged()
    {
        if (_session.Account is not null) return;
        if (_settings.LocalOnly)
        {
            _ = RefreshAsync();
            return;
        }
        _ = _connections.DisconnectAllAsync();
        Ui(ClearUnlessShown);
    }

    private async Task LoadAsync(string hostId, RemoteHostClient client, HostState state)
    {
        // Only the latest reading is shown: an older one that arrives late is dropped.
        var load = Interlocked.Increment(ref state.Loads);
        try
        {
            using var limit = new CancellationTokenSource(CallTimeout);
            var ct = limit.Token;
            // Agents, projects (+ Desktop groups), bots, and (when supported) bot groups are independent; fetch in parallel.
            var agentsTask = client.ListAgentsAsync(ct);
            var projectListTask = client.ListProjectsWithGroupsAsync(ct);
            var botsTask = client.SupportsBots ? client.ListBotsAsync(ct) : Task.FromResult<IReadOnlyList<BotMessage>>([]);
            var botGroupsTask = client.SupportsBotGroups ? client.ListBotGroupsAsync(ct) : Task.FromResult<IReadOnlyList<BotGroupMessageDto>>([]);
            await Task.WhenAll(agentsTask, projectListTask, botsTask, botGroupsTask).ConfigureAwait(false);
            var agents = await agentsTask.ConfigureAwait(false);
            var projectList = await projectListTask.ConfigureAwait(false);
            var projects = projectList.Projects;
            var projectGroups = projectList.Groups ?? [];
            var bots = await botsTask.ConfigureAwait(false);
            var botGroups = await botGroupsTask.ConfigureAwait(false);
            var itemPairs = await Task.WhenAll(projects.Select(async project =>
                (project.Id, Items: await client.ListItemsAsync(project.Id, ct).ConfigureAwait(false)))).ConfigureAwait(false);
            var items = itemPairs.ToDictionary(p => p.Id, p => p.Items, StringComparer.Ordinal);
            Ui(() =>
            {
                if (load != Volatile.Read(ref state.Loads) || _states.GetValueOrDefault(hostId) != state) return;
                var mappedAgents = agents.Select(agent => new AgentOption(
                    agent.Id,
                    agent.Name,
                    InitialOf(agent.Name),
                    ColorOf(agent.Id),
                    [.. agent.Models.Select(model => model.Label)],
                    [.. agent.Efforts.Select(effort => effort.Label)],
                    agent.SupportsAcp,
                    agent.DefaultSurface == ItemSurfaces.Gui)).ToList();
                if (!mappedAgents.Select(a => (a.Id, a.SupportsAcp, a.DefaultGui)).SequenceEqual(state.Host.Agents.Select(a => (a.Id, a.SupportsAcp, a.DefaultGui))))
                {
                    state.Agents = agents.ToDictionary(agent => agent.Id, StringComparer.Ordinal);
                    state.Host.Agents = mappedAgents;
                }
                ApplyProjectGroups(state.Host, projectGroups);
                Apply(state, client, projects, items);
                ApplyBots(state.Host, bots);
                ApplyBotGroups(state.Host, botGroups);
            });
        }
        catch (Exception e) when (e is IOException or RemoteException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static void ApplyProjectGroups(RemoteHost host, IReadOnlyList<ProjectGroupMessage> groupMessages)
    {
        var target = new List<RemoteProjectGroup>(groupMessages.Count);
        foreach (var msg in groupMessages)
        {
            var group = host.ProjectGroups.FirstOrDefault(known => string.Equals(known.Id, msg.Id, StringComparison.Ordinal))
                ?? new RemoteProjectGroup(host, msg.Id, msg.Name);
            group.Name = msg.Name;
            group.Color = msg.Color;
            group.MergeWorkspaces = msg.MergeWorkspaces;
            target.Add(group);
        }
        Replace(host.ProjectGroups, target);
    }

    // The host's projects and items into the page's: what is still there keeps its object (a page may be showing it).
    private void Apply(HostState state, RemoteHostClient client, IReadOnlyList<ProjectMessage> projects, Dictionary<string, IReadOnlyList<ItemMessage>> items)
    {
        var host = state.Host;
        var target = new List<RemoteProject>(projects.Count);
        var activeImages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in projects)
        {
            var project = host.Projects.FirstOrDefault(known => known.Id == message.Id && known.Name == message.Name && known.Path == message.Path)
                ?? new RemoteProject(host, message.Id, message.Name, message.Path);
            project.Color = message.Color;
            project.Icon = message.Icon;
            project.GroupId = string.IsNullOrWhiteSpace(message.GroupId) ? null : message.GroupId;
            if (!string.IsNullOrWhiteSpace(message.Image)) activeImages.Add(message.Image);
            SyncProjectImage(host.Id, client, project, message.Image);
            project.IsGitRepository = message.IsGitRepository;
            project.DefaultBranch = message.DefaultBranch;
            var workspaces = (message.Workspaces ?? []).Select(MapWorkspace).ToList();
            ReplaceValues(project.Workspaces, workspaces);
            var sessions = items.GetValueOrDefault(project.Id, []).Select(item =>
            {
                // Switched between GUI and terminal on the host: a new object, since how it opens changed.
                if (project.Sessions.FirstOrDefault(known => known.Id == item.Id && known.IsGui == (item.Surface == ItemSurfaces.Gui)) is not { } session)
                    return NewSession(state, project, item);
                session.Title = item.Title;
                session.Activity = ActivityOf(item.Activity);
                UpdateSessionWorkspace(project, session, item);
                ApplyOrigin(session, item);
                return session;
            }).ToList();
            Replace(project.Sessions, sessions);
            target.Add(project);
        }
        Replace(host.Projects, target);
        _ = Task.Run(() => _iconCache.Prune(host.Id, activeImages));
    }

    /// <summary>
    /// Syncs a project's custom thumbnail image using <paramref name="imageKey"/> as an immutable cache key:
    /// unchanged images stay in memory, disk cache hits avoid any RPC, and only missing/changed keys fetch from the host.
    /// </summary>
    private void SyncProjectImage(string hostId, RemoteHostClient client, RemoteProject project, string? imageKey)
    {
        var normalized = string.IsNullOrWhiteSpace(imageKey) ? null : imageKey;
        if (normalized is null)
        {
            project.ImageKey = null;
            project.ImageBitmap = null;
            return;
        }
        if (string.Equals(project.ImageKey, normalized, StringComparison.Ordinal) && project.ImageBitmap is not null)
        {
            // Unchanged and already decoded in memory: nothing to reload or fetch.
            return;
        }
        project.ImageKey = normalized;
        if (_iconCache.TryLoad(hostId, normalized) is { } cached)
        {
            // Disk/memory cache hit: no network request needed.
            project.ImageBitmap = cached;
            return;
        }
        project.ImageBitmap = null;
        if (!client.SupportsProjectIcons) return;

        var inflightKey = $"{hostId}\0{project.Id}\0{normalized}";
        if (!_loadingIcons.Add(inflightKey)) return;
        _ = FetchProjectIconAsync(hostId, client, project, normalized, inflightKey);
    }

    private async Task FetchProjectIconAsync(string hostId, RemoteHostClient client, RemoteProject project, string expectedKey, string inflightKey)
    {
        try
        {
            using var limit = new CancellationTokenSource(CallTimeout);
            var response = await client.GetProjectIconAsync(project.Id, limit.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.Image) || string.IsNullOrWhiteSpace(response.Data)) return;
            var bytes = Convert.FromBase64String(response.Data);
            var bitmap = _iconCache.SaveAndLoad(hostId, response.Image, bytes);
            if (bitmap is null) return;
            Ui(() =>
            {
                if (_disposed) return;
                if (string.Equals(project.ImageKey, response.Image, StringComparison.Ordinal)
                    || string.Equals(project.ImageKey, expectedKey, StringComparison.Ordinal))
                {
                    project.ImageKey = response.Image;
                    project.ImageBitmap = bitmap;
                }
            });
        }
        catch (Exception e) when (e is IOException or RemoteException or FormatException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            Ui(() => _loadingIcons.Remove(inflightKey));
        }
    }

    private static void Replace<T>(ObservableCollection<T> collection, List<T> target) where T : class =>
        MobileCollectionSync.SyncByReference(collection, target);

    private static void ReplaceValues<T>(ObservableCollection<T> collection, List<T> target) =>
        MobileCollectionSync.SyncByValue(collection, target);

    private static RemoteBranchWorkspace MapWorkspace(WorkspaceMessage ws) =>
        new(ws.Id, ws.Name, ws.Branch, ws.Path, ws.TaskKind, ws.TaskKey, ws.TaskTitle, ws.TaskUrl);

    private static void UpdateSessionWorkspace(RemoteProject project, RemoteSession session, ItemMessage item)
    {
        var ws = string.IsNullOrEmpty(item.WorkspaceId) ? null : project.Workspaces.FirstOrDefault(w => w.Id == item.WorkspaceId);
        session.WorkspaceId = item.WorkspaceId;
        session.WorkspaceBranch = item.WorkspaceBranch ?? ws?.DisplayName;
        session.WorkspaceTaskKey = ws?.TaskKey;
    }

    private static RemoteSession NewSession(HostState? state, RemoteProject project, ItemMessage item)
    {
        var agent = item.AgentId is null ? null : state?.Agents.GetValueOrDefault(item.AgentId);
        var isAgent = item.Kind == ItemKinds.Agent;
        var ws = string.IsNullOrEmpty(item.WorkspaceId) ? null : project.Workspaces.FirstOrDefault(w => w.Id == item.WorkspaceId);
        var model = agent?.Models.FirstOrDefault(choice => choice.Id == item.Model)?.Label ?? item.Model;
        var effort = agent?.Efforts.FirstOrDefault(choice => choice.Id == item.Effort)?.Label ?? item.Effort;
        return new RemoteSession(project, item.Id, isAgent ? SessionKind.Agent : SessionKind.Terminal, item.Title)
        {
            AgentId = item.AgentId,
            Model = isAgent ? model : null,
            Effort = isAgent ? effort : null,
            IsGui = item.Surface == ItemSurfaces.Gui,
            WorkspaceId = item.WorkspaceId,
            WorkspaceBranch = item.WorkspaceBranch ?? ws?.DisplayName,
            WorkspaceTaskKey = ws?.TaskKey,
            // Model and effort as the host labels them; the agent's name when neither was chosen.
            Detail = !isAgent ? null : string.Join(" · ", new[] { model, effort }.Where(part => !string.IsNullOrEmpty(part))) is { Length: > 0 } detail
                ? detail
                : agent?.Name ?? item.AgentId,
            Activity = ActivityOf(item.Activity),
            SizeOwner = project.Host.Name,
            ParentItemId = item.ParentItemId,
            OriginDevice = item.OriginDevice,
            OriginSession = item.OriginSession,
        };
    }

    /// <summary>Who started the session (an agent of the host, or another computer's agent), as the host says now.</summary>
    private static void ApplyOrigin(RemoteSession session, ItemMessage item)
    {
        session.ParentItemId = item.ParentItemId;
        session.OriginDevice = item.OriginDevice;
        session.OriginSession = item.OriginSession;
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
