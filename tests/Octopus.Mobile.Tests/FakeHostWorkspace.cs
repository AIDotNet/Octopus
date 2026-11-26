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
    private readonly Dictionary<string, byte[]> _projectIcons = new(StringComparer.Ordinal);
    private int _nextId;
    private int _iconRequests;

    public int IconRequestCount => Volatile.Read(ref _iconRequests);

    public HostInfoMessage Info { get; } = new() { Name = "Studio", Os = "test", AppVersion = "1.0", Home = "/home/ada" };
    public List<AgentMessage> Agents { get; } =
    [
        new()
        {
            Id = "claude", Name = "Claude", TakesPrompt = true, IsDefault = true, SupportsAcp = true,
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

    public void SetProjectAppearance(ProjectMessage project, string? color = null, string? icon = null, string? image = null, byte[]? imageBytes = null)
    {
        lock (_gate)
        {
            project.Color = color;
            project.Icon = icon;
            project.Image = image;
            if (image is not null && imageBytes is not null) _projectIcons[project.Id] = imageBytes;
            else _projectIcons.Remove(project.Id);
        }
        Changed?.Invoke();
    }

    public void NotifyWorkspaceChanged() => Changed?.Invoke();

    public Task<ProjectIconMessage?> GetProjectIconAsync(string projectId)
    {
        Interlocked.Increment(ref _iconRequests);
        lock (_gate)
        {
            var project = _projects.FirstOrDefault(p => p.Id == projectId);
            if (project is null || string.IsNullOrWhiteSpace(project.Image) || !_projectIcons.TryGetValue(projectId, out var bytes))
            {
                return Task.FromResult<ProjectIconMessage?>(new ProjectIconMessage());
            }
            return Task.FromResult<ProjectIconMessage?>(new ProjectIconMessage
            {
                Image = project.Image,
                Data = Convert.ToBase64String(bytes),
            });
        }
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
            Id = "i" + Interlocked.Increment(ref _nextId),
            ProjectId = project.Id,
            Title = title,
            AgentId = agentId,
            Model = model,
            Effort = effort,
            WorkspaceId = workspace?.Id,
            WorkspaceBranch = workspace?.Branch,
            Surface = surface,
            Kind = agentId is null ? ItemKinds.Terminal : ItemKinds.Agent,
            Activity = activity,
            IsRunning = true,
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
        var surface = request.Kind == ItemKinds.Agent ? request.Surface : null;
        return Task.FromResult(AddItem(project, title, request.Kind == ItemKinds.Agent ? request.AgentId : null,
            request.Prompt is null ? ItemActivities.Idle : ItemActivities.Working, request.Model, request.Effort, workspace, surface));
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

    private readonly Dictionary<string, FakeConversation> _conversations = new(StringComparer.Ordinal);

    /// <summary>The ACP GUI conversation of an item, created on first access.</summary>
    public FakeConversation ConversationOf(string itemId)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(itemId, out var conv)) _conversations[itemId] = conv = new FakeConversation(itemId);
            return conv;
        }
    }

    public Task<IAcpAttachment> AttachConversationAsync(string itemId, Action<AcpUpdateMessage> updated)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(known => known.Id == itemId)
                ?? throw new RemoteException(RemoteMethods.NotFound, "No such item.");
            if (item.Surface != ItemSurfaces.Gui) throw new RemoteException(RemoteMethods.NotAllowed, "Not a GUI conversation.");
        }
        return Task.FromResult<IAcpAttachment>(ConversationOf(itemId).Attach(updated));
    }

    public Task PromptAsync(AcpPromptMessage prompt)
    {
        ConversationOf(prompt.ItemId).Prompt(prompt);
        return Task.CompletedTask;
    }

    public Task CancelTurnAsync(string itemId)
    {
        ConversationOf(itemId).Cancel();
        return Task.CompletedTask;
    }

    public Task RespondAsync(AcpRespondMessage answer)
    {
        ConversationOf(answer.ItemId).Respond(answer);
        return Task.CompletedTask;
    }

    public Task SetConversationOptionAsync(AcpSetOptionMessage option)
    {
        ConversationOf(option.ItemId).SetOption(option);
        return Task.CompletedTask;
    }
}

