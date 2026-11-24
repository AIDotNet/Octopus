using System.Collections.Concurrent;
using System.Text;
using Octopus.Remote.Host;
using Octopus.Remote.Protocol;

namespace Octopus.Mobile.Tests;

/// <summary>
/// A host's workspace for tests of the device side: projects, items and agents that are just lists, and terminals that
/// are a line of text, an echo of what is typed, and one size with one view that decides it. No program runs.
/// </summary>
internal sealed class FakeHostWorkspace : IHostWorkspace
{
    /// <summary>The size a terminal has while this computer's own window decides it.</summary>
    public const int OwnColumns = 132, OwnRows = 40;

    private readonly object _gate = new();
    private readonly List<ProjectMessage> _projects = [];
    private readonly List<ItemMessage> _items = [];
    private readonly Dictionary<string, WorkspaceOptionsMessage> _workspaceOptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakeTerminal> _terminals = new(StringComparer.Ordinal);
    private int _nextId;

    public HostInfoMessage Info { get; } = new() { Name = "Studio", Os = "test", AppVersion = "1.0", Home = "/home/ada" };
    public List<AgentMessage> Agents { get; } =
    [
        new()
        {
            Id = "claude", Name = "Claude", TakesPrompt = true, IsDefault = true,
            Models = [new() { Id = "fable-5", Label = "Fable 5.2" }, new() { Id = "opus-5", Label = "Opus 5.5" }],
            Efforts = [new() { Id = "low", Label = "低" }, new() { Id = "high", Label = "高" }],
        },
    ];
    /// <summary>What devices asked to be started, in order.</summary>
    public ConcurrentQueue<CreateItemMessage> Created { get; } = new();
    /// <summary>Branch workspaces devices asked to create, in order.</summary>
    public ConcurrentQueue<CreateWorkspaceMessage> CreatedWorkspaces { get; } = new();

    public event Action? Changed;
    public event Action<ItemMessage>? ItemChanged;

    public ProjectMessage AddProject(string name, string path, bool isGitRepository = false, string? defaultBranch = null)
    {
        var project = new ProjectMessage
        {
            Id = "p" + Interlocked.Increment(ref _nextId),
            Name = name,
            Path = path,
            IsGitRepository = isGitRepository,
            DefaultBranch = defaultBranch ?? (isGitRepository ? "main" : null),
        };
        lock (_gate) _projects.Add(project);
        Changed?.Invoke();
        return project;
    }

    public WorkspaceMessage AddWorkspace(ProjectMessage project, string branch, string? taskKind = null, string? taskKey = null, string? taskTitle = null, string? taskUrl = null)
    {
        var ws = new WorkspaceMessage
        {
            Id = "w" + Interlocked.Increment(ref _nextId),
            Name = branch,
            Branch = branch,
            Path = $"/home/ada/.octopus/workspaces/{Octopus.Core.Projects.WorkspacePaths.SanitizeSegment(project.Name)}/{Octopus.Core.Projects.WorkspacePaths.SanitizeSegment(branch)}",
            TaskKind = taskKind,
            TaskKey = taskKey,
            TaskTitle = taskTitle,
            TaskUrl = taskUrl,
        };
        lock (_gate)
        {
            project.IsGitRepository = true;
            project.DefaultBranch ??= "main";
            project.Workspaces ??= [];
            project.Workspaces.Add(ws);
        }
        Changed?.Invoke();
        return ws;
    }

    public void SetWorkspaceOptions(ProjectMessage project, WorkspaceOptionsMessage options)
    {
        lock (_gate) _workspaceOptions[project.Id] = options;
    }

    public ItemMessage AddItem(ProjectMessage project, string title, string? agentId = null, string activity = ItemActivities.Idle, string? model = null, string? effort = null, WorkspaceMessage? workspace = null, string? surface = null)
    {
        var item = new ItemMessage
        {
            Id = "i" + Interlocked.Increment(ref _nextId), ProjectId = project.Id, Title = title, AgentId = agentId, Model = model, Effort = effort,
            WorkspaceId = workspace?.Id, WorkspaceBranch = workspace?.Branch, Surface = surface,
            Kind = agentId is null ? ItemKinds.Terminal : ItemKinds.Agent, Activity = activity, IsRunning = true,
        };
        lock (_gate) _items.Add(item);
        Changed?.Invoke();
        return item;
    }

    /// <summary>The program of an item starts to work, waits for the person, fails…</summary>
    public void SetActivity(ItemMessage item, string activity, string? title = null)
    {
        item.Activity = activity;
        if (title is not null) item.Title = title;
        ItemChanged?.Invoke(item);
    }

    /// <summary>The terminal of an item, made when first asked for.</summary>
    public FakeTerminal TerminalOf(string itemId)
    {
        lock (_gate)
        {
            if (!_terminals.TryGetValue(itemId, out var terminal)) _terminals[itemId] = terminal = new FakeTerminal($"terminal of {itemId}\r\n$ ");
            return terminal;
        }
    }

