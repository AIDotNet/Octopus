using System.Collections.Concurrent;
using Octopus.Remote.Protocol;
using Octopus.Remote.Security;
using Octopus.Remote.Transport;
using Octopus.Terminal;

namespace Octopus.Remote.Client;

/// <summary>
/// A connection to a host: its projects, items and agents as calls, its changes as events, and its terminals as
/// connections a terminal control can show. Made with <see cref="ConnectAsync"/>; when the connection ends
/// (<see cref="Completion"/>) every call throws <see cref="IOException"/> and a new client has to be made.
/// </summary>
public sealed class RemoteHostClient : IAsyncDisposable
{
    private readonly RemoteConnection _connection;
    private readonly ConcurrentDictionary<uint, RemoteTerminalConnection> _terminals = new();
    private int _nextChannel;

    private RemoteHostClient(RemoteConnection connection, HelloMessage own, bool wasPaired)
    {
        _connection = connection;
        OwnHello = own;
        WasPaired = wasPaired;
    }

    /// <summary>
    /// Connects over <paramref name="transport"/>: the secure channel's handshake, the exchange of <c>hello</c>, and the
    /// host's description of itself. Throws <see cref="HandshakeException"/> when the host is not the expected one or
    /// refuses this device, <see cref="RemoteException"/> when the two speak different protocol versions.
    /// </summary>
    public static async Task<RemoteHostClient> ConnectAsync(IMessageTransport transport, DeviceIdentity identity, DeviceDescription description,
        PeerExpectation expected, HelloMessage hello, ConnectOptions? options = null, CancellationToken cancellationToken = default)
    {
        var channel = await SecureChannel.ConnectAsync(transport, identity, description, expected, options, cancellationToken).ConfigureAwait(false);
        var client = new RemoteHostClient(new RemoteConnection(channel), hello, channel.WasPaired);
        try
        {
            var json = RemoteJsonContext.Default;
            client._connection.On(RemoteMethods.WorkspaceChanged, json.Nothing, _ => client.WorkspaceChanged?.Invoke());
            client._connection.On(RemoteMethods.ItemChanged, json.ItemMessage, item => client.ItemChanged?.Invoke(item));
            client._connection.On(RemoteMethods.FsChanged, json.PathMessage, changed => client.FolderChanged?.Invoke(changed.Path));
            client._connection.On(RemoteMethods.SessionExited, json.ExitedMessage, exited =>
            {
                if (client._terminals.TryGetValue(exited.Channel, out var terminal)) terminal.OnExited(exited.ExitCode);
            });
            client._connection.On(RemoteMethods.SessionResync, json.ChannelMessage, resync =>
            {
                if (client._terminals.TryGetValue(resync.Channel, out var terminal)) terminal.OnResync();
            });
            client._connection.DataReceived = (header, payload) =>
            {
                if (client._terminals.TryGetValue(header.Channel, out var terminal)) terminal.OnFrame(header.Type, payload);
            };
            client._connection.Start();
            _ = client._connection.Completion.ContinueWith(_ =>
            {
                foreach (var terminal in client._terminals.Values) terminal.OnConnectionLost();
            }, TaskScheduler.Default);
            client.HostHello = await RemoteHello.SendAsync(client._connection, hello, cancellationToken).ConfigureAwait(false);
            client.Info = await client.CallAsync(RemoteMethods.HostInfo, new Nothing(), json.Nothing, json.HostInfoMessage, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The host's device key, proven by the handshake.</summary>
    public DevicePublicKey Host => _connection.Peer;
    public DeviceDescription HostDescription => _connection.PeerDescription;
    public HelloMessage OwnHello { get; }
    public HelloMessage HostHello { get; private set; } = new();
    public HostInfoMessage Info { get; private set; } = new();
    /// <summary>True when this connection paired the two devices: the caller then remembers <see cref="Host"/>.</summary>
    public bool WasPaired { get; }
    /// <summary>Completes when the connection has ended.</summary>
    public Task Completion => _connection.Completion;

    /// <summary>Both sides carry the same build of the terminal library: terminals are handed over as its own snapshot.</summary>
    public bool NativeSnapshots =>
        OwnHello.TerminalBuild is { Length: > 0 } own && own == HostHello.TerminalBuild
        && RemoteHello.Shared(OwnHello, HostHello).Contains(RemoteMethods.CapabilityNativeSnapshot);

    /// <summary>Projects or their items changed on the host. Raised on the receiving thread.</summary>
    public event Action? WorkspaceChanged;
    /// <summary>One item's title or activity changed. Raised on the receiving thread.</summary>
    public event Action<ItemMessage>? ItemChanged;
    /// <summary>Something changed under a folder this device asked to watch; the argument is that folder. Raised on the receiving thread.</summary>
    public event Action<string>? FolderChanged;

    public async Task<IReadOnlyList<ProjectMessage>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        (await CallAsync(RemoteMethods.ProjectList, new Nothing(), RemoteJsonContext.Default.Nothing, RemoteJsonContext.Default.ProjectListMessage, cancellationToken).ConfigureAwait(false)).Projects;

    public Task<ProjectMessage> CreateProjectAsync(string path, string? name = null, bool createFolder = false, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.ProjectCreate, new CreateProjectMessage { Path = path, Name = name, CreateFolder = createFolder },
            RemoteJsonContext.Default.CreateProjectMessage, RemoteJsonContext.Default.ProjectMessage, cancellationToken);

    public async Task<IReadOnlyList<ItemMessage>> ListItemsAsync(string projectId, CancellationToken cancellationToken = default) =>
        (await CallAsync(RemoteMethods.ItemList, new ProjectRefMessage { ProjectId = projectId }, RemoteJsonContext.Default.ProjectRefMessage, RemoteJsonContext.Default.ItemListMessage, cancellationToken).ConfigureAwait(false)).Items;

    public Task<ItemMessage> CreateItemAsync(CreateItemMessage request, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.ItemCreate, request, RemoteJsonContext.Default.CreateItemMessage, RemoteJsonContext.Default.ItemMessage, cancellationToken);

    public Task RemoveItemAsync(string itemId, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.ItemRemove, new ItemRefMessage { ItemId = itemId }, RemoteJsonContext.Default.ItemRefMessage, RemoteJsonContext.Default.Nothing, cancellationToken);

    public Task RenameItemAsync(string itemId, string? title, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.ItemRename, new RenameItemMessage { ItemId = itemId, Title = title }, RemoteJsonContext.Default.RenameItemMessage, RemoteJsonContext.Default.Nothing, cancellationToken);

    public async Task<IReadOnlyList<AgentMessage>> ListAgentsAsync(CancellationToken cancellationToken = default) =>
        (await CallAsync(RemoteMethods.AgentCatalog, new Nothing(), RemoteJsonContext.Default.Nothing, RemoteJsonContext.Default.AgentListMessage, cancellationToken).ConfigureAwait(false)).Agents;

    // ---- files of the host's projects ----

    public async Task<string> ResolveAsync(string path, CancellationToken cancellationToken = default) =>
        (await CallAsync(RemoteMethods.FsResolve, new PathMessage { Path = path }, RemoteJsonContext.Default.PathMessage, RemoteJsonContext.Default.ResolvedMessage, cancellationToken).ConfigureAwait(false)).Path;

    public async Task<IReadOnlyList<FileEntryMessage>> ListFolderAsync(string directory, CancellationToken cancellationToken = default) =>
        (await CallAsync(RemoteMethods.FsList, new PathMessage { Path = directory }, RemoteJsonContext.Default.PathMessage, RemoteJsonContext.Default.FileListMessage, cancellationToken).ConfigureAwait(false)).Entries;

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        (await CallAsync(RemoteMethods.FsExists, new PathMessage { Path = path }, RemoteJsonContext.Default.PathMessage, RemoteJsonContext.Default.ExistsMessage, cancellationToken).ConfigureAwait(false)).Exists;

    /// <summary>The whole file, read piece by piece. Throws <see cref="InvalidDataException"/> when it has more than <paramref name="maxBytes"/> (nothing more is read then).</summary>
    public async Task<byte[]> ReadFileAsync(string path, long maxBytes, CancellationToken cancellationToken = default)
    {
        using var content = new MemoryStream();
        while (true)
        {
            var piece = await CallAsync(RemoteMethods.FsRead, new ReadFileMessage { Path = path, Offset = content.Length, Length = RemoteMethods.FilePieceBytes },
                RemoteJsonContext.Default.ReadFileMessage, RemoteJsonContext.Default.FilePieceMessage, cancellationToken).ConfigureAwait(false);
            if (piece.Size > maxBytes) throw new InvalidDataException($"{piece.Size} bytes is more than the {maxBytes} bytes that are copied.") { Data = { ["size"] = piece.Size } };
            var bytes = Convert.FromBase64String(piece.Data);
            content.Write(bytes);
            if (bytes.Length == 0 || content.Length >= piece.Size) return content.ToArray();
        }
    }

    /// <summary>Replaces a file's content, piece by piece; the host swaps the file in when the last piece arrived.</summary>
    public async Task WriteFileAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        var upload = Guid.NewGuid().ToString("N");
        var offset = 0;
        do
        {
            var length = Math.Min(RemoteMethods.FilePieceBytes, bytes.Length - offset);
            var last = offset + length >= bytes.Length;
            await CallAsync(RemoteMethods.FsWrite, new WriteFileMessage { Path = path, Upload = upload, Data = Convert.ToBase64String(bytes, offset, length), Last = last },
                RemoteJsonContext.Default.WriteFileMessage, RemoteJsonContext.Default.Nothing, cancellationToken).ConfigureAwait(false);
            offset += length;
        }
        while (offset < bytes.Length);
    }

    public Task CreateFileAsync(string path, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.FsCreateFile, new PathMessage { Path = path }, RemoteJsonContext.Default.PathMessage, RemoteJsonContext.Default.Nothing, cancellationToken);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.FsCreateDirectory, new PathMessage { Path = path }, RemoteJsonContext.Default.PathMessage, RemoteJsonContext.Default.Nothing, cancellationToken);

    public Task RenameAsync(string path, string newPath, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.FsRename, new MoveMessage { Path = path, NewPath = newPath }, RemoteJsonContext.Default.MoveMessage, RemoteJsonContext.Default.Nothing, cancellationToken);

    public Task DeleteAsync(string path, bool isDirectory, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.FsDelete, new DeleteMessage { Path = path, IsDirectory = isDirectory }, RemoteJsonContext.Default.DeleteMessage, RemoteJsonContext.Default.Nothing, cancellationToken);

    /// <summary>Asks the host to report changes under a folder (<see cref="FolderChanged"/>) from now on.</summary>
    public Task WatchFolderAsync(string folder, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.FsWatch, new PathMessage { Path = folder }, RemoteJsonContext.Default.PathMessage, RemoteJsonContext.Default.Nothing, cancellationToken);

    public Task UnwatchFolderAsync(string folder, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.FsUnwatch, new PathMessage { Path = folder }, RemoteJsonContext.Default.PathMessage, RemoteJsonContext.Default.Nothing, cancellationToken);

    /// <summary>A call of the host's git service; arguments and result are JSON the two apps agree on.</summary>
    public async Task<string> GitAsync(string method, string arguments, CancellationToken cancellationToken = default) =>
        (await CallAsync(RemoteMethods.GitCall, new CallMessage { Method = method, Arguments = arguments }, RemoteJsonContext.Default.CallMessage, RemoteJsonContext.Default.CallResultMessage, cancellationToken).ConfigureAwait(false)).Result;

    /// <summary>
    /// The terminal of an item on the host, as what a terminal control starts with. Each start attaches to the session
    /// (the launch spec is ignored: the host runs the program); a start after the program ended restarts it there.
    /// </summary>
    public ITerminalConnectionFactory OpenTerminal(string itemId) => new TerminalFactory(this, itemId, claimSize: true, null);

    /// <summary>
    /// A terminal of the host to look at without taking its size (a phone, design/12 §6.5): it is shown at the size it
    /// has, until this view claims the size, or someone types into it here.
    /// </summary>
    public RemoteTerminalView ViewTerminal(string itemId) => new(this, itemId);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    internal Task<TResult> CallAsync<TParams, TResult>(string method, TParams parameters,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> parametersType, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType,
        CancellationToken cancellationToken = default) =>
        _connection.RequestAsync(method, parameters, parametersType, resultType, cancellationToken);

    internal ValueTask SendDataAsync(FrameType type, uint channel, ReadOnlySpan<byte> payload) => _connection.SendDataAsync(type, channel, payload);

    internal uint Register(RemoteTerminalConnection terminal)
    {
        while (true)
        {
            var channel = (uint)Interlocked.Increment(ref _nextChannel);
            if (channel != 0 && _terminals.TryAdd(channel, terminal)) return channel;
        }
    }

    internal void Unregister(uint channel) => _terminals.TryRemove(channel, out _);

    internal sealed class TerminalFactory(RemoteHostClient client, string itemId, bool claimSize, Action<RemoteTerminalConnection>? started) : ITerminalConnectionFactory
    {
        private RemoteTerminalConnection? _last;

        public RemoteTerminalConnection? Last => _last;

        public ITerminalConnection Start(Core.Terminals.LaunchSpec spec, int columns, int rows)
        {
            // The previous connection saw the program end: starting again means running it again on the host.
            var restart = _last is { ProgramExited: true };
            var connection = _last = new RemoteTerminalConnection(client, itemId, columns, rows, restart, claimSize);
            started?.Invoke(connection);
            return connection;
        }
    }
}

/// <summary>
/// One device's view of a terminal on a host, which starts out only looking: the terminal keeps the size another view
/// gave it. <see cref="Factory"/> is what the terminal control is started with.
/// </summary>
public sealed class RemoteTerminalView
{
    private readonly RemoteHostClient.TerminalFactory _factory;

    internal RemoteTerminalView(RemoteHostClient client, string itemId) =>
        _factory = new RemoteHostClient.TerminalFactory(client, itemId, claimSize: false, connection => connection.SizeChanged += (columns, rows, owns) => SizeChanged?.Invoke(columns, rows, owns));

    public ITerminalConnectionFactory Factory => _factory;
    /// <summary>True while this view decides the terminal's size.</summary>
    public bool OwnsSize => _factory.Last?.OwnsSize ?? false;
    /// <summary>The terminal's size on the host, and whether this view decides it. Raised on a background thread.</summary>
    public event Action<int, int, bool>? SizeChanged;

    /// <summary>Makes this view decide the size. Nothing happens before the terminal control started.</summary>
    public Task ClaimSizeAsync(int columns, int rows) => _factory.Last?.ClaimSizeAsync(columns, rows) ?? Task.CompletedTask;

    /// <summary>Gives the size back to the view that had it before.</summary>
    public Task ReleaseSizeAsync() => _factory.Last?.ReleaseSizeAsync() ?? Task.CompletedTask;
}