/// <summary>In-memory ACP conversation on <see cref="FakeHostWorkspace"/> for protocol-level mobile tests.</summary>
internal sealed class FakeConversation(string itemId)
{
    private readonly object _gate = new();
    private readonly List<Action<AcpUpdateMessage>> _listeners = [];
    private readonly List<AcpEntryMessage> _entries =
    [
        new() { Id = "u1", Kind = AcpEntryKinds.User, Text = "检查 API 文档" },
        new()
        {
            Id = "a1",
            Kind = AcpEntryKinds.Assistant,
            Thought = "读取现有接口定义",
            Text = "已核对 3 个接口，准备运行测试。",
        },
        new()
        {
            Id = "p1",
            Kind = AcpEntryKinds.Permission,
            Permission = new AcpPermissionMessage
            {
                Title = "运行测试",
                Detail = "dotnet test",
                Options =
                [
                    new() { Id = "allow_once", Label = "允许一次", Detail = "allow_once" },
                    new() { Id = "reject_once", Label = "拒绝", Detail = "reject_once" },
                ],
            },
        },
    ];
    private string _state = AcpStates.Waiting;
    private string? _statusDetail = "等待确认";
    private string _selectedModel = "opus-5";
    private int _nextId = 2;

    public ConcurrentQueue<AcpPromptMessage> Prompts { get; } = new();
    public ConcurrentQueue<AcpRespondMessage> Responses { get; } = new();
    public ConcurrentQueue<AcpSetOptionMessage> OptionChanges { get; } = new();
    public int CancelCount { get; private set; }
    public int Viewers { get { lock (_gate) return _listeners.Count; } }

    public IAcpAttachment Attach(Action<AcpUpdateMessage> updated)
    {
        lock (_gate)
        {
            _listeners.Add(updated);
            var snapshot = new AcpSnapshotMessage
            {
                ItemId = itemId,
                AgentName = "Claude",
                State = _state,
                StatusDetail = _statusDetail,
                Entries = [.. _entries],
                Composer = BuildComposer(),
            };
            return new Attachment(this, updated, snapshot);
        }
    }

    public void Prompt(AcpPromptMessage prompt)
    {
        Prompts.Enqueue(prompt);
        AcpEntryMessage userEntry, assistantEntry;
        Action<AcpUpdateMessage>[] listeners;
        lock (_gate)
        {
            var id = Interlocked.Increment(ref _nextId);
            userEntry = new AcpEntryMessage { Id = "u" + id, Kind = AcpEntryKinds.User, Text = prompt.Text };
            assistantEntry = new AcpEntryMessage { Id = "a" + id, Kind = AcpEntryKinds.Assistant, Text = "收到：" + prompt.Text, IsStreaming = true };
            _entries.Add(userEntry);
            _entries.Add(assistantEntry);
            _state = AcpStates.Working;
            _statusDetail = "回复中";
            listeners = [.. _listeners];
        }
        var update = new AcpUpdateMessage
        {
            ItemId = itemId,
            State = AcpStates.Working,
            StatusDetail = "回复中",
            Upserts = [userEntry, assistantEntry],
        };
        foreach (var l in listeners) l(update);
    }

    public void Cancel()
    {
        Action<AcpUpdateMessage>[] listeners;
        lock (_gate)
        {
            CancelCount++;
            _state = AcpStates.Ready;
            _statusDetail = "";
            listeners = [.. _listeners];
        }
        var update = new AcpUpdateMessage { ItemId = itemId, State = AcpStates.Ready, StatusDetail = "" };
        foreach (var l in listeners) l(update);
    }

    public void Respond(AcpRespondMessage answer)
    {
        Responses.Enqueue(answer);
        AcpEntryMessage? updatedCard = null;
        Action<AcpUpdateMessage>[] listeners;
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Id == answer.EntryId);
            if (index >= 0 && _entries[index].Permission is { } perm)
            {
                var current = _entries[index];
                var label = perm.Options.FirstOrDefault(o => o.Id == answer.OptionId)?.Label ?? answer.OptionId;
                updatedCard = new AcpEntryMessage
                {
                    Id = current.Id,
                    Kind = current.Kind,
                    Permission = new AcpPermissionMessage
                    {
                        Title = perm.Title,
                        Detail = perm.Detail,
                        Options = perm.Options,
                        IsResolved = true,
                        ResolvedSummary = label,
                    },
                };
                _entries[index] = updatedCard;
            }
            _state = AcpStates.Ready;
            _statusDetail = "";
            listeners = [.. _listeners];
        }
        var update = new AcpUpdateMessage
        {
            ItemId = itemId,
            State = AcpStates.Ready,
            StatusDetail = "",
            Upserts = updatedCard is null ? [] : [updatedCard],
        };
        foreach (var l in listeners) l(update);
    }

    public void SetOption(AcpSetOptionMessage option)
    {
        OptionChanges.Enqueue(option);
        AcpComposerMessage composer;
        Action<AcpUpdateMessage>[] listeners;
        lock (_gate)
        {
            if (option.Kind == AcpOptionKinds.Model) _selectedModel = option.Value;
            composer = BuildComposer();
            listeners = [.. _listeners];
        }
        var update = new AcpUpdateMessage { ItemId = itemId, Composer = composer };
        foreach (var l in listeners) l(update);
    }

    private AcpComposerMessage BuildComposer() => new()
    {
        Models = [new() { Id = "opus-5", Label = "Opus 5.5" }, new() { Id = "sonnet-5", Label = "Sonnet 5.5" }],
        Model = _selectedModel,
    };

    private sealed class Attachment(FakeConversation owner, Action<AcpUpdateMessage> listener, AcpSnapshotMessage snapshot) : IAcpAttachment
    {
        public AcpSnapshotMessage Snapshot { get; } = snapshot;
        public void Dispose()
        {
            lock (owner._gate) owner._listeners.Remove(listener);
        }
    }
}

