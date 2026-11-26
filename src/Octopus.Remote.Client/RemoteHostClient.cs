using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Channels;
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
    private readonly ConcurrentDictionary<uint, ClientStreamReader> _streamReaders = new();
    private readonly ConcurrentDictionary<uint, ClientStreamWindow> _streamWindows = new();
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
            client._connection.On(RemoteMethods.AcpUpdate, json.AcpUpdateMessage, update => client.AcpUpdated?.Invoke(update));
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
                if (client._terminals.TryGetValue(header.Channel, out var terminal))
                {
                    terminal.OnFrame(header.Type, payload);
                    return;
                }
                if (header.Type is FrameType.StreamData or FrameType.StreamEnd
                    && client._streamReaders.TryGetValue(header.Channel, out var reader))
                {
                    reader.OnFrame(header.Type, payload);
                    return;
                }
                if (header.Type == FrameType.StreamWindow && payload.Length >= 8
                    && client._streamWindows.TryGetValue(header.Channel, out var window))
                {
                    window.Advance(BinaryPrimitives.ReadUInt64LittleEndian(payload.Span));
                }
            };
            client._connection.Start();
            _ = client._connection.Completion.ContinueWith(_ =>
            {
                foreach (var terminal in client._terminals.Values) terminal.OnConnectionLost();
                foreach (var reader in client._streamReaders.Values) reader.OnConnectionLost();
                foreach (var window in client._streamWindows.Values) window.Abort();
            }, TaskScheduler.Default);
            client.HostHello = await RemoteHello.SendAsync(client._connection, hello, cancellationToken).ConfigureAwait(false);
            client.Info = client.HostHello.HostInfo
                ?? await client.CallAsync(RemoteMethods.HostInfo, new Nothing(), json.Nothing, json.HostInfoMessage, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The underlying protocol connection.</summary>
    public RemoteConnection Connection => _connection;
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
    /// <summary>The negotiated protocol version (1 or 2).</summary>
    public int NegotiatedVersion => _connection.NegotiatedVersion;
    /// <summary>The negotiated frame payload compression algorithm.</summary>
    public FrameCompressionKind Compression => _connection.Compression;
    /// <summary>The most recently measured round-trip time from <see cref="PingAsync"/>.</summary>
    public TimeSpan? LastRtt => _connection.LastRtt;

    public Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default) => _connection.PingAsync(cancellationToken);

    public Task MigrateTransportAsync(SecureChannel newChannel, CancellationToken cancellationToken = default) =>
        _connection.MigrateTransportAsync(newChannel, cancellationToken);

    /// <summary>
    /// Negotiates and punches a direct WebRTC P2P UDP channel over this active connection (e.g. a relay tunnel),
    /// authenticates a new <see cref="SecureChannel"/> with the host, and seamlessly migrates this client to the P2P
    /// transport without dropping attached terminals or conversations (v2).
    /// </summary>
    public async Task UpgradeToP2pAsync(DeviceIdentity identity, DeviceDescription description, System.Net.IPEndPoint? stunServer = null, bool loopbackOnly = false, CancellationToken cancellationToken = default)
    {
        using var endpoint = new IceP2pEndpoint(loopbackOnly ? System.Net.IPAddress.Loopback : System.Net.IPAddress.Any, 0);
        var ownHandshake = await endpoint.GatherAsync(stunServer, includeLoopback: true, cancellationToken).ConfigureAwait(false);
        var hostHandshake = await CallAsync(RemoteMethods.TransportP2pHandshake, ownHandshake,
            RemoteJsonContext.Default.P2pHandshakeMessage, RemoteJsonContext.Default.P2pHandshakeMessage, cancellationToken).ConfigureAwait(false);
        var p2pTransport = await endpoint.PunchAsync(hostHandshake, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        try
        {
            var newChannel = await SecureChannel.ConnectAsync(p2pTransport, identity, description, PeerExpectation.Paired(Host), cancellationToken: cancellationToken).ConfigureAwait(false);
            await MigrateTransportAsync(newChannel, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await p2pTransport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Both sides carry the same build of the terminal library: terminals are handed over as its own snapshot.</summary>
    public bool NativeSnapshots =>
        OwnHello.TerminalBuild is { Length: > 0 } own && own == HostHello.TerminalBuild
        && RemoteHello.Shared(OwnHello, HostHello).Contains(RemoteMethods.CapabilityNativeSnapshot);

    /// <summary>The host supports browsing folders for picking a new project (<see cref="RemoteMethods.FsBrowse"/>).</summary>
    public bool SupportsBrowse =>
        RemoteHello.Shared(OwnHello, HostHello).Contains(RemoteMethods.CapabilityBrowse);

    /// <summary>The host shows its GUI conversations (<see cref="RemoteMethods.CapabilityAcpGui"/>).</summary>
    public bool SupportsAcpGui =>
        RemoteHello.Shared(OwnHello, HostHello).Contains(RemoteMethods.CapabilityAcpGui);

    /// <summary>The host supports branch workspaces (<see cref="RemoteMethods.WorkspaceOptions"/> and <see cref="RemoteMethods.WorkspaceCreate"/>).</summary>
    public bool SupportsWorkspaces =>
        RemoteHello.Shared(OwnHello, HostHello).Contains(RemoteMethods.CapabilityWorkspaces);

    /// <summary>The host takes pictures for its sessions (<see cref="RemoteMethods.FsUpload"/>).</summary>
    public bool SupportsUpload =>
        RemoteHello.Shared(OwnHello, HostHello).Contains(RemoteMethods.CapabilityUpload);

    /// <summary>The host answers <see cref="RemoteMethods.SystemMetrics"/>.</summary>
    public bool SupportsSystemMetrics =>
        RemoteHello.Shared(OwnHello, HostHello).Contains(RemoteMethods.CapabilitySystemMetrics);

    /// <summary>The host answers <see cref="RemoteMethods.SystemSignalProcess"/>.</summary>
    public bool SupportsSystemSignal =>
        RemoteHello.Shared(OwnHello, HostHello).Contains(RemoteMethods.CapabilitySystemSignal);

    /// <summary>The host answers <see cref="RemoteMethods.ProcessRun"/>.</summary>
    public bool SupportsProcessRun =>
        RemoteHello.Shared(OwnHello, HostHello).Contains(RemoteMethods.CapabilityProcessRun);

    /// <summary>Both sides support raw binary file and image streaming (<see cref="RemoteMethods.CapabilityFileStream"/>).</summary>
    public bool SupportsFileStream =>
        RemoteHello.Shared(OwnHello, HostHello).Contains(RemoteMethods.CapabilityFileStream);

    /// <summary>Both sides support terminal ring-buffer delta reconnection (<see cref="RemoteMethods.CapabilitySessionResume"/>).</summary>
    public bool SupportsSessionResume =>
        RemoteHello.Shared(OwnHello, HostHello).Contains(RemoteMethods.CapabilitySessionResume);

    /// <summary>Both sides support WebRTC P2P direct transport negotiation (<see cref="RemoteMethods.CapabilityTransportP2p"/>).</summary>
    public bool SupportsTransportP2p =>
        RemoteHello.Shared(OwnHello, HostHello).Contains(RemoteMethods.CapabilityTransportP2p);

    /// <summary>Both sides support live transport migration (<see cref="RemoteMethods.CapabilityTransportMigrate"/>).</summary>
    public bool SupportsTransportMigrate =>
        RemoteHello.Shared(OwnHello, HostHello).Contains(RemoteMethods.CapabilityTransportMigrate);

    /// <summary>Both sides support syncing custom project thumbnail images (<see cref="RemoteMethods.ProjectIcon"/>).</summary>
    public bool SupportsProjectIcons =>
        RemoteHello.Shared(OwnHello, HostHello).Contains(RemoteMethods.CapabilityProjectIcons);

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

    public Task<ProjectIconMessage> GetProjectIconAsync(string projectId, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.ProjectIcon, new ProjectRefMessage { ProjectId = projectId },
            RemoteJsonContext.Default.ProjectRefMessage, RemoteJsonContext.Default.ProjectIconMessage, cancellationToken);

    public async Task<IReadOnlyList<ItemMessage>> ListItemsAsync(string projectId, CancellationToken cancellationToken = default) =>
        (await CallAsync(RemoteMethods.ItemList, new ProjectRefMessage { ProjectId = projectId }, RemoteJsonContext.Default.ProjectRefMessage, RemoteJsonContext.Default.ItemListMessage, cancellationToken).ConfigureAwait(false)).Items;

    public Task<ItemMessage> CreateItemAsync(CreateItemMessage request, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.ItemCreate, request, RemoteJsonContext.Default.CreateItemMessage, RemoteJsonContext.Default.ItemMessage, cancellationToken);

    public Task RemoveItemAsync(string itemId, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.ItemRemove, new ItemRefMessage { ItemId = itemId }, RemoteJsonContext.Default.ItemRefMessage, RemoteJsonContext.Default.Nothing, cancellationToken);

    /// <summary>Starts an item's program again on the host; a view attached to it sees its terminal cleared.</summary>
    public Task RestartItemAsync(string itemId, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.SessionRestart, new ItemRefMessage { ItemId = itemId }, RemoteJsonContext.Default.ItemRefMessage, RemoteJsonContext.Default.Nothing, cancellationToken);

    public Task RenameItemAsync(string itemId, string? title, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.ItemRename, new RenameItemMessage { ItemId = itemId, Title = title }, RemoteJsonContext.Default.RenameItemMessage, RemoteJsonContext.Default.Nothing, cancellationToken);

    public Task<WorkspaceOptionsMessage> GetWorkspaceOptionsAsync(string projectId, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.WorkspaceOptions, new ProjectRefMessage { ProjectId = projectId }, RemoteJsonContext.Default.ProjectRefMessage, RemoteJsonContext.Default.WorkspaceOptionsMessage, cancellationToken);

    public Task<WorkspaceMessage> CreateWorkspaceAsync(CreateWorkspaceMessage request, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.WorkspaceCreate, request, RemoteJsonContext.Default.CreateWorkspaceMessage, RemoteJsonContext.Default.WorkspaceMessage, cancellationToken);

    public async Task<IReadOnlyList<AgentMessage>> ListAgentsAsync(CancellationToken cancellationToken = default) =>
        (await CallAsync(RemoteMethods.AgentCatalog, new Nothing(), RemoteJsonContext.Default.Nothing, RemoteJsonContext.Default.AgentListMessage, cancellationToken).ConfigureAwait(false)).Agents;

    // ---- GUI conversations (AcpMessages.cs) ----

    /// <summary>An attached GUI conversation changed. Raised on the receiving thread.</summary>
    public event Action<AcpUpdateMessage>? AcpUpdated;

    /// <summary>Starts receiving <see cref="AcpUpdated"/> for a GUI conversation; returns how it is now.</summary>
    public Task<AcpSnapshotMessage> AttachConversationAsync(string itemId, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.AcpAttach, new AcpRefMessage { ItemId = itemId }, RemoteJsonContext.Default.AcpRefMessage, RemoteJsonContext.Default.AcpSnapshotMessage, cancellationToken);

    public Task DetachConversationAsync(string itemId, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.AcpDetach, new AcpRefMessage { ItemId = itemId }, RemoteJsonContext.Default.AcpRefMessage, RemoteJsonContext.Default.Nothing, cancellationToken);

    public Task SendPromptAsync(string itemId, string text, IReadOnlyList<string>? attachments = null, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.AcpPrompt, new AcpPromptMessage { ItemId = itemId, Text = text, Attachments = attachments is { Count: > 0 } ? [.. attachments] : null },
            RemoteJsonContext.Default.AcpPromptMessage, RemoteJsonContext.Default.Nothing, cancellationToken);

    public Task CancelTurnAsync(string itemId, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.AcpCancel, new AcpRefMessage { ItemId = itemId }, RemoteJsonContext.Default.AcpRefMessage, RemoteJsonContext.Default.Nothing, cancellationToken);

    public Task RespondAsync(AcpRespondMessage answer, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.AcpRespond, answer, RemoteJsonContext.Default.AcpRespondMessage, RemoteJsonContext.Default.Nothing, cancellationToken);

    public Task SetConversationOptionAsync(string itemId, string kind, string value, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.AcpSetOption, new AcpSetOptionMessage { ItemId = itemId, Kind = kind, Value = value },
            RemoteJsonContext.Default.AcpSetOptionMessage, RemoteJsonContext.Default.Nothing, cancellationToken);

    // ---- files of the host's projects ----

    public async Task<string> ResolveAsync(string path, CancellationToken cancellationToken = default) =>
        (await CallAsync(RemoteMethods.FsResolve, new PathMessage { Path = path }, RemoteJsonContext.Default.PathMessage, RemoteJsonContext.Default.ResolvedMessage, cancellationToken).ConfigureAwait(false)).Path;

    public async Task<IReadOnlyList<FileEntryMessage>> ListFolderAsync(string directory, CancellationToken cancellationToken = default) =>
        (await CallAsync(RemoteMethods.FsList, new PathMessage { Path = directory }, RemoteJsonContext.Default.PathMessage, RemoteJsonContext.Default.FileListMessage, cancellationToken).ConfigureAwait(false)).Entries;

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        (await CallAsync(RemoteMethods.FsExists, new PathMessage { Path = path }, RemoteJsonContext.Default.PathMessage, RemoteJsonContext.Default.ExistsMessage, cancellationToken).ConfigureAwait(false)).Exists;

    /// <summary>The whole file, read piece by piece (or as a binary stream when <see cref="SupportsFileStream"/>). Throws <see cref="InvalidDataException"/> when it has more than <paramref name="maxBytes"/> (nothing more is read then).</summary>
    public async Task<byte[]> ReadFileAsync(string path, long maxBytes, CancellationToken cancellationToken = default)
    {
        if (SupportsFileStream)
        {
            return await ReadFileStreamAsync(path, maxBytes, cancellationToken).ConfigureAwait(false);
        }
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

    private async Task<byte[]> ReadFileStreamAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        var reader = new ClientStreamReader(this);
        var channel = AllocateStreamChannel();
        _streamReaders[channel] = reader;
        try
        {
            var ready = await CallAsync(RemoteMethods.FsReadStream, new ReadStreamMessage { Path = path, Channel = channel, MaxBytes = maxBytes },
                RemoteJsonContext.Default.ReadStreamMessage, RemoteJsonContext.Default.ReadStreamReadyMessage, cancellationToken).ConfigureAwait(false);
            if (ready.Size > maxBytes)
            {
                throw new InvalidDataException($"{ready.Size} bytes is more than the {maxBytes} bytes that are copied.") { Data = { ["size"] = ready.Size } };
            }
            return await reader.ReadAllAsync(channel, (int)ready.Size, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _streamReaders.TryRemove(channel, out _);
        }
    }

    /// <summary>Replaces a file's content, piece by piece (or via a pipelined binary stream when <see cref="SupportsFileStream"/>); the host swaps the file in when the last piece arrived.</summary>
    public async Task WriteFileAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        if (SupportsFileStream)
        {
            await WriteFileStreamAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return;
        }
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

    private async Task WriteFileStreamAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var channel = AllocateStreamChannel();
        var window = new ClientStreamWindow();
        _streamWindows[channel] = window;
        try
        {
            await CallAsync(RemoteMethods.FsWriteStream, new WriteStreamMessage { Path = path, Channel = channel, Size = bytes.Length },
                RemoteJsonContext.Default.WriteStreamMessage, RemoteJsonContext.Default.Nothing, cancellationToken).ConfigureAwait(false);
            await PumpStreamDataAsync(channel, bytes, window, progress: null, cancellationToken).ConfigureAwait(false);
            await CallAsync(RemoteMethods.FsWriteFinish, new ChannelMessage { Channel = channel },
                RemoteJsonContext.Default.ChannelMessage, RemoteJsonContext.Default.Nothing, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _streamWindows.TryRemove(channel, out _);
        }
    }

    /// <summary>
    /// Hands the host a picture for a session, piece by piece, and returns where the host keeps it (its own temporary
    /// folder). <paramref name="progress"/> hears the share sent so far, from 0 to 1, after every piece.
    /// </summary>
    public async Task<string> UploadAsync(string itemId, string name, byte[] bytes, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (bytes.Length > RemoteMethods.UploadMaxBytes) throw new InvalidDataException($"{bytes.Length} bytes is more than the {RemoteMethods.UploadMaxBytes} bytes a picture may have.");
        if (SupportsFileStream)
        {
            return await UploadStreamAsync(itemId, name, bytes, progress, cancellationToken).ConfigureAwait(false);
        }
        var upload = Guid.NewGuid().ToString("N");
        var offset = 0;
        while (true)
        {
            var length = Math.Min(RemoteMethods.FilePieceBytes, bytes.Length - offset);
            var last = offset + length >= bytes.Length;
            var answer = await CallAsync(RemoteMethods.FsUpload,
                new UploadMessage { ItemId = itemId, Upload = upload, Name = name, Data = Convert.ToBase64String(bytes, offset, length), Last = last },
                RemoteJsonContext.Default.UploadMessage, RemoteJsonContext.Default.UploadedMessage, cancellationToken).ConfigureAwait(false);
            offset += length;
            progress?.Report(bytes.Length == 0 ? 1 : (double)offset / bytes.Length);
            if (last) return answer.Path is { Length: > 0 } path ? path : throw new RemoteException(RemoteProtocol.InternalError, "The host did not say where it kept the picture.");
        }
    }

    private async Task<string> UploadStreamAsync(string itemId, string name, byte[] bytes, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var channel = AllocateStreamChannel();
        var window = new ClientStreamWindow();
        _streamWindows[channel] = window;
        try
        {
            await CallAsync(RemoteMethods.FsUploadStream, new UploadStreamMessage { ItemId = itemId, Name = name, Channel = channel, Size = bytes.Length },
                RemoteJsonContext.Default.UploadStreamMessage, RemoteJsonContext.Default.Nothing, cancellationToken).ConfigureAwait(false);
            await PumpStreamDataAsync(channel, bytes, window, progress, cancellationToken).ConfigureAwait(false);
            var answer = await CallAsync(RemoteMethods.FsUploadFinish, new ChannelMessage { Channel = channel },
                RemoteJsonContext.Default.ChannelMessage, RemoteJsonContext.Default.UploadedMessage, cancellationToken).ConfigureAwait(false);
            return answer.Path is { Length: > 0 } path ? path : throw new RemoteException(RemoteProtocol.InternalError, "The host did not say where it kept the picture.");
        }
        finally
        {
            _streamWindows.TryRemove(channel, out _);
        }
    }

    private async Task PumpStreamDataAsync(uint channel, byte[] bytes, ClientStreamWindow window, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (bytes.Length == 0)
        {
            await _connection.SendDataAsync(FrameType.StreamEnd, channel, new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
            progress?.Report(1);
            return;
        }
        var chunkSize = Math.Clamp(_connection.MaxChunkBytes, 16 * 1024, RemoteMethods.FilePieceBytes);
        var offset = 0;
        var nextReportAt = Math.Min(RemoteMethods.FilePieceBytes, bytes.Length);
        while (offset < bytes.Length)
        {
            var length = Math.Min(chunkSize, bytes.Length - offset);
            await window.WaitForCreditAsync((ulong)offset, length, cancellationToken).ConfigureAwait(false);
            await _connection.SendDataAsync(FrameType.StreamData, channel, bytes.AsMemory(offset, length), cancellationToken).ConfigureAwait(false);
            offset += length;
            if (progress is not null && (offset >= nextReportAt || offset == bytes.Length))
            {
                var reported = Math.Min(nextReportAt, bytes.Length);
                progress.Report((double)reported / bytes.Length);
                nextReportAt += RemoteMethods.FilePieceBytes;
            }
        }
        await _connection.SendDataAsync(FrameType.StreamEnd, channel, new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
    }

    private uint AllocateStreamChannel()
    {
        while (true)
        {
            var channel = (uint)Interlocked.Increment(ref _nextChannel);
            if (channel != 0 && !_terminals.ContainsKey(channel) && !_streamReaders.ContainsKey(channel) && !_streamWindows.ContainsKey(channel))
            {
                return channel;
            }
        }
    }

    private sealed class ClientStreamReader(RemoteHostClient owner)
    {
        private readonly Channel<(FrameType Type, byte[] Payload)> _frames = Channel.CreateUnbounded<(FrameType, byte[])>(new UnboundedChannelOptions { SingleReader = true });

        public void OnFrame(FrameType type, ReadOnlyMemory<byte> payload) => _frames.Writer.TryWrite((type, payload.ToArray()));

        public void OnConnectionLost() => _frames.Writer.TryComplete(new IOException("The connection to the host was lost."));

        public async Task<byte[]> ReadAllAsync(uint channel, int expectedSize, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream(Math.Max(0, expectedSize));
            ulong consumed = 0;
            ulong acknowledged = 0;
            await foreach (var (type, payload) in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (type == FrameType.StreamData)
                {
                    buffer.Write(payload, 0, payload.Length);
                    consumed += (ulong)payload.Length;
                    if (consumed - acknowledged >= RemoteMethods.StreamAckEveryBytes)
                    {
                        acknowledged = consumed;
                        var win = new byte[8];
                        BinaryPrimitives.WriteUInt64LittleEndian(win, acknowledged);
                        await owner._connection.SendDataAsync(FrameType.StreamWindow, channel, (ReadOnlyMemory<byte>)win, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (type == FrameType.StreamEnd)
                {
                    if (payload.Length > 0 && payload[0] != 0) throw new IOException("The host aborted the file stream.");
                    return buffer.ToArray();
                }
            }
            throw new IOException("The connection ended before the file stream completed.");
        }
    }

    private sealed class ClientStreamWindow
    {
        private readonly object _gate = new();
        private ulong _acknowledged;
        private TaskCompletionSource? _waiter;
        private bool _aborted;

        public void Advance(ulong acknowledged)
        {
            TaskCompletionSource? wake = null;
            lock (_gate)
            {
                if (acknowledged > _acknowledged)
                {
                    _acknowledged = acknowledged;
                    wake = _waiter;
                    _waiter = null;
                }
            }
            wake?.TrySetResult();
        }

        public void Abort()
        {
            TaskCompletionSource? wake;
            lock (_gate)
            {
                _aborted = true;
                wake = _waiter;
                _waiter = null;
            }
            wake?.TrySetException(new IOException("The connection to the host was lost."));
        }

        public async ValueTask WaitForCreditAsync(ulong sentSoFar, int nextBytes, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (_aborted) throw new IOException("The connection to the host was lost.");
                    if (sentSoFar + (ulong)nextBytes - _acknowledged <= RemoteMethods.StreamWindowBytes) return;
                    _waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _waiter.Task;
                }
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
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

    /// <summary>Lists folders (directories only) anywhere on the host for choosing a project folder.</summary>
    public Task<BrowseFolderResultMessage> BrowseFolderAsync(string? path = null, bool listRoots = false, bool fallbackToParent = false, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.FsBrowse, new BrowseFolderMessage { Path = path, ListRoots = listRoots, FallbackToParent = fallbackToParent },
            RemoteJsonContext.Default.BrowseFolderMessage, RemoteJsonContext.Default.BrowseFolderResultMessage, cancellationToken);

    /// <summary>Creates a new empty folder under <paramref name="parentPath"/> on the host and returns its full path.</summary>
    public async Task<string> BrowseCreateDirectoryAsync(string parentPath, string name, CancellationToken cancellationToken = default) =>
        (await CallAsync(RemoteMethods.FsBrowseCreateDirectory, new CreateBrowseFolderMessage { ParentPath = parentPath, Name = name },
            RemoteJsonContext.Default.CreateBrowseFolderMessage, RemoteJsonContext.Default.ResolvedMessage, cancellationToken).ConfigureAwait(false)).Path;

    /// <summary>A call of the host's git service; arguments and result are JSON the two apps agree on.</summary>
    public async Task<string> GitAsync(string method, string arguments, CancellationToken cancellationToken = default) =>
        (await CallAsync(RemoteMethods.GitCall, new CallMessage { Method = method, Arguments = arguments }, RemoteJsonContext.Default.CallMessage, RemoteJsonContext.Default.CallResultMessage, cancellationToken).ConfigureAwait(false)).Result;

    /// <summary>Collects a CPU/memory/disk/network/process telemetry snapshot from this remote host.</summary>
    public Task<SystemMetricsSnapshotMessage> GetSystemMetricsAsync(SystemMetricsRequestMessage request, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.SystemMetrics, request, RemoteJsonContext.Default.SystemMetricsRequestMessage, RemoteJsonContext.Default.SystemMetricsSnapshotMessage, cancellationToken);

    /// <summary>Sends a POSIX/termination signal to a process on this remote host.</summary>
    public async Task<bool> SignalProcessAsync(int pid, string signal, CancellationToken cancellationToken = default) =>
        (await CallAsync(RemoteMethods.SystemSignalProcess, new SignalProcessMessage { Pid = pid, Signal = signal }, RemoteJsonContext.Default.SignalProcessMessage, RemoteJsonContext.Default.SignalProcessResultMessage, cancellationToken).ConfigureAwait(false)).Ok;

    /// <summary>Runs an allowlisted executable on this remote host without a shell.</summary>
    public Task<RemoteProcessRunResultMessage> RunProcessAsync(RemoteProcessRunMessage request, CancellationToken cancellationToken = default) =>
        CallAsync(RemoteMethods.ProcessRun, request, RemoteJsonContext.Default.RemoteProcessRunMessage, RemoteJsonContext.Default.RemoteProcessRunResultMessage, cancellationToken);

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
