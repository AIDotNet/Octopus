using Avalonia.Threading;
using Octopus.App.Activity;
using Octopus.App.Services;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Core.Terminals;
using Octopus.Remote.Client;
using Octopus.Remote.Protocol;
using Octopus.Terminal;

namespace Octopus.App.Remote;

/// <summary>
/// The projects of this app that live on another computer running Octopus (design/12-远程操作.md §7.2). The project
/// itself (its place on the rail, color, layout) is kept here; its terminals and agents are that computer's, and the
/// list kept here is brought in line with it whenever the host is connected or says something changed. What the person
/// does here (a new terminal, closing a tab, a new name) is done on the host.
/// </summary>
public sealed class RemoteProjects : IDisposable
{
    private readonly IProjectService _projects;
    private readonly RemoteClientRuntime _runtime;
    private readonly IItemActivityService _activity;
    // The names the person gave, as last sent or received: only a change of one is sent to the host.
    private readonly Dictionary<string, string?> _customTitles = new(StringComparer.Ordinal);
    private bool _applying;

    public RemoteProjects(IProjectService projects, RemoteClientRuntime runtime, IItemActivityService activity)
    {
        _projects = projects;
        _runtime = runtime;
        _activity = activity;
        projects.Changed += OnProjectsChanged;
        runtime.StateChanged += OnHostStateChanged;
        runtime.WorkspaceChanged += OnHostWorkspaceChanged;
        runtime.ItemChanged += OnHostItemChanged;
    }

    /// <summary>The last sync of each project, for tests to wait on.</summary>
    internal Task LastSync { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Adds a folder of a paired host as a project: the host makes the project (and the folder when it is missing), or
    /// returns the one that folder already is; it then appears here with the terminals and agents it has there.
    /// </summary>
    public async Task<Project> AddAsync(string hostId, string path, string? name)
    {
        var client = await _runtime.ConnectAsync(hostId);
        var created = await client.CreateProjectAsync(path, string.IsNullOrWhiteSpace(name) ? null : name, createFolder: true);
        var project = _projects.AddRemoteProject(hostId, created.Id, created.Path, string.IsNullOrWhiteSpace(name) ? created.Name : name!.Trim());
        await SyncAsync(project);
        return project;
    }

    /// <summary>Starts a terminal or agent in a project on its host; it becomes the tab in front here.</summary>
    public async Task<ProjectItem?> CreateItemAsync(Project project, string? agentId = null, AgentLaunchOptions? launch = null)
    {
        if (project.RemoteHost is not { } hostId || project.RemoteId is not { } remoteId) return null;
        var client = await _runtime.ConnectAsync(hostId);
        var created = await client.CreateItemAsync(new CreateItemMessage
        {
            ProjectId = remoteId,
            Kind = agentId is null ? ItemKinds.Terminal : ItemKinds.Agent,
            AgentId = agentId,
            Model = launch?.Model,
            Effort = launch?.Effort,
            Prompt = launch?.Prompt,
        });
        await SyncAsync(project);
        if (_projects.FindItem(created.Id) is not { } item) return null;
        _projects.ActivateItem(item.Id);
        return item;
    }

    /// <summary>What a terminal tab of an item on a host starts with.</summary>
    public ITerminalConnectionFactory TerminalFactory(Project project, ProjectItem item) =>
        new HostTerminalFactory(_runtime, project.RemoteHost ?? "", item.Id);

    /// <summary>Brings every project of the host in line with it (after it connected, or said something changed).</summary>
    public Task SyncHostAsync(string hostId) =>
        LastSync = Task.WhenAll(_projects.Projects.Where(p => p.RemoteHost == hostId).ToList().Select(SyncAsync));

    public void Dispose()
    {
        _projects.Changed -= OnProjectsChanged;
        _runtime.StateChanged -= OnHostStateChanged;
        _runtime.WorkspaceChanged -= OnHostWorkspaceChanged;
        _runtime.ItemChanged -= OnHostItemChanged;
    }

    // The host's list is the truth: what it no longer has goes, what it has and is missing here comes, names follow.
    private async Task SyncAsync(Project project)
    {
        if (project.RemoteHost is not { } hostId || project.RemoteId is not { } remoteId || _runtime.ClientOf(hostId) is not { } client) return;
        IReadOnlyList<ItemMessage> items;
        try { items = await client.ListItemsAsync(remoteId); }
        catch (Exception e) when (e is IOException or RemoteException) { return; }
        await OnUi(() =>
        {
            if (!_projects.Projects.Contains(project)) return;
            _applying = true;
            try
            {
                var known = items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var gone in project.Items.Where(i => IsSession(i) && !known.Contains(i.Id)).ToList())
                {
                    _projects.RemoveItem(gone.Id);
                    _customTitles.Remove(gone.Id);
                }
                foreach (var message in items)
                {
                    if (_projects.FindItem(message.Id) is null)
                    {
                        _projects.AddItem(project, new ProjectItem
                        {
                            Id = message.Id,
                            Kind = message.Kind == ItemKinds.Agent ? ProjectItemKind.Agent : ProjectItemKind.Terminal,
                            Title = message.Title,
                            AgentId = message.AgentId,
                            Launch = message.Kind == ItemKinds.Agent ? new AgentLaunchOptions { Model = message.Model, Effort = message.Effort } : null,
                        }, activate: project.ActiveItemId is null);
                        _customTitles[message.Id] = null;
                    }
                    Apply(message);
                }
            }
            finally
            {
                _applying = false;
            }
        });
    }