/// <summary>A terminal of the test host: what it shows, what was typed into it, its size and which view decides that.</summary>
internal sealed class FakeTerminal(string screen)
{
    private readonly object _gate = new();
    private readonly List<View> _views = [];
    private readonly StringBuilder _screen = new(screen);
    private readonly StringBuilder _typed = new();
    private ulong _position;
    private View? _owner;

    public int Columns { get; private set; } = FakeHostWorkspace.OwnColumns;
    public int Rows { get; private set; } = FakeHostWorkspace.OwnRows;

    /// <summary>Everything devices typed, in order.</summary>
    public string Typed
    {
        get { lock (_gate) return _typed.ToString(); }
    }

    /// <summary>True while a device's view decides the size; false while it is this computer's own window.</summary>
    public bool IsOwnedByDevice
    {
        get { lock (_gate) return _owner is not null; }
    }

    public int Viewers
    {
        get { lock (_gate) return _views.Count; }
    }

    /// <summary>The program prints something.</summary>
    public void Print(string text)
    {
        lock (_gate) Output(Encoding.UTF8.GetBytes(text));
    }

    public ISessionAttachment Attach(ISessionSink sink, int columns, int rows, bool claim)
    {
        lock (_gate)
        {
            var view = new View(this, sink, (columns, rows));
            if (claim) Claim(view, columns, rows);
            view.Begin(Encoding.UTF8.GetBytes(_screen.ToString()), _position, Columns, Rows);
            _views.Add(view);
            return view;
        }
    }

    // Under the gate.
    private void Output(byte[] bytes)
    {
        _screen.Append(Encoding.UTF8.GetString(bytes));
        foreach (var view in _views) view.Sink.Output(_position, bytes);
        _position += (ulong)bytes.Length;
    }

    private void Claim(View view, int columns, int rows)
    {
        _owner = view;
        SetSize(columns, rows);
    }

    private void SetSize(int columns, int rows)
    {
        if ((columns, rows) == (Columns, Rows)) return;
        (Columns, Rows) = (columns, rows);
        foreach (var view in _views) view.Sink.Resized(_position, columns, rows);
        _position++;
    }

    private sealed class View(FakeTerminal terminal, ISessionSink sink, (int Columns, int Rows) wanted) : ISessionAttachment
    {
        private byte[]? _snapshot = [];
        public ISessionSink Sink { get; } = sink;
        public ulong Position { get; private set; }
        public bool IsNative => false;
        public int SnapshotLength { get; private set; }
        public byte[] TakeSnapshot() => Interlocked.Exchange(ref _snapshot, null) ?? [];
        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public string? Title => null;
        public bool IsRunning => true;
        public int? ExitCode => null;
        public bool OwnsSize => ReferenceEquals(terminal._owner, this);

        public void Begin(byte[] snapshot, ulong position, int columns, int rows) => (_snapshot, SnapshotLength, Position, Columns, Rows) = (snapshot, snapshot.Length, position, columns, rows);

        public void Input(ReadOnlySpan<byte> bytes)
        {
            var copy = bytes.ToArray();
            lock (terminal._gate)
            {
                terminal._typed.Append(Encoding.UTF8.GetString(copy));
                // Echoed, as a line editor would.
                terminal.Output(copy);
            }
        }

        public (bool Applied, int Columns, int Rows) Resize(int columns, int rows, bool claim)
        {
            lock (terminal._gate)
            {
                wanted = (columns, rows);
                if (claim) terminal.Claim(this, columns, rows);
                else if (OwnsSize) terminal.SetSize(columns, rows);
                return (OwnsSize, terminal.Columns, terminal.Rows);
            }
        }

        public (int Columns, int Rows) ReleaseSize()
        {
            lock (terminal._gate)
            {
                if (OwnsSize)
                {
                    // Back to this computer's own window and its size.
                    terminal._owner = null;
                    terminal.SetSize(FakeHostWorkspace.OwnColumns, FakeHostWorkspace.OwnRows);
                }
                return (terminal.Columns, terminal.Rows);
            }
        }

        public void Dispose()
        {
            lock (terminal._gate)
            {
                terminal._views.Remove(this);
                if (!OwnsSize) return;
                terminal._owner = null;
                terminal.SetSize(FakeHostWorkspace.OwnColumns, FakeHostWorkspace.OwnRows);
            }
        }
    }
}