    public Task<IReadOnlyList<ProjectMessage>> ListProjectsAsync()
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<ProjectMessage>>([.. _projects]);
    }

    public Task<ProjectMessage> CreateProjectAsync(CreateProjectMessage request) => Task.FromResult(AddProject(request.Name ?? Path.GetFileName(request.Path), request.Path));

    public Task<IReadOnlyList<ItemMessage>> ListItemsAsync(string projectId)
    {
        lock (_gate)
        {
            if (_projects.All(project => project.Id != projectId)) throw new RemoteException(RemoteMethods.NotFound, "No such project.");
            return Task.FromResult<IReadOnlyList<ItemMessage>>([.. _items.Where(item => item.ProjectId == projectId)]);
        }
    }

    public Task<ItemMessage> CreateItemAsync(CreateItemMessage request)
    {
        ProjectMessage project;
        WorkspaceMessage? workspace = null;
        lock (_gate)
        {
            project = _projects.FirstOrDefault(known => known.Id == request.ProjectId) ?? throw new RemoteException(RemoteMethods.NotFound, "No such project.");
            if (!string.IsNullOrWhiteSpace(request.WorkspaceId))
            {
                workspace = project.Workspaces?.FirstOrDefault(w => w.Id == request.WorkspaceId)
                    ?? throw new RemoteException(RemoteMethods.NotFound, "No such branch workspace.");
            }
        }
        if (request.Prompt == "fail") throw new RemoteException(RemoteMethods.StartFailed, "The agent is not installed on this computer.");
        Created.Enqueue(request);
        var title = request.Kind == ItemKinds.Agent ? request.Prompt ?? "Claude" : "zsh";
        return Task.FromResult(AddItem(project, title, request.Kind == ItemKinds.Agent ? request.AgentId : null,
            request.Prompt is null ? ItemActivities.Idle : ItemActivities.Working, request.Model, request.Effort, workspace));
    }

    public Task<WorkspaceOptionsMessage> GetWorkspaceOptionsAsync(string projectId)
    {
        lock (_gate)
        {
            var project = _projects.FirstOrDefault(known => known.Id == projectId) ?? throw new RemoteException(RemoteMethods.NotFound, "No such project.");
            if (_workspaceOptions.TryGetValue(projectId, out var custom)) return Task.FromResult(custom);
            if (!project.IsGitRepository) return Task.FromResult(new WorkspaceOptionsMessage());
            var def = project.DefaultBranch ?? "main";
            return Task.FromResult(new WorkspaceOptionsMessage
            {
                IsGitRepository = true,
                DefaultBranch = def,
                Branches = [new WorkspaceBranchMessage { Name = def, IsCurrent = true, OccupiedBy = project.Name }],
            });
        }
    }

    public Task<WorkspaceMessage> CreateWorkspaceAsync(CreateWorkspaceMessage request)
    {
        ProjectMessage project;
        lock (_gate)
        {
            project = _projects.FirstOrDefault(known => known.Id == request.ProjectId) ?? throw new RemoteException(RemoteMethods.NotFound, "No such project.");
        }
        if (string.IsNullOrWhiteSpace(request.BranchName)) throw new RemoteException(RemoteProtocol.InvalidParams, "Branch name is required.");
        if (request.BranchName == "fail") throw new RemoteException(RemoteMethods.StartFailed, "git worktree add failed.");
        CreatedWorkspaces.Enqueue(request);
        var branch = !request.CreateBranch && request.IsRemoteBranch && request.BranchName.IndexOf('/') is var slash and >= 0
            ? request.BranchName[(slash + 1)..]
            : request.BranchName.Trim();
        return Task.FromResult(AddWorkspace(project, branch, request.TaskKind, request.TaskKey, request.TaskTitle, request.TaskUrl));
    }

    public Task RemoveItemAsync(string itemId)
    {
        lock (_gate) _items.RemoveAll(item => item.Id == itemId);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>What the device renamed, in order: the item and the title it asked for.</summary>
    public ConcurrentQueue<(string ItemId, string? Title)> Renamed { get; } = new();
    /// <summary>The items the device asked to restart, in order.</summary>
    public ConcurrentQueue<string> Restarted { get; } = new();

    public Task RenameItemAsync(string itemId, string? title)
    {
        Renamed.Enqueue((itemId, title));
        ItemMessage? item;
        lock (_gate) item = _items.FirstOrDefault(known => known.Id == itemId);
        if (item is not null && title is not null)
        {
            item.Title = title;
            ItemChanged?.Invoke(item);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentMessage>> ListAgentsAsync() => Task.FromResult<IReadOnlyList<AgentMessage>>(Agents);

    public Task<ISessionAttachment> AttachAsync(string itemId, int columns, int rows, bool nativeSnapshot, bool claimSize, ISessionSink sink)
    {
        lock (_gate)
        {
            if (_items.All(item => item.Id != itemId)) throw new RemoteException(RemoteMethods.NotFound, "No such item.");
        }
        return Task.FromResult<ISessionAttachment>(TerminalOf(itemId).Attach(sink, columns, rows, claimSize));
    }

    public Task RestartAsync(string itemId)
    {
        lock (_gate)
        {
            if (_items.FirstOrDefault(item => item.Id == itemId) is { Surface: ItemSurfaces.Gui }) throw new RemoteException(RemoteMethods.NotAllowed, "A GUI conversation.");
        }
        Restarted.Enqueue(itemId);
        return Task.CompletedTask;
    }

    /// <summary>In-memory files keyed by full host path (with forward slashes), for <c>fs/list</c> and <c>fs/read</c> tests.</summary>
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Optional handler for <c>git/call</c> (<c>(method, argumentsJson) =&gt; replyJson</c>).</summary>
    public Func<string, string, string>? GitHandler { get; set; }

    private static RemoteException NotHere() => new(RemoteMethods.NotAllowed, "Not available in this test host.");
    public Task<string> ResolveAsync(string path) => throw NotHere();

    public Task<IReadOnlyList<FileEntryMessage>> ListAsync(string directory)
    {
        var dir = directory.TrimEnd('/');
        var prefix = dir + "/";
        var folders = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<FileEntryMessage>();
        var matchedAny = false;
        lock (_gate)
        {
            matchedAny = _projects.Any(project => project.Path.TrimEnd('/') == dir
                || (project.Workspaces?.Any(ws => ws.Path.TrimEnd('/') == dir) ?? false));
            foreach (var key in Files.Keys)
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                matchedAny = true;
                var rest = key[prefix.Length..];
                if (rest.IndexOf('/') is var slash and >= 0)
                {
                    var name = rest[..slash];
                    if (folders.Add(name))
                    {
                        entries.Add(new FileEntryMessage { Name = name, Path = prefix + name, IsDirectory = true, IsHidden = name.StartsWith('.') });
                    }
                }
                else
                {
                    entries.Add(new FileEntryMessage { Name = rest, Path = key, IsDirectory = false, IsHidden = rest.StartsWith('.') });
                }
            }
        }
        if (!matchedAny) throw new RemoteException(RemoteMethods.NotFound, $"Folder '{directory}' was not found.");
        return Task.FromResult<IReadOnlyList<FileEntryMessage>>(entries);
    }

    public Task<bool> ExistsAsync(string path) => throw NotHere();

    public Task<(byte[] Data, long Size)> ReadAsync(string path, long offset, int length)
    {
        byte[] bytes;
        lock (_gate)
        {
            if (!Files.TryGetValue(path, out bytes!)) throw new RemoteException(RemoteMethods.NotFound, $"File '{path}' was not found.");
        }
        var start = (int)Math.Clamp(offset, 0, bytes.LongLength);
        var count = (int)Math.Min(length, bytes.LongLength - start);
        var slice = new byte[count];
        Array.Copy(bytes, start, slice, 0, count);
        return Task.FromResult((slice, bytes.LongLength));
    }

    public Task WriteAsync(string path, string upload, byte[] data, bool last) => throw NotHere();
    public Task CreateFileAsync(string path) => throw NotHere();
    public Task CreateDirectoryAsync(string path) => throw NotHere();
    public Task RenameAsync(string path, string newPath) => throw NotHere();
    public Task DeleteAsync(string path, bool isDirectory) => throw NotHere();
    public Task<IDisposable> WatchAsync(string folder, Action changed) => throw NotHere();
    /// <summary>A home with two folders in it; any other folder is empty.</summary>
    public Task<BrowseFolderResultMessage> BrowseFolderAsync(BrowseFolderMessage request)
    {
        var path = string.IsNullOrEmpty(request.Path) ? Info.Home : request.Path;
        List<BrowseEntryMessage> entries = path == Info.Home
            ? [new() { Name = "code", Path = Info.Home + "/code" }, new() { Name = "notes", Path = Info.Home + "/notes" }]
            : [];
        return Task.FromResult(new BrowseFolderResultMessage
        {
            Path = path,
            Parent = path == "/" ? null : Path.GetDirectoryName(path),
            Separator = "/",
            Home = Info.Home,
            Roots = [new() { Name = "ada", Path = Info.Home, Kind = "home" }, new() { Name = "/", Path = "/", Kind = "drive" }],
            Entries = entries,
        });
    }
    public Task<string> BrowseCreateDirectoryAsync(CreateBrowseFolderMessage request) => throw NotHere();
    public Task<string> GitAsync(string method, string arguments) =>
        GitHandler is { } handler ? Task.FromResult(handler(method, arguments)) : throw NotHere();

}
