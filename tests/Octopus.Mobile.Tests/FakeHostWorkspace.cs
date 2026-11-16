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

    public event Action? Changed;
    public event Action<ItemMessage>? ItemChanged;

    public ProjectMessage AddProject(string name, string path)
    {
        var project = new ProjectMessage { Id = "p" + Interlocked.Increment(ref _nextId), Name = name, Path = path };
        lock (_gate) _projects.Add(project);
        Changed?.Invoke();
        return project;
    }

    public ItemMessage AddItem(ProjectMessage project, string title, string? agentId = null, string activity = ItemActivities.Idle, string? model = null, string? effort = null)
    {
        var item = new ItemMessage
        {
            Id = "i" + Interlocked.Increment(ref _nextId), ProjectId = project.Id, Title = title, AgentId = agentId, Model = model, Effort = effort,
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
        lock (_gate) project = _projects.FirstOrDefault(known => known.Id == request.ProjectId) ?? throw new RemoteException(RemoteMethods.NotFound, "No such project.");
        if (request.Prompt == "fail") throw new RemoteException(RemoteMethods.StartFailed, "The agent is not installed on this computer.");
        Created.Enqueue(request);
        var title = request.Kind == ItemKinds.Agent ? request.Prompt ?? "Claude" : "zsh";
        return Task.FromResult(AddItem(project, title, request.Kind == ItemKinds.Agent ? request.AgentId : null,
            request.Prompt is null ? ItemActivities.Idle : ItemActivities.Working, request.Model, request.Effort));
    }

    public Task RemoveItemAsync(string itemId)
    {
        lock (_gate) _items.RemoveAll(item => item.Id == itemId);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task RenameItemAsync(string itemId, string? title) => Task.CompletedTask;

    public Task<IReadOnlyList<AgentMessage>> ListAgentsAsync() => Task.FromResult<IReadOnlyList<AgentMessage>>(Agents);

    public Task<ISessionAttachment> AttachAsync(string itemId, int columns, int rows, bool nativeSnapshot, bool claimSize, ISessionSink sink)
    {
        lock (_gate)
        {
            if (_items.All(item => item.Id != itemId)) throw new RemoteException(RemoteMethods.NotFound, "No such item.");
        }
        return Task.FromResult<ISessionAttachment>(TerminalOf(itemId).Attach(sink, columns, rows, claimSize));
    }

    public Task RestartAsync(string itemId) => Task.CompletedTask;

    // Files and git are not what these tests are about.
    private static RemoteException NotHere() => new(RemoteMethods.NotAllowed, "Not available in this test host.");
    public Task<string> ResolveAsync(string path) => throw NotHere();
    public Task<IReadOnlyList<FileEntryMessage>> ListAsync(string directory) => throw NotHere();
    public Task<bool> ExistsAsync(string path) => throw NotHere();
    public Task<(byte[] Data, long Size)> ReadAsync(string path, long offset, int length) => throw NotHere();
    public Task WriteAsync(string path, string upload, byte[] data, bool last) => throw NotHere();
    public Task CreateFileAsync(string path) => throw NotHere();
    public Task CreateDirectoryAsync(string path) => throw NotHere();
    public Task RenameAsync(string path, string newPath) => throw NotHere();
    public Task DeleteAsync(string path, bool isDirectory) => throw NotHere();
    public Task<IDisposable> WatchAsync(string folder, Action changed) => throw NotHere();
    public Task<string> GitAsync(string method, string arguments) => throw NotHere();
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
        public ISessionSink Sink { get; } = sink;
        public ulong Position { get; private set; }
        public bool IsNative => false;
        public byte[] Snapshot { get; private set; } = [];
        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public string? Title => null;
        public bool IsRunning => true;
        public int? ExitCode => null;
        public bool OwnsSize => ReferenceEquals(terminal._owner, this);

        public void Begin(byte[] snapshot, ulong position, int columns, int rows) => (Snapshot, Position, Columns, Rows) = (snapshot, position, columns, rows);

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