    // One item as the host has it now: its name, and what it is doing.
    private void Apply(ItemMessage message)
    {
        if (_projects.FindItem(message.Id) is not { } item) return;
        if (item.Title != message.Title && string.IsNullOrEmpty(item.CustomTitle)) _projects.SetItemTitle(item.Id, message.Title);
        _activity.ReportRemote(item.Id, message.Activity switch
        {
            ItemActivities.Working => ItemActivity.Working,
            ItemActivities.Waiting => ItemActivity.Waiting,
            ItemActivities.Error => ItemActivity.Error,
            _ => ItemActivity.Idle,
        });
    }

    private void OnHostStateChanged(string hostId)
    {
        if (_runtime.StateOf(hostId) == RemoteHostState.Connected) _ = SyncHostAsync(hostId);
    }

    private void OnHostWorkspaceChanged(string hostId) => _ = SyncHostAsync(hostId);

    private void OnHostItemChanged(string hostId, ItemMessage message) => _ = OnUi(() =>
    {
        _applying = true;
        try { Apply(message); }
        finally { _applying = false; }
    });

    // What the person did here to a project on a host is done there.
    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (e.Project is not { RemoteHost: { } hostId } project) return;
        switch (e.Kind)
        {
            case ProjectChangeKind.CurrentProject:
                // Looking at it: its host is wanted from now on, and connected in the background.
                _ = ConnectQuietlyAsync(hostId);
                break;
            case ProjectChangeKind.Items when !_applying && e.Item is { } removed && IsSession(removed) && !project.Items.Contains(removed):
                _customTitles.Remove(removed.Id);
                _ = OnHostAsync(hostId, client => client.RemoveItemAsync(removed.Id));
                break;
            case ProjectChangeKind.ItemTitle when !_applying && e.Item is { } renamed && IsSession(renamed):
                var custom = string.IsNullOrWhiteSpace(renamed.CustomTitle) ? null : renamed.CustomTitle;
                if (_customTitles.TryGetValue(renamed.Id, out var sent) && sent == custom) break;
                _customTitles[renamed.Id] = custom;
                _ = OnHostAsync(hostId, client => client.RenameItemAsync(renamed.Id, custom));
                break;
        }
    }

    /// <summary>Connects a host in the background: a failure only shows in the host's state, and is retried.</summary>
    public async Task ConnectQuietlyAsync(string hostId)
    {
        try { await _runtime.ConnectAsync(hostId); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { /* shown by the host's state; retried in the background */ }
    }

    private async Task OnHostAsync(string hostId, Func<RemoteHostClient, Task> action)
    {
        try { await action(await _runtime.ConnectAsync(hostId)); }
        catch (Exception e) when (e is IOException or RemoteException or ObjectDisposedException) { }
    }

    private static bool IsSession(ProjectItem item) => item.Kind is ProjectItemKind.Terminal or ProjectItemKind.Agent;

    private static Task OnUi(Action action)
    {
        if (!Dispatcher.UIThread.CheckAccess()) return Dispatcher.UIThread.InvokeAsync(action).GetTask();
        action();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts the terminal of an item on a host. While the host is not connected the terminal says so and ends; starting
    /// it again (Enter in the tab) tries again, and the host is being reconnected in the background meanwhile.
    /// </summary>
    private sealed class HostTerminalFactory(RemoteClientRuntime runtime, string hostId, string itemId) : ITerminalConnectionFactory
    {
        private RemoteHostClient? _client;
        private ITerminalConnectionFactory? _inner;

        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows)
        {
            if (runtime.ClientOf(hostId) is not { } client)
            {
                _ = Reconnect();
                var why = runtime.ErrorOf(hostId) ?? "Not connected to the host.";
                return new Unavailable(runtime.HostName(hostId) + ": " + why);
            }
            // The client's own factory knows that a start after the program ended means "run it again": kept per connection.
            if (!ReferenceEquals(client, _client)) (_client, _inner) = (client, client.OpenTerminal(itemId));
            return _inner!.Start(spec, columns, rows);
        }

        private async Task Reconnect()
        {
            try { await runtime.ConnectAsync(hostId); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }

    /// <summary>A terminal that only says why there is nothing to show.</summary>
    private sealed class Unavailable(string message) : ITerminalConnection
    {
        private readonly TaskCompletionSource<TerminalExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProcessId => 0;
        public Task<TerminalExit> Exited => _exit.Task;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return System.Text.Encoding.UTF8.GetBytes("\u001b[31m" + message + "\u001b[0m\r\n");
            await Task.CompletedTask;
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
